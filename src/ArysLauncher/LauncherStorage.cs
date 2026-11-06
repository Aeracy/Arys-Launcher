using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ArysLauncher;

public sealed class LauncherStorage
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³"
    };
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public string Root { get; }
    public string GameDirectory { get; }
    public string Cache { get; }
    public string Components { get; }
    public string Logs { get; }
    public string Updates { get; }
    public LauncherSettings Settings { get; private set; }
    public GameInstallation? Installation { get; private set; }
    public List<LauncherAccount> Accounts { get; private set; }
    internal SemaphoreSlim AccountSessionGate { get; } = new(1, 1);
    private readonly object writeGate = new();

    public LauncherStorage(string? applicationRoot = null)
    {
        Root = ResolveDataRoot(applicationRoot ?? AppContext.BaseDirectory);
        GameDirectory = Path.Combine(Path.GetDirectoryName(Root)!, ".minecraft");
        Cache = Path.Combine(Root, "cache");
        Components = Path.Combine(Root, "components");
        Logs = Path.Combine(Root, "logs");
        Updates = Path.Combine(Root, "updates");
        foreach (var directory in new[] { Root, GameDirectory, Cache, Components, Logs })
        {
            Directory.CreateDirectory(directory);
        }
        Settings = Read("settings.json", new LauncherSettings());
        Installation = Read<GameInstallation?>("installation.json", null);
        if (Installation != null)
        {
            Installation.DirectoryPath = GameDirectory;
        }
        Accounts = Read("accounts.json", new List<LauncherAccount>());
    }

    public void SaveSettings(LauncherSettings settings)
    {
        settings.Validate();
        Write("settings.json", settings);
        Settings = settings;
    }

    public void SaveInstallation(GameInstallation installation)
    {
        Write("installation.json", installation);
        Installation = installation;
    }

    public void SaveAccount(LauncherAccount account, AccountTokens tokens, bool selectAccount = true)
    {
        account.ProtectedTokens = Protect(tokens);
        lock (writeGate)
        {
            var accounts = Accounts.Where(existing => !(existing.Kind == account.Kind && existing.PlayerUuid == account.PlayerUuid &&
                existing.ServerAddress.Equals(account.ServerAddress, StringComparison.OrdinalIgnoreCase))).Append(account).ToList();
            Write("accounts.json", accounts);
            Accounts = accounts;
            if (selectAccount)
            {
                Settings.SelectedAccountId = account.Id;
                SaveSettings(Settings);
            }
        }
    }

    public void RemoveAccount(LauncherAccount account)
    {
        lock (writeGate)
        {
            var accounts = Accounts.Where(existing => existing.Id != account.Id).ToList();
            Write("accounts.json", accounts);
            Accounts = accounts;
            if (Settings.SelectedAccountId == account.Id)
            {
                Settings.SelectedAccountId = Accounts.FirstOrDefault()?.Id ?? "";
                SaveSettings(Settings);
            }
        }
    }

    public static AccountTokens Unprotect(LauncherAccount account)
    {
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(account.ProtectedTokens),
                Encoding.UTF8.GetBytes("ArysLauncher.Accounts"), DataProtectionScope.CurrentUser);
            try
            {
                return JsonSerializer.Deserialize<AccountTokens>(bytes, JsonOptions) ?? throw new CryptographicException();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or JsonException)
        {
            throw new InvalidOperationException(LauncherText.Get("AccountDecryptionFailed"), exception);
        }
    }

    public static string SafeTarget(string root, string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith('/') || normalized.Contains(':') ||
            normalized.Split('/').Any(part => string.IsNullOrEmpty(part) || part is ".." or "." || part.EndsWith(' ') || part.EndsWith('.')))
        {
            throw new InvalidDataException(LauncherText.Get("PackPathUnsafe", relativePath));
        }
        foreach (var component in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var stem = component.Split('.')[0].TrimEnd(' ');
            if (component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                ReservedNames.Contains(stem))
            {
                throw new InvalidDataException(LauncherText.Get("PackPathUnsupported", relativePath));
            }
        }
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(fullRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(LauncherText.Get("PackPathOutsideGameDirectory"));
        }
        return target;
    }

    public static void RemoveOwnedStaging(string stagingRoot, string path)
    {
        var fullRoot = Path.GetFullPath(stagingRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(LauncherText.Get("StagingDirectoryOutOfBounds"));
        }
        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, true);
        }
    }

    private T Read<T>(string filename, T fallback)
    {
        var path = Path.Combine(Root, filename);
        if (!File.Exists(path))
        {
            return fallback;
        }
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException(LauncherText.Get("LauncherDataEmpty", filename));
    }

    internal T ReadRecord<T>(string filename, T fallback)
    {
        return Read(filename, fallback);
    }

    internal void Write<T>(string filename, T value)
    {
        lock (writeGate)
        {
            var destination = Path.Combine(Root, filename);
            var temporary = destination + ".new";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, JsonOptions);
                stream.Flush(true);
            }
            File.Move(temporary, destination, true);
        }
    }

    private static string Protect(AccountTokens tokens)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(tokens, JsonOptions);
        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(bytes, Encoding.UTF8.GetBytes("ArysLauncher.Accounts"), DataProtectionScope.CurrentUser));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string ResolveDataRoot(string applicationRoot)
    {
        var current = new DirectoryInfo(applicationRoot);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "launcher.marker")))
            {
                return Path.Combine(current.FullName, "data");
            }
            current = current.Parent;
        }
        return Path.Combine(applicationRoot, "data");
    }
}
