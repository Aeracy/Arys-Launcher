using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArysLauncher;

internal static class GameOptionsSettings
{
    private static readonly Regex KeyBinding = new("^(?:key\\.(?:keyboard|mouse)\\.[a-z0-9_.-]+|scancode\\.\\d+)(?::(?:SHIFT|CONTROL|ALT))*$", RegexOptions.CultureInvariant);
    private static readonly Regex SettingName = new("^[A-Za-z0-9_.:-]+$", RegexOptions.CultureInvariant);

    public static async Task InitializeAsync(string gameDirectory, CancellationToken cancellationToken)
    {
        var path = UpdatePaths.Target(gameDirectory, "options.txt");
        if (File.Exists(path) && new FileInfo(path).Length > 65536)
        {
            throw new IOException(LauncherText.Get("GameOptionsTooLarge"));
        }
        var lines = File.Exists(path) ? await File.ReadAllLinesAsync(path, cancellationToken) : Array.Empty<string>();
        var rules = PackContentPolicy.GameOptions;
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var separator = line.IndexOf(':');
            if (separator > 0)
            {
                var key = line[..separator];
                var value = line[(separator + 1)..];
                if (IsAllowed(key, value, rules))
                {
                    options[key] = value;
                }
            }
        }
        foreach (var (key, value) in rules.Defaults)
        {
            options[key] = value;
        }
        options["lang"] = ResolveLanguage(CultureInfo.CurrentUICulture, rules.Languages);
        var temporary = UpdatePaths.Target(gameDirectory, "options.arys-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllLinesAsync(temporary, options.Select(option => option.Key + ":" + option.Value), new UTF8Encoding(false), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static bool IsAllowed(string key, string value, PackContentPolicy.GameOptionRules rules)
    {
        if ((!rules.Keys.Contains(key, StringComparer.Ordinal) && !rules.Defaults.ContainsKey(key) &&
                !rules.Prefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal))) ||
            value.Length > 16384 || value.Any(char.IsControl))
        {
            return false;
        }
        if (key.StartsWith("key_", StringComparison.Ordinal))
        {
            return SettingName.IsMatch(key) && KeyBinding.IsMatch(value);
        }
        if (key is "lang" or "tutorialStep")
        {
            return value.Length > 0 && value.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '_');
        }
        try
        {
            using var document = JsonDocument.Parse(value);
            var parsed = document.RootElement;
            if (key is "resourcePacks" or "incompatibleResourcePacks")
            {
                return parsed.ValueKind == JsonValueKind.Array && parsed.GetArrayLength() <= 256 &&
                    parsed.EnumerateArray().All(IsPackName);
            }
            return parsed.ValueKind switch
            {
                JsonValueKind.True or JsonValueKind.False => true,
                JsonValueKind.Number => parsed.TryGetDouble(out var number) && double.IsFinite(number),
                JsonValueKind.String => parsed.GetString() is { Length: <= 128 } text && text.IndexOfAny([':', '/', '\\', '\r', '\n']) < 0,
                _ => false
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsPackName(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String || element.GetString() is not { Length: > 0 and <= 256 } name)
        {
            return false;
        }
        if (name.StartsWith("mod/", StringComparison.Ordinal))
        {
            return Regex.IsMatch(name, "^mod/[a-z0-9_.-]+:[a-z0-9_.-]+$", RegexOptions.CultureInvariant);
        }
        var filename = name.StartsWith("file/", StringComparison.Ordinal) ? name[5..] : name;
        return filename.Length > 0 && filename is not ("." or "..") && filename.IndexOfAny(['\\', '/', ':', '\r', '\n']) < 0;
    }

    private static string ResolveLanguage(CultureInfo culture, string[] languages)
    {
        var exact = culture.Name.Replace('-', '_').ToLowerInvariant();
        if (languages.Contains(exact, StringComparer.Ordinal))
        {
            return exact;
        }
        var language = culture.TwoLetterISOLanguageName;
        if (language == "zh")
        {
            return culture.Name.Contains("Hant", StringComparison.OrdinalIgnoreCase) || exact.EndsWith("_tw") || exact.EndsWith("_mo") ? "zh_tw" :
                exact.EndsWith("_hk") ? "zh_hk" : "zh_cn";
        }
        var alias = language switch
        {
            "nb" or "no" => "no_no",
            "nn" => "nn_no",
            "sr" => "sr_sp",
            "fil" => "fil_ph",
            _ => ""
        };
        if (alias.Length > 0)
        {
            return alias;
        }
        try
        {
            var standard = CultureInfo.CreateSpecificCulture(language).Name.Replace('-', '_').ToLowerInvariant();
            if (languages.Contains(standard, StringComparer.Ordinal))
            {
                return standard;
            }
        }
        catch (CultureNotFoundException)
        {
        }
        return languages.FirstOrDefault(code => code.StartsWith(language + "_", StringComparison.Ordinal)) ?? "en_us";
    }
}
