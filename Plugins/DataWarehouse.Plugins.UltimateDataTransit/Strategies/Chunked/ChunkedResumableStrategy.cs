using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using DataWarehouse.SDK.Contracts.Transit;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataTransit.Strategies.Chunked;

/// <summary>
/// Chunked resumable transfer strategy that splits large files into configurable chunks
/// with SHA-256 integrity verification per chunk and manifest-based resume capability.
/// </summary>
/// <remarks>
/// <para>
/// Files are split into fixed-size chunks (default 4MB). Each chunk is hashed independently
/// with SHA-256 before transfer. A <see cref="ChunkManifest"/> tracks every chunk's offset,
/// size, hash, and completion status. If a transfer is interrupted, calling
/// <see cref="ResumeTransferAsync"/> picks up from the last completed chunk without
/// re-transferring data that already arrived at the destination.
/// </para>
/// <para>
/// Individual chunk transfers are retried up to 3 times with exponential backoff (1s, 2s, 4s)
/// before failing the entire transfer. Thread safety is ensured via
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> for manifest storage and
/// <see cref="Interlocked"/> for byte counters.
/// </para>
/// </remarks>
internal sealed class ChunkedResumableStrategy : DataTransitStrategyBase
{
    /// <summary>
    /// Default chunk size in bytes (4 MB).
    /// </summary>
    private const int DefaultChunkSizeBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Maximum number of retry attempts per chunk before failing the transfer.
    /// </summary>
    private const int MaxRetryAttempts = 3;

    /// <summary>
    /// Thread-safe storage of active and completed chunk manifests keyed by transfer ID.
    /// </summary>
    private readonly BoundedDictionary<string, ChunkManifest> _manifests = new BoundedDictionary<string, ChunkManifest>(1000);

    /// <summary>
    /// HTTP client used for chunk upload and manifest submission.
    /// </summary>
    private readonly HttpClient _httpClient;

    /// <inheritdoc/>
    public override string StrategyId => "transit-chunked-resumable";

    /// <inheritdoc/>
    public override string Name => "Chunked Resumable Transfer";

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
        SupportedProtocols = ["http", "https", "http2", "http3"]
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="ChunkedResumableStrategy"/> class.
    /// Configures the internal <see cref="HttpClient"/> with HTTP/2 support, connection pooling,
    /// and infinite timeout for large transfers.
    /// </summary>
    public ChunkedResumableStrategy()
    {
        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(30)
        };

        _httpClient = new HttpClient(handler)
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
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
    /// <summary>
    /// Transfers data by splitting the source stream into fixed-size chunks, computing a
    /// SHA-256 hash for each chunk, uploading each chunk individually with Content-Range headers,
    /// and finally posting the completed manifest for server-side reassembly.
    /// </summary>
    public override async Task<TransitResult> TransferAsync(
        TransitRequest request,
        IProgress<TransitProgress>? progress = null,
        CancellationToken ct = default)
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
            var totalSize = DetermineSize(request);
            var chunkSize = DetermineChunkSize(request);
            var manifest = CreateManifest(transferId, totalSize, chunkSize);
            manifest.Request = request; // Store for resume capability
            _manifests[transferId] = manifest;

            progress?.Report(new TransitProgress
            {
                TransferId = transferId,
                CurrentPhase = "Preparing chunks",
                PercentComplete = 0,
                TotalBytes = totalSize
            });

            using var fullHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            totalBytesTransferred = await TransferChunksAsync(
                manifest, request, fullHash, progress, cts.Token);

            // Post manifest to destination for server-side assembly
            await PostManifestAsync(manifest, request.Destination, cts.Token);

            stopwatch.Stop();

            var contentHash = Convert.ToHexStringLower(fullHash.GetHashAndReset());

            RecordTransferSuccess(totalBytesTransferred);

