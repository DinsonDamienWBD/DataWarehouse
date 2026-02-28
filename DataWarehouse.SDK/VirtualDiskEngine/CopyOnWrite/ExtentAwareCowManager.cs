using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;
using DataWarehouse.SDK.VirtualDiskEngine.Format;
using DataWarehouse.SDK.VirtualDiskEngine.Journal;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.CopyOnWrite;

/// <summary>
/// Manages Copy-on-Write operations at extent granularity instead of block granularity.
/// A 1TB file with 256K blocks requires 256K reference counts per block; with 64 extents,
/// only 64 reference counts are needed -- a 4000x reduction in snapshot metadata.
/// </summary>
/// <remarks>
/// Extent-level reference counting:
/// - Keyed by extent start block number.
/// - Default ref count is 1 (not tracked in dictionary).
/// - Shared extents marked with <see cref="ExtentFlags.SharedCow"/>.
/// - Partial-extent CoW splits into up to 3 sub-extents to minimize copying.
/// - All mutations are WAL-journaled for crash safety.
/// - Ref count table serialized to SNAP region, loaded on VDE open.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Extent-aware CoW snapshots (VOPT-26)")]
public sealed class ExtentAwareCowManager
{
    private readonly IBlockDevice _device;
    private readonly IBlockAllocator _allocator;
    private readonly IWriteAheadLog _wal;
    private readonly int _blockSize;

    /// <summary>
    /// Extent-level reference counts keyed by extent start block.
    /// Only extents with refCount != 1 are tracked (1 is the implicit default).
    /// </summary>
    private readonly ConcurrentDictionary<long, int> _extentRefCounts = new();

    /// <summary>Default reference count for extents not tracked in the dictionary.</summary>
    private const int DefaultRefCount = 1;

