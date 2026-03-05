using System.Buffers.Binary;
using System.Numerics;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Regions;

/// <summary>
/// Sync status for a replication watermark.
/// </summary>
public enum SyncStatus : byte
{
    /// <summary>Replica is idle (not actively syncing).</summary>
    Idle = 0,

    /// <summary>Replica is currently syncing.</summary>
    Syncing = 1,

    /// <summary>Sync completed successfully.</summary>
    Complete = 2,

    /// <summary>Sync failed.</summary>
    Failed = 3
}

/// <summary>
/// Dotted Version Vector entry tracking causal ordering for a single replica.
/// Serialized size: 32 bytes.
/// </summary>
public readonly struct DottedVersionVector : IEquatable<DottedVersionVector>
{
    /// <summary>Serialized size per entry in bytes.</summary>
    public const int SerializedSize = 32;

    /// <summary>Unique identifier of the replica node.</summary>
    public Guid ReplicaId { get; }

    /// <summary>Logical clock value (monotonically increasing per replica).</summary>
    public long Counter { get; }

    /// <summary>Wall-clock timestamp (UTC ticks) for conflict resolution hints.</summary>
    public long TimestampUtcTicks { get; }

    public DottedVersionVector(Guid replicaId, long counter, long timestampUtcTicks)
    {
        ReplicaId = replicaId;
        Counter = counter;
        TimestampUtcTicks = timestampUtcTicks;
    }

    /// <summary>Writes this DVV entry into the buffer at the given offset.</summary>
    internal void WriteTo(Span<byte> buffer, int offset)
    {
        ReplicaId.TryWriteBytes(buffer.Slice(offset, 16));
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset + 16), Counter);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset + 24), TimestampUtcTicks);
    }

    /// <summary>Reads a DVV entry from the buffer at the given offset.</summary>
    internal static DottedVersionVector ReadFrom(ReadOnlySpan<byte> buffer, int offset)
    {
        var replicaId = new Guid(buffer.Slice(offset, 16));
        var counter = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset + 16));
        var timestamp = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset + 24));
        return new DottedVersionVector(replicaId, counter, timestamp);
    }

    /// <inheritdoc />
    public bool Equals(DottedVersionVector other) =>
        ReplicaId == other.ReplicaId && Counter == other.Counter && TimestampUtcTicks == other.TimestampUtcTicks;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is DottedVersionVector other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(ReplicaId, Counter, TimestampUtcTicks);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(DottedVersionVector left, DottedVersionVector right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(DottedVersionVector left, DottedVersionVector right) => !left.Equals(right);
}

