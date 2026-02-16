using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;
using DataWarehouse.SDK.VirtualDiskEngine.Index;
using DataWarehouse.SDK.VirtualDiskEngine.Integrity;
using DataWarehouse.SDK.VirtualDiskEngine.Journal;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.CopyOnWrite;

/// <summary>
/// Manages Copy-on-Write operations with reference-counted blocks.
/// </summary>
/// <remarks>
/// Reference counting strategy:
/// - Blocks with refCount == 1 (default) are NOT stored in the B-Tree (space optimization).
/// - Only blocks with refCount != 1 are tracked in the B-Tree.
/// - All operations are WAL-protected for crash safety.
/// - Key format: block number as 8-byte big-endian (for correct B-Tree ordering).
/// - Value format: reference count as 8-byte little-endian long.
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 33: VDE CoW engine (VDE-06)")]
public sealed class CowBlockManager : ICowEngine
{
    private readonly IBlockDevice _device;
    private readonly IBlockAllocator _allocator;
    private readonly IBTreeIndex _refCountTree;
    private readonly IWriteAheadLog _wal;
    private readonly IBlockChecksummer _checksummer;

    /// <summary>
    /// Default reference count for blocks not tracked in the B-Tree.
    /// </summary>
    private const int DefaultRefCount = 1;

