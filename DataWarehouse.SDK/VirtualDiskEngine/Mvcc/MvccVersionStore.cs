using DataWarehouse.SDK.Contracts;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.Mvcc;

/// <summary>
/// Manages on-disk storage for old versions in a dedicated MVCC region of the VDE.
/// When a write commits, the previous version of the data is stored here so that
/// concurrent readers with older snapshots can still access it.
/// </summary>
/// <remarks>
/// Version record format per block:
/// <code>
///   [0..8)   TransactionId        (long)
///   [8..16)  InodeNumber           (long)
///   [16..24) PreviousVersionBlock  (long, 0 = end of chain)
///   [24..28) DataLength            (int)
///   [28..N)  Data                  (variable)
///   [N..N+8) XxHash64 checksum    (ulong, covers bytes [0..N))
/// </code>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87: MVCC core (VOPT-12)")]
public sealed class MvccVersionStore
{
    /// <summary>Header size: TransactionId(8) + InodeNumber(8) + PreviousVersionBlock(8) + DataLength(4) = 28 bytes.</summary>
    private const int HeaderSize = 28;

    /// <summary>Checksum size in bytes (XxHash64).</summary>
    private const int ChecksumSize = 8;

    private readonly IBlockDevice _device;
    private readonly long _mvccRegionStartBlock;
    private readonly long _mvccRegionBlockCount;
    private readonly int _blockSize;
    private long _nextFreeBlock;
    private readonly SemaphoreSlim _allocLock = new(1, 1);

    /// <summary>
    /// Gets the number of blocks currently used for storing old versions.
    /// </summary>
    public long UsedBlocks => _nextFreeBlock;

    /// <summary>
    /// Gets the number of free blocks remaining in the MVCC region.
    /// </summary>
    public long FreeBlocks => _mvccRegionBlockCount - _nextFreeBlock;

