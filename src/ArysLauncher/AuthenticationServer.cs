namespace ArysLauncher;

public static class AuthenticationServer
{
    public const string LittleSkinAddress = "https://littleskin.cn/api/yggdrasil/";
    public const string LittleSkinRegistration = "https://littleskin.cn/auth/register";

    public static bool IsLittleSkin(string address)
    {
        return Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
            uri.Host.Equals("littleskin.cn", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.TrimEnd('/').Equals("/api/yggdrasil", StringComparison.Ordinal);
    }

    public static Uri? ValidateRegistrationUrl(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException(LauncherText.Get("RegistrationUrlInvalid"));
        }
        return uri;
    }

    public static (string Host, int Port)? ParseGameAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }
        if (address.Any(char.IsWhiteSpace) || !Uri.TryCreate("minecraft://" + address, UriKind.Absolute, out var uri) ||
            string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath is not ("" or "/") || uri.Port == 0 || uri.Port > 65535)
        {
            throw new InvalidOperationException(LauncherText.Get("AutoJoinAddressInvalid"));
        }
        return (uri.Host.Trim('[', ']'), uri.Port < 0 ? 25565 : uri.Port);
    }
}