    /// <summary>
    /// Initializes a new instance of the <see cref="CowBlockManager"/> class.
    /// </summary>
    /// <param name="device">Block device for reading/writing blocks.</param>
    /// <param name="allocator">Block allocator for allocating new blocks.</param>
    /// <param name="refCountTree">B-Tree index for storing reference counts (key=block number, value=refCount).</param>
    /// <param name="wal">Write-ahead log for transaction safety.</param>
    /// <param name="checksummer">Block checksummer for integrity verification.</param>
    public CowBlockManager(
        IBlockDevice device,
        IBlockAllocator allocator,
        IBTreeIndex refCountTree,
        IWriteAheadLog wal,
        IBlockChecksummer checksummer)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _refCountTree = refCountTree ?? throw new ArgumentNullException(nameof(refCountTree));
        _wal = wal ?? throw new ArgumentNullException(nameof(wal));
        _checksummer = checksummer ?? throw new ArgumentNullException(nameof(checksummer));
    }

    /// <inheritdoc/>
    public async Task<long> WriteBlockCowAsync(long originalBlockNumber, ReadOnlyMemory<byte> newData, CancellationToken ct = default)
    {
        if (newData.Length != _device.BlockSize)
        {
            throw new ArgumentException($"Data must be exactly {_device.BlockSize} bytes.", nameof(newData));
        }

        // Begin WAL transaction
        await using var txn = await _wal.BeginTransactionAsync(ct);

        // Get current reference count
        int refCount = await GetRefCountInternalAsync(originalBlockNumber, ct);

        long resultBlockNumber;

        if (refCount == 1)
        {
            // Exclusive ownership: write in-place
            // Read before-image for WAL
            byte[] beforeImage = ArrayPool<byte>.Shared.Rent(_device.BlockSize);
            try
            {
                await _device.ReadBlockAsync(originalBlockNumber, beforeImage.AsMemory(0, _device.BlockSize), ct);

                // Log the write to WAL
                await txn.LogBlockWriteAsync(
                    originalBlockNumber,
                    beforeImage.AsMemory(0, _device.BlockSize),
                    newData,
                    ct);

                // Write in-place
                await _device.WriteBlockAsync(originalBlockNumber, newData, ct);

                // Update checksum
                ulong checksum = _checksummer.ComputeChecksum(newData.Span);
                await _checksummer.StoreChecksumAsync(originalBlockNumber, checksum, ct);

                resultBlockNumber = originalBlockNumber;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(beforeImage);
            }
        }
        else
        {
            // Shared block: allocate new block, copy-on-write
            long newBlockNumber = _allocator.AllocateBlock(ct);

            // Read before-image (empty for newly allocated block)
            byte[] emptyBlock = ArrayPool<byte>.Shared.Rent(_device.BlockSize);
            try
            {
                Array.Clear(emptyBlock, 0, _device.BlockSize);

                // Log the write to WAL
                await txn.LogBlockWriteAsync(
                    newBlockNumber,
                    emptyBlock.AsMemory(0, _device.BlockSize),
                    newData,
                    ct);

                // Write to new block
                await _device.WriteBlockAsync(newBlockNumber, newData, ct);

                // Update checksum for new block
                ulong checksum = _checksummer.ComputeChecksum(newData.Span);
                await _checksummer.StoreChecksumAsync(newBlockNumber, checksum, ct);

                // Decrement reference count on original block
                await DecrementRefInternalAsync(originalBlockNumber, txn, ct);

                resultBlockNumber = newBlockNumber;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(emptyBlock);
            }
        }

        // Commit transaction
        await txn.CommitAsync(ct);

        return resultBlockNumber;
    }

    /// <inheritdoc/>
    public async Task IncrementRefAsync(long blockNumber, CancellationToken ct = default)
    {
        await using var txn = await _wal.BeginTransactionAsync(ct);
        await IncrementRefInternalAsync(blockNumber, txn, ct);
        await txn.CommitAsync(ct);
    }

    /// <inheritdoc/>
    public async Task DecrementRefAsync(long blockNumber, CancellationToken ct = default)
    {
        await using var txn = await _wal.BeginTransactionAsync(ct);
        await DecrementRefInternalAsync(blockNumber, txn, ct);
        await txn.CommitAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<int> GetRefCountAsync(long blockNumber, CancellationToken ct = default)
    {
        return await GetRefCountInternalAsync(blockNumber, ct);
    }

    /// <inheritdoc/>
    public async Task IncrementRefBatchAsync(IEnumerable<long> blockNumbers, CancellationToken ct = default)
    {
        await using var txn = await _wal.BeginTransactionAsync(ct);

        foreach (long blockNumber in blockNumbers)
        {
            await IncrementRefInternalAsync(blockNumber, txn, ct);
        }

        await txn.CommitAsync(ct);
    }

    /// <inheritdoc/>
    public async Task DecrementRefBatchAsync(IEnumerable<long> blockNumbers, CancellationToken ct = default)
    {
        await using var txn = await _wal.BeginTransactionAsync(ct);

        foreach (long blockNumber in blockNumbers)
        {
            await DecrementRefInternalAsync(blockNumber, txn, ct);
        }

        await txn.CommitAsync(ct);
    }

    /// <summary>
    /// Internal method to get reference count (default 1 if not in B-Tree).
    /// </summary>
    private async Task<int> GetRefCountInternalAsync(long blockNumber, CancellationToken ct)
    {
        byte[] key = EncodeBlockNumberKey(blockNumber);
        long? value = await _refCountTree.LookupAsync(key, ct);

        if (value == null)
        {
            return DefaultRefCount; // Not tracked = default refCount of 1
        }

        return (int)value.Value;
    }

    /// <summary>
    /// Internal method to increment reference count (with active transaction).
    /// </summary>
    private async Task IncrementRefInternalAsync(long blockNumber, WalTransaction txn, CancellationToken ct)
    {
        byte[] key = EncodeBlockNumberKey(blockNumber);
        int currentRefCount = await GetRefCountInternalAsync(blockNumber, ct);
        int newRefCount = currentRefCount + 1;

        if (currentRefCount == 1)
        {
            // Transitioning from default (1) to 2: insert into B-Tree
            await _refCountTree.InsertAsync(key, newRefCount, ct);
        }
        else
        {
            // Already tracked: update
            await _refCountTree.UpdateAsync(key, newRefCount, ct);
        }
    }

    /// <summary>
    /// Internal method to decrement reference count (with active transaction).
    /// </summary>
    private async Task DecrementRefInternalAsync(long blockNumber, WalTransaction txn, CancellationToken ct)
    {
        byte[] key = EncodeBlockNumberKey(blockNumber);
        int currentRefCount = await GetRefCountInternalAsync(blockNumber, ct);

        if (currentRefCount <= 0)
        {
            throw new InvalidOperationException($"Block {blockNumber} already has refCount 0 or negative.");
        }

        int newRefCount = currentRefCount - 1;

        if (newRefCount == 0)
        {
            // Reference count reached 0: free the block
            // Read before-image for WAL
            byte[] beforeImage = ArrayPool<byte>.Shared.Rent(_device.BlockSize);
            try
            {
                await _device.ReadBlockAsync(blockNumber, beforeImage.AsMemory(0, _device.BlockSize), ct);

                // Log block free to WAL
                await txn.LogBlockFreeAsync(blockNumber, beforeImage.AsMemory(0, _device.BlockSize), ct);

                // Free the block
                _allocator.FreeBlock(blockNumber);

                // Remove from B-Tree if present
                await _refCountTree.DeleteAsync(key, ct);

                // Invalidate checksum cache
                _checksummer.InvalidateCacheEntry(blockNumber);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(beforeImage);
            }
        }
        else if (newRefCount == 1)
        {
            // Transitioning back to default (1): remove from B-Tree
            await _refCountTree.DeleteAsync(key, ct);
        }
        else
        {
            // Still shared: update B-Tree
            await _refCountTree.UpdateAsync(key, newRefCount, ct);
        }
    }

    /// <summary>
    /// Encodes a block number as a B-Tree key (8-byte big-endian for correct ordering).
    /// </summary>
    private static byte[] EncodeBlockNumberKey(long blockNumber)
    {
        byte[] key = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(key, blockNumber);
        return key;
    }
}
