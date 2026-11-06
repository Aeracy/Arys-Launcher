using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace ArysLauncher;

internal sealed class LauncherUpdates
{
    private readonly LauncherStorage storage;
    private readonly string applicationRoot;

    public UpdateChannel Channel { get; }

    public LauncherUpdates(LauncherStorage storage)
    {
        this.storage = storage;
        applicationRoot = AppContext.BaseDirectory;
        var channelPath = Path.Combine(applicationRoot, "release-channel.json");
        Channel = File.Exists(channelPath) ?
            JsonSerializer.Deserialize<UpdateChannel>(File.ReadAllText(channelPath), LauncherStorage.JsonOptions) ??
                throw new InvalidDataException("更新来源配置为空。") : new UpdateChannel();
    }

    public async Task<UpdateManifest?> CheckAsync(UpdateKind kind, CancellationToken cancellationToken)
    {
        var address = Channel.ManifestAddress(kind);
        if (address == null)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(Channel.PublicKey))
        {
            throw new InvalidDataException("更新来源尚未配置发行公钥。");
        }
        using var client = NetworkClients.Create(storage.Settings);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var bytes = await DownloadMetadataAsync(client, address, 4 * 1024 * 1024, timeout.Token);
        var signatureAddress = UpdatePaths.HttpsAddress(address.AbsoluteUri + ".sig");
        var signature = await DownloadMetadataAsync(client, signatureAddress, 1024, timeout.Token);
        using var signer = ECDsa.Create();
        try
        {
            signer.ImportSubjectPublicKeyInfo(Convert.FromBase64String(Channel.PublicKey), out _);
            if (!signer.VerifyData(bytes, Convert.FromBase64String(System.Text.Encoding.UTF8.GetString(signature).Trim()),
                    HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                throw new CryptographicException();
            }
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            throw new InvalidDataException("更新清单签名无效，请稍后重新检查。", exception);
        }
        var release = JsonSerializer.Deserialize<UpdateManifest>(bytes, LauncherStorage.JsonOptions)
            ?? throw new InvalidDataException("更新清单为空。");
        release.Validate(kind);
        var currentVersion = kind == UpdateKind.Launcher ? App.LauncherVersion : storage.Installation?.PackVersion;
        if (kind == UpdateKind.Pack && storage.Installation == null)
        {
            return null;
        }
        if (Version.TryParse(currentVersion, out var installed) && Version.Parse(release.Version) <= installed)
        {
            return null;
        }
        return release;
    }

