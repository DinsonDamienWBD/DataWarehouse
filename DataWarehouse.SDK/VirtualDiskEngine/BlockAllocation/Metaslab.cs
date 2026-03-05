using DataWarehouse.SDK.Contracts;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;

/// <summary>
/// Metaslab state for memory management and eviction decisions.
/// </summary>
public enum MetaslabState : byte
{
    /// <summary>Bitmap not loaded — O(1) directory entry only.</summary>
    Unloaded = 0,
    /// <summary>Bitmap loaded and actively being used for allocation.</summary>
    Active = 1,
    /// <summary>Metaslab is completely full — no free blocks available.</summary>
    Full = 2,
    /// <summary>Free space is highly fragmented — defrag candidate.</summary>
    Fragmented = 3,
    /// <summary>Loaded but not recently accessed — eligible for bitmap eviction.</summary>
    EvictionCandidate = 4
}

/// <summary>
/// Individual metaslab with lazy bitmap loading and size-class segregated free lists.
/// A metaslab manages a fixed-size partition of the VDE address space. Its bitmap
/// is loaded lazily from the device on first access, enabling O(log M) allocation
/// at petabyte scale where only active metaslabs consume memory.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87-18: Hierarchical metaslab allocator (VOPT-31)")]
public sealed class Metaslab
{
    // Size class boundaries (in blocks):
    // Small:  1-16 blocks   (4KB-64KB at 4KB block size)
    // Medium: 17-256 blocks (64KB-1MB)
    // Large:  257+ blocks   (1MB+)
    private const int SmallMaxBlocks = 16;
    private const int MediumMaxBlocks = 256;

    private readonly int _metaslabId;
    private readonly IBlockDevice _device;
    private readonly long _bitmapBlockOffset;
    private readonly int _blockSize;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

    // Lazy-loaded bitmap: null until first allocation request
    private byte[]? _bitmap;

    // Free lists per size class, sorted by start block for coalescing
    private readonly SortedList<long, int> _smallFreeList = new();   // startBlock -> runLength (1-16)
    private readonly SortedList<long, int> _mediumFreeList = new();  // startBlock -> runLength (17-256)
    private readonly SortedList<long, int> _largeFreeList = new();   // startBlock -> runLength (257+)

    private long _freeBlockCount;
    private MetaslabState _state;
    private long _lastAccessTicks;
    private bool _bitmapDirty;

    /// <summary>Gets the unique identifier of this metaslab.</summary>
    public int MetaslabId => _metaslabId;

    /// <summary>Gets the absolute block number where this metaslab starts.</summary>
    public long StartBlock { get; }

    /// <summary>Gets the total number of blocks managed by this metaslab.</summary>
    public long BlockCount { get; }

    /// <summary>Gets whether the bitmap is currently loaded into memory.</summary>
    public bool IsLoaded => _bitmap != null;

