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
    private readonly Dictionary<UpdateKind, TaggedRelease> taggedReleases = new();
    private bool releaseCatalogLoaded;

    public UpdateChannel Channel { get; }

    public LauncherUpdates(LauncherStorage storage)
    {
        this.storage = storage;
        applicationRoot = AppContext.BaseDirectory;
        var channelPath = Path.Combine(applicationRoot, "release-channel.json");
        Channel = File.Exists(channelPath) ?
            JsonSerializer.Deserialize<UpdateChannel>(File.ReadAllText(channelPath), LauncherStorage.JsonOptions) ??
                throw new InvalidDataException(LauncherText.Get("UpdateChannelEmpty")) : new UpdateChannel();
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
            throw new InvalidDataException(LauncherText.Get("UpdatePublicKeyMissing"));
        }
        using var client = NetworkClients.Create(storage.Settings);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        if (address.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            address = await ResolveTaggedReleaseAsync(client, kind, timeout.Token);
        }
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
            throw new InvalidDataException(LauncherText.Get("UpdateSignatureInvalid"), exception);
        }
        var release = JsonSerializer.Deserialize<UpdateManifest>(bytes, LauncherStorage.JsonOptions)
            ?? throw new InvalidDataException(LauncherText.Get("UpdateManifestEmpty"));
        release.Validate(kind);
        if (taggedReleases.TryGetValue(kind, out var taggedRelease) && Version.Parse(release.Version) != taggedRelease.Version)
        {
            throw new InvalidDataException(LauncherText.Get("UpdateTagVersionMismatch"));
        }
        var currentVersion = kind == UpdateKind.Launcher ? App.LauncherVersion : storage.Installation?.PackVersion;
        if (kind == UpdateKind.Pack && storage.Installation == null)
        {
            return null;
        }
        var releaseVersion = Version.Parse(release.Version);
        if (Version.TryParse(currentVersion, out var installed) &&
            (releaseVersion < installed || kind == UpdateKind.Launcher && releaseVersion == installed))
        {
            return null;
        }
        if (kind == UpdateKind.Pack)
        {
            var journal = await BuildJournalAsync(release, cancellationToken);
            if (journal.Changes.Count == 0)
            {
                await AddModReplacementsAsync(journal, null, cancellationToken);
            }
            var instance = storage.Installation!;
            if (journal.Changes.Count == 0 && releaseVersion == installed &&
                instance.MinecraftVersion == release.Pack!.MinecraftVersion && instance.ForgeVersion == release.Pack.ForgeVersion)
            {
                return null;
            }
        }
        return release;
    }

    private async Task<Uri> ResolveTaggedReleaseAsync(HttpClient client, UpdateKind kind, CancellationToken cancellationToken)
    {
        if (!releaseCatalogLoaded)
        {
            for (var page = 1; page <= 10; page++)
            {
                var url = new Uri($"https://api.github.com/repos/{Channel.LauncherRepository}/releases?per_page=100&page={page}");
                var bytes = await DownloadMetadataAsync(client, url, 4 * 1024 * 1024, cancellationToken);
                using var document = JsonDocument.Parse(bytes);
                var releases = document.RootElement;
                if (releases.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException(LauncherText.Get("UpdateReleaseCatalogInvalid"));
                }
                foreach (var release in releases.EnumerateArray())
                {
                    if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
                    {
                        continue;
                    }
                    var tag = release.GetProperty("tag_name").GetString() ?? "";
                    foreach (var candidateKind in Enum.GetValues<UpdateKind>())
                    {
                        var prefix = candidateKind == UpdateKind.Launcher ? Channel.LauncherTagPrefix : Channel.PackTagPrefix;
                        if (string.IsNullOrWhiteSpace(prefix) || !tag.StartsWith(prefix, StringComparison.Ordinal) ||
                            !Version.TryParse(tag[prefix.Length..], out var version) ||
                            taggedReleases.TryGetValue(candidateKind, out var current) && current.Version >= version)
                        {
                            continue;
                        }
                        var manifestName = candidateKind == UpdateKind.Launcher ? "launcher-update.json" : "pack-update.json";
                        var assets = release.GetProperty("assets").EnumerateArray().ToArray();
                        var manifest = assets.FirstOrDefault(asset => asset.GetProperty("name").GetString() == manifestName);
                        if (manifest.ValueKind == JsonValueKind.Undefined ||
                            !assets.Any(asset => asset.GetProperty("name").GetString() == manifestName + ".sig"))
                        {
                            continue;
                        }
                        var address = manifest.GetProperty("browser_download_url").GetString() ?? "";
                        var expected = $"https://github.com/{Channel.LauncherRepository}/releases/download/{tag}/{manifestName}";
                        if (address != expected)
                        {
                            throw new InvalidDataException(LauncherText.Get("UpdateAddressRequiresHttps"));
                        }
                        taggedReleases[candidateKind] = new TaggedRelease(version, UpdatePaths.HttpsAddress(address));
                    }
                }
                if (releases.GetArrayLength() < 100)
                {
                    break;
                }
            }
            releaseCatalogLoaded = true;
        }
        if (!taggedReleases.TryGetValue(kind, out var selected))
        {
            throw new InvalidDataException(LauncherText.Get("UpdateChannelReleaseMissing"));
        }
        return selected.Address;
    }

    private sealed record TaggedRelease(Version Version, Uri Address);

    private async Task<UpdateJournal> BuildJournalAsync(UpdateManifest release, CancellationToken cancellationToken)
    {
        release.Validate(release.Kind);
        var targetRoot = release.Kind == UpdateKind.Launcher ? applicationRoot : storage.GameDirectory;
        var installed = storage.ReadRecord(FileUpdateTransaction.StateFile(release.Kind), new InstalledFiles());
        var productId = release.Kind == UpdateKind.Launcher ? "ArysLauncher" : release.Pack!.Id;
        if (installed.ProductId.Length > 0 && installed.ProductId != productId)
        {
            throw new InvalidDataException(LauncherText.Get("UpdateProductMismatch"));
        }
        if (release.Kind == UpdateKind.Pack && installed.ProductId.Length == 0 &&
            (!Directory.Exists(Path.Combine(targetRoot, "mods")) ||
                !Directory.EnumerateFiles(Path.Combine(targetRoot, "mods"), "*.jar")
                    .Any(path => PackContentPolicy.IsArysMod("mods/" + Path.GetFileName(path)))))
        {
            throw new InvalidDataException(LauncherText.Get("PackUpdateSourceUnlinked"));
        }
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
            if (release.Kind == UpdateKind.Pack && PackContentPolicy.IsInitialSettingsPath(file.Path))
            {
                continue;
            }
            var target = UpdatePaths.Target(targetRoot, file.Path);
            if (await UpdatePaths.MatchesAsync(target, file, cancellationToken))
            {
                journal.Installed.Files.Add(file);
                continue;
            }
            ManagedFile? original = null;
            var checkModIdentity = false;
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
                if (original == null && release.Kind == UpdateKind.Pack && ModIdentity.IsModPath(file.Path))
                {
                    original = await ReadLocalFileAsync(targetRoot, file.Path, cancellationToken);
                    checkModIdentity = true;
                }
                if (original == null)
                {
                    throw new IOException(LauncherText.Get("UpdateUnrecognizedLocalFile", file.Path));
                }
            }
            journal.Changes.Add(new PendingFileChange
            {
                Path = file.Path,
                Original = original,
                Replacement = file,
                CheckModIdentity = checkModIdentity
            });
            journal.Installed.Files.Add(file);
        }
        var targetPaths = release.Files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in knownByPath.Where(group => !targetPaths.Contains(group.Key)))
        {
            if (release.Kind == UpdateKind.Pack && PackContentPolicy.IsInitialSettingsPath(group.Key))
            {
                continue;
            }
            var target = UpdatePaths.Target(targetRoot, group.Key);
            if (!File.Exists(target))
            {
                continue;
            }
            var original = await FindMatchingAsync(target, group, cancellationToken);
            if (original == null)
            {
                throw new IOException(LauncherText.Get("UpdateRetiredFileModified", group.Key));
            }
            journal.Changes.Add(new PendingFileChange { Path = group.Key, Original = original });
        }
        return journal;
    }

    public async Task<FileUpdateTransaction> PrepareAsync(UpdateManifest release, IProgress<WorkSnapshot> progress,
        CancellationToken cancellationToken)
    {
        progress.Report(new WorkSnapshot(LauncherText.Get("CheckingUpdateFiles"), LauncherText.Get("ComparingUpdateFiles"), 0, 0, 0, 0, 0, 0, true));
        var journal = await BuildJournalAsync(release, cancellationToken);
        var targetRoot = journal.TargetRoot;
        var bytesNeeded = journal.Changes.Sum(change => change.Replacement?.Size ?? 0);
        var drive = new DriveInfo(Path.GetPathRoot(targetRoot)!);
        if (drive.IsReady && drive.AvailableFreeSpace < bytesNeeded * 2 + 64L * 1024 * 1024)
        {
            throw new IOException(LauncherText.Get("UpdateDiskSpaceLow"));
        }
        var transaction = FileUpdateTransaction.Create(storage, journal);
        try
        {
            using var client = NetworkClients.Create(storage.Settings, download: true);
            using var downloader = new ContentDownloader(storage, client);
            var downloads = journal.Changes.Where(change => change.Replacement != null).ToArray();
            var reporter = new WorkProgress(progress, downloads.Length, bytesNeeded);
            reporter.Stage(LauncherText.Get("DownloadingUpdate"), LauncherText.Get("UpdateFilesNeeded", downloads.Length));
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
            if (release.Kind == UpdateKind.Pack)
            {
                await AddModReplacementsAsync(journal, transaction, cancellationToken);
                transaction.Save();
            }
            reporter.Report(true);
            return transaction;
        }
        catch
        {
            transaction.Cleanup();
            throw;
        }
    }

    private static async Task AddModReplacementsAsync(UpdateJournal journal, FileUpdateTransaction? transaction, CancellationToken token)
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in journal.Release.Files.Where(file => ModIdentity.IsModPath(file.Path)))
        {
            token.ThrowIfCancellationRequested();
            var change = journal.Changes.FirstOrDefault(change => change.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase));
            var path = change?.Replacement != null && transaction != null ? transaction.StagedPath(file.Path) :
                UpdatePaths.Target(journal.TargetRoot, file.Path);
            var identities = ModIdentity.Read(path);
            foreach (var identity in identities)
            {
                if (!owners.TryAdd(identity, file.Path))
                {
                    throw new InvalidDataException(LauncherText.Get("UpdateDuplicateMod", owners[identity], file.Path));
                }
            }
            if (change is { CheckModIdentity: true, Original: not null })
            {
                var original = ModIdentity.Read(UpdatePaths.Target(journal.TargetRoot, file.Path));
                if (original.Count == 0 || !original.SetEquals(identities))
                {
                    throw new IOException(LauncherText.Get("UpdateModIdentityMismatch", file.Path));
                }
            }
        }
        var directory = Path.Combine(journal.TargetRoot, "mods");
        if (!Directory.Exists(directory))
        {
            return;
        }
        var releasePaths = journal.Release.Files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changedPaths = journal.Changes.Select(change => change.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var local in Directory.EnumerateFiles(directory, "*.jar"))
        {
            token.ThrowIfCancellationRequested();
            var relative = "mods/" + Path.GetFileName(local);
            if (releasePaths.Contains(relative) || changedPaths.Contains(relative))
            {
                continue;
            }
            var path = UpdatePaths.Target(journal.TargetRoot, relative);
            HashSet<string> identities;
            try
            {
                identities = ModIdentity.Read(path);
            }
            catch (InvalidDataException)
            {
                continue;
            }
            if (!identities.Any(owners.ContainsKey))
            {
                continue;
            }
            if (!identities.All(owners.ContainsKey))
            {
                throw new IOException(LauncherText.Get("UpdateSharedJarConflict", relative));
            }
            journal.Changes.Add(new PendingFileChange
            {
                Path = relative,
                Original = await ReadLocalFileAsync(journal.TargetRoot, relative, token)
            });
        }
        journal.Changes = journal.Changes.OrderBy(change => change.Replacement != null).ToList();
    }

    private static async Task<ManagedFile> ReadLocalFileAsync(string root, string relative, CancellationToken token)
    {
        var path = UpdatePaths.Target(root, relative);
        await using var stream = File.OpenRead(path);
        return new ManagedFile
        {
            Path = relative,
            Size = stream.Length,
            Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant()
        };
    }

    public void StartLauncherReplacement(FileUpdateTransaction transaction)
    {
        var executable = Environment.ProcessPath ?? throw new IOException(LauncherText.Get("LauncherPathUnavailable"));
        if (File.Exists(Path.Combine(applicationRoot, "ArysLauncher.dll")))
        {
            throw new IOException(LauncherText.Get("LauncherUpdateRequiresExe"));
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
        using var process = Process.Start(info) ?? throw new IOException(LauncherText.Get("LauncherUpdaterStartFailed"));
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
            throw new InvalidDataException(LauncherText.Get("UpdateResponseOutOfRange"));
        }
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > maximumBytes)
            {
                throw new InvalidDataException(LauncherText.Get("UpdateResponseTooLarge"));
            }
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
}
