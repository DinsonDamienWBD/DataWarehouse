using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Identity;

/// <summary>
/// Records the identity of the last writer to a VDE volume: session ID, timestamp, and node ID.
/// Provides forensic traceability in multi-node environments by tracking which node and session
/// last modified the superblock.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 74: VDE Identity (VTMP-04, VTMP-06)")]
public readonly struct LastWriterIdentity : IEquatable<LastWriterIdentity>
{
    /// <summary>Session ID of the last writer (unique per open session).</summary>
    public Guid SessionId { get; }

    /// <summary>Timestamp of the last write operation (UTC ticks).</summary>
    public long TimestampUtcTicks { get; }

    /// <summary>Node ID of the last writer (identifies the cluster node).</summary>
    public Guid NodeId { get; }

    /// <summary>Creates a new LastWriterIdentity with all fields specified.</summary>
    /// <param name="sessionId">Unique session identifier.</param>
    /// <param name="timestampUtcTicks">UTC ticks of the write operation.</param>
    /// <param name="nodeId">Cluster node identifier.</param>
    public LastWriterIdentity(Guid sessionId, long timestampUtcTicks, Guid nodeId)
    {
        SessionId = sessionId;
        TimestampUtcTicks = timestampUtcTicks;
        NodeId = nodeId;
    }

    /// <summary>
    /// Creates a new LastWriterIdentity for the current moment with a fresh session ID.
    /// </summary>
    /// <param name="nodeId">The cluster node identifier for this writer.</param>
    /// <returns>A new LastWriterIdentity with a generated session GUID and current UTC timestamp.</returns>
    public static LastWriterIdentity CreateCurrent(Guid nodeId)
    {
        return new LastWriterIdentity(
            sessionId: Guid.NewGuid(),
            timestampUtcTicks: DateTimeOffset.UtcNow.Ticks,
            nodeId: nodeId);
    }

    /// <summary>
    /// Reads the last writer identity from a superblock's last-writer fields.
    /// </summary>
    /// <param name="sb">The superblock to read from.</param>
    /// <returns>A LastWriterIdentity populated from the superblock's fields.</returns>
    public static LastWriterIdentity FromSuperblock(in SuperblockV2 sb)
    {
        return new LastWriterIdentity(
            sessionId: sb.LastWriterSessionId,
            timestampUtcTicks: sb.LastWriterTimestamp,
            nodeId: sb.LastWriterNodeId);
    }

    /// <summary>
    /// Creates a new <see cref="SuperblockV2"/> with the last-writer fields replaced by
    /// this identity's values. All other fields are preserved from the original superblock.
    /// </summary>
    /// <param name="sb">The original superblock whose non-writer fields are preserved.</param>
    /// <param name="writer">The writer identity to apply.</param>
    /// <returns>A new SuperblockV2 with updated last-writer fields.</returns>
    public static SuperblockV2 UpdateSuperblockLastWriter(in SuperblockV2 sb, LastWriterIdentity writer)
    {
        return new SuperblockV2(
            magic: sb.Magic,
            versionInfo: sb.VersionInfo,
            moduleManifest: sb.ModuleManifest,
            moduleConfig: sb.ModuleConfig,
            moduleConfigExt: sb.ModuleConfigExt,
            blockSize: sb.BlockSize,
            totalBlocks: sb.TotalBlocks,
            freeBlocks: sb.FreeBlocks,
            expectedFileSize: sb.ExpectedFileSize,
            totalAllocatedBlocks: sb.TotalAllocatedBlocks,
            volumeUuid: sb.VolumeUuid,
            clusterNodeId: sb.ClusterNodeId,
            defaultCompressionAlgo: sb.DefaultCompressionAlgo,
            defaultEncryptionAlgo: sb.DefaultEncryptionAlgo,
            defaultChecksumAlgo: sb.DefaultChecksumAlgo,
            inodeSize: sb.InodeSize,
            policyVersion: sb.PolicyVersion,
            replicationEpoch: sb.ReplicationEpoch,
            wormHighWaterMark: sb.WormHighWaterMark,
            encryptionKeyFingerprint: sb.EncryptionKeyFingerprint,
            sovereigntyZoneId: sb.SovereigntyZoneId,
            volumeLabel: sb.VolumeLabel,
            createdTimestampUtc: sb.CreatedTimestampUtc,
            modifiedTimestampUtc: sb.ModifiedTimestampUtc,
            lastScrubTimestamp: sb.LastScrubTimestamp,
            checkpointSequence: sb.CheckpointSequence,
            errorMapBlockCount: sb.ErrorMapBlockCount,
            lastWriterSessionId: writer.SessionId,
            lastWriterTimestamp: writer.TimestampUtcTicks,
            lastWriterNodeId: writer.NodeId,
            physicalAllocatedBlocks: sb.PhysicalAllocatedBlocks,
            headerIntegritySeal: sb.HeaderIntegritySeal);
    }

    /// <inheritdoc />
    public bool Equals(LastWriterIdentity other) =>
        SessionId == other.SessionId
        && TimestampUtcTicks == other.TimestampUtcTicks
        && NodeId == other.NodeId;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is LastWriterIdentity other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(SessionId, TimestampUtcTicks, NodeId);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(LastWriterIdentity left, LastWriterIdentity right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(LastWriterIdentity left, LastWriterIdentity right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() =>
        $"LastWriter(Session={SessionId}, Node={NodeId}, Time={new DateTimeOffset(TimestampUtcTicks, TimeSpan.Zero):O})";
}