    /// <summary>
    /// Creates a new extent-aware CoW manager.
    /// </summary>
    /// <param name="device">Block device for reading/writing blocks.</param>
    /// <param name="allocator">Block allocator for allocating new extents.</param>
    /// <param name="wal">Write-ahead log for crash-safe mutations.</param>
    /// <param name="blockSize">Block size in bytes (e.g., 4096).</param>
    public ExtentAwareCowManager(
        IBlockDevice device,
        IBlockAllocator allocator,
        IWriteAheadLog wal,
        int blockSize)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _wal = wal ?? throw new ArgumentNullException(nameof(wal));
        _blockSize = blockSize > 0 ? blockSize : throw new ArgumentOutOfRangeException(nameof(blockSize));
    }

    // ── Reference counting ──────────────────────────────────────────────

    /// <summary>
    /// Increments the reference count for an extent (called when extent is shared by a snapshot).
    /// </summary>
    /// <param name="extent">The extent whose reference count to increment.</param>
    public void IncrementRef(InodeExtent extent)
    {
        _extentRefCounts.AddOrUpdate(
            extent.StartBlock,
            static (_, _) => DefaultRefCount + 1,
            static (_, current, _) => current + 1,
            (object?)null);
    }

    /// <summary>
    /// Decrements the reference count for an extent. If the count reaches 0, the extent
    /// blocks can be freed.
    /// </summary>
    /// <param name="extent">The extent whose reference count to decrement.</param>
    public void DecrementRef(InodeExtent extent)
    {
        int newCount = _extentRefCounts.AddOrUpdate(
            extent.StartBlock,
            static (_, _) => 0, // Should not normally happen (decrement from default 1 -> 0)
            static (_, current, _) => current - 1,
            (object?)null);

        if (newCount <= 0)
        {
            // Ref count hit zero — keep it at 0 in the dictionary so GetRefCount returns 0 (freed)
            _extentRefCounts.TryUpdate(extent.StartBlock, 0, newCount);
        }
    }

    /// <summary>
    /// Gets the current reference count for an extent (default 1 if not tracked).
    /// </summary>
    /// <param name="extent">The extent to query.</param>
    /// <returns>The reference count (>= 0).</returns>
    public int GetRefCount(InodeExtent extent)
    {
        return _extentRefCounts.TryGetValue(extent.StartBlock, out int count) ? count : DefaultRefCount;
    }

    // ── Snapshot creation ───────────────────────────────────────────────

    /// <summary>
    /// Creates an extent-aware snapshot of a source inode. The new inode points to the
    /// same extents as the source; all extents are marked SharedCow and their reference
    /// counts are incremented.
    /// </summary>
    /// <param name="sourceInodeNumber">Inode number of the source file.</param>
    /// <param name="sourceInode">The source inode metadata.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The snapshot inode number.</returns>
    public async Task<long> CreateSnapshotAsync(
        long sourceInodeNumber,
        InodeV2 sourceInode,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourceInode);

        await using var txn = await _wal.BeginTransactionAsync(ct);

        // Allocate a new inode number for the snapshot
        long snapshotInodeNumber = _allocator.AllocateBlock(ct);

        // Process each extent: mark as SharedCow and increment ref count
        var snapshotExtents = new InodeExtent[FormatConstants.MaxExtentsPerInode];

        for (int i = 0; i < sourceInode.ExtentCount; i++)
        {
            InodeExtent extent = sourceInode.Extents[i];
            if (extent.IsEmpty) continue;

            // Mark extent as shared (set SharedCow flag)
            var sharedExtent = new InodeExtent(
                extent.StartBlock,
                extent.BlockCount,
                extent.Flags | ExtentFlags.SharedCow,
                extent.LogicalOffset);

            snapshotExtents[i] = sharedExtent;

            // Also update source inode extent to SharedCow
            sourceInode.Extents[i] = sharedExtent;

            // Increment reference count (both source and snapshot share this extent)
            IncrementRef(extent);
        }

        // Log snapshot creation to WAL for crash safety
        var logEntry = new JournalEntry
        {
            SequenceNumber = -1,
            TransactionId = txn.TransactionId,
            Type = JournalEntryType.Checkpoint,
            TargetBlockNumber = snapshotInodeNumber,
            BeforeImage = null,
            AfterImage = null
        };
        await _wal.AppendEntryAsync(logEntry, ct);

        await txn.CommitAsync(ct);

        return snapshotInodeNumber;
    }

    // ── Copy-on-Write ───────────────────────────────────────────────────

    /// <summary>
    /// Performs extent-level copy-on-write. If the extent is shared (ref count > 1),
    /// allocates new blocks, copies existing data, applies the write, and decrements
    /// the original extent's ref count. If the extent is not shared, writes directly.
    /// </summary>
    /// <param name="sharedExtent">The extent to write to (may be shared).</param>
    /// <param name="writeOffset">Byte offset within the extent where the write starts.</param>
    /// <param name="newData">Data to write.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The resulting extent (new if copied, same if not shared). Partial-extent CoW
    /// may split into up to 3 extents returned as the primary modified extent.
    /// </returns>
    public async Task<InodeExtent> CopyOnWriteAsync(
        InodeExtent sharedExtent,
        long writeOffset,
        ReadOnlyMemory<byte> newData,
        CancellationToken ct = default)
    {
        int refCount = GetRefCount(sharedExtent);

        if (refCount <= 1)
        {
            // Not shared: write directly to existing blocks
            await WriteToExtentAsync(sharedExtent, writeOffset, newData, ct);
            return sharedExtent;
        }

        // Shared extent: must copy-on-write
        await using var txn = await _wal.BeginTransactionAsync(ct);

        long extentByteSize = (long)sharedExtent.BlockCount * _blockSize;
        long writeEndOffset = writeOffset + newData.Length;

        // Determine if partial extent CoW applies
        bool isPartialWrite = writeOffset > 0 || writeEndOffset < extentByteSize;

        if (isPartialWrite && sharedExtent.BlockCount > 1)
        {
            // Partial extent CoW: split into up to 3 sub-extents
            return await PartialExtentCowAsync(sharedExtent, writeOffset, newData, txn, ct);
        }

        // Full extent copy: allocate new blocks, copy data, apply write
        long[] newBlocks = _allocator.AllocateExtent(sharedExtent.BlockCount, ct);

        // Copy existing data from shared extent to new blocks
        byte[] buffer = ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            for (int i = 0; i < sharedExtent.BlockCount; i++)
            {
                await _device.ReadBlockAsync(sharedExtent.StartBlock + i, buffer.AsMemory(0, _blockSize), ct);
                await _device.WriteBlockAsync(newBlocks[i], buffer.AsMemory(0, _blockSize), ct);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // Apply the write to the new blocks
        var newExtent = new InodeExtent(
            newBlocks[0],
            sharedExtent.BlockCount,
            sharedExtent.Flags & ~ExtentFlags.SharedCow,
            sharedExtent.LogicalOffset);

        await WriteToExtentAsync(newExtent, writeOffset, newData, ct);

        // Decrement ref on original extent
        DecrementRef(sharedExtent);

        await txn.CommitAsync(ct);

        return newExtent;
    }

    /// <summary>
    /// Performs partial-extent CoW by splitting into up to 3 sub-extents:
    /// [before][modified][after] to minimize data copying.
    /// </summary>
    private async Task<InodeExtent> PartialExtentCowAsync(
        InodeExtent sharedExtent,
        long writeOffset,
        ReadOnlyMemory<byte> newData,
        WalTransaction txn,
        CancellationToken ct)
    {
        int startBlockOffset = (int)(writeOffset / _blockSize);
        int endBlockOffset = (int)((writeOffset + newData.Length + _blockSize - 1) / _blockSize);
        int modifiedBlockCount = endBlockOffset - startBlockOffset;

        if (modifiedBlockCount < 1) modifiedBlockCount = 1;
        if (modifiedBlockCount > sharedExtent.BlockCount) modifiedBlockCount = sharedExtent.BlockCount;

        // Allocate new blocks only for the modified portion
        long[] newBlocks = _allocator.AllocateExtent(modifiedBlockCount, ct);

        // Copy the affected blocks from the shared extent
        byte[] buffer = ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            for (int i = 0; i < modifiedBlockCount; i++)
            {
                long sourceBlock = sharedExtent.StartBlock + startBlockOffset + i;
                await _device.ReadBlockAsync(sourceBlock, buffer.AsMemory(0, _blockSize), ct);
                await _device.WriteBlockAsync(newBlocks[i], buffer.AsMemory(0, _blockSize), ct);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // Create the new modified extent
        long modifiedLogicalOffset = sharedExtent.LogicalOffset + (long)startBlockOffset * _blockSize;
        var modifiedExtent = new InodeExtent(
            newBlocks[0],
            modifiedBlockCount,
            sharedExtent.Flags & ~ExtentFlags.SharedCow,
            modifiedLogicalOffset);

        // Apply the write to the new blocks (adjust offset relative to new extent)
        long adjustedOffset = writeOffset - (long)startBlockOffset * _blockSize;
        await WriteToExtentAsync(modifiedExtent, adjustedOffset, newData, ct);

        // Decrement ref on original extent
        DecrementRef(sharedExtent);

        await txn.CommitAsync(ct);

        return modifiedExtent;
    }

    // ── Snapshot deletion ───────────────────────────────────────────────

    /// <summary>
    /// Deletes a snapshot by decrementing reference counts on all its extents.
    /// Extents whose ref count drops to 0 have their blocks freed.
    /// </summary>
    /// <param name="snapshotInodeNumber">Inode number of the snapshot to delete.</param>
    /// <param name="snapshotInode">The snapshot's inode metadata.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task DeleteSnapshotAsync(
        long snapshotInodeNumber,
        InodeV2 snapshotInode,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshotInode);

        await using var txn = await _wal.BeginTransactionAsync(ct);

        for (int i = 0; i < snapshotInode.ExtentCount; i++)
        {
            InodeExtent extent = snapshotInode.Extents[i];
            if (extent.IsEmpty) continue;

            DecrementRef(extent);

            int refCount = GetRefCount(extent);
            if (refCount <= 0)
            {
                // Free all blocks in this extent
                _allocator.FreeExtent(extent.StartBlock, extent.BlockCount);
            }
        }

        // Free the snapshot inode block
        _allocator.FreeBlock(snapshotInodeNumber);

        // Log deletion to WAL
        var logEntry = new JournalEntry
        {
            SequenceNumber = -1,
            TransactionId = txn.TransactionId,
            Type = JournalEntryType.Checkpoint,
            TargetBlockNumber = snapshotInodeNumber,
            BeforeImage = null,
            AfterImage = null
        };
        await _wal.AppendEntryAsync(logEntry, ct);

        await txn.CommitAsync(ct);
    }

    // ── Persistence ─────────────────────────────────────────────────────

    /// <summary>
    /// Serializes the extent ref count table for persistence to the SNAP region.
    /// Format: [entryCount:4][startBlock:8 + refCount:4]...
    /// </summary>
    /// <returns>Serialized ref count table bytes.</returns>
    public byte[] SerializeRefCounts()
    {
        var entries = new List<KeyValuePair<long, int>>(_extentRefCounts);
        int size = 4 + entries.Count * 12; // 4-byte count + 12 bytes per entry
        byte[] data = new byte[size];

        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0, 4), entries.Count);

        int offset = 4;
        foreach (var entry in entries)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(offset, 8), entry.Key);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset + 8, 4), entry.Value);
            offset += 12;
        }

        return data;
    }

    /// <summary>
    /// Deserializes the extent ref count table from SNAP region data, restoring state on VDE open.
    /// </summary>
    /// <param name="data">Serialized ref count table bytes.</param>
    public void DeserializeRefCounts(ReadOnlySpan<byte> data)
    {
        _extentRefCounts.Clear();

        if (data.Length < 4) return;

        int entryCount = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data[..4]);
        int offset = 4;

        for (int i = 0; i < entryCount && offset + 12 <= data.Length; i++)
        {
            long startBlock = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset, 8));
            int refCount = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset + 8, 4));

            if (refCount > 1)
            {
                _extentRefCounts.TryAdd(startBlock, refCount);
            }

            offset += 12;
        }
    }

    /// <summary>
    /// Gets the number of extents currently tracked with non-default reference counts.
    /// </summary>
    public int TrackedExtentCount => _extentRefCounts.Count;

    // ── Internal helpers ────────────────────────────────────────────────

    /// <summary>
    /// Writes data to an extent at the specified byte offset.
    /// </summary>
    private async Task WriteToExtentAsync(
        InodeExtent extent,
        long writeOffset,
        ReadOnlyMemory<byte> data,
        CancellationToken ct)
    {
        int startBlock = (int)(writeOffset / _blockSize);
        int blockOffset = (int)(writeOffset % _blockSize);
        int remaining = data.Length;
        int dataOffset = 0;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            while (remaining > 0 && startBlock < extent.BlockCount)
            {
                long blockNumber = extent.StartBlock + startBlock;

                // Read current block content
                await _device.ReadBlockAsync(blockNumber, buffer.AsMemory(0, _blockSize), ct);

                // Overwrite the relevant portion
                int bytesToWrite = Math.Min(remaining, _blockSize - blockOffset);
                data.Span.Slice(dataOffset, bytesToWrite).CopyTo(buffer.AsSpan(blockOffset, bytesToWrite));

                // Write back
                await _device.WriteBlockAsync(blockNumber, buffer.AsMemory(0, _blockSize), ct);

                remaining -= bytesToWrite;
                dataOffset += bytesToWrite;
                startBlock++;
                blockOffset = 0; // Subsequent blocks start at offset 0
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
