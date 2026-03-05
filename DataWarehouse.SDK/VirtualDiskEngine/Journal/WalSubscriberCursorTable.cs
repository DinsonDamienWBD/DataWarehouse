using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Journal;

// ── WalSubscriberFlags ────────────────────────────────────────────────────────

/// <summary>
/// Per-subscriber flags that control stream delivery behaviour and ACL enforcement.
/// Stored in the Flags field of each <see cref="WalSubscriberCursor"/>.
/// </summary>
[Flags]
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: WALS subscriber cursor flags (VOPT-35)")]
public enum WalSubscriberFlags : ulong
{
    /// <summary>No flags set.</summary>
    None = 0,

    /// <summary>Subscriber is active and consuming entries.</summary>
    Active = 1,

    /// <summary>Subscriber is temporarily paused; WAL advances but entries are not delivered.</summary>
    Paused = 2,

    /// <summary>Per-subscriber ACL restrictions are in effect (see policy vault).</summary>
    AclRestricted = 4,

    /// <summary>Subscriber is read-only; cannot acknowledge or advance its own cursor.</summary>
    ReadOnly = 8,

    /// <summary>Cursor advances automatically when entries are consumed via poll.</summary>
    AutoAdvance = 16,
}

// ── WalSubscriberCursor ───────────────────────────────────────────────────────

/// <summary>
/// A 32-byte on-disk cursor entry tracking a single WAL subscriber's read position.
/// Layout: [SubscriberId:8][LastEpoch:8][LastSequence:8][Flags:8]
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: WALS subscriber cursor entry (VOPT-35)")]
public readonly struct WalSubscriberCursor : IEquatable<WalSubscriberCursor>
{
    /// <summary>Size of one cursor entry in bytes.</summary>
    public const int CursorSize = 32;

    /// <summary>Unique identifier for the subscriber; zero is reserved (unregistered slot).</summary>
    public ulong SubscriberId { get; init; }

    /// <summary>Last MVCC epoch successfully consumed by this subscriber.</summary>
    public ulong LastEpoch { get; init; }

    /// <summary>Last WAL sequence number successfully consumed by this subscriber.</summary>
    public ulong LastSequence { get; init; }

    /// <summary>Per-subscriber flags controlling delivery and ACL enforcement.</summary>
    public WalSubscriberFlags Flags { get; init; }

    // ── Serialization ─────────────────────────────────────────────────────────

    /// <summary>
    /// Serializes the cursor to exactly <see cref="CursorSize"/> bytes in little-endian order.
    /// </summary>
    /// <param name="cursor">Cursor to serialize.</param>
    /// <param name="buffer">Destination buffer; must be at least <see cref="CursorSize"/> bytes.</param>
    /// <exception cref="ArgumentException">Buffer is too small.</exception>
    public static void Serialize(in WalSubscriberCursor cursor, Span<byte> buffer)
    {
        if (buffer.Length < CursorSize)
        {
            throw new ArgumentException(
                $"Buffer too small: need {CursorSize} bytes, got {buffer.Length}.", nameof(buffer));
        }

        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(0, 8), cursor.SubscriberId);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(8, 8), cursor.LastEpoch);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(16, 8), cursor.LastSequence);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(24, 8), (ulong)cursor.Flags);
    }

    /// <summary>
    /// Deserializes a cursor from exactly <see cref="CursorSize"/> bytes in little-endian order.
    /// </summary>
    /// <param name="buffer">Source buffer; must be at least <see cref="CursorSize"/> bytes.</param>
    /// <returns>The deserialized cursor.</returns>
    /// <exception cref="ArgumentException">Buffer is too small.</exception>
    public static WalSubscriberCursor Deserialize(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < CursorSize)
        {
            throw new ArgumentException(
                $"Buffer too small: need {CursorSize} bytes, got {buffer.Length}.", nameof(buffer));
        }

        return new WalSubscriberCursor
        {
            SubscriberId = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(0, 8)),
            LastEpoch    = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(8, 8)),
            LastSequence = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(16, 8)),
            Flags        = (WalSubscriberFlags)BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(24, 8)),
        };
    }

    // ── Equality ──────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public bool Equals(WalSubscriberCursor other)
        => SubscriberId == other.SubscriberId
        && LastEpoch    == other.LastEpoch
        && LastSequence == other.LastSequence
        && Flags        == other.Flags;

    /// <inheritdoc/>
    public override bool Equals(object? obj)
        => obj is WalSubscriberCursor c && Equals(c);

    /// <inheritdoc/>
    public override int GetHashCode()
        => HashCode.Combine(SubscriberId, LastEpoch, LastSequence, (ulong)Flags);

    /// <inheritdoc/>
    public static bool operator ==(WalSubscriberCursor left, WalSubscriberCursor right) => left.Equals(right);

    /// <inheritdoc/>
    public static bool operator !=(WalSubscriberCursor left, WalSubscriberCursor right) => !left.Equals(right);
}

// ── WalSubscriberCursorTable ──────────────────────────────────────────────────

