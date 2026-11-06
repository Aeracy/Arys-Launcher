using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace ArysLauncher;

internal static class ModIdentity
{
    public static bool IsModPath(string path)
    {
        return path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) &&
            path.LastIndexOf('/') == 4 && path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
    }

    public static HashSet<string> Read(string path)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        using var archive = ZipFile.OpenRead(path);
        var metadata = ReadEntry(archive, "META-INF/mods.toml");
        var sections = Regex.Matches(metadata,
            """(?ms)^[ \t]*\[\[[ \t]*mods[ \t]*\]\][ \t]*(?:#[^\r\n]*)?\r?\n(?<body>.*?)(?=^[ \t]*\[|\z)""");
        foreach (Match section in sections)
        {
            var match = Regex.Match(section.Groups["body"].Value,
                """(?m)^[ \t]*modId[ \t]*=[ \t]*["'](?<id>[a-z][a-z0-9_]{1,63})["'][ \t]*(?:#[^\r\n]*)?\r?$""");
            if (match.Success)
            {
                identities.Add("mod:" + match.Groups["id"].Value);
            }
        }
        if (identities.Count == 0)
        {
            var manifest = ReadEntry(archive, "META-INF/MANIFEST.MF").Replace("\r\n ", "").Replace("\n ", "");
            var module = Regex.Match(manifest, """(?m)^Automatic-Module-Name:[ \t]*(?<name>[A-Za-z0-9_.]+)[ \t]*\r?$""");
            if (Regex.IsMatch(manifest, """(?m)^FMLModType:[ \t]*LIBRARY[ \t]*\r?$""") && module.Success)
            {
                identities.Add("module:" + module.Groups["name"].Value);
            }
        }
        return identities;
    }

    private static string ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        if (entry == null)
        {
            return "";
        }
        if (entry.Length > 1024 * 1024)
        {
            throw new InvalidDataException(LauncherText.Get("ModDescriptorTooLarge"));
        }
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }
}
