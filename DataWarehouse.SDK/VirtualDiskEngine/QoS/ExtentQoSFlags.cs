using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.QoS;

/// <summary>
/// Helpers for reading and writing QoS-related bits from the 32-bit <c>Flags</c> field
/// of an extent descriptor (<c>InodeExtent.Flags</c>).
/// </summary>
/// <remarks>
/// Two independent QoS fields are packed into the 32-bit extent <c>Flags</c> uint:
/// <list type="bullet">
///   <item>
///     <description>
///       Bits 6-7 (<see cref="QoSClassMask"/> at shift <see cref="QoSClassShift"/>):
///       Two-bit <see cref="NvmeQoSPriority"/> coarse hardware priority class.
///       Maps to NVMe SQE ioprio submission queue priority.
///       Independent of the per-inode <see cref="QoSClass"/> (3-bit, 8 levels).
///     </description>
///   </item>
///   <item>
///     <description>
///       Bits 19-22 (<see cref="DeadlineTierMask"/> at shift <see cref="DeadlineTierShift"/>):
///       Four-bit <c>DeadlineTier</c> (0-15) per-extent I/O deadline hint.
///       Zero means "inherited from inode QoS class"; non-zero overrides with a
///       specific latency deadline (see <see cref="GetDeadlineFromTier"/>).
///       Gated by QOS module (bit 34 of Module Registry).
///     </description>
///   </item>
/// </list>
/// All methods are pure functions; no allocation occurs.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: extent QoS flag helpers for bits 6-7 and 19-22 (VOPT-72, VOPT-73)")]
public static class ExtentQoSFlags
{
    // -----------------------------------------------------------------------
    // Bit-field constants
    // -----------------------------------------------------------------------

    private const int    QoSClassShift    = 6;
    private const uint   QoSClassMask     = 0x03u;   // 2 bits

    private const int    DeadlineTierShift = 19;
    private const uint   DeadlineTierMask  = 0x0Fu;  // 4 bits

    // -----------------------------------------------------------------------
    // NVMe hardware priority (bits 6-7)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Extracts the two-bit NVMe hardware priority class from extent Flags bits 6-7.
    /// </summary>
    /// <param name="flags">The 32-bit extent Flags value.</param>
    /// <returns>The <see cref="NvmeQoSPriority"/> encoded in bits 6-7.</returns>
    public static NvmeQoSPriority GetQoSClass(uint flags)
        => (NvmeQoSPriority)((flags >> QoSClassShift) & QoSClassMask);

    /// <summary>
    /// Returns a new Flags value with bits 6-7 set to the specified NVMe hardware priority class.
    /// All other bits are preserved.
    /// </summary>
    /// <param name="flags">The original 32-bit extent Flags value.</param>
    /// <param name="priority">The <see cref="NvmeQoSPriority"/> to store in bits 6-7.</param>
    /// <returns>Updated Flags value.</returns>
    public static uint SetQoSClass(uint flags, NvmeQoSPriority priority)
        => (flags & ~(QoSClassMask << QoSClassShift)) | ((uint)priority << QoSClassShift);

    // -----------------------------------------------------------------------
    // I/O deadline tier (bits 19-22)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Extracts the four-bit I/O deadline tier from extent Flags bits 19-22.
    /// </summary>
    /// <param name="flags">The 32-bit extent Flags value.</param>
    /// <returns>
    /// Tier value 0-15. Zero means "inherited from inode QoS class".
    /// 0xF means "best-effort". Values 0xC-0xE are reserved.
    /// </returns>
    public static byte GetDeadlineTier(uint flags)
        => (byte)((flags >> DeadlineTierShift) & DeadlineTierMask);

    /// <summary>
    /// Returns a new Flags value with bits 19-22 set to the specified I/O deadline tier.
    /// All other bits are preserved.
    /// </summary>
    /// <param name="flags">The original 32-bit extent Flags value.</param>
    /// <param name="tier">Deadline tier 0-15. Values above 15 are masked to 4 bits.</param>
    /// <returns>Updated Flags value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="tier"/> exceeds 0x0F (15).
    /// </exception>
    public static uint SetDeadlineTier(uint flags, byte tier)
    {
        if (tier > 0x0F)
            throw new ArgumentOutOfRangeException(nameof(tier), tier,
                "DeadlineTier must be in the range 0-15 (0x00-0x0F).");

        return (flags & ~(DeadlineTierMask << DeadlineTierShift)) | ((uint)(tier & 0x0F) << DeadlineTierShift);
    }

    // -----------------------------------------------------------------------
    // Deadline tier → TimeSpan mapping
    // -----------------------------------------------------------------------

    /// <summary>
    /// Maps a DeadlineTier value (0-15) to the corresponding I/O latency deadline as a
    /// <see cref="TimeSpan"/>, or <see langword="null"/> for inherited/reserved/best-effort tiers.
    /// </summary>
    /// <param name="tier">Deadline tier extracted from extent Flags bits 19-22.</param>
    /// <returns>
    /// The target latency deadline, or <see langword="null"/> when:
    /// <list type="bullet">
    ///   <item><description>0x0 — inherited from inode QoS class</description></item>
    ///   <item><description>0xC-0xE — reserved (undefined)</description></item>
    ///   <item><description>0xF — best-effort (no deadline guarantee)</description></item>
    /// </list>
    /// </returns>
    public static TimeSpan? GetDeadlineFromTier(byte tier) => tier switch
    {
        0x0 => null,                                          // inherited from inode QoS class
        0x1 => TimeSpan.FromMicroseconds(10),                 // NVMe passthrough / SPDK absolute minimum
        0x2 => TimeSpan.FromMicroseconds(50),                 // local NVMe, PCIe DMA
        0x3 => TimeSpan.FromMicroseconds(100),                // local NVMe, OS block layer
        0x4 => TimeSpan.FromMicroseconds(500),                // local SSD
        0x5 => TimeSpan.FromMilliseconds(1),                  // local HDD or network storage
        0x6 => TimeSpan.FromMilliseconds(5),                  // network storage / iSCSI
        0x7 => TimeSpan.FromMilliseconds(10),                 // acceptable for background I/O
        0x8 => TimeSpan.FromMilliseconds(50),                 // batch workload
        0x9 => TimeSpan.FromMilliseconds(100),                // offline analytics
        0xA => TimeSpan.FromMilliseconds(500),                // archival tier
        0xB => TimeSpan.FromMilliseconds(1_000),              // tape or cold archive (1 s)
        0xC => null,                                          // reserved
        0xD => null,                                          // reserved
        0xE => null,                                          // reserved
        0xF => null,                                          // best-effort (no deadline guarantee)
        _   => null,                                          // out-of-range: treat as inherited
    };
}
