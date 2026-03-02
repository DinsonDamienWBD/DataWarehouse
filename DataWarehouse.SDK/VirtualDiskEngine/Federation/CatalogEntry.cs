using System;
using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Federation;

/// <summary>
/// Status of a catalog entry within the shard catalog hierarchy.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Shard Catalog (VFED-06)")]
public enum CatalogEntryStatus : byte
{
    /// <summary>Entry is active and serving requests.</summary>
    Active = 0,

    /// <summary>Entry is being migrated to a new shard.</summary>
    Migrating = 1,

    /// <summary>Entry is draining connections before removal.</summary>
    Draining = 2,

    /// <summary>Entry is offline and not serving requests.</summary>
    Offline = 3
}

/// <summary>
/// Binary-serializable catalog entry shared by all four levels of the shard catalog hierarchy.
/// Fixed 64-byte layout for cache-friendly sorted-array storage.
/// </summary>
/// <remarks>
/// <para>Binary layout (64 bytes, little-endian):</para>
/// <list type="table">
/// <item><term>[0..15]</term><description>ShardVdeId (16 bytes, GUID)</description></item>
/// <item><term>[16..19]</term><description>StartSlot (int32 LE, inclusive)</description></item>
/// <item><term>[20..23]</term><description>EndSlot (int32 LE, exclusive)</description></item>
/// <item><term>[24]</term><description>Level (byte)</description></item>
/// <item><term>[25]</term><description>Status (byte)</description></item>
/// <item><term>[26..27]</term><description>Reserved (2 bytes, zero)</description></item>
/// <item><term>[28..35]</term><description>BloomFilterDigest (int64 LE)</description></item>
/// <item><term>[36..43]</term><description>CreatedUtcTicks (int64 LE)</description></item>
/// <item><term>[44..51]</term><description>LastAccessedUtcTicks (int64 LE)</description></item>
/// <item><term>[52..63]</term><description>Reserved (12 bytes, zero, future use)</description></item>
/// </list>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Shard Catalog (VFED-06)")]
public readonly record struct CatalogEntry
{
    /// <summary>
    /// Serialized size of a single catalog entry in bytes.
    /// </summary>
    public const int SerializedSize = 64;

    /// <summary>
    /// The VDE instance this entry routes to.
    /// </summary>
    public Guid ShardVdeId { get; init; }

    /// <summary>
    /// Beginning of the hash-slot range (inclusive).
    /// </summary>
    public int StartSlot { get; init; }

    /// <summary>
    /// End of the hash-slot range (exclusive).
    /// </summary>
    public int EndSlot { get; init; }

    /// <summary>
    /// Which catalog level this entry represents.
    /// </summary>
    public ShardLevel Level { get; init; }

    /// <summary>
    /// Current operational status of this entry.
    /// </summary>
    public CatalogEntryStatus Status { get; init; }

    /// <summary>
    /// 8-byte bloom filter fingerprint for quick negative rejection. Zero means no filter is set.
    /// </summary>
    public long BloomFilterDigest { get; init; }

    /// <summary>
    /// Creation timestamp as UTC ticks.
    /// </summary>
    public long CreatedUtcTicks { get; init; }

    /// <summary>
    /// Last access timestamp as UTC ticks, used for LRU eviction decisions.
    /// </summary>
    public long LastAccessedUtcTicks { get; init; }

    /// <summary>
    /// Returns <c>true</c> if the given slot falls within this entry's [StartSlot, EndSlot) range.
    /// </summary>
    /// <param name="slot">The hash slot to test.</param>
    /// <returns><c>true</c> if the slot is contained; otherwise <c>false</c>.</returns>
    public bool ContainsSlot(int slot) => slot >= StartSlot && slot < EndSlot;

    /// <summary>
    /// Returns <c>true</c> if this entry's status is <see cref="CatalogEntryStatus.Active"/>.
    /// </summary>
    public bool IsActive => Status == CatalogEntryStatus.Active;

    /// <summary>
    /// Serializes this entry to the specified destination span using BinaryPrimitives.
    /// </summary>
    /// <param name="dest">A span of at least <see cref="SerializedSize"/> bytes.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="dest"/> is smaller than <see cref="SerializedSize"/> bytes.</exception>
    public void WriteTo(Span<byte> dest)
    {
        if (dest.Length < SerializedSize)
            throw new ArgumentException($"Destination must be at least {SerializedSize} bytes.", nameof(dest));

        // [0..15] ShardVdeId
        ShardVdeId.TryWriteBytes(dest[..16]);

        // [16..19] StartSlot
        BinaryPrimitives.WriteInt32LittleEndian(dest[16..], StartSlot);

        // [20..23] EndSlot
        BinaryPrimitives.WriteInt32LittleEndian(dest[20..], EndSlot);

        // [24] Level
        dest[24] = (byte)Level;

        // [25] Status
        dest[25] = (byte)Status;

        // [26..27] Reserved
        dest[26] = 0;
        dest[27] = 0;

        // [28..35] BloomFilterDigest
        BinaryPrimitives.WriteInt64LittleEndian(dest[28..], BloomFilterDigest);

        // [36..43] CreatedUtcTicks
        BinaryPrimitives.WriteInt64LittleEndian(dest[36..], CreatedUtcTicks);

        // [44..51] LastAccessedUtcTicks
        BinaryPrimitives.WriteInt64LittleEndian(dest[44..], LastAccessedUtcTicks);

        // [52..63] Reserved (12 bytes)
        dest[52..64].Clear();
    }

    /// <summary>
    /// Deserializes a <see cref="CatalogEntry"/> from the specified source span.
    /// </summary>
    /// <param name="src">A read-only span of at least <see cref="SerializedSize"/> bytes.</param>
    /// <returns>The deserialized catalog entry.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="src"/> is smaller than <see cref="SerializedSize"/> bytes.</exception>
    public static CatalogEntry ReadFrom(ReadOnlySpan<byte> src)
    {
        if (src.Length < SerializedSize)
            throw new ArgumentException($"Source must be at least {SerializedSize} bytes.", nameof(src));

        return new CatalogEntry
        {
            ShardVdeId = new Guid(src[..16]),
            StartSlot = BinaryPrimitives.ReadInt32LittleEndian(src[16..]),
            EndSlot = BinaryPrimitives.ReadInt32LittleEndian(src[20..]),
            Level = (ShardLevel)src[24],
            Status = (CatalogEntryStatus)src[25],
            BloomFilterDigest = BinaryPrimitives.ReadInt64LittleEndian(src[28..]),
            CreatedUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(src[36..]),
            LastAccessedUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(src[44..])
        };
    }
}
