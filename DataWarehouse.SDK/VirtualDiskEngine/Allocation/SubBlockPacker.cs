using System;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Allocation;

/// <summary>
/// Packs multiple small objects into shared blocks via tail-merging, eliminating
/// internal fragmentation for objects smaller than half a block. Each shared block
/// is divided into fixed-size slots tracked by a <see cref="SubBlockBitmap"/>.
/// </summary>
/// <remarks>
/// <para>
/// Size classes: 64, 128, 256, 512, 1024, 2048 bytes. Objects are rounded up to
/// the nearest power-of-2 slot size. Objects larger than blockSize/2 are NOT
/// sub-block packed and should use normal block allocation.
/// </para>
/// <para>
/// When a shared block becomes fully free after slot releases, the block is
/// returned to the allocation group's primary bitmap for reuse.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Sub-block packing for small object tail-merging (VOPT-11)")]
public sealed class SubBlockPacker
{
    private readonly IBlockDevice _device;
    private readonly int _blockSize;
    private readonly SubBlockBitmap _bitmap;
    private readonly SemaphoreSlim _packLock = new(1, 1);

    /// <summary>
    /// Gets the maximum data size that can be sub-block packed. Objects larger
    /// than this must use normal block allocation.
    /// </summary>
    public int MaxPackableSize => _blockSize / 2;

    /// <summary>
    /// Gets the sub-block bitmap tracking slot occupancy.
    /// </summary>
    public SubBlockBitmap Bitmap => _bitmap;

    /// <summary>
    /// Creates a new sub-block packer.
    /// </summary>
    /// <param name="device">The block device for reading/writing block data.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="bitmap">The sub-block bitmap tracking slot occupancy.</param>
    /// <exception cref="ArgumentNullException">Thrown if device or bitmap is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if blockSize is invalid.</exception>
    public SubBlockPacker(IBlockDevice device, int blockSize, SubBlockBitmap bitmap)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _bitmap = bitmap ?? throw new ArgumentNullException(nameof(bitmap));

        if (blockSize <= 0 || (blockSize & (blockSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(blockSize), "Block size must be a positive power-of-2.");

        _blockSize = blockSize;
    }

    /// <summary>
    /// Packs small data into a sub-block slot within a shared block.
    /// </summary>
    /// <param name="data">The data to pack (must be &lt;= <see cref="MaxPackableSize"/> bytes).</param>
    /// <param name="allocationGroupId">
    /// The allocation group to search for shared blocks. Used for context; actual block
    /// allocation is performed via the allocation group when no existing shared block has space.
    /// </param>
    /// <param name="allocateNewBlock">
    /// Delegate to allocate a new block from the allocation group when no existing
    /// shared block has space. Returns the absolute block number.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A tuple of (BlockNumber, SlotIndex, SlotOffset) identifying the packed location.
    /// SlotOffset is the byte offset within the block where data was written.
    /// </returns>
    /// <exception cref="ArgumentException">Thrown if data exceeds <see cref="MaxPackableSize"/>.</exception>
    public async Task<(long BlockNumber, int SlotIndex, int SlotOffset)> PackAsync(
        ReadOnlyMemory<byte> data,
        int allocationGroupId,
        Func<long> allocateNewBlock,
        CancellationToken ct = default)
    {
        if (data.Length > MaxPackableSize)
            throw new ArgumentException(
                $"Data size {data.Length} exceeds maximum packable size {MaxPackableSize}. Use normal block allocation.",
                nameof(data));
        if (data.Length == 0)
            throw new ArgumentException("Data must not be empty.", nameof(data));

        int slotSize = SubBlockBitmap.RoundUpToSlotClass(data.Length);

        await _packLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Search for an existing shared block with a free slot of the right size
            foreach (long blockNumber in _bitmap.GetBlocksWithFreeSlots(slotSize))
            {
                if (_bitmap.AllocateSlot(blockNumber, slotSize, out int slotIndex))
                {
                    int slotOffset = slotIndex * _bitmap.MinSlotSize;
                    await WriteSlotDataAsync(blockNumber, slotOffset, data, ct).ConfigureAwait(false);
                    return (blockNumber, slotIndex, slotOffset);
                }
            }

            // No existing shared block has space; allocate a new block
            long newBlock = allocateNewBlock();
            if (_bitmap.AllocateSlot(newBlock, slotSize, out int newSlotIndex))
            {
                int slotOffset = newSlotIndex * _bitmap.MinSlotSize;

                // Zero-fill the new block first to ensure clean state
                byte[] zeroBlock = new byte[_blockSize];
                await _device.WriteBlockAsync(newBlock, zeroBlock, ct).ConfigureAwait(false);

                await WriteSlotDataAsync(newBlock, slotOffset, data, ct).ConfigureAwait(false);
                return (newBlock, newSlotIndex, slotOffset);
            }

            throw new InvalidOperationException(
                "Failed to allocate sub-block slot in newly allocated block. This indicates a bitmap inconsistency.");
        }
        finally
        {
            _packLock.Release();
        }
    }

