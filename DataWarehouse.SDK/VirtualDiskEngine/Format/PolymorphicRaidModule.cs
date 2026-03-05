using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Format;

/// <summary>
/// Erasure coding scheme for a per-inode polymorphic RAID descriptor.
/// Values 0-4 correspond to extent flag bits 9-11 (RAID_Topology) encoding;
/// values 5-7 are reserved.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87-25: Polymorphic RAID topology scheme (VOPT-38/VOPT-60)")]
public enum RaidTopologyScheme : byte
{
    /// <summary>No redundancy. Raw data only. Extent flag encoding: 000.</summary>
    Standard = 0,

    /// <summary>Mirror: 1:1 full copy on a second device. Extent flag encoding: 001.</summary>
    Mirror = 1,

    /// <summary>Reed-Solomon erasure coding: 2 data shards + 1 parity shard. Extent flag encoding: 010.</summary>
    Ec21 = 2,

    /// <summary>Reed-Solomon erasure coding: 4 data shards + 2 parity shards. Extent flag encoding: 011.</summary>
    Ec42 = 3,

    /// <summary>Reed-Solomon erasure coding: 8 data shards + 3 parity shards. Extent flag encoding: 100.</summary>
    Ec83 = 4,
}

/// <summary>
/// Behavior flags for a per-inode polymorphic RAID descriptor.
/// </summary>
[Flags]
[SdkCompatibility("6.0.0", Notes = "Phase 87-25: Polymorphic RAID descriptor flags (VOPT-38/VOPT-60)")]
public enum PolymorphicRaidFlags : byte
{
    /// <summary>No special flags.</summary>
    None = 0,

    /// <summary>
    /// This inode inherits its RAID parameters from the VDE-level defaults stored in
    /// <see cref="DataWarehouse.SDK.VirtualDiskEngine.Regions.RaidMetadataRegion"/>.
    /// When set, <see cref="PolymorphicRaidModule.DataShards"/>,
    /// <see cref="PolymorphicRaidModule.ParityShards"/>, and
    /// <see cref="PolymorphicRaidModule.DeviceMap"/> are ignored at read time.
    /// </summary>
    InheritFromVolume = 1,

    /// <summary>
    /// Allow degraded-mode reads when one or more shards are offline.
    /// If clear the VDE surfaces an I/O error rather than serving degraded data.
    /// </summary>
    AllowDegradation = 2,

    /// <summary>
    /// This inode is eligible for parity-decay: parity shards may be asynchronously
    /// demoted to warm-tier storage during low-activity windows to reduce IOPS cost.
    /// </summary>
    ParityDecayEligible = 4,
}

