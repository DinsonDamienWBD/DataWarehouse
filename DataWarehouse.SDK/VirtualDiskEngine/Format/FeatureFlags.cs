using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Format;

/// <summary>
/// Features that PREVENT older readers from opening the VDE file.
/// If a reader does not understand a set bit, it must refuse to open the file.
/// </summary>
[Flags]
[SdkCompatibility("6.0.0", Notes = "Phase 71: VDE v2.0 incompatible feature flags (VDE2-01)")]
public enum IncompatibleFeatureFlags : uint
{
    /// <summary>No incompatible features enabled.</summary>
    None = 0,

    /// <summary>Encryption is enabled; data blocks are encrypted.</summary>
    EncryptionEnabled = 1 << 0,

    /// <summary>WORM (Write-Once-Read-Many) region is active.</summary>
    WormRegionActive = 1 << 1,

    /// <summary>RAID striping/mirroring is active across devices.</summary>
    RaidEnabled = 1 << 2,

    /// <summary>Compute cache region is active for near-data processing.</summary>
    ComputeCacheActive = 1 << 3,

    /// <summary>Fabric links for multi-VDE federation are active.</summary>
    FabricLinksActive = 1 << 4,

    /// <summary>Consensus log (Raft/Paxos) is active for distributed coordination.</summary>
    ConsensusLogActive = 1 << 5,
}

/// <summary>
/// Features that allow read-only access by older readers that do not
/// understand the feature. Writing requires a reader that understands all set bits.
/// </summary>
[Flags]
[SdkCompatibility("6.0.0", Notes = "Phase 71: VDE v2.0 read-only compatible feature flags (VDE2-01)")]
public enum ReadOnlyCompatibleFeatureFlags : uint
{
    /// <summary>No read-only compatible features enabled.</summary>
    None = 0,

    /// <summary>Transparent compression is enabled on data blocks.</summary>
    CompressionEnabled = 1 << 0,

    /// <summary>Replication metadata is present for multi-site sync.</summary>
    ReplicationEnabled = 1 << 1,

    /// <summary>Streaming region is active for append-optimized data.</summary>
    StreamingRegionActive = 1 << 2,

    /// <summary>Snapshot metadata is present; point-in-time views exist.</summary>
    SnapshotActive = 1 << 3,

    /// <summary>Intelligence cache region is active for ML/inference data.</summary>
    IntelligenceCacheActive = 1 << 4,
}

/// <summary>
/// Features that are fully compatible with older readers.
/// Older readers can safely read AND write the file, ignoring these features.
/// </summary>
[Flags]
[SdkCompatibility("6.0.0", Notes = "Phase 71: VDE v2.0 compatible feature flags (VDE2-01)")]
public enum CompatibleFeatureFlags : uint
{
    /// <summary>No compatible features enabled.</summary>
    None = 0,

    /// <summary>Tag index B-tree region is active.</summary>
    TagIndexActive = 1 << 0,

    /// <summary>Policy engine evaluation metadata is present.</summary>
    PolicyEngineActive = 1 << 1,

    /// <summary>Tamper-proof hash chain is active for audit trail.</summary>
    TamperProofChainActive = 1 << 2,

    /// <summary>Compliance vault region is active.</summary>
    ComplianceVaultActive = 1 << 3,

    /// <summary>Air-gapped operation mode is active.</summary>
    AirgapMode = 1 << 4,

    /// <summary>FUSE compatibility mode is active.</summary>
    FuseCompatMode = 1 << 5,

    /// <summary>Dirty flag indicating unclean shutdown.</summary>
    DirtyFlag = 1 << 6,
}

