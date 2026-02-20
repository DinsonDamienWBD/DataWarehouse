using System.Diagnostics;
using System.Security.Cryptography;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Deduplication;

/// <summary>
/// Fixed-size block deduplication strategy with configurable block sizes.
/// Divides data into fixed-size blocks and deduplicates at the block level.
/// </summary>
/// <remarks>
/// Features:
/// - Configurable block sizes (4KB to 128KB)
/// - Block-level deduplication
/// - Reference counting for blocks
/// - Block map reconstruction for retrieval
/// - Memory-efficient for large files
/// </remarks>
public sealed class FixedBlockDeduplicationStrategy : DeduplicationStrategyBase
{
    private readonly BoundedDictionary<string, byte[]> _blockStore = new BoundedDictionary<string, byte[]>(1000);
    private readonly BoundedDictionary<string, ObjectBlockMap> _objectMaps = new BoundedDictionary<string, ObjectBlockMap>(1000);
    private readonly int _blockSize;
    private readonly int _minBlockSize;
    private readonly int _maxBlockSize;

    /// <summary>
    /// Initializes with default 8KB block size.
    /// </summary>
    public FixedBlockDeduplicationStrategy() : this(8192) { }

    /// <summary>
    /// Initializes with specified block size.
    /// </summary>
    /// <param name="blockSize">Block size in bytes (4KB to 128KB).</param>
    public FixedBlockDeduplicationStrategy(int blockSize)
    {
        _minBlockSize = 4096;      // 4KB minimum
        _maxBlockSize = 131072;    // 128KB maximum

        if (blockSize < _minBlockSize || blockSize > _maxBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"Block size must be between {_minBlockSize} and {_maxBlockSize} bytes");

        _blockSize = blockSize;
    }

    /// <inheritdoc/>
    public override string StrategyId => "dedup.fixedblock";

    /// <inheritdoc/>
    public override string DisplayName => "Fixed Block Deduplication";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = false,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 200_000,
        TypicalLatencyMs = 1.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        $"Fixed-size block deduplication using {_blockSize / 1024}KB blocks. " +
        "Divides data into uniform blocks and deduplicates at the block level. " +
        "Provides predictable performance and efficient storage for large files with repeated patterns.";

    /// <inheritdoc/>
    public override string[] Tags => ["deduplication", "block", "fixed-size", "chunk", "sub-file"];

    /// <summary>
    /// Gets the configured block size.
    /// </summary>
    public int BlockSize => _blockSize;

    /// <summary>
    /// Gets the total number of unique blocks stored.
    /// </summary>
    public int UniqueBlockCount => _blockStore.Count;

    /// <inheritdoc/>
    protected override async Task<DeduplicationResult> DeduplicateCoreAsync(
        Stream data,
        DeduplicationContext context,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var effectiveBlockSize = context.FixedBlockSize > 0 ? context.FixedBlockSize : _blockSize;

        // Validate block size
        if (effectiveBlockSize < _minBlockSize || effectiveBlockSize > _maxBlockSize)
        {
            effectiveBlockSize = _blockSize;
        }

        var blockHashes = new List<string>();
        var buffer = new byte[effectiveBlockSize];
        long totalSize = 0;
        int duplicateBlocks = 0;
        int totalBlocks = 0;
        long storedSize = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var bytesRead = await ReadFullBlockAsync(data, buffer, ct);
            if (bytesRead == 0)
                break;

            totalBlocks++;
            totalSize += bytesRead;

            // Get actual block data (may be less than buffer for last block)
            var blockData = bytesRead < effectiveBlockSize
                ? buffer[..bytesRead]
                : buffer;

            // Compute block hash
            var blockHash = ComputeHash(blockData);
            var hashString = HashToString(blockHash);
            blockHashes.Add(hashString);

            // Check if block exists
            if (ChunkIndex.TryGetValue(hashString, out var existing))
            {
                duplicateBlocks++;
                Interlocked.Increment(ref existing.ReferenceCount);
            }
            else
            {
                // Store new block
                var blockCopy = new byte[bytesRead];
                Array.Copy(buffer, blockCopy, bytesRead);
                _blockStore[hashString] = blockCopy;

                ChunkIndex[hashString] = new ChunkEntry
                {
                    StorageId = hashString,
                    Size = bytesRead,
                    ReferenceCount = 1
                };

                storedSize += bytesRead;
            }
        }

