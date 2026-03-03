using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Federation;

/// <summary>
/// Encodes per-shard offsets into an opaque Base64 continuation token for deterministic
/// cross-shard pagination. Each cursor captures how many items have been consumed from
/// each shard, allowing the next page request to skip already-returned items.
/// </summary>
/// <remarks>
/// <para>
/// Binary format:
/// [0..3]   Entry count (Int32, little-endian)
/// For each entry (24 bytes):
///   [0..15]  ShardVdeId (GUID, 16 bytes, raw bytes)
///   [16..23] Offset (Int64, little-endian) — number of items consumed from this shard
/// </para>
/// <para>
/// Immutable: all mutation methods return new instances. Thread-safe by design.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Cross-Shard Operations (VFED-20)")]
public sealed class FederatedPaginationCursor
{
    private const int HeaderSize = 4; // Int32 entry count
    private const int EntrySize = 24; // 16 (Guid) + 8 (Int64)

    private readonly Dictionary<Guid, long> _shardOffsets;

    /// <summary>
    /// An empty cursor representing the start of pagination (no items consumed).
    /// </summary>
    public static readonly FederatedPaginationCursor Empty = new(new Dictionary<Guid, long>());

    private FederatedPaginationCursor(Dictionary<Guid, long> shardOffsets)
    {
        _shardOffsets = shardOffsets;
    }

    /// <summary>
    /// Returns true if this cursor has no shard offsets (start of pagination).
    /// </summary>
    public bool IsEmpty => _shardOffsets.Count == 0;

    /// <summary>
    /// The number of shards tracked in this cursor.
    /// </summary>
    public int ShardCount => _shardOffsets.Count;

    /// <summary>
    /// Creates a cursor from the specified shard offsets.
    /// </summary>
    /// <param name="shardOffsets">Per-shard offset positions.</param>
    /// <returns>A new cursor encoding the given positions.</returns>
    /// <exception cref="ArgumentNullException">Thrown when shardOffsets is null.</exception>
    public static FederatedPaginationCursor Create(IReadOnlyDictionary<Guid, long> shardOffsets)
    {
        ArgumentNullException.ThrowIfNull(shardOffsets);

        var dict = new Dictionary<Guid, long>(shardOffsets.Count);
        foreach (var kvp in shardOffsets)
        {
            dict[kvp.Key] = kvp.Value;
        }

        return new FederatedPaginationCursor(dict);
    }

    /// <summary>
    /// Serializes this cursor to an opaque Base64 string token using BinaryPrimitives.
    /// </summary>
    /// <returns>An opaque Base64-encoded continuation token.</returns>
    public string Encode()
    {
        int totalSize = HeaderSize + (_shardOffsets.Count * EntrySize);
        byte[] buffer = new byte[totalSize];
        Span<byte> span = buffer;

        // Write entry count
        BinaryPrimitives.WriteInt32LittleEndian(span[..HeaderSize], _shardOffsets.Count);
        int offset = HeaderSize;

        // Write each entry: Guid (16 bytes) + offset (8 bytes LE)
        foreach (var kvp in _shardOffsets)
        {
            bool written = kvp.Key.TryWriteBytes(span.Slice(offset, 16));
            if (!written)
                throw new InvalidOperationException("Failed to write GUID bytes during cursor encoding.");

            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(offset + 16, 8), kvp.Value);
            offset += EntrySize;
        }

        return Convert.ToBase64String(buffer);
    }

    /// <summary>
    /// Deserializes a cursor from an opaque Base64 string token.
    /// </summary>
    /// <param name="token">The Base64-encoded continuation token.</param>
    /// <returns>The deserialized cursor.</returns>
    /// <exception cref="ArgumentNullException">Thrown when token is null.</exception>
    /// <exception cref="FormatException">Thrown when the token is malformed or corrupted.</exception>
    public static FederatedPaginationCursor Decode(string token)
    {
        ArgumentNullException.ThrowIfNull(token);

        byte[] buffer;
        try
        {
            buffer = Convert.FromBase64String(token);
        }
        catch (FormatException ex)
        {
            throw new FormatException("Invalid pagination cursor: not valid Base64.", ex);
        }

        ReadOnlySpan<byte> span = buffer;

        if (span.Length < HeaderSize)
            throw new FormatException($"Invalid pagination cursor: too short ({span.Length} bytes, minimum {HeaderSize}).");

        int count = BinaryPrimitives.ReadInt32LittleEndian(span[..HeaderSize]);

        if (count < 0)
            throw new FormatException($"Invalid pagination cursor: negative entry count ({count}).");

        int expectedSize = HeaderSize + (count * EntrySize);
        if (span.Length < expectedSize)
            throw new FormatException(
                $"Invalid pagination cursor: declared {count} entries ({expectedSize} bytes required), but only {span.Length} bytes available.");

        var offsets = new Dictionary<Guid, long>(count);
        int pos = HeaderSize;

        for (int i = 0; i < count; i++)
        {
            var shardId = new Guid(span.Slice(pos, 16));
            long shardOffset = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(pos + 16, 8));
            offsets[shardId] = shardOffset;
            pos += EntrySize;
        }

        return new FederatedPaginationCursor(offsets);
    }

    /// <summary>
    /// Gets the number of items already consumed from the specified shard.
    /// Returns 0 if the shard is not tracked in this cursor (e.g., a new shard added
    /// after the cursor was created).
    /// </summary>
    /// <param name="shardVdeId">The shard VDE identifier.</param>
    /// <returns>The offset (items consumed) for the shard, or 0 if not present.</returns>
    public long GetOffset(Guid shardVdeId)
    {
        return _shardOffsets.TryGetValue(shardVdeId, out long offset) ? offset : 0;
    }

    /// <summary>
    /// Returns a new cursor with the offset for the specified shard advanced by the
    /// given number of consumed items. Implements an immutable update pattern.
    /// </summary>
    /// <param name="shardVdeId">The shard whose offset to update.</param>
    /// <param name="itemsConsumed">Total items consumed from this shard (absolute, not delta).</param>
    /// <returns>A new cursor with the updated offset.</returns>
    public FederatedPaginationCursor WithAdvanced(Guid shardVdeId, long itemsConsumed)
    {
        var newOffsets = new Dictionary<Guid, long>(_shardOffsets)
        {
            [shardVdeId] = itemsConsumed
        };

        return new FederatedPaginationCursor(newOffsets);
    }

    /// <summary>
    /// Returns a snapshot of all shard offsets in this cursor.
    /// </summary>
    /// <returns>Read-only dictionary of shard-to-offset mappings.</returns>
    public IReadOnlyDictionary<Guid, long> GetAllOffsets()
    {
        return _shardOffsets;
    }
}
