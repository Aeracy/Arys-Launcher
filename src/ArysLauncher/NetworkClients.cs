using System.Net;
using System.Net.Http;

namespace ArysLauncher;

public static class NetworkClients
{
    public static HttpClient Create(LauncherSettings settings, bool allowRedirect = true, bool download = false)
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = DownloadPolicy.HostConnections(settings),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(20),
            AllowAutoRedirect = allowRedirect,
            MaxAutomaticRedirections = 8,
            AutomaticDecompression = download ? DecompressionMethods.None :
                DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            EnableMultipleHttp2Connections = true
        };
        if (!string.IsNullOrWhiteSpace(settings.ProxyAddress))
        {
            handler.Proxy = new WebProxy(settings.ProxyAddress);
        }
        HttpMessageHandler pipeline = download && settings.DownloadLimitMiBPerSecond > 0 ?
            new DownloadBandwidthHandler(handler, settings.DownloadLimitMiBPerSecond) : handler;
        var client = new HttpClient(pipeline)
        {
            Timeout = TimeSpan.FromMinutes(15),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"ArysLauncher/{App.LauncherVersion} (Minecraft modpack launcher)");
        return client;
    }
}
