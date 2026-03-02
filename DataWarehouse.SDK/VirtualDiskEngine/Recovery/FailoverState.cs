using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Recovery;

/// <summary>
/// Role of a VDE node in a high-availability cluster.
/// State machine transitions:
///   Standalone -> Secondary  (join cluster)
///   Secondary  -> Promoting  (begin promotion)
///   Promoting  -> Primary    (promotion complete)
///   Primary    -> Demoting   (begin demotion)
///   Demoting   -> Secondary  (demotion complete, re-sync)
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5 VOPT-78: Failover role state machine")]
public enum FailoverRole : byte
{
    /// <summary>Standalone node — not participating in HA cluster.</summary>
    Standalone = 0x00,

    /// <summary>Secondary (replica) node in an HA cluster.</summary>
    Secondary = 0x01,

    /// <summary>Node is promoting itself to Primary (transitional state).</summary>
    Promoting = 0x02,

    /// <summary>Primary (active) node in an HA cluster.</summary>
    Primary = 0x03,

    /// <summary>Node is demoting itself back to Secondary (transitional state).</summary>
    Demoting = 0x04,
}

/// <summary>
/// Control flags for the failover state.
/// </summary>
[Flags]
[SdkCompatibility("6.0.0", Notes = "Phase 91.5 VOPT-78: Failover flags")]
public enum FailoverFlags : byte
{
    /// <summary>No flags set.</summary>
    None = 0,

    /// <summary>STONITH or IO-fencing is currently active for a peer.</summary>
    FencingActive = 1 << 0,

    /// <summary>Split-brain condition has been detected. Manual intervention may be required.</summary>
    SplitBrainDetected = 1 << 1,

    /// <summary>A quorum witness is required before any promotion can proceed.</summary>
    WitnessRequired = 1 << 2,
}

