using DataWarehouse.SDK.Contracts;
using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.Container;

/// <summary>
/// Manages DWVD container file lifecycle: creation, opening, validation, and checkpoint writes.
/// Provides dual superblock mirroring for fault tolerance.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 33: Virtual Disk Engine (VDE-01/VDE-04)")]
public sealed class ContainerFile : IAsyncDisposable
{
    private readonly SemaphoreSlim _checkpointLock = new(1, 1);
    private volatile bool _disposed;

    /// <summary>
    /// The underlying block device.
    /// </summary>
    public IBlockDevice BlockDevice { get; }

    /// <summary>
    /// The current superblock state.
    /// </summary>
    public Superblock CurrentSuperblock { get; private set; }

    /// <summary>
    /// The computed container layout.
    /// </summary>
    public ContainerLayout Layout { get; }

    private ContainerFile(IBlockDevice blockDevice, Superblock superblock, ContainerLayout layout)
    {
        BlockDevice = blockDevice;
        CurrentSuperblock = superblock;
        Layout = layout;
    }

    /// <summary>
    /// Creates a new DWVD container file.
    /// </summary>
    /// <param name="path">Path to the container file.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="totalBlocks">Total number of blocks.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A new <see cref="ContainerFile"/> instance.</returns>
    public static async Task<ContainerFile> CreateAsync(string path, int blockSize, long totalBlocks, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSize, VdeConstants.MinBlockSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(blockSize, VdeConstants.MaxBlockSize);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(totalBlocks, 0L);

        // Compute layout
        var layout = ContainerFormat.ComputeLayout(blockSize, totalBlocks);

        // Create block device
        var device = new FileBlockDevice(path, blockSize, totalBlocks, createNew: true);

        try
        {
            // Create initial superblock
            long nowUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var superblock = new Superblock
            {
                Magic = VdeConstants.MagicBytes,
                FormatVersion = VdeConstants.FormatVersion,
                Flags = 0,
                BlockSize = blockSize,
                TotalBlocks = totalBlocks,
                FreeBlocks = layout.DataBlockCount, // All data blocks are initially free
                InodeTableBlock = layout.InodeTableBlock,
                BitmapStartBlock = layout.BitmapStartBlock,
                BitmapBlockCount = layout.BitmapBlockCount,
                BTreeRootBlock = layout.BTreeRootBlock,
                WalStartBlock = layout.WalStartBlock,
                WalBlockCount = layout.WalBlockCount,
                ChecksumTableBlock = layout.ChecksumTableBlock,
                CreatedTimestampUtc = nowUtc,
                ModifiedTimestampUtc = nowUtc,
                CheckpointSequence = 0,
                Checksum = 0 // Will be computed during serialization
            };

            // Write dual superblocks
            await WriteSuperblockAsync(device, 0, superblock, ct);
            await WriteSuperblockAsync(device, 1, superblock, ct);

            // Initialize bitmap (all data blocks free = all bits 0)
            await InitializeBitmapAsync(device, layout, ct);

            await device.FlushAsync(ct);

            return new ContainerFile(device, superblock, layout);
        }
        catch
        {
            await device.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Opens an existing DWVD container file.
    /// Validates magic bytes and checksum. Falls back to mirror superblock if primary is corrupt.
    /// </summary>
    /// <param name="path">Path to the container file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="ContainerFile"/> instance.</returns>
    public static async Task<ContainerFile> OpenAsync(string path, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(path);

        // Read primary superblock from block 0
        Superblock superblock;
        bool primaryValid = false;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(VdeConstants.SuperblockSize);
        try
        {
            // We need to determine block size from the superblock, but we don't know it yet.
            // Read as a raw file first to get the superblock.
            await using var tempDevice = new FileBlockDevice(path, VdeConstants.SuperblockSize, 2, createNew: false);

            await tempDevice.ReadBlockAsync(0, buffer, ct);
            primaryValid = Superblock.Deserialize(buffer, out superblock);

            if (!primaryValid)
            {
                // Try mirror superblock at block 1
                await tempDevice.ReadBlockAsync(1, buffer, ct);
                if (!Superblock.Deserialize(buffer, out superblock))
                {
                    throw new InvalidOperationException("Container is corrupt: both primary and mirror superblocks are invalid.");
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // Now we know the block size, reopen with the correct parameters
        var device = new FileBlockDevice(path, superblock.BlockSize, superblock.TotalBlocks, createNew: false);

        // Compute layout
        var layout = ContainerFormat.ComputeLayout(superblock.BlockSize, superblock.TotalBlocks);

        return new ContainerFile(device, superblock, layout);
    }

    /// <summary>
    /// Writes a checkpoint: updates the superblock with current state.
    /// Writes mirror first, then primary (safe ordering for fault tolerance).
    /// </summary>
    /// <param name="freeBlocks">Updated free block count.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WriteCheckpointAsync(long? freeBlocks = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _checkpointLock.WaitAsync(ct);
        try
        {
            var updated = CurrentSuperblock with
            {
                FreeBlocks = freeBlocks ?? CurrentSuperblock.FreeBlocks,
                ModifiedTimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                CheckpointSequence = CurrentSuperblock.CheckpointSequence + 1,
                Checksum = 0 // Will be recomputed during serialization
            };

            // Write mirror first (block 1), then primary (block 0)
            // This ensures that if a crash occurs during write, we still have one valid superblock
            await WriteSuperblockAsync(BlockDevice, 1, updated, ct);
            await WriteSuperblockAsync(BlockDevice, 0, updated, ct);

            await BlockDevice.FlushAsync(ct);

            CurrentSuperblock = updated;
        }
        finally
        {
            _checkpointLock.Release();
        }
    }

    /// <summary>
    /// Writes a superblock to the specified block number.
    /// </summary>
    private static async Task WriteSuperblockAsync(IBlockDevice device, long blockNumber, Superblock sb, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(device.BlockSize);
        try
        {
            Array.Clear(buffer, 0, device.BlockSize);
            Superblock.Serialize(sb, buffer);
            await device.WriteBlockAsync(blockNumber, buffer.AsMemory(0, device.BlockSize), ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Initializes the free space bitmap: all data blocks are initially free (all bits 0).
    /// </summary>
    private static async Task InitializeBitmapAsync(IBlockDevice device, ContainerLayout layout, CancellationToken ct)
    {
        byte[] zeroBlock = ArrayPool<byte>.Shared.Rent(device.BlockSize);
        try
        {
            Array.Clear(zeroBlock, 0, device.BlockSize);

            for (long i = 0; i < layout.BitmapBlockCount; i++)
            {
                await device.WriteBlockAsync(layout.BitmapStartBlock + i, zeroBlock.AsMemory(0, device.BlockSize), ct);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(zeroBlock);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _checkpointLock.WaitAsync();
        try
        {
            await BlockDevice.DisposeAsync();
        }
        finally
        {
            _checkpointLock.Release();
            _checkpointLock.Dispose();
        }
    }
}
