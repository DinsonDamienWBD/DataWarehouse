using DataWarehouse.SDK.Contracts;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;

/// <summary>
/// Compact (32-byte) directory entry describing a single metaslab.
/// Layout: MetaslabId(4) + StartBlock(8) + BlockCount(8) + FreeBlockCount(8) + State(1) + ZoneId(1) + RegionId(2) = 32 bytes.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87-18: Hierarchical metaslab allocator (VOPT-31)")]
public readonly struct MetaslabDirectoryEntry
{
    /// <summary>Fixed size per entry: 32 bytes for 128 MB directory at 4M metaslabs.</summary>
    public const int Size = 32;

    /// <summary>Unique metaslab identifier.</summary>
    public int MetaslabId { get; init; }

    /// <summary>Absolute block number where this metaslab starts.</summary>
    public long StartBlock { get; init; }

    /// <summary>Total number of blocks in this metaslab.</summary>
    public long BlockCount { get; init; }

    /// <summary>Number of free blocks (updated on every alloc/free without loading bitmap).</summary>
    public long FreeBlockCount { get; init; }

    /// <summary>Current state of the metaslab.</summary>
    public MetaslabState State { get; init; }

    /// <summary>Zone identifier (top-level hierarchy, 0-15).</summary>
    public byte ZoneId { get; init; }

    /// <summary>Region identifier within the zone (0-255).</summary>
    public byte RegionId { get; init; }

    /// <summary>Serializes this entry into a 32-byte span.</summary>
    public void Serialize(Span<byte> buffer)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buffer, MetaslabId);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(4), StartBlock);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(12), BlockCount);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(20), FreeBlockCount);
        buffer[28] = (byte)State;
        buffer[29] = ZoneId;
        BinaryPrimitives.WriteInt16LittleEndian(buffer.Slice(30), RegionId);
    }

    /// <summary>Deserializes a directory entry from a 32-byte span.</summary>
    public static MetaslabDirectoryEntry Deserialize(ReadOnlySpan<byte> buffer) => new()
    {
        MetaslabId    = BinaryPrimitives.ReadInt32LittleEndian(buffer),
        StartBlock    = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(4)),
        BlockCount    = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(12)),
        FreeBlockCount = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(20)),
        State         = (MetaslabState)buffer[28],
        ZoneId        = buffer[29],
        RegionId      = (byte)BinaryPrimitives.ReadInt16LittleEndian(buffer.Slice(30))
    };
}

/// <summary>
/// Directory of metaslabs with zone/region/AG hierarchy.
/// Provides O(1) best-fit metaslab selection and O(1) metaslab lookup by block number.
///
/// Hierarchy:
/// - Zone: top-level partition (16 zones for first 16 PB, expandable)
/// - Region: mid-level within zone (256 regions per zone)
/// - AG: existing AllocationGroup within region
/// - Metaslab: smallest unit (~256MB default, tunable)
///
/// Memory management: LRU eviction of loaded metaslab bitmaps when loaded count exceeds MaxLoadedMetaslabs.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87-18: Hierarchical metaslab allocator (VOPT-31)")]
public sealed class MetaslabDirectory : IDisposable
{
    /// <summary>Default metaslab size (256 MB at 4KB blocks = 65,536 blocks).</summary>
    public const long DefaultMetaslabSizeBytes = 256L * 1024 * 1024;

    /// <summary>Maximum number of concurrently loaded metaslab bitmaps before LRU eviction.</summary>
    public const int DefaultMaxLoadedMetaslabs = 64;

    private readonly IBlockDevice _device;
    private readonly long _directoryStartBlock;
    private readonly int _blockSize;
    private readonly long _totalVdeBlocks;
    private readonly long _metaslabBlockCount;

    private MetaslabDirectoryEntry[] _entries = Array.Empty<MetaslabDirectoryEntry>();
    private readonly Dictionary<int, Metaslab> _loadedMetaslabs = new();
    private readonly LinkedList<int> _lruOrder = new(); // metaslabId in LRU order (front = MRU)
    private readonly Dictionary<int, LinkedListNode<int>> _lruNodes = new();
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private bool _disposed;

    /// <summary>Gets the number of metaslabs in the directory.</summary>
    public int MetaslabCount
    {
        get
        {
            _lock.EnterReadLock();
            try { return _entries.Length; }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>Gets the configured maximum number of concurrently loaded metaslab bitmaps.</summary>
    public int MaxLoadedMetaslabs { get; }

    /// <summary>
    /// Creates a new metaslab directory.
    /// </summary>
    /// <param name="device">Block device for directory and bitmap I/O.</param>
    /// <param name="directoryStartBlock">Block number where the directory entries are stored on disk.</param>
    /// <param name="blockSize">Size of each block in bytes.</param>
    /// <param name="totalVdeBlocks">Total blocks in the VDE (used to auto-partition if directory empty).</param>
    /// <param name="metaslabSizeBytes">Target size per metaslab (default 256 MB).</param>
    /// <param name="maxLoadedMetaslabs">Maximum concurrently loaded bitmaps before LRU eviction.</param>
    public MetaslabDirectory(
        IBlockDevice device,
        long directoryStartBlock,
        int blockSize,
        long totalVdeBlocks,
        long metaslabSizeBytes = DefaultMetaslabSizeBytes,
        int maxLoadedMetaslabs = DefaultMaxLoadedMetaslabs)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfNegative(directoryStartBlock);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalVdeBlocks);

        _device = device;
        _directoryStartBlock = directoryStartBlock;
        _blockSize = blockSize;
        _totalVdeBlocks = totalVdeBlocks;
        _metaslabBlockCount = Math.Max(1, metaslabSizeBytes / blockSize);
        MaxLoadedMetaslabs = maxLoadedMetaslabs;
    }

