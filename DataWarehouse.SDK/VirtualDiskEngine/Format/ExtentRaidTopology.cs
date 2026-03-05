using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Format;

/// <summary>
/// Static helper that reads and writes the RAID_Topology field encoded in bits 9-11 of
/// an <see cref="InodeExtent"/> <c>Flags</c> value (<see cref="ExtentFlags"/>).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Field layout inside ExtentFlags (uint32):</strong>
/// <code>
///   bit  9: RAID_Topology[0]  (LSB of 3-bit field)
///   bit 10: RAID_Topology[1]
///   bit 11: RAID_Topology[2]  (MSB of 3-bit field)
/// </code>
/// The 3-bit value directly maps to <see cref="RaidTopologyScheme"/> (0-4).
/// Values 5-7 are reserved; see <see cref="IsValidScheme"/>.
/// </para>
/// <para>
/// <strong>Purpose:</strong> enables per-extent selection of an erasure coding scheme,
/// allowing different regions within the same file — or different files on the same
/// physical NVMe — to independently opt into the RAID level that best fits their
/// redundancy/cost trade-off. The volume-level <see cref="RaidTopologyScheme"/> stored
/// in <see cref="DataWarehouse.SDK.VirtualDiskEngine.Regions.RaidMetadataRegion"/> still
/// applies to extents whose bits 9-11 read as <see cref="RaidTopologyScheme.Standard"/>
/// and whose inode carries an active <see cref="PolymorphicRaidModule"/> with
/// <see cref="PolymorphicRaidFlags.InheritFromVolume"/>.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87-25: Per-extent RAID topology via extent flags bits 9-11 (VOPT-60)")]
public static class ExtentRaidTopology
{
    // ── Constants ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Bit position of the least-significant bit of the 3-bit RAID_Topology field
    /// inside the 32-bit <see cref="ExtentFlags"/> value.
    /// </summary>
    public const int RaidTopologyBitOffset = 9;

    /// <summary>
    /// 3-bit mask at bits 9-11 that isolates the RAID_Topology field from the rest
    /// of an <see cref="ExtentFlags"/> value.
    /// </summary>
    public const uint RaidTopologyMask = 0b111u << RaidTopologyBitOffset; // 0x00000E00

    // ── Core accessors ────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the <see cref="RaidTopologyScheme"/> from bits 9-11 of
    /// <paramref name="extentFlags"/>.
    /// </summary>
    /// <param name="extentFlags">
    /// The raw <c>uint</c> representation of an <see cref="ExtentFlags"/> value.
    /// </param>
    /// <returns>
    /// The <see cref="RaidTopologyScheme"/> encoded in bits 9-11. Values 5-7 are
    /// returned as-is (callers should check <see cref="IsValidScheme"/> if needed).
    /// </returns>
    public static RaidTopologyScheme GetTopology(uint extentFlags)
        => (RaidTopologyScheme)((extentFlags & RaidTopologyMask) >> RaidTopologyBitOffset);

    /// <summary>
    /// Returns a copy of <paramref name="extentFlags"/> with bits 9-11 set to
    /// the encoded value of <paramref name="scheme"/>, preserving all other flag bits.
    /// </summary>
    /// <param name="extentFlags">
    /// The raw <c>uint</c> representation of an <see cref="ExtentFlags"/> value.
    /// </param>
    /// <param name="scheme">The RAID topology scheme to encode.</param>
    /// <returns>Updated flags with the RAID_Topology field set.</returns>
    public static uint SetTopology(uint extentFlags, RaidTopologyScheme scheme)
        => (extentFlags & ~RaidTopologyMask) | (((uint)scheme << RaidTopologyBitOffset) & RaidTopologyMask);

    /// <summary>
    /// Returns <see langword="true"/> when the extent has any redundancy scheme
    /// (i.e., <see cref="RaidTopologyScheme"/> != <see cref="RaidTopologyScheme.Standard"/>).
    /// </summary>
    /// <param name="extentFlags">
    /// The raw <c>uint</c> representation of an <see cref="ExtentFlags"/> value.
    /// </param>
    public static bool HasRedundancy(uint extentFlags)
        => GetTopology(extentFlags) != RaidTopologyScheme.Standard;

    // ── Validation ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="value"/> maps to a defined
    /// <see cref="RaidTopologyScheme"/> (0-4). Values 5-7 are reserved and return
    /// <see langword="false"/>.
    /// </summary>
    /// <param name="value">Raw 3-bit field value extracted from extent flags.</param>
    public static bool IsValidScheme(byte value) => value <= 4;

    // ── Human-readable description ────────────────────────────────────────────

    /// <summary>
    /// Returns a human-readable description of the given <see cref="RaidTopologyScheme"/>.
    /// </summary>
    /// <param name="scheme">The RAID topology scheme.</param>
    /// <returns>
    /// A short descriptive string, e.g. <c>"Standard (no redundancy)"</c> or
    /// <c>"EC 4+2 (Reed-Solomon)"</c>.
    /// </returns>
    public static string DescribeTopology(RaidTopologyScheme scheme) => scheme switch
    {
        RaidTopologyScheme.Standard => "Standard (no redundancy)",
        RaidTopologyScheme.Mirror   => "Mirror (1:1 copy)",
        RaidTopologyScheme.Ec21   => "EC 2+1 (Reed-Solomon)",
        RaidTopologyScheme.Ec42   => "EC 4+2 (Reed-Solomon)",
        RaidTopologyScheme.Ec83   => "EC 8+3 (Reed-Solomon)",
        _                           => $"Reserved ({(byte)scheme})",
    };

    // ── ExtentFlags convenience overloads ─────────────────────────────────────

    /// <summary>
    /// Extracts the <see cref="RaidTopologyScheme"/> from an <see cref="ExtentFlags"/> value.
    /// </summary>
    /// <param name="flags">The <see cref="ExtentFlags"/> value.</param>
    /// <returns>The encoded <see cref="RaidTopologyScheme"/>.</returns>
    public static RaidTopologyScheme GetTopology(ExtentFlags flags)
        => GetTopology((uint)flags);

    /// <summary>
    /// Returns a copy of <paramref name="flags"/> with the RAID_Topology field set to
    /// <paramref name="scheme"/>, preserving all other flag bits.
    /// </summary>
    /// <param name="flags">The source <see cref="ExtentFlags"/> value.</param>
    /// <param name="scheme">The RAID topology scheme to encode.</param>
    /// <returns>Updated <see cref="ExtentFlags"/>.</returns>
    public static ExtentFlags SetTopology(ExtentFlags flags, RaidTopologyScheme scheme)
        => (ExtentFlags)SetTopology((uint)flags, scheme);

    /// <summary>
    /// Returns <see langword="true"/> when the extent has any redundancy scheme.
    /// </summary>
    /// <param name="flags">The <see cref="ExtentFlags"/> value.</param>
    public static bool HasRedundancy(ExtentFlags flags)
        => HasRedundancy((uint)flags);
}
