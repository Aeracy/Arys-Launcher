using System.Text.RegularExpressions;

namespace ArysLauncher;

internal static class GitHubDownloadRoutes
{
    private static readonly string[] Relays = { "https://ghfast.top/", "https://gh-proxy.com/" };

    public static IReadOnlyList<Uri> Expand(IReadOnlyList<Uri> sources, bool enabled)
    {
        if (!enabled)
        {
            return sources;
        }
        var result = new List<Uri>();
        foreach (var source in sources)
        {
            result.Add(source);
            if (IsPublicResource(source))
            {
                result.AddRange(Relays.Select(relay => new Uri(relay + source.AbsoluteUri)));
            }
        }
        return result.Distinct().ToArray();
    }

    public static bool IsGitHubRoute(Uri address)
    {
        if (IsPublicResource(address))
        {
            return true;
        }
        foreach (var relay in Relays)
        {
            if (address.AbsoluteUri.StartsWith(relay, StringComparison.OrdinalIgnoreCase) &&
                Uri.TryCreate(address.AbsoluteUri[relay.Length..], UriKind.Absolute, out var original) && IsPublicResource(original))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsPublicResource(Uri address)
    {
        if (address.Scheme != Uri.UriSchemeHttps || address.Port != 443 || address.UserInfo.Length != 0 || address.Fragment.Length != 0)
        {
            return false;
        }
        var segments = address.GetComponents(UriComponents.Path, UriFormat.Unescaped).Split('/');
        var api = address.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase);
        var offset = api ? 1 : 0;
        if (segments.Length < offset + 3 || api && segments[0] != "repos" ||
            !Regex.IsMatch(segments[offset], "^[A-Za-z0-9_.-]+$") ||
            !Regex.IsMatch(segments[offset + 1], "^[A-Za-z0-9_.-]+$") ||
            segments[offset].Equals("Aeracy", StringComparison.OrdinalIgnoreCase) &&
                segments[offset + 1].Equals("arysmod", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (api)
        {
            return segments.Length == 4 && segments[3] == "releases" &&
                Regex.IsMatch(address.Query, "^\\?per_page=100(&page=[0-9]{1,2})?$");
        }
        if (address.Query.Length != 0)
        {
            return false;
        }
        if (address.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            return segments.Length >= 4;
        }
        return address.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
            (segments.Length >= 6 && segments[2] == "releases" && segments[3] == "download" ||
                segments.Length >= 6 && segments[2] == "releases" && segments[3] == "latest" && segments[4] == "download" ||
                segments.Length >= 5 && segments[2] == "raw");
    }
}
