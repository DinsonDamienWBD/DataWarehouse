using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Regions;

/// <summary>
/// Per-Raft-group consensus state tracking term, committed/applied indices,
/// voted-for, leader identity, and heartbeat timing.
/// </summary>
/// <remarks>
/// Serialized layout (89 bytes):
/// [GroupId:16][CurrentTerm:8 LE][CommittedIndex:8 LE][AppliedIndex:8 LE]
/// [VotedFor:16][LeaderId:16][MemberCount:1][LastHeartbeatUtcTicks:8 LE]
/// [LastUpdatedUtcTicks:8 LE]
/// Total: 16+8+8+8+16+16+1+8+8 = 89.
/// </remarks>
[SdkCompatibility("6.0.0")]
public readonly record struct ConsensusGroupState
{
    /// <summary>Serialized size per entry in bytes.</summary>
    public const int SerializedSize = 89;

    /// <summary>Raft group identifier.</summary>
    public Guid GroupId { get; init; }

    /// <summary>Current Raft term number.</summary>
    public long CurrentTerm { get; init; }

    /// <summary>Highest committed log index.</summary>
    public long CommittedIndex { get; init; }

    /// <summary>Highest applied log index.</summary>
    public long AppliedIndex { get; init; }

    /// <summary>Candidate voted for in current term (<see cref="Guid.Empty"/> if none).</summary>
    public Guid VotedFor { get; init; }

    /// <summary>Current known leader (<see cref="Guid.Empty"/> if unknown).</summary>
    public Guid LeaderId { get; init; }

    /// <summary>Number of members in the group (for quorum calculation).</summary>
    public byte MemberCount { get; init; }

    /// <summary>Last heartbeat received from leader (UTC ticks).</summary>
    public long LastHeartbeatUtcTicks { get; init; }

    /// <summary>When this state was last persisted (UTC ticks).</summary>
    public long LastUpdatedUtcTicks { get; init; }

    /// <summary>Writes this state into the buffer at the given offset.</summary>
    internal void WriteTo(Span<byte> buffer, int offset)
    {
        GroupId.TryWriteBytes(buffer.Slice(offset, 16));
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset + 16), CurrentTerm);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset + 24), CommittedIndex);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset + 32), AppliedIndex);
        VotedFor.TryWriteBytes(buffer.Slice(offset + 40, 16));
        LeaderId.TryWriteBytes(buffer.Slice(offset + 56, 16));
        buffer[offset + 72] = MemberCount;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset + 73), LastHeartbeatUtcTicks);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset + 81), LastUpdatedUtcTicks);
    }

    /// <summary>Reads a state entry from the buffer at the given offset.</summary>
    internal static ConsensusGroupState ReadFrom(ReadOnlySpan<byte> buffer, int offset)
    {
        return new ConsensusGroupState
        {
            GroupId = new Guid(buffer.Slice(offset, 16)),
            CurrentTerm = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset + 16)),
            CommittedIndex = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset + 24)),
            AppliedIndex = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset + 32)),
            VotedFor = new Guid(buffer.Slice(offset + 40, 16)),
            LeaderId = new Guid(buffer.Slice(offset + 56, 16)),
            MemberCount = buffer[offset + 72],
            LastHeartbeatUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset + 73)),
            LastUpdatedUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset + 81))
        };
    }
}

/// <summary>
/// Consensus Log Region: tracks per-Raft-group term, committed/applied indices,
/// voted-for, and leader identity for distributed coordination.
/// Supports multiple concurrent Raft groups, each with independent state.
/// Serialized using <see cref="BlockTypeTags.CLOG"/> type tag.
/// </summary>
/// <remarks>
/// Header block layout:
/// [GroupCount:4 LE][Reserved:12][ConsensusGroupState entries x GroupCount]
/// [UniversalBlockTrailer]
///
/// Groups are fixed-size (89 bytes each) so overflow calculation is straightforward.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 73: VDE regions -- Consensus Log (VREG-15)")]
public sealed class ConsensusLogRegion
{
    /// <summary>Size of the fixed header: [GroupCount:4][Reserved:12] = 16 bytes.</summary>
    private const int HeaderFieldsSize = 16;

