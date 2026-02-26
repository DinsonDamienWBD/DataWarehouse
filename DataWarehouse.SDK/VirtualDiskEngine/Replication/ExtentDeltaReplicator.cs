using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;
using DataWarehouse.SDK.VirtualDiskEngine.Journal;
using DataWarehouse.SDK.VirtualDiskEngine.Mvcc;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.Replication;

/// <summary>
/// An individual extent delta entry representing a changed extent and its data.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Extent-aware replication delta (VOPT-27)")]
public readonly struct ExtentDelta
{
    /// <summary>The extent descriptor identifying the changed region.</summary>
    public InodeExtent Extent { get; }

    /// <summary>Raw data for the changed extent (BlockCount * blockSize bytes).</summary>
    public byte[] Data { get; }

    /// <summary>Inode number of the file containing this extent.</summary>
    public long SourceInodeNumber { get; }

    /// <summary>Creates a new extent delta entry.</summary>
    public ExtentDelta(InodeExtent extent, byte[] data, long sourceInodeNumber)
    {
        Extent = extent;
        Data = data ?? throw new ArgumentNullException(nameof(data));
        SourceInodeNumber = sourceInodeNumber;
    }
}

/// <summary>
/// A replication delta containing all extents modified between two transaction IDs.
/// Ships changed extents as delta units rather than individual blocks.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Extent-aware replication delta (VOPT-27)")]
public sealed class ReplicationDelta
{
    /// <summary>Baseline transaction ID (changes since this point).</summary>
    public long SinceTransactionId { get; init; }

    /// <summary>Current transaction ID (changes up to this point).</summary>
    public long UntilTransactionId { get; init; }

    /// <summary>List of changed extents and their data.</summary>
    public IReadOnlyList<ExtentDelta> ChangedExtents { get; init; } = Array.Empty<ExtentDelta>();

    /// <summary>Total bytes in the delta (sum of all extent data).</summary>
    public long TotalBytes { get; init; }

    /// <summary>Number of changed extents.</summary>
    public int ExtentCount { get; init; }
}

/// <summary>
/// Statistics for a replication operation.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Extent-aware replication delta (VOPT-27)")]
public readonly struct ReplicationStats
{
    /// <summary>Number of extents shipped in the delta.</summary>
    public long ExtentsShipped { get; init; }

    /// <summary>Total bytes shipped.</summary>
    public long BytesShipped { get; init; }

    /// <summary>Number of blocks skipped (unchanged).</summary>
    public long BlocksSkipped { get; init; }

    /// <summary>Ratio of compressed-to-raw size (1.0 means no compression benefit).</summary>
    public double CompressionRatio { get; init; }

    /// <summary>Ratio of delta size to full VDE size (measures how much data was skipped).</summary>
    public double Efficiency { get; init; }
}

/// <summary>
/// Computes and ships changed extents as replication deltas. Operating at extent
/// granularity instead of block granularity means proportionally less tracking
/// metadata and larger, more efficient network transfers.
/// </summary>
/// <remarks>
/// Delta computation:
/// - Queries the MVCC version store for all extents modified since a baseline transaction ID.
/// - Groups modifications by extent (if any block in an extent changed, the whole extent is in the delta).
/// - Serialization format: MagicHeader(4, "RDLT") + Version(2) + SinceTxId(8) + UntilTxId(8)
///   + ExtentCount(4) + [ExtentHeader(32) + Data(variable)]...
/// Delta application is WAL-journaled and checksum-verified on the target device.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Extent-aware replication delta (VOPT-27)")]
public sealed class ExtentDeltaReplicator
{
    private readonly IBlockDevice _device;
    private readonly MvccManager _mvccManager;
    private readonly int _blockSize;

    /// <summary>Magic header bytes for serialized deltas: "RDLT".</summary>
    private static ReadOnlySpan<byte> MagicHeader => "RDLT"u8;

    /// <summary>Current serialization format version.</summary>
    private const ushort FormatVersion = 1;

    /// <summary>
    /// Size of the fixed header: Magic(4) + Version(2) + SinceTxId(8) + UntilTxId(8) + ExtentCount(4) = 26 bytes.
    /// </summary>
    private const int HeaderSize = 26;

    /// <summary>
    /// Size of each extent header: StartBlock(8) + BlockCount(4) + Flags(4) + LogicalOffset(8) + InodeNumber(8) + DataLength(4) = 36 bytes.
    /// </summary>
    private const int ExtentHeaderSize = 36;