/// <summary>
/// A 32-byte per-inode erasure coding descriptor stored in the VDE inode overflow area
/// (RAID module bit 5). Enables different files on the same physical NVMe to use different
/// RAID levels — e.g., a large ML dataset uses <see cref="RaidTopologyScheme.Standard"/>
/// (zero overhead) while an adjacent financial ledger uses <see cref="RaidTopologyScheme.Ec42"/>
/// — eliminating the traditional whole-disk RAID tax.
/// </summary>
/// <remarks>
/// <para>
/// <strong>On-disk layout (32 bytes, little-endian):</strong>
/// <code>
///   [0]      Scheme      — byte: RaidTopologyScheme
///   [1]      DataShards  — byte: number of data shards
///   [2]      ParityShards— byte: number of parity shards
///   [3]      Flags       — byte: PolymorphicRaidFlags
///   [4..32)  DeviceMap   — 28 bytes: physical device index per shard slot (0xFF = unassigned)
/// </code>
/// </para>
/// <para>
/// <strong>Integration:</strong> extends the existing RAID module
/// (<c>ModuleId.Raid</c>, bit 5) whose standard inode field is 4 bytes. When polymorphic
/// RAID is active the VDE driver reads this 32-byte overflow descriptor from the inode
/// extension area, keyed by <see cref="ModuleBitPosition"/>.
/// </para>
/// <para>
/// <strong>DeviceMap encoding:</strong> each of the 28 bytes holds a device index (0-254)
/// that references an entry in the volume-level
/// <see cref="DataWarehouse.SDK.VirtualDiskEngine.Regions.RaidMetadataRegion"/>.
/// The sentinel value <c>0xFF</c> means the slot is unassigned. Index 0 is the first data
/// shard, indices [DataShards..DataShards+ParityShards) are parity shards; unused slots
/// carry <c>0xFF</c>.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87-25: Per-inode polymorphic RAID descriptor (VOPT-38/VOPT-60, Module bit 5 overflow)")]
public readonly struct PolymorphicRaidModule : IEquatable<PolymorphicRaidModule>
{
    // ── Constants ─────────────────────────────────────────────────────────────

    /// <summary>Serialized size of this descriptor in bytes.</summary>
    public const int SerializedSize = 32;

    /// <summary>
    /// Bit position in the ModuleManifest for the existing RAID module that this
    /// descriptor extends. Equal to <see cref="ModuleId.Raid"/> = 5.
    /// </summary>
    public const byte ModuleBitPosition = 5;

    /// <summary>
    /// Number of bytes reserved for the device map. Supports up to 28 independent
    /// shard device assignments per inode.
    /// </summary>
    public const int DeviceMapSize = 28;

    /// <summary>Sentinel byte value indicating an unassigned device map slot.</summary>
    public const byte UnassignedDevice = 0xFF;

    // ── Fields ────────────────────────────────────────────────────────────────

    /// <summary>Erasure coding scheme applied to this inode.</summary>
    public RaidTopologyScheme Scheme { get; init; }

    /// <summary>
    /// Number of data shards. For <see cref="RaidTopologyScheme.Ec42"/> this is 4;
    /// for <see cref="RaidTopologyScheme.Mirror"/> this is 1 (one original copy).
    /// Ignored when <see cref="PolymorphicRaidFlags.InheritFromVolume"/> is set.
    /// </summary>
    public byte DataShards { get; init; }

    /// <summary>
    /// Number of parity / redundancy shards. For <see cref="RaidTopologyScheme.Ec42"/>
    /// this is 2. For <see cref="RaidTopologyScheme.Standard"/> this is 0.
    /// Ignored when <see cref="PolymorphicRaidFlags.InheritFromVolume"/> is set.
    /// </summary>
    public byte ParityShards { get; init; }

    /// <summary>Behavioral flags for this inode's RAID descriptor.</summary>
    public PolymorphicRaidFlags Flags { get; init; }

    /// <summary>
    /// 28-byte device map. Each byte is a device index (0-254) into the volume-level
    /// <see cref="DataWarehouse.SDK.VirtualDiskEngine.Regions.RaidMetadataRegion"/>,
    /// or <see cref="UnassignedDevice"/> (0xFF) for unused slots.
    /// Index 0 is the first data shard; indices [DataShards, DataShards+ParityShards)
    /// are parity shards.
    /// </summary>
    public byte[] DeviceMap { get; init; }

    // ── Computed properties ────────────────────────────────────────────────────

    /// <summary>
    /// Returns <see langword="true"/> when this inode uses erasure coding
    /// (<see cref="Scheme"/> >= <see cref="RaidTopologyScheme.Ec21"/>).
    /// </summary>
    public bool IsErasureCoded => Scheme >= RaidTopologyScheme.Ec21;

    /// <summary>Total number of physical shards (data + parity).</summary>
    public int TotalShards => DataShards + ParityShards;

    /// <summary>
    /// Storage overhead ratio: <c>ParityShards / (double)DataShards</c>.
    /// Returns 1.0 for <see cref="RaidTopologyScheme.Mirror"/> (100% overhead),
    /// 0.5 for Ec42 (50% overhead), and 0.0 for <see cref="RaidTopologyScheme.Standard"/>.
    /// </summary>
    public double StorageOverhead =>
        DataShards == 0 ? 0.0 : ParityShards / (double)DataShards;

    // ── Factory methods ────────────────────────────────────────────────────────

    /// <summary>
    /// Default descriptor: no redundancy, <see cref="RaidTopologyScheme.Standard"/>.
    /// All device map slots are unassigned.
    /// </summary>
    public static PolymorphicRaidModule Standard { get; } = new PolymorphicRaidModule
    {
        Scheme = RaidTopologyScheme.Standard,
        DataShards = 1,
        ParityShards = 0,
        Flags = PolymorphicRaidFlags.None,
        DeviceMap = CreateEmptyDeviceMap(),
    };

    /// <summary>
    /// Creates a mirrored RAID descriptor (1:1 copy on a second device).
    /// </summary>
    /// <returns>A <see cref="PolymorphicRaidModule"/> with <see cref="RaidTopologyScheme.Mirror"/>.</returns>
    public static PolymorphicRaidModule CreateMirror() => new PolymorphicRaidModule
    {
        Scheme = RaidTopologyScheme.Mirror,
        DataShards = 1,
        ParityShards = 1,
        Flags = PolymorphicRaidFlags.None,
        DeviceMap = CreateEmptyDeviceMap(),
    };

    /// <summary>
    /// Creates a Reed-Solomon erasure-coded RAID descriptor.
    /// </summary>
    /// <param name="dataShards">Number of data shards (1-28).</param>
    /// <param name="parityShards">Number of parity shards (1-27; dataShards + parityShards &lt;= 28).</param>
    /// <returns>
    /// A <see cref="PolymorphicRaidModule"/> with an appropriate <see cref="RaidTopologyScheme"/>
    /// if the combination maps to a well-known scheme, or a custom EC descriptor otherwise.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when shard counts are zero, negative, or exceed the device map capacity.
    /// </exception>
    public static PolymorphicRaidModule CreateErasureCoded(byte dataShards, byte parityShards)
    {
        ArgumentOutOfRangeException.ThrowIfZero(dataShards, nameof(dataShards));
        ArgumentOutOfRangeException.ThrowIfZero(parityShards, nameof(parityShards));
        if (dataShards + parityShards > DeviceMapSize)
            throw new ArgumentOutOfRangeException(nameof(dataShards),
                $"dataShards ({dataShards}) + parityShards ({parityShards}) must not exceed {DeviceMapSize}.");

        // Map well-known (data, parity) pairs to named schemes.
        var scheme = (dataShards, parityShards) switch
        {
            (2, 1) => RaidTopologyScheme.Ec21,
            (4, 2) => RaidTopologyScheme.Ec42,
            (8, 3) => RaidTopologyScheme.Ec83,
            _      => RaidTopologyScheme.Ec21, // closest defined; caller controls DataShards/ParityShards
        };

        return new PolymorphicRaidModule
        {
            Scheme = scheme,
            DataShards = dataShards,
            ParityShards = parityShards,
            Flags = PolymorphicRaidFlags.None,
            DeviceMap = CreateEmptyDeviceMap(),
        };
    }

    // ── Serialization ─────────────────────────────────────────────────────────

    /// <summary>
    /// Serializes <paramref name="module"/> into <paramref name="buffer"/>.
    /// </summary>
    /// <param name="module">The module state to serialize.</param>
    /// <param name="buffer">
    /// Output span of at least <see cref="SerializedSize"/> (32) bytes.
    /// Layout: [Scheme:1][DataShards:1][ParityShards:1][Flags:1][DeviceMap:28].
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="buffer"/> is shorter than <see cref="SerializedSize"/> bytes.
    /// </exception>
    public static void Serialize(in PolymorphicRaidModule module, Span<byte> buffer)
    {
        if (buffer.Length < SerializedSize)
            throw new ArgumentException(
                $"Buffer must be at least {SerializedSize} bytes.", nameof(buffer));

        buffer[0] = (byte)module.Scheme;
        buffer[1] = module.DataShards;
        buffer[2] = module.ParityShards;
        buffer[3] = (byte)module.Flags;

        var deviceMap = module.DeviceMap;
        var mapSlice = buffer.Slice(4, DeviceMapSize);
        mapSlice.Fill(UnassignedDevice);
        if (deviceMap is { Length: > 0 })
        {
            int copyLen = Math.Min(deviceMap.Length, DeviceMapSize);
            deviceMap.AsSpan(0, copyLen).CopyTo(mapSlice);
        }
    }

    /// <summary>
    /// Deserializes a <see cref="PolymorphicRaidModule"/> from <paramref name="buffer"/>.
    /// </summary>
    /// <param name="buffer">
    /// Input span of at least <see cref="SerializedSize"/> (32) bytes.
    /// Layout: [Scheme:1][DataShards:1][ParityShards:1][Flags:1][DeviceMap:28].
    /// </param>
    /// <returns>The deserialized module state.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="buffer"/> is shorter than <see cref="SerializedSize"/> bytes.
    /// </exception>
    public static PolymorphicRaidModule Deserialize(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < SerializedSize)
            throw new ArgumentException(
                $"Buffer must be at least {SerializedSize} bytes.", nameof(buffer));

        var scheme       = (RaidTopologyScheme)buffer[0];
        byte dataShards  = buffer[1];
        byte parityShards = buffer[2];
        var flags        = (PolymorphicRaidFlags)buffer[3];

        var deviceMap = new byte[DeviceMapSize];
        buffer.Slice(4, DeviceMapSize).CopyTo(deviceMap);

        return new PolymorphicRaidModule
        {
            Scheme       = scheme,
            DataShards   = dataShards,
            ParityShards = parityShards,
            Flags        = flags,
            DeviceMap    = deviceMap,
        };
    }

    // ── IEquatable ────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public bool Equals(PolymorphicRaidModule other)
    {
        if (Scheme != other.Scheme
            || DataShards != other.DataShards
            || ParityShards != other.ParityShards
            || Flags != other.Flags)
        {
            return false;
        }

        var a = DeviceMap ?? Array.Empty<byte>();
        var b = other.DeviceMap ?? Array.Empty<byte>();
        if (a.Length != b.Length) return false;
        return a.AsSpan().SequenceEqual(b.AsSpan());
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PolymorphicRaidModule other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hc = new HashCode();
        hc.Add(Scheme);
        hc.Add(DataShards);
        hc.Add(ParityShards);
        hc.Add(Flags);
        var map = DeviceMap ?? Array.Empty<byte>();
        foreach (byte b in map) hc.Add(b);
        return hc.ToHashCode();
    }

    /// <summary>Equality operator.</summary>
    public static bool operator ==(PolymorphicRaidModule left, PolymorphicRaidModule right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(PolymorphicRaidModule left, PolymorphicRaidModule right) => !left.Equals(right);

    // ── Private helpers ───────────────────────────────────────────────────────

    private static byte[] CreateEmptyDeviceMap()
    {
        var map = new byte[DeviceMapSize];
        map.AsSpan().Fill(UnassignedDevice);
        return map;
    }
}
