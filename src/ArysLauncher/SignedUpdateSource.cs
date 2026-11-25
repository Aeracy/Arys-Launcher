using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ArysLauncher;

internal sealed class SignedUpdateSource
{
    private static readonly object LogGate = new();
    private readonly LauncherStorage storage;
    private readonly UpdateChannel channel;
    private readonly Dictionary<UpdateKind, TaggedRelease> taggedReleases = new();
    private bool releaseCatalogLoaded;

    public SignedUpdateSource(LauncherStorage storage, UpdateChannel channel)
    {
        this.storage = storage;
        this.channel = channel;
    }

    public async Task<UpdateManifest?> ReadAsync(UpdateKind kind, CancellationToken cancellationToken)
    {
        var configured = channel.ManifestAddress(kind);
        if (configured == null)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(channel.PublicKey))
        {
            throw new InvalidDataException(LauncherText.Get("UpdatePublicKeyMissing"));
        }
        using var client = NetworkClients.Create(storage.Settings, publicContent: true);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(100));
        if (!configured.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            return await ReadPairAsync(client, configured, kind, null, deadline.Token);
        }
        var name = kind == UpdateKind.Launcher ? "launcher" : "pack";
        var feeds = new[]
        {
            new Uri($"https://raw.githubusercontent.com/{channel.LauncherRepository}/main/updates/{name}.json"),
            new Uri($"https://github.com/{channel.LauncherRepository}/raw/refs/heads/main/updates/{name}.json")
        };
        foreach (var address in feeds)
        {
            try
            {
                return await ReadRoutesAsync(address, kind, async (route, token) =>
                {
                    var bytes = await DownloadMetadataAsync(client, route, 6 * 1024 * 1024, token);
                    var feed = JsonSerializer.Deserialize<SignedFeed>(bytes, LauncherStorage.JsonOptions);
                    if (feed == null || feed.SchemaVersion != 1 || feed.Manifest is not { Length: > 0 and <= 5600000 } ||
                        feed.Signature is not { Length: > 0 and <= 128 })
                    {
                        throw new InvalidDataException(LauncherText.Get("UpdateManifestEmpty"));
                    }
                    var release = ReadManifest(Convert.FromBase64String(feed.Manifest), feed.Signature, kind, null);
                    Record(kind, route.Host, "signed-feed " + release.Version);
                    return release;
                }, deadline.Token, preferNewest: true);
            }
            catch (Exception exception) when (CanFallback(exception, deadline.Token))
            {
                RecordFailure(kind, address.Host, exception);
            }
        }
        if (kind == UpdateKind.Launcher)
        {
            var latest = new Uri($"https://github.com/{channel.LauncherRepository}/releases/latest/download/launcher-update.json");
            try
            {
                return await ReadPairAsync(client, latest, kind, null, deadline.Token);
            }
            catch (Exception exception) when (CanFallback(exception, deadline.Token))
            {
                RecordFailure(kind, "latest-release", exception);
            }
        }
        var selected = await ResolveTaggedReleaseAsync(client, kind, deadline.Token);
        return await ReadPairAsync(client, selected.Address, kind, selected.Version, deadline.Token);
    }

    public void RecordFailure(UpdateKind kind, string stage, Exception exception)
    {
        var status = exception is HttpRequestException request ? request.StatusCode?.ToString() ?? "transport" : exception.GetType().Name;
        var message = exception is HttpRequestException or OperationCanceledException ? status :
            exception.Message.Replace('\r', ' ').Replace('\n', ' ');
        Record(kind, stage, status + " " + message[..Math.Min(message.Length, 1000)]);
    }

    private void Record(UpdateKind kind, string stage, string message)
    {
        try
        {
            lock (LogGate)
            {
                var path = Path.Combine(storage.Logs, "updates.log");
                if (File.Exists(path) && new FileInfo(path).Length > 128 * 1024)
                {
                    File.Move(path, path + ".previous", true);
                }
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {kind} {stage} {message}{Environment.NewLine}");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private async Task<UpdateManifest> ReadPairAsync(HttpClient client, Uri address, UpdateKind kind, Version? expectedVersion,
        CancellationToken cancellationToken)
    {
        return await ReadRoutesAsync(address, kind, async (route, token) =>
        {
            var bytes = await DownloadMetadataAsync(client, route, 4 * 1024 * 1024, token);
            var signature = await DownloadMetadataAsync(client, UpdatePaths.HttpsAddress(route.AbsoluteUri + ".sig"), 1024, token);
            var release = ReadManifest(bytes, Encoding.UTF8.GetString(signature).Trim(), kind, expectedVersion);
            Record(kind, route.Host, "signed-manifest " + release.Version);
            return release;
        }, cancellationToken, preferNewest: expectedVersion == null);
    }

    private async Task<T> ReadRoutesAsync<T>(Uri address, UpdateKind kind, Func<Uri, CancellationToken, Task<T>> read,
        CancellationToken cancellationToken, bool preferNewest = false) where T : class
    {
        var routes = GitHubDownloadRoutes.Expand(new[] { address }, storage.Settings.AutomaticGitHubAcceleration);
        using var requests = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        async Task<(T? Value, Exception? Failure)> AttemptAsync(Uri route)
        {
            try
            {
                return (await read(route, requests.Token), null);
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                if (!requests.IsCancellationRequested)
                {
                    RecordFailure(kind, route.Host, exception);
                }
                return (null, exception);
            }
        }
        var pending = routes.Select(AttemptAsync).ToList();
        var allRequests = pending.ToArray();
        Exception? failure = null;
        T? selected = null;
        Task? freshnessWindow = null;
        try
        {
            while (pending.Count > 0)
            {
                var completedRequest = freshnessWindow == null ? await Task.WhenAny(pending) :
                    await Task.WhenAny(pending.Cast<Task>().Append(freshnessWindow));
                cancellationToken.ThrowIfCancellationRequested();
                if (completedRequest == freshnessWindow)
                {
                    return selected!;
                }
                var completed = (Task<(T? Value, Exception? Failure)>)completedRequest;
                pending.Remove(completed);
                var result = await completed;
                cancellationToken.ThrowIfCancellationRequested();
                if (result.Value != null)
                {
                    if (!preferNewest || result.Value is not UpdateManifest candidate)
                    {
                        return result.Value;
                    }
                    if (selected is not UpdateManifest current || Version.Parse(candidate.Version) > Version.Parse(current.Version))
                    {
                        selected = result.Value;
                    }
                    freshnessWindow ??= Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken);
                }
                failure = result.Failure;
            }
            if (selected != null)
            {
                return selected;
            }
            throw failure ?? new IOException(LauncherText.Get("UpdateManifestEmpty"));
        }
        finally
        {
            requests.Cancel();
            await Task.WhenAll(allRequests);
        }
    }

    private UpdateManifest ReadManifest(byte[] bytes, string signature, UpdateKind kind, Version? expectedVersion)
    {
        using var signer = ECDsa.Create();
        try
        {
            signer.ImportSubjectPublicKeyInfo(Convert.FromBase64String(channel.PublicKey), out _);
            if (!signer.VerifyData(bytes, Convert.FromBase64String(signature), HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                throw new CryptographicException();
            }
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            throw new InvalidDataException(LauncherText.Get("UpdateSignatureInvalid"), exception);
        }
        var release = JsonSerializer.Deserialize<UpdateManifest>(bytes, LauncherStorage.JsonOptions) ??
            throw new InvalidDataException(LauncherText.Get("UpdateManifestEmpty"));
        release.Validate(kind, requireCompatibleLauncher: false);
        if (expectedVersion != null && Version.Parse(release.Version) != expectedVersion)
        {
            throw new InvalidDataException(LauncherText.Get("UpdateTagVersionMismatch"));
        }
        return release;
    }

    private async Task<TaggedRelease> ResolveTaggedReleaseAsync(HttpClient client, UpdateKind kind, CancellationToken cancellationToken)
    {
        if (!releaseCatalogLoaded)
        {
            var candidates = new Dictionary<UpdateKind, TaggedRelease>();
            for (var page = 1; page <= 10; page++)
            {
                var url = new Uri($"https://api.github.com/repos/{channel.LauncherRepository}/releases?per_page=100&page={page}");
                var bytes = await ReadRoutesAsync(url, kind, async (route, token) =>
                {
                    var response = await DownloadMetadataAsync(client, route, 4 * 1024 * 1024, token);
                    using var catalog = JsonDocument.Parse(response);
                    if (catalog.RootElement.ValueKind != JsonValueKind.Array)
                    {
                        throw new InvalidDataException(LauncherText.Get("UpdateReleaseCatalogInvalid"));
                    }
                    return response;
                }, cancellationToken);
                using var document = JsonDocument.Parse(bytes);
                var releases = document.RootElement;
                if (releases.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException(LauncherText.Get("UpdateReleaseCatalogInvalid"));
                }
                foreach (var release in releases.EnumerateArray())
                {
                    if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
                    {
                        continue;
                    }
                    var tag = release.GetProperty("tag_name").GetString() ?? "";
                    foreach (var candidateKind in Enum.GetValues<UpdateKind>())
                    {
                        var prefix = candidateKind == UpdateKind.Launcher ? channel.LauncherTagPrefix : channel.PackTagPrefix;
                        if (string.IsNullOrWhiteSpace(prefix) || !tag.StartsWith(prefix, StringComparison.Ordinal) ||
                            !Version.TryParse(tag[prefix.Length..], out var version) ||
                            candidates.TryGetValue(candidateKind, out var current) && current.Version >= version)
                        {
                            continue;
                        }
                        var manifestName = candidateKind == UpdateKind.Launcher ? "launcher-update.json" : "pack-update.json";
                        var assets = release.GetProperty("assets").EnumerateArray().ToArray();
                        var manifest = assets.FirstOrDefault(asset => asset.GetProperty("name").GetString() == manifestName);
                        if (manifest.ValueKind == JsonValueKind.Undefined ||
                            !assets.Any(asset => asset.GetProperty("name").GetString() == manifestName + ".sig"))
                        {
                            continue;
                        }
                        var address = manifest.GetProperty("browser_download_url").GetString() ?? "";
                        if (address != $"https://github.com/{channel.LauncherRepository}/releases/download/{tag}/{manifestName}")
                        {
                            throw new InvalidDataException(LauncherText.Get("UpdateAddressRequiresHttps"));
                        }
                        candidates[candidateKind] = new TaggedRelease(version, UpdatePaths.HttpsAddress(address));
                    }
                }
                if (releases.GetArrayLength() < 100)
                {
                    break;
                }
            }
            foreach (var (candidateKind, selected) in candidates)
            {
                taggedReleases[candidateKind] = selected;
            }
            releaseCatalogLoaded = true;
        }
        if (!taggedReleases.TryGetValue(kind, out var result))
        {
            throw new InvalidDataException(LauncherText.Get("UpdateChannelReleaseMissing"));
        }
        return result;
    }

    private static bool CanFallback(Exception exception, CancellationToken cancellationToken)
    {
        return !cancellationToken.IsCancellationRequested && IsRecoverable(exception);
    }

    private static bool IsRecoverable(Exception exception)
    {
        return exception is HttpRequestException or IOException or InvalidDataException or JsonException or FormatException or OperationCanceledException;
    }

    private static async Task<byte[]> DownloadMetadataAsync(HttpClient client, Uri address, int maximumBytes, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps || response.Content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidDataException(LauncherText.Get("UpdateResponseOutOfRange"));
        }
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int count;
        while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0)
        {
            if (output.Length + count > maximumBytes)
            {
                throw new InvalidDataException(LauncherText.Get("UpdateResponseTooLarge"));
            }
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private sealed record TaggedRelease(Version Version, Uri Address);

    private sealed class SignedFeed
    {
        public SignedFeed()
        {
        }

        public int SchemaVersion { get; set; }
        public string Manifest { get; set; } = "";
        public string Signature { get; set; } = "";
    }
}