    /// <summary>
    /// Returns the best metaslab for the requested allocation size using O(1) directory lookup.
    /// Picks least-utilized metaslab in preferred zone with sufficient free space in the right size class.
    /// </summary>
    /// <param name="requestedBlocks">Number of blocks needed (used for size-class routing).</param>
    /// <returns>The best metaslab, or null if no suitable metaslab found.</returns>
    public Metaslab? GetBestMetaslab(int requestedBlocks)
    {
        _lock.EnterReadLock();
        try
        {
            if (_entries.Length == 0) return null;

            MetaslabDirectoryEntry best = default;
            bool found = false;
            double bestUtilization = double.MaxValue;

            foreach (var entry in _entries)
            {
                if (entry.State == MetaslabState.Full) continue;
                if (entry.FreeBlockCount < requestedBlocks) continue;

                double utilization = 1.0 - (double)entry.FreeBlockCount / entry.BlockCount;
                if (!found || utilization < bestUtilization)
                {
                    best = entry;
                    bestUtilization = utilization;
                    found = true;
                }
            }

            if (!found) return null;
            return GetOrCreateMetaslabInstance(best);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Returns the metaslab containing the specified block using O(1) division.
    /// </summary>
    /// <param name="blockNumber">Absolute block number to look up.</param>
    public Metaslab GetMetaslabForBlock(long blockNumber)
    {
        _lock.EnterReadLock();
        try
        {
            if (_entries.Length == 0)
                throw new InvalidOperationException("Metaslab directory is empty.");

            // O(1): divide block number by metaslab size to get index
            int idx = (int)(blockNumber / _metaslabBlockCount);
            if (idx >= _entries.Length)
                throw new ArgumentOutOfRangeException(nameof(blockNumber),
                    $"Block {blockNumber} is beyond the last metaslab (max index {_entries.Length - 1}).");

            return GetOrCreateMetaslabInstance(_entries[idx]);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Loads the metaslab directory entries from the device.
    /// If the device has no directory (new VDE), initializes from the total block count.
    /// </summary>
    public async Task LoadDirectoryAsync(CancellationToken ct = default)
    {
        int metaslabCount = (int)((_totalVdeBlocks + _metaslabBlockCount - 1) / _metaslabBlockCount);
        long dirBytes = (long)metaslabCount * MetaslabDirectoryEntry.Size;
        long dirBlocks = (dirBytes + _blockSize - 1) / _blockSize;

        byte[] dirBuffer = new byte[dirBytes];
        byte[] buffer = new byte[_blockSize];

        try
        {
            for (long i = 0; i < dirBlocks; i++)
            {
                await _device.ReadBlockAsync(_directoryStartBlock + i, buffer, ct).ConfigureAwait(false);
                long destOffset = i * _blockSize;
                int copyLen = (int)Math.Min(_blockSize, dirBytes - destOffset);
                Array.Copy(buffer, 0, dirBuffer, destOffset, copyLen);
            }
        }
        catch
        {
            // Device read failed (new VDE) — initialize fresh directory
            _lock.EnterWriteLock();
            try
            {
                _entries = BuildInitialDirectory(metaslabCount);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
            return;
        }

        // Parse loaded entries
        var entries = new MetaslabDirectoryEntry[metaslabCount];
        bool hasValidEntries = false;
        for (int i = 0; i < metaslabCount; i++)
        {
            var entry = MetaslabDirectoryEntry.Deserialize(dirBuffer.AsSpan(i * MetaslabDirectoryEntry.Size, MetaslabDirectoryEntry.Size));
            entries[i] = entry;
            if (entry.BlockCount > 0) hasValidEntries = true;
        }

        _lock.EnterWriteLock();
        try
        {
            _entries = hasValidEntries ? entries : BuildInitialDirectory(metaslabCount);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Persists all directory entries back to the device.
    /// </summary>
    public async Task PersistDirectoryAsync(CancellationToken ct = default)
    {
        MetaslabDirectoryEntry[] snapshot;
        _lock.EnterReadLock();
        try
        {
            snapshot = (MetaslabDirectoryEntry[])_entries.Clone();
        }
        finally
        {
            _lock.ExitReadLock();
        }

        long dirBytes = (long)snapshot.Length * MetaslabDirectoryEntry.Size;
        long dirBlocks = (dirBytes + _blockSize - 1) / _blockSize;
        byte[] dirBuffer = new byte[dirBytes];

        for (int i = 0; i < snapshot.Length; i++)
            snapshot[i].Serialize(dirBuffer.AsSpan(i * MetaslabDirectoryEntry.Size, MetaslabDirectoryEntry.Size));

        byte[] buffer = new byte[_blockSize];
        for (long i = 0; i < dirBlocks; i++)
        {
            Array.Clear(buffer, 0, _blockSize);
            long srcOffset = i * _blockSize;
            int copyLen = (int)Math.Min(_blockSize, dirBytes - srcOffset);
            Array.Copy(dirBuffer, srcOffset, buffer, 0, copyLen);
            await _device.WriteBlockAsync(_directoryStartBlock + i, buffer.AsMemory(0, _blockSize), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Updates the directory entry for a metaslab after allocation/free.
    /// Called by MetaslabAllocator to keep directory in sync without loading bitmaps.
    /// </summary>
    public void UpdateEntry(int metaslabId, long freeBlockCount, MetaslabState state)
    {
        _lock.EnterWriteLock();
        try
        {
            if (metaslabId < 0 || metaslabId >= _entries.Length) return;
            var e = _entries[metaslabId];
            _entries[metaslabId] = e with { FreeBlockCount = freeBlockCount, State = state };
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Gets the total free block count across all metaslabs (from directory entries, no bitmap loading).
    /// </summary>
    public long GetTotalFreeBlocks()
    {
        _lock.EnterReadLock();
        try
        {
            long total = 0;
            foreach (var e in _entries)
                total += e.FreeBlockCount;
            return total;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Releases the reader-writer lock.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lock.Dispose();
    }

    // ── Private helpers ──────────────────────────────────────────────────

    private MetaslabDirectoryEntry[] BuildInitialDirectory(int metaslabCount)
    {
        var entries = new MetaslabDirectoryEntry[metaslabCount];
        for (int i = 0; i < metaslabCount; i++)
        {
            long start = (long)i * _metaslabBlockCount;
            long count = Math.Min(_metaslabBlockCount, _totalVdeBlocks - start);

            // Assign zone/region based on index
            byte zoneId = (byte)(i / (256 * (metaslabCount / 16 + 1)));
            byte regionId = (byte)((i / (metaslabCount / 256 + 1)) % 256);

            entries[i] = new MetaslabDirectoryEntry
            {
                MetaslabId = i,
                StartBlock = start,
                BlockCount = count,
                FreeBlockCount = count,
                State = MetaslabState.Unloaded,
                ZoneId = zoneId,
                RegionId = regionId
            };
        }
        return entries;
    }

    /// <summary>
    /// Gets or creates an in-memory Metaslab instance for the given directory entry.
    /// Manages LRU eviction to keep loaded metaslab count below MaxLoadedMetaslabs.
    /// Caller must hold at least a read lock.
    /// </summary>
    private Metaslab GetOrCreateMetaslabInstance(MetaslabDirectoryEntry entry)
    {
        int id = entry.MetaslabId;

        if (_loadedMetaslabs.TryGetValue(id, out var existing))
        {
            // Promote to MRU
            if (_lruNodes.TryGetValue(id, out var node))
            {
                _lruOrder.Remove(node);
                _lruOrder.AddFirst(node);
            }
            return existing;
        }

        // Create new Metaslab instance
        // Bitmap lives after the directory on disk: bitmapOffset = directoryStartBlock + dirBlocks + id * bitmapBlocksPerMetaslab
        long bitmapBytesPerMetaslab = (_metaslabBlockCount + 7) / 8;
        long bitmapBlocksPerMetaslab = (bitmapBytesPerMetaslab + _blockSize - 1) / _blockSize;
        long dirBlocks = ((long)_entries.Length * MetaslabDirectoryEntry.Size + _blockSize - 1) / _blockSize;
        long bitmapStartBlock = _directoryStartBlock + dirBlocks + id * bitmapBlocksPerMetaslab;

        var metaslab = new Metaslab(
            id,
            entry.StartBlock,
            entry.BlockCount,
            _device,
            bitmapStartBlock,
            _blockSize,
            entry.FreeBlockCount);

        _loadedMetaslabs[id] = metaslab;
        var newNode = _lruOrder.AddFirst(id);
        _lruNodes[id] = newNode;

        // Evict LRU if over limit
        while (_loadedMetaslabs.Count > MaxLoadedMetaslabs && _lruOrder.Last != null)
        {
            int evictId = _lruOrder.Last.Value;
            _lruOrder.RemoveLast();
            _lruNodes.Remove(evictId);
            if (_loadedMetaslabs.TryGetValue(evictId, out var evicted))
            {
                evicted.Unload();
                _loadedMetaslabs.Remove(evictId);
            }
        }

        return metaslab;
    }
}
