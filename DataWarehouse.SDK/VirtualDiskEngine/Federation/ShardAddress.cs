using System;
using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Federation;

/// <summary>
/// Identifies a shard's position in the federation hierarchy.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Federation Router (VFED-02)")]
public enum ShardLevel : byte
{
    /// <summary>Root level: the top-level federation coordinator.</summary>
    Root = 0,

    /// <summary>Domain level: a namespace partition within the federation.</summary>
    Domain = 1,

    /// <summary>Index level: an index shard within a domain.</summary>
    Index = 2,

    /// <summary>Data level: a leaf shard holding actual data blocks.</summary>
    Data = 3
}

/// <summary>
/// Immutable address identifying a specific shard within the federation.
/// Combines a VDE instance identifier, an inode number within that VDE, and a hierarchy level.
/// Binary-serializable to a fixed 32-byte layout using BinaryPrimitives.
/// </summary>
/// <remarks>
/// Wire format (32 bytes):
/// [0..15]  VdeId (GUID, 16 bytes, written as raw bytes)
/// [16..23] InodeNumber (Int64, little-endian)
/// [24]     Level (byte)
/// [25..31] Reserved padding (7 bytes, zeroed)
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Federation Router (VFED-02)")]
public readonly record struct ShardAddress(Guid VdeId, long InodeNumber, ShardLevel Level)
{
    /// <summary>
    /// Serialized size in bytes for a single ShardAddress.
    /// </summary>
    public const int SerializedSize = 32;

    /// <summary>
    /// Sentinel value representing "not found" or "unresolved" address.
    /// </summary>
    public static readonly ShardAddress None = default;

    /// <summary>
    /// Returns true if this address points to a valid VDE instance (non-empty GUID).
    /// </summary>
    public bool IsValid => VdeId != Guid.Empty;

    /// <summary>
    /// Serializes this ShardAddress into a 32-byte span using BinaryPrimitives.
    /// </summary>
    /// <param name="destination">Target span, must be at least <see cref="SerializedSize"/> bytes.</param>
    /// <exception cref="ArgumentException">Thrown when destination is too small.</exception>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < SerializedSize)
            throw new ArgumentException($"Destination must be at least {SerializedSize} bytes.", nameof(destination));

        // Write GUID as raw bytes (16 bytes)
        bool written = VdeId.TryWriteBytes(destination[..16]);
        if (!written)
            throw new InvalidOperationException("Failed to write GUID bytes.");

        // Write inode number as little-endian Int64
        BinaryPrimitives.WriteInt64LittleEndian(destination[16..24], InodeNumber);

        // Write level byte
        destination[24] = (byte)Level;

        // Zero padding bytes [25..31]
        destination[25..SerializedSize].Clear();
    }

    /// <summary>
    /// Deserializes a ShardAddress from a 32-byte span using BinaryPrimitives.
    /// </summary>
    /// <param name="source">Source span, must be at least <see cref="SerializedSize"/> bytes.</param>
    /// <returns>The deserialized ShardAddress.</returns>
    /// <exception cref="ArgumentException">Thrown when source is too small.</exception>
    public static ShardAddress ReadFrom(ReadOnlySpan<byte> source)
    {
        if (source.Length < SerializedSize)
            throw new ArgumentException($"Source must be at least {SerializedSize} bytes.", nameof(source));

        var vdeId = new Guid(source[..16]);
        long inodeNumber = BinaryPrimitives.ReadInt64LittleEndian(source[16..24]);
        var level = (ShardLevel)source[24];

        return new ShardAddress(vdeId, inodeNumber, level);
    }
}