            progress?.Report(new TransitProgress
            {
                TransferId = transferId,
                BytesTransferred = totalBytesTransferred,
                TotalBytes = totalSize,
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
                StrategyUsed = StrategyId,
                Metadata = new Dictionary<string, string>
                {
                    ["chunkSize"] = chunkSize.ToString(),
                    ["totalChunks"] = manifest.Chunks.Count.ToString(),
                    ["completedChunks"] = manifest.Chunks.Count(c => c.Completed).ToString()
                }
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

    /// <inheritdoc/>
    /// <summary>
    /// Resumes an interrupted transfer by reading the stored <see cref="ChunkManifest"/>,
    /// finding the first incomplete chunk, and continuing from that offset.
    /// Completed chunks are skipped entirely without re-transfer or re-hashing.
    /// </summary>
    public override async Task<TransitResult> ResumeTransferAsync(
        string transferId,
        IProgress<TransitProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transferId);

        if (!_manifests.TryGetValue(transferId, out var manifest))
        {
            throw new ArgumentException($"No manifest found for transfer '{transferId}'. Cannot resume.", nameof(transferId));
        }

        if (manifest.Request is null)
        {
            throw new InvalidOperationException($"Transfer '{transferId}' has no stored request context. Cannot resume.");
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ActiveTransferCancellations[transferId] = cts;
        IncrementActiveTransfers();

        var stopwatch = Stopwatch.StartNew();
        long totalBytesTransferred = 0;

        try
        {
            // Count already completed bytes
            long previouslyTransferred = manifest.Chunks
                .Where(c => c.Completed)
                .Sum(c => (long)c.Size);

            var completedCount = manifest.Chunks.Count(c => c.Completed);
            var totalChunks = manifest.Chunks.Count;

            progress?.Report(new TransitProgress
            {
                TransferId = transferId,
                BytesTransferred = previouslyTransferred,
                TotalBytes = manifest.TotalSize,
                PercentComplete = totalChunks > 0 ? (double)completedCount / totalChunks * 100.0 : 0,
                CurrentPhase = $"Resuming from chunk {completedCount + 1}/{totalChunks}"
            });

            using var fullHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            totalBytesTransferred = await TransferChunksAsync(
                manifest, manifest.Request, fullHash, progress, cts.Token);

            // Post manifest to destination for server-side assembly
            await PostManifestAsync(manifest, manifest.Request.Destination, cts.Token);

            stopwatch.Stop();

            var contentHash = Convert.ToHexStringLower(fullHash.GetHashAndReset());

            RecordTransferSuccess(totalBytesTransferred);

            progress?.Report(new TransitProgress
            {
                TransferId = transferId,
                BytesTransferred = previouslyTransferred + totalBytesTransferred,
                TotalBytes = manifest.TotalSize,
                PercentComplete = 100.0,
                CurrentPhase = "Resumed transfer completed"
            });

            return new TransitResult
            {
                TransferId = transferId,
                Success = true,
                BytesTransferred = previouslyTransferred + totalBytesTransferred,
                Duration = stopwatch.Elapsed,
                ContentHash = contentHash,
                StrategyUsed = StrategyId,
                Metadata = new Dictionary<string, string>
                {
                    ["resumed"] = "true",
                    ["skippedChunks"] = completedCount.ToString(),
                    ["transferredChunks"] = (totalChunks - completedCount).ToString()
                }
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

    /// <summary>
    /// Transfers all incomplete chunks from the manifest. Completed chunks are skipped.
    /// Each chunk is read from the source stream, hashed with SHA-256, and uploaded
    /// with Content-Range headers. Failed chunks are retried up to <see cref="MaxRetryAttempts"/>
    /// times with exponential backoff.
    /// </summary>
    /// <param name="manifest">The chunk manifest tracking completion state.</param>
    /// <param name="request">The original transfer request with source stream and destination.</param>
    /// <param name="fullHash">Incremental hash accumulator for the complete content hash.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The total number of bytes actually transferred (excluding skipped chunks).</returns>
    private async Task<long> TransferChunksAsync(
        ChunkManifest manifest,
        TransitRequest request,
        IncrementalHash fullHash,
        IProgress<TransitProgress>? progress,
        CancellationToken ct)
    {
        long bytesTransferred = 0;
        var totalChunks = manifest.Chunks.Count;
        var completedSoFar = manifest.Chunks.Count(c => c.Completed);

        for (var i = 0; i < totalChunks; i++)
        {
            ct.ThrowIfCancellationRequested();

            var chunk = manifest.Chunks[i];

            // Skip completed chunks entirely
            if (chunk.Completed)
            {
                continue;
            }

            var chunkBytes = await ReadChunkFromStreamAsync(request.DataStream, chunk.Offset, chunk.Size, ct);

            // Compute SHA-256 hash for this chunk
            var chunkHash = Convert.ToHexStringLower(SHA256.HashData(chunkBytes));
            chunk.Sha256Hash = chunkHash;

            // Append to full content hash
            fullHash.AppendData(chunkBytes);

            // Upload chunk with retry logic
            await UploadChunkWithRetryAsync(
                manifest.TransferId,
                chunk,
                chunkBytes,
                request.Destination,
                manifest.TotalSize,
                ct);

            // Mark chunk as completed
            chunk.Completed = true;
            Interlocked.Add(ref bytesTransferred, chunkBytes.Length);

            completedSoFar++;
            var pct = totalChunks > 0 ? (double)completedSoFar / totalChunks * 100.0 : 0;

            progress?.Report(new TransitProgress
            {
                TransferId = manifest.TransferId,
                BytesTransferred = Interlocked.Read(ref bytesTransferred),
                TotalBytes = manifest.TotalSize,
                PercentComplete = pct,
                CurrentPhase = $"Transferring chunk {completedSoFar}/{totalChunks}"
            });
        }

        return Interlocked.Read(ref bytesTransferred);
    }

    /// <summary>
    /// Reads a chunk of data from the source stream at the specified offset and size.
    /// Seeks to the correct position if the stream supports seeking.
    /// </summary>
    /// <param name="stream">The source data stream.</param>
    /// <param name="offset">The byte offset to read from.</param>
    /// <param name="size">The number of bytes to read.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A byte array containing the chunk data. May be shorter than <paramref name="size"/> for the last chunk.</returns>
    private static async Task<byte[]> ReadChunkFromStreamAsync(
        Stream? stream,
        long offset,
        int size,
        CancellationToken ct)
    {
        if (stream is null)
        {
            throw new InvalidOperationException("No data stream available for reading chunk data.");
        }

        if (stream.CanSeek)
        {
            stream.Position = offset;
        }

        var buffer = new byte[size];
        var totalRead = 0;

        while (totalRead < size)
        {
            var bytesRead = await stream.ReadAsync(
                buffer.AsMemory(totalRead, size - totalRead), ct);

            if (bytesRead == 0)
            {
                break; // End of stream (last chunk may be smaller)
            }

            totalRead += bytesRead;
        }

        if (totalRead < size)
        {
            // Trim the buffer to actual bytes read (last chunk)
            var trimmed = new byte[totalRead];
            Array.Copy(buffer, trimmed, totalRead);
            return trimmed;
        }

        return buffer;
    }

    /// <summary>
    /// Uploads a single chunk to the destination with retry logic.
    /// Retries up to <see cref="MaxRetryAttempts"/> times with exponential backoff
    /// (1s, 2s, 4s) before propagating the failure.
    /// </summary>
    /// <param name="transferId">The transfer identifier.</param>
    /// <param name="chunk">The chunk metadata.</param>
    /// <param name="chunkData">The raw chunk bytes to upload.</param>
    /// <param name="destination">The destination endpoint.</param>
    /// <param name="totalSize">The total file size for Content-Range headers.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task UploadChunkWithRetryAsync(
        string transferId,
        ChunkInfo chunk,
        byte[] chunkData,
        TransitEndpoint destination,
        long totalSize,
        CancellationToken ct)
    {
        Exception? lastException = null;

        for (var attempt = 0; attempt <= MaxRetryAttempts; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    // Exponential backoff: 1s, 2s, 4s
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                    await Task.Delay(delay, ct);
                }

                await UploadChunkAsync(transferId, chunk, chunkData, destination, totalSize, ct);
                return; // Success
            }
            catch (OperationCanceledException)
            {
                throw; // Do not retry on cancellation
            }
            catch (Exception ex)
            {
                lastException = ex;
            }
        }

        throw new InvalidOperationException(
            $"Chunk {chunk.Index} failed after {MaxRetryAttempts + 1} attempts. " +
            $"Offset={chunk.Offset}, Size={chunk.Size}",
            lastException);
    }

    /// <summary>
    /// Uploads a single chunk to the destination endpoint using HTTP POST with Content-Range header.
    /// The chunk is sent to <c>{destination.Uri}/chunks/{transferId}/{chunkIndex}</c>.
    /// </summary>
    /// <param name="transferId">The transfer identifier.</param>
    /// <param name="chunk">The chunk metadata including index and offset.</param>
    /// <param name="chunkData">The raw chunk bytes to upload.</param>
    /// <param name="destination">The destination endpoint.</param>
    /// <param name="totalSize">The total file size for the Content-Range header.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task UploadChunkAsync(
        string transferId,
        ChunkInfo chunk,
        byte[] chunkData,
        TransitEndpoint destination,
        long totalSize,
        CancellationToken ct)
    {
        var chunkUri = new Uri(destination.Uri, $"chunks/{transferId}/{chunk.Index}");

        using var content = new ByteArrayContent(chunkData);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = chunkData.Length;

        // Content-Range: bytes {start}-{end}/{total}
        var rangeEnd = chunk.Offset + chunkData.Length - 1;
        content.Headers.Add("Content-Range", $"bytes {chunk.Offset}-{rangeEnd}/{totalSize}");

        using var request = new HttpRequestMessage(HttpMethod.Post, chunkUri)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Content = content
        };

        // Add chunk SHA-256 hash header for server-side verification
        if (!string.IsNullOrEmpty(chunk.Sha256Hash))
        {
            request.Headers.TryAddWithoutValidation("X-Chunk-SHA256", chunk.Sha256Hash);
        }

        if (!string.IsNullOrEmpty(destination.AuthToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", destination.AuthToken);
        }

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Posts the completed chunk manifest to the destination for server-side reassembly.
    /// Sent as JSON to <c>{destination.Uri}/manifest/{transferId}</c>.
    /// </summary>
    /// <param name="manifest">The completed chunk manifest.</param>
    /// <param name="destination">The destination endpoint.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task PostManifestAsync(
        ChunkManifest manifest,
        TransitEndpoint destination,
        CancellationToken ct)
    {
        var manifestUri = new Uri(destination.Uri, $"manifest/{manifest.TransferId}");

        var manifestPayload = System.Text.Json.JsonSerializer.Serialize(new
        {
            manifest.TransferId,
            manifest.TotalSize,
            manifest.ChunkSizeBytes,
            Chunks = manifest.Chunks.Select(c => new
            {
                c.Index,
                c.Offset,
                c.Size,
                c.Sha256Hash,
                c.Completed
            })
        });

        using var content = new StringContent(
            manifestPayload,
            System.Text.Encoding.UTF8,
            "application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, manifestUri)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Content = content
        };

        if (!string.IsNullOrEmpty(destination.AuthToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", destination.AuthToken);
        }

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Determines the total data size from the request, using <see cref="TransitRequest.SizeBytes"/>
    /// if available, otherwise falling back to the stream length.
    /// </summary>
    /// <param name="request">The transfer request.</param>
    /// <returns>The total data size in bytes.</returns>
    private static long DetermineSize(TransitRequest request)
    {
        if (request.SizeBytes > 0)
        {
            return request.SizeBytes;
        }

        if (request.DataStream is { CanSeek: true })
        {
            return request.DataStream.Length;
        }

        throw new InvalidOperationException(
            "Cannot determine total size: SizeBytes not set and stream is not seekable. " +
            "Chunked resumable transfers require a known total size.");
    }

    // Cat 14 (finding 2686): cap chunk size to avoid massive allocations (e.g. int.MaxValue ~2 GB).
    private const int MaxChunkSizeBytes = 256 * 1024 * 1024; // 256 MiB upper bound

    /// <summary>
    /// Determines the chunk size from request metadata or falls back to <see cref="DefaultChunkSizeBytes"/>.
    /// Callers can specify a custom chunk size via the <c>chunkSizeBytes</c> metadata key.
    /// </summary>
    /// <param name="request">The transfer request with optional metadata.</param>
    /// <returns>The chunk size in bytes, clamped to [1, 256 MiB].</returns>
    private static int DetermineChunkSize(TransitRequest request)
    {
        if (request.Metadata.TryGetValue("chunkSizeBytes", out var chunkSizeStr) &&
            int.TryParse(chunkSizeStr, out var customChunkSize) &&
            customChunkSize > 0)
        {
            // Clamp to [1 byte, 256 MiB] to prevent runaway allocation.
            return Math.Min(customChunkSize, MaxChunkSizeBytes);
        }

        return DefaultChunkSizeBytes;
    }

    /// <summary>
    /// Creates a new <see cref="ChunkManifest"/> with chunk entries computed from the total size
    /// and desired chunk size. The last chunk may be smaller than the others.
    /// </summary>
    /// <param name="transferId">The unique transfer identifier.</param>
    /// <param name="totalSize">The total data size in bytes.</param>
    /// <param name="chunkSize">The desired chunk size in bytes.</param>
    /// <returns>A new manifest with all chunks initialized to incomplete.</returns>
    private static ChunkManifest CreateManifest(string transferId, long totalSize, int chunkSize)
    {
        var chunks = new List<ChunkInfo>();
        long offset = 0;
        var index = 0;

        while (offset < totalSize)
        {
            var remaining = totalSize - offset;
            var currentChunkSize = (int)Math.Min(remaining, chunkSize);

            chunks.Add(new ChunkInfo
            {
                Index = index,
                Offset = offset,
                Size = currentChunkSize,
                Sha256Hash = null,
                Completed = false
            });

            offset += currentChunkSize;
            index++;
        }

        return new ChunkManifest
        {
            TransferId = transferId,
            TotalSize = totalSize,
            ChunkSizeBytes = chunkSize,
            Chunks = chunks
        };
    }

    /// <summary>
    /// Represents the complete chunk manifest for a transfer operation.
    /// Tracks every chunk's identity, hash, and completion status to enable resumable transfers.
    /// </summary>
    internal sealed class ChunkManifest
    {
        /// <summary>
        /// The unique transfer identifier this manifest belongs to.
        /// </summary>
        public required string TransferId { get; init; }

        /// <summary>
        /// Total size of the data being transferred in bytes.
        /// </summary>
        public required long TotalSize { get; init; }

        /// <summary>
        /// The configured chunk size in bytes (default 4MB).
        /// </summary>
        public required int ChunkSizeBytes { get; init; }

        /// <summary>
        /// The list of chunks comprising this transfer.
        /// </summary>
        public required List<ChunkInfo> Chunks { get; init; }

        /// <summary>
        /// The original transfer request, stored for resume operations.
        /// Null if the manifest was created without storing the request.
        /// </summary>
        public TransitRequest? Request { get; set; }
    }

    /// <summary>
    /// Describes a single chunk within a chunked transfer, including its position,
    /// integrity hash, and completion status.
    /// </summary>
    internal sealed class ChunkInfo
    {
        /// <summary>
        /// Zero-based index of this chunk in the manifest.
        /// </summary>
        public required int Index { get; init; }

        /// <summary>
        /// Byte offset of this chunk within the source data.
        /// </summary>
        public required long Offset { get; init; }

        /// <summary>
        /// Size of this chunk in bytes. The last chunk may be smaller than the configured chunk size.
        /// </summary>
        public required int Size { get; init; }

        /// <summary>
        /// SHA-256 hash of the chunk data for integrity verification.
        /// Set after the chunk is read and hashed; null before hashing.
        /// </summary>
        public string? Sha256Hash { get; set; }

        /// <summary>
        /// Whether this chunk has been successfully transferred to the destination.
        /// </summary>
        public bool Completed { get; set; }
    }
}