    /// <summary>
    /// Creates a new MVCC version store backed by a region of the block device.
    /// </summary>
    /// <param name="device">Block device for reading/writing version data.</param>
    /// <param name="mvccRegionStartBlock">First block of the dedicated MVCC region.</param>
    /// <param name="mvccRegionBlockCount">Total blocks allocated to the MVCC region.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    public MvccVersionStore(IBlockDevice device, long mvccRegionStartBlock, long mvccRegionBlockCount, int blockSize)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _mvccRegionStartBlock = mvccRegionStartBlock;
        _mvccRegionBlockCount = mvccRegionBlockCount;
        _blockSize = blockSize;
    }

    /// <summary>
    /// Stores the old version of an inode's data in the MVCC region.
    /// </summary>
    /// <param name="inodeNumber">Inode whose old version is being stored.</param>
    /// <param name="transactionId">Transaction ID that created this version.</param>
    /// <param name="data">The old version data.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The absolute block number where the version was stored.</returns>
    /// <exception cref="InvalidOperationException">MVCC region is full.</exception>
    public async Task<long> StoreOldVersionAsync(
        long inodeNumber,
        long transactionId,
        ReadOnlyMemory<byte> data,
        CancellationToken ct = default)
    {
        return await StoreOldVersionAsync(inodeNumber, transactionId, data, previousVersionBlock: 0, ct);
    }

    /// <summary>
    /// Stores the old version of an inode's data with a link to the previous version block.
    /// </summary>
    /// <param name="inodeNumber">Inode whose old version is being stored.</param>
    /// <param name="transactionId">Transaction ID that created this version.</param>
    /// <param name="data">The old version data.</param>
    /// <param name="previousVersionBlock">Block number of the previous version in the chain (0 = end).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The absolute block number where the version was stored.</returns>
    /// <exception cref="InvalidOperationException">MVCC region is full.</exception>
    public async Task<long> StoreOldVersionAsync(
        long inodeNumber,
        long transactionId,
        ReadOnlyMemory<byte> data,
        long previousVersionBlock,
        CancellationToken ct = default)
    {
        int maxDataSize = _blockSize - HeaderSize - ChecksumSize;
        if (data.Length > maxDataSize)
        {
            throw new ArgumentException(
                $"Version data ({data.Length} bytes) exceeds maximum ({maxDataSize} bytes) for block size {_blockSize}.",
                nameof(data));
        }

        long relativeBlock;
        await _allocLock.WaitAsync(ct);
        try
        {
            if (_nextFreeBlock >= _mvccRegionBlockCount)
            {
                throw new InvalidOperationException("MVCC version region is full. Cannot store old version.");
            }

            relativeBlock = _nextFreeBlock++;
        }
        finally
        {
            _allocLock.Release();
        }

        long absoluteBlock = _mvccRegionStartBlock + relativeBlock;

        // Build the version record
        var block = new byte[_blockSize];
        int offset = 0;

        BinaryPrimitives.WriteInt64LittleEndian(block.AsSpan(offset, 8), transactionId);
        offset += 8;

        BinaryPrimitives.WriteInt64LittleEndian(block.AsSpan(offset, 8), inodeNumber);
        offset += 8;

        BinaryPrimitives.WriteInt64LittleEndian(block.AsSpan(offset, 8), previousVersionBlock);
        offset += 8;

        BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(offset, 4), data.Length);
        offset += 4;

        data.Span.CopyTo(block.AsSpan(offset, data.Length));
        offset += data.Length;

        // XxHash64 checksum over [0..offset)
        ulong checksum = XxHash64.HashToUInt64(block.AsSpan(0, offset));
        BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(offset, 8), checksum);

        await _device.WriteBlockAsync(absoluteBlock, block, ct);

        return absoluteBlock;
    }

    /// <summary>
    /// Reads a stored version from the given block number.
    /// </summary>
    /// <param name="versionBlockNumber">Absolute block number of the stored version.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Tuple of (TransactionId, Data) if the block is valid and checksum passes; null otherwise.
    /// </returns>
    public async Task<(long TransactionId, byte[] Data)?> ReadVersionAsync(
        long versionBlockNumber,
        CancellationToken ct = default)
    {
        var buffer = new byte[_blockSize];
        await _device.ReadBlockAsync(versionBlockNumber, buffer, ct);

        int offset = 0;
        long transactionId = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(offset, 8));
        offset += 8;

        // Skip InodeNumber (8) and PreviousVersionBlock (8)
        offset += 16;

        int dataLength = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset, 4));
        offset += 4;

        if (dataLength < 0 || offset + dataLength + ChecksumSize > _blockSize)
        {
            return null;
        }

        var data = new byte[dataLength];
        buffer.AsSpan(offset, dataLength).CopyTo(data);
        offset += dataLength;

        // Verify checksum
        ulong storedChecksum = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(offset, 8));
        ulong computedChecksum = XxHash64.HashToUInt64(buffer.AsSpan(0, offset));

        if (storedChecksum != computedChecksum)
        {
            return null;
        }

        return (transactionId, data);
    }

    /// <summary>
    /// Follows PreviousVersionBlock pointers starting from headBlock to build the full version chain.
    /// </summary>
    /// <param name="headBlock">Absolute block number of the version chain head.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Ordered list of (TransactionId, VersionBlock) from newest to oldest.</returns>
    public async Task<IReadOnlyList<(long TransactionId, long VersionBlock)>> GetVersionChainAsync(
        long headBlock,
        CancellationToken ct = default)
    {
        var chain = new List<(long TransactionId, long VersionBlock)>();
        var visited = new HashSet<long>();
        long currentBlock = headBlock;

        while (currentBlock > 0 && visited.Add(currentBlock))
        {
            var buffer = new byte[_blockSize];
            await _device.ReadBlockAsync(currentBlock, buffer, ct);

            long transactionId = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(0, 8));
            // Skip InodeNumber at offset 8
            long previousVersionBlock = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(16, 8));

            chain.Add((transactionId, currentBlock));
            currentBlock = previousVersionBlock;
        }

        return chain;
    }
}
