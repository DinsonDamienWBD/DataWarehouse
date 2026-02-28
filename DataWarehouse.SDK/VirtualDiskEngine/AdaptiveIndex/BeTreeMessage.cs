using DataWarehouse.SDK.Contracts;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// Type of message buffered in internal Be-tree nodes.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-02 Be-tree message types")]
public enum BeTreeMessageType : byte
{
    /// <summary>Insert a new key-value pair.</summary>
    Insert = 0,

    /// <summary>Update an existing key's value.</summary>
    Update = 1,

    /// <summary>Delete a key (tombstone marker).</summary>
    Delete = 2,

    /// <summary>Insert or update (set value regardless of existence).</summary>
    Upsert = 3
}

/// <summary>
/// A message buffered in internal Be-tree nodes. Messages are batched in internal
/// node buffers and flushed downward only when buffers overflow, achieving
/// O(log_B(N)/epsilon) amortized I/Os per write.
/// </summary>
/// <remarks>
/// Messages are sorted by key first, then by timestamp descending (newest first)
/// for resolution. The Resolve method applies all pending messages for a key to
/// determine the effective state.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-02 Be-tree message")]
public readonly record struct BeTreeMessage : IComparable<BeTreeMessage>
{
    /// <summary>
    /// Gets the message type (Insert, Update, Delete, Upsert).
    /// </summary>
    public BeTreeMessageType Type { get; init; }

    /// <summary>
    /// Gets the key this message applies to.
    /// </summary>
    public byte[] Key { get; init; }

    /// <summary>
    /// Gets the value associated with this message (0 for Delete).
    /// </summary>
    public long Value { get; init; }

    /// <summary>
    /// Gets the monotonic timestamp for ordering. Newer messages take precedence.
    /// </summary>
    public long Timestamp { get; init; }

    /// <summary>
    /// Serialized size in bytes: [Type:1][KeyLen:4][Key:N][Value:8][Timestamp:8]
    /// </summary>
    public int SerializedSize => 1 + 4 + Key.Length + 8 + 8;

    /// <summary>
    /// Creates a new Be-tree message.
    /// </summary>
    public BeTreeMessage(BeTreeMessageType type, byte[] key, long value, long timestamp)
    {
        Type = type;
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Value = value;
        Timestamp = timestamp;
    }

    /// <summary>
    /// Compares messages by key first, then by timestamp descending (newest first).
    /// </summary>
    public int CompareTo(BeTreeMessage other)
    {
        int keyCompare = CompareKeys(Key, other.Key);
        if (keyCompare != 0)
            return keyCompare;
        // Descending timestamp: newer first
        return other.Timestamp.CompareTo(Timestamp);
    }

    /// <summary>
    /// Resolves a list of messages for the same key to determine the effective state.
    /// Messages must all share the same key. Returns the effective value, or null if deleted.
    /// </summary>
    /// <param name="messages">All messages for a single key, sorted by timestamp descending (newest first).</param>
    /// <returns>The resolved value, or null if the key is effectively deleted.</returns>
    public static long? Resolve(IReadOnlyList<BeTreeMessage> messages)
    {
        if (messages == null || messages.Count == 0)
            return null;

        // The most recent message (first in timestamp-descending order) wins
        var newest = messages[0];

        return newest.Type switch
        {
            BeTreeMessageType.Delete => null,
            BeTreeMessageType.Insert => newest.Value,
            BeTreeMessageType.Update => newest.Value,
            BeTreeMessageType.Upsert => newest.Value,
            _ => null
        };
    }

    /// <summary>
    /// Serializes this message to a span using big-endian byte order.
    /// Format: [Type:1][KeyLen:4][Key:N][Value:8][Timestamp:8]
    /// </summary>
    /// <param name="destination">Target span (must be at least SerializedSize bytes).</param>
    /// <returns>Number of bytes written.</returns>
    public int Serialize(Span<byte> destination)
    {
        int offset = 0;
        destination[offset++] = (byte)Type;
        BinaryPrimitives.WriteInt32BigEndian(destination.Slice(offset), Key.Length);
        offset += 4;
        Key.AsSpan().CopyTo(destination.Slice(offset));
        offset += Key.Length;
        BinaryPrimitives.WriteInt64BigEndian(destination.Slice(offset), Value);
        offset += 8;
        BinaryPrimitives.WriteInt64BigEndian(destination.Slice(offset), Timestamp);
        offset += 8;
        return offset;
    }

    /// <summary>
    /// Deserializes a message from a span using big-endian byte order.
    /// </summary>
    /// <param name="source">Source span.</param>
    /// <param name="bytesRead">Number of bytes consumed.</param>
    /// <returns>The deserialized message.</returns>
    public static BeTreeMessage Deserialize(ReadOnlySpan<byte> source, out int bytesRead)
    {
        int offset = 0;
        var type = (BeTreeMessageType)source[offset++];
        int keyLen = BinaryPrimitives.ReadInt32BigEndian(source.Slice(offset));
        offset += 4;
        // Guard against OOM from corrupt/malicious keyLen before allocating
        const int MaxKeyLen = 65536; // 64 KB — reasonable upper bound for B-epsilon tree keys
        if (keyLen < 0 || keyLen > MaxKeyLen || offset + keyLen > source.Length)
            throw new InvalidDataException(
                $"BeTreeMessage deserialization failed: keyLen {keyLen} is invalid (max {MaxKeyLen}, available {source.Length - offset}).");
        var key = source.Slice(offset, keyLen).ToArray();
        offset += keyLen;
        long value = BinaryPrimitives.ReadInt64BigEndian(source.Slice(offset));
        offset += 8;
        long timestamp = BinaryPrimitives.ReadInt64BigEndian(source.Slice(offset));
        offset += 8;
        bytesRead = offset;
        return new BeTreeMessage(type, key, value, timestamp);
    }

    /// <summary>
    /// Lexicographic byte-array comparison.
    /// </summary>
    internal static int CompareKeys(byte[] a, byte[] b)
    {
        if (a is null && b is null) return 0;
        if (a is null) return -1;
        if (b is null) return 1;

        int minLen = Math.Min(a.Length, b.Length);
        for (int i = 0; i < minLen; i++)
        {
            if (a[i] != b[i])
                return a[i].CompareTo(b[i]);
        }
        return a.Length.CompareTo(b.Length);
    }
}
