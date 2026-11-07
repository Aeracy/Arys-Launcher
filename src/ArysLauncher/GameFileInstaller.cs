using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using CmlLib.Core;
using CmlLib.Core.Files;
using CmlLib.Core.Installers;

namespace ArysLauncher;

public sealed class GameFileInstaller : ParallelGameInstaller, IDisposable
{
    private readonly ContentDownloader downloader;
    private readonly HttpClient client;
    private readonly int parallelDownloads;
    private readonly ConcurrentDictionary<string, VerifiedFile> verifiedFiles = new(StringComparer.OrdinalIgnoreCase);
    private long networkBytes;

    public long NetworkBytes
    {
        get
        {
            return Interlocked.Read(ref networkBytes);
        }
    }

    public GameFileInstaller(LauncherStorage storage, HttpClient client)
        : base(Math.Clamp(Environment.ProcessorCount / 2, 1, 2), DownloadPolicy.FileConcurrency(storage.Settings), 256, client)
    {
        CheckFileSize = true;
        this.client = client;
        downloader = new ContentDownloader(storage, client);
        parallelDownloads = DownloadPolicy.FileConcurrency(storage.Settings);
    }

    protected override async ValueTask Install(IEnumerable<GameFile> gameFiles, CancellationToken cancellationToken)
    {
        var files = gameFiles.Where(file => !string.IsNullOrEmpty(file.Path) && !string.IsNullOrEmpty(file.Url) &&
            !IsExcludedPath(file.Path)).DistinctBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(file => file.Size).ToArray();
        var totalBytes = files.Sum(file => Math.Max(0, file.Size));
        var reporter = new WorkProgress(new CallbackProgress<WorkSnapshot>(snapshot =>
        {
            FireByteProgress(new ByteProgress(snapshot.TotalBytes, snapshot.CompletedBytes));
            FireFileProgress(snapshot.TotalFiles, snapshot.CompletedFiles, "", InstallerEventType.Done);
        }), files.Length, totalBytes);
        reporter.Report(true);
        using var checks = new SemaphoreSlim(3);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await Parallel.ForEachAsync(files.Select((file, index) => (file, index)), new ParallelOptions
        {
            MaxDegreeOfParallelism = parallelDownloads,
            CancellationToken = cancellation.Token
        }, async (item, token) =>
        {
            try
            {
                await checks.WaitAsync(token);
                bool update;
                try
                {
                    update = await NeedsDownloadAsync(item.file, token);
                }
                finally
                {
                    checks.Release();
                }
                if (update)
                {
                    var bytes = new CallbackProgress<ByteProgress>(value => reporter.FileBytes(item.index,
                        item.file.Size > 0 ? Math.Clamp(value.ProgressedBytes, 0, item.file.Size) : 0));
                    await Download(item.file, bytes, token);
                }
                await item.file.ExecuteUpdateTask(token);
                var installed = new FileInfo(item.file.Path!);
                if (installed.Exists)
                {
                    verifiedFiles[item.file.Path!] = new VerifiedFile(item.file.Hash, item.file.Size, installed.Length, installed.LastWriteTimeUtc);
                }
                reporter.FileBytes(item.index, Math.Max(0, item.file.Size));
                reporter.FileDone(item.index);
            }
            catch
            {
                await cancellation.CancelAsync();
                throw;
            }
        });
        reporter.Report(true);
    }