    private readonly Dictionary<Guid, ConsensusGroupState> _groups = new();

    /// <summary>Monotonic generation number for torn-write detection.</summary>
    public uint Generation { get; set; }

    /// <summary>Number of Raft groups currently tracked.</summary>
    public int GroupCount => _groups.Count;

    /// <summary>
    /// Updates (upserts) the state for a Raft group. If the group already exists,
    /// its state is replaced; otherwise a new entry is created.
    /// </summary>
    /// <param name="state">The consensus group state to store.</param>
    public void UpdateGroupState(ConsensusGroupState state)
    {
        _groups[state.GroupId] = state;
    }

    /// <summary>
    /// Returns the state for a specific Raft group, or null if not tracked.
    /// </summary>
    /// <param name="groupId">The Raft group identifier.</param>
    /// <returns>The group state, or null if the group is not tracked.</returns>
    public ConsensusGroupState? GetGroupState(Guid groupId)
    {
        return _groups.TryGetValue(groupId, out var state) ? state : null;
    }

    /// <summary>
    /// Removes tracking for the specified Raft group.
    /// </summary>
    /// <param name="groupId">The Raft group identifier to remove.</param>
    /// <returns>True if the group was found and removed; false otherwise.</returns>
    public bool RemoveGroup(Guid groupId)
    {
        return _groups.Remove(groupId);
    }

    /// <summary>
    /// Returns a snapshot of all tracked group states.
    /// </summary>
    public IReadOnlyList<ConsensusGroupState> GetAllGroups()
    {
        return _groups.Values.ToList().AsReadOnly();
    }

    /// <summary>
    /// Returns all groups where the specified node is the current leader.
    /// </summary>
    /// <param name="leaderId">The node identifier to match as leader.</param>
    /// <returns>Groups led by the specified node.</returns>
    public IReadOnlyList<ConsensusGroupState> GetGroupsByLeader(Guid leaderId)
    {
        var result = new List<ConsensusGroupState>();
        foreach (var state in _groups.Values)
        {
            if (state.LeaderId == leaderId)
                result.Add(state);
        }
        return result.AsReadOnly();
    }

    /// <summary>
    /// Returns the highest term number across all tracked groups.
    /// Useful for staleness detection.
    /// </summary>
    /// <returns>The maximum term, or 0 if no groups are tracked.</returns>
    public long GetHighestTerm()
    {
        long max = 0;
        foreach (var state in _groups.Values)
        {
            if (state.CurrentTerm > max)
                max = state.CurrentTerm;
        }
        return max;
    }

    /// <summary>
    /// Computes the number of blocks required to serialize this region.
    /// </summary>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <returns>Total number of blocks required.</returns>
    public int RequiredBlocks(int blockSize)
    {
        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);
        int groupBytes = _groups.Count * ConsensusGroupState.SerializedSize;
        int availableInBlock0 = payloadSize - HeaderFieldsSize;

        if (groupBytes <= availableInBlock0)
            return 1;

