using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Regions;

// ── ZNS Zone State ───────────────────────────────────────────────────────────

/// <summary>
/// Lifecycle state of a ZNS physical zone as tracked by the DWVD engine.
/// </summary>
public enum ZnsZoneState : ushort
{
    /// <summary>Zone is empty and available for a new epoch mapping.</summary>
    Free = 0,

    /// <summary>Zone is mapped to an active epoch and accepting sequential writes.</summary>
    Active = 1,

    /// <summary>Zone is full — no more writes allowed, epoch still live.</summary>
    Full = 2,

    /// <summary>
    /// Zone's epoch has been vacuumed. The zone is queued for a
    /// <c>ZNS_ZONE_RESET</c> hardware command.
    /// </summary>
    Dead = 3,

    /// <summary>
    /// A <c>ZNS_ZONE_RESET</c> command has been issued and is in flight.
    /// Transitions to <see cref="Free"/> on hardware acknowledgement.
    /// </summary>
    Resetting = 4,
}

// ── ZNS Zone Flags ───────────────────────────────────────────────────────────

/// <summary>
/// Bit-flags providing additional hints about a ZNS zone's intended usage.
/// </summary>
[Flags]
public enum ZnsZoneFlags : ushort
{
    /// <summary>No special flags.</summary>
    None = 0,

    /// <summary>Zone is reserved for metadata (conventional-zone topology companion).</summary>
    MetadataZone = 1,

    /// <summary>Zone carries data blocks only (normal ZNS data path).</summary>
    DataOnly = 2,

    /// <summary>Reserved for future use — must be zero when writing.</summary>
    Reserved = 4,
}

// ── ZnsZoneMapEntry ──────────────────────────────────────────────────────────

/// <summary>
/// A single entry in the ZNS Zone Map: maps one MVCC epoch to one physical ZNS zone.
/// On-disk layout: <c>[EpochId:8][ZoneId:4][State:2][Flags:2]</c> = 16 bytes, all little-endian.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: ZNSM epoch-to-zone mapping entry (VOPT-34)")]
public readonly struct ZnsZoneMapEntry : IEquatable<ZnsZoneMapEntry>
{
    /// <summary>Fixed on-disk size of one zone map entry in bytes.</summary>
    public const int EntrySize = 16;

    /// <summary>MVCC epoch that owns this zone.</summary>
    public ulong EpochId { get; init; }

    /// <summary>Physical ZNS zone identifier reported by the NVMe controller.</summary>
    public uint ZoneId { get; init; }

    /// <summary>Current lifecycle state of the zone.</summary>
    public ZnsZoneState State { get; init; }

    /// <summary>Hint flags for zone usage and routing.</summary>
    public ZnsZoneFlags Flags { get; init; }

    // ── Serialization ────────────────────────────────────────────────────

    /// <summary>
    /// Serializes the entry into <paramref name="buffer"/> (must be at least
    /// <see cref="EntrySize"/> = 16 bytes). Uses little-endian byte order.
    /// </summary>
    public static void Serialize(in ZnsZoneMapEntry entry, Span<byte> buffer)
    {
        if (buffer.Length < EntrySize)
            throw new ArgumentException($"Buffer must be at least {EntrySize} bytes.", nameof(buffer));

        BinaryPrimitives.WriteUInt64LittleEndian(buffer,         entry.EpochId);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[8..],    entry.ZoneId);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[12..],   (ushort)entry.State);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[14..],   (ushort)entry.Flags);
    }

    /// <summary>
    /// Deserializes one entry from <paramref name="buffer"/> (must be at least
    /// <see cref="EntrySize"/> = 16 bytes). Uses little-endian byte order.
    /// </summary>
    public static ZnsZoneMapEntry Deserialize(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < EntrySize)
            throw new ArgumentException($"Buffer must be at least {EntrySize} bytes.", nameof(buffer));

        return new ZnsZoneMapEntry
        {
            EpochId = BinaryPrimitives.ReadUInt64LittleEndian(buffer),
            ZoneId  = BinaryPrimitives.ReadUInt32LittleEndian(buffer[8..]),
            State   = (ZnsZoneState)BinaryPrimitives.ReadUInt16LittleEndian(buffer[12..]),
            Flags   = (ZnsZoneFlags)BinaryPrimitives.ReadUInt16LittleEndian(buffer[14..]),
        };
    }

    // ── Equality ─────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public bool Equals(ZnsZoneMapEntry other) =>
        EpochId == other.EpochId &&
        ZoneId  == other.ZoneId  &&
        State   == other.State   &&
        Flags   == other.Flags;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ZnsZoneMapEntry e && Equals(e);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(EpochId, ZoneId, State, Flags);

    /// <inheritdoc/>
    public static bool operator ==(ZnsZoneMapEntry left, ZnsZoneMapEntry right) => left.Equals(right);

    /// <inheritdoc/>
    public static bool operator !=(ZnsZoneMapEntry left, ZnsZoneMapEntry right) => !left.Equals(right);

    /// <inheritdoc/>
    public override string ToString() =>
        $"[ZnsZoneMapEntry Epoch={EpochId} Zone={ZoneId} State={State} Flags={Flags}]";
}