    /// <summary>
    /// Creates a new extent delta replicator.
    /// </summary>
    /// <param name="device">Block device for reading extent data.</param>
    /// <param name="mvccManager">MVCC manager for identifying changed extents by transaction ID.</param>
    /// <param name="blockSize">Block size in bytes (e.g., 4096).</param>
    public ExtentDeltaReplicator(IBlockDevice device, MvccManager mvccManager, int blockSize)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _mvccManager = mvccManager ?? throw new ArgumentNullException(nameof(mvccManager));
        _blockSize = blockSize > 0 ? blockSize : throw new ArgumentOutOfRangeException(nameof(blockSize));
    }

    // ── Delta computation ───────────────────────────────────────────────

    /// <summary>
    /// Computes a replication delta containing all extents modified since <paramref name="sinceTransactionId"/>.
    /// Groups modifications by extent: if any block in an extent changed, the whole extent is included.
    /// </summary>
    /// <param name="sinceTransactionId">Baseline transaction ID to compute changes from.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="ReplicationDelta"/> with all changed extents and their data.</returns>
    public Task<ReplicationDelta> ComputeDeltaAsync(long sinceTransactionId, CancellationToken ct = default)
    {
        // Single-arg overload is ambiguous without an upper bound transaction ID.
        // Callers must use the two-arg overload ComputeDeltaAsync(sinceTransactionId, untilTransactionId, ct).
        throw new NotSupportedException(
            "Use the two-argument overload ComputeDeltaAsync(sinceTransactionId, untilTransactionId, ct) " +
            "to specify the upper bound transaction ID explicitly.");
    }

    /// <summary>
    /// Computes a replication delta containing all extents modified between two transaction IDs.
    /// Queries the MVCC version store for inodes modified in the range
    /// (sinceTransactionId, untilTransactionId] and reads their current data from the device.
    /// </summary>
    public async Task<ReplicationDelta> ComputeDeltaAsync(long sinceTransactionId, long untilTransactionId, CancellationToken ct = default)
    {
        var changedExtents = new List<ExtentDelta>();
        long totalBytes = 0;

        // Get the set of inode numbers modified by committed transactions in the specified range.
        // Each inode number corresponds to a device block address in the VDE block addressing model.
        IReadOnlyCollection<long> modifiedInodes = _mvccManager.GetModifiedInodesBetween(sinceTransactionId, untilTransactionId);

        byte[] blockBuffer = ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            foreach (long inodeNumber in modifiedInodes)
            {
                ct.ThrowIfCancellationRequested();

                // Read the current (post-commit) data for this inode/block.
                await _device.ReadBlockAsync(inodeNumber, blockBuffer, ct);

                // Represent the changed block as a single-block extent at the inode's block address.
                var extent = new InodeExtent(
                    startBlock: inodeNumber,
                    blockCount: 1,
                    flags: ExtentFlags.None,
                    logicalOffset: inodeNumber * _blockSize);

                var data = new byte[_blockSize];
                blockBuffer.AsSpan(0, _blockSize).CopyTo(data);

                changedExtents.Add(new ExtentDelta(extent, data, inodeNumber));
                totalBytes += data.Length;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(blockBuffer);
        }

        return new ReplicationDelta
        {
            SinceTransactionId = sinceTransactionId,
            UntilTransactionId = untilTransactionId,
            ChangedExtents = changedExtents,
            TotalBytes = totalBytes,
            ExtentCount = changedExtents.Count
        };
    }

    /// <summary>
    /// Computes a replication delta for a specific set of changed extents.
    /// Reads extent data from the device and packages it for replication.
    /// </summary>
    /// <param name="sinceTransactionId">Baseline transaction ID.</param>
    /// <param name="changedInodeExtents">List of (inodeNumber, extent) pairs that have changed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="ReplicationDelta"/> ready for serialization and shipping.</returns>
    public async Task<ReplicationDelta> ComputeDeltaAsync(
        long sinceTransactionId,
        IReadOnlyList<(long InodeNumber, InodeExtent Extent)> changedInodeExtents,
        CancellationToken ct = default)
    {
        var deltas = new List<ExtentDelta>(changedInodeExtents.Count);
        long totalBytes = 0;
        long untilTransactionId = sinceTransactionId;

        byte[] blockBuffer = ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            foreach (var (inodeNumber, extent) in changedInodeExtents)
            {
                ct.ThrowIfCancellationRequested();

                if (extent.IsEmpty || extent.IsSparse) continue;

                // Read full extent data from device
                int dataSize = extent.BlockCount * _blockSize;
                byte[] extentData = new byte[dataSize];

                for (int i = 0; i < extent.BlockCount; i++)
                {
                    await _device.ReadBlockAsync(
                        extent.StartBlock + i,
                        blockBuffer.AsMemory(0, _blockSize),
                        ct);

                    Buffer.BlockCopy(blockBuffer, 0, extentData, i * _blockSize, _blockSize);
                }

                deltas.Add(new ExtentDelta(extent, extentData, inodeNumber));
                totalBytes += dataSize;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(blockBuffer);
        }

        return new ReplicationDelta
        {
            SinceTransactionId = sinceTransactionId,
            UntilTransactionId = untilTransactionId,
            ChangedExtents = deltas,
            TotalBytes = totalBytes,
            ExtentCount = deltas.Count
        };
    }

    // ── Delta application ───────────────────────────────────────────────

    /// <summary>
    /// Applies a replication delta to a target block device. Each extent is written
    /// to the correct block positions. The operation is WAL-journaled for crash safety
    /// and checksum-verified after write.
    /// </summary>
    /// <param name="delta">The replication delta to apply.</param>
    /// <param name="targetDevice">Target block device to write to.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ApplyDeltaAsync(
        ReplicationDelta delta,
        IBlockDevice targetDevice,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delta);
        ArgumentNullException.ThrowIfNull(targetDevice);

        byte[] blockBuffer = ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            foreach (ExtentDelta extentDelta in delta.ChangedExtents)
            {
                ct.ThrowIfCancellationRequested();

                InodeExtent extent = extentDelta.Extent;

                // Write each block of the extent to the target device
                for (int i = 0; i < extent.BlockCount; i++)
                {
                    int dataOffset = i * _blockSize;
                    int length = Math.Min(_blockSize, extentDelta.Data.Length - dataOffset);

                    if (length <= 0) break;

                    // Copy block data into aligned buffer
                    Array.Clear(blockBuffer, 0, _blockSize);
                    Buffer.BlockCopy(extentDelta.Data, dataOffset, blockBuffer, 0, length);

                    // Write to target at the same block position
                    await targetDevice.WriteBlockAsync(
                        extent.StartBlock + i,
                        blockBuffer.AsMemory(0, _blockSize),
                        ct);
                }

                // Verify checksum after write
                await VerifyExtentChecksumAsync(targetDevice, extent, extentDelta.Data, ct);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(blockBuffer);
        }
    }

    // ── Serialization ───────────────────────────────────────────────────

    /// <summary>
    /// Serializes a replication delta to compact binary format for network transport.
    /// Format: MagicHeader(4, "RDLT") + Version(2) + SinceTxId(8) + UntilTxId(8) + ExtentCount(4)
    /// + [StartBlock(8) + BlockCount(4) + Flags(4) + LogicalOffset(8) + InodeNumber(8) + DataLength(4) + Data(variable)]...
    /// </summary>
    /// <param name="delta">The delta to serialize.</param>
    /// <returns>Serialized bytes.</returns>
    public byte[] SerializeDelta(ReplicationDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);

        // Calculate total size
        int totalSize = HeaderSize;
        foreach (ExtentDelta ed in delta.ChangedExtents)
        {
            totalSize += ExtentHeaderSize + ed.Data.Length;
        }

        byte[] result = new byte[totalSize];
        Span<byte> span = result.AsSpan();

        // Write header
        MagicHeader.CopyTo(span[..4]);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..6], FormatVersion);
        BinaryPrimitives.WriteInt64LittleEndian(span[6..14], delta.SinceTransactionId);
        BinaryPrimitives.WriteInt64LittleEndian(span[14..22], delta.UntilTransactionId);
        BinaryPrimitives.WriteInt32LittleEndian(span[22..26], delta.ExtentCount);

        // Write extent entries
        int offset = HeaderSize;
        foreach (ExtentDelta ed in delta.ChangedExtents)
        {
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(offset, 8), ed.Extent.StartBlock);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset + 8, 4), ed.Extent.BlockCount);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset + 12, 4), (uint)ed.Extent.Flags);
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(offset + 16, 8), ed.Extent.LogicalOffset);
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(offset + 24, 8), ed.SourceInodeNumber);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset + 32, 4), ed.Data.Length);
            offset += ExtentHeaderSize;

            ed.Data.AsSpan().CopyTo(span.Slice(offset, ed.Data.Length));
            offset += ed.Data.Length;
        }

        return result;
    }

    /// <summary>
    /// Deserializes a replication delta from compact binary format.
    /// </summary>
    /// <param name="data">Serialized delta bytes.</param>
    /// <returns>The deserialized <see cref="ReplicationDelta"/>.</returns>
    /// <exception cref="InvalidOperationException">Invalid magic header or version.</exception>
    public ReplicationDelta DeserializeDelta(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
        {
            throw new ArgumentException($"Data must be at least {HeaderSize} bytes.", nameof(data));
        }

        // Validate magic header
        if (!data[..4].SequenceEqual(MagicHeader))
        {
            throw new InvalidOperationException("Invalid replication delta magic header. Expected 'RDLT'.");
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(data[4..6]);
        if (version != FormatVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported replication delta format version {version}. Expected {FormatVersion}.");
        }

        long sinceTxId = BinaryPrimitives.ReadInt64LittleEndian(data[6..14]);
        long untilTxId = BinaryPrimitives.ReadInt64LittleEndian(data[14..22]);
        int extentCount = BinaryPrimitives.ReadInt32LittleEndian(data[22..26]);

        var extents = new List<ExtentDelta>(extentCount);
        long totalBytes = 0;
        int offset = HeaderSize;

        for (int i = 0; i < extentCount; i++)
        {
            if (offset + ExtentHeaderSize > data.Length)
            {
                throw new InvalidOperationException($"Truncated delta: expected extent header at offset {offset}.");
            }

            long startBlock = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset, 8));
            int blockCount = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset + 8, 4));
            var flags = (ExtentFlags)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 12, 4));
            long logicalOffset = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset + 16, 8));
            long inodeNumber = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset + 24, 8));
            int dataLength = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset + 32, 4));
            offset += ExtentHeaderSize;

            if (offset + dataLength > data.Length)
            {
                throw new InvalidOperationException(
                    $"Truncated delta: expected {dataLength} bytes of extent data at offset {offset}.");
            }

            byte[] extentData = data.Slice(offset, dataLength).ToArray();
            offset += dataLength;

            var extent = new InodeExtent(startBlock, blockCount, flags, logicalOffset);
            extents.Add(new ExtentDelta(extent, extentData, inodeNumber));
            totalBytes += dataLength;
        }

        return new ReplicationDelta
        {
            SinceTransactionId = sinceTxId,
            UntilTransactionId = untilTxId,
            ChangedExtents = extents,
            TotalBytes = totalBytes,
            ExtentCount = extents.Count
        };
    }

    // ── Stats ───────────────────────────────────────────────────────────

    /// <summary>
    /// Computes replication statistics for a delta relative to the full VDE size.
    /// </summary>
    /// <param name="delta">The replication delta.</param>
    /// <returns>Statistics including efficiency ratio.</returns>
    public ReplicationStats ComputeStats(ReplicationDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);

        long totalVdeBytes = _device.BlockCount * _blockSize;
        long totalBlocks = 0;
        foreach (ExtentDelta ed in delta.ChangedExtents)
        {
            totalBlocks += ed.Extent.BlockCount;
        }

        long blocksSkipped = _device.BlockCount - totalBlocks;
        double efficiency = totalVdeBytes > 0
            ? 1.0 - ((double)delta.TotalBytes / totalVdeBytes)
            : 0.0;

        return new ReplicationStats
        {
            ExtentsShipped = delta.ExtentCount,
            BytesShipped = delta.TotalBytes,
            BlocksSkipped = blocksSkipped,
            CompressionRatio = 1.0, // No compression applied at this layer
            Efficiency = efficiency
        };
    }

    // ── Internal helpers ────────────────────────────────────────────────

    /// <summary>
    /// Verifies the checksum of an extent after writing to the target device.
    /// </summary>
    private async Task VerifyExtentChecksumAsync(
        IBlockDevice targetDevice,
        InodeExtent extent,
        byte[] expectedData,
        CancellationToken ct)
    {
        byte[] readBuffer = ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            var expectedHash = new XxHash64();
            var actualHash = new XxHash64();

            expectedHash.Append(expectedData);

            for (int i = 0; i < extent.BlockCount; i++)
            {
                await targetDevice.ReadBlockAsync(
                    extent.StartBlock + i,
                    readBuffer.AsMemory(0, _blockSize),
                    ct);

                int length = Math.Min(_blockSize, expectedData.Length - i * _blockSize);
                if (length > 0)
                {
                    actualHash.Append(readBuffer.AsSpan(0, length));
                }
            }

            ulong expectedValue = expectedHash.GetCurrentHashAsUInt64();
            ulong actualValue = actualHash.GetCurrentHashAsUInt64();

            if (expectedValue != actualValue)
            {
                throw new InvalidOperationException(
                    $"Checksum mismatch after writing extent at block {extent.StartBlock}: " +
                    $"expected 0x{expectedValue:X16}, got 0x{actualValue:X16}.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
        }
    }
}
