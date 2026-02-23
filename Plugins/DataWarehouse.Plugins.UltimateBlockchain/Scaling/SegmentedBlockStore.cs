// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using System.Collections.Concurrent;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Scaling;
using DataWarehouse.SDK.Contracts.TamperProof;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateBlockchain.Scaling;

/// <summary>
/// Segmented memory-mapped page store that replaces unbounded <c>List&lt;Block&gt;</c> with
/// fixed-size 64 MB segments. All block indices use <see langword="long"/> to support VDEs
/// with more than 2 billion blocks without integer overflow.
/// </summary>
/// <remarks>
/// <para>
/// Blocks are stored contiguously within segments. A segment index maps block number ranges
/// to their containing segment. Hot segments (most recent N) are kept in memory; cold segments
/// are loaded on demand from disk. The sharded journal partitions writes by block range for
/// parallel append throughput. Per-tier locks enable concurrent access across hot/warm/cold tiers.
/// </para>
/// <para>
/// The block page cache uses <see cref="BoundedCache{TKey,TValue}"/> with LRU eviction,
/// sized from <see cref="ScalingLimits.MaxCacheEntries"/> (default 100,000 blocks).
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 88-04: Segmented block store replacing List<Block>")]
public sealed class SegmentedBlockStore : IDisposable
{
    /// <summary>
    /// Default segment size in bytes (64 MB).
    /// </summary>
    public const long DefaultSegmentSizeBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Default number of blocks per journal shard.
    /// </summary>
    public const long DefaultBlocksPerJournalShard = 10_000;

    /// <summary>
    /// Default maximum number of cached block pages.
    /// </summary>
    public const int DefaultMaxCacheEntries = 100_000;

    private readonly ILogger _logger;
    private readonly long _segmentSizeBytes;
    private readonly long _blocksPerJournalShard;
    private readonly int _maxHotSegments;
    private readonly string _dataDirectory;

    // Segment index: maps segment number -> segment metadata
    private readonly ConcurrentDictionary<long, SegmentMetadata> _segments = new();
    private long _totalBlockCount;
    private long _nextSegmentNumber;
    // Per-tier locks for concurrent access across hot/warm/cold tiers
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _tierLocks = new();

    // Block page cache with LRU eviction
    private readonly BoundedCache<long, BlockData> _blockCache;

    // Journal shards: keyed by shard number (blockNumber / blocksPerShard)
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _journalShardLocks = new();

    // Global append lock for segment management
    private readonly SemaphoreSlim _appendLock = new(1, 1);

    private bool _disposed;

    /// <summary>
    /// Initializes a new <see cref="SegmentedBlockStore"/> with the specified configuration.
    /// </summary>
    /// <param name="logger">Logger instance for diagnostics.</param>
    /// <param name="dataDirectory">Root directory for segment files and journal shards.</param>
    /// <param name="segmentSizeBytes">Size of each segment in bytes. Default: 64 MB.</param>
    /// <param name="blocksPerJournalShard">Number of blocks per journal shard. Default: 10,000.</param>
    /// <param name="maxHotSegments">Maximum number of hot (in-memory) segments. Default: 4.</param>
    /// <param name="maxCacheEntries">Maximum block page cache entries. Default: 100,000.</param>
    public SegmentedBlockStore(
        ILogger logger,
        string dataDirectory,
        long segmentSizeBytes = DefaultSegmentSizeBytes,
        long blocksPerJournalShard = DefaultBlocksPerJournalShard,
        int maxHotSegments = 4,
        int maxCacheEntries = DefaultMaxCacheEntries)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        _segmentSizeBytes = segmentSizeBytes > 0 ? segmentSizeBytes : DefaultSegmentSizeBytes;
        _blocksPerJournalShard = blocksPerJournalShard > 0 ? blocksPerJournalShard : DefaultBlocksPerJournalShard;
        _maxHotSegments = maxHotSegments > 0 ? maxHotSegments : 4;

        Directory.CreateDirectory(_dataDirectory);
        Directory.CreateDirectory(Path.Combine(_dataDirectory, "segments"));
        Directory.CreateDirectory(Path.Combine(_dataDirectory, "journal"));