// ── ZnsZoneMapRegion ─────────────────────────────────────────────────────────

/// <summary>
/// The ZNSM region (module bit 23) stores the epoch-to-ZNS-zone mapping table.
/// Each entry maps one MVCC epoch to one physical ZNS zone so that dead epochs
/// can be reclaimed via a single <c>ZNS_ZONE_RESET</c> command instead of
/// millions of per-block TRIM commands.
/// </summary>
/// <remarks>
/// On-disk block type tag: <c>ZNSM</c> (0x5A4E534D). Each block holds
/// <c>(blockSize - UniversalBlockTrailerSize) / 16</c> entries.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: ZNSM zone map region (VOPT-34)")]
public sealed class ZnsZoneMapRegion
{
    // ── Constants ────────────────────────────────────────────────────────

    /// <summary>
    /// Block type tag for ZNS Zone Map blocks: <c>ZNSM</c> = 0x5A4E534D,
    /// matching <see cref="BlockTypeTags.ZNSM"/>.
    /// </summary>
    public const uint BlockTypeTag = 0x5A4E534D;

    // ── Fields ───────────────────────────────────────────────────────────

    private readonly List<ZnsZoneMapEntry> _entries;

    // ── Constructor ──────────────────────────────────────────────────────

    /// <summary>
    /// Initialises an empty ZNS zone map region.
    /// </summary>
    /// <param name="regionStartBlock">First block number of this region in the VDE.</param>
    /// <param name="blockSize">Block size in bytes for this VDE (must be a valid DWVD block size).</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="blockSize"/> is smaller than the trailer plus one entry.
    /// </exception>
    public ZnsZoneMapRegion(long regionStartBlock, int blockSize)
    {
        if (regionStartBlock < 0)
            throw new ArgumentOutOfRangeException(nameof(regionStartBlock), "Region start block must be non-negative.");

        int minBlockSize = FormatConstants.UniversalBlockTrailerSize + ZnsZoneMapEntry.EntrySize;
        if (blockSize < minBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"Block size must be at least {minBlockSize} bytes to hold one zone map entry.");

        RegionStartBlock = regionStartBlock;
        BlockSize        = blockSize;
        _entries         = new List<ZnsZoneMapEntry>();
    }

    // ── Properties ───────────────────────────────────────────────────────

    /// <summary>First block number of this region within the VDE.</summary>
    public long RegionStartBlock { get; }

    /// <summary>Block size in bytes for the owning VDE.</summary>
    public int BlockSize { get; }

    /// <summary>
    /// Maximum number of zone map entries that fit in a single block, accounting
    /// for the universal block trailer.
    /// </summary>
    public int MaxEntriesPerBlock =>
        (BlockSize - FormatConstants.UniversalBlockTrailerSize) / ZnsZoneMapEntry.EntrySize;

    /// <summary>All zone map entries currently held in this region. Uniqueness is enforced by the allocator layer.</summary>
    public IReadOnlyList<ZnsZoneMapEntry> Entries => _entries;

    // ── Mutation ─────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a new zone map entry. Duplicate epoch or zone IDs are permitted by
    /// the region itself — uniqueness is enforced by the allocator layer.
    /// </summary>
    public void AddEntry(ZnsZoneMapEntry entry) => _entries.Add(entry);