        int overflowBytes = groupBytes - availableInBlock0;
        int overflowBlocks = (overflowBytes + payloadSize - 1) / payloadSize;
        return 1 + overflowBlocks;
    }

    /// <summary>
    /// Serializes the consensus log region into blocks with <see cref="UniversalBlockTrailer"/>
    /// on each block.
    /// </summary>
    /// <param name="buffer">Target buffer (must be at least RequiredBlocks * blockSize bytes).</param>
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

        buffer.Slice(0, totalSize).Clear();

        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);

        // Block 0 header: [GroupCount:4][Reserved:12]
        var block0 = buffer.Slice(0, blockSize);
        BinaryPrimitives.WriteInt32LittleEndian(block0, _groups.Count);
        // bytes 4..15 reserved (already zeroed)

        // Collect groups into a stable list for serialization
        var groupList = _groups.Values.ToList();

        int offset = HeaderFieldsSize;
        int groupIndex = 0;
        int currentBlock = 0;

        while (groupIndex < groupList.Count)
        {
            var block = buffer.Slice(currentBlock * blockSize, blockSize);
            int blockPayloadEnd = payloadSize;

            if (currentBlock > 0)
                offset = 0;

            while (groupIndex < groupList.Count
                && offset + ConsensusGroupState.SerializedSize <= blockPayloadEnd)
            {
                groupList[groupIndex].WriteTo(block, offset);
                offset += ConsensusGroupState.SerializedSize;
                groupIndex++;
            }

            UniversalBlockTrailer.Write(block, blockSize, BlockTypeTags.CLOG, Generation);

            if (groupIndex < groupList.Count)
            {
                currentBlock++;
                offset = 0;
            }
        }

        // Write trailer for block 0 if no groups
        if (groupList.Count == 0)
            UniversalBlockTrailer.Write(block0, blockSize, BlockTypeTags.CLOG, Generation);

        // Write trailers for any remaining blocks
        for (int blk = currentBlock + 1; blk < requiredBlocks; blk++)
        {
            var block = buffer.Slice(blk * blockSize, blockSize);
            UniversalBlockTrailer.Write(block, blockSize, BlockTypeTags.CLOG, Generation);
        }
    }

    /// <summary>
    /// Deserializes a consensus log region from blocks, verifying block trailers.
    /// </summary>
    /// <param name="buffer">Source buffer containing the region blocks.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="blockCount">Number of blocks in the buffer for this region.</param>
    /// <returns>A populated <see cref="ConsensusLogRegion"/>.</returns>
    /// <exception cref="InvalidDataException">Thrown if block trailers fail verification.</exception>
    public static ConsensusLogRegion Deserialize(ReadOnlySpan<byte> buffer, int blockSize, int blockCount)
    {
        int totalSize = blockSize * blockCount;
        if (buffer.Length < totalSize)
            throw new ArgumentException($"Buffer must be at least {totalSize} bytes.", nameof(buffer));
        if (blockSize < FormatConstants.MinBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"Block size must be at least {FormatConstants.MinBlockSize} bytes.");
        if (blockCount < 1)
            throw new ArgumentOutOfRangeException(nameof(blockCount), "At least one block is required.");

        // Verify all block trailers
        for (int blk = 0; blk < blockCount; blk++)
        {
            var block = buffer.Slice(blk * blockSize, blockSize);
            if (!UniversalBlockTrailer.Verify(block, blockSize))
                throw new InvalidDataException($"Consensus log region block {blk} trailer verification failed.");
        }

        var block0 = buffer.Slice(0, blockSize);
        var trailer = UniversalBlockTrailer.Read(block0, blockSize);

        int groupCount = BinaryPrimitives.ReadInt32LittleEndian(block0);

        var region = new ConsensusLogRegion
        {
            Generation = trailer.GenerationNumber
        };

        // Read group entries across blocks
        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);
        int offset = HeaderFieldsSize;
        int currentBlock = 0;
        int groupsRead = 0;

        while (groupsRead < groupCount)
        {
            var block = buffer.Slice(currentBlock * blockSize, blockSize);
            int blockPayloadEnd = payloadSize;

            while (groupsRead < groupCount
                && offset + ConsensusGroupState.SerializedSize <= blockPayloadEnd)
            {
                var state = ConsensusGroupState.ReadFrom(block, offset);
                region._groups[state.GroupId] = state;
                offset += ConsensusGroupState.SerializedSize;
                groupsRead++;
            }

            currentBlock++;
            offset = 0;
        }

        return region;
    }
}
