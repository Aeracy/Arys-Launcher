using System.IO;
using System.Text.Json.Serialization;

namespace ArysLauncher;

public sealed class LauncherSettings
{
    public const int DefaultWindowWidth = 1920;
    public const int DefaultWindowHeight = 1080;
    public const bool DefaultFullscreen = true;

    public string JavaPath { get; set; } = "";
    public bool AutomaticJavaSelection { get; set; } = true;
    public bool AutomaticMemoryAllocation { get; set; } = true;
    public int MaximumMemoryMb { get; set; } = 8192;
    public int WindowWidth { get; set; } = DefaultWindowWidth;
    public int WindowHeight { get; set; } = DefaultWindowHeight;
    public bool Fullscreen { get; set; } = DefaultFullscreen;
    public string JvmArguments { get; set; } = "";
    public bool JvmArgumentsAreComplete { get; set; }
    public string GameArguments { get; set; } = "";
    public bool AutomaticDownloadConnections { get; set; } = true;
    public double DownloadLimitMiBPerSecond { get; set; }
    public int DownloadConcurrency { get; set; } = 8;
    public int PerHostConnections { get; set; } = 4;
    public string ProxyAddress { get; set; } = "";
    public bool ReducedMotion { get; set; }
    public bool CheckUpdatesAtStartup { get; set; } = true;
    public bool IncludeOptionalMods { get; set; } = true;
    public bool MinimizeOnLaunch { get; set; }
    public string SelectedAccountId { get; set; } = "";
    public string AuthlibInjectorPath { get; set; } = "";
    public string AuthenticationServerAddress { get; set; } = AuthenticationServer.LittleSkinAddress;
    public string AuthenticationServerName { get; set; } = "LittleSkin";
    public string AuthenticationRegistrationUrl { get; set; } = AuthenticationServer.LittleSkinRegistration;
    public string JoinServerAddress { get; set; } = "";

    public void Validate()
    {
        if (MaximumMemoryMb < 256 || MaximumMemoryMb > 131072)
        {
            throw new InvalidOperationException(LauncherText.Get("MemoryLimitInvalid"));
        }
        if (WindowWidth < 640 || WindowWidth > 16384 || WindowHeight < 360 || WindowHeight > 16384)
        {
            throw new InvalidOperationException(LauncherText.Get("WindowSizeInvalid"));
        }
        if (DownloadConcurrency < 1 || DownloadConcurrency > 32 || PerHostConnections < 1 || PerHostConnections > 16)
        {
            throw new InvalidOperationException(LauncherText.Get("DownloadConcurrencyInvalid"));
        }
        if (!double.IsFinite(DownloadLimitMiBPerSecond) ||
            DownloadLimitMiBPerSecond != 0 && (DownloadLimitMiBPerSecond < 0.1 || DownloadLimitMiBPerSecond > 102400))
        {
            throw new InvalidOperationException(LauncherText.Get("DownloadSpeedLimitInvalid"));
        }
        if (!string.IsNullOrWhiteSpace(ProxyAddress) &&
            (!Uri.TryCreate(ProxyAddress, UriKind.Absolute, out var proxy) ||
                proxy.Scheme is not ("http" or "https" or "socks5") || !string.IsNullOrEmpty(proxy.UserInfo)))
        {
            throw new InvalidOperationException(LauncherText.Get("ProxyAddressInvalid"));
        }
        if (!AutomaticJavaSelection && !string.IsNullOrWhiteSpace(JavaPath) && !File.Exists(JavaPath))
        {
            throw new InvalidOperationException(LauncherText.Get("JavaExecutableMissing"));
        }
        if (!string.IsNullOrWhiteSpace(AuthenticationServerAddress))
        {
            AccountService.NormalizeServer(AuthenticationServerAddress);
        }
        AuthenticationServer.ValidateRegistrationUrl(AuthenticationRegistrationUrl);
        AuthenticationServer.ParseGameAddress(JoinServerAddress);
    }
}

public enum AccountKind
{
    Microsoft,
    External
}

public sealed class LauncherAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public AccountKind Kind { get; set; }
    public string PlayerName { get; set; } = "";
    public string PlayerUuid { get; set; } = "";
    public string ServerAddress { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string MicrosoftClientId { get; set; } = "";
    public string ProtectedTokens { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset LastUsedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public string Description
    {
        get
        {
            return Kind == AccountKind.Microsoft ? LauncherText.Get("MicrosoftAccountDescription") :
                LauncherText.Get("ExternalAccountDescription", (string.IsNullOrEmpty(ServerName) ? new Uri(ServerAddress).Host : ServerName));
        }
    }

    public override string ToString()
    {
        return $"{PlayerName}  ·  {(Kind == AccountKind.Microsoft ? "Microsoft" : LauncherText.Get("ExternalAuthentication"))}";
    }
}

public sealed class AccountTokens
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string ClientToken { get; set; } = "";
    public string Xuid { get; set; } = "";
    public string XboxUserToken { get; set; } = "";
    public DateTimeOffset XboxUserTokenExpiresAt { get; set; }
}

public sealed class GameInstallation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string MinecraftVersion { get; set; } = "1.20.1";
    public string ForgeVersion { get; set; } = "";
    public string LaunchVersion { get; set; } = "";
    public string DirectoryPath { get; set; } = "";
    public string SourceFormat { get; set; } = "";
    public string PackVersion { get; set; } = "";
    public int ModCount { get; set; }
    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool NeedsFirstLaunchCleanup { get; set; }

    [JsonIgnore]
    public string VersionLabel
    {
        get
        {
            return $"Minecraft {MinecraftVersion}  /  {(string.IsNullOrEmpty(ForgeVersion) ? LauncherText.Get("VanillaOrBundledLoader") : "Forge " + ForgeVersion)}";
        }
    }

    public override string ToString()
    {
        return Name;
    }
}

public sealed record PackFile(string Path, long Size, string Sha512, string Sha1, IReadOnlyList<Uri> Downloads);

public sealed record ArchiveFile(string EntryName, string TargetPath, long Size);

public sealed record PackPlan(
    string ArchivePath,
    string Name,
    string MinecraftVersion,
    string ForgeVersion,
    string LaunchVersion,
    string PackVersion,
    string Format,
    IReadOnlyList<PackFile> Downloads,
    IReadOnlyList<ArchiveFile> Entries,
    long ExtractedBytes,
    int OptionalFiles,
    bool IsArysPack)
{
    public long DownloadBytes
    {
        get
        {
            return Downloads.Sum(file => file.Size);
        }
    }
}

public sealed record WorkSnapshot(
    string Stage,
    string Detail,
    long CompletedBytes,
    long TotalBytes,
    int CompletedFiles,
    int TotalFiles,
    long NetworkBytes,
    int CacheHits,
    bool Indeterminate = false);

public sealed record GameProfile(string Id, string Name);

public sealed record AuthenticatedSession(LauncherAccount Account, AccountTokens Tokens);
