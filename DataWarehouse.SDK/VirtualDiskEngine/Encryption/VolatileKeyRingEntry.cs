using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Encryption;

/// <summary>
/// A single entry in the volatile key ring, holding an ephemeral AES-256 key
/// with TTL-based expiry semantics. Key material is RAM-only and MUST NOT be
/// persisted to any on-disk structure.
/// </summary>
/// <remarks>
/// The <see cref="KeyMaterial"/> array contains the raw AES-256 key bytes (32 bytes).
/// When the key is no longer needed, callers should zero the array via
/// <see cref="System.Security.Cryptography.CryptographicOperations.ZeroMemory"/> before
/// releasing the entry, though the volatile ring handles this automatically on drop.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87-20: Volatile key ring entry for EKEY module (VOPT-33)")]
public readonly record struct VolatileKeyRingEntry
{
    /// <summary>Unique identifier for this ephemeral key (16 bytes / 128-bit GUID).</summary>
    public Guid EphemeralKeyId { get; init; }

    /// <summary>
    /// The raw AES-256 key material (32 bytes). This array is RAM-only;
    /// it MUST NOT be serialized to disk, written to KMS, or logged.
    /// </summary>
    public byte[] KeyMaterial { get; init; }

    /// <summary>
    /// The MVCC epoch at which this key expires. When <c>currentEpoch &gt;= TtlEpoch</c>
    /// the key is considered expired and eligible for reaping.
    /// </summary>
    public ulong TtlEpoch { get; init; }

    /// <summary>Position of this entry within the volatile ring's slot array.</summary>
    public uint SlotIndex { get; init; }

    /// <summary>UTC timestamp at which this key was allocated.</summary>
    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="currentEpoch"/> is greater than or
    /// equal to <see cref="TtlEpoch"/>, indicating the key has expired.
    /// </summary>
    /// <param name="currentEpoch">The current MVCC epoch to evaluate against.</param>
    public bool IsExpired(ulong currentEpoch) => currentEpoch >= TtlEpoch;
}