/// <summary>
/// Per-replica watermark tracking sync progress. Serialized size: 48 bytes.
/// </summary>
public readonly struct ReplicationWatermark : IEquatable<ReplicationWatermark>
{
    /// <summary>Serialized size per entry in bytes.</summary>
    public const int SerializedSize = 48;

    /// <summary>Unique identifier of the replica node.</summary>
    public Guid ReplicaId { get; }

    /// <summary>Highest block number known to be replicated to this replica.</summary>
    public long LastSyncedBlock { get; }

    /// <summary>Generation at time of last successful sync.</summary>
    public long LastSyncedGeneration { get; }

    /// <summary>UTC ticks when sync completed.</summary>
    public long SyncTimestampUtcTicks { get; }

    /// <summary>Current sync status (0=Idle, 1=Syncing, 2=Complete, 3=Failed).</summary>
    public byte SyncStatusValue { get; }

    /// <summary>Reserved byte 1.</summary>
    public byte Reserved1 { get; }

    /// <summary>Reserved byte 2.</summary>
    public byte Reserved2 { get; }

    /// <summary>Reserved byte 3.</summary>
    public byte Reserved3 { get; }

    /// <summary>Number of blocks awaiting replication to this replica.</summary>
    public int PendingBlockCount { get; }

    public ReplicationWatermark(
        Guid replicaId,
        long lastSyncedBlock,
        long lastSyncedGeneration,
        long syncTimestampUtcTicks,
        byte syncStatusValue,
        int pendingBlockCount)
    {
        ReplicaId = replicaId;
        LastSyncedBlock = lastSyncedBlock;
        LastSyncedGeneration = lastSyncedGeneration;
        SyncTimestampUtcTicks = syncTimestampUtcTicks;
        SyncStatusValue = syncStatusValue;
        Reserved1 = 0;
        Reserved2 = 0;
        Reserved3 = 0;
        PendingBlockCount = pendingBlockCount;
    }

    /// <summary>Writes this watermark into the buffer at the given offset.</summary>
    internal void WriteTo(Span<byte> buffer, int offset)
    {
        ReplicaId.TryWriteBytes(buffer.Slice(offset, 16));
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset + 16), LastSyncedBlock);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset + 24), LastSyncedGeneration);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset + 32), SyncTimestampUtcTicks);
        buffer[offset + 40] = SyncStatusValue;
        buffer[offset + 41] = Reserved1;
        buffer[offset + 42] = Reserved2;
        buffer[offset + 43] = Reserved3;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(offset + 44), PendingBlockCount);
    }

    /// <summary>Reads a watermark from the buffer at the given offset.</summary>
    internal static ReplicationWatermark ReadFrom(ReadOnlySpan<byte> buffer, int offset)
    {
        var replicaId = new Guid(buffer.Slice(offset, 16));
        var lastSyncedBlock = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset + 16));
        var lastSyncedGen = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset + 24));
        var syncTimestamp = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset + 32));
        var syncStatus = buffer[offset + 40];
        var pendingCount = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset + 44));
        return new ReplicationWatermark(replicaId, lastSyncedBlock, lastSyncedGen, syncTimestamp, syncStatus, pendingCount);
    }

    /// <inheritdoc />
    public bool Equals(ReplicationWatermark other) =>
        ReplicaId == other.ReplicaId && LastSyncedBlock == other.LastSyncedBlock
        && LastSyncedGeneration == other.LastSyncedGeneration
        && SyncTimestampUtcTicks == other.SyncTimestampUtcTicks
        && SyncStatusValue == other.SyncStatusValue
        && PendingBlockCount == other.PendingBlockCount;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ReplicationWatermark other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(ReplicaId, LastSyncedBlock, LastSyncedGeneration, SyncStatusValue);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(ReplicationWatermark left, ReplicationWatermark right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(ReplicationWatermark left, ReplicationWatermark right) => !left.Equals(right);
}

/// <summary>
/// Replication State Region: tracks causal ordering (DVV), per-replica sync watermarks,
/// and a dirty bitmap identifying blocks modified since last successful replication.
/// Serialized across 2+ blocks using <see cref="BlockTypeTags.REPL"/> type tag.
/// </summary>
/// <remarks>
/// Block 0 layout: [ActiveReplicaCount:2 LE][TrackedBlockCount:4 LE][Reserved:2]
///                  [DVV entries x 32 (1024 bytes)][Watermark entries x 32 (1536 bytes)]
///                  [zero-fill][UniversalBlockTrailer]
///                  Header: 8 bytes. DVV: 1024. Watermarks: 1536. Total payload: 2568.
///
/// Block 1..N:     [DirtyBitmap bytes][zero-fill][UniversalBlockTrailer]
///                  Max bitmap per block payload = payloadSize bytes = payloadSize * 8 blocks.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 72: VDE regions -- Replication State (VREG-05)")]
public sealed class ReplicationStateRegion
{
    /// <summary>Maximum number of tracked replica nodes.</summary>
    public const int MaxReplicas = 32;

    /// <summary>Size of the fixed header at the start of block 0.</summary>
    private const int Block0HeaderSize = 8;

    private readonly object _stateLock = new();
    private readonly DottedVersionVector[] _vectors = new DottedVersionVector[MaxReplicas];
    private readonly ReplicationWatermark[] _watermarks = new ReplicationWatermark[MaxReplicas];
    private byte[] _dirtyBitmap;

    /// <summary>Number of data blocks the dirty bitmap covers.</summary>
    public int TrackedBlockCount { get; }

    /// <summary>Number of replicas currently being tracked (with non-empty ReplicaId).</summary>
    public int ActiveReplicaCount { get; private set; }

    /// <summary>Monotonic generation number for torn-write detection.</summary>
    public uint Generation { get; set; }

