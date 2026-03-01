using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using DataWarehouse.SDK.Contracts.Transit;

namespace DataWarehouse.Plugins.UltimateDataTransit.Strategies.Direct;

/// <summary>
/// gRPC streaming transfer strategy using raw HTTP/2 with gRPC binary framing.
/// Implements gRPC protocol framing (1-byte compressed flag + 4-byte length + message)
/// without requiring proto definitions, enabling binary data streaming over gRPC channels.
/// </summary>
/// <remarks>
/// This strategy uses <see cref="HttpClient"/> with gRPC content-type headers and manual
/// binary framing since we do not have proto definitions. Data is transferred as raw bytes
/// within gRPC wire format frames.
/// </remarks>
internal sealed class GrpcStreamingTransitStrategy : DataTransitStrategyBase
{
    private readonly HttpClient _httpClient;

    /// <inheritdoc/>
    public override string StrategyId => "transit-grpc";

    /// <inheritdoc/>
    public override string Name => "gRPC Streaming Transfer";

    /// <inheritdoc/>
    public override TransitCapabilities Capabilities => new()
    {
        SupportsResumable = false,
        SupportsStreaming = true,
        SupportsDelta = false,
        SupportsMultiPath = false,
        SupportsP2P = false,
        SupportsOffline = false,
        SupportsCompression = false,
        SupportsEncryption = true,
        MaxTransferSizeBytes = long.MaxValue,
        SupportedProtocols = ["grpc", "grpc-web"]
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="GrpcStreamingTransitStrategy"/> class.
    /// </summary>
    public GrpcStreamingTransitStrategy()
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
            // Probe the gRPC endpoint with an OPTIONS request
            using var request = new HttpRequestMessage(HttpMethod.Options, endpoint.Uri)
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };

            request.Headers.TryAddWithoutValidation("Content-Type", "application/grpc");

            if (!string.IsNullOrEmpty(endpoint.AuthToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.AuthToken);
            }

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            // gRPC endpoints typically return 200 or 415 (unsupported media type for non-gRPC requests)
            return response.IsSuccessStatusCode
                || response.StatusCode == HttpStatusCode.UnsupportedMediaType
                || response.StatusCode == HttpStatusCode.MethodNotAllowed;
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
                CurrentPhase = "Preparing gRPC frames",
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
                // Pull mode: GET from source endpoint
                using var getRequest = new HttpRequestMessage(HttpMethod.Get, request.Source.Uri)
                {
                    Version = HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
                };

                if (!string.IsNullOrEmpty(request.Source.AuthToken))
                {
                    getRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.Source.AuthToken);
                }

                // P2-2683: dispose the HttpResponseMessage when we are done reading its stream.
                using var getResponse = await _httpClient.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                getResponse.EnsureSuccessStatusCode();
                dataStream = await getResponse.Content.ReadAsStreamAsync(cts.Token);
            }

            // Build gRPC-framed payload: chunk the data into gRPC frames
            // gRPC frame: [1 byte compressed flag][4 bytes message length (big-endian)][message bytes]
            const int chunkSize = 65536; // 64KB chunks
            using var framedStream = new MemoryStream(65536);
            var readBuffer = new byte[chunkSize];
            int bytesRead;

            while ((bytesRead = await dataStream.ReadAsync(readBuffer.AsMemory(0, chunkSize), cts.Token)) > 0)
            {
                sha256.AppendData(readBuffer.AsSpan(0, bytesRead));

                // Write gRPC frame header
                framedStream.WriteByte(0); // compressed flag = 0 (no compression)

                // Write 4-byte big-endian message length
                var lengthBytes = new byte[4];
                lengthBytes[0] = (byte)(bytesRead >> 24);
                lengthBytes[1] = (byte)(bytesRead >> 16);
                lengthBytes[2] = (byte)(bytesRead >> 8);
                lengthBytes[3] = (byte)bytesRead;
                await framedStream.WriteAsync(lengthBytes.AsMemory(0, 4), cts.Token);

                // Write message bytes
                await framedStream.WriteAsync(readBuffer.AsMemory(0, bytesRead), cts.Token);

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
                    CurrentPhase = "Framing data"
                });
            }

            framedStream.Position = 0;

            // POST the gRPC-framed data to destination
            using var content = new StreamContent(framedStream);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
            content.Headers.ContentLength = framedStream.Length;

            using var postRequest = new HttpRequestMessage(HttpMethod.Post, request.Destination.Uri)
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = content
            };

            postRequest.Headers.TryAddWithoutValidation("TE", "trailers");

            if (!string.IsNullOrEmpty(request.Destination.AuthToken))
            {
                postRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.Destination.AuthToken);
            }

            foreach (var kvp in request.Metadata)
            {
                postRequest.Headers.TryAddWithoutValidation($"x-transit-{kvp.Key}", kvp.Value);
            }

            progress?.Report(new TransitProgress
            {
                TransferId = transferId,
                BytesTransferred = totalBytesTransferred,
                TotalBytes = request.SizeBytes,
                PercentComplete = 75.0,
                CurrentPhase = "Streaming to destination"
            });

            using var response = await _httpClient.SendAsync(postRequest, cts.Token);

            // Check gRPC status from trailers or response
            var grpcStatus = response.TrailingHeaders.TryGetValues("grpc-status", out var statusValues)
                ? statusValues.FirstOrDefault()
                : null;

            var success = response.IsSuccessStatusCode && (grpcStatus == null || grpcStatus == "0");

            stopwatch.Stop();

            var hash = sha256.GetHashAndReset();
            var contentHash = Convert.ToHexStringLower(hash);

            if (success)
            {
                RecordTransferSuccess(totalBytesTransferred);
            }
            else
            {
                RecordTransferFailure();
            }

            progress?.Report(new TransitProgress
            {
                TransferId = transferId,
                BytesTransferred = totalBytesTransferred,
                TotalBytes = request.SizeBytes,
                PercentComplete = 100.0,
                CurrentPhase = success ? "Completed" : "Failed"
            });

            return new TransitResult
            {
                TransferId = transferId,
                Success = success,
                BytesTransferred = totalBytesTransferred,
                Duration = stopwatch.Elapsed,
                ContentHash = contentHash,
                ErrorMessage = success ? null : $"gRPC status: {grpcStatus}",
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
