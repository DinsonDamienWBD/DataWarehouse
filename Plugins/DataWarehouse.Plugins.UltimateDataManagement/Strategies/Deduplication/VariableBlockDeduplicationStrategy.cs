using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Deduplication;

/// <summary>
/// Variable-size block deduplication using content-defined chunking (CDC) with Rabin fingerprinting.
/// Creates content-aware chunk boundaries for better deduplication of modified files.
/// </summary>
/// <remarks>
/// Features:
/// - Rabin fingerprinting for chunk boundary detection
/// - Content-shift resistant chunking
/// - Configurable min/max/average chunk sizes
/// - Efficient detection of modified file sections
/// - Better dedup ratio than fixed-block for most workloads
/// </remarks>
public sealed class VariableBlockDeduplicationStrategy : DeduplicationStrategyBase
{
    private readonly ConcurrentDictionary<string, byte[]> _chunkStore = new();
    private readonly ConcurrentDictionary<string, ObjectChunkMap> _objectMaps = new();
    private readonly int _minChunkSize;
    private readonly int _maxChunkSize;
    private readonly int _avgChunkSize;
    private readonly ulong _fingerPrintMask;

    // Rabin fingerprint parameters
    private const ulong RabinPrime = 0x3DA3358B4DC173UL;
    private const int WindowSize = 48;
    private readonly ulong[] _rabinTable;
    private readonly ulong _rabinWindowMultiplier;

    /// <summary>
    /// Initializes with default chunk sizes (min=2KB, max=64KB, avg=8KB).
    /// </summary>
    public VariableBlockDeduplicationStrategy() : this(2048, 65536, 8192) { }

    /// <summary>
    /// Initializes with specified chunk size parameters.
    /// </summary>
    /// <param name="minChunkSize">Minimum chunk size in bytes.</param>
    /// <param name="maxChunkSize">Maximum chunk size in bytes.</param>
    /// <param name="avgChunkSize">Target average chunk size in bytes.</param>
    public VariableBlockDeduplicationStrategy(int minChunkSize, int maxChunkSize, int avgChunkSize)
    {
        if (minChunkSize < 512)
            throw new ArgumentOutOfRangeException(nameof(minChunkSize), "Minimum chunk size must be at least 512 bytes");
        if (maxChunkSize < minChunkSize)
            throw new ArgumentOutOfRangeException(nameof(maxChunkSize), "Maximum chunk size must be >= minimum");
        if (avgChunkSize < minChunkSize || avgChunkSize > maxChunkSize)
            throw new ArgumentOutOfRangeException(nameof(avgChunkSize), "Average chunk size must be between min and max");

        _minChunkSize = minChunkSize;
        _maxChunkSize = maxChunkSize;
        _avgChunkSize = avgChunkSize;

        // Calculate fingerprint mask for target average size
        // Number of bits to match = log2(avgChunkSize)
        var bitsToMatch = (int)Math.Log2(avgChunkSize);
        _fingerPrintMask = (1UL << bitsToMatch) - 1;

        // Initialize Rabin lookup table
        _rabinTable = new ulong[256];
        for (int i = 0; i < 256; i++)
        {
            ulong fp = (ulong)i;
            for (int j = 0; j < 8; j++)
            {
                fp = (fp >> 1) ^ ((fp & 1) * RabinPrime);
            }
            _rabinTable[i] = fp;
        }

        // Calculate window multiplier
        _rabinWindowMultiplier = 1;
        for (int i = 0; i < WindowSize * 8; i++)
        {
            _rabinWindowMultiplier = (_rabinWindowMultiplier >> 1) ^ ((_rabinWindowMultiplier & 1) * RabinPrime);
        }
    }

    /// <inheritdoc/>
    public override string StrategyId => "dedup.variableblock";

    /// <inheritdoc/>
    public override string DisplayName => "Variable Block Deduplication (CDC)";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = false,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 150_000,
        TypicalLatencyMs = 2.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        $"Content-defined chunking (CDC) with Rabin fingerprinting. Target chunk sizes: {_minChunkSize / 1024}KB-{_maxChunkSize / 1024}KB (avg {_avgChunkSize / 1024}KB). " +
        "Content-aware boundaries resist data shift, providing better deduplication for modified files. " +
        "Ideal for backup systems and versioned storage.";

    /// <inheritdoc/>
    public override string[] Tags => ["deduplication", "cdc", "rabin", "variable-block", "content-defined"];

    /// <summary>
    /// Gets the minimum chunk size.
    /// </summary>
    public int MinChunkSize => _minChunkSize;

    /// <summary>
    /// Gets the maximum chunk size.
    /// </summary>
    public int MaxChunkSize => _maxChunkSize;

    /// <summary>
    /// Gets the target average chunk size.
    /// </summary>
    public int AverageChunkSize => _avgChunkSize;

    /// <inheritdoc/>
    protected override async Task<DeduplicationResult> DeduplicateCoreAsync(
        Stream data,
        DeduplicationContext context,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // Use context settings if provided
        var minSize = context.MinChunkSize > 0 ? context.MinChunkSize : _minChunkSize;
        var maxSize = context.MaxChunkSize > 0 ? context.MaxChunkSize : _maxChunkSize;

        // Read all data for chunking
        using var memoryStream = new MemoryStream(65536);
        await data.CopyToAsync(memoryStream, ct);
        var dataBytes = memoryStream.ToArray();
        var totalSize = dataBytes.Length;

        if (totalSize == 0)
        {
            sw.Stop();
            return DeduplicationResult.Unique(Array.Empty<byte>(), 0, 0, 0, 0, sw.Elapsed);
        }

        // Perform content-defined chunking
        var chunks = ChunkData(dataBytes, minSize, maxSize);
        var chunkHashes = new List<ChunkInfo>();
        int duplicateChunks = 0;
        long storedSize = 0;

        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();

            var chunkHash = ComputeHash(chunk.Data);
            var hashString = HashToString(chunkHash);

            chunkHashes.Add(new ChunkInfo
            {
                Hash = hashString,
                Offset = chunk.Offset,
                Size = chunk.Size
            });

            if (ChunkIndex.TryGetValue(hashString, out var existing))
            {
                duplicateChunks++;
                Interlocked.Increment(ref existing.ReferenceCount);
            }
            else
            {
                // Store new chunk
                _chunkStore[hashString] = chunk.Data;
                ChunkIndex[hashString] = new ChunkEntry
                {
                    StorageId = hashString,
                    Size = chunk.Size,
                    ReferenceCount = 1
                };
                storedSize += chunk.Size;
            }
        }

