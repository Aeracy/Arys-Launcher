using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace ArysLauncher;

public sealed record ContentRequest(long Size, string Hash, HashAlgorithmName Algorithm, IReadOnlyList<Uri> Sources);

public sealed class ContentDownloader : IDisposable
{
    private readonly HttpClient client;
    private readonly string cacheRoot;
    private readonly int perHostLimit;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> hostGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> contentGates = new();
    private readonly ConcurrentDictionary<string, byte> verified = new();

    public ContentDownloader(LauncherStorage storage, HttpClient client)
    {
        this.client = client;
        cacheRoot = storage.Cache;
        perHostLimit = DownloadPolicy.HostConnections(storage.Settings);
    }

    public async Task<string> FetchAsync(ContentRequest request, int progressIndex, WorkProgress progress, CancellationToken cancellationToken)
    {
        Validate(request);
        var key = request.Algorithm.Name + "-" + request.Hash.ToLowerInvariant();
        var destination = Path.Combine(cacheRoot, "objects", request.Algorithm.Name!, request.Hash[..2], request.Hash.ToLowerInvariant());
        var gate = contentGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(destination) && new FileInfo(destination).Length == request.Size &&
                (verified.ContainsKey(key) || await CheckHashAsync(destination, request, cancellationToken)))
            {
                verified.TryAdd(key, 0);
                progress.FileBytes(progressIndex, request.Size);
                progress.CacheHit();
                return destination;
            }
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temporary = destination + ".part";
            Exception? lastFailure = null;
            foreach (var source in request.Sources)
            {
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var hostGate = hostGates.GetOrAdd(source.Authority, _ => new SemaphoreSlim(perHostLimit, perHostLimit));
                        await hostGate.WaitAsync(cancellationToken);
                        try
                        {
                            await DownloadAsync(source, temporary, request, progressIndex, progress, cancellationToken);
                        }
                        finally
                        {
                            hostGate.Release();
                        }
                        File.Move(temporary, destination, true);
                        verified.TryAdd(key, 0);
                        return destination;
                    }
                    catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        lastFailure = exception;
                        if (exception is HttpRequestException httpException && httpException.StatusCode is
                            HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                        {
                            break;
                        }
                        if (attempt < 2)
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(700 * (attempt + 1)), cancellationToken);
                        }
                    }
                }
            }
            throw new IOException(LauncherText.Get("DownloadIncomplete"), lastFailure);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task DownloadAsync(Uri source, string destination, ContentRequest request, int index,
        WorkProgress progress, CancellationToken cancellationToken)
    {
        var offset = File.Exists(destination) ? new FileInfo(destination).Length : 0;
        if (offset > request.Size)
        {
            File.Delete(destination);
            offset = 0;
        }
        if (offset == request.Size && File.Exists(destination) && await CheckHashAsync(destination, request, cancellationToken))
        {
            progress.FileBytes(index, request.Size);
            return;
        }
        if (offset == request.Size && File.Exists(destination))
        {
            File.Delete(destination);
            offset = 0;
        }
        using var message = new HttpRequestMessage(HttpMethod.Get, source);
        if (offset > 0)
        {
            message.Headers.Range = new RangeHeaderValue(offset, null);
        }
        using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headerTimeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token);
        headerTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
        if (response.RequestMessage?.RequestUri?.Scheme != "https")
        {
            throw new InvalidDataException(LauncherText.Get("DownloadRedirectInsecure"));
        }
        if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
        {
            var delay = response.Headers.RetryAfter?.Delta ??
                (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(2);
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 1, 30)), cancellationToken);
            throw new HttpRequestException(LauncherText.Get("DownloadRateLimited"), null, response.StatusCode);
        }
        response.EnsureSuccessStatusCode();
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            if (range?.From != offset || range.Length != request.Size || range.To != request.Size - 1)
            {
                File.Delete(destination);
                throw new IOException(LauncherText.Get("DownloadRangeMismatch"));
            }
        }
        else
        {
            offset = 0;
        }
        if (response.Content.Headers.ContentLength is long responseLength && responseLength != request.Size - offset)
        {
            throw new IOException(LauncherText.Get("DownloadSizeMismatch"));
        }
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            using var hash = IncrementalHash.CreateHash(request.Algorithm);
            await using var output = new FileStream(destination, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (offset == 0)
            {
                output.SetLength(0);
            }
            else
            {
                var remaining = offset;
                while (remaining > 0)
                {
                    var count = await output.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                    if (count == 0)
                    {
                        throw new EndOfStreamException(LauncherText.Get("DownloadPartialChanged"));
                    }
                    hash.AppendData(buffer, 0, count);
                    remaining -= count;
                }
            }
            output.Position = offset;
            progress.FileBytes(index, offset);
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var written = offset;
            while (true)
            {
                idleTimeout.CancelAfter(TimeSpan.FromSeconds(45));
                var count = await input.ReadAsync(buffer.AsMemory(), idleTimeout.Token);
                idleTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
                if (count == 0)
                {
                    break;
                }
                if (written + count > request.Size)
                {
                    throw new IOException(LauncherText.Get("DownloadTooLarge"));
                }
                hash.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                written += count;
                progress.NetworkBytes(count);
                progress.FileBytes(index, written);
            }
            await output.FlushAsync(cancellationToken);
            if (written != request.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(request.Hash, StringComparison.OrdinalIgnoreCase))
            {
                output.Close();
                File.Delete(destination);
                progress.FileBytes(index, 0);
                throw new IOException(LauncherText.Get("DownloadHashMismatch"));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<bool> CheckHashAsync(string path, ContentRequest request, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = request.Algorithm.Name switch
        {
            "SHA512" => await SHA512.HashDataAsync(stream, cancellationToken),
            "SHA256" => await SHA256.HashDataAsync(stream, cancellationToken),
            "SHA1" => await SHA1.HashDataAsync(stream, cancellationToken),
            _ => throw new InvalidDataException(LauncherText.Get("DownloadAlgorithmUnsupported"))
        };
        return Convert.ToHexString(hash).Equals(request.Hash, StringComparison.OrdinalIgnoreCase);
    }

    private static void Validate(ContentRequest request)
    {
        var expectedLength = request.Algorithm.Name switch { "SHA512" => 128, "SHA256" => 64, "SHA1" => 40, _ => 0 };
        if (expectedLength == 0 || request.Hash.Length != expectedLength || request.Hash.Any(character => !Uri.IsHexDigit(character)) ||
            request.Size < 0 || request.Size > 16L * 1024 * 1024 * 1024 || request.Sources.Count == 0 ||
            request.Sources.Any(uri => uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo)))
        {
            throw new InvalidDataException(LauncherText.Get("DownloadRecordInvalid"));
        }
    }

    public void Dispose()
    {
        foreach (var gate in hostGates.Values.Concat(contentGates.Values))
        {
            gate.Dispose();
        }
    }
}