/// <summary>
/// Combines version gating fields with feature flags to determine whether
/// a given reader/writer implementation can open a VDE file.
/// Serialized size: 16 bytes (2+2+4+4+4).
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 71: VDE v2.0 format version info (VDE2-01)")]
public readonly struct FormatVersionInfo : IEquatable<FormatVersionInfo>
{
    /// <summary>Serialized size of FormatVersionInfo in bytes.</summary>
    public const int SerializedSize = 16;

    /// <summary>Minimum reader version that can open this VDE file.</summary>
    public ushort MinReaderVersion { get; }

    /// <summary>Minimum writer version that can modify this VDE file.</summary>
    public ushort MinWriterVersion { get; }

    /// <summary>Incompatible feature flags set on this VDE file.</summary>
    public IncompatibleFeatureFlags IncompatibleFeatures { get; }

    /// <summary>Read-only compatible feature flags set on this VDE file.</summary>
    public ReadOnlyCompatibleFeatureFlags ReadOnlyCompatibleFeatures { get; }

    /// <summary>Fully compatible feature flags set on this VDE file.</summary>
    public CompatibleFeatureFlags CompatibleFeatures { get; }

    /// <summary>
    /// Creates a new FormatVersionInfo with the specified parameters.
    /// </summary>
    public FormatVersionInfo(
        ushort minReaderVersion,
        ushort minWriterVersion,
        IncompatibleFeatureFlags incompatibleFeatures,
        ReadOnlyCompatibleFeatureFlags readOnlyCompatibleFeatures,
        CompatibleFeatureFlags compatibleFeatures)
    {
        MinReaderVersion = minReaderVersion;
        MinWriterVersion = minWriterVersion;
        IncompatibleFeatures = incompatibleFeatures;
        ReadOnlyCompatibleFeatures = readOnlyCompatibleFeatures;
        CompatibleFeatures = compatibleFeatures;
    }

    /// <summary>
    /// Returns true if the given reader version can open this VDE file.
    /// </summary>
    public bool CanRead(ushort readerVersion) => readerVersion >= MinReaderVersion;

    /// <summary>
    /// Returns true if the given writer version can modify this VDE file.
    /// </summary>
    public bool CanWrite(ushort writerVersion) => writerVersion >= MinWriterVersion;

    /// <summary>
    /// Returns <c>true</c> if <em>any</em> of the specified incompatible feature flags are set
    /// in the container (OR semantics, not AND).
    /// </summary>
    /// <remarks>
    /// Cat 15 (finding 813): the method name might suggest AND semantics ("has ALL of these
    /// incompatible features"), but the implementation uses bitwise OR — it returns true if
    /// at least one flag in <paramref name="check"/> is set. This is the correct behavior
    /// for compatibility gating: a single unknown feature is sufficient to reject the container.
    /// </remarks>
    public bool HasIncompatibleFeatures(IncompatibleFeatureFlags check) =>
        (IncompatibleFeatures & check) != 0;

    /// <summary>
    /// Serializes this FormatVersionInfo into exactly 16 bytes.
    /// </summary>
    public static void Serialize(in FormatVersionInfo info, Span<byte> buffer)
    {
        if (buffer.Length < SerializedSize)
            throw new ArgumentException(
                $"Buffer must be at least {SerializedSize} bytes.", nameof(buffer));

        BinaryPrimitives.WriteUInt16LittleEndian(buffer, info.MinReaderVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(2), info.MinWriterVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(4), (uint)info.IncompatibleFeatures);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(8), (uint)info.ReadOnlyCompatibleFeatures);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(12), (uint)info.CompatibleFeatures);
    }

    /// <summary>
    /// Deserializes a FormatVersionInfo from a 16-byte buffer.
    /// </summary>
    public static FormatVersionInfo Deserialize(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < SerializedSize)
            throw new ArgumentException(
                $"Buffer must be at least {SerializedSize} bytes.", nameof(buffer));

        var minReader = BinaryPrimitives.ReadUInt16LittleEndian(buffer);
        var minWriter = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(2));
        var incompat = (IncompatibleFeatureFlags)BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(4));
        var roCompat = (ReadOnlyCompatibleFeatureFlags)BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(8));
        var compat = (CompatibleFeatureFlags)BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(12));

        return new FormatVersionInfo(minReader, minWriter, incompat, roCompat, compat);
    }

    /// <inheritdoc />
    public bool Equals(FormatVersionInfo other) =>
        MinReaderVersion == other.MinReaderVersion
        && MinWriterVersion == other.MinWriterVersion
        && IncompatibleFeatures == other.IncompatibleFeatures
        && ReadOnlyCompatibleFeatures == other.ReadOnlyCompatibleFeatures
        && CompatibleFeatures == other.CompatibleFeatures;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is FormatVersionInfo other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(MinReaderVersion, MinWriterVersion, IncompatibleFeatures,
            ReadOnlyCompatibleFeatures, CompatibleFeatures);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(FormatVersionInfo left, FormatVersionInfo right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(FormatVersionInfo left, FormatVersionInfo right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() =>
        $"FormatVersionInfo(Reader>={MinReaderVersion}, Writer>={MinWriterVersion}, " +
        $"Incompat={IncompatibleFeatures}, ROCompat={ReadOnlyCompatibleFeatures}, Compat={CompatibleFeatures})";
}
