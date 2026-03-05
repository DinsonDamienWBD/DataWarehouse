using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Quic;
using System.Security.Cryptography;
using DataWarehouse.SDK.Contracts.Transit;

namespace DataWarehouse.Plugins.UltimateDataTransit.Strategies.Direct;

/// <summary>
/// HTTP/3 direct transfer strategy using <see cref="HttpClient"/> with QUIC-based transport.
/// Leverages HTTP/3 for improved performance on high-latency or lossy networks via
/// <see cref="HttpVersion.Version30"/> with <see cref="HttpVersionPolicy.RequestVersionExact"/>.
/// </summary>
internal sealed class Http3TransitStrategy : DataTransitStrategyBase
{
    private readonly HttpClient _httpClient;

    /// <inheritdoc/>
    public override string StrategyId => "transit-http3";

    /// <inheritdoc/>
    public override string Name => "HTTP/3 Direct Transfer (QUIC)";

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
        SupportedProtocols = ["http3", "quic"]
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="Http3TransitStrategy"/> class.
    /// Configures the HttpClient for HTTP/3 (QUIC) transport.
    /// </summary>
    public Http3TransitStrategy()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(30)
        };

        _httpClient = new HttpClient(handler)
        {
            DefaultRequestVersion = HttpVersion.Version30,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    /// <inheritdoc/>
    public override async Task<bool> IsAvailableAsync(TransitEndpoint endpoint, CancellationToken ct = default)
    {
        // HTTP/3 requires QUIC support on the platform
        if (!QuicConnection.IsSupported)
        {
            return false;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, endpoint.Uri)
            {
                Version = HttpVersion.Version30,
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

        try
        {
            progress?.Report(new TransitProgress
            {
                TransferId = transferId,
                CurrentPhase = "Connecting via QUIC",
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
                // Pull from source
                using var getRequest = new HttpRequestMessage(HttpMethod.Get, request.Source.Uri)
                {
                    Version = HttpVersion.Version30,
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
                };

                if (!string.IsNullOrEmpty(request.Source.AuthToken))
                {
                    getRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.Source.AuthToken);
                }

                // P2-2683: dispose the HttpResponseMessage when done reading its content stream.
                using var getResponse = await _httpClient.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                getResponse.EnsureSuccessStatusCode();
                dataStream = await getResponse.Content.ReadAsStreamAsync(cts.Token);
            }

            // Stream data through hashing buffer and POST to destination
            using var buffer = new MemoryStream(65536);
            var readBuffer = new byte[81920];
            int bytesRead;

            progress?.Report(new TransitProgress
            {
                TransferId = transferId,
                CurrentPhase = "Transferring",
                PercentComplete = 0
            });

            while ((bytesRead = await dataStream.ReadAsync(readBuffer.AsMemory(0, readBuffer.Length), cts.Token)) > 0)
            {
                sha256.AppendData(readBuffer.AsSpan(0, bytesRead));
                await buffer.WriteAsync(readBuffer.AsMemory(0, bytesRead), cts.Token);
                totalBytesTransferred += bytesRead;

                var pct = request.SizeBytes > 0
                    ? (double)totalBytesTransferred / request.SizeBytes * 50.0
                    : 0;

                progress?.Report(new TransitProgress
                {
                    TransferId = transferId,
                    BytesTransferred = totalBytesTransferred,
                    TotalBytes = request.SizeBytes,
                    PercentComplete = pct,
                    CurrentPhase = "Reading source"
                });
            }

            buffer.Position = 0;

            using var content = new StreamContent(buffer);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentLength = totalBytesTransferred;

            using var postRequest = new HttpRequestMessage(HttpMethod.Post, request.Destination.Uri)
            {
                Version = HttpVersion.Version30,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = content
            };

            if (!string.IsNullOrEmpty(request.Destination.AuthToken))
            {
                postRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.Destination.AuthToken);
            }

            foreach (var kvp in request.Metadata)
            {
                postRequest.Headers.TryAddWithoutValidation($"X-Transit-{kvp.Key}", kvp.Value);
            }

            progress?.Report(new TransitProgress
            {
                TransferId = transferId,
                BytesTransferred = totalBytesTransferred,
                TotalBytes = request.SizeBytes,
                PercentComplete = 75.0,
                CurrentPhase = "Uploading to destination"
            });

            using var response = await _httpClient.SendAsync(postRequest, cts.Token);
            response.EnsureSuccessStatusCode();

            stopwatch.Stop();

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
}