/// <summary>
/// Failover State (36 bytes): stored in the Superblock at offset +0x188..+0x1AB.
/// Tracks the HA role of this VDE node, the current primary, peer count, and
/// epoch-based split-brain resolution.
///
/// Wire layout (exactly 36 bytes):
///   +0x00  byte          Role                (FailoverRole)
///   +0x01  byte          Flags               (FailoverFlags)
///   +0x02  ushort        PeerCount
///   +0x04  ulong         FailoverEpoch       monotonic; higher = more recent
///   +0x0C  Guid (16 b)   PrimaryNodeUuid
///   +0x1C  ulong         LastFailoverUtcTicks (UTC nanoseconds)
///
/// Split-brain resolution:
///   When two nodes both believe they are Primary, compare FailoverEpoch values.
///   Higher epoch wins (becomes Primary). Lower epoch should demote (enters Promoting
///   as a hold-off state before being assigned Secondary). Equal epochs require a
///   witness — both nodes set SplitBrainDetected and neither promotes unilaterally.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5 VOPT-78: 36-byte Failover State with split-brain resolution")]
public readonly struct FailoverState : IEquatable<FailoverState>
{
    // ── Layout Constants ─────────────────────────────────────────────────

    /// <summary>Total size of the serialized Failover State in bytes.</summary>
    public const int Size = 36;

    // ── Properties ───────────────────────────────────────────────────────

    /// <summary>Current HA role of this node (+0x00).</summary>
    public FailoverRole Role { get; }

    /// <summary>Control flags (+0x01).</summary>
    public FailoverFlags Flags { get; }

    /// <summary>Number of peer nodes known to this node (+0x02).</summary>
    public ushort PeerCount { get; }

    /// <summary>
    /// Monotonic failover epoch counter (+0x04). Incremented on every successful failover.
    /// Higher epoch = more recent primary. Used for split-brain resolution.
    /// </summary>
    public ulong FailoverEpoch { get; }

    /// <summary>UUID of the node currently acting as Primary (+0x0C, 16 bytes).</summary>
    public Guid PrimaryNodeUuid { get; }

    /// <summary>Timestamp of the last failover event (+0x1C, UTC nanoseconds since Unix epoch).</summary>
    public ulong LastFailoverUtcTicks { get; }

    // ── Constructor ──────────────────────────────────────────────────────

    /// <summary>Creates a FailoverState with all fields specified.</summary>
    public FailoverState(
        FailoverRole role,
        FailoverFlags flags,
        ushort peerCount,
        ulong failoverEpoch,
        Guid primaryNodeUuid,
        ulong lastFailoverUtcTicks)
    {
        Role = role;
        Flags = flags;
        PeerCount = peerCount;
        FailoverEpoch = failoverEpoch;
        PrimaryNodeUuid = primaryNodeUuid;
        LastFailoverUtcTicks = lastFailoverUtcTicks;
    }

    // ── State Machine ────────────────────────────────────────────────────

    /// <summary>
    /// Returns <see langword="true"/> when a transition from <paramref name="from"/> to
    /// <paramref name="to"/> is permitted by the HA state machine.
    ///
    /// Valid transitions:
    ///   Standalone -> Secondary  (join cluster)
    ///   Secondary  -> Promoting  (begin promotion)
    ///   Promoting  -> Primary    (promotion complete)
    ///   Primary    -> Demoting   (begin demotion)
    ///   Demoting   -> Secondary  (demotion complete, re-sync)
    /// </summary>
    public static bool IsValidTransition(FailoverRole from, FailoverRole to) =>
        (from, to) switch
        {
            (FailoverRole.Standalone, FailoverRole.Secondary)  => true,
            (FailoverRole.Secondary,  FailoverRole.Promoting)  => true,
            (FailoverRole.Promoting,  FailoverRole.Primary)    => true,
            (FailoverRole.Primary,    FailoverRole.Demoting)   => true,
            (FailoverRole.Demoting,   FailoverRole.Secondary)  => true,
            _                                                  => false,
        };

    // ── Split-Brain Resolution ───────────────────────────────────────────

    /// <summary>
    /// Resolves a split-brain condition where two nodes both claim the Primary role.
    ///
    /// Resolution rules:
    ///   - Higher epoch wins: returns <see cref="FailoverRole.Primary"/> (the winning role).
    ///   - Lower epoch loses: returns <see cref="FailoverRole.Promoting"/> (hold-off — the
    ///     losing node should stop I/O, fence itself, then transition to Secondary).
    ///   - Equal epochs: both nodes must set <see cref="FailoverFlags.SplitBrainDetected"/>
    ///     and wait for a quorum witness. Returns <see cref="FailoverRole.Promoting"/> as a
    ///     safe hold-off state for the local node.
    ///
    /// IMPORTANT: When this method returns <see cref="FailoverRole.Promoting"/> for an equal-epoch
    /// case, the caller MUST also set <see cref="FailoverFlags.SplitBrainDetected"/> and
    /// <see cref="FailoverFlags.WitnessRequired"/> in the persisted FailoverState.
    /// </summary>
    /// <param name="localEpoch">The FailoverEpoch on this local node.</param>
    /// <param name="remoteEpoch">The FailoverEpoch reported by the remote node.</param>
    /// <returns>The role this local node should adopt.</returns>
    public static FailoverRole ResolveSplitBrain(ulong localEpoch, ulong remoteEpoch)
    {
        if (localEpoch > remoteEpoch)
        {
            // Local node has higher epoch — it is the authoritative Primary
            return FailoverRole.Primary;
        }

        if (localEpoch < remoteEpoch)
        {
            // Remote node has higher epoch — local node must step down
            // Return Promoting as a hold-off state; caller transitions Promoting -> Secondary
            return FailoverRole.Promoting;
        }

        // Equal epochs: split-brain is unresolvable without a witness.
        // Both nodes must set SplitBrainDetected + WitnessRequired.
        // Return Promoting as a safe hold-off state.
        return FailoverRole.Promoting;
    }

    // ── Serialization ────────────────────────────────────────────────────

    /// <summary>
    /// Writes this state as exactly 36 bytes into <paramref name="buffer"/> starting at index 0.
    /// </summary>
    public void WriteTo(Span<byte> buffer)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(buffer.Length, Size);

        int offset = 0;

        // +0x00: Role (1 byte)
        buffer[offset++] = (byte)Role;

        // +0x01: Flags (1 byte)
        buffer[offset++] = (byte)Flags;

        // +0x02: PeerCount (2 bytes)
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(offset), PeerCount);
        offset += 2;

        // +0x04: FailoverEpoch (8 bytes)
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(offset), FailoverEpoch);
        offset += 8;

        // +0x0C: PrimaryNodeUuid (16 bytes)
        PrimaryNodeUuid.TryWriteBytes(buffer.Slice(offset));
        offset += 16;

        // +0x1C: LastFailoverUtcTicks (8 bytes)
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(offset), LastFailoverUtcTicks);
    }

    /// <summary>
    /// Reads a FailoverState from exactly 36 bytes starting at the beginning of
    /// <paramref name="buffer"/>.
    /// </summary>
    public static FailoverState ReadFrom(ReadOnlySpan<byte> buffer)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(buffer.Length, Size);

        int offset = 0;

        var role = (FailoverRole)buffer[offset++];
        var flags = (FailoverFlags)buffer[offset++];

        var peerCount = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset));
        offset += 2;

        var epoch = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        var primaryNodeUuid = new Guid(buffer.Slice(offset, 16));
        offset += 16;

        var lastFailoverUtcTicks = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(offset));

        return new FailoverState(role, flags, peerCount, epoch, primaryNodeUuid, lastFailoverUtcTicks);
    }

    // ── Equality ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public bool Equals(FailoverState other) =>
        Role == other.Role
        && Flags == other.Flags
        && PeerCount == other.PeerCount
        && FailoverEpoch == other.FailoverEpoch
        && PrimaryNodeUuid == other.PrimaryNodeUuid
        && LastFailoverUtcTicks == other.LastFailoverUtcTicks;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is FailoverState other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Role, FailoverEpoch, PrimaryNodeUuid);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(FailoverState left, FailoverState right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(FailoverState left, FailoverState right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() =>
        $"FailoverState(Role={Role}, Epoch={FailoverEpoch}, Primary={PrimaryNodeUuid}, Peers={PeerCount}, Flags={Flags})";
}
