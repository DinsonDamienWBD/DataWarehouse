using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Format;

/// <summary>
/// Coordinate system used to interpret the geohash coordinates stored in
/// the <c>SpatialGeohash</c> field of a spatiotemporal extent entry.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87-26: STEX coordinate system enum (VOPT-39)")]
public enum SpatioCoordinateSystem : ushort
{
    /// <summary>
    /// WGS-84 geographic coordinates (latitude / longitude in degrees).
    /// This is the coordinate reference system used by GPS and most mapping systems.
    /// </summary>
    Wgs84 = 0,

    /// <summary>
    /// Earth-Centered, Earth-Fixed (ECEF) Cartesian coordinates (X, Y, Z in metres).
    /// Used in satellite navigation and geodesy where curvature effects must be explicit.
    /// </summary>
    Ecef = 1,

    /// <summary>
    /// Local Cartesian (flat-Earth approximation) with an origin defined per-VDE.
    /// Suitable for small-area deployments (robots, warehouses, indoor positioning).
    /// </summary>
    LocalCartesian = 2,

    /// <summary>
    /// Universal Transverse Mercator (UTM) projected coordinates. The zone number is
    /// encoded in the upper 8 bits of <see cref="SpatioTemporalModule.Precision"/>.
    /// </summary>
    UtmZone = 3,
}

/// <summary>
/// A 6-byte inode module entry stored at fixed offset <c>0x1FA</c> (byte 506) inside
/// the DataOsModuleArea of an <see cref="ExtendedInode512"/>. It records the coordinate
/// system, geohash precision, and Hilbert curve order used for 4D spatiotemporal extent
/// addressing (STEX, Module bit 24).
/// </summary>
/// <remarks>
/// <para>
/// <strong>On-disk layout (6 bytes, little-endian):</strong>
/// <code>
///   [0..2)  CoordinateSystem  — ushort: SpatioCoordinateSystem
///   [2..4)  Precision         — ushort: geohash precision in bits (64 = ~10 cm global)
///   [4..6)  HilbertOrder      — ushort: Hilbert curve order for spatial clustering (16-32 typical)
/// </code>
/// </para>
/// <para>
/// <strong>Module registration:</strong> STEX, bit 24 in the ModuleManifest.
/// Fixed inode offset <c>0x1FA</c> = byte 506 in the 512-byte inode DataOsModuleArea.
/// </para>
/// <para>
/// <strong>Purpose:</strong> When the SPATIOTEMPORAL flag is present in the module manifest
/// the inode's direct extent array is reinterpreted as an array of 64-byte
/// spatiotemporal extent entries (3 entries fit in the 6 standard 32-byte slots).
/// The <see cref="HilbertOrder"/> ensures spatially adjacent data lands on physically adjacent
/// blocks, enabling NVMe scatter-gather DMA prefetch for bounding-box queries.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87-26: STEX inode module (VOPT-39, Module bit 24)")]
public readonly struct SpatioTemporalModule
{
    // ── Constants ────────────────────────────────────────────────────────────

    /// <summary>Serialized size of this module entry in bytes.</summary>
    public const int SerializedSize = 6;

    /// <summary>
    /// Bit position of the STEX module in the ModuleManifest.
    /// The corresponding manifest bit is <c>1u &lt;&lt; ModuleBitPosition</c> = 0x01000000.
    /// </summary>
    public const byte ModuleBitPosition = 24;

    /// <summary>
    /// Fixed byte offset within the 512-byte inode DataOsModuleArea where this module
    /// is stored. <c>0x1FA</c> = 506 decimal.
    /// </summary>
    public const int InodeOffset = 0x1FA;

    // ── Fields ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Coordinate reference system used to interpret the geohash bytes stored in
    /// the <c>SpatialGeohash</c> field of each spatiotemporal extent entry.
    /// </summary>
    public SpatioCoordinateSystem CoordinateSystem { get; init; }

    /// <summary>
    /// Geohash precision in bits. Higher values yield finer spatial resolution:
    /// 64 bits ≈ 10 cm global resolution. Must be in the range [1, 128].
    /// </summary>
    public ushort Precision { get; init; }

    /// <summary>
    /// Hilbert curve order used to map 2D geohash coordinates to a 1D block-address
    /// sequence that clusters spatially adjacent data on physically adjacent blocks.
    /// Typical values are 16 (coarse) to 32 (fine). Higher orders improve spatial
    /// locality at the cost of longer Hilbert index computation.
    /// </summary>
    public ushort HilbertOrder { get; init; }

    // ── Default instance ─────────────────────────────────────────────────────

    /// <summary>
    /// Default module state: WGS-84 coordinate system, 64-bit geohash precision
    /// (~10 cm global resolution), Hilbert curve order 16.
    /// </summary>
    public static SpatioTemporalModule DefaultWgs84 { get; } = new SpatioTemporalModule
    {
        CoordinateSystem = SpatioCoordinateSystem.Wgs84,
        Precision        = 64,
        HilbertOrder     = 16,
    };

    // ── Serialization ────────────────────────────────────────────────────────

    /// <summary>
    /// Serializes <paramref name="module"/> into <paramref name="buffer"/> using
    /// little-endian byte order.
    /// </summary>
    /// <param name="module">The module state to serialize.</param>
    /// <param name="buffer">
    /// Output span of at least <see cref="SerializedSize"/> (6) bytes.
    /// Layout: [CoordinateSystem:2][Precision:2][HilbertOrder:2].
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="buffer"/> is shorter than <see cref="SerializedSize"/> bytes.
    /// </exception>
    public static void Serialize(in SpatioTemporalModule module, Span<byte> buffer)
    {
        if (buffer.Length < SerializedSize)
            throw new ArgumentException(
                $"Buffer must be at least {SerializedSize} bytes for SpatioTemporalModule.",
                nameof(buffer));

        BinaryPrimitives.WriteUInt16LittleEndian(buffer[0..2], (ushort)module.CoordinateSystem);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[2..4], module.Precision);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[4..6], module.HilbertOrder);
    }

    /// <summary>
    /// Deserializes a <see cref="SpatioTemporalModule"/> from <paramref name="buffer"/> using
    /// little-endian byte order.
    /// </summary>
    /// <param name="buffer">
    /// Input span of at least <see cref="SerializedSize"/> (6) bytes.
    /// Layout: [CoordinateSystem:2][Precision:2][HilbertOrder:2].
    /// </param>
    /// <returns>The deserialized module state.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="buffer"/> is shorter than <see cref="SerializedSize"/> bytes.
    /// </exception>
    public static SpatioTemporalModule Deserialize(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < SerializedSize)
            throw new ArgumentException(
                $"Buffer must be at least {SerializedSize} bytes for SpatioTemporalModule.",
                nameof(buffer));

        var coordinateSystem = (SpatioCoordinateSystem)BinaryPrimitives.ReadUInt16LittleEndian(buffer[0..2]);
        ushort precision     = BinaryPrimitives.ReadUInt16LittleEndian(buffer[2..4]);
        ushort hilbertOrder  = BinaryPrimitives.ReadUInt16LittleEndian(buffer[4..6]);

        return new SpatioTemporalModule
        {
            CoordinateSystem = coordinateSystem,
            Precision        = precision,
            HilbertOrder     = hilbertOrder,
        };
    }
}
