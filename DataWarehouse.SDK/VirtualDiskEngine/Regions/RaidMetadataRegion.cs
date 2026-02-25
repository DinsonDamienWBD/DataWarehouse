using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Regions;

/// <summary>
/// RAID strategy identifying the data redundancy scheme.
/// </summary>
public enum RaidStrategy : byte
{
    /// <summary>No RAID (single device).</summary>
    None = 0,

    /// <summary>RAID 0: striping, no redundancy.</summary>
    Raid0 = 1,

    /// <summary>RAID 1: mirroring.</summary>
    Raid1 = 2,

    /// <summary>RAID 5: striping with single parity.</summary>
    Raid5 = 3,

    /// <summary>RAID 6: striping with double parity.</summary>
    Raid6 = 4,

    /// <summary>RAID 10: mirrored stripes.</summary>
    Raid10 = 5,

    /// <summary>RAID-Z1: ZFS-style single parity.</summary>
    RaidZ1 = 6,

    /// <summary>RAID-Z2: ZFS-style double parity.</summary>
    RaidZ2 = 7,

    /// <summary>RAID-Z3: ZFS-style triple parity.</summary>
    RaidZ3 = 8
}

/// <summary>
/// Status of a shard (storage device member) in a RAID array.
/// </summary>
public enum ShardStatus : byte
{
    /// <summary>Shard is online and healthy.</summary>
    Online = 0,

    /// <summary>Shard is operating in degraded mode.</summary>
    Degraded = 1,

    /// <summary>Shard is offline / unavailable.</summary>
    Offline = 2,

    /// <summary>Shard is being rebuilt.</summary>
    Rebuilding = 3,

    /// <summary>Shard is a hot spare.</summary>
    Spare = 4
}

/// <summary>
/// Parity computation type.
/// </summary>
public enum ParityType : byte
{
    /// <summary>XOR-based parity.</summary>
    Xor = 0,

    /// <summary>Reed-Solomon erasure coding.</summary>
    ReedSolomon = 1,

    /// <summary>Diagonal parity (EVENODD / RDP).</summary>
    Diagonal = 2
}

/// <summary>
/// Rebuild operation status.
/// </summary>
public enum RebuildStatus : byte
{
    /// <summary>Rebuild has not started.</summary>
    NotStarted = 0,

    /// <summary>Rebuild is in progress.</summary>
    InProgress = 1,

    /// <summary>Rebuild is paused.</summary>
    Paused = 2,

    /// <summary>Rebuild completed successfully.</summary>
    Completed = 3,

    /// <summary>Rebuild failed.</summary>
    Failed = 4
}

/// <summary>
/// Describes a single shard (storage device) in a RAID array.
/// Serialized size: 40 bytes.
/// </summary>
public readonly struct ShardDescriptor : IEquatable<ShardDescriptor>
{
    /// <summary>Serialized size per shard in bytes.</summary>
    public const int SerializedSize = 40;

    /// <summary>Unique identifier of the storage device.</summary>
    public Guid DeviceId { get; }

    /// <summary>First block on this device.</summary>
    public long StartBlock { get; }

    /// <summary>Number of blocks on this device.</summary>
    public long BlockCount { get; }

    /// <summary>Shard status (0=Online, 1=Degraded, 2=Offline, 3=Rebuilding, 4=Spare).</summary>
    public byte Status { get; }

    /// <summary>Reserved byte 1.</summary>
    public byte Reserved1 { get; }

    /// <summary>Reserved byte 2.</summary>
    public byte Reserved2 { get; }

    /// <summary>Reserved byte 3.</summary>
    public byte Reserved3 { get; }

    /// <summary>Position in the stripe unit.</summary>
    public int StripeIndex { get; }

    public ShardDescriptor(Guid deviceId, long startBlock, long blockCount, byte status, int stripeIndex)
    {
        DeviceId = deviceId;
        StartBlock = startBlock;
        BlockCount = blockCount;
        Status = status;
        Reserved1 = 0;
        Reserved2 = 0;
        Reserved3 = 0;
        StripeIndex = stripeIndex;
    }

    /// <summary>Writes this shard descriptor into the buffer at the given offset.</summary>
    internal void WriteTo(Span<byte> buffer, int offset)
    {
        DeviceId.TryWriteBytes(buffer.Slice(offset, 16));
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset + 16), StartBlock);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset + 24), BlockCount);
        buffer[offset + 32] = Status;
        buffer[offset + 33] = Reserved1;
        buffer[offset + 34] = Reserved2;
        buffer[offset + 35] = Reserved3;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(offset + 36), StripeIndex);
    }

    /// <summary>Reads a shard descriptor from the buffer at the given offset.</summary>
    internal static ShardDescriptor ReadFrom(ReadOnlySpan<byte> buffer, int offset)
    {
        var deviceId = new Guid(buffer.Slice(offset, 16));
        var startBlock = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset + 16));
        var blockCount = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset + 24));
        var status = buffer[offset + 32];
        var stripeIndex = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset + 36));
        return new ShardDescriptor(deviceId, startBlock, blockCount, status, stripeIndex);
    }

    /// <inheritdoc />
    public bool Equals(ShardDescriptor other) =>
        DeviceId == other.DeviceId && StartBlock == other.StartBlock
        && BlockCount == other.BlockCount && Status == other.Status
        && StripeIndex == other.StripeIndex;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ShardDescriptor other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(DeviceId, StartBlock, BlockCount, Status, StripeIndex);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(ShardDescriptor left, ShardDescriptor right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(ShardDescriptor left, ShardDescriptor right) => !left.Equals(right);
}

