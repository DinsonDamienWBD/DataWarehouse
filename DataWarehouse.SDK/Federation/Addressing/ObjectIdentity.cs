using DataWarehouse.SDK.Contracts;
using System;

namespace DataWarehouse.SDK.Federation.Addressing;

/// <summary>
/// Location-independent object identity represented as a UUID.
/// </summary>
/// <remarks>
/// <para>
/// ObjectIdentity wraps a <see cref="Guid"/> with object-oriented semantics for federated
/// object storage. The same UUID refers to the same logical object across all storage nodes,
/// regardless of replication, migration, or geographic distribution.
/// </para>
/// <para>
/// <strong>UUID v7 Format:</strong> This type is designed to work with UUID v7 (RFC 9562),
/// which embeds a 48-bit millisecond timestamp in the most significant bits:
/// <code>
/// |----- 48 bits -----|-4-|-- 12 --|--2--|---------- 62 bits ----------|
/// |   unix_ts_ms      |ver| rand_a |var |          rand_b             |
/// </code>
/// </para>
/// <para>
/// <strong>Time-Ordering:</strong> The <see cref="Timestamp"/> property extracts the embedded
/// timestamp, enabling time-range queries without secondary indexes. UUIDs generated earlier
/// will sort before UUIDs generated later (assuming clocks are synchronized within milliseconds).
/// </para>
/// <para>
/// <strong>Equality and Comparison:</strong> ObjectIdentity is a value type with structural
/// equality. Two identities are equal if their underlying Guids are equal. Comparison is
/// lexicographic (byte-by-byte), which preserves time-ordering for UUID v7.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Location-independent object identity (FOS-02)")]
public readonly struct ObjectIdentity : IEquatable<ObjectIdentity>, IComparable<ObjectIdentity>
{
    private readonly Guid _value;

    /// <summary>
    /// Initializes a new instance of the <see cref="ObjectIdentity"/> struct.
    /// </summary>
    /// <param name="value">The underlying GUID value.</param>
    public ObjectIdentity(Guid value)
    {
        _value = value;
    }

    /// <summary>
    /// Gets the underlying GUID value.
    /// </summary>
    public Guid Value => _value;

    /// <summary>
    /// Extracts the timestamp from a UUID v7 identity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// UUID v7 embeds a 48-bit Unix timestamp (milliseconds since epoch) in the first 6 bytes.
    /// This property decodes that timestamp and returns it as a <see cref="DateTimeOffset"/>.
    /// </para>
    /// <para>
    /// <strong>Byte Order:</strong> The UUID spec uses big-endian byte order for the timestamp,
    /// but <see cref="Guid.ToByteArray"/> returns bytes in mixed-endian format (first 4 bytes
    /// are little-endian, next 2 are little-endian, remaining 10 are big-endian). This property
    /// accounts for that encoding.
    /// </para>
    /// <para>
    /// <strong>Non-UUID v7 Behavior:</strong> If this identity is not a UUID v7 (version field != 7),
    /// the returned timestamp may be meaningless. Use <see cref="IObjectIdentityProvider.IsValid"/>
    /// to validate UUID version before relying on timestamp extraction.
    /// </para>
    /// </remarks>
    public DateTimeOffset Timestamp
    {
        get
        {
            // UUID v7 format: timestamp_ms (48 bits) | ver (4) | rand_a (12) | var (2) | rand_b (62)
            var bytes = _value.ToByteArray();

            // Guid.ToByteArray() uses mixed endianness:
            // - Bytes 0-3: time_low (little-endian)
            // - Bytes 4-5: time_mid (little-endian)
            // - Bytes 6-7: time_hi_and_version (little-endian)
            // - Bytes 8-15: clock_seq and node (big-endian)
            //
            // For UUID v7, the timestamp is in the first 48 bits in big-endian.
            // We need to reverse the Guid's mixed-endian layout to extract the timestamp.

            // Extract timestamp from Guid's mixed-endian byte array
            // Time_low (4 bytes) + time_mid (2 bytes) = 48 bits
            long timestampMs = ((long)bytes[3] << 40) | ((long)bytes[2] << 32) |
                              ((long)bytes[1] << 24) | ((long)bytes[0] << 16) |
                              ((long)bytes[5] << 8) | bytes[4];

            return DateTimeOffset.FromUnixTimeMilliseconds(timestampMs);
        }
    }

    /// <summary>
    /// Gets an empty (all-zeros) object identity.
    /// </summary>
    public static ObjectIdentity Empty => new(Guid.Empty);

    /// <summary>
    /// Gets a value indicating whether this identity is empty (all zeros).
    /// </summary>
    public bool IsEmpty => _value == Guid.Empty;

    /// <summary>
    /// Returns the string representation of this identity in standard UUID format (xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx).
    /// </summary>
    public override string ToString() => _value.ToString("D");

    /// <summary>
    /// Returns the string representation of this identity using the specified format.
    /// </summary>
    /// <param name="format">
    /// A single format specifier that indicates how to format the value (D = dashes, N = no dashes, B = braces, P = parentheses).
    /// </param>
    public string ToString(string format) => _value.ToString(format);

    /// <summary>
    /// Determines whether this identity is equal to another identity.
    /// </summary>
    public bool Equals(ObjectIdentity other) => _value.Equals(other._value);

    /// <summary>
    /// Determines whether this identity is equal to the specified object.
    /// </summary>
    public override bool Equals(object? obj) => obj is ObjectIdentity other && Equals(other);

    /// <summary>
    /// Returns the hash code for this identity.
    /// </summary>
    public override int GetHashCode() => _value.GetHashCode();

    /// <summary>
    /// Compares this identity to another identity for ordering.
    /// </summary>
    /// <remarks>
    /// Comparison is lexicographic (byte-by-byte). For UUID v7, this preserves time-ordering:
    /// earlier UUIDs sort before later UUIDs.
    /// </remarks>
    public int CompareTo(ObjectIdentity other) => _value.CompareTo(other._value);

    /// <summary>
    /// Equality operator.
    /// </summary>
    public static bool operator ==(ObjectIdentity left, ObjectIdentity right) => left.Equals(right);

    /// <summary>
    /// Inequality operator.
    /// </summary>
    public static bool operator !=(ObjectIdentity left, ObjectIdentity right) => !left.Equals(right);

    /// <summary>
    /// Less-than operator.
    /// </summary>
    public static bool operator <(ObjectIdentity left, ObjectIdentity right) => left.CompareTo(right) < 0;

    /// <summary>
    /// Greater-than operator.
    /// </summary>
    public static bool operator >(ObjectIdentity left, ObjectIdentity right) => left.CompareTo(right) > 0;

    /// <summary>
    /// Less-than-or-equal operator.
    /// </summary>
    public static bool operator <=(ObjectIdentity left, ObjectIdentity right) => left.CompareTo(right) <= 0;

    /// <summary>
    /// Greater-than-or-equal operator.
    /// </summary>
    public static bool operator >=(ObjectIdentity left, ObjectIdentity right) => left.CompareTo(right) >= 0;

    /// <summary>
    /// Implicit conversion from ObjectIdentity to Guid.
    /// </summary>
    public static implicit operator Guid(ObjectIdentity identity) => identity._value;

    /// <summary>
    /// Implicit conversion from Guid to ObjectIdentity.
    /// </summary>
    public static implicit operator ObjectIdentity(Guid guid) => new(guid);

    /// <summary>
    /// Parses a string representation of a UUID into an ObjectIdentity.
    /// </summary>
    /// <param name="value">The string to parse.</param>
    /// <returns>The parsed ObjectIdentity.</returns>
    /// <exception cref="FormatException">Thrown if the string is not a valid UUID format.</exception>
    public static ObjectIdentity Parse(string value) => new(Guid.Parse(value));

    /// <summary>
    /// Attempts to parse a string representation of a UUID into an ObjectIdentity.
    /// </summary>
    /// <param name="value">The string to parse.</param>
    /// <param name="identity">When this method returns, contains the parsed identity if successful; otherwise <see cref="Empty"/>.</param>
    /// <returns><c>true</c> if parsing succeeded; otherwise <c>false</c>.</returns>
    public static bool TryParse(string? value, out ObjectIdentity identity)
    {
        if (Guid.TryParse(value, out var guid))
        {
            identity = new ObjectIdentity(guid);
            return true;
        }
        identity = Empty;
        return false;
    }
}