    /// <summary>
    /// Gets the number of free blocks in this metaslab.
    /// Maintained without loading bitmap via directory entry tracking.
    /// </summary>
    public long FreeBlockCount
    {
        get
        {
            _lock.EnterReadLock();
            try { return _freeBlockCount; }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>Gets the utilization ratio (0.0 = empty, 1.0 = full).</summary>
    public double Utilization => 1.0 - (double)FreeBlockCount / BlockCount;

    /// <summary>Gets the current state of this metaslab.</summary>
    public MetaslabState State
    {
        get
        {
            _lock.EnterReadLock();
            try { return _state; }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>Gets the last access time ticks for LRU eviction decisions.</summary>
    public long LastAccessTicks => Interlocked.Read(ref _lastAccessTicks);

    /// <summary>
    /// Creates a new metaslab with all blocks initially free.
    /// </summary>
    /// <param name="metaslabId">Unique metaslab identifier within the directory.</param>
    /// <param name="startBlock">Absolute block number where this metaslab starts.</param>
    /// <param name="blockCount">Number of blocks in this metaslab.</param>
    /// <param name="device">Block device for lazy bitmap loading and persistence.</param>
    /// <param name="bitmapBlockOffset">Block offset on the device where this metaslab's bitmap is stored.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="freeBlockCount">Initial free block count from directory entry (avoids loading bitmap).</param>
    public Metaslab(
        int metaslabId,
        long startBlock,
        long blockCount,
        IBlockDevice device,
        long bitmapBlockOffset,
        int blockSize,
        long freeBlockCount = -1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(metaslabId);
        ArgumentOutOfRangeException.ThrowIfNegative(startBlock);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockCount);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize);

        _metaslabId = metaslabId;
        StartBlock = startBlock;
        BlockCount = blockCount;
        _device = device;
        _bitmapBlockOffset = bitmapBlockOffset;
        _blockSize = blockSize;
        _freeBlockCount = freeBlockCount < 0 ? blockCount : freeBlockCount;
        _state = _freeBlockCount == 0 ? MetaslabState.Full : MetaslabState.Unloaded;
        _lastAccessTicks = Environment.TickCount64;
    }

    /// <summary>
    /// Ensures the bitmap is loaded from the device. Called on first allocation.
    /// </summary>
    public async Task EnsureBitmapLoadedAsync(CancellationToken ct = default)
    {
        // Fast path: already loaded
        if (_bitmap != null) return;

        Interlocked.Exchange(ref _lastAccessTicks, Environment.TickCount64);

        long bitmapBytes = (BlockCount + 7) / 8;
        long bitmapBlocks = (bitmapBytes + _blockSize - 1) / _blockSize;
        byte[] bitmap = new byte[bitmapBytes];

        byte[] buffer = ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            for (long i = 0; i < bitmapBlocks; i++)
            {
                await _device.ReadBlockAsync(_bitmapBlockOffset + i, buffer, ct).ConfigureAwait(false);

                long destOffset = i * _blockSize;
                int copyLength = (int)Math.Min(_blockSize, bitmapBytes - destOffset);
                if (copyLength > 0)
                    Array.Copy(buffer, 0, bitmap, destOffset, copyLength);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        _lock.EnterWriteLock();
        try
        {
            if (_bitmap == null) // double-checked under write lock
            {
                _bitmap = bitmap;
                RebuildFreeLists(bitmap);
                _state = _freeBlockCount == 0 ? MetaslabState.Full : MetaslabState.Active;
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Allocates a single block. Loads bitmap lazily if not already loaded.
    /// Returns the absolute block number.
    /// </summary>
    public long AllocateBlock(CancellationToken ct = default)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_bitmap == null)
                throw new InvalidOperationException($"Metaslab {_metaslabId}: bitmap not loaded. Call EnsureBitmapLoadedAsync first.");

            if (_freeBlockCount == 0)
                throw new InvalidOperationException($"Metaslab {_metaslabId} is full.");

            // Try to get a single block from small free list first
            long localBlock = AllocateFromSizeClass(1);
            if (localBlock < 0)
                throw new InvalidOperationException($"Metaslab {_metaslabId}: bitmap inconsistency, free count > 0 but no free block found.");

            SetBitmapBit(localBlock, true);
            _freeBlockCount--;
            _bitmapDirty = true;
            UpdateState();
            Interlocked.Exchange(ref _lastAccessTicks, Environment.TickCount64);

            return StartBlock + localBlock;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Allocates a contiguous extent of blocks using appropriate size class.
    /// Returns the absolute block numbers as a contiguous array.
    /// </summary>
    public long[] AllocateExtent(int blockCount, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockCount);

        _lock.EnterWriteLock();
        try
        {
            if (_bitmap == null)
                throw new InvalidOperationException($"Metaslab {_metaslabId}: bitmap not loaded. Call EnsureBitmapLoadedAsync first.");

            if (_freeBlockCount < blockCount)
                throw new InvalidOperationException($"Metaslab {_metaslabId} has insufficient free blocks ({_freeBlockCount} < {blockCount}).");

            long localStart = AllocateFromSizeClass(blockCount);
            if (localStart < 0)
                throw new InvalidOperationException($"Metaslab {_metaslabId} has no contiguous extent of {blockCount} blocks.");

            // Mark all blocks as allocated in bitmap
            for (int i = 0; i < blockCount; i++)
                SetBitmapBit(localStart + i, true);

            _freeBlockCount -= blockCount;
            _bitmapDirty = true;
            UpdateState();
            Interlocked.Exchange(ref _lastAccessTicks, Environment.TickCount64);

            var result = new long[blockCount];
            for (int i = 0; i < blockCount; i++)
                result[i] = StartBlock + localStart + i;

            return result;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Frees a single block and merges with adjacent free entries in size class list.
    /// </summary>
    public void FreeBlock(long blockNumber)
    {
        long localBlock = ValidateAndGetLocal(blockNumber);

        _lock.EnterWriteLock();
        try
        {
            if (_bitmap == null)
                throw new InvalidOperationException($"Metaslab {_metaslabId}: bitmap not loaded.");

            if (!GetBitmapBit(localBlock))
                throw new InvalidOperationException($"Block {blockNumber} is already free in metaslab {_metaslabId}.");

            SetBitmapBit(localBlock, false);
            _freeBlockCount++;
            _bitmapDirty = true;
            CoalesceAndAddFreeRun(localBlock, 1);
            UpdateState();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Frees a contiguous extent of blocks and coalesces adjacent free runs.
    /// </summary>
    public void FreeExtent(long startBlock, int blockCount)
    {
        long localStart = ValidateAndGetLocal(startBlock);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockCount);

        _lock.EnterWriteLock();
        try
        {
            if (_bitmap == null)
                throw new InvalidOperationException($"Metaslab {_metaslabId}: bitmap not loaded.");

            for (int i = 0; i < blockCount; i++)
            {
                long local = localStart + i;
                if (!GetBitmapBit(local))
                    throw new InvalidOperationException($"Block {startBlock + i} is already free in metaslab {_metaslabId}.");

                SetBitmapBit(local, false);
            }

            _freeBlockCount += blockCount;
            _bitmapDirty = true;
            CoalesceAndAddFreeRun(localStart, blockCount);
            UpdateState();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Persists the dirty bitmap back to the device.
    /// </summary>
    public async Task PersistAsync(CancellationToken ct = default)
    {
        byte[]? bitmapToWrite;
        _lock.EnterReadLock();
        try
        {
            if (_bitmap == null || !_bitmapDirty) return;
            bitmapToWrite = (byte[])_bitmap.Clone();
        }
        finally
        {
            _lock.ExitReadLock();
        }

        long bitmapBytes = (BlockCount + 7) / 8;
        long bitmapBlocks = (bitmapBytes + _blockSize - 1) / _blockSize;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            for (long i = 0; i < bitmapBlocks; i++)
            {
                Array.Clear(buffer, 0, _blockSize);

                long srcOffset = i * _blockSize;
                int copyLength = (int)Math.Min(_blockSize, bitmapBytes - srcOffset);
                if (copyLength > 0)
                    Array.Copy(bitmapToWrite, srcOffset, buffer, 0, copyLength);

                await _device.WriteBlockAsync(_bitmapBlockOffset + i, buffer.AsMemory(0, _blockSize), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        _lock.EnterWriteLock();
        try { _bitmapDirty = false; }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Releases the bitmap from memory to reclaim RAM under memory pressure.
    /// Only safe when no active allocations are in flight for this metaslab.
    /// </summary>
    public void Unload()
    {
        _lock.EnterWriteLock();
        try
        {
            _bitmap = null;
            _smallFreeList.Clear();
            _mediumFreeList.Clear();
            _largeFreeList.Clear();
            _state = _freeBlockCount == 0 ? MetaslabState.Full : MetaslabState.Unloaded;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────

    private void RebuildFreeLists(byte[] bitmap)
    {
        // Clear existing lists
        _smallFreeList.Clear();
        _mediumFreeList.Clear();
        _largeFreeList.Clear();

        // Scan bitmap for free runs (bit = 0 means free in BitmapAllocator convention,
        // but for metaslabs we use bit = 0 means free consistently)
        long runStart = -1;
        long runLen = 0;

        for (long i = 0; i <= BlockCount; i++)
        {
            bool isFree = i < BlockCount && !GetBitmapBitRaw(bitmap, i);
            if (isFree)
            {
                if (runStart < 0) { runStart = i; runLen = 0; }
                runLen++;
            }
            else
            {
                if (runStart >= 0 && runLen > 0)
                    AddToFreeList(runStart, (int)Math.Min(runLen, int.MaxValue));
                runStart = -1;
                runLen = 0;
            }
        }
    }

    private void AddToFreeList(long localStart, int length)
    {
        if (length <= SmallMaxBlocks)
            _smallFreeList[localStart] = length;
        else if (length <= MediumMaxBlocks)
            _mediumFreeList[localStart] = length;
        else
            _largeFreeList[localStart] = length;
    }

    private void RemoveFromFreeLists(long localStart)
    {
        if (!_smallFreeList.Remove(localStart))
            if (!_mediumFreeList.Remove(localStart))
                _largeFreeList.Remove(localStart);
    }

    /// <summary>
    /// Allocates from the appropriate size class. Returns local block start or -1.
    /// </summary>
    private long AllocateFromSizeClass(int count)
    {
        // Pick the right list
        SortedList<long, int> list;
        if (count <= SmallMaxBlocks)
            list = _smallFreeList;
        else if (count <= MediumMaxBlocks)
            list = _mediumFreeList;
        else
            list = _largeFreeList;

        // Find best-fit within the chosen list
        long bestStart = -1;
        int bestLen = int.MaxValue;

        foreach (var kv in list)
        {
            if (kv.Value >= count && kv.Value < bestLen)
            {
                bestStart = kv.Key;
                bestLen = kv.Value;
                if (bestLen == count) break; // perfect fit
            }
        }

        // Fallback: try larger size classes
        if (bestStart < 0)
        {
            if (count <= SmallMaxBlocks)
            {
                // Try medium list
                foreach (var kv in _mediumFreeList)
                {
                    if (kv.Value >= count && kv.Value < bestLen)
                    {
                        bestStart = kv.Key; bestLen = kv.Value;
                        break;
                    }
                }
            }
            if (bestStart < 0)
            {
                // Try large list
                foreach (var kv in _largeFreeList)
                {
                    if (kv.Value >= count && kv.Value < bestLen)
                    {
                        bestStart = kv.Key; bestLen = kv.Value;
                        break;
                    }
                }
            }
        }

        if (bestStart < 0) return -1;

        // Remove the entry and re-add remainder if any
        RemoveFromFreeLists(bestStart);
        int remainder = bestLen - count;
        if (remainder > 0)
            AddToFreeList(bestStart + count, remainder);

        return bestStart;
    }

    private void CoalesceAndAddFreeRun(long localStart, int length)
    {
        // Look for adjacent free runs to merge
        long mergedStart = localStart;
        int mergedLen = length;

        // Check if there's a run ending exactly at localStart (predecessor)
        // Scan small, medium, large lists for a run ending at our start
        foreach (var list in new[] { _smallFreeList, _mediumFreeList, _largeFreeList })
        {
            // Check predecessor: a run where start + len == localStart
            // SortedList doesn't support range queries natively, iterate nearby range
            for (int i = list.Count - 1; i >= 0; i--)
            {
                long s = list.Keys[i];
                int l = list.Values[i];
                if (s + l == mergedStart)
                {
                    // Merge predecessor into our run
                    list.RemoveAt(i);
                    mergedStart = s;
                    mergedLen += l;
                    break;
                }
                if (s < mergedStart) break; // sorted, no point looking further back
            }
        }

        // Check successor: a run starting exactly at mergedStart + mergedLen
        long successorStart = mergedStart + mergedLen;
        foreach (var list in new[] { _smallFreeList, _mediumFreeList, _largeFreeList })
        {
            if (list.TryGetValue(successorStart, out int succLen))
            {
                list.Remove(successorStart);
                mergedLen += succLen;
                break;
            }
        }

        AddToFreeList(mergedStart, mergedLen);
    }

    private void UpdateState()
    {
        if (_freeBlockCount == 0)
            _state = MetaslabState.Full;
        else if (_bitmap != null)
        {
            // Check fragmentation: many small runs relative to total free space
            int totalRuns = _smallFreeList.Count + _mediumFreeList.Count + _largeFreeList.Count;
            double fragRatio = _freeBlockCount > 0 ? (double)(totalRuns - 1) / _freeBlockCount : 0;
            _state = fragRatio > 0.5 ? MetaslabState.Fragmented : MetaslabState.Active;
        }
    }

    private long ValidateAndGetLocal(long absoluteBlock)
    {
        long local = absoluteBlock - StartBlock;
        if (local < 0 || local >= BlockCount)
            throw new ArgumentOutOfRangeException(nameof(absoluteBlock),
                $"Block {absoluteBlock} is outside metaslab {_metaslabId} range [{StartBlock}..{StartBlock + BlockCount - 1}].");
        return local;
    }

    private bool GetBitmapBit(long localBlock)
    {
        return GetBitmapBitRaw(_bitmap!, localBlock);
    }

    private static bool GetBitmapBitRaw(byte[] bitmap, long localBlock)
    {
        long byteIndex = localBlock / 8;
        int bitOffset = (int)(localBlock % 8);
        return (bitmap[byteIndex] & (1 << bitOffset)) != 0;
    }

    private void SetBitmapBit(long localBlock, bool allocated)
    {
        long byteIndex = localBlock / 8;
        int bitOffset = (int)(localBlock % 8);
        if (allocated)
            _bitmap![byteIndex] |= (byte)(1 << bitOffset);
        else
            _bitmap![byteIndex] &= (byte)~(1 << bitOffset);
    }
}
