using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Regions;

/// <summary>
/// Defines the storage role a VDE plays in a separated configuration.
/// </summary>
[SdkCompatibility("6.0.0")]
public enum VdeRole : byte
{
    /// <summary>Stores raw data blocks.</summary>
    Data = 0,

    /// <summary>Stores index structures (B-tree nodes, tag index).</summary>
    Index = 1,

    /// <summary>Stores metadata (inodes, region directory, policies).</summary>
    Metadata = 2,

    /// <summary>Single VDE holds all roles (default, no separation).</summary>
    Combined = 3
}

/// <summary>
/// Assigns a VDE to a specific storage role with routing priority and capacity tracking.
/// </summary>
/// <remarks>
/// Serialized layout (36 bytes):
/// [VdeId:16][Role:1][Priority:1][CapacityBlocks:8][UsedBlocks:8][IsReadOnly:1][Padding:1]
/// </remarks>
[SdkCompatibility("6.0.0")]
public readonly record struct VdeRoleAssignment
{
    /// <summary>Serialized size of one assignment in bytes.</summary>
    public const int SerializedSize = 36; // 16+1+1+8+8+1+1

    /// <summary>The VDE being assigned a role.</summary>
    public Guid VdeId { get; init; }

    /// <summary>What this VDE stores.</summary>
    public VdeRole Role { get; init; }

    /// <summary>Routing priority (0=primary, 1=secondary, 2=tertiary).</summary>
    public byte Priority { get; init; }

    /// <summary>Total blocks available on this VDE for this role.</summary>
    public long CapacityBlocks { get; init; }

    /// <summary>Blocks currently used.</summary>
    public long UsedBlocks { get; init; }

    /// <summary>Whether this VDE is read-only for this role.</summary>
    public bool IsReadOnly { get; init; }

    /// <summary>Writes this assignment to the buffer at the specified offset.</summary>
    internal void WriteTo(Span<byte> buffer, int offset)
    {
        VdeId.TryWriteBytes(buffer.Slice(offset, 16));
        offset += 16;
        buffer[offset++] = (byte)Role;
        buffer[offset++] = Priority;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), CapacityBlocks);
        offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), UsedBlocks);
        offset += 8;
        buffer[offset++] = IsReadOnly ? (byte)1 : (byte)0;
        buffer[offset] = 0; // padding
    }

    /// <summary>Reads an assignment from the buffer at the specified offset.</summary>
    internal static VdeRoleAssignment ReadFrom(ReadOnlySpan<byte> buffer, int offset)
    {
        var vdeId = new Guid(buffer.Slice(offset, 16));
        offset += 16;
        var role = (VdeRole)buffer[offset++];
        byte priority = buffer[offset++];
        long capacityBlocks = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;
        long usedBlocks = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;
        bool isReadOnly = buffer[offset] != 0;

        return new VdeRoleAssignment
        {
            VdeId = vdeId,
            Role = role,
            Priority = priority,
            CapacityBlocks = capacityBlocks,
            UsedBlocks = usedBlocks,
            IsReadOnly = isReadOnly
        };
    }
}

/// <summary>
/// Manages VDE index/metadata separation: allows spreading a logical dataset across
/// multiple VDEs (data on VDE1, index on VDE2, metadata on VDE3) with user-configurable
/// routing and priority-based VDE selection.
/// </summary>
/// <remarks>
/// Serialization layout:
///   Block 0 (header): [AssignmentCount:4 LE][Reserved:12][VdeRoleAssignment entries...]
///   Entries overflow to subsequent blocks as needed.
///   Each block ends with <see cref="UniversalBlockTrailer"/>.
///   Uses <see cref="BlockTypeTags.XREF"/> (differentiated from CrossVdeReferenceRegion
///   by RegionDirectory slot assignment).
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 73: VDE regions -- VDE Separation (VADV-01)")]
public sealed class VdeSeparationManager
{
    /// <summary>Header size: AssignmentCount(4) + Reserved(12) = 16 bytes.</summary>
    private const int HeaderFieldsSize = 16;

    private readonly List<VdeRoleAssignment> _assignments = new();
    private readonly Dictionary<Guid, List<int>> _indexByVdeId = new();

    /// <summary>Monotonic generation number for torn-write detection.</summary>
    public uint Generation { get; set; }

    /// <summary>Number of role assignments stored.</summary>
    public int AssignmentCount => _assignments.Count;

    /// <summary>
    /// True if any assignments exist with different roles on different VDEs
    /// (false if all Combined or empty).
    /// </summary>
    public bool IsSeparated
    {
        get
        {
            if (_assignments.Count == 0) return false;

            var seenRoles = new HashSet<VdeRole>();
            var seenVdes = new HashSet<Guid>();

            for (int i = 0; i < _assignments.Count; i++)
            {
                var a = _assignments[i];
                if (a.Role == VdeRole.Combined) continue;
                seenRoles.Add(a.Role);
                seenVdes.Add(a.VdeId);
            }

            // Separated if multiple distinct non-Combined roles exist across multiple VDEs
            return seenRoles.Count > 1 && seenVdes.Count > 1;
        }
    }

