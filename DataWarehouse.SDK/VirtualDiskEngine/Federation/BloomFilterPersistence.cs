using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Federation;

/// <summary>
/// Serializes and persists bloom filter snapshots to <see cref="IBlockDevice"/> metadata blocks
/// so that filters survive VDE restart without requiring a full rebuild from keys.
/// </summary>
/// <remarks>
/// <para>
/// On-disk format (48-byte header at start of first block):
/// <list type="bullet">
/// <item>Bytes 0-3: Magic <c>0x424C4F4D</c> ("BLOM") as uint32 LE</item>
/// <item>Bytes 4-7: Version uint32 LE (currently 1)</item>
/// <item>Bytes 8-23: ShardVdeId as 16-byte Guid</item>
/// <item>Bytes 24-31: ItemCount int64 LE</item>
/// <item>Bytes 32-39: FalsePositiveRate as double via BinaryPrimitives (Int64BitsToDouble pattern)</item>
/// <item>Bytes 40-43: FilterDataLength int32 LE</item>
/// <item>Bytes 44-47: Checksum uint32 LE (XxHash32 of FilterData)</item>
/// <item>Bytes 48+: FilterData bytes (may span multiple blocks)</item>
/// </list>
/// </para>
/// <para>
/// All integer serialization uses <see cref="BinaryPrimitives"/> exclusively (no BinaryReader/BinaryWriter).
/// Temporary buffers use <see cref="ArrayPool{T}.Shared"/> to minimize allocations.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Bloom Filter Persistence (VFED-16)")]
public sealed class BloomFilterPersistence
{
    /// <summary>Magic bytes "BLOM" identifying a persisted bloom filter block.</summary>
    private const uint Magic = 0x424C4F4D;

    /// <summary>Current on-disk format version.</summary>
    private const uint FormatVersion = 1;

    /// <summary>Size of the header in bytes.</summary>
    private const int HeaderSize = 48;

    /// <summary>Maximum allowed filter data length (64 MB) to prevent corrupted headers from causing OOM.</summary>
    private const int MaxFilterDataLength = 64 * 1024 * 1024;

    private readonly int _blockSize;

    /// <summary>
    /// Creates a new bloom filter persistence handler.
    /// </summary>
    /// <param name="blockSize">The block device block size in bytes. Default: 4096.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="blockSize"/> is less than <see cref="HeaderSize"/>.</exception>
    public BloomFilterPersistence(int blockSize = 4096)
    {
        if (blockSize < HeaderSize)
            throw new ArgumentOutOfRangeException(
                nameof(blockSize),
                $"Block size must be at least {HeaderSize} bytes to fit the header.");

        _blockSize = blockSize;
    }

    /// <summary>
    /// Calculates the number of blocks needed to store a bloom filter with the given data length.
    /// </summary>
    /// <param name="filterDataLength">The length of the serialized filter data in bytes.</param>
    /// <returns>The number of blocks required.</returns>
    public int CalculateBlocksNeeded(int filterDataLength)
    {
        if (filterDataLength <= 0)
            return 1; // Header-only block

        int firstBlockPayload = _blockSize - HeaderSize;
        if (filterDataLength <= firstBlockPayload)
            return 1;

        int remaining = filterDataLength - firstBlockPayload;
        return 1 + (remaining + _blockSize - 1) / _blockSize;
    }

