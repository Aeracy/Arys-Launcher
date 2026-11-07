using System.Net.Http;

namespace ArysLauncher;

internal sealed class GameDownloadHandler : DelegatingHandler
{
    private readonly DownloadSourceSelection selection;

    public GameDownloadHandler(HttpMessageHandler inner) : base(inner)
    {
        selection = new DownloadSourceSelection((request, token) => base.SendAsync(request, token));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get || request.RequestUri == null || request.Headers.Authorization != null ||
            MirrorAddress(request.RequestUri) is not { } mirror)
        {
            return await base.SendAsync(request, cancellationToken);
        }
        var original = request.RequestUri;
        var sources = (await selection.OrderAsync(new[] { original, mirror }, cancellationToken)).ToArray();
        if (request.Options.TryGetValue(new HttpRequestOptionsKey<bool>("Arys.RetrySource"), out var retry) && retry)
        {
            Array.Reverse(sources);
        }
        Exception? failure = null;
        foreach (var source in sources)
        {
            using var message = CopyRequest(request, source, HttpMethod.Get);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var response = await base.SendAsync(message, timeout.Token);
                timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                if (response.IsSuccessStatusCode || source == sources[^1])
                {
                    return response;
                }
                response.Dispose();
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                failure = exception;
            }
        }
        throw new HttpRequestException(LauncherText.Get("GameDownloadSourcesUnavailable"), failure);
    }

    private static HttpRequestMessage CopyRequest(HttpRequestMessage source, Uri address, HttpMethod method)
    {
        var message = new HttpRequestMessage(method, address)
        {
            Version = source.Version,
            VersionPolicy = source.VersionPolicy
        };
        foreach (var header in source.Headers)
        {
            message.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return message;
    }

    private static Uri? MirrorAddress(Uri source)
    {
        if (source.Scheme != Uri.UriSchemeHttps || source.UserInfo.Length != 0 || source.Query.Length != 0)
        {
            return null;
        }
        var prefix = source.Host.ToLowerInvariant() switch
        {
            "resources.download.minecraft.net" => "/assets",
            "libraries.minecraft.net" or "maven.minecraftforge.net" => "/maven",
            "piston-meta.mojang.com" or "piston-data.mojang.com" or "launchermeta.mojang.com" or "launcher.mojang.com" => "",
            _ => null
        };
        return prefix == null ? null : new Uri("https://bmclapi2.bangbang93.com" + prefix + source.AbsolutePath);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            selection.Dispose();
        }
        base.Dispose(disposing);
    }
}
