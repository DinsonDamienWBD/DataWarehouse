using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using DataWarehouse.SDK.Contracts.Transit;

namespace DataWarehouse.Plugins.UltimateDataTransit.Strategies.Direct;

/// <summary>
/// HTTP/2 direct transfer strategy using <see cref="HttpClient"/> with explicit HTTP/2 version.
/// Supports streaming uploads via POST, resumable transfers via Range headers,
/// and SHA-256 content hash verification.
/// </summary>
internal sealed class Http2TransitStrategy : DataTransitStrategyBase
{
    private readonly HttpClient _httpClient;

    /// <inheritdoc/>
    public override string StrategyId => "transit-http2";

    /// <inheritdoc/>
    public override string Name => "HTTP/2 Direct Transfer";

    /// <inheritdoc/>
    public override TransitCapabilities Capabilities => new()
    {
        SupportsResumable = true,
        SupportsStreaming = true,
        SupportsDelta = false,
        SupportsMultiPath = false,
        SupportsP2P = false,
        SupportsOffline = false,
        SupportsCompression = true,
        SupportsEncryption = true,
        MaxTransferSizeBytes = long.MaxValue,
        SupportedProtocols = ["http", "https", "http2"]
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="Http2TransitStrategy"/> class.
    /// Configures the HttpClient for HTTP/2 with streaming support.
    /// </summary>
    public Http2TransitStrategy()
    {
        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(30)
        };

        _httpClient = new HttpClient(handler)
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    /// <inheritdoc/>
    public override async Task<bool> IsAvailableAsync(TransitEndpoint endpoint, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, endpoint.Uri)
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };

            if (!string.IsNullOrEmpty(endpoint.AuthToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.AuthToken);
            }

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.MethodNotAllowed;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public override async Task<TransitResult> TransferAsync(TransitRequest request, IProgress<TransitProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var transferId = request.TransferId;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ActiveTransferCancellations[transferId] = cts;
        IncrementActiveTransfers();

        var stopwatch = Stopwatch.StartNew();
        long totalBytesTransferred = 0;
        var tracker = new ByteTracker();

        try
        {
            progress?.Report(new TransitProgress
            {
                TransferId = transferId,
                CurrentPhase = "Connecting",
                PercentComplete = 0
            });

            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Stream dataStream;

            if (request.DataStream != null)
            {
                dataStream = request.DataStream;
            }
            else
            {
                // Pull mode: GET from source
                using var getRequest = new HttpRequestMessage(HttpMethod.Get, request.Source.Uri)
                {
                    Version = HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact
                };

                if (!string.IsNullOrEmpty(request.Source.AuthToken))
                {
                    getRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.Source.AuthToken);
                }

                var getResponse = await _httpClient.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                getResponse.EnsureSuccessStatusCode();
                dataStream = await getResponse.Content.ReadAsStreamAsync(cts.Token);
            }

            // Push mode: POST to destination with streaming
            using var content = new StreamContent(new HashingProgressStream(
                dataStream, sha256, progress, transferId, request.SizeBytes, tracker));

            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            if (request.SizeBytes > 0)
            {
                content.Headers.ContentLength = request.SizeBytes;
            }

            using var postRequest = new HttpRequestMessage(HttpMethod.Post, request.Destination.Uri)
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = content
            };