        // Store block map for this object
        var blockMap = new ObjectBlockMap
        {
            ObjectId = context.ObjectId,
            BlockHashes = blockHashes,
            BlockSize = effectiveBlockSize,
            TotalSize = totalSize,
            CreatedAt = DateTime.UtcNow
        };
        _objectMaps[context.ObjectId] = blockMap;

        // Compute overall file hash
        var fileHash = ComputeFileHash(blockHashes);

        sw.Stop();

        if (totalBlocks == duplicateBlocks && totalBlocks > 0)
        {
            // Entire file is duplicate
            return DeduplicationResult.Duplicate(fileHash, GetDuplicateReference(blockHashes), totalSize, sw.Elapsed);
        }

        return DeduplicationResult.Unique(fileHash, totalSize, storedSize, totalBlocks, duplicateBlocks, sw.Elapsed);
    }

    private async Task<int> ReadFullBlockAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), ct);
            if (read == 0)
                break;
            totalRead += read;
        }
        return totalRead;
    }

    private byte[] ComputeFileHash(List<string> blockHashes)
    {
        var combined = string.Join(":", blockHashes);
        return SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(combined));
    }

    private string GetDuplicateReference(List<string> blockHashes)
    {
        // Find first object with same block sequence
        foreach (var map in _objectMaps.Values)
        {
            if (map.BlockHashes.SequenceEqual(blockHashes))
            {
                return map.ObjectId;
            }
        }
        return blockHashes.FirstOrDefault() ?? string.Empty;
    }

    /// <summary>
    /// Reconstructs data for an object from its block map.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Reconstructed data stream.</returns>
    public async Task<Stream?> ReconstructAsync(string objectId, CancellationToken ct = default)
    {
        if (!_objectMaps.TryGetValue(objectId, out var blockMap))
            return null;

        var memoryStream = new MemoryStream(blockMap.BlockHashes.Count * _blockSize);

        foreach (var hashString in blockMap.BlockHashes)
        {
            ct.ThrowIfCancellationRequested();

            if (_blockStore.TryGetValue(hashString, out var blockData))
            {
                await memoryStream.WriteAsync(blockData, ct);
            }
            else
            {
                // Block missing - data corruption
                memoryStream.Dispose();
                return null;
            }
        }

        memoryStream.Position = 0;
        return memoryStream;
    }

    /// <summary>
    /// Removes an object and decrements block reference counts.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <returns>True if removed successfully.</returns>
    public bool RemoveObject(string objectId)
    {
        if (!_objectMaps.TryRemove(objectId, out var blockMap))
            return false;

        foreach (var hashString in blockMap.BlockHashes)
        {
            if (ChunkIndex.TryGetValue(hashString, out var entry))
            {
                var newCount = Interlocked.Decrement(ref entry.ReferenceCount);
                if (newCount <= 0)
                {
                    ChunkIndex.TryRemove(hashString, out _);
                    _blockStore.TryRemove(hashString, out _);
                }
            }
        }

        return true;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _blockStore.Clear();
        _objectMaps.Clear();
        ChunkIndex.Clear();
        return Task.CompletedTask;
    }

    private sealed class ObjectBlockMap
    {
        public required string ObjectId { get; init; }
        public required List<string> BlockHashes { get; init; }
        public required int BlockSize { get; init; }
        public required long TotalSize { get; init; }
        public required DateTime CreatedAt { get; init; }
    }
}