    /// <summary>
    /// Assigns a VDE to a role. Multiple VDEs can have the same role (for redundancy).
    /// One VDE can have multiple roles.
    /// </summary>
    /// <param name="assignment">The role assignment to add.</param>
    public void AssignRole(VdeRoleAssignment assignment)
    {
        int index = _assignments.Count;
        _assignments.Add(assignment);

        if (!_indexByVdeId.TryGetValue(assignment.VdeId, out var indices))
        {
            indices = new List<int>();
            _indexByVdeId[assignment.VdeId] = indices;
        }
        indices.Add(index);
    }

    /// <summary>
    /// Removes a specific role from a VDE.
    /// </summary>
    /// <param name="vdeId">The VDE to remove the role from.</param>
    /// <param name="role">The role to remove.</param>
    /// <returns>True if the assignment was found and removed; false otherwise.</returns>
    public bool RemoveAssignment(Guid vdeId, VdeRole role)
    {
        if (!_indexByVdeId.TryGetValue(vdeId, out var indices))
            return false;

        for (int i = 0; i < indices.Count; i++)
        {
            int listIndex = indices[i];
            if (_assignments[listIndex].Role == role)
            {
                // Remove from the assignments list using swap-with-last
                int lastIndex = _assignments.Count - 1;
                if (listIndex < lastIndex)
                {
                    var lastAssignment = _assignments[lastIndex];
                    _assignments[listIndex] = lastAssignment;

                    // Update the moved item's index in VDE lookup
                    if (_indexByVdeId.TryGetValue(lastAssignment.VdeId, out var lastVdeIndices))
                    {
                        for (int j = 0; j < lastVdeIndices.Count; j++)
                        {
                            if (lastVdeIndices[j] == lastIndex)
                            {
                                lastVdeIndices[j] = listIndex;
                                break;
                            }
                        }
                    }
                }
                _assignments.RemoveAt(lastIndex);

                // Remove from VDE index
                indices.RemoveAt(i);
                if (indices.Count == 0)
                    _indexByVdeId.Remove(vdeId);

                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns all VDEs assigned to this role, ordered by priority (lowest first).
    /// </summary>
    /// <param name="role">The role to query.</param>
    /// <returns>Assignments ordered by priority.</returns>
    public IReadOnlyList<VdeRoleAssignment> GetAssignmentsByRole(VdeRole role)
    {
        var results = new List<VdeRoleAssignment>();
        for (int i = 0; i < _assignments.Count; i++)
        {
            if (_assignments[i].Role == role)
                results.Add(_assignments[i]);
        }
        results.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        return results;
    }

    /// <summary>
    /// Returns all roles assigned to a specific VDE.
    /// </summary>
    /// <param name="vdeId">The VDE to query.</param>
    /// <returns>All role assignments for this VDE.</returns>
    public IReadOnlyList<VdeRoleAssignment> GetAssignmentsByVde(Guid vdeId)
    {
        if (!_indexByVdeId.TryGetValue(vdeId, out var indices))
            return Array.Empty<VdeRoleAssignment>();

        var results = new List<VdeRoleAssignment>(indices.Count);
        for (int i = 0; i < indices.Count; i++)
            results.Add(_assignments[indices[i]]);
        return results;
    }

    /// <summary>
    /// Returns the primary (lowest priority number) non-readonly VDE for a role.
    /// </summary>
    /// <param name="role">The role to resolve.</param>
    /// <returns>The VDE ID, or null if no writable VDE is assigned to this role.</returns>
    public Guid? ResolveVdeForRole(VdeRole role)
    {
        Guid? best = null;
        byte bestPriority = byte.MaxValue;

        for (int i = 0; i < _assignments.Count; i++)
        {
            var a = _assignments[i];
            if (a.Role == role && !a.IsReadOnly && a.Priority < bestPriority)
            {
                bestPriority = a.Priority;
                best = a.VdeId;
            }
        }

        return best;
    }

    /// <summary>
    /// Returns all VDEs for a role, ordered by priority (lowest first).
    /// </summary>
    /// <param name="role">The role to resolve.</param>
    /// <returns>VDE IDs ordered by priority.</returns>
    public IReadOnlyList<Guid> ResolveAllVdesForRole(VdeRole role)
    {
        var assignments = GetAssignmentsByRole(role);
        var result = new List<Guid>(assignments.Count);
        for (int i = 0; i < assignments.Count; i++)
            result.Add(assignments[i].VdeId);
        return result;
    }

    /// <summary>
    /// Generates <see cref="VdeReference"/> entries for all cross-VDE relationships.
    /// For each pair of VDEs with different roles, creates a VdeReference with
    /// ReferenceType matching the target role (DataLink for Data, IndexLink for Index,
    /// MetadataLink for Metadata). These can be added to a <see cref="CrossVdeReferenceRegion"/>.
    /// </summary>
    /// <returns>Cross-VDE references representing the separation topology.</returns>
    public IReadOnlyList<VdeReference> GenerateCrossReferences()
    {
        var references = new List<VdeReference>();
        long nowTicks = DateTime.UtcNow.Ticks;

        for (int i = 0; i < _assignments.Count; i++)
        {
            var source = _assignments[i];
            if (source.Role == VdeRole.Combined) continue;

            for (int j = 0; j < _assignments.Count; j++)
            {
                if (i == j) continue;
                var target = _assignments[j];
                if (target.Role == VdeRole.Combined) continue;
                if (source.VdeId == target.VdeId) continue;
                if (source.Role == target.Role) continue;

                ushort refType = target.Role switch
                {
                    VdeRole.Data => VdeReference.TypeDataLink,
                    VdeRole.Index => VdeReference.TypeIndexLink,
                    VdeRole.Metadata => VdeReference.TypeMetadataLink,
                    _ => VdeReference.TypeFabricLink
                };

                references.Add(new VdeReference
                {
                    ReferenceId = Guid.NewGuid(),
                    SourceVdeId = source.VdeId,
                    TargetVdeId = target.VdeId,
                    SourceInodeNumber = 0, // VDE-level reference
                    TargetInodeNumber = 0,
                    ReferenceType = refType,
                    CreatedUtcTicks = nowTicks
                });
            }
        }

        return references;
    }

    /// <summary>
    /// Computes the number of blocks required to serialize this manager.
    /// </summary>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <returns>Number of blocks required (minimum 1).</returns>
    public int RequiredBlocks(int blockSize)
    {
        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);
        if (_assignments.Count == 0)
            return 1;

        int totalBytes = _assignments.Count * VdeRoleAssignment.SerializedSize;
        int availableInBlock0 = payloadSize - HeaderFieldsSize;
        if (totalBytes <= availableInBlock0)
            return 1;

        int overflowBytes = totalBytes - availableInBlock0;
        int overflowBlocks = (overflowBytes + payloadSize - 1) / payloadSize;
        return 1 + overflowBlocks;
    }

    /// <summary>
    /// Serializes the separation manager into blocks with UniversalBlockTrailer on each block.
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

        // Block 0: Header + assignment entries
        var block0 = buffer.Slice(0, blockSize);
        BinaryPrimitives.WriteInt32LittleEndian(block0, _assignments.Count);
        // bytes 4..15 reserved (already zeroed)

        int offset = HeaderFieldsSize;
        int currentBlock = 0;
        int entryIndex = 0;

        while (entryIndex < _assignments.Count)
        {
            var block = buffer.Slice(currentBlock * blockSize, blockSize);
            int blockPayloadEnd = payloadSize;

            if (currentBlock > 0)
                offset = 0;

            while (entryIndex < _assignments.Count)
            {
                if (offset + VdeRoleAssignment.SerializedSize > blockPayloadEnd)
                    break;

                _assignments[entryIndex].WriteTo(block, offset);
                offset += VdeRoleAssignment.SerializedSize;
                entryIndex++;
            }

            UniversalBlockTrailer.Write(block, blockSize, BlockTypeTags.XREF, Generation);

            if (entryIndex < _assignments.Count)
            {
                currentBlock++;
                offset = 0;
            }
        }

        // Write trailer for block 0 if no assignments
        if (_assignments.Count == 0)
            UniversalBlockTrailer.Write(block0, blockSize, BlockTypeTags.XREF, Generation);

        // Write trailers for any remaining blocks
        for (int blk = currentBlock + 1; blk < requiredBlocks; blk++)
        {
            var block = buffer.Slice(blk * blockSize, blockSize);
            UniversalBlockTrailer.Write(block, blockSize, BlockTypeTags.XREF, Generation);
        }
    }

    /// <summary>
    /// Deserializes a separation manager from blocks, verifying block trailers
    /// and rebuilding the VDE index.
    /// </summary>
    /// <param name="buffer">Source buffer containing the region blocks.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="blockCount">Number of blocks in the buffer for this region.</param>
    /// <returns>A populated <see cref="VdeSeparationManager"/>.</returns>
    /// <exception cref="InvalidDataException">Thrown if block trailers fail verification.</exception>
    public static VdeSeparationManager Deserialize(ReadOnlySpan<byte> buffer, int blockSize, int blockCount)
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
                throw new InvalidDataException($"VDE Separation block {blk} trailer verification failed.");
        }

        var block0 = buffer.Slice(0, blockSize);
        var trailer = UniversalBlockTrailer.Read(block0, blockSize);

        int assignmentCount = BinaryPrimitives.ReadInt32LittleEndian(block0);

        var manager = new VdeSeparationManager
        {
            Generation = trailer.GenerationNumber
        };

        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);
        int offset = HeaderFieldsSize;
        int currentBlock = 0;
        int entriesRead = 0;

        while (entriesRead < assignmentCount)
        {
            var block = buffer.Slice(currentBlock * blockSize, blockSize);
            int blockPayloadEnd = payloadSize;

            while (entriesRead < assignmentCount && offset + VdeRoleAssignment.SerializedSize <= blockPayloadEnd)
            {
                var assignment = VdeRoleAssignment.ReadFrom(block, offset);
                manager.AssignRole(assignment);
                offset += VdeRoleAssignment.SerializedSize;
                entriesRead++;
            }

            currentBlock++;
            offset = 0;
        }

        return manager;
    }
}