/// <summary>
/// Describes parity placement for a stripe group.
/// Serialized size: 12 bytes.
/// </summary>
public readonly struct ParityDescriptor : IEquatable<ParityDescriptor>
{
    /// <summary>Serialized size per descriptor in bytes.</summary>
    public const int SerializedSize = 12;

    /// <summary>Which stripe group this descriptor applies to.</summary>
    public int StripeGroupIndex { get; }

    /// <summary>Which shard holds parity data for this group.</summary>
    public int ParityShardIndex { get; }

    /// <summary>Parity computation type (0=XOR, 1=ReedSolomon, 2=Diagonal).</summary>
    public byte ParityTypeValue { get; }

    /// <summary>Reserved byte 1.</summary>
    public byte Reserved1 { get; }

    /// <summary>Reserved byte 2.</summary>
    public byte Reserved2 { get; }

    /// <summary>Reserved byte 3.</summary>
    public byte Reserved3 { get; }

    public ParityDescriptor(int stripeGroupIndex, int parityShardIndex, byte parityTypeValue)
    {
        StripeGroupIndex = stripeGroupIndex;
        ParityShardIndex = parityShardIndex;
        ParityTypeValue = parityTypeValue;
        Reserved1 = 0;
        Reserved2 = 0;
        Reserved3 = 0;
    }

    /// <summary>Writes this parity descriptor into the buffer at the given offset.</summary>
    internal void WriteTo(Span<byte> buffer, int offset)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(offset), StripeGroupIndex);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(offset + 4), ParityShardIndex);
        buffer[offset + 8] = ParityTypeValue;
        buffer[offset + 9] = Reserved1;
        buffer[offset + 10] = Reserved2;
        buffer[offset + 11] = Reserved3;
    }

    /// <summary>Reads a parity descriptor from the buffer at the given offset.</summary>
    internal static ParityDescriptor ReadFrom(ReadOnlySpan<byte> buffer, int offset)
    {
        var stripeGroup = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset));
        var parityShard = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset + 4));
        var parityType = buffer[offset + 8];
        return new ParityDescriptor(stripeGroup, parityShard, parityType);
    }

    /// <inheritdoc />
    public bool Equals(ParityDescriptor other) =>
        StripeGroupIndex == other.StripeGroupIndex
        && ParityShardIndex == other.ParityShardIndex
        && ParityTypeValue == other.ParityTypeValue;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ParityDescriptor other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(StripeGroupIndex, ParityShardIndex, ParityTypeValue);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(ParityDescriptor left, ParityDescriptor right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(ParityDescriptor left, ParityDescriptor right) => !left.Equals(right);
}