    private async Task<bool> NeedsDownloadAsync(GameFile file, CancellationToken cancellationToken)
    {
        if (!File.Exists(file.Path))
        {
            return true;
        }
        var info = new FileInfo(file.Path);
        if (verifiedFiles.TryGetValue(file.Path!, out var verified) && verified.Hash == file.Hash && verified.ExpectedSize == file.Size &&
            verified.Length == info.Length && verified.LastWriteTime == info.LastWriteTimeUtc)
        {
            return false;
        }
        if (CheckFileSize && file.Size > 0 && new FileInfo(file.Path).Length != file.Size)
        {
            return true;
        }
        if (CheckFileChecksum && !string.IsNullOrEmpty(file.Hash))
        {
            await using var stream = new FileStream(file.Path!, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA1.HashDataAsync(stream, cancellationToken);
            return !Convert.ToHexString(hash).Equals(file.Hash, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    protected override async Task Download(GameFile file, IProgress<ByteProgress>? progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(file.Path) || string.IsNullOrEmpty(file.Url))
        {
            throw new InvalidDataException(LauncherText.Get("GameDependencyAddressMissing"));
        }
        var source = new Uri(file.Url);
        if (source.Scheme != "https")
        {
            throw new InvalidDataException(LauncherText.Get("GameDependencyRequiresHttps"));
        }
        if (file.Size > 0 && file.Hash?.Length == 40 && file.Hash.All(Uri.IsHexDigit))
        {
            long reportedBytes = 0;
            var reporter = new WorkProgress(new CallbackProgress<WorkSnapshot>(snapshot =>
            {
                var previousBytes = Interlocked.Exchange(ref reportedBytes, snapshot.NetworkBytes);
                Interlocked.Add(ref networkBytes, snapshot.NetworkBytes - previousBytes);
                progress?.Report(new ByteProgress(snapshot.TotalBytes, snapshot.CompletedBytes));
            }), 1, file.Size);
            var cached = await downloader.FetchAsync(new ContentRequest(file.Size, file.Hash, HashAlgorithmName.SHA1, new[] { source }),
                0, reporter, cancellationToken);
            reporter.Report(true);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);
            File.Copy(cached, file.Path + ".ready", true);
            File.Move(file.Path + ".ready", file.Path, true);
            progress?.Report(new ByteProgress(file.Size, file.Size));
            return;
        }
        Exception? lastException = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var temporary = file.Path + ".download";
            try
            {
                await DownloadBootstrapAsync(source, temporary, file.Size, progress, attempt > 0, cancellationToken);
                if (file.Size > 0 && new FileInfo(temporary).Length != file.Size)
                {
                    throw new IOException(LauncherText.Get("GameDependencySizeMismatch"));
                }
                if (!string.IsNullOrEmpty(file.Hash))
                {
                    await using var input = File.OpenRead(temporary);
                    var hash = Convert.ToHexString(await SHA1.HashDataAsync(input, cancellationToken));
                    if (!hash.Equals(file.Hash, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException(LauncherText.Get("GameDependencyHashMismatch"));
                    }
                }
                File.Move(temporary, file.Path, true);
                return;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lastException = exception;
                if (attempt < 2)
                {
                    await Task.Delay(700 * (attempt + 1), cancellationToken);
                }
            }
        }
        throw new IOException(LauncherText.Get("GameDependencyDownloadFailed"), lastException);
    }

    private async Task DownloadBootstrapAsync(Uri source, string path, long expectedSize, IProgress<ByteProgress>? progress,
        bool retrySource, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, source);
        message.Options.Set(new HttpRequestOptionsKey<bool>("Arys.RetrySource"), retrySource);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        timeout.CancelAfter(Timeout.InfiniteTimeSpan);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException(LauncherText.Get("DownloadRedirectInsecure"));
        }
        var size = response.Content.Headers.ContentLength ?? expectedSize;
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            long written = 0;
            while (true)
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var count = await input.ReadAsync(buffer.AsMemory(), timeout.Token);
                timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                if (count == 0)
                {
                    break;
                }
                written += count;
                if (written > 16L * 1024 * 1024 * 1024 || expectedSize > 0 && written > expectedSize)
                {
                    throw new IOException(LauncherText.Get("GameDependencySizeMismatch"));
                }
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                Interlocked.Add(ref networkBytes, count);
                progress?.Report(new ByteProgress(size, written));
            }
            await output.FlushAsync(cancellationToken);
            if (size > 0 && written != size)
            {
                throw new IOException(LauncherText.Get("GameDependencySizeMismatch"));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose()
    {
        downloader.Dispose();
    }

    private sealed record VerifiedFile(string? Hash, long ExpectedSize, long Length, DateTime LastWriteTime);
}

public sealed class CallbackProgress<T> : IProgress<T>
{
    private readonly Action<T> callback;

    public CallbackProgress(Action<T> callback)
    {
        this.callback = callback;
    }

    public void Report(T value)
    {
        callback(value);
    }
}