/// <summary>
/// Manages the WALS region: a contiguous set of blocks that stores one
/// <see cref="WalSubscriberCursor"/> (32 bytes) per active WAL subscriber.
///
/// The spec mandates 128 cursor slots per 4 KB block, using a fixed 16-byte
/// region header.  This class enforces that limit and provides the in-memory
/// authoritative view; serialization round-trips to/from raw bytes.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: WALS subscriber cursor table region (VOPT-35)")]
public sealed class WalSubscriberCursorTable
{
    // ── Region identity ───────────────────────────────────────────────────────

    /// <summary>
    /// Block type tag for the WALS region: ASCII "WALS" = 0x57414C53.
    /// Matches <see cref="BlockTypeTags.Wals"/>.
    /// </summary>
    public const uint BlockTypeTag = BlockTypeTags.Wals;

    /// <summary>Module registry bit position for WALS (bit 21).</summary>
    public const byte ModuleBitPosition = 21;

    // ── Capacity constants ────────────────────────────────────────────────────

    /// <summary>
    /// Maximum number of subscriber cursors per spec: 128 per 4 KB cursor block.
    /// </summary>
    public const int MaxSubscribers = 128;

    // ── Fields ────────────────────────────────────────────────────────────────

    private readonly long _regionStartBlock;
    private readonly int  _blockSize;

    /// <summary>
    /// Computed capacity: (blockSize - 16) / 32.  For a 4 KB block this is 127,
    /// but the spec mandates a ceiling of <see cref="MaxSubscribers"/> (128), so
    /// implementations SHOULD use a 16-byte header that leaves room for exactly
    /// 128 slots.
    /// </summary>
    public int MaxCursorsPerBlock { get; }

    private readonly Dictionary<ulong, WalSubscriberCursor> _cursors;

    // ── Constructor ───────────────────────────────────────────────────────────

    /// <summary>
    /// Initialises a new, empty subscriber cursor table.
    /// </summary>
    /// <param name="regionStartBlock">Starting block number of the WALS region on disk.</param>
    /// <param name="blockSize">VDE block size in bytes (typically 4096).</param>
    public WalSubscriberCursorTable(long regionStartBlock, int blockSize)
    {
        if (blockSize < WalSubscriberCursor.CursorSize + 16)
        {
            throw new ArgumentOutOfRangeException(
                nameof(blockSize),
                $"Block size must be at least {WalSubscriberCursor.CursorSize + 16} bytes.");
        }

        _regionStartBlock = regionStartBlock;
        _blockSize        = blockSize;
        MaxCursorsPerBlock = (blockSize - 16) / WalSubscriberCursor.CursorSize;
        _cursors           = new Dictionary<ulong, WalSubscriberCursor>(MaxSubscribers);
    }

    // ── Properties ────────────────────────────────────────────────────────────

    /// <summary>Number of subscribers whose cursor has the <see cref="WalSubscriberFlags.Active"/> flag set.</summary>
    public int ActiveSubscriberCount
    {
        get
        {
            int count = 0;
            foreach (var c in _cursors.Values)
            {
                if ((c.Flags & WalSubscriberFlags.Active) != 0) count++;
            }
            return count;
        }
    }

    /// <summary>Total number of registered subscribers (active or paused).</summary>
    public int TotalSubscriberCount => _cursors.Count;

    // ── Subscriber management ─────────────────────────────────────────────────

    /// <summary>
    /// Returns the cursor for <paramref name="subscriberId"/>, or <see langword="null"/>
    /// if the subscriber is not registered.
    /// </summary>
    public WalSubscriberCursor? GetCursor(ulong subscriberId)
        => _cursors.TryGetValue(subscriberId, out var c) ? c : null;

    /// <summary>
    /// Registers a new subscriber with <paramref name="flags"/> starting at epoch 0, sequence 0.
    /// </summary>
    /// <param name="subscriberId">Unique subscriber identifier (zero is reserved).</param>
    /// <param name="flags">Initial subscriber flags.</param>
    /// <returns>
    /// <see langword="true"/> if registration succeeded;
    /// <see langword="false"/> if the table is at capacity (<see cref="MaxSubscribers"/>)
    /// or the subscriber is already registered.
    /// </returns>
    /// <exception cref="ArgumentException">subscriberId is zero.</exception>
    public bool RegisterSubscriber(ulong subscriberId, WalSubscriberFlags flags)
    {
        if (subscriberId == 0)
        {
            throw new ArgumentException("SubscriberId 0 is reserved.", nameof(subscriberId));
        }

        if (_cursors.ContainsKey(subscriberId)) return false;
        if (_cursors.Count >= MaxSubscribers)   return false;

        _cursors[subscriberId] = new WalSubscriberCursor
        {
            SubscriberId = subscriberId,
            LastEpoch    = 0,
            LastSequence = 0,
            Flags        = flags,
        };

        return true;
    }