/// <summary>
/// Tracks the progress of a RAID rebuild operation for a single device.
/// Serialized size: 64 bytes.
/// </summary>
public readonly struct RebuildProgress : IEquatable<RebuildProgress>
{
    /// <summary>Serialized size per rebuild entry in bytes.</summary>
    public const int SerializedSize = 64;

    /// <summary>Device being rebuilt.</summary>
    public Guid DeviceId { get; }

    /// <summary>Total number of blocks to rebuild.</summary>
    public long TotalBlocks { get; }

    /// <summary>Number of blocks rebuilt so far.</summary>
    public long CompletedBlocks { get; }

    /// <summary>Last block successfully rebuilt.</summary>
    public long LastRebuiltBlock { get; }

    /// <summary>UTC ticks when the rebuild started.</summary>
    public long StartTimeUtcTicks { get; }

    /// <summary>UTC ticks of the last progress update.</summary>
    public long LastUpdateUtcTicks { get; }

    /// <summary>Rebuild status (0=NotStarted, 1=InProgress, 2=Paused, 3=Completed, 4=Failed).</summary>
    public byte StatusValue { get; }

    /// <summary>Reserved byte 1.</summary>
    public byte Reserved1 { get; }

    /// <summary>Reserved byte 2.</summary>
    public byte Reserved2 { get; }

    /// <summary>Reserved byte 3.</summary>
    public byte Reserved3 { get; }

    /// <summary>Number of errors encountered during rebuild.</summary>
    public int ErrorCount { get; }

    public RebuildProgress(
        Guid deviceId,
        long totalBlocks,
        long completedBlocks,
        long lastRebuiltBlock,
        long startTimeUtcTicks,
        long lastUpdateUtcTicks,
        byte statusValue,
        int errorCount)
    {
        DeviceId = deviceId;
        TotalBlocks = totalBlocks;
        CompletedBlocks = completedBlocks;
        LastRebuiltBlock = lastRebuiltBlock;
        StartTimeUtcTicks = startTimeUtcTicks;
        LastUpdateUtcTicks = lastUpdateUtcTicks;
        StatusValue = statusValue;
        Reserved1 = 0;
        Reserved2 = 0;
        Reserved3 = 0;
        ErrorCount = errorCount;
    }

    /// <summary>Writes this rebuild progress into the buffer at the given offset.</summary>
    internal void WriteTo(Span<byte> buffer, int offset)
    {
        DeviceId.TryWriteBytes(buffer.Slice(offset, 16));
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset + 16), TotalBlocks);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset + 24), CompletedBlocks);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset + 32), LastRebuiltBlock);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset + 40), StartTimeUtcTicks);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset + 48), LastUpdateUtcTicks);
        buffer[offset + 56] = StatusValue;
        buffer[offset + 57] = Reserved1;
        buffer[offset + 58] = Reserved2;
        buffer[offset + 59] = Reserved3;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(offset + 60), ErrorCount);
    }

    /// <summary>Reads a rebuild progress from the buffer at the given offset.</summary>
    internal static RebuildProgress ReadFrom(ReadOnlySpan<byte> buffer, int offset)
    {
        var deviceId = new Guid(buffer.Slice(offset, 16));
        var totalBlocks = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset + 16));
        var completedBlocks = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset + 24));
        var lastRebuiltBlock = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset + 32));
        var startTime = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset + 40));
        var lastUpdate = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset + 48));
        var status = buffer[offset + 56];
        var errorCount = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset + 60));
        return new RebuildProgress(deviceId, totalBlocks, completedBlocks, lastRebuiltBlock, startTime, lastUpdate, status, errorCount);
    }

    /// <inheritdoc />
    public bool Equals(RebuildProgress other) =>
        DeviceId == other.DeviceId && TotalBlocks == other.TotalBlocks
        && CompletedBlocks == other.CompletedBlocks && LastRebuiltBlock == other.LastRebuiltBlock
        && StatusValue == other.StatusValue && ErrorCount == other.ErrorCount;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is RebuildProgress other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(DeviceId, TotalBlocks, CompletedBlocks, StatusValue);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(RebuildProgress left, RebuildProgress right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(RebuildProgress left, RebuildProgress right) => !left.Equals(right);
}

