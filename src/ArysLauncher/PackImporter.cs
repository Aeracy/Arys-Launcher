using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
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
        "global_packs", "paxi", "fancymenu", "CustomSkinLoader", "journeymap", "xaero", "local", "moonlight-global-datapacks"
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
            throw new InvalidDataException(LauncherText.Get("PackTooManyEntries"));
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
                throw new InvalidDataException(LauncherText.Get("PackLinksUnsupported"));
            }
            if (!names.Add(normalized.TrimEnd('/')))
            {
                throw new InvalidDataException(LauncherText.Get("PackDuplicateArchivePath", entry.FullName));
            }
            if (normalized.EndsWith('/'))
            {
                continue;
            }
            if (entry.Length > 16L * 1024 * 1024 * 1024)
            {
                throw new InvalidDataException(LauncherText.Get("PackEntryTooLarge"));
            }
            expanded = checked(expanded + entry.Length);
            if (expanded > MaximumExpandedBytes)
            {
                throw new InvalidDataException(LauncherText.Get("PackExpandedSizeTooLarge"));
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
            throw new InvalidOperationException(LauncherText.Get("PackNameRequired"));
        }
        if (!Regex.IsMatch(minecraftVersion, "^[A-Za-z0-9_.-]{1,100}$") ||
            !string.IsNullOrEmpty(forgeVersion) && !Regex.IsMatch(forgeVersion, "^[0-9]+(\\.[0-9]+){1,5}$"))
        {
            throw new InvalidOperationException(LauncherText.Get("PackVersionsInvalid"));
        }
        var final = Path.GetFullPath(storage.GameDirectory);
        var instanceRoot = Path.GetDirectoryName(final) ?? throw new InvalidOperationException(LauncherText.Get("GameDirectoryIsDriveRoot"));
        Directory.CreateDirectory(instanceRoot);
        if (Directory.Exists(final) && Directory.EnumerateFileSystemEntries(final).Any())
        {
            throw new InvalidOperationException(LauncherText.Get("GameDirectoryNotEmpty"));
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
                throw new IOException(LauncherText.Get("ImportDiskSpaceLow", requiredSpace / (1024.0 * 1024 * 1024)));
            }
        }
        var reporter = new WorkProgress(progress, plan.Entries.Count + plan.Downloads.Count, plan.ExtractedBytes + plan.DownloadBytes);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var client = NetworkClients.Create(storage.Settings, download: true);
        using var downloader = new ContentDownloader(storage, client);
        Directory.CreateDirectory(staging);
        try
        {
            reporter.Stage(LauncherText.Get("ImportingPack"), LauncherText.Get("ExtractingAndDownloading"));
            var downloadPaths = plan.Downloads.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var independentEntries = plan.Entries.Select((entry, index) => (entry, index))
                .Where(item => !downloadPaths.Contains(item.entry.TargetPath)).ToArray();
            var overrideEntries = plan.Entries.Select((entry, index) => (entry, index))
                .Where(item => downloadPaths.Contains(item.entry.TargetPath)).ToArray();
            async Task ExtractAsync((ArchiveFile entry, int index)[] selectedEntries)
            {
                try
                {
                    await ExtractEntriesAsync(plan.ArchivePath, selectedEntries, staging, plan.IsArysPack, reporter, cancellation.Token);
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
                reporter.Stage(LauncherText.Get("ApplyingPackConfig"), LauncherText.Get("ApplyingClientOverrides"));
                await ExtractAsync(overrideEntries);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (plan.IsArysPack)
            {
                await GameOptionsSettings.InitializeAsync(staging, cancellationToken);
            }
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
                NeedsFirstLaunchCleanup = plan.IsArysPack,
                RuntimePrepared = false,
                ModCount = Directory.Exists(Path.Combine(staging, "mods")) ? Directory.EnumerateFiles(Path.Combine(staging, "mods"), "*.jar").Count() : 0
            };
            await File.WriteAllTextAsync(Path.Combine(staging, "arys-installation.json"),
                JsonSerializer.Serialize(instance, LauncherStorage.JsonOptions), cancellationToken);
            reporter.Stage(LauncherText.Get("FinishingInstallation"), LauncherText.Get("CompletingImport"));
            if (Directory.Exists(final))
            {
                Directory.Delete(final, false);
            }
            Directory.Move(staging, final);
            reporter.Stage(LauncherText.Get("ImportComplete"), instance.Name);
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
            throw new InvalidDataException(LauncherText.Get("ModrinthFormatUnsupported"));
        }
        var dependencies = root.GetProperty("dependencies");
        if (new[] { "fabric-loader", "quilt-loader", "neoforge" }.Any(key => dependencies.TryGetProperty(key, out _)))
        {
            throw new InvalidDataException(LauncherText.Get("PackLoaderUnsupported"));
        }
        var prefix = MetadataPrefix(index, "modrinth.index.json");
        var layers = new[] { prefix + "overrides/", prefix + "client-overrides/" };
        var indexFiles = root.GetProperty("files");
        var isArysPack = indexFiles.EnumerateArray().Any(file => PackContentPolicy.IsArysMod(file.GetProperty("path").GetString() ?? "")) ||
            entries.Any(entry =>
            {
                var path = entry.FullName.Replace('\\', '/');
                return layers.Any(layer => path.StartsWith(layer, StringComparison.Ordinal) && PackContentPolicy.IsArysMod(path[layer.Length..]));
            });
        var files = new List<PackFile>();
        var optionalCount = 0;
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in indexFiles.EnumerateArray())
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
                    throw new InvalidDataException(LauncherText.Get("PackClientDependencyUnknown"));
                }
            }
            var path = file.GetProperty("path").GetString() ?? throw new InvalidDataException(LauncherText.Get("PackFilePathMissing"));
            LauncherStorage.SafeTarget(Path.GetTempPath(), path);
            path = path.Replace('\\', '/');
            if (isArysPack && (!PackContentPolicy.IsImportable(path) || path.Equals("options.txt", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            if (!targets.Add(path))
            {
                throw new InvalidDataException(LauncherText.Get("PackDuplicateDestination", path));
            }
            var hashes = file.GetProperty("hashes");
            var sha512 = hashes.GetProperty("sha512").GetString() ?? "";
            var sha1 = hashes.GetProperty("sha1").GetString() ?? "";
            var urls = file.GetProperty("downloads").EnumerateArray().Select(element => new Uri(element.GetString()!)).ToArray();
            if (sha512.Length != 128 || sha512.Any(character => !Uri.IsHexDigit(character)) || sha1.Length != 40 ||
                sha1.Any(character => !Uri.IsHexDigit(character)) || urls.Length == 0 ||
                urls.Any(uri => uri.Scheme != "https" || !DownloadHosts.Contains(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)))
            {
                throw new InvalidDataException(LauncherText.Get("PackDownloadEntryInvalid", path));
            }
            var size = file.GetProperty("fileSize").GetInt64();
            if (size < 0 || size > 16L * 1024 * 1024 * 1024)
            {
                throw new InvalidDataException(LauncherText.Get("PackDownloadSizeInvalid"));
            }
            files.Add(new PackFile(path, size, sha512.ToLowerInvariant(), sha1.ToLowerInvariant(), urls));
        }
        var overrides = new Dictionary<string, ArchiveFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var layerPrefix in layers)
        {
            foreach (var entry in entries.Where(entry => entry.FullName.Replace('\\', '/').StartsWith(layerPrefix, StringComparison.Ordinal)))
            {
                var target = entry.FullName.Replace('\\', '/')[layerPrefix.Length..];
                if (isArysPack && !PackContentPolicy.IsImportable(target))
                {
                    continue;
                }
                overrides[target] = new ArchiveFile(entry.FullName, target, entry.Length);
            }
        }
        var extracted = overrides.Values.Sum(entry => entry.Size);
        if (checked(extracted + files.Sum(file => file.Size)) > MaximumExpandedBytes || files.Count + overrides.Count > 100000)
        {
            throw new InvalidDataException(LauncherText.Get("PackLimitsExceeded"));
        }
        return new PackPlan(archivePath, root.GetProperty("name").GetString() ?? Path.GetFileNameWithoutExtension(archivePath),
            dependencies.GetProperty("minecraft").GetString() ?? "1.20.1",
            dependencies.TryGetProperty("forge", out var forge) ? forge.GetString() ?? "" : "", "",
            root.GetProperty("versionId").GetString() ?? "", "Modrinth", files, overrides.Values.ToArray(), extracted, optionalCount, isArysPack);
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
                    throw new InvalidDataException(LauncherText.Get("CurseForgeManifestUnsupported"));
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
                        throw new InvalidDataException(LauncherText.Get("PackLoaderUnsupported"));
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
                throw new InvalidDataException(LauncherText.Get("MultipleGameDirectories"));
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
        var isArysPack = payload.Any(file => PackContentPolicy.IsArysMod(file.TargetPath));
        if (isArysPack)
        {
            payload.RemoveAll(file => !PackContentPolicy.IsImportable(file.TargetPath));
        }
        if (payload.Count == 0)
        {
            throw new InvalidDataException(LauncherText.Get("NoRecognizedPackFiles"));
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
            throw new InvalidDataException(LauncherText.Get("MultipleLaunchVersions"));
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
            payload, payload.Sum(file => file.Size), 0, isArysPack);
    }

    private static async Task ExtractEntriesAsync(string archivePath, IReadOnlyList<(ArchiveFile entry, int index)> entries,
        string targetRoot, bool isArysPack, WorkProgress progress, CancellationToken cancellationToken)
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
                var entry = archive.GetEntry(file.EntryName) ?? throw new InvalidDataException(LauncherText.Get("PackChangedDuringImport"));
                if (entry.Length != file.Size)
                {
                    throw new InvalidDataException(LauncherText.Get("PackChangedDuringImport"));
                }
                var target = LauncherStorage.SafeTarget(targetRoot, file.TargetPath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = entry.Open();
                if (isArysPack && file.TargetPath.Equals("options.txt", StringComparison.OrdinalIgnoreCase))
                {
                    if (file.Size > 65536)
                    {
                        throw new IOException(LauncherText.Get("GameOptionsTooLarge"));
                    }
                }
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
                        throw new InvalidDataException(LauncherText.Get("ArchiveExpandedEntryTooLarge"));
                    }
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    written += count;
                    progress.FileBytes(index, written);
                }
                if (written != file.Size)
                {
                    throw new InvalidDataException(LauncherText.Get("ArchiveEntryIncomplete"));
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
            throw new InvalidDataException(LauncherText.Get("PackMetadataTooLarge"));
        }
        using var input = entry.Open();
        return JsonDocument.Parse(input);
    }
}