    /// <summary>
    /// Removes the subscriber cursor for <paramref name="subscriberId"/>.
    /// </summary>
    /// <returns><see langword="true"/> if the subscriber was found and removed.</returns>
    public bool UnregisterSubscriber(ulong subscriberId)
        => _cursors.Remove(subscriberId);

    /// <summary>
    /// Updates the <see cref="WalSubscriberCursor.LastEpoch"/> and
    /// <see cref="WalSubscriberCursor.LastSequence"/> fields for an existing subscriber.
    /// No-op if the subscriber is not registered.
    /// </summary>
    public void AdvanceCursor(ulong subscriberId, ulong epoch, ulong sequence)
    {
        if (!_cursors.TryGetValue(subscriberId, out var existing)) return;

        _cursors[subscriberId] = existing with
        {
            LastEpoch    = epoch,
            LastSequence = sequence,
        };
    }

    // ── Queries ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all cursors whose <see cref="WalSubscriberFlags.Active"/> flag is set.
    /// </summary>
    public IReadOnlyList<WalSubscriberCursor> GetActiveCursors()
    {
        var result = new List<WalSubscriberCursor>();
        foreach (var c in _cursors.Values)
        {
            if ((c.Flags & WalSubscriberFlags.Active) != 0)
                result.Add(c);
        }
        return result;
    }

    /// <summary>
    /// Returns the minimum <see cref="WalSubscriberCursor.LastEpoch"/> across all active
    /// subscribers, which dictates the earliest epoch the WAL must retain for replay.
    /// Returns <see cref="ulong.MaxValue"/> when there are no active subscribers.
    /// </summary>
    public ulong GetOldestSubscriberEpoch()
    {
        ulong oldest = ulong.MaxValue;
        foreach (var c in _cursors.Values)
        {
            if ((c.Flags & WalSubscriberFlags.Active) != 0 && c.LastEpoch < oldest)
                oldest = c.LastEpoch;
        }
        return oldest;
    }

    // ── Serialization ─────────────────────────────────────────────────────────

    /// <summary>
    /// Serializes all registered cursors into <paramref name="buffer"/>.
    /// Format: [CursorCount:4][Reserved:12][cursor0:32][cursor1:32]...
    /// </summary>
    /// <param name="buffer">Destination buffer.</param>
    /// <returns>Number of bytes written.</returns>
    /// <exception cref="ArgumentException">Buffer is too small.</exception>
    public int Serialize(Span<byte> buffer)
    {
        int required = 16 + (_cursors.Count * WalSubscriberCursor.CursorSize);
        if (buffer.Length < required)
        {
            throw new ArgumentException(
                $"Buffer too small: need {required} bytes, got {buffer.Length}.", nameof(buffer));
        }

        // 16-byte header: [CursorCount:4][Reserved:12]
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(0, 4), _cursors.Count);
        buffer.Slice(4, 12).Clear();  // reserved

        int offset = 16;
        foreach (var cursor in _cursors.Values)
        {
            WalSubscriberCursor.Serialize(in cursor, buffer.Slice(offset, WalSubscriberCursor.CursorSize));
            offset += WalSubscriberCursor.CursorSize;
        }

        return offset;
    }

    /// <summary>
    /// Deserializes a <see cref="WalSubscriberCursorTable"/> from raw bytes produced by
    /// <see cref="Serialize"/>.
    /// </summary>
    /// <param name="buffer">Source buffer.</param>
    /// <param name="regionStartBlock">Starting block number of the WALS region on disk.</param>
    /// <param name="blockSize">VDE block size in bytes.</param>
    /// <returns>A populated <see cref="WalSubscriberCursorTable"/>.</returns>
    /// <exception cref="ArgumentException">Buffer is malformed or too small.</exception>
    public static WalSubscriberCursorTable Deserialize(
        ReadOnlySpan<byte> buffer, long regionStartBlock, int blockSize)
    {
        if (buffer.Length < 16)
        {
            throw new ArgumentException(
                "Buffer is too small to contain the WALS region header (16 bytes).", nameof(buffer));
        }

        int count = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(0, 4));
        if (count < 0 || count > MaxSubscribers)
        {
            throw new ArgumentException(
                $"Invalid cursor count {count}; must be in [0, {MaxSubscribers}].", nameof(buffer));
        }

        int required = 16 + (count * WalSubscriberCursor.CursorSize);
        if (buffer.Length < required)
        {
            throw new ArgumentException(
                $"Buffer too small for {count} cursors: need {required} bytes, got {buffer.Length}.",
                nameof(buffer));
        }

        var table  = new WalSubscriberCursorTable(regionStartBlock, blockSize);
        int offset = 16;

        for (int i = 0; i < count; i++)
        {
            var cursor = WalSubscriberCursor.Deserialize(
                buffer.Slice(offset, WalSubscriberCursor.CursorSize));

            if (cursor.SubscriberId != 0)
                table._cursors[cursor.SubscriberId] = cursor;

            offset += WalSubscriberCursor.CursorSize;
        }

        return table;
    }
}