    /// <summary>
    /// Creates a new replication state region tracking the specified number of data blocks.
    /// </summary>
    /// <param name="trackedBlockCount">Number of data blocks to track in the dirty bitmap.</param>
    public ReplicationStateRegion(int trackedBlockCount)
    {
        if (trackedBlockCount < 0)
            throw new ArgumentOutOfRangeException(nameof(trackedBlockCount), "Tracked block count must be non-negative.");

        TrackedBlockCount = trackedBlockCount;
        _dirtyBitmap = new byte[(trackedBlockCount + 7) / 8];
    }

    /// <summary>
    /// Sets the DVV vector for a replica at the specified index.
    /// </summary>
    /// <param name="replicaIndex">Index (0..31).</param>
    /// <param name="dvv">The dotted version vector entry.</param>
    public void SetVector(int replicaIndex, DottedVersionVector dvv)
    {
        ValidateReplicaIndex(replicaIndex);
        lock (_stateLock)
        {
            _vectors[replicaIndex] = dvv;
            RecalculateActiveCount();
        }
    }

    /// <summary>
    /// Gets the DVV vector for a replica at the specified index.
    /// </summary>
    /// <param name="replicaIndex">Index (0..31).</param>
    /// <returns>The dotted version vector entry.</returns>
    public DottedVersionVector GetVector(int replicaIndex)
    {
        ValidateReplicaIndex(replicaIndex);
        return _vectors[replicaIndex];
    }

    /// <summary>
    /// Increments the logical clock counter for the specified replica and updates the timestamp.
    /// </summary>
    /// <param name="replicaIndex">Index (0..31).</param>
    public void IncrementVector(int replicaIndex)
    {
        ValidateReplicaIndex(replicaIndex);
        lock (_stateLock)
        {
            var current = _vectors[replicaIndex];
            _vectors[replicaIndex] = new DottedVersionVector(
                current.ReplicaId,
                current.Counter + 1,
                DateTime.UtcNow.Ticks);
        }
    }

    /// <summary>
    /// Returns all active DVV vectors (those with non-empty ReplicaId).
    /// </summary>
    public DottedVersionVector[] GetAllVectors()
    {
        var active = new List<DottedVersionVector>();
        for (int i = 0; i < MaxReplicas; i++)
        {
            if (_vectors[i].ReplicaId != Guid.Empty)
                active.Add(_vectors[i]);
        }
        return active.ToArray();
    }

    /// <summary>
    /// Sets the watermark for a replica at the specified index.
    /// </summary>
    /// <param name="replicaIndex">Index (0..31).</param>
    /// <param name="watermark">The replication watermark.</param>
    public void SetWatermark(int replicaIndex, ReplicationWatermark watermark)
    {
        ValidateReplicaIndex(replicaIndex);
        lock (_stateLock) { _watermarks[replicaIndex] = watermark; }
    }

    /// <summary>
    /// Gets the watermark for a replica at the specified index.
    /// </summary>
    /// <param name="replicaIndex">Index (0..31).</param>
    /// <returns>The replication watermark.</returns>
    public ReplicationWatermark GetWatermark(int replicaIndex)
    {
        ValidateReplicaIndex(replicaIndex);
        return _watermarks[replicaIndex];
    }

    /// <summary>
    /// Marks a block as dirty (modified since last replication).
    /// </summary>
    /// <param name="blockIndex">Zero-based block index.</param>
    public void MarkDirty(long blockIndex)
    {
        ValidateBlockIndex(blockIndex);
        int byteIndex = (int)(blockIndex / 8);
        int bitIndex = (int)(blockIndex % 8);
        lock (_stateLock) { _dirtyBitmap[byteIndex] |= (byte)(1 << bitIndex); }
    }

    /// <summary>
    /// Clears the dirty flag for a block.
    /// </summary>
    /// <param name="blockIndex">Zero-based block index.</param>
    public void ClearDirty(long blockIndex)
    {
        ValidateBlockIndex(blockIndex);
        int byteIndex = (int)(blockIndex / 8);
        int bitIndex = (int)(blockIndex % 8);
        lock (_stateLock) { _dirtyBitmap[byteIndex] &= (byte)~(1 << bitIndex); }
    }

