using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;

namespace ArysLauncher;

internal sealed class DownloadSourceSelection : IDisposable
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send;
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> choices = new(StringComparer.OrdinalIgnoreCase);
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
        return sources.OrderBy(source => source.Authority.Equals(selected, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToArray();
    }

    private async Task<string?> SelectAsync(IReadOnlyList<Uri> sources)
    {
        using var probes = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        probes.CancelAfter(TimeSpan.FromSeconds(3));
        async Task<string?> ProbeAsync(Uri address)
        {
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Head, address)
                {
                    Version = HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
                };
                message.Headers.UserAgent.ParseAdd($"ArysLauncher/{App.LauncherVersion}");
                using var response = await send(message, probes.Token);
                return response.IsSuccessStatusCode ? address.Authority : null;
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
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
