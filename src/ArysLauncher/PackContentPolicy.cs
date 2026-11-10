using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArysLauncher;

internal static class PackContentPolicy
{
    private static readonly ContentRules Rules = Load();

    public static GameOptionRules GameOptions => Rules.GameOptions;

    public static bool IsInitialSettingsPath(string path)
    {
        return !Rules.ManagedSettingsPaths.Contains(path, StringComparer.OrdinalIgnoreCase) &&
            (Rules.InitialSettingsPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
                path.StartsWith("shaderpacks/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".zip.txt", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsArysMod(string path)
    {
        var normalized = path.Replace('\\', '/');
        return IsModJar(normalized) &&
            (normalized.StartsWith("mods/arysdungeonmod-", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("mods/arysdungeonmod.jar", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsDistributable(string relativePath)
    {
        var path = relativePath.Replace('\\', '/');
        var root = path.Split('/')[0];
        if (Rules.ExcludedRoots.Contains(root, StringComparer.OrdinalIgnoreCase) ||
            Rules.ExcludedRootPrefixes.Any(prefix => root.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
            Rules.ExcludedPaths.Contains(path, StringComparer.OrdinalIgnoreCase) ||
            Rules.ExcludedPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
            Regex.IsMatch(path, "^(?:hs_err_pid|replay_pid)[0-9]+\\.log$", RegexOptions.IgnoreCase))
        {
            return false;
        }
        return !root.Equals("mods", StringComparison.OrdinalIgnoreCase) || IsModJar(path);
    }

    public static bool IsImportable(string path)
    {
        return path.Equals("options.txt", StringComparison.OrdinalIgnoreCase) ||
            IsDistributable(path) && !Rules.LegacyConfigPaths.Contains(path, StringComparer.OrdinalIgnoreCase) &&
            !Rules.LegacyConfigPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsModJar(string path)
    {
        return path.LastIndexOf('/') == "mods".Length && path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
    }

    public static void PrepareFirstLaunch(string gameDirectory)
    {
        foreach (var relative in Rules.FirstLaunchCleanup)
        {
            var target = UpdatePaths.Target(gameDirectory, relative);
            if (Directory.Exists(target))
            {
                var files = new DirectoryInfo(target).EnumerateFileSystemInfos("*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    AttributesToSkip = 0
                });
                if (files.Any(file => (file.Attributes & FileAttributes.ReparsePoint) != 0))
                {
                    throw new IOException(LauncherText.Get("FirstLaunchLinkedCache"));
                }
                Directory.Delete(target, true);
            }
            else if (File.Exists(target))
            {
                File.Delete(target);
            }
        }
    }

    private static ContentRules Load()
    {
        using var stream = typeof(PackContentPolicy).Assembly.GetManifestResourceStream("ArysLauncher.pack-content-policy.json") ??
            throw new InvalidDataException(LauncherText.Get("PackRulesMissing"));
        return JsonSerializer.Deserialize<ContentRules>(stream) ?? throw new InvalidDataException(LauncherText.Get("PackRulesEmpty"));
    }

    public sealed class GameOptionRules
    {
        public GameOptionRules()
        {
        }

        public string[] Keys { get; set; } = Array.Empty<string>();
        public string[] Prefixes { get; set; } = Array.Empty<string>();
        public Dictionary<string, string> Defaults { get; set; } = new();
        public string[] Languages { get; set; } = Array.Empty<string>();
    }

    private sealed class ContentRules
    {
        public ContentRules()
        {
        }

        public string[] ExcludedRoots { get; set; } = Array.Empty<string>();
        public string[] ExcludedRootPrefixes { get; set; } = Array.Empty<string>();
        public string[] ExcludedPaths { get; set; } = Array.Empty<string>();
        public string[] ExcludedPrefixes { get; set; } = Array.Empty<string>();
        public string[] LegacyConfigPaths { get; set; } = Array.Empty<string>();
        public string[] LegacyConfigPrefixes { get; set; } = Array.Empty<string>();
        public string[] InitialSettingsPrefixes { get; set; } = Array.Empty<string>();
        public string[] ManagedSettingsPaths { get; set; } = Array.Empty<string>();
        public GameOptionRules GameOptions { get; set; } = new();
        public string[] FirstLaunchCleanup { get; set; } = Array.Empty<string>();
    }
}