/// <summary>
/// RAID Metadata Region: stores shard maps, parity layout descriptors, and rebuild progress
/// for all supported RAID strategies. Serialized across 2 blocks using <see cref="BlockTypeTags.RAID"/>
/// type tag.
/// </summary>
/// <remarks>
/// Block 0 layout: [Strategy:1][StripeSize:4 LE][DataShardCount:1][ParityShardCount:1]
///                  [ActiveShardCount:1][ParityDescCount:2 LE][RebuildCount:1][Reserved:5]
///                  [ShardDescriptor x 64 (2560 bytes)][zero-fill][UniversalBlockTrailer]
///                  Header: 16 bytes. Shards: 2560. Total payload: 2576. Fits in 4080.
///
/// Block 1 layout: [ParityDescriptor entries (up to 256, 3072 bytes)]
///                  [RebuildProgress entries (up to 4, 256 bytes)]
///                  [zero-fill][UniversalBlockTrailer]
///                  Total payload: 3328. Fits in 4080.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 72: VDE regions -- RAID Metadata (VREG-06)")]
public sealed class RaidMetadataRegion
{
    /// <summary>Maximum number of shard devices.</summary>
    public const int MaxShards = 64;

    /// <summary>Maximum number of parity descriptors.</summary>
    public const int MaxParityDescriptors = 256;

    /// <summary>Maximum number of concurrent rebuild operations.</summary>
    public const int MaxConcurrentRebuilds = 4;

    /// <summary>Number of blocks this region occupies.</summary>
    public const int BlockCount = 2;

    /// <summary>Size of the fixed header at the start of block 0.</summary>
    private const int Block0HeaderSize = 16;

    private readonly ShardDescriptor[] _shards = new ShardDescriptor[MaxShards];
    private readonly List<ParityDescriptor> _parityLayout = new();
    private readonly RebuildProgress[] _rebuilds = new RebuildProgress[MaxConcurrentRebuilds];

    /// <summary>RAID strategy in use.</summary>
    public RaidStrategy Strategy { get; set; }

    /// <summary>Number of blocks per stripe unit.</summary>
    public int StripeSize { get; set; }

    /// <summary>Number of data shards (excludes parity shards).</summary>
    public int DataShardCount { get; set; }

    /// <summary>Number of parity shards.</summary>
    public int ParityShardCount { get; set; }

    /// <summary>Monotonic generation number for torn-write detection.</summary>
    public uint Generation { get; set; }

    /// <summary>Creates a new empty RAID metadata region.</summary>
    public RaidMetadataRegion()
    {
    }

    /// <summary>
    /// Sets the shard descriptor at the specified index.
    /// </summary>
    /// <param name="index">Shard index (0..63).</param>
    /// <param name="shard">The shard descriptor.</param>
    public void SetShard(int index, ShardDescriptor shard)
    {
        if (index < 0 || index >= MaxShards)
            throw new ArgumentOutOfRangeException(nameof(index), $"Shard index must be 0..{MaxShards - 1}.");
        _shards[index] = shard;
    }

    /// <summary>
    /// Gets the shard descriptor at the specified index.
    /// </summary>
    /// <param name="index">Shard index (0..63).</param>
    /// <returns>The shard descriptor.</returns>
    public ShardDescriptor GetShard(int index)
    {
        if (index < 0 || index >= MaxShards)
            throw new ArgumentOutOfRangeException(nameof(index), $"Shard index must be 0..{MaxShards - 1}.");
        return _shards[index];
    }

    /// <summary>
    /// Returns all non-Offline shards.
    /// </summary>
    public IReadOnlyList<ShardDescriptor> GetActiveShards()
    {
        var active = new List<ShardDescriptor>();
        for (int i = 0; i < MaxShards; i++)
        {
            if (_shards[i].DeviceId != Guid.Empty && _shards[i].Status != (byte)ShardStatus.Offline)
                active.Add(_shards[i]);
        }
        return active;
    }

    /// <summary>
    /// Adds a parity descriptor to the layout.
    /// </summary>
    /// <param name="pd">The parity descriptor.</param>
    /// <exception cref="InvalidOperationException">Maximum parity descriptor count reached.</exception>
    public void AddParityDescriptor(ParityDescriptor pd)
    {
        if (_parityLayout.Count >= MaxParityDescriptors)
            throw new InvalidOperationException($"Cannot exceed {MaxParityDescriptors} parity descriptors.");
        _parityLayout.Add(pd);
    }

