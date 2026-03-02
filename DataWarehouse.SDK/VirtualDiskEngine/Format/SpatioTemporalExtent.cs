using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Format;

/// <summary>
/// A 64-byte spatiotemporal extent pointer that maps a 4D bounding region
/// (spatial geohash + temporal range) to a contiguous run of physical blocks.
/// </summary>
/// <remarks>
/// <para>
/// <strong>On-disk layout (64 bytes, little-endian):</strong>
/// <code>
///   [0..16)   SpatialGeohash  — 16 bytes: binary geohash (~10 cm resolution at 64-bit precision)
///   [16..24)  TimeEpochStart  — ulong: start of temporal range (UTC nanoseconds since Unix epoch)
///   [24..32)  TimeEpochEnd    — ulong: end of temporal range (UTC nanoseconds since Unix epoch)
///   [32..40)  StartBlock      — long:  first physical block in the Data Region
///   [40..44)  BlockCount      — uint:  number of contiguous blocks
///   [44..48)  Flags           — uint:  extent flags (same 32-bit flags as standard extents)
///   [48..64)  ExpectedHash    — 16 bytes: BLAKE3 truncated to 16 bytes for integrity checking
/// </code>
/// Total: 64 bytes = 2 × standard 32-byte extent slots (or ~2.67 × the 24-byte <see cref="InodeExtent"/>).
/// </para>
/// <para>
/// <strong>Reinterpretation rule:</strong> When the SPATIOTEMPORAL flag is set in the inode's
/// module manifest, the six standard 32-byte direct extent slots are reinterpreted as three
/// 64-byte <see cref="SpatioTemporalExtent"/> entries (<see cref="MaxDirectExtents"/> = 3).
/// Indirect blocks hold floor(4080 / 64) = 63 entries per block
/// (<see cref="ExtentsPerIndirectBlock"/> = 63).
/// </para>
/// <para>
/// <strong>Hilbert curve ordering:</strong> The <see cref="ComputeHilbertIndex"/> method maps a
/// 2D geohash to a 1D Hilbert curve index so the VDE allocator can place spatially adjacent data
/// on physically adjacent blocks, enabling efficient NVMe scatter-gather DMA prefetch for
/// bounding-box queries.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87-26: STEX 64B spatiotemporal extent (VOPT-39)")]
public readonly struct SpatioTemporalExtent : IEquatable<SpatioTemporalExtent>
{
    // ── Layout constants ─────────────────────────────────────────────────────

    /// <summary>Serialized size of a spatiotemporal extent in bytes (64).</summary>
    public const int SerializedSize = 64;

    /// <summary>
    /// Maximum number of spatiotemporal extents stored in the inode's direct extent slots.
    /// Six standard 32-byte slots = three 64-byte spatiotemporal slots.
    /// </summary>
    public const int MaxDirectExtents = 3;

    /// <summary>
    /// Number of spatiotemporal extents per indirect block.
    /// An indirect block payload is 4080 bytes (4096 - 16 byte trailer);
    /// floor(4080 / 64) = 63.
    /// </summary>
    public const int ExtentsPerIndirectBlock = 63;

    /// <summary>Size of the binary geohash field in bytes.</summary>
    public const int GeohashSize = 16;

    /// <summary>Size of the expected-hash field in bytes (BLAKE3 truncated to 16 bytes).</summary>
    public const int ExpectedHashSize = 16;

    // ── Fields ───────────────────────────────────────────────────────────────

    /// <summary>
    /// 16-byte binary geohash encoding the spatial bounding box of the data in this extent.
    /// At 64-bit precision this provides approximately 10 cm global resolution.
    /// Encode/decode using <see cref="EncodeGeohash"/> and <see cref="DecodeGeohash"/>.
    /// </summary>
    public byte[] SpatialGeohash { get; init; }

    /// <summary>
    /// Start of the temporal range covered by this extent, expressed as UTC nanoseconds
    /// since the Unix epoch (1970-01-01T00:00:00Z). Inclusive.
    /// </summary>
    public ulong TimeEpochStart { get; init; }

    /// <summary>
    /// End of the temporal range covered by this extent, expressed as UTC nanoseconds
    /// since the Unix epoch (1970-01-01T00:00:00Z). Inclusive.
    /// </summary>
    public ulong TimeEpochEnd { get; init; }

    /// <summary>First physical block number in the Data Region for this extent.</summary>
    public long StartBlock { get; init; }

    /// <summary>Number of contiguous physical blocks in this extent.</summary>
    public uint BlockCount { get; init; }

    /// <summary>
    /// Extent property flags. Uses the same 32-bit flag space as standard
    /// <see cref="ExtentFlags"/> (Compressed, Encrypted, Hole, SharedCow, etc.).
    /// </summary>
    public uint Flags { get; init; }

    /// <summary>
    /// 16-byte integrity hash (BLAKE3 truncated to 16 bytes) over the raw block data.
    /// Zero-filled when integrity checking is disabled for this extent.
    /// </summary>
    public byte[] ExpectedHash { get; init; }

    // ── Spatial / temporal query helpers ─────────────────────────────────────

    /// <summary>
    /// Returns <see langword="true"/> if this extent's spatial geohash covers the given
    /// WGS-84 point at the specified precision.
    /// </summary>
    /// <param name="latitude">Latitude in degrees (WGS-84), range [-90, 90].</param>
    /// <param name="longitude">Longitude in degrees (WGS-84), range [-180, 180].</param>
    /// <param name="precisionBits">Number of geohash bits to compare (must match the module's Precision).</param>
    /// <returns><see langword="true"/> if the encoded point falls within this extent's geohash cell.</returns>
    public bool ContainsPoint(double latitude, double longitude, int precisionBits)
    {
        if (SpatialGeohash is null || SpatialGeohash.Length < GeohashSize)
            return false;

        byte[] encoded = EncodeGeohash(latitude, longitude, precisionBits);
        return GeohashesMatch(SpatialGeohash, encoded, precisionBits);
    }

    /// <summary>
    /// Returns <see langword="true"/> if this extent's temporal range overlaps the query range.
    /// Two ranges overlap when neither ends before the other starts.
    /// </summary>
    /// <param name="queryStart">Start of query range (UTC nanoseconds since Unix epoch). Inclusive.</param>
    /// <param name="queryEnd">End of query range (UTC nanoseconds since Unix epoch). Inclusive.</param>
    public bool OverlapsTimeRange(ulong queryStart, ulong queryEnd)
        => TimeEpochStart <= queryEnd && TimeEpochEnd >= queryStart;

    /// <summary>
    /// Returns <see langword="true"/> if this extent overlaps the given 4D bounding box
    /// (spatial geohash range + temporal range).
    /// </summary>
    /// <param name="minGeohash">
    /// 16-byte binary geohash representing the minimum corner of the spatial bounding box.
    /// </param>
    /// <param name="maxGeohash">
    /// 16-byte binary geohash representing the maximum corner of the spatial bounding box.
    /// </param>
    /// <param name="timeStart">Start of temporal range (UTC nanoseconds). Inclusive.</param>
    /// <param name="timeEnd">End of temporal range (UTC nanoseconds). Inclusive.</param>
    /// <param name="precisionBits">Number of geohash bits used for comparison.</param>
    /// <returns>
    /// <see langword="true"/> if this extent's spatial geohash falls within [minGeohash, maxGeohash]
    /// and its temporal range overlaps [timeStart, timeEnd].
    /// </returns>
    public bool OverlapsBoundingBox(
        ReadOnlySpan<byte> minGeohash,
        ReadOnlySpan<byte> maxGeohash,
        ulong timeStart,
        ulong timeEnd,
        int precisionBits)
    {
        if (!OverlapsTimeRange(timeStart, timeEnd))
            return false;

        if (SpatialGeohash is null || SpatialGeohash.Length < GeohashSize)
            return false;

        int fullBytes   = precisionBits / 8;
        int remainBits  = precisionBits % 8;

        // Compare full bytes
        for (int i = 0; i < Math.Min(fullBytes, GeohashSize); i++)
        {
            if (SpatialGeohash[i] < minGeohash[i] || SpatialGeohash[i] > maxGeohash[i])
                return false;
        }

        // Compare remaining partial byte
        if (remainBits > 0 && fullBytes < GeohashSize)
        {
            byte mask = (byte)(0xFF << (8 - remainBits));
            byte extentNibble = (byte)(SpatialGeohash[fullBytes] & mask);
            byte minNibble    = (byte)(minGeohash[fullBytes]     & mask);
            byte maxNibble    = (byte)(maxGeohash[fullBytes]     & mask);
            if (extentNibble < minNibble || extentNibble > maxNibble)
                return false;
        }

        return true;
    }

    // ── Hilbert curve utilities ───────────────────────────────────────────────

    /// <summary>
    /// Encodes a WGS-84 latitude/longitude pair into a 16-byte binary geohash using
    /// interleaved bit encoding.
    /// </summary>
    /// <param name="latitude">Latitude in degrees, range [-90, 90].</param>
    /// <param name="longitude">Longitude in degrees, range [-180, 180].</param>
    /// <param name="precisionBits">
    /// Number of bits to encode. Maximum is 128 (16 bytes). Typical: 64 for ~10 cm global resolution.
    /// </param>
    /// <returns>A 16-byte binary geohash (zero-padded beyond <paramref name="precisionBits"/>).</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="precisionBits"/> is not in the range [1, 128].
    /// </exception>
    public static byte[] EncodeGeohash(double latitude, double longitude, int precisionBits)
    {
        if (precisionBits is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(precisionBits), precisionBits,
                "precisionBits must be in the range [1, 128].");

        // Clamp coordinates to valid ranges
        latitude  = Math.Clamp(latitude,   -90.0,  90.0);
        longitude = Math.Clamp(longitude, -180.0, 180.0);

        byte[] result = new byte[GeohashSize];

        double latMin = -90.0,  latMax = 90.0;
        double lonMin = -180.0, lonMax = 180.0;

        // Interleave longitude bits (even positions) and latitude bits (odd positions)
        for (int bit = 0; bit < precisionBits; bit++)
        {
            int byteIndex = bit / 8;
            int bitIndex  = 7 - (bit % 8);

            if (bit % 2 == 0)
            {
                // Longitude bit
                double lonMid = (lonMin + lonMax) / 2.0;
                if (longitude >= lonMid)
                {
                    result[byteIndex] |= (byte)(1 << bitIndex);
                    lonMin = lonMid;
                }
                else
                {
                    lonMax = lonMid;
                }
            }
            else
            {
                // Latitude bit
                double latMid = (latMin + latMax) / 2.0;
                if (latitude >= latMid)
                {
                    result[byteIndex] |= (byte)(1 << bitIndex);
                    latMin = latMid;
                }
                else
                {
                    latMax = latMid;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Decodes a 16-byte binary geohash back to a WGS-84 latitude/longitude pair.
    /// The returned coordinate represents the centre of the geohash cell.
    /// </summary>
    /// <param name="geohash">16-byte binary geohash produced by <see cref="EncodeGeohash"/>.</param>
    /// <param name="precisionBits">
    /// Number of bits that were encoded. Must match the value used when encoding.
    /// </param>
    /// <returns>The (Latitude, Longitude) centre of the geohash cell in degrees.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="geohash"/> is shorter than <see cref="GeohashSize"/> bytes.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="precisionBits"/> is not in the range [1, 128].
    /// </exception>
    public static (double Latitude, double Longitude) DecodeGeohash(
        ReadOnlySpan<byte> geohash,
        int precisionBits)
    {
        if (geohash.Length < GeohashSize)
            throw new ArgumentException(
                $"Geohash span must be at least {GeohashSize} bytes.", nameof(geohash));

        if (precisionBits is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(precisionBits), precisionBits,
                "precisionBits must be in the range [1, 128].");

        double latMin = -90.0,  latMax = 90.0;
        double lonMin = -180.0, lonMax = 180.0;

        for (int bit = 0; bit < precisionBits; bit++)
        {
            int byteIndex = bit / 8;
            int bitIndex  = 7 - (bit % 8);
            bool bitSet   = (geohash[byteIndex] & (1 << bitIndex)) != 0;

            if (bit % 2 == 0)
            {
                // Longitude bit
                double lonMid = (lonMin + lonMax) / 2.0;
                if (bitSet) lonMin = lonMid;
                else        lonMax = lonMid;
            }
            else
            {
                // Latitude bit
                double latMid = (latMin + latMax) / 2.0;
                if (bitSet) latMin = latMid;
                else        latMax = latMid;
            }
        }

        return ((latMin + latMax) / 2.0, (lonMin + lonMax) / 2.0);
    }

    /// <summary>
    /// Maps a 16-byte binary geohash to a 1D Hilbert curve index using the specified order.
    /// The Hilbert index is used to sort extents so that spatially adjacent data lands on
    /// physically adjacent blocks, enabling efficient scatter-gather DMA prefetch.
    /// </summary>
    /// <param name="geohash">16-byte binary geohash produced by <see cref="EncodeGeohash"/>.</param>
    /// <param name="hilbertOrder">
    /// Hilbert curve order (matches <see cref="SpatioTemporalModule.HilbertOrder"/>).
    /// Typical range: 16-32.
    /// </param>
    /// <returns>
    /// A 64-bit Hilbert index derived from the leading bits of the geohash, suitable for
    /// block-address ordering by the VDE extent allocator.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="geohash"/> is shorter than <see cref="GeohashSize"/> bytes.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="hilbertOrder"/> is not in the range [1, 32].
    /// </exception>
    public static ulong ComputeHilbertIndex(ReadOnlySpan<byte> geohash, int hilbertOrder)
    {
        if (geohash.Length < GeohashSize)
            throw new ArgumentException(
                $"Geohash span must be at least {GeohashSize} bytes.", nameof(geohash));

        if (hilbertOrder is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(hilbertOrder), hilbertOrder,
                "hilbertOrder must be in the range [1, 32].");

        // Extract X (longitude, even bits) and Y (latitude, odd bits) from interleaved geohash bits.
        // We use up to hilbertOrder bits per axis for a (2^hilbertOrder × 2^hilbertOrder) grid.
        int bitsNeeded = hilbertOrder * 2; // interleaved pairs
        uint x = 0u;
        uint y = 0u;

        for (int bit = 0; bit < Math.Min(bitsNeeded, 64); bit++)
        {
            int byteIndex = bit / 8;
            int bitIndex  = 7 - (bit % 8);
            bool bitSet   = (geohash[byteIndex] & (1 << bitIndex)) != 0;
            int axisBit   = bit / 2;

            if (bit % 2 == 0)
                x |= bitSet ? (1u << (hilbertOrder - 1 - axisBit)) : 0u;
            else
                y |= bitSet ? (1u << (hilbertOrder - 1 - axisBit)) : 0u;
        }

        // XY-to-Hilbert index via the standard rotate/reflect algorithm (order N = 2^hilbertOrder).
        return XyToHilbert(x, y, hilbertOrder);
    }

    // ── Serialization ────────────────────────────────────────────────────────

    /// <summary>
    /// Serializes <paramref name="extent"/> into <paramref name="buffer"/> using little-endian byte order.
    /// </summary>
    /// <param name="extent">The spatiotemporal extent to serialize.</param>
    /// <param name="buffer">Output span; must be at least <see cref="SerializedSize"/> (64) bytes.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="buffer"/> is shorter than <see cref="SerializedSize"/> bytes.
    /// </exception>
    public static void Serialize(in SpatioTemporalExtent extent, Span<byte> buffer)
    {
        if (buffer.Length < SerializedSize)
            throw new ArgumentException(
                $"Buffer must be at least {SerializedSize} bytes for SpatioTemporalExtent.",
                nameof(buffer));

        buffer[..SerializedSize].Clear();

        // [0..16)  SpatialGeohash
        if (extent.SpatialGeohash is { Length: > 0 })
        {
            int copyLen = Math.Min(extent.SpatialGeohash.Length, GeohashSize);
            extent.SpatialGeohash.AsSpan(0, copyLen).CopyTo(buffer[0..GeohashSize]);
        }

        // [16..24) TimeEpochStart
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[16..24], extent.TimeEpochStart);

        // [24..32) TimeEpochEnd
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[24..32], extent.TimeEpochEnd);

        // [32..40) StartBlock
        BinaryPrimitives.WriteInt64LittleEndian(buffer[32..40], extent.StartBlock);

        // [40..44) BlockCount
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[40..44], extent.BlockCount);

        // [44..48) Flags
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[44..48], extent.Flags);

        // [48..64) ExpectedHash
        if (extent.ExpectedHash is { Length: > 0 })
        {
            int copyLen = Math.Min(extent.ExpectedHash.Length, ExpectedHashSize);
            extent.ExpectedHash.AsSpan(0, copyLen).CopyTo(buffer[48..64]);
        }
    }

    /// <summary>
    /// Deserializes a <see cref="SpatioTemporalExtent"/> from exactly
    /// <see cref="SerializedSize"/> bytes using little-endian byte order.
    /// </summary>
    /// <param name="buffer">Input span; must be at least <see cref="SerializedSize"/> (64) bytes.</param>
    /// <returns>The deserialized spatiotemporal extent.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="buffer"/> is shorter than <see cref="SerializedSize"/> bytes.
    /// </exception>
    public static SpatioTemporalExtent Deserialize(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < SerializedSize)
            throw new ArgumentException(
                $"Buffer must be at least {SerializedSize} bytes for SpatioTemporalExtent.",
                nameof(buffer));

        byte[] geohash      = buffer[0..GeohashSize].ToArray();
        ulong  timeStart    = BinaryPrimitives.ReadUInt64LittleEndian(buffer[16..24]);
        ulong  timeEnd      = BinaryPrimitives.ReadUInt64LittleEndian(buffer[24..32]);
        long   startBlock   = BinaryPrimitives.ReadInt64LittleEndian(buffer[32..40]);
        uint   blockCount   = BinaryPrimitives.ReadUInt32LittleEndian(buffer[40..44]);
        uint   flags        = BinaryPrimitives.ReadUInt32LittleEndian(buffer[44..48]);
        byte[] expectedHash = buffer[48..64].ToArray();

        return new SpatioTemporalExtent
        {
            SpatialGeohash = geohash,
            TimeEpochStart = timeStart,
            TimeEpochEnd   = timeEnd,
            StartBlock     = startBlock,
            BlockCount     = blockCount,
            Flags          = flags,
            ExpectedHash   = expectedHash,
        };
    }

    // ── IEquatable ───────────────────────────────────────────────────────────

    /// <inheritdoc />
    public bool Equals(SpatioTemporalExtent other)
    {
        if (TimeEpochStart != other.TimeEpochStart ||
            TimeEpochEnd   != other.TimeEpochEnd   ||
            StartBlock     != other.StartBlock      ||
            BlockCount     != other.BlockCount      ||
            Flags          != other.Flags)
            return false;

        ReadOnlySpan<byte> h1 = SpatialGeohash  ?? Array.Empty<byte>();
        ReadOnlySpan<byte> h2 = other.SpatialGeohash ?? Array.Empty<byte>();
        ReadOnlySpan<byte> e1 = ExpectedHash    ?? Array.Empty<byte>();
        ReadOnlySpan<byte> e2 = other.ExpectedHash    ?? Array.Empty<byte>();

        return h1.SequenceEqual(h2) && e1.SequenceEqual(e2);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SpatioTemporalExtent other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(StartBlock, BlockCount, Flags, TimeEpochStart, TimeEpochEnd);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(SpatioTemporalExtent left, SpatioTemporalExtent right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(SpatioTemporalExtent left, SpatioTemporalExtent right) => !left.Equals(right);

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Converts 2D grid coordinates to a Hilbert curve index using the standard
    /// rotate/reflect algorithm. This is an in-place, branch-based implementation
    /// with O(order) iterations and no heap allocation.
    /// </summary>
    private static ulong XyToHilbert(uint x, uint y, int order)
    {
        ulong index = 0uL;
        for (uint step = (uint)(1 << (order - 1)); step > 0; step >>= 1)
        {
            uint rx = (x & step) > 0 ? 1u : 0u;
            uint ry = (y & step) > 0 ? 1u : 0u;
            index += (ulong)step * step * ((3u * rx) ^ ry);
            RotateHilbert(step, ref x, ref y, rx, ry);
        }
        return index;
    }

    /// <summary>
    /// Applies the Hilbert curve rotation/reflection transform to (x, y)
    /// for a given step size and quadrant flags.
    /// </summary>
    private static void RotateHilbert(uint n, ref uint x, ref uint y, uint rx, uint ry)
    {
        if (ry == 0)
        {
            if (rx == 1)
            {
                x = n - 1 - x;
                y = n - 1 - y;
            }
            (x, y) = (y, x);
        }
    }

    /// <summary>
    /// Compares two geohashes for equality up to <paramref name="precisionBits"/> bits.
    /// Bits beyond <paramref name="precisionBits"/> are masked out before comparison.
    /// </summary>
    private static bool GeohashesMatch(
        ReadOnlySpan<byte> a,
        ReadOnlySpan<byte> b,
        int precisionBits)
    {
        int fullBytes  = precisionBits / 8;
        int remainBits = precisionBits % 8;

        for (int i = 0; i < Math.Min(fullBytes, GeohashSize); i++)
        {
            if (a[i] != b[i])
                return false;
        }

        if (remainBits > 0 && fullBytes < GeohashSize)
        {
            byte mask = (byte)(0xFF << (8 - remainBits));
            if ((a[fullBytes] & mask) != (b[fullBytes] & mask))
                return false;
        }

        return true;
    }
}