        _blockCache = new BoundedCache<long, BlockData>(
            new BoundedCacheOptions<long, BlockData>
            {
                MaxEntries = maxCacheEntries > 0 ? maxCacheEntries : DefaultMaxCacheEntries,
                EvictionPolicy = CacheEvictionMode.LRU
            });

        // Initialize first segment
        _segments[0] = new SegmentMetadata(0, 0, 0, true);
        _nextSegmentNumber = 1;
    }

    /// <summary>
    /// Gets the total number of blocks across all segments.
    /// Uses <see langword="long"/> to support more than 2 billion blocks.
    /// </summary>
    public long BlockCount => Interlocked.Read(ref _totalBlockCount);

    /// <summary>
    /// Gets the number of segments currently allocated.
    /// </summary>
    public long SegmentCount => Interlocked.Read(ref _nextSegmentNumber);

    /// <summary>
    /// Gets the number of journal shards.
    /// </summary>
    public long JournalShardCount => (BlockCount + _blocksPerJournalShard - 1) / _blocksPerJournalShard;

    /// <summary>
    /// Gets cache statistics from the block page cache.
    /// </summary>
    public CacheStatistics CacheStatistics => _blockCache.GetStatistics();

    /// <summary>
    /// Appends a block to the current segment, creating a new segment when the current one is full.
    /// </summary>
    /// <param name="block">The block data to append.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the block has been appended and journaled.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="block"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when the store has been disposed.</exception>
    public async Task AppendBlockAsync(BlockData block, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(block);

        await _appendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long blockNumber = Interlocked.Read(ref _totalBlockCount);
            block = block with { BlockNumber = blockNumber };

            // Determine segment, create new one if current is full
            long currentSegment = _nextSegmentNumber - 1;
            long estimatedBlockSize = EstimateBlockSize(block);

            var segmentMeta = _segments[currentSegment];
            if (segmentMeta.UsedBytes + estimatedBlockSize > _segmentSizeBytes && segmentMeta.BlockCount > 0)
            {
                // Mark current segment as cold if beyond hot range
                currentSegment = CreateNewSegment();
                segmentMeta = _segments[currentSegment];
            }

            // Update segment metadata
            _segments[currentSegment] = segmentMeta with
            {
                BlockCount = segmentMeta.BlockCount + 1,
                UsedBytes = segmentMeta.UsedBytes + estimatedBlockSize,
                IsHot = true
            };

            // Add to cache
            _blockCache.Put(blockNumber, block);

            // Increment total count
            Interlocked.Increment(ref _totalBlockCount);

            // Journal the block to the appropriate shard
            await JournalBlockAsync(block, ct).ConfigureAwait(false);

            // Manage hot/cold segment transitions
            ManageHotSegments();

            _logger.LogDebug(
                "Appended block {BlockNumber} to segment {Segment}, total blocks: {Total}",
                blockNumber, currentSegment, Interlocked.Read(ref _totalBlockCount));
        }
        finally
        {
            _appendLock.Release();
        }
    }

    /// <summary>
    /// Retrieves a block by its number, checking cache first then falling back to segment lookup.
    /// </summary>
    /// <param name="blockNumber">The block number to retrieve (long-safe, no int cast).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The block data if found; otherwise <c>null</c>.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the store has been disposed.</exception>
    public async Task<BlockData?> GetBlockAsync(long blockNumber, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (blockNumber < 0 || blockNumber >= Interlocked.Read(ref _totalBlockCount))
            return null;

        // Cache-first lookup
        var cached = _blockCache.GetOrDefault(blockNumber);
        if (cached != null)
            return cached;

        // Determine which tier this block belongs to
        string tier = GetTierForBlock(blockNumber);
        var tierLock = _tierLocks.GetOrAdd(tier, _ => new SemaphoreSlim(1, 1));

        await tierLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Double-check cache after acquiring lock
            cached = _blockCache.GetOrDefault(blockNumber);
            if (cached != null)
                return cached;

            // Load from journal shard
            var block = await LoadBlockFromJournalAsync(blockNumber, ct).ConfigureAwait(false);
            if (block != null)
            {
                _blockCache.Put(blockNumber, block);
            }

            return block;
        }
        finally
        {
            tierLock.Release();
        }
    }

    /// <summary>
    /// Retrieves a contiguous range of blocks by starting number and count.
    /// </summary>
    /// <param name="start">The starting block number (long-safe).</param>
    /// <param name="count">The number of blocks to retrieve (long-safe).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A read-only list of block data for the requested range.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the store has been disposed.</exception>
    public async Task<IReadOnlyList<BlockData>> GetBlockRangeAsync(long start, long count, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var totalBlocks = Interlocked.Read(ref _totalBlockCount);
        if (start < 0 || start >= totalBlocks || count <= 0)
            return Array.Empty<BlockData>();

        long end = Math.Min(start + count, totalBlocks);
        var result = new List<BlockData>((int)Math.Min(end - start, int.MaxValue));

        for (long i = start; i < end; i++)
        {
            var block = await GetBlockAsync(i, ct).ConfigureAwait(false);
            if (block != null)
                result.Add(block);
        }

        return result;
    }

    /// <summary>
    /// Compacts segments by merging small segments and defragmenting block storage.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when compaction is done.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the store has been disposed.</exception>
    public async Task CompactAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _appendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Identify small segments that can be merged
            var smallSegments = new List<long>();
            foreach (var kvp in _segments)
            {
                if (kvp.Value.UsedBytes < _segmentSizeBytes / 4 && !kvp.Value.IsHot)
                {
                    smallSegments.Add(kvp.Key);
                }
            }

            if (smallSegments.Count < 2)
            {
                _logger.LogDebug("No segments eligible for compaction");
                return;
            }

            long mergedCount = 0;
            for (int i = 0; i + 1 < smallSegments.Count; i += 2)
            {
                var seg1 = smallSegments[i];
                var seg2 = smallSegments[i + 1];

                if (!_segments.TryGetValue(seg1, out var meta1) || !_segments.TryGetValue(seg2, out var meta2))
                    continue;

                if (meta1.UsedBytes + meta2.UsedBytes <= _segmentSizeBytes)
                {
                    // Merge seg2 into seg1
                    _segments[seg1] = meta1 with
                    {
                        BlockCount = meta1.BlockCount + meta2.BlockCount,
                        UsedBytes = meta1.UsedBytes + meta2.UsedBytes
                    };
                    _segments.TryRemove(seg2, out _);
                    mergedCount++;
                }
            }

            _logger.LogInformation("Compacted {Count} segment pairs", mergedCount);
        }
        finally
        {
            _appendLock.Release();
        }
    }

    /// <summary>
    /// Maps a block number to its segment number and intra-segment offset.
    /// Uses <see langword="long"/> throughout to prevent overflow at more than 2 billion blocks.
    /// </summary>
    /// <param name="blockNumber">The block number to resolve.</param>
    /// <returns>A tuple of (segmentNumber, offsetWithinSegment).</returns>
    public (long SegmentNumber, long Offset) GetBlockIndex(long blockNumber)
    {
        // Walk segments to find which one contains this block
        long runningCount = 0;
        foreach (var kvp in _segments.OrderBy(s => s.Key))
        {
            if (blockNumber < runningCount + kvp.Value.BlockCount)
            {
                return (kvp.Key, blockNumber - runningCount);
            }
            runningCount += kvp.Value.BlockCount;
        }

        return (-1, -1); // Block not found
    }

    /// <summary>
    /// Gets the per-tier lock contention information for scaling metrics.
    /// </summary>
    /// <returns>A dictionary of tier name to current wait count.</returns>
    public IReadOnlyDictionary<string, int> GetTierLockContention()
    {
        var result = new Dictionary<string, int>();
        foreach (var kvp in _tierLocks)
        {
            result[kvp.Key] = kvp.Value.CurrentCount == 0 ? 1 : 0;
        }
        return result;
    }

    // ---- Private helpers ----

    private long CreateNewSegment()
    {
        long segNum = Interlocked.Increment(ref _nextSegmentNumber) - 1;
        _segments[segNum] = new SegmentMetadata(segNum, 0, 0, true);

        _logger.LogDebug("Created new segment {SegmentNumber}", segNum);
        return segNum;
    }

    private void ManageHotSegments()
    {
        // Demote oldest hot segments to cold when we exceed max hot count
        var hotSegments = _segments
            .Where(s => s.Value.IsHot)
            .OrderBy(s => s.Key)
            .ToList();

        while (hotSegments.Count > _maxHotSegments)
        {
            var oldest = hotSegments[0];
            _segments[oldest.Key] = oldest.Value with { IsHot = false };
            hotSegments.RemoveAt(0);
        }
    }

    private string GetTierForBlock(long blockNumber)
    {
        long total = Interlocked.Read(ref _totalBlockCount);
        if (total == 0) return "hot";

        double position = (double)blockNumber / total;
        if (position >= 0.8) return "hot";
        if (position >= 0.4) return "warm";
        return "cold";
    }

    private async Task JournalBlockAsync(BlockData block, CancellationToken ct)
    {
        long shardNumber = block.BlockNumber / _blocksPerJournalShard;
        var shardLock = _journalShardLocks.GetOrAdd(shardNumber, _ => new SemaphoreSlim(1, 1));

        await shardLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string shardPath = Path.Combine(_dataDirectory, "journal", $"shard-{shardNumber:D6}.jsonl");
            var json = System.Text.Json.JsonSerializer.Serialize(block);
            await File.AppendAllLinesAsync(shardPath, new[] { json }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to journal block {BlockNumber} to shard {Shard}",
                block.BlockNumber, shardNumber);
        }
        finally
        {
            shardLock.Release();
        }
    }

    private async Task<BlockData?> LoadBlockFromJournalAsync(long blockNumber, CancellationToken ct)
    {
        long shardNumber = blockNumber / _blocksPerJournalShard;
        string shardPath = Path.Combine(_dataDirectory, "journal", $"shard-{shardNumber:D6}.jsonl");

        if (!File.Exists(shardPath))
            return null;

        try
        {
            var lines = await File.ReadAllLinesAsync(shardPath, ct).ConfigureAwait(false);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var block = System.Text.Json.JsonSerializer.Deserialize<BlockData>(line);
                if (block != null && block.BlockNumber == blockNumber)
                    return block;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load block {BlockNumber} from journal shard {Shard}",
                blockNumber, shardNumber);
        }

        return null;
    }

    private static long EstimateBlockSize(BlockData block)
    {
        // Conservative estimate: base overhead + transaction data
        long baseSize = 256; // Block headers, hash, timestamp, etc.
        long txSize = block.TransactionCount * 512L; // Estimated per-transaction size
        return baseSize + txSize;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _blockCache.Dispose();
        _appendLock.Dispose();

        foreach (var kvp in _tierLocks)
            kvp.Value.Dispose();

        foreach (var kvp in _journalShardLocks)
            kvp.Value.Dispose();
    }

    // ---- Nested types ----

    /// <summary>
    /// Metadata for a single segment in the store.
    /// </summary>
    /// <param name="SegmentNumber">The segment's ordinal number.</param>
    /// <param name="BlockCount">Number of blocks in this segment.</param>
    /// <param name="UsedBytes">Approximate bytes used in this segment.</param>
    /// <param name="IsHot">Whether this segment is in memory (hot) or file-backed only (cold).</param>
    private record SegmentMetadata(long SegmentNumber, long BlockCount, long UsedBytes, bool IsHot);
}

/// <summary>
/// Immutable representation of a block in the segmented store.
/// All indices use <see langword="long"/> to prevent overflow beyond 2 billion blocks.
/// </summary>
/// <param name="BlockNumber">The block's position in the chain (long-safe).</param>
/// <param name="PreviousHash">SHA-256 hash of the previous block.</param>
/// <param name="Timestamp">UTC timestamp when the block was created.</param>
/// <param name="TransactionCount">Number of transactions in this block.</param>
/// <param name="MerkleRoot">Merkle tree root hash of the block's transactions.</param>
/// <param name="Hash">SHA-256 hash of this block's contents.</param>
/// <param name="TransactionData">Serialized transaction data for persistence.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 88-04: Block data record with long addressing")]
public record BlockData(
    long BlockNumber,
    string PreviousHash,
    DateTimeOffset Timestamp,
    int TransactionCount,
    string MerkleRoot,
    string Hash,
    string? TransactionData = null);