    /// <summary>
    /// Returns the parity layout descriptors.
    /// </summary>
    public IReadOnlyList<ParityDescriptor> GetParityLayout() => _parityLayout.AsReadOnly();

    /// <summary>
    /// Sets the rebuild progress at the specified index.
    /// </summary>
    /// <param name="index">Rebuild slot index (0..3).</param>
    /// <param name="progress">The rebuild progress.</param>
    public void SetRebuildProgress(int index, RebuildProgress progress)
    {
        if (index < 0 || index >= MaxConcurrentRebuilds)
            throw new ArgumentOutOfRangeException(nameof(index), $"Rebuild index must be 0..{MaxConcurrentRebuilds - 1}.");
        _rebuilds[index] = progress;
    }

    /// <summary>
    /// Gets the rebuild progress at the specified index.
    /// </summary>
    /// <param name="index">Rebuild slot index (0..3).</param>
    /// <returns>The rebuild progress.</returns>
    public RebuildProgress GetRebuildProgress(int index)
    {
        if (index < 0 || index >= MaxConcurrentRebuilds)
            throw new ArgumentOutOfRangeException(nameof(index), $"Rebuild index must be 0..{MaxConcurrentRebuilds - 1}.");
        return _rebuilds[index];
    }

    /// <summary>
    /// Returns true if any rebuild slot has InProgress status.
    /// </summary>
    public bool HasActiveRebuild
    {
        get
        {
            for (int i = 0; i < MaxConcurrentRebuilds; i++)
            {
                if (_rebuilds[i].StatusValue == (byte)RebuildStatus.InProgress)
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Calculates the rebuild percentage for the specified rebuild slot.
    /// </summary>
    /// <param name="index">Rebuild slot index (0..3).</param>
    /// <returns>Percentage completed (0.0 to 100.0).</returns>
    public double GetRebuildPercentage(int index)
    {
        if (index < 0 || index >= MaxConcurrentRebuilds)
            throw new ArgumentOutOfRangeException(nameof(index), $"Rebuild index must be 0..{MaxConcurrentRebuilds - 1}.");

        var rebuild = _rebuilds[index];
        if (rebuild.TotalBlocks <= 0) return 0.0;
        return (double)rebuild.CompletedBlocks / rebuild.TotalBlocks * 100.0;
    }

    /// <summary>
    /// Serializes the RAID metadata into a 2-block buffer with
    /// <see cref="UniversalBlockTrailer"/> on each block.
    /// </summary>
    /// <param name="buffer">Target buffer, must be at least blockSize * 2 bytes.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    public void Serialize(Span<byte> buffer, int blockSize)
    {
        int totalSize = blockSize * BlockCount;
        if (buffer.Length < totalSize)
            throw new ArgumentException($"Buffer must be at least {totalSize} bytes.", nameof(buffer));
        if (blockSize < FormatConstants.MinBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"Block size must be at least {FormatConstants.MinBlockSize} bytes.");

        // Clear entire buffer
        buffer.Slice(0, totalSize).Clear();

        // Count active shards
        byte activeShardCount = 0;
        for (int i = 0; i < MaxShards; i++)
        {
            if (_shards[i].DeviceId != Guid.Empty)
                activeShardCount++;
        }

        // Count active rebuilds
        byte rebuildCount = 0;
        for (int i = 0; i < MaxConcurrentRebuilds; i++)
        {
            if (_rebuilds[i].DeviceId != Guid.Empty)
                rebuildCount++;
        }

        // ── Block 0: [Header:16][ShardDescriptor x 64][zero-fill][Trailer] ──
        var block0 = buffer.Slice(0, blockSize);
        block0[0] = (byte)Strategy;
        BinaryPrimitives.WriteInt32LittleEndian(block0.Slice(1), StripeSize);
        block0[5] = (byte)DataShardCount;
        block0[6] = (byte)ParityShardCount;
        block0[7] = activeShardCount;
        BinaryPrimitives.WriteUInt16LittleEndian(block0.Slice(8), (ushort)_parityLayout.Count);
        block0[10] = rebuildCount;
        // bytes 11..15 reserved (already zeroed)

        int offset = Block0HeaderSize;
        for (int i = 0; i < MaxShards; i++)
        {
            _shards[i].WriteTo(block0, offset);
            offset += ShardDescriptor.SerializedSize;
        }

        UniversalBlockTrailer.Write(block0, blockSize, BlockTypeTags.RAID, Generation);

        // ── Block 1: [ParityDescriptor entries][RebuildProgress entries][zero-fill][Trailer] ──
        var block1 = buffer.Slice(blockSize, blockSize);
        offset = 0;
        for (int i = 0; i < _parityLayout.Count; i++)
        {
            _parityLayout[i].WriteTo(block1, offset);
            offset += ParityDescriptor.SerializedSize;
        }

        // Skip to after max parity area for rebuild entries (fixed offset for predictable layout)
        offset = MaxParityDescriptors * ParityDescriptor.SerializedSize;
        for (int i = 0; i < MaxConcurrentRebuilds; i++)
        {
            _rebuilds[i].WriteTo(block1, offset);
            offset += RebuildProgress.SerializedSize;
        }

        UniversalBlockTrailer.Write(block1, blockSize, BlockTypeTags.RAID, Generation);
    }

    /// <summary>
    /// Deserializes a 2-block buffer into a <see cref="RaidMetadataRegion"/>,
    /// verifying block trailers.
    /// </summary>
    /// <param name="buffer">Source buffer containing 2 blocks.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <returns>A populated <see cref="RaidMetadataRegion"/>.</returns>
    /// <exception cref="InvalidDataException">Thrown if block trailers fail verification.</exception>
    public static RaidMetadataRegion Deserialize(ReadOnlySpan<byte> buffer, int blockSize)
    {
        int totalSize = blockSize * BlockCount;
        if (buffer.Length < totalSize)
            throw new ArgumentException($"Buffer must be at least {totalSize} bytes.", nameof(buffer));
        if (blockSize < FormatConstants.MinBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"Block size must be at least {FormatConstants.MinBlockSize} bytes.");

        var block0 = buffer.Slice(0, blockSize);
        var block1 = buffer.Slice(blockSize, blockSize);

        // Verify block trailers
        if (!UniversalBlockTrailer.Verify(block0, blockSize))
            throw new InvalidDataException("RAID Metadata block 0 trailer verification failed.");
        if (!UniversalBlockTrailer.Verify(block1, blockSize))
            throw new InvalidDataException("RAID Metadata block 1 trailer verification failed.");

        var trailer = UniversalBlockTrailer.Read(block0, blockSize);

        var region = new RaidMetadataRegion
        {
            Generation = trailer.GenerationNumber,
            Strategy = (RaidStrategy)block0[0],
            StripeSize = BinaryPrimitives.ReadInt32LittleEndian(block0.Slice(1)),
            DataShardCount = block0[5],
            ParityShardCount = block0[6]
        };

        int parityDescCount = BinaryPrimitives.ReadUInt16LittleEndian(block0.Slice(8));
        int rebuildCount = block0[10];

        // Read shard descriptors
        int offset = Block0HeaderSize;
        for (int i = 0; i < MaxShards; i++)
        {
            region._shards[i] = ShardDescriptor.ReadFrom(block0, offset);
            offset += ShardDescriptor.SerializedSize;
        }

        // Read parity descriptors from block 1
        offset = 0;
        for (int i = 0; i < parityDescCount && i < MaxParityDescriptors; i++)
        {
            region._parityLayout.Add(ParityDescriptor.ReadFrom(block1, offset));
            offset += ParityDescriptor.SerializedSize;
        }

        // Read rebuild progress from fixed offset in block 1
        offset = MaxParityDescriptors * ParityDescriptor.SerializedSize;
        int rebuildsToRead = Math.Min(rebuildCount, MaxConcurrentRebuilds);
        for (int i = 0; i < rebuildsToRead; i++)
        {
            region._rebuilds[i] = RebuildProgress.ReadFrom(block1, offset);
            offset += RebuildProgress.SerializedSize;
        }

        return region;
    }
}