        // Store chunk map
        _objectMaps[context.ObjectId] = new ObjectChunkMap
        {
            ObjectId = context.ObjectId,
            Chunks = chunkHashes,
            TotalSize = totalSize,
            CreatedAt = DateTime.UtcNow
        };

        // Compute file hash
        var fileHash = ComputeFileHash(chunkHashes.Select(c => c.Hash));

        sw.Stop();

        if (chunks.Count == duplicateChunks && chunks.Count > 0)
        {
            return DeduplicationResult.Duplicate(fileHash, FindDuplicateObject(chunkHashes), totalSize, sw.Elapsed);
        }

        return DeduplicationResult.Unique(fileHash, totalSize, storedSize, chunks.Count, duplicateChunks, sw.Elapsed);
    }

    private List<DataChunk> ChunkData(byte[] data, int minSize, int maxSize)
    {
        var chunks = new List<DataChunk>();
        int offset = 0;

        while (offset < data.Length)
        {
            var chunkEnd = FindChunkBoundary(data, offset, minSize, maxSize);
            var chunkSize = chunkEnd - offset;

            var chunkData = new byte[chunkSize];
            Array.Copy(data, offset, chunkData, 0, chunkSize);

            chunks.Add(new DataChunk
            {
                Data = chunkData,
                Offset = offset,
                Size = chunkSize
            });

            offset = chunkEnd;
        }

        return chunks;
    }

    private int FindChunkBoundary(byte[] data, int start, int minSize, int maxSize)
    {
        var end = Math.Min(start + maxSize, data.Length);

        // If remaining data is small, use it all
        if (end - start <= minSize)
            return end;

        // Initialize fingerprint with first window
        ulong fingerprint = 0;
        var windowStart = start + minSize - WindowSize;
        if (windowStart < start)
            windowStart = start;

        for (int i = windowStart; i < start + minSize && i < end; i++)
        {
            fingerprint = RollingHash(fingerprint, 0, data[i]);
        }

        // Slide window looking for boundary
        for (int i = start + minSize; i < end; i++)
        {
            var oldByte = i >= WindowSize ? data[i - WindowSize] : (byte)0;
            fingerprint = RollingHash(fingerprint, oldByte, data[i]);

            // Check if fingerprint matches mask (marks chunk boundary)
            if ((fingerprint & _fingerPrintMask) == 0)
            {
                return i + 1;
            }
        }

        // No boundary found, use max size
        return end;
    }

    private ulong RollingHash(ulong currentHash, byte removeByte, byte addByte)
    {
        // Remove old byte contribution
        currentHash ^= _rabinTable[removeByte] * _rabinWindowMultiplier;
        // Add new byte
        currentHash = (currentHash << 8) ^ _rabinTable[addByte];
        return currentHash;
    }

    private byte[] ComputeFileHash(IEnumerable<string> chunkHashes)
    {
        var combined = string.Join(":", chunkHashes);
        return SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(combined));
    }

    private string FindDuplicateObject(List<ChunkInfo> chunks)
    {
        var targetHashes = chunks.Select(c => c.Hash).ToList();

        foreach (var map in _objectMaps.Values)
        {
            if (map.Chunks.Select(c => c.Hash).SequenceEqual(targetHashes))
            {
                return map.ObjectId;
            }
        }

        return chunks.FirstOrDefault()?.Hash ?? string.Empty;
    }

    /// <summary>
    /// Reconstructs data for an object from its chunk map.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Reconstructed data stream.</returns>
    public async Task<Stream?> ReconstructAsync(string objectId, CancellationToken ct = default)
    {
        if (!_objectMaps.TryGetValue(objectId, out var chunkMap))
            return null;

        var memoryStream = new MemoryStream(chunkMap.Chunks.Sum(c => c.Size));

        foreach (var chunk in chunkMap.Chunks.OrderBy(c => c.Offset))
        {
            ct.ThrowIfCancellationRequested();

            if (_chunkStore.TryGetValue(chunk.Hash, out var chunkData))
            {
                await memoryStream.WriteAsync(chunkData, ct);
            }
            else
            {
                memoryStream.Dispose();
                return null;
            }
        }

        memoryStream.Position = 0;
        return memoryStream;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _chunkStore.Clear();
        _objectMaps.Clear();
        ChunkIndex.Clear();
        return Task.CompletedTask;
    }

    private sealed record DataChunk
    {
        public required byte[] Data { get; init; }
        public required int Offset { get; init; }
        public required int Size { get; init; }
    }

    private sealed class ChunkInfo
    {
        public required string Hash { get; init; }
        public required int Offset { get; init; }
        public required int Size { get; init; }
    }

    private sealed class ObjectChunkMap
    {
        public required string ObjectId { get; init; }
        public required List<ChunkInfo> Chunks { get; init; }
        public required long TotalSize { get; init; }
        public required DateTime CreatedAt { get; init; }
    }
}
