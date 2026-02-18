using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using DataWarehouse.SDK.Contracts.Transit;

namespace DataWarehouse.Plugins.UltimateDataTransit.Strategies.Chunked;

/// <summary>
/// Delta differential transfer strategy that uses an rsync-style rolling hash algorithm
/// to identify changed blocks between source and destination, transferring only the differences.
/// </summary>
/// <remarks>
/// <para>
/// The strategy operates in three phases:
/// <list type="number">
///   <item><description><b>Signature phase:</b> Request block signatures (Adler-32 weak hash + SHA-256 strong hash)
///   from the destination for the existing version of the file.</description></item>
///   <item><description><b>Matching phase:</b> Scan the source data with a rolling Adler-32 window. When a weak hash
///   matches a destination block, verify with SHA-256 strong hash. Unchanged blocks are recorded as
///   MATCH instructions; new/changed data is recorded as LITERAL instructions.</description></item>
///   <item><description><b>Transfer phase:</b> Send only the delta instruction set (MATCH references + LITERAL data)
///   to the destination for reconstruction.</description></item>
/// </list>
/// </para>
/// <para>
/// The <see cref="RollingHashComputer"/> is stateless and thread-safe. It provides O(1) per-byte
/// rolling hash updates via the Adler-32 algorithm and SHA-256 for strong hash collision resolution.
/// </para>
/// </remarks>
internal sealed class DeltaDifferentialStrategy : DataTransitStrategyBase
{
    /// <summary>
    /// Default block size in bytes for delta comparison.
    /// </summary>
    private const int DefaultBlockSize = 4096;

    /// <summary>
    /// HTTP client used for signature retrieval and delta transfer.
    /// </summary>
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Stateless rolling hash computer shared across all transfers.
    /// </summary>
    private readonly RollingHashComputer _hashComputer = new();

    /// <inheritdoc/>
    public override string StrategyId => "transit-delta-differential";

    /// <inheritdoc/>
    public override string Name => "Delta Differential Transfer";