    /// <summary>
    /// Reads data from a sub-block slot.
    /// </summary>
    /// <param name="blockNumber">The block number containing the slot.</param>
    /// <param name="slotIndex">The zero-based slot index within the block.</param>
    /// <param name="destination">Buffer to receive the slot data. Size determines how many bytes are read.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task UnpackAsync(long blockNumber, int slotIndex, Memory<byte> destination, CancellationToken ct = default)
    {
        if (slotIndex < 0 || slotIndex >= _bitmap.SlotsPerBlock)
            throw new ArgumentOutOfRangeException(nameof(slotIndex),
                $"Slot index must be in range [0, {_bitmap.SlotsPerBlock}).");

        int slotOffset = slotIndex * _bitmap.MinSlotSize;
        byte[] blockData = new byte[_blockSize];
        await _device.ReadBlockAsync(blockNumber, blockData, ct).ConfigureAwait(false);

        int bytesToCopy = Math.Min(destination.Length, _blockSize - slotOffset);
        blockData.AsMemory(slotOffset, bytesToCopy).CopyTo(destination);
    }

    /// <summary>
    /// Frees a sub-block slot. If the block becomes fully free after this operation,
    /// the block can be returned to the allocation group's primary bitmap.
    /// </summary>
    /// <param name="blockNumber">The block number containing the slot.</param>
    /// <param name="slotIndex">The zero-based slot index to free.</param>
    /// <param name="onBlockFreed">
    /// Optional callback invoked when the block becomes fully free and should be
    /// returned to the allocation group. Receives the block number.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public Task FreeAsync(
        long blockNumber,
        int slotIndex,
        Action<long>? onBlockFreed = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        _bitmap.FreeSlot(blockNumber, slotIndex);

        if (_bitmap.IsBlockFullyFree(blockNumber))
        {
            _bitmap.RemoveBlock(blockNumber);
            onBlockFreed?.Invoke(blockNumber);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Determines whether the given data size qualifies for sub-block packing.
    /// Objects larger than blockSize/2 should use normal block allocation.
    /// </summary>
    /// <param name="dataSize">Size of the data in bytes.</param>
    /// <returns><c>true</c> if the data should be sub-block packed.</returns>
    public bool ShouldPack(int dataSize)
    {
        return dataSize > 0 && dataSize <= MaxPackableSize;
    }

    /// <summary>
    /// Gets the slot size class that would be used for the given data size.
    /// </summary>
    /// <param name="dataSize">Size of the data in bytes.</param>
    /// <returns>The slot size class in bytes.</returns>
    public static int GetSlotSizeClass(int dataSize)
    {
        return SubBlockBitmap.RoundUpToSlotClass(dataSize);
    }

    // ── Private helpers ─────────────────────────────────────────────────

    private async Task WriteSlotDataAsync(long blockNumber, int slotOffset, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        // Read current block, overlay data at slot offset, write back
        byte[] blockData = new byte[_blockSize];
        await _device.ReadBlockAsync(blockNumber, blockData, ct).ConfigureAwait(false);

        data.CopyTo(blockData.AsMemory(slotOffset));
        await _device.WriteBlockAsync(blockNumber, blockData, ct).ConfigureAwait(false);
    }
}
