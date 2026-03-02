using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Encryption;

/// <summary>
/// Flags for an ephemeral key module entry, controlling key lifecycle and storage behaviour.
/// </summary>
[Flags]
[SdkCompatibility("6.0.0", Notes = "Phase 87-20: Ephemeral key module flags (VOPT-33)")]
public enum EphemeralKeyFlags : uint
{
    /// <summary>No special flags. Key is RAM-only with standard TTL reaping.</summary>
    None = 0,

    /// <summary>
    /// Key is sealed by a TPM into volatile PCR state. Survives platform sleep,
    /// is destroyed on cold boot or PCR mismatch.
    /// </summary>
    TpmSealed = 1,

    /// <summary>
    /// Key is automatically reaped when its TTL epoch expires.
    /// When clear, the key persists in the ring until explicitly dropped.
    /// </summary>
    AutoReap = 2,

    /// <summary>
    /// Key is forcibly expired and dropped from the volatile ring when the VDE is unmounted.
    /// Use this for sensitive per-session keys that must not survive unmount.
    /// </summary>
    ForceExpireOnUnmount = 4,
}

/// <summary>
/// Per-inode ephemeral key module entry stored in the Module Overflow Block.
/// Provides a 32-byte serializable reference to a volatile key ring slot, enabling
/// O(1) cryptographic shredding by dropping the referenced key.
/// </summary>
/// <remarks>
/// <para>
/// <strong>On-disk layout (32 bytes, little-endian):</strong>
/// <code>
///   [0..16)   EphemeralKeyID   — 16-byte GUID identifying the key in the volatile ring
///   [16..24)  TTL_Epoch        — 8-byte ulong MVCC epoch at which the key expires
///   [24..28)  KeyRingSlot      — 4-byte uint slot index in the volatile ring
///   [28..32)  Flags            — 4-byte EphemeralKeyFlags
/// </code>
/// </para>
/// <para>
/// <strong>Security guarantee:</strong> The key material itself is NEVER written to a VDE block.
/// Only the <see cref="EphemeralKeyId"/> (a lookup handle) and metadata are persisted.
/// Dropping the key from the volatile ring renders the associated ciphertext irrecoverable
/// without any disk I/O.
/// </para>
/// <para>
/// <strong>Module position:</strong> Bit 20 in the <c>ModuleManifest</c> (EKEY / VOPT-33).
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87-20: EKEY inode module (VOPT-33, Module bit 20)")]
public readonly struct EphemeralKeyModule
{
    /// <summary>Serialized size of this module entry in bytes (32 bytes).</summary>
    public const int SerializedSize = 32;

    /// <summary>
    /// Bit position in the <c>ModuleManifest</c> for the EKEY module.
    /// The corresponding manifest bit is <c>1u &lt;&lt; ModuleBitPosition</c> = 0x00100000.
    /// </summary>
    public const byte ModuleBitPosition = 20;

    /// <summary>16-byte GUID used to look up the key in the <see cref="IVolatileKeyRing"/>.</summary>
    public Guid EphemeralKeyId { get; init; }

    /// <summary>
    /// MVCC epoch at which the key expires. When the current epoch reaches or exceeds
    /// this value, the key is eligible for automatic reaping via
    /// <see cref="IVolatileKeyRing.ReapExpiredAsync"/>.
    /// </summary>
    public ulong TtlEpoch { get; init; }

    /// <summary>
    /// The slot index within the <see cref="IVolatileKeyRing"/> where this key resides.
    /// Used as a fast-path hint; implementations MUST fall back to
    /// <see cref="IVolatileKeyRing.GetKeyAsync"/> by <see cref="EphemeralKeyId"/> if the slot
    /// does not contain the expected key.
    /// </summary>
    public uint KeyRingSlot { get; init; }

    /// <summary>Flags controlling key lifecycle and storage backend.</summary>
    public EphemeralKeyFlags Flags { get; init; }

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="currentEpoch"/> is greater than or
    /// equal to <see cref="TtlEpoch"/>, indicating the key has expired.
    /// </summary>
    /// <param name="currentEpoch">The current MVCC epoch to evaluate against.</param>
    public bool IsExpired(ulong currentEpoch) => currentEpoch >= TtlEpoch;

    /// <summary>
    /// Serializes the module entry into a 32-byte little-endian buffer.
    /// </summary>
    /// <param name="module">The module entry to serialize.</param>
    /// <param name="buffer">
    /// Output span of exactly <see cref="SerializedSize"/> bytes.
    /// Layout: [EphemeralKeyID:16][TTL_Epoch:8][KeyRingSlot:4][Flags:4].
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="buffer"/> is shorter than <see cref="SerializedSize"/> bytes.
    /// </exception>
    public static void Serialize(in EphemeralKeyModule module, Span<byte> buffer)
    {
        if (buffer.Length < SerializedSize)
            throw new ArgumentException(
                $"Buffer must be at least {SerializedSize} bytes.", nameof(buffer));

        // [0..16) EphemeralKeyID as raw GUID bytes
        if (!module.EphemeralKeyId.TryWriteBytes(buffer[..16]))
            throw new InvalidOperationException("Failed to write EphemeralKeyId GUID bytes.");

        // [16..24) TTL_Epoch as little-endian ulong
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[16..24], module.TtlEpoch);

        // [24..28) KeyRingSlot as little-endian uint
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[24..28], module.KeyRingSlot);

        // [28..32) Flags as little-endian uint
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[28..32], (uint)module.Flags);
    }

    /// <summary>
    /// Deserializes a module entry from a 32-byte little-endian buffer.
    /// </summary>
    /// <param name="buffer">
    /// Input span of exactly <see cref="SerializedSize"/> bytes.
    /// Layout: [EphemeralKeyID:16][TTL_Epoch:8][KeyRingSlot:4][Flags:4].
    /// </param>
    /// <returns>The deserialized <see cref="EphemeralKeyModule"/>.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="buffer"/> is shorter than <see cref="SerializedSize"/> bytes.
    /// </exception>
    public static EphemeralKeyModule Deserialize(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < SerializedSize)
            throw new ArgumentException(
                $"Buffer must be at least {SerializedSize} bytes.", nameof(buffer));

        var keyId = new Guid(buffer[..16]);
        ulong ttlEpoch = BinaryPrimitives.ReadUInt64LittleEndian(buffer[16..24]);
        uint keyRingSlot = BinaryPrimitives.ReadUInt32LittleEndian(buffer[24..28]);
        var flags = (EphemeralKeyFlags)BinaryPrimitives.ReadUInt32LittleEndian(buffer[28..32]);

        return new EphemeralKeyModule
        {
            EphemeralKeyId = keyId,
            TtlEpoch = ttlEpoch,
            KeyRingSlot = keyRingSlot,
            Flags = flags,
        };
    }
}
