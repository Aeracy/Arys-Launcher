using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArysLauncher;

public sealed class PackImporter
{
    private const long MaximumExpandedBytes = 128L * 1024 * 1024 * 1024;
    private static readonly HashSet<string> GameDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "mods", "config", "defaultconfigs", "resourcepacks", "shaderpacks", "datapacks", "resources", "resource",
        "kubejs", "scripts", "libraries", "versions", "assets", "saves", "natives", "patchouli_books", "openloader",
        "global_packs", "paxi", "fancymenu", "CustomSkinLoader", "journeymap", "xaero", "local"
    };
    private static readonly HashSet<string> GameFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "options.txt", "optionsof.txt", "optionsshaders.txt", "servers.dat", "pack.png", "pack.mcmeta"
    };
    private static readonly HashSet<string> DownloadHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cdn.modrinth.com", "github.com", "raw.githubusercontent.com", "gitlab.com",
        "mediafilez.forgecdn.net", "edge.forgecdn.net", "media.forgecdn.net"
    };
    private readonly LauncherStorage storage;

    public PackImporter(LauncherStorage storage)
    {
        this.storage = storage;
    }

    public PackPlan Inspect(string archivePath, bool includeOptional, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > 100000)
        {
            throw new InvalidDataException("整合包包含超过 100000 个条目，请先拆分归档。");
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<ZipArchiveEntry>();
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = entry.FullName.Replace('\\', '/');
            LauncherStorage.SafeTarget(Path.GetTempPath(), normalized.TrimEnd('/'));
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("整合包不能包含符号链接。");
            }
            if (!names.Add(normalized.TrimEnd('/')))
            {
                throw new InvalidDataException($"整合包含有重复或仅大小写不同的路径：{entry.FullName}");
            }
            if (normalized.EndsWith('/'))
            {
                continue;
            }
            if (entry.Length > 16L * 1024 * 1024 * 1024)
            {
                throw new InvalidDataException("单个归档文件超过 16 GiB。");
            }
            expanded = checked(expanded + entry.Length);
            if (expanded > MaximumExpandedBytes)
            {
                throw new InvalidDataException("整合包解压后超过 128 GiB，请先拆分归档。");
            }
            entries.Add(entry);
        }
        var index = FindMetadata(entries, "modrinth.index.json");
        if (index != null)
        {
            return InspectModrinth(archivePath, entries, index, includeOptional);
        }
        return InspectBundledZip(archivePath, entries);
    }

    public async Task<GameInstallation> ImportAsync(PackPlan plan, string instanceName, string minecraftVersion,
        string forgeVersion, IProgress<WorkSnapshot> progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(instanceName))
        {
            throw new InvalidOperationException("请填写整合包名称。");
        }
        if (!Regex.IsMatch(minecraftVersion, "^[A-Za-z0-9_.-]{1,100}$") ||
            !string.IsNullOrEmpty(forgeVersion) && !Regex.IsMatch(forgeVersion, "^[0-9]+(\\.[0-9]+){1,5}$"))
        {
            throw new InvalidOperationException("请填写有效的 Minecraft 与 Forge 版本号。");
        }
        var final = Path.GetFullPath(storage.GameDirectory);
        var instanceRoot = Path.GetDirectoryName(final) ?? throw new InvalidOperationException("游戏目录不能是磁盘根目录。");
        Directory.CreateDirectory(instanceRoot);
        if (Directory.Exists(final) && Directory.EnumerateFileSystemEntries(final).Any())
        {
            throw new InvalidOperationException("游戏目录已有文件，本次导入不会覆盖现有整合包。请在首次安装时选择空目录。");
        }
        var id = Guid.NewGuid().ToString("N");
        var stagingRoot = Path.Combine(instanceRoot, ".staging");
        var staging = Path.Combine(stagingRoot, id);
        var requiredSpace = checked(plan.ExtractedBytes + plan.DownloadBytes * 2 + 64 * 1024 * 1024);
        var driveRoot = Path.GetPathRoot(instanceRoot);
        if (driveRoot != null && !driveRoot.StartsWith("\\\\"))
        {
            var drive = new DriveInfo(driveRoot);
            if (drive.IsReady && drive.AvailableFreeSpace < requiredSpace)
            {
                throw new IOException($"安装磁盘空间不足。本次导入预计需要预留 {requiredSpace / (1024.0 * 1024 * 1024):0.0} GiB。");
            }
        }
        var reporter = new WorkProgress(progress, plan.Entries.Count + plan.Downloads.Count, plan.ExtractedBytes + plan.DownloadBytes);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var client = NetworkClients.Create(storage.Settings, download: true);
        using var downloader = new ContentDownloader(storage, client);
        Directory.CreateDirectory(staging);
        try
        {
            reporter.Stage("正在导入", "正在提取文件并获取整合包依赖");
            var downloadPaths = plan.Downloads.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var independentEntries = plan.Entries.Select((entry, index) => (entry, index))
                .Where(item => !downloadPaths.Contains(item.entry.TargetPath)).ToArray();
            var overrideEntries = plan.Entries.Select((entry, index) => (entry, index))
                .Where(item => downloadPaths.Contains(item.entry.TargetPath)).ToArray();
            async Task ExtractAsync((ArchiveFile entry, int index)[] selectedEntries)
            {
                try
                {
                    await ExtractEntriesAsync(plan.ArchivePath, selectedEntries, staging, reporter, cancellation.Token);
                }
                catch
                {
                    await cancellation.CancelAsync();
                    throw;
                }
            }
            async Task DownloadAsync()
            {
                try
                {
                    var ordered = plan.Downloads.Select((file, index) => (file, index)).OrderByDescending(item => item.file.Size);
                    await Parallel.ForEachAsync(ordered, new ParallelOptions
                    {
                        MaxDegreeOfParallelism = DownloadPolicy.FileConcurrency(storage.Settings),
                        CancellationToken = cancellation.Token
                    }, async (item, token) =>
                    {
                        var progressIndex = plan.Entries.Count + item.index;
                        var file = item.file;
                        var request = new ContentRequest(file.Size, file.Sha512, HashAlgorithmName.SHA512, file.Downloads);
                        var cached = await downloader.FetchAsync(request, progressIndex, reporter, token);
                        var target = LauncherStorage.SafeTarget(staging, file.Path);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        token.ThrowIfCancellationRequested();
                        File.Copy(cached, target, false);
                        reporter.FileDone(progressIndex);
                    });
                }
                catch
                {
                    await cancellation.CancelAsync();
                    throw;
                }
            }
            await Task.WhenAll(ExtractAsync(independentEntries), DownloadAsync());
            if (overrideEntries.Length > 0)
            {
                reporter.Stage("正在应用配置", "按整合包规则应用客户端覆盖文件");
                await ExtractAsync(overrideEntries);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var instance = new GameInstallation
            {
                Id = id,
                Name = instanceName.Trim(),
                MinecraftVersion = minecraftVersion,
                ForgeVersion = forgeVersion,
                LaunchVersion = minecraftVersion == plan.MinecraftVersion && forgeVersion == plan.ForgeVersion ? plan.LaunchVersion : "",
                PackVersion = plan.PackVersion,
                SourceFormat = plan.Format,
                DirectoryPath = final,
                ModCount = Directory.Exists(Path.Combine(staging, "mods")) ? Directory.EnumerateFiles(Path.Combine(staging, "mods"), "*.jar").Count() : 0
            };
            await File.WriteAllTextAsync(Path.Combine(staging, "arys-installation.json"),
                JsonSerializer.Serialize(instance, LauncherStorage.JsonOptions), cancellationToken);
            reporter.Stage("正在完成安装", "文件已就绪，正在完成导入");
            if (Directory.Exists(final))
            {
                Directory.Delete(final, false);
            }
            Directory.Move(staging, final);
            reporter.Stage("导入完成", instance.Name);
            return instance;
        }
        catch
        {
            LauncherStorage.RemoveOwnedStaging(stagingRoot, staging);
            throw;
        }
    }

    private static PackPlan InspectModrinth(string archivePath, IReadOnlyList<ZipArchiveEntry> entries, ZipArchiveEntry index, bool includeOptional)
    {
        using var document = ReadMetadata(index);
        var root = document.RootElement;
        if (root.GetProperty("formatVersion").GetInt32() != 1 || root.GetProperty("game").GetString() != "minecraft")
        {
            throw new InvalidDataException("此 Modrinth 整合包格式或游戏类型暂不支持。");
        }
        var dependencies = root.GetProperty("dependencies");
        if (new[] { "fabric-loader", "quilt-loader", "neoforge" }.Any(key => dependencies.TryGetProperty(key, out _)))
        {
            throw new InvalidDataException("本启动器使用 Forge，此整合包要求其他 Mod 加载器。");
        }
        var prefix = MetadataPrefix(index, "modrinth.index.json");
        var files = new List<PackFile>();
        var optionalCount = 0;
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in root.GetProperty("files").EnumerateArray())
        {
            if (file.TryGetProperty("env", out var environment) && environment.TryGetProperty("client", out var client))
            {
                var requirement = client.GetString();
                if (requirement == "unsupported")
                {
                    continue;
                }
                if (requirement == "optional")
                {
                    optionalCount++;
                    if (!includeOptional)
                    {
                        continue;
                    }
                }
                else if (requirement != "required")
                {
                    throw new InvalidDataException("整合包包含未知的客户端依赖类型。");
                }
            }
            var path = file.GetProperty("path").GetString() ?? throw new InvalidDataException("文件路径为空。");
            LauncherStorage.SafeTarget(Path.GetTempPath(), path);
            path = path.Replace('\\', '/');
            if (!targets.Add(path))
            {
                throw new InvalidDataException($"文件清单存在重复目标：{path}");
            }
            var hashes = file.GetProperty("hashes");
            var sha512 = hashes.GetProperty("sha512").GetString() ?? "";
            var sha1 = hashes.GetProperty("sha1").GetString() ?? "";
            var urls = file.GetProperty("downloads").EnumerateArray().Select(element => new Uri(element.GetString()!)).ToArray();
            if (sha512.Length != 128 || sha512.Any(character => !Uri.IsHexDigit(character)) || sha1.Length != 40 ||
                sha1.Any(character => !Uri.IsHexDigit(character)) || urls.Length == 0 ||
                urls.Any(uri => uri.Scheme != "https" || !DownloadHosts.Contains(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)))
            {
                throw new InvalidDataException($"文件 {path} 的哈希或下载地址无效。当前支持 Modrinth、GitHub、GitLab 与 CurseForge CDN。");
            }
            var size = file.GetProperty("fileSize").GetInt64();
            if (size < 0 || size > 16L * 1024 * 1024 * 1024)
            {
                throw new InvalidDataException("下载文件的大小超出允许范围。");
            }
            files.Add(new PackFile(path, size, sha512.ToLowerInvariant(), sha1.ToLowerInvariant(), urls));
        }
        var overrides = new Dictionary<string, ArchiveFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in new[] { "overrides/", "client-overrides/" })
        {
            var layerPrefix = prefix + layer;
            foreach (var entry in entries.Where(entry => entry.FullName.Replace('\\', '/').StartsWith(layerPrefix, StringComparison.Ordinal)))
            {
                var target = entry.FullName.Replace('\\', '/')[layerPrefix.Length..];
                overrides[target] = new ArchiveFile(entry.FullName, target, entry.Length);
            }
        }
        var extracted = overrides.Values.Sum(entry => entry.Size);
        if (checked(extracted + files.Sum(file => file.Size)) > MaximumExpandedBytes || files.Count + overrides.Count > 100000)
        {
            throw new InvalidDataException("整合包文件数量或总体积超出允许范围。");
        }
        return new PackPlan(archivePath, root.GetProperty("name").GetString() ?? Path.GetFileNameWithoutExtension(archivePath),
            dependencies.GetProperty("minecraft").GetString() ?? "1.20.1",
            dependencies.TryGetProperty("forge", out var forge) ? forge.GetString() ?? "" : "", "",
            root.GetProperty("versionId").GetString() ?? "", "Modrinth", files, overrides.Values.ToArray(), extracted, optionalCount);
    }

    private static PackPlan InspectBundledZip(string archivePath, IReadOnlyList<ZipArchiveEntry> entries)
    {
        var manifest = FindMetadata(entries, "manifest.json");
        var minecraft = "1.20.1";
        var forge = "";
        var launchVersion = "";
        var name = Path.GetFileNameWithoutExtension(archivePath);
        var prefix = "";
        if (manifest != null)
        {
            using var document = ReadMetadata(manifest);
            if (document.RootElement.TryGetProperty("minecraft", out var minecraftElement))
            {
                if (document.RootElement.TryGetProperty("files", out var files) && files.GetArrayLength() > 0)
                {
                    throw new InvalidDataException("这是仅列出 CurseForge 项目编号的整合包，需要 CurseForge API 下载授权。请导出 .mrpack，或导出包含实际 mods 文件的 ZIP。");
                }
                minecraft = minecraftElement.GetProperty("version").GetString() ?? minecraft;
                if (minecraftElement.TryGetProperty("modLoaders", out var loaders))
                {
                    var loader = loaders.EnumerateArray().Select(element => element.GetProperty("id").GetString()).FirstOrDefault();
                    if (loader?.StartsWith("forge-") == true)
                    {
                        forge = loader[6..];
                    }
                    else if (!string.IsNullOrEmpty(loader))
                    {
                        throw new InvalidDataException("本启动器使用 Forge，此整合包要求其他 Mod 加载器。");
                    }
                }
                name = document.RootElement.TryGetProperty("name", out var packName) ? packName.GetString() ?? name : name;
                prefix = MetadataPrefix(manifest, "manifest.json") + "overrides/";
            }
        }
        if (string.IsNullOrEmpty(prefix))
        {
            var gameRoots = entries.Select(entry => entry.FullName.Replace('\\', '/'))
                .Where(path => path.Contains(".minecraft/", StringComparison.OrdinalIgnoreCase))
                .Select(path => path[..(path.IndexOf(".minecraft/", StringComparison.OrdinalIgnoreCase) + 11)])
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (gameRoots.Length > 1)
            {
                throw new InvalidDataException("ZIP 中含有多个游戏目录，请导出本整合包的单一游戏目录。");
            }
            if (gameRoots.Length == 1)
            {
                prefix = gameRoots[0];
            }
            else
            {
                var roots = entries.Select(entry => entry.FullName.Replace('\\', '/').Split('/')[0]).Distinct().ToArray();
                if (roots.Length == 1 && !GameDirectories.Contains(roots[0]))
                {
                    prefix = roots[0] + "/";
                }
            }
        }
        var payload = new List<ArchiveFile>();
        foreach (var entry in entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var target = normalized[prefix.Length..];
            var first = target.Split('/')[0];
            if (target.Contains('/') ? GameDirectories.Contains(first) : GameFiles.Contains(target))
            {
                payload.Add(new ArchiveFile(entry.FullName, target, entry.Length));
            }
        }
        if (payload.Count == 0)
        {
            throw new InvalidDataException("ZIP 中没有可识别的游戏文件。请导入 .mrpack，或包含 mods、config 等目录的整合包 ZIP。");
        }
        var versionFiles = payload.Where(file => Regex.IsMatch(file.TargetPath, "^versions/[^/]+/[^/]+\\.json$", RegexOptions.IgnoreCase)).ToArray();
        var versions = new List<(string Id, string Parent, JsonElement Root)>();
        foreach (var versionFile in versionFiles)
        {
            using var document = ReadMetadata(entries.First(entry => entry.FullName == versionFile.EntryName));
            var root = document.RootElement;
            if (root.TryGetProperty("id", out var idElement))
            {
                versions.Add((idElement.GetString() ?? "", root.TryGetProperty("inheritsFrom", out var parent) ? parent.GetString() ?? "" : "", root.Clone()));
            }
        }
        var leafVersions = versions.Where(version => versions.All(other => other.Parent != version.Id)).ToArray();
        if (leafVersions.Length > 1)
        {
            throw new InvalidDataException("ZIP 中含有多个可启动版本，请先导出单个游戏实例。");
        }
        if (leafVersions.Length == 1)
        {
            var version = leafVersions[0];
            launchVersion = version.Id;
            minecraft = string.IsNullOrEmpty(version.Parent) ? version.Id : version.Parent;
            if (version.Root.TryGetProperty("libraries", out var libraries))
            {
                foreach (var library in libraries.EnumerateArray())
                {
                    var libraryName = library.GetProperty("name").GetString() ?? "";
                    var match = Regex.Match(libraryName, "^net\\.minecraftforge:(?:forge|fmlloader):([0-9.]+)-([0-9.]+)");
                    if (match.Success)
                    {
                        minecraft = match.Groups[1].Value;
                        forge = match.Groups[2].Value;
                        break;
                    }
                }
            }
        }
        return new PackPlan(archivePath, name, minecraft, forge, launchVersion, "", "ZIP", Array.Empty<PackFile>(),
            payload, payload.Sum(file => file.Size), 0);
    }

    private static async Task ExtractEntriesAsync(string archivePath, IReadOnlyList<(ArchiveFile entry, int index)> entries,
        string targetRoot, WorkProgress progress, CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            return;
        }
        using var archive = ZipFile.OpenRead(archivePath);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            foreach (var (file, index) in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = archive.GetEntry(file.EntryName) ?? throw new InvalidDataException("整合包在导入期间发生了变化。");
                if (entry.Length != file.Size)
                {
                    throw new InvalidDataException("整合包在导入期间发生了变化。");
                }
                var target = LauncherStorage.SafeTarget(targetRoot, file.TargetPath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = entry.Open();
                await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                long written = 0;
                while (true)
                {
                    var count = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
                    if (count == 0)
                    {
                        break;
                    }
                    if (written + count > file.Size)
                    {
                        throw new InvalidDataException("解压数据超出了归档声明的大小。");
                    }
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    written += count;
                    progress.FileBytes(index, written);
                }
                if (written != file.Size)
                {
                    throw new InvalidDataException("归档文件数据不完整。");
                }
                progress.FileDone(index);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static ZipArchiveEntry? FindMetadata(IEnumerable<ZipArchiveEntry> entries, string filename)
    {
        return entries.Where(entry => entry.FullName.Replace('\\', '/').Split('/').Last().Equals(filename, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.FullName.Count(character => character == '/')).FirstOrDefault();
    }

    private static string MetadataPrefix(ZipArchiveEntry entry, string filename)
    {
        return entry.FullName.Replace('\\', '/')[..^filename.Length];
    }

    private static JsonDocument ReadMetadata(ZipArchiveEntry entry)
    {
        if (entry.Length > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("整合包清单超过 16 MiB。");
        }
        using var input = entry.Open();
        return JsonDocument.Parse(input);
    }
}