    /// <summary>
    /// Transitions the zone associated with <paramref name="epochId"/> to
    /// <see cref="ZnsZoneState.Dead"/>. Only <see cref="ZnsZoneState.Active"/>
    /// and <see cref="ZnsZoneState.Full"/> entries are eligible.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no active/full entry for <paramref name="epochId"/> is found.
    /// </exception>
    public void MarkZoneDead(ulong epochId)
    {
        int idx = FindIndexByEpoch(epochId);
        if (idx < 0)
            throw new InvalidOperationException($"No zone map entry for epoch {epochId}.");

        var entry = _entries[idx];
        if (entry.State != ZnsZoneState.Active && entry.State != ZnsZoneState.Full)
            throw new InvalidOperationException(
                $"Cannot mark zone dead: epoch {epochId} is in state {entry.State} (must be Active or Full).");

        _entries[idx] = entry with { State = ZnsZoneState.Dead };
    }

    /// <summary>
    /// Transitions zone <paramref name="zoneId"/> from
    /// <see cref="ZnsZoneState.Dead"/> (or <see cref="ZnsZoneState.Resetting"/>)
    /// to <see cref="ZnsZoneState.Free"/> after a hardware <c>ZNS_ZONE_RESET</c>
    /// has completed.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no matching Dead/Resetting entry is found.
    /// </exception>
    public void MarkZoneReset(uint zoneId)
    {
        int idx = FindIndexByZone(zoneId);
        if (idx < 0)
            throw new InvalidOperationException($"No zone map entry for zone {zoneId}.");

        var entry = _entries[idx];
        if (entry.State != ZnsZoneState.Dead && entry.State != ZnsZoneState.Resetting)
            throw new InvalidOperationException(
                $"Cannot reset zone {zoneId}: state is {entry.State} (must be Dead or Resetting).");

        _entries[idx] = entry with { State = ZnsZoneState.Free, EpochId = 0 };
    }

    // ── Queries ──────────────────────────────────────────────────────────

    /// <summary>Returns the entry for <paramref name="epochId"/>, or <c>null</c> if not found.</summary>
    public ZnsZoneMapEntry? FindByEpoch(ulong epochId)
    {
        int idx = FindIndexByEpoch(epochId);
        return idx >= 0 ? _entries[idx] : null;
    }

    /// <summary>Returns the entry for <paramref name="zoneId"/>, or <c>null</c> if not found.</summary>
    public ZnsZoneMapEntry? FindByZone(uint zoneId)
    {
        int idx = FindIndexByZone(zoneId);
        return idx >= 0 ? _entries[idx] : null;
    }

    /// <summary>Returns all entries whose state is <see cref="ZnsZoneState.Dead"/>.</summary>
    public IReadOnlyList<ZnsZoneMapEntry> GetDeadZones() =>
        _entries.Where(e => e.State == ZnsZoneState.Dead).ToList();

    // ── Serialization ────────────────────────────────────────────────────

    /// <summary>
    /// Serializes all zone map entries into <paramref name="buffer"/>, packed
    /// contiguously (no per-block trailers — callers handle block framing).
    /// Returns the number of bytes written.
    /// </summary>
    public int Serialize(Span<byte> buffer)
    {
        int required = _entries.Count * ZnsZoneMapEntry.EntrySize;
        if (buffer.Length < required)
            throw new ArgumentException(
                $"Buffer must be at least {required} bytes for {_entries.Count} entries.", nameof(buffer));

        int offset = 0;
        foreach (var entry in _entries)
        {
            ZnsZoneMapEntry.Serialize(in entry, buffer[offset..]);
            offset += ZnsZoneMapEntry.EntrySize;
        }

        return offset;
    }

    /// <summary>
    /// Deserializes a <see cref="ZnsZoneMapRegion"/> from <paramref name="buffer"/>,
    /// reading as many complete 16-byte entries as the buffer contains.
    /// </summary>
    public static ZnsZoneMapRegion Deserialize(
        ReadOnlySpan<byte> buffer,
        long regionStartBlock,
        int blockSize)
    {
        var region  = new ZnsZoneMapRegion(regionStartBlock, blockSize);
        int offset  = 0;
        int entryCount = buffer.Length / ZnsZoneMapEntry.EntrySize;

        for (int i = 0; i < entryCount; i++)
        {
            var entry = ZnsZoneMapEntry.Deserialize(buffer[offset..]);
            region._entries.Add(entry);
            offset += ZnsZoneMapEntry.EntrySize;
        }

        return region;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private int FindIndexByEpoch(ulong epochId)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].EpochId == epochId && _entries[i].State != ZnsZoneState.Free)
                return i;
        }
        return -1;
    }

    private int FindIndexByZone(uint zoneId)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].ZoneId == zoneId)
                return i;
        }
        return -1;
    }
}
