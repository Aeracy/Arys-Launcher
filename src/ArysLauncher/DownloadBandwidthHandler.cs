using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;

namespace ArysLauncher;

internal sealed class DownloadBandwidthHandler : DelegatingHandler
{
    private readonly DownloadBandwidth bandwidth;

    public DownloadBandwidthHandler(HttpMessageHandler innerHandler, double limitMiBPerSecond) : base(innerHandler)
    {
        bandwidth = new DownloadBandwidth(limitMiBPerSecond * 1024 * 1024);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.Content = new LimitedContent(response.Content, bandwidth);
        return response;
    }

    private sealed class LimitedContent : HttpContent
    {
        private readonly HttpContent inner;
        private readonly DownloadBandwidth bandwidth;

        public LimitedContent(HttpContent inner, DownloadBandwidth bandwidth)
        {
            this.inner = inner;
            this.bandwidth = bandwidth;
            foreach (var header in inner.Headers)
            {
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = inner.Headers.ContentLength ?? 0;
            return inner.Headers.ContentLength.HasValue;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            return CreateContentReadStreamAsync(CancellationToken.None);
        }

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            return new LimitedReadStream(await inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), bandwidth);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return SerializeToStreamAsync(stream, context, CancellationToken.None);
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            var input = await ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await input.CopyToAsync(stream, 16384, cancellationToken).ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class DownloadBandwidth
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        private readonly double bytesPerSecond;
        private readonly double capacity;
        private double availableBytes;
        private long sampledAt;

        public DownloadBandwidth(double bytesPerSecond)
        {
            this.bytesPerSecond = bytesPerSecond;
            capacity = Math.Clamp(bytesPerSecond * 0.125, 16384, 16 * 1024 * 1024);
            availableBytes = capacity;
            sampledAt = Stopwatch.GetTimestamp();
        }

        public async ValueTask WaitAsync(int count, CancellationToken cancellationToken)
        {
            if (count == 0)
            {
                return;
            }
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var now = Stopwatch.GetTimestamp();
                    availableBytes = Math.Min(capacity, availableBytes +
                        (now - sampledAt) / (double)Stopwatch.Frequency * bytesPerSecond);
                    sampledAt = now;
                    if (availableBytes >= count)
                    {
                        availableBytes -= count;
                        return;
                    }
                    var delaySeconds = Math.Max(0.001, (count - availableBytes) / bytesPerSecond);
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private sealed class LimitedReadStream : Stream
    {
        private readonly Stream inner;
        private readonly DownloadBandwidth bandwidth;

        public LimitedReadStream(Stream inner, DownloadBandwidth bandwidth)
        {
            this.inner = inner;
            this.bandwidth = bandwidth;
        }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get
            {
                throw new NotSupportedException();
            }
            set
            {
                throw new NotSupportedException();
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            var count = inner.Read(buffer[..Math.Min(buffer.Length, 16384)]);
            bandwidth.WaitAsync(count, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, 16384)], cancellationToken).ConfigureAwait(false);
            await bandwidth.WaitAsync(count, cancellationToken).ConfigureAwait(false);
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }
}