    /// <summary>
    /// Writes a bloom filter snapshot to the block device starting at the specified block.
    /// The snapshot is serialized with a 48-byte header followed by the filter data,
    /// spanning as many blocks as needed.
    /// </summary>
    /// <param name="device">The block device to write to.</param>
    /// <param name="startBlock">The starting block number for the write.</param>
    /// <param name="snapshot">The bloom filter snapshot to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="device"/> is null or snapshot FilterData is null.</exception>
    public async Task WriteAsync(IBlockDevice device, long startBlock, BloomFilterSnapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (snapshot.FilterData == null)
            throw new ArgumentNullException(nameof(snapshot), "Snapshot FilterData must not be null.");

        int filterDataLength = snapshot.FilterData.Length;
        int totalBlocks = CalculateBlocksNeeded(filterDataLength);

        // Compute checksum over filter data using XxHash32
        uint checksum = ComputeChecksum(snapshot.FilterData);

        // Serialize Guid to 16 bytes
        Span<byte> guidBytes = stackalloc byte[16];
        snapshot.ShardVdeId.TryWriteBytes(guidBytes);

        // Write first block with header
        byte[] blockBuffer = ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            Array.Clear(blockBuffer, 0, _blockSize);
            var span = blockBuffer.AsSpan(0, _blockSize);

            // Write header fields via BinaryPrimitives
            BinaryPrimitives.WriteUInt32LittleEndian(span[0..], Magic);
            BinaryPrimitives.WriteUInt32LittleEndian(span[4..], FormatVersion);
            guidBytes.CopyTo(span[8..]);
            BinaryPrimitives.WriteInt64LittleEndian(span[24..], snapshot.ItemCount);
            BinaryPrimitives.WriteInt64LittleEndian(span[32..], BitConverter.DoubleToInt64Bits(snapshot.FalsePositiveRate));
            BinaryPrimitives.WriteInt32LittleEndian(span[40..], filterDataLength);
            BinaryPrimitives.WriteUInt32LittleEndian(span[44..], checksum);

            // Copy as much filter data as fits in the first block
            int firstBlockPayload = Math.Min(filterDataLength, _blockSize - HeaderSize);
            if (firstBlockPayload > 0)
            {
                snapshot.FilterData.AsSpan(0, firstBlockPayload).CopyTo(span[HeaderSize..]);
            }

            await device.WriteBlockAsync(startBlock, new ReadOnlyMemory<byte>(blockBuffer, 0, _blockSize), ct).ConfigureAwait(false);

            // Write subsequent blocks for overflow data
            int dataOffset = firstBlockPayload;
            for (int blockIndex = 1; blockIndex < totalBlocks; blockIndex++)
            {
                Array.Clear(blockBuffer, 0, _blockSize);
                int bytesToCopy = Math.Min(filterDataLength - dataOffset, _blockSize);
                if (bytesToCopy > 0)
                {
                    snapshot.FilterData.AsSpan(dataOffset, bytesToCopy).CopyTo(blockBuffer.AsSpan());
                }
                dataOffset += bytesToCopy;

                await device.WriteBlockAsync(startBlock + blockIndex, new ReadOnlyMemory<byte>(blockBuffer, 0, _blockSize), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(blockBuffer);
        }
    }

    /// <summary>
    /// Reads a bloom filter snapshot from the block device starting at the specified block.
    /// Returns <c>null</c> if the magic bytes don't match (no filter persisted) or
    /// if the checksum verification fails (corrupted filter, will be rebuilt).
    /// </summary>
    /// <param name="device">The block device to read from.</param>
    /// <param name="startBlock">The starting block number for the read.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The deserialized <see cref="BloomFilterSnapshot"/>, or <c>null</c> if no valid
    /// filter is found at the specified location.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="device"/> is null.</exception>
    public async Task<BloomFilterSnapshot?> ReadAsync(IBlockDevice device, long startBlock, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        byte[] blockBuffer = ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            // Read first block
            await device.ReadBlockAsync(startBlock, new Memory<byte>(blockBuffer, 0, _blockSize), ct).ConfigureAwait(false);
            var span = blockBuffer.AsSpan(0, _blockSize);

            // Verify magic
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(span[0..]);
            if (magic != Magic)
                return null;

            // Parse header
            uint version = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
            if (version != FormatVersion)
                return null; // Unknown version — cannot parse

            Guid shardVdeId = new Guid(span.Slice(8, 16));
            long itemCount = BinaryPrimitives.ReadInt64LittleEndian(span[24..]);
            double falsePositiveRate = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(span[32..]));
            int filterDataLength = BinaryPrimitives.ReadInt32LittleEndian(span[40..]);
            uint storedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(span[44..]);

            // Validate filter data length
            if (filterDataLength < 0 || filterDataLength > MaxFilterDataLength)
                return null; // Corrupted header

            // Read filter data from first block remainder + subsequent blocks
            byte[] filterData = new byte[filterDataLength];
            int firstBlockPayload = Math.Min(filterDataLength, _blockSize - HeaderSize);
            if (firstBlockPayload > 0)
            {
                span.Slice(HeaderSize, firstBlockPayload).CopyTo(filterData.AsSpan());
            }

            int dataOffset = firstBlockPayload;
            int totalBlocks = CalculateBlocksNeeded(filterDataLength);
            for (int blockIndex = 1; blockIndex < totalBlocks; blockIndex++)
            {
                await device.ReadBlockAsync(startBlock + blockIndex, new Memory<byte>(blockBuffer, 0, _blockSize), ct).ConfigureAwait(false);
                int bytesToCopy = Math.Min(filterDataLength - dataOffset, _blockSize);
                if (bytesToCopy > 0)
                {
                    blockBuffer.AsSpan(0, bytesToCopy).CopyTo(filterData.AsSpan(dataOffset));
                }
                dataOffset += bytesToCopy;
            }

            // Verify checksum
            uint computedChecksum = ComputeChecksum(filterData);
            if (computedChecksum != storedChecksum)
                return null; // Corrupted data — trigger rebuild

            return new BloomFilterSnapshot(shardVdeId, filterData, itemCount, falsePositiveRate);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(blockBuffer);
        }
    }

    /// <summary>
    /// Erases bloom filter data by writing zero-filled blocks.
    /// Used when decommissioning a shard to clean up its metadata blocks.
    /// </summary>
    /// <param name="device">The block device to erase on.</param>
    /// <param name="startBlock">The starting block number.</param>
    /// <param name="blockCount">The number of blocks to erase.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="device"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="blockCount"/> is non-positive.</exception>
    public async Task EraseAsync(IBlockDevice device, long startBlock, int blockCount, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockCount);

        byte[] zeroBlock = ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            Array.Clear(zeroBlock, 0, _blockSize);
            var data = new ReadOnlyMemory<byte>(zeroBlock, 0, _blockSize);

            for (int i = 0; i < blockCount; i++)
            {
                await device.WriteBlockAsync(startBlock + i, data, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(zeroBlock);
        }
    }

    /// <summary>
    /// Computes an XxHash32 checksum over the filter data for integrity verification.
    /// </summary>
    private static uint ComputeChecksum(byte[] data)
    {
        return XxHash32.HashToUInt32(data);
    }
}
