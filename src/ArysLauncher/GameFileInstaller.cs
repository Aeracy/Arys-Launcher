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
        downloader = new ContentDownloader(storage, client);
    }

    protected override async Task Download(GameFile file, IProgress<ByteProgress>? progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(file.Path) || string.IsNullOrEmpty(file.Url))
        {
            throw new InvalidDataException("游戏依赖缺少文件位置或下载地址。");
        }
        var source = new Uri(file.Url);
        if (source.Scheme != "https")
        {
            throw new InvalidDataException("游戏依赖必须使用 HTTPS 下载地址。");
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
                await base.Download(file with { Path = temporary }, progress, cancellationToken);
                if (file.Size > 0 && new FileInfo(temporary).Length != file.Size)
                {
                    throw new IOException("游戏依赖下载大小不匹配。");
                }
                if (!string.IsNullOrEmpty(file.Hash))
                {
                    await using var input = File.OpenRead(temporary);
                    var hash = Convert.ToHexString(await SHA1.HashDataAsync(input, cancellationToken));
                    if (!hash.Equals(file.Hash, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException("游戏依赖未通过 SHA-1 校验。");
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
        throw new IOException("游戏依赖下载失败，请检查网络或代理后重试。", lastException);
    }

    public void Dispose()
    {
        downloader.Dispose();
    }
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