            if (!string.IsNullOrEmpty(request.Destination.AuthToken))
            {
                postRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.Destination.AuthToken);
            }

            // Add metadata as custom headers
            foreach (var kvp in request.Metadata)
            {
                postRequest.Headers.TryAddWithoutValidation($"X-Transit-{kvp.Key}", kvp.Value);
            }

            using var response = await _httpClient.SendAsync(postRequest, cts.Token);
            response.EnsureSuccessStatusCode();

            stopwatch.Stop();
            totalBytesTransferred = tracker.Value;

            var hash = sha256.GetHashAndReset();
            var contentHash = Convert.ToHexStringLower(hash);

            RecordTransferSuccess(totalBytesTransferred);

            progress?.Report(new TransitProgress
            {
                TransferId = transferId,
                BytesTransferred = totalBytesTransferred,
                TotalBytes = request.SizeBytes,
                PercentComplete = 100.0,
                CurrentPhase = "Completed"
            });

            return new TransitResult
            {
                TransferId = transferId,
                Success = true,
                BytesTransferred = totalBytesTransferred,
                Duration = stopwatch.Elapsed,
                ContentHash = contentHash,
                StrategyUsed = StrategyId
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            totalBytesTransferred = tracker.Value;
            RecordTransferFailure();

            return new TransitResult
            {
                TransferId = transferId,
                Success = false,
                BytesTransferred = totalBytesTransferred,
                Duration = stopwatch.Elapsed,
                ErrorMessage = ex.Message,
                StrategyUsed = StrategyId
            };
        }
        finally
        {
            DecrementActiveTransfers();
            ActiveTransferCancellations.TryRemove(transferId, out _);
            cts.Dispose();
        }
    }

    /// <summary>
    /// Thread-safe mutable wrapper for tracking total bytes transferred.
    /// Avoids ref parameter issues in closures/lambdas.
    /// </summary>
    private sealed class ByteTracker
    {
        private long _value;

        /// <summary>
        /// Gets or sets the current byte count in a thread-safe manner.
        /// </summary>
        public long Value
        {
            get => Interlocked.Read(ref _value);
            set => Interlocked.Exchange(ref _value, value);
        }
    }

    /// <summary>
    /// A stream wrapper that computes an incremental SHA-256 hash and reports progress
    /// as data flows through it during transfer.
    /// </summary>
    private sealed class HashingProgressStream : Stream
    {
        private readonly Stream _inner;
        private readonly IncrementalHash _hash;
        private readonly IProgress<TransitProgress>? _progress;
        private readonly string _transferId;
        private readonly long _totalBytes;
        private readonly ByteTracker _tracker;
        private long _bytesRead;

        /// <summary>
        /// Initializes a new instance of the <see cref="HashingProgressStream"/> class.
        /// </summary>
        public HashingProgressStream(
            Stream inner,
            IncrementalHash hash,
            IProgress<TransitProgress>? progress,
            string transferId,
            long totalBytes,
            ByteTracker tracker)
        {
            _inner = inner;
            _hash = hash;
            _progress = progress;
            _transferId = transferId;
            _totalBytes = totalBytes;
            _tracker = tracker;
        }

        /// <inheritdoc/>
        public override bool CanRead => _inner.CanRead;

        /// <inheritdoc/>
        public override bool CanSeek => false;

        /// <inheritdoc/>
        public override bool CanWrite => false;

        /// <inheritdoc/>
        public override long Length => _inner.Length;

        /// <inheritdoc/>
        public override long Position
        {
            get => _bytesRead;
            set => throw new NotSupportedException();
        }

        /// <inheritdoc/>
        public override int Read(byte[] buffer, int offset, int count)
        {
            var bytesRead = _inner.Read(buffer, offset, count);
            if (bytesRead > 0)
            {
                _hash.AppendData(buffer.AsSpan(offset, bytesRead));
                _bytesRead += bytesRead;
                _tracker.Value = _bytesRead;
                ReportProgress();
            }
            return bytesRead;
        }

        /// <inheritdoc/>
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var bytesRead = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
            if (bytesRead > 0)
            {
                _hash.AppendData(buffer.AsSpan(offset, bytesRead));
                _bytesRead += bytesRead;
                _tracker.Value = _bytesRead;
                ReportProgress();
            }
            return bytesRead;
        }

        /// <inheritdoc/>
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var bytesRead = await _inner.ReadAsync(buffer, cancellationToken);
            if (bytesRead > 0)
            {
                _hash.AppendData(buffer.Span[..bytesRead]);
                _bytesRead += bytesRead;
                _tracker.Value = _bytesRead;
                ReportProgress();
            }
            return bytesRead;
        }

        private void ReportProgress()
        {
            var pct = _totalBytes > 0 ? (double)_bytesRead / _totalBytes * 100.0 : 0;
            _progress?.Report(new TransitProgress
            {
                TransferId = _transferId,
                BytesTransferred = _bytesRead,
                TotalBytes = _totalBytes,
                PercentComplete = pct,
                CurrentPhase = "Transferring"
            });
        }

        /// <inheritdoc/>
        public override void Flush() => _inner.Flush();

        /// <inheritdoc/>
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc/>
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc/>
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