    /// <summary>
    /// Checks whether a block is marked as dirty.
    /// </summary>
    /// <param name="blockIndex">Zero-based block index.</param>
    /// <returns>True if the block is dirty.</returns>
    public bool IsDirty(long blockIndex)
    {
        ValidateBlockIndex(blockIndex);
        int byteIndex = (int)(blockIndex / 8);
        int bitIndex = (int)(blockIndex % 8);
        return (_dirtyBitmap[byteIndex] & (1 << bitIndex)) != 0;
    }

    /// <summary>
    /// Returns the total number of dirty blocks (population count of the bitmap).
    /// </summary>
    public int DirtyBlockCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _dirtyBitmap.Length; i++)
                count += BitOperations.PopCount(_dirtyBitmap[i]);
            return count;
        }
    }

    /// <summary>
    /// Clears all dirty flags (zeros the entire bitmap).
    /// </summary>
    public void ClearAllDirty()
    {
        Array.Clear(_dirtyBitmap, 0, _dirtyBitmap.Length);
    }

    /// <summary>
    /// Enumerates all block indices that are marked as dirty.
    /// </summary>
    public IReadOnlyList<long> GetDirtyBlocks()
    {
        var result = new List<long>();
        for (int byteIdx = 0; byteIdx < _dirtyBitmap.Length; byteIdx++)
        {
            if (_dirtyBitmap[byteIdx] == 0) continue;
            for (int bit = 0; bit < 8; bit++)
            {
                if ((_dirtyBitmap[byteIdx] & (1 << bit)) != 0)
                {
                    long blockIndex = (long)byteIdx * 8 + bit;
                    if (blockIndex < TrackedBlockCount)
                        result.Add(blockIndex);
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Computes the number of blocks required to store this region.
    /// Block 0: header + DVV + watermarks. Block 1..N: dirty bitmap.
    /// </summary>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <returns>Total number of blocks required.</returns>
    public int RequiredBlocks(int blockSize)
    {
        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);
        int bitmapBytes = _dirtyBitmap.Length;
        int bitmapBlocks = bitmapBytes > 0 ? (bitmapBytes + payloadSize - 1) / payloadSize : 1;
        return 1 + bitmapBlocks;
    }

    /// <summary>
    /// Serializes the replication state into a multi-block buffer with
    /// <see cref="UniversalBlockTrailer"/> on each block.
    /// </summary>
    /// <param name="buffer">Target buffer, must be at least RequiredBlocks(blockSize) * blockSize bytes.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    public void Serialize(Span<byte> buffer, int blockSize)
    {
        int requiredBlocks = RequiredBlocks(blockSize);
        int totalSize = blockSize * requiredBlocks;
        if (buffer.Length < totalSize)
            throw new ArgumentException($"Buffer must be at least {totalSize} bytes.", nameof(buffer));
        if (blockSize < FormatConstants.MinBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"Block size must be at least {FormatConstants.MinBlockSize} bytes.");

        // Clear entire buffer
        buffer.Slice(0, totalSize).Clear();

        // ── Block 0: [ActiveReplicaCount:2][TrackedBlockCount:4][Reserved:2][DVV x 32][Watermarks x 32] ──
        var block0 = buffer.Slice(0, blockSize);
        BinaryPrimitives.WriteUInt16LittleEndian(block0, (ushort)ActiveReplicaCount);
        BinaryPrimitives.WriteInt32LittleEndian(block0.Slice(2), TrackedBlockCount);
        // bytes 6..7 reserved (already zeroed)

        int offset = Block0HeaderSize;
        for (int i = 0; i < MaxReplicas; i++)
        {
            _vectors[i].WriteTo(block0, offset);
            offset += DottedVersionVector.SerializedSize;
        }

        for (int i = 0; i < MaxReplicas; i++)
        {
            _watermarks[i].WriteTo(block0, offset);
            offset += ReplicationWatermark.SerializedSize;
        }

        UniversalBlockTrailer.Write(block0, blockSize, BlockTypeTags.REPL, Generation);

        // ── Block 1..N: [DirtyBitmap bytes][zero-fill][Trailer] ──
        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);
        int bitmapOffset = 0;
        for (int blk = 1; blk < requiredBlocks; blk++)
        {
            var block = buffer.Slice(blk * blockSize, blockSize);
            int bytesToWrite = Math.Min(payloadSize, _dirtyBitmap.Length - bitmapOffset);
            if (bytesToWrite > 0)
            {
                _dirtyBitmap.AsSpan(bitmapOffset, bytesToWrite).CopyTo(block);
                bitmapOffset += bytesToWrite;
            }
            UniversalBlockTrailer.Write(block, blockSize, BlockTypeTags.REPL, Generation);
        }
    }

    /// <summary>
    /// Deserializes a multi-block buffer into a <see cref="ReplicationStateRegion"/>,
    /// verifying block trailers.
    /// </summary>
    /// <param name="buffer">Source buffer containing the region blocks.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="blockCount">Number of blocks in the buffer for this region.</param>
    /// <returns>A populated <see cref="ReplicationStateRegion"/>.</returns>
    /// <exception cref="InvalidDataException">Thrown if block trailers fail verification.</exception>
    public static ReplicationStateRegion Deserialize(ReadOnlySpan<byte> buffer, int blockSize, int blockCount)
    {
        int totalSize = blockSize * blockCount;
        if (buffer.Length < totalSize)
            throw new ArgumentException($"Buffer must be at least {totalSize} bytes.", nameof(buffer));
        if (blockSize < FormatConstants.MinBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"Block size must be at least {FormatConstants.MinBlockSize} bytes.");
        if (blockCount < 2)
            throw new ArgumentOutOfRangeException(nameof(blockCount), "Replication state requires at least 2 blocks.");

        // Verify all block trailers
        for (int blk = 0; blk < blockCount; blk++)
        {
            var block = buffer.Slice(blk * blockSize, blockSize);
            if (!UniversalBlockTrailer.Verify(block, blockSize))
                throw new InvalidDataException($"Replication State block {blk} trailer verification failed.");
        }

        var block0 = buffer.Slice(0, blockSize);
        var trailer = UniversalBlockTrailer.Read(block0, blockSize);

        // Read header
        int trackedBlockCount = BinaryPrimitives.ReadInt32LittleEndian(block0.Slice(2));

        var region = new ReplicationStateRegion(trackedBlockCount)
        {
            Generation = trailer.GenerationNumber
        };

        // Read DVV vectors
        int offset = Block0HeaderSize;
        for (int i = 0; i < MaxReplicas; i++)
        {
            region._vectors[i] = DottedVersionVector.ReadFrom(block0, offset);
            offset += DottedVersionVector.SerializedSize;
        }

        // Read watermarks
        for (int i = 0; i < MaxReplicas; i++)
        {
            region._watermarks[i] = ReplicationWatermark.ReadFrom(block0, offset);
            offset += ReplicationWatermark.SerializedSize;
        }

        region.RecalculateActiveCount();

        // Read dirty bitmap from block 1..N
        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);
        int bitmapOffset = 0;
        for (int blk = 1; blk < blockCount; blk++)
        {
            var block = buffer.Slice(blk * blockSize, blockSize);
            int bytesToRead = Math.Min(payloadSize, region._dirtyBitmap.Length - bitmapOffset);
            if (bytesToRead > 0)
            {
                block.Slice(0, bytesToRead).CopyTo(region._dirtyBitmap.AsSpan(bitmapOffset, bytesToRead));
                bitmapOffset += bytesToRead;
            }
        }

        return region;
    }

    private void RecalculateActiveCount()
    {
        int count = 0;
        for (int i = 0; i < MaxReplicas; i++)
        {
            if (_vectors[i].ReplicaId != Guid.Empty)
                count++;
        }
        ActiveReplicaCount = count;
    }

    private static void ValidateReplicaIndex(int index)
    {
        if (index < 0 || index >= MaxReplicas)
            throw new ArgumentOutOfRangeException(nameof(index), $"Replica index must be 0..{MaxReplicas - 1}.");
    }

    private void ValidateBlockIndex(long blockIndex)
    {
        if (blockIndex < 0 || blockIndex >= TrackedBlockCount)
            throw new ArgumentOutOfRangeException(nameof(blockIndex),
                $"Block index must be 0..{TrackedBlockCount - 1}.");
    }
}
