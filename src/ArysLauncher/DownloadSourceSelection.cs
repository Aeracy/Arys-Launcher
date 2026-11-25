using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace ArysLauncher;

internal sealed class DownloadSourceSelection : IDisposable
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send;
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> choices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> unavailableUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource lifetime = new();

    public DownloadSourceSelection(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
    {
        this.send = send;
    }

    public async Task<IReadOnlyList<Uri>> OrderAsync(IReadOnlyList<Uri> sources, CancellationToken cancellationToken)
    {
        var hosts = sources.Select(source => source.Authority).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
        if (hosts.Length < 2)
        {
            return sources;
        }
        var key = string.Join('|', hosts);
        var choice = choices.GetOrAdd(key, _ => new Lazy<Task<string?>>(() => SelectAsync(sources),
            LazyThreadSafetyMode.ExecutionAndPublication));
        var selected = await choice.Value.WaitAsync(cancellationToken);
        var now = Environment.TickCount64;
        return sources.OrderBy(source => unavailableUntil.TryGetValue(source.Authority, out var until) && until > now ? 1 : 0)
            .ThenBy(source => source.Authority.Equals(selected, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToArray();
    }

    public void ReportFailure(Uri source)
    {
        unavailableUntil[source.Authority] = Environment.TickCount64 + 90000;
    }

    public void ReportSuccess(Uri source)
    {
        unavailableUntil.TryRemove(source.Authority, out _);
    }

    private async Task<string?> SelectAsync(IReadOnlyList<Uri> sources)
    {
        using var probes = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        probes.CancelAfter(TimeSpan.FromSeconds(sources.Any(GitHubDownloadRoutes.IsGitHubRoute) ? 6 : 3));
        async Task<string?> ProbeAsync(Uri address)
        {
            try
            {
                var sampleBody = GitHubDownloadRoutes.IsGitHubRoute(address);
                using var message = new HttpRequestMessage(sampleBody ? HttpMethod.Get : HttpMethod.Head, address)
                {
                    Version = HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
                };
                message.Headers.UserAgent.ParseAdd($"ArysLauncher/{App.LauncherVersion}");
                if (sampleBody)
                {
                    message.Headers.Range = new RangeHeaderValue(0, 65535);
                }
                using var response = await send(message, probes.Token);
                if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
                {
                    return null;
                }
                if (!sampleBody)
                {
                    return address.Authority;
                }
                if (response.Content.Headers.ContentType?.MediaType?.Equals("text/html", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return null;
                }
                if (response.StatusCode == HttpStatusCode.PartialContent &&
                    (response.Content.Headers.ContentRange is not { From: 0, To: >= 0 and <= 65535, Length: > 0 }))
                {
                    return null;
                }
                await using var input = await response.Content.ReadAsStreamAsync(probes.Token);
                var buffer = new byte[16384];
                var received = 0;
                while (received < 65536)
                {
                    var count = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, 65536 - received)), probes.Token);
                    if (count == 0)
                    {
                        break;
                    }
                    received += count;
                }
                return received > 0 ? address.Authority : null;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
            {
                return null;
            }
        }
        var tasks = sources.DistinctBy(source => source.Authority).Select(ProbeAsync).ToList();
        var allTasks = tasks.ToArray();
        try
        {
            while (tasks.Count > 0)
            {
                var completed = await Task.WhenAny(tasks);
                tasks.Remove(completed);
                if (await completed is { } host)
                {
                    return host;
                }
            }
            return null;
        }
        finally
        {
            probes.Cancel();
            await Task.WhenAll(allTasks);
        }
    }

    public void Dispose()
    {
        lifetime.Cancel();
        lifetime.Dispose();
    }
}
