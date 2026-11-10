using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ArysLauncher;

[JsonConverter(typeof(JsonStringEnumConverter<UpdateKind>))]
public enum UpdateKind
{
    Launcher,
    Pack
}

public sealed class UpdateChannel
{
    public string LauncherRepository { get; set; } = "";
    public string PackManifestUrl { get; set; } = "";
    public string LauncherTagPrefix { get; set; } = "v";
    public string PackTagPrefix { get; set; } = "pack-v";
    public string PublicKey { get; set; } = "";

    public Uri? ManifestAddress(UpdateKind kind)
    {
        var legacyPackAddress = $"https://github.com/{LauncherRepository}/releases/latest/download/pack-update.json";
        if (kind == UpdateKind.Pack && !string.IsNullOrWhiteSpace(PackManifestUrl) &&
            !PackManifestUrl.Equals(legacyPackAddress, StringComparison.OrdinalIgnoreCase))
        {
            return UpdatePaths.HttpsAddress(PackManifestUrl);
        }
        if (string.IsNullOrWhiteSpace(LauncherRepository))
        {
            return null;
        }
        if (!Regex.IsMatch(LauncherRepository, "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$") ||
            LauncherRepository.Equals("Aeracy/arysmod", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(LauncherText.Get("SeparateReleaseRepositoryRequired"));
        }
        return new Uri($"https://api.github.com/repos/{LauncherRepository}/releases?per_page=100");
    }
}

public sealed class UpdateManifest
{
    public int SchemaVersion { get; set; } = 1;
    public UpdateKind Kind { get; set; }
    public string Version { get; set; } = "";
    public string MinimumLauncherVersion { get; set; } = "1.0.0";
    public string ReleaseNotes { get; set; } = "";
    public PackRelease? Pack { get; set; }
    public List<ReleaseFile> Files { get; set; } = new();
    public List<ManagedFile> RetiredFiles { get; set; } = new();

    public void Validate(UpdateKind expectedKind, bool legacyState = false)
    {
        if (SchemaVersion != 1 || Kind != expectedKind || !System.Version.TryParse(Version, out _) ||
            !System.Version.TryParse(MinimumLauncherVersion, out var minimum) ||
            minimum > System.Version.Parse(App.LauncherVersion) || Files.Count is 0 or > 10000 ||
            RetiredFiles.Count > 30000 || ReleaseNotes.Length > 16000)
        {
            throw new InvalidDataException(LauncherText.Get("UpdateManifestIncompatible"));
        }
        if (Kind == UpdateKind.Pack && (Pack == null || string.IsNullOrWhiteSpace(Pack.Id) ||
            Pack.Id.Length > 100 || string.IsNullOrWhiteSpace(Pack.Name) ||
            !Regex.IsMatch(Pack.MinecraftVersion, "^[0-9]+\\.[0-9]+(\\.[0-9]+)?$") ||
            !Regex.IsMatch(Pack.ForgeVersion, "^[0-9]+(\\.[0-9]+){1,5}$")))
        {
            throw new InvalidDataException(LauncherText.Get("UpdatePackVersionInvalid"));
        }
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in Files)
        {
            UpdatePaths.ValidateFile(file, Kind, legacyState);
            if (!paths.Add(file.Path))
            {
                throw new InvalidDataException(LauncherText.Get("UpdateDuplicateFilePath"));
            }
            if (file.Downloads.Count is 0 or > 8 || file.Path == "options.txt" && file.Size > 65536)
            {
                throw new InvalidDataException(LauncherText.Get("UpdateDownloadAddressMissing"));
            }
            foreach (var address in file.Downloads)
            {
                UpdatePaths.HttpsAddress(address);
            }
            total = checked(total + file.Size);
        }
        if (total > 128L * 1024 * 1024 * 1024)
        {
            throw new InvalidDataException(LauncherText.Get("UpdateTotalSizeTooLarge"));
        }
        foreach (var file in RetiredFiles)
        {
            UpdatePaths.ValidateFile(file, Kind, legacyState);
        }
        if (Kind == UpdateKind.Launcher && !paths.Contains("ArysLauncher.exe"))
        {
            throw new InvalidDataException(LauncherText.Get("UpdateExecutableMissing"));
        }
    }
}

public sealed class PackRelease
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string MinecraftVersion { get; set; } = "";
    public string ForgeVersion { get; set; } = "";
}

public class ManagedFile
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

public sealed class ReleaseFile : ManagedFile
{
    public List<string> Downloads { get; set; } = new();
    public bool PreserveExisting { get; set; }
}

public sealed class InstalledFiles
{
    public string ProductId { get; set; } = "";
    public string Version { get; set; } = "";
    public List<ManagedFile> Files { get; set; } = new();
}

internal static class UpdatePaths
{
    private static readonly HashSet<string> PackDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "mods", "config", "defaultconfigs", "resourcepacks", "shaderpacks", "datapacks", "resources", "resource",
        "kubejs", "scripts", "patchouli_books", "openloader", "global_packs", "paxi", "fancymenu", "moonlight-global-datapacks"
    };

    public static Uri HttpsAddress(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidDataException(LauncherText.Get("UpdateAddressRequiresHttps"));
        }
        return uri;
    }

    public static void ValidateFile(ManagedFile file, UpdateKind kind, bool legacyState = false)
    {
        LauncherStorage.SafeTarget(System.IO.Path.GetTempPath(), file.Path);
        var segments = file.Path.Split('/');
        var launcherFile = file.Path is "ArysLauncher.exe" or "release-channel.json" or "README.md" ||
            segments.Length > 1 && segments[0] == "Licenses";
        var packFile = file.Path == "options.txt" || segments.Length > 1 && PackDirectories.Contains(segments[0]) &&
            (PackContentPolicy.IsDistributable(file.Path) || legacyState && PackContentPolicy.IsArysConfiguration(file.Path));
        if (file.Path.Contains('\\') || file.Size < 0 || file.Size > 16L * 1024 * 1024 * 1024 ||
            file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit) ||
            !(kind == UpdateKind.Launcher ? launcherFile : packFile))
        {
            throw new InvalidDataException(LauncherText.Get("UpdateFileOutsideScope", file.Path));
        }
    }

    public static string Target(string root, string path)
    {
        var destination = LauncherStorage.SafeTarget(root, path);
        var current = new DirectoryInfo(System.IO.Path.GetFullPath(root));
        while (current != null)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(LauncherText.Get("UpdateLinkedDirectory"));
            }
            current = current.Parent;
        }
        var relative = System.IO.Path.GetRelativePath(root, destination);
        var candidate = root;
        foreach (var part in relative.Split(System.IO.Path.DirectorySeparatorChar))
        {
            candidate = System.IO.Path.Combine(candidate, part);
            if ((File.Exists(candidate) || Directory.Exists(candidate)) &&
                (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(LauncherText.Get("UpdateLinkedFile"));
            }
        }
        return destination;
    }

    public static async Task<bool> MatchesAsync(string path, ManagedFile expected, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != expected.Size)
        {
            return false;
        }
        await using var stream = File.OpenRead(path);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(digest).Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase);
    }
}