    /// <inheritdoc/>
    public override TransitCapabilities Capabilities => new()
    {
        SupportsResumable = false,
        SupportsStreaming = true,
        SupportsDelta = true,
        SupportsMultiPath = false,
        SupportsP2P = false,
        SupportsOffline = false,
        SupportsCompression = false,
        SupportsEncryption = false,
        MaxTransferSizeBytes = long.MaxValue,
        SupportedProtocols = ["http", "https", "http2", "rsync"]
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="DeltaDifferentialStrategy"/> class.
    /// Configures the internal <see cref="HttpClient"/> with HTTP/2 support and infinite timeout
    /// for large delta comparison operations.
    /// </summary>
    public DeltaDifferentialStrategy()
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
    /// <summary>
    /// Checks whether the destination supports the delta protocol by sending a HEAD request
    /// to the signatures endpoint. Returns true if the server responds with 200 OK or 501
    /// (Not Implemented, indicating the endpoint exists but delta is not configured).
    /// </summary>
    public override async Task<bool> IsAvailableAsync(TransitEndpoint endpoint, CancellationToken ct = default)
    {
        try
        {
            var sigUri = new Uri(endpoint.Uri, "signatures");

            using var request = new HttpRequestMessage(HttpMethod.Head, sigUri)
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };

            if (!string.IsNullOrEmpty(endpoint.AuthToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.AuthToken);
            }

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotImplemented;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    /// <summary>
    /// Executes a delta differential transfer by computing block-level differences between
    /// the source data and the destination's existing copy, then transmitting only the
    /// changed blocks (literal data) and match references.
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
        long bytesTransferred = 0;

        try
        {
            var blockSize = DetermineBlockSize(request);

            progress?.Report(new TransitProgress
            {
                TransferId = transferId,
                CurrentPhase = "Requesting destination signatures",
                PercentComplete = 0
            });

            // Phase 1: Get destination block signatures
            var destSignatures = await GetDestinationSignaturesAsync(
                request.Destination, request.Metadata.GetValueOrDefault("path", ""), blockSize, cts.Token);

            if (destSignatures is null || destSignatures.Count == 0)
            {
                // No existing file at destination; fall back to full transfer
                bytesTransferred = await FullTransferFallbackAsync(
                    transferId, request, progress, cts.Token);

                stopwatch.Stop();
                RecordTransferSuccess(bytesTransferred);

                return new TransitResult
                {
                    TransferId = transferId,
                    Success = true,
                    BytesTransferred = bytesTransferred,
                    Duration = stopwatch.Elapsed,
                    StrategyUsed = StrategyId,
                    Metadata = new Dictionary<string, string>
                    {
                        ["deltaMode"] = "full",
                        ["deltaSavingsPercent"] = "0"
                    }
                };
            }

            progress?.Report(new TransitProgress
            {
                TransferId = transferId,
                CurrentPhase = "Computing block differences",
                PercentComplete = 10
            });

            // Phase 2: Scan source and compute delta instructions
            var sourceData = await ReadSourceDataAsync(request, cts.Token);
            var totalSourceSize = sourceData.Length;

            var instructions = ComputeDeltaInstructions(sourceData, destSignatures, blockSize);

            // Calculate transfer savings
            long literalBytes = 0;
            foreach (var instr in instructions)
            {
                if (instr.Type == DeltaInstructionType.Literal)
                {
                    literalBytes += instr.Length;
                }
            }

            var savingsPercent = totalSourceSize > 0
                ? (1.0 - (double)literalBytes / totalSourceSize) * 100.0
                : 0.0;

            progress?.Report(new TransitProgress
            {
                TransferId = transferId,
                CurrentPhase = $"Transferring delta ({savingsPercent:F1}% savings)",
                PercentComplete = 50,
                TotalBytes = totalSourceSize
            });

            // Phase 3: Send delta instruction set to destination
            bytesTransferred = await SendDeltaAsync(
                transferId, instructions, sourceData, request.Destination, cts.Token);

            stopwatch.Stop();
            RecordTransferSuccess(bytesTransferred);

            // Compute content hash of the full source for verification
            var contentHash = Convert.ToHexStringLower(SHA256.HashData(sourceData));

            progress?.Report(new TransitProgress
            {
                TransferId = transferId,
                BytesTransferred = bytesTransferred,
                TotalBytes = totalSourceSize,
                PercentComplete = 100.0,
                CurrentPhase = "Completed"
            });

            return new TransitResult
            {
                TransferId = transferId,
                Success = true,
                BytesTransferred = bytesTransferred,
                Duration = stopwatch.Elapsed,
                ContentHash = contentHash,
                StrategyUsed = StrategyId,
                Metadata = new Dictionary<string, string>
                {
                    ["deltaMode"] = "incremental",
                    ["deltaSavingsPercent"] = savingsPercent.ToString("F2"),
                    ["totalSourceBytes"] = totalSourceSize.ToString(),
                    ["literalBytes"] = literalBytes.ToString(),
                    ["matchedBlocks"] = instructions.Count(i => i.Type == DeltaInstructionType.Match).ToString(),
                    ["literalInstructions"] = instructions.Count(i => i.Type == DeltaInstructionType.Literal).ToString()
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
                BytesTransferred = bytesTransferred,
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
    /// Requests block signatures from the destination endpoint for the specified file path.
    /// Sends a GET request to <c>{destination.Uri}/signatures/{path}?blockSize={blockSize}</c>.
    /// Returns null if the destination does not have the file (404 response).
    /// </summary>
    /// <param name="destination">The destination endpoint.</param>
    /// <param name="path">The file path on the destination.</param>
    /// <param name="blockSize">The block size for signature computation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of block signatures, or null if the file does not exist at the destination.</returns>
    private async Task<List<BlockSignature>?> GetDestinationSignaturesAsync(
        TransitEndpoint destination,
        string path,
        int blockSize,
        CancellationToken ct)
    {
        var encodedPath = Uri.EscapeDataString(path);
        var sigUri = new Uri(destination.Uri, $"signatures/{encodedPath}?blockSize={blockSize}");

        using var request = new HttpRequestMessage(HttpMethod.Get, sigUri)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };

        if (!string.IsNullOrEmpty(destination.AuthToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", destination.AuthToken);
        }

        using var response = await _httpClient.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null; // File doesn't exist at destination
        }

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var signatures = System.Text.Json.JsonSerializer.Deserialize<List<BlockSignatureDto>>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (signatures is null)
        {
            return null;
        }

        var result = new List<BlockSignature>(signatures.Count);
        foreach (var dto in signatures)
        {
            result.Add(new BlockSignature(
                dto.Index,
                dto.Offset,
                dto.Size,
                dto.WeakHash,
                Convert.FromHexString(dto.StrongHash)));
        }

        return result;
    }

    /// <summary>
    /// Computes delta instructions by scanning the source data against destination block signatures
    /// using an rsync-style rolling hash algorithm.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Builds a lookup dictionary from destination weak hashes to block signatures. Scans the source
    /// data byte-by-byte using a rolling Adler-32 window. When a weak hash matches, verifies with
    /// SHA-256 strong hash. Matched blocks become MATCH instructions; unmatched regions become
    /// LITERAL instructions carrying the raw bytes to transfer.
    /// </para>
    /// </remarks>
    /// <param name="sourceData">The complete source data as a byte array.</param>
    /// <param name="destSignatures">The destination block signatures to match against.</param>
    /// <param name="blockSize">The block size used for comparison.</param>
    /// <returns>An ordered list of delta instructions (MATCH and LITERAL).</returns>
    private List<DeltaInstruction> ComputeDeltaInstructions(
        byte[] sourceData,
        List<BlockSignature> destSignatures,
        int blockSize)
    {
        // Build weak hash -> signatures lookup
        var weakHashLookup = new Dictionary<uint, List<BlockSignature>>();
        foreach (var sig in destSignatures)
        {
            if (!weakHashLookup.TryGetValue(sig.WeakHash, out var list))
            {
                list = new List<BlockSignature>();
                weakHashLookup[sig.WeakHash] = list;
            }
            list.Add(sig);
        }

        var instructions = new List<DeltaInstruction>();
        var sourceLength = sourceData.Length;
        var literalStart = 0;
        var pos = 0;

        while (pos <= sourceLength - blockSize)
        {
            var windowSpan = sourceData.AsSpan(pos, blockSize);
            var weakHash = _hashComputer.ComputeAdler32(windowSpan);

            if (weakHashLookup.TryGetValue(weakHash, out var candidates))
            {
                // Weak hash match found; verify with strong hash
                var strongHash = _hashComputer.ComputeStrongHash(windowSpan);
                BlockSignature? matchedBlock = null;

                foreach (var candidate in candidates)
                {
                    if (candidate.StrongHash.AsSpan().SequenceEqual(strongHash))
                    {
                        matchedBlock = candidate;
                        break;
                    }
                }

                if (matchedBlock is not null)
                {
                    // Emit any accumulated literal data before this match
                    if (pos > literalStart)
                    {
                        instructions.Add(new DeltaInstruction
                        {
                            Type = DeltaInstructionType.Literal,
                            SourceOffset = literalStart,
                            Length = pos - literalStart
                        });
                    }

                    // Emit match instruction
                    instructions.Add(new DeltaInstruction
                    {
                        Type = DeltaInstructionType.Match,
                        DestBlockIndex = matchedBlock.Index,
                        SourceOffset = pos,
                        Length = blockSize
                    });

                    pos += blockSize;
                    literalStart = pos;
                    continue;
                }
            }

            pos++;
        }

        // Emit any remaining literal data after the last match
        if (literalStart < sourceLength)
        {
            instructions.Add(new DeltaInstruction
            {
                Type = DeltaInstructionType.Literal,
                SourceOffset = literalStart,
                Length = sourceLength - literalStart
            });
        }

        return instructions;
    }

    /// <summary>
    /// Sends the delta instruction set and literal data to the destination endpoint.
    /// Posts a binary payload to <c>{destination.Uri}/delta/{transferId}</c> containing
    /// serialized MATCH and LITERAL instructions.
    /// </summary>
    /// <param name="transferId">The transfer identifier.</param>
    /// <param name="instructions">The computed delta instructions.</param>
    /// <param name="sourceData">The source data for extracting literal bytes.</param>
    /// <param name="destination">The destination endpoint.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The total number of bytes actually sent over the network.</returns>
    private async Task<long> SendDeltaAsync(
        string transferId,
        List<DeltaInstruction> instructions,
        byte[] sourceData,
        TransitEndpoint destination,
        CancellationToken ct)
    {
        var deltaUri = new Uri(destination.Uri, $"delta/{transferId}");

        // Serialize delta instructions as binary payload
        using var payloadStream = new MemoryStream(65536);
        using var writer = new BinaryWriter(payloadStream, System.Text.Encoding.UTF8, leaveOpen: true);

        // Write instruction count
        writer.Write(instructions.Count);

        foreach (var instr in instructions)
        {
            // Write instruction type (0 = MATCH, 1 = LITERAL)
            writer.Write((byte)(instr.Type == DeltaInstructionType.Match ? 0 : 1));

            if (instr.Type == DeltaInstructionType.Match)
            {
                // MATCH: destination block index + source offset
                writer.Write(instr.DestBlockIndex);
                writer.Write(instr.SourceOffset);
            }
            else
            {
                // LITERAL: offset + length + raw data
                writer.Write(instr.SourceOffset);
                writer.Write(instr.Length);
                writer.Write(sourceData, instr.SourceOffset, instr.Length);
            }
        }

        writer.Flush();
        var payloadBytes = payloadStream.ToArray();

        using var content = new ByteArrayContent(payloadBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = payloadBytes.Length;

        using var request = new HttpRequestMessage(HttpMethod.Post, deltaUri)
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

        return payloadBytes.Length;
    }

    /// <summary>
    /// Performs a full transfer when the destination does not have an existing version of the file
    /// (no block signatures available for delta comparison).
    /// </summary>
    /// <param name="transferId">The transfer identifier.</param>
    /// <param name="request">The original transfer request.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The total number of bytes transferred.</returns>
    private async Task<long> FullTransferFallbackAsync(
        string transferId,
        TransitRequest request,
        IProgress<TransitProgress>? progress,
        CancellationToken ct)
    {
        progress?.Report(new TransitProgress
        {
            TransferId = transferId,
            CurrentPhase = "Full transfer (no existing destination file)",
            PercentComplete = 20
        });

        var sourceData = await ReadSourceDataAsync(request, ct);

        using var content = new ByteArrayContent(sourceData);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = sourceData.Length;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, request.Destination.Uri)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Content = content
        };

        if (!string.IsNullOrEmpty(request.Destination.AuthToken))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.Destination.AuthToken);
        }

        using var response = await _httpClient.SendAsync(httpRequest, ct);
        response.EnsureSuccessStatusCode();

        progress?.Report(new TransitProgress
        {
            TransferId = transferId,
            BytesTransferred = sourceData.Length,
            TotalBytes = sourceData.Length,
            PercentComplete = 100.0,
            CurrentPhase = "Full transfer completed"
        });

        return sourceData.Length;
    }

    /// <summary>
    /// Reads the complete source data from the transfer request's data stream.
    /// If no data stream is provided, performs an HTTP GET from the source endpoint.
    /// </summary>
    /// <param name="request">The transfer request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The source data as a byte array.</returns>
    private async Task<byte[]> ReadSourceDataAsync(TransitRequest request, CancellationToken ct)
    {
        if (request.DataStream is not null)
        {
            if (request.DataStream.CanSeek)
            {
                request.DataStream.Position = 0;
            }

            using var memStream = new MemoryStream(65536);
            await request.DataStream.CopyToAsync(memStream, ct);
            return memStream.ToArray();
        }

        // Pull from source endpoint
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, request.Source.Uri)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };

        if (!string.IsNullOrEmpty(request.Source.AuthToken))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.Source.AuthToken);
        }

        using var response = await _httpClient.SendAsync(httpRequest, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>
    /// Determines the block size from request metadata or falls back to <see cref="DefaultBlockSize"/>.
    /// </summary>
    /// <param name="request">The transfer request with optional metadata.</param>
    /// <returns>The block size in bytes.</returns>
    private static int DetermineBlockSize(TransitRequest request)
    {
        if (request.Metadata.TryGetValue("blockSize", out var blockSizeStr) &&
            int.TryParse(blockSizeStr, out var customBlockSize) &&
            customBlockSize > 0)
        {
            return customBlockSize;
        }

        return DefaultBlockSize;
    }

    /// <summary>
    /// Stateless, thread-safe rolling hash computer implementing the rsync-style algorithm.
    /// Provides Adler-32 weak hash with O(1) rolling updates and SHA-256 strong hash
    /// for collision resolution.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Adler-32 algorithm maintains two running sums: <c>s1</c> (byte sum) and <c>s2</c>
    /// (cumulative s1 sum), both reduced modulo 65521 (the largest prime less than 2^16).
    /// The combined hash is <c>(s2 &lt;&lt; 16) | s1</c>.
    /// </para>
    /// <para>
    /// The rolling update (<see cref="RollHash"/>) achieves O(1) per-byte cost by subtracting
    /// the outgoing byte's contribution and adding the incoming byte's contribution without
    /// rescanning the entire window.
    /// </para>
    /// </remarks>
    internal sealed class RollingHashComputer
    {
        /// <summary>
        /// Adler-32 modulus: the largest prime less than 2^16.
        /// </summary>
        private const uint Adler32Mod = 65521;

        /// <summary>
        /// Computes the Adler-32 weak hash over the given data span.
        /// </summary>
        /// <param name="data">The data to hash.</param>
        /// <returns>The 32-bit Adler-32 hash value with s2 in the upper 16 bits and s1 in the lower 16 bits.</returns>
        public uint ComputeAdler32(ReadOnlySpan<byte> data)
        {
            uint s1 = 1;
            uint s2 = 0;

            for (var i = 0; i < data.Length; i++)
            {
                s1 = (s1 + data[i]) % Adler32Mod;
                s2 = (s2 + s1) % Adler32Mod;
            }

            return (s2 << 16) | s1;
        }

        /// <summary>
        /// Computes the SHA-256 strong hash over the given data span for collision resolution.
        /// </summary>
        /// <param name="data">The data to hash.</param>
        /// <returns>The 32-byte SHA-256 hash.</returns>
        public byte[] ComputeStrongHash(ReadOnlySpan<byte> data)
        {
            return SHA256.HashData(data);
        }

        /// <summary>
        /// Performs an O(1) rolling update of the Adler-32 hash when the window slides by one byte.
        /// Removes the contribution of <paramref name="removedByte"/> (leaving the window) and adds
        /// the contribution of <paramref name="addedByte"/> (entering the window).
        /// </summary>
        /// <param name="currentHash">The current Adler-32 hash of the window.</param>
        /// <param name="removedByte">The byte leaving the window (leftmost byte of the previous window).</param>
        /// <param name="addedByte">The byte entering the window (rightmost byte of the new window).</param>
        /// <param name="blockSize">The fixed window/block size.</param>
        /// <returns>The updated Adler-32 hash for the new window position.</returns>
        public uint RollHash(uint currentHash, byte removedByte, byte addedByte, int blockSize)
        {
            var s1 = currentHash & 0xFFFF;
            var s2 = currentHash >> 16;

            // Update s1: remove old byte, add new byte
            s1 = (s1 + Adler32Mod - removedByte + addedByte) % Adler32Mod;

            // Update s2: remove old s1 contribution (blockSize * removedByte), add new s1, subtract 1
            s2 = (s2 + Adler32Mod + Adler32Mod * (uint)blockSize - (uint)blockSize * removedByte + s1 - 1) % Adler32Mod;

            return (s2 << 16) | s1;
        }
    }

    /// <summary>
    /// Represents the signature of a single block at the destination, including both
    /// a weak hash (Adler-32) for fast matching and a strong hash (SHA-256) for verification.
    /// </summary>
    /// <param name="Index">Zero-based index of the block.</param>
    /// <param name="Offset">Byte offset of the block within the file.</param>
    /// <param name="Size">Size of the block in bytes.</param>
    /// <param name="WeakHash">Adler-32 weak hash for fast O(1) rolling comparison.</param>
    /// <param name="StrongHash">SHA-256 strong hash for collision resolution.</param>
    internal sealed record BlockSignature(int Index, long Offset, int Size, uint WeakHash, byte[] StrongHash);

    /// <summary>
    /// DTO for deserializing block signatures from the destination's JSON response.
    /// </summary>
    private sealed class BlockSignatureDto
    {
        /// <summary>
        /// Zero-based block index.
        /// </summary>
        public int Index { get; set; }

        /// <summary>
        /// Byte offset within the file.
        /// </summary>
        public long Offset { get; set; }

        /// <summary>
        /// Block size in bytes.
        /// </summary>
        public int Size { get; set; }

        /// <summary>
        /// Adler-32 weak hash value.
        /// </summary>
        public uint WeakHash { get; set; }

        /// <summary>
        /// SHA-256 strong hash as a hex string.
        /// </summary>
        public string StrongHash { get; set; } = string.Empty;
    }

    /// <summary>
    /// Types of delta instructions generated during the matching phase.
    /// </summary>
    private enum DeltaInstructionType
    {
        /// <summary>
        /// Block matches an existing destination block; reference only, no data transfer needed.
        /// </summary>
        Match,

        /// <summary>
        /// New or changed data that must be transferred literally.
        /// </summary>
        Literal
    }

    /// <summary>
    /// A single delta instruction representing either a matched block reference
    /// or a literal data region to transfer.
    /// </summary>
    private sealed class DeltaInstruction
    {
        /// <summary>
        /// The instruction type (MATCH or LITERAL).
        /// </summary>
        public required DeltaInstructionType Type { get; init; }

        /// <summary>
        /// For MATCH instructions: the destination block index to reference.
        /// For LITERAL instructions: not used (defaults to 0).
        /// </summary>
        public int DestBlockIndex { get; init; }

        /// <summary>
        /// The byte offset in the source data where this instruction starts.
        /// </summary>
        public required int SourceOffset { get; init; }

        /// <summary>
        /// The length of the data covered by this instruction in bytes.
        /// </summary>
        public required int Length { get; init; }
    }
}