    public async Task<FileUpdateTransaction> PrepareAsync(UpdateManifest release, IProgress<WorkSnapshot> progress,
        CancellationToken cancellationToken)
    {
        release.Validate(release.Kind);
        var targetRoot = release.Kind == UpdateKind.Launcher ? applicationRoot : storage.GameDirectory;
        var installed = storage.ReadRecord(FileUpdateTransaction.StateFile(release.Kind), new InstalledFiles());
        var productId = release.Kind == UpdateKind.Launcher ? "ArysLauncher" : release.Pack!.Id;
        if (installed.ProductId.Length > 0 && installed.ProductId != productId)
        {
            throw new InvalidDataException("更新来源与已安装的整合包不同。");
        }
        progress.Report(new WorkSnapshot("正在检查更新文件", "比较本地文件和发行清单。", 0, 0, 0, 0, 0, 0, true));
        var knownFiles = installed.Files.Concat(release.RetiredFiles).ToArray();
        foreach (var file in knownFiles)
        {
            UpdatePaths.ValidateFile(file, release.Kind);
        }
        var knownByPath = knownFiles.ToLookup(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var journal = new UpdateJournal
        {
            Kind = release.Kind,
            TargetRoot = targetRoot,
            Release = release,
            Installed = new InstalledFiles { ProductId = productId, Version = release.Version }
        };
        foreach (var file in release.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = UpdatePaths.Target(targetRoot, file.Path);
            if (await UpdatePaths.MatchesAsync(target, file, cancellationToken))
            {
                journal.Installed.Files.Add(file);
                continue;
            }
            ManagedFile? original = null;
            if (File.Exists(target))
            {
                original = await FindMatchingAsync(target, knownByPath[file.Path], cancellationToken);
                if (file.PreserveExisting)
                {
                    if (original != null)
                    {
                        journal.Installed.Files.Add(original);
                    }
                    continue;
                }
                if (original == null)
                {
                    throw new IOException($"文件与已有发行版本不同，请先备份并处理该文件。{file.Path}");
                }
            }
            journal.Changes.Add(new PendingFileChange { Path = file.Path, Original = original, Replacement = file });
            journal.Installed.Files.Add(file);
        }
        var targetPaths = release.Files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in knownByPath.Where(group => !targetPaths.Contains(group.Key)))
        {
            var target = UpdatePaths.Target(targetRoot, group.Key);
            if (!File.Exists(target))
            {
                continue;
            }
            var original = await FindMatchingAsync(target, group, cancellationToken) ??
                throw new IOException($"待移除的发行文件已被修改，请先处理该文件。{group.Key}");
            journal.Changes.Add(new PendingFileChange { Path = group.Key, Original = original });
        }
        var bytesNeeded = journal.Changes.Sum(change => change.Replacement?.Size ?? 0);
        var drive = new DriveInfo(Path.GetPathRoot(targetRoot)!);
        if (drive.IsReady && drive.AvailableFreeSpace < bytesNeeded * 2 + 64L * 1024 * 1024)
        {
            throw new IOException("安装位置的可用空间不足以完成更新。");
        }
        var transaction = FileUpdateTransaction.Create(storage, journal);
        try
        {
            using var client = NetworkClients.Create(storage.Settings, download: true);
            using var downloader = new ContentDownloader(storage, client);
            var downloads = journal.Changes.Where(change => change.Replacement != null).ToArray();
            var reporter = new WorkProgress(progress, downloads.Length, bytesNeeded);
            reporter.Stage("正在下载更新", $"需要获取 {downloads.Length} 个文件。");
            await Parallel.ForEachAsync(downloads.Select((change, index) => (change, index)), new ParallelOptions
            {
                MaxDegreeOfParallelism = DownloadPolicy.FileConcurrency(storage.Settings),
                CancellationToken = cancellationToken
            }, async (item, token) =>
            {
                var file = (ReleaseFile)item.change.Replacement!;
                var cached = await downloader.FetchAsync(new ContentRequest(file.Size, file.Sha256,
                    HashAlgorithmName.SHA256, file.Downloads.Select(UpdatePaths.HttpsAddress).ToArray()), item.index, reporter, token);
                var staged = transaction.StagedPath(file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                File.Copy(cached, staged);
                reporter.FileDone(item.index);
            });
            reporter.Report(true);
            return transaction;
        }
        catch
        {
            transaction.Cleanup();
            throw;
        }
    }

    public void StartLauncherReplacement(FileUpdateTransaction transaction)
    {
        var executable = Environment.ProcessPath ?? throw new IOException("无法读取启动器程序位置。");
        if (File.Exists(Path.Combine(applicationRoot, "ArysLauncher.dll")))
        {
            throw new IOException("请使用打包后的 ArysLauncher.exe 更新启动器。");
        }
        var helper = UpdatePaths.Target(transaction.DirectoryPath, "helper/ArysLauncher.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(helper)!);
        File.Copy(executable, helper);
        using var parent = Process.GetCurrentProcess();
        var info = new ProcessStartInfo(helper) { UseShellExecute = false, WorkingDirectory = applicationRoot };
        foreach (var argument in new[]
        {
            "--apply-update", transaction.DirectoryPath, parent.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            parent.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), applicationRoot
        })
        {
            info.ArgumentList.Add(argument);
        }
        using var process = Process.Start(info) ?? throw new IOException("无法启动更新程序。");
    }

    public static async Task RecoverAsync(LauncherStorage storage, string applicationRoot)
    {
        if (!Directory.Exists(storage.Updates))
        {
            return;
        }
        foreach (var directory in Directory.EnumerateDirectories(storage.Updates))
        {
            if (File.Exists(Path.Combine(directory, "journal.json")))
            {
                await FileUpdateTransaction.Load(storage, directory, applicationRoot).RecoverAsync();
            }
        }
    }

    public static async Task RecordLauncherFilesAsync(LauncherStorage storage)
    {
        var current = storage.ReadRecord(FileUpdateTransaction.StateFile(UpdateKind.Launcher), new InstalledFiles());
        if (current.Version == App.LauncherVersion ||
            File.Exists(Path.Combine(AppContext.BaseDirectory, "ArysLauncher.dll")))
        {
            return;
        }
        var installed = new InstalledFiles { ProductId = "ArysLauncher", Version = App.LauncherVersion };
        var paths = new List<string> { "ArysLauncher.exe", "release-channel.json", "README.md" };
        var licenses = Path.Combine(AppContext.BaseDirectory, "Licenses");
        if (Directory.Exists(licenses))
        {
            paths.AddRange(Directory.EnumerateFiles(licenses).Select(path => "Licenses/" + Path.GetFileName(path)));
        }
        foreach (var path in paths)
        {
            var target = UpdatePaths.Target(AppContext.BaseDirectory, path);
            if (!File.Exists(target))
            {
                continue;
            }
            await using var input = File.OpenRead(target);
            installed.Files.Add(new ManagedFile
            {
                Path = path,
                Size = input.Length,
                Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(input)).ToLowerInvariant()
            });
        }
        storage.Write(FileUpdateTransaction.StateFile(UpdateKind.Launcher), installed);
    }

    private static async Task<ManagedFile?> FindMatchingAsync(string path, IEnumerable<ManagedFile> candidates, CancellationToken token)
    {
        foreach (var candidate in candidates)
        {
            if (await UpdatePaths.MatchesAsync(path, candidate, token))
            {
                return candidate;
            }
        }
        return null;
    }

    private static async Task<byte[]> DownloadMetadataAsync(HttpClient client, Uri address, int maximumBytes, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps || response.Content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidDataException("更新服务响应超出允许范围。");
        }
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > maximumBytes)
            {
                throw new InvalidDataException("更新服务响应过大。");
            }
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
}
