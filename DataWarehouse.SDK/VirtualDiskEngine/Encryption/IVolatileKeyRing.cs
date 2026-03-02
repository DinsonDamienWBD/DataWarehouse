using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Encryption;

/// <summary>
/// Contract for a volatile (RAM-only) key ring that stores ephemeral encryption keys
/// with TTL-based expiry, enabling O(1) cryptographic shredding by key deletion.
/// </summary>
/// <remarks>
/// <para>
/// <strong>RAM-only guarantee:</strong> Implementations MUST NOT persist key material to
/// any on-disk structure, database, KMS backup, or log file. The entire security model
/// depends on keys existing solely in RAM (or TPM-sealed volatile storage). When a key
/// is dropped, the ciphertext it protects becomes permanently irrecoverable.
/// </para>
/// <para>
/// <strong>Permitted backends:</strong>
/// <list type="bullet">
///   <item><description>RAM-only (e.g., <c>ConcurrentDictionary&lt;Guid, VolatileKeyRingEntry&gt;</c>)</description></item>
///   <item><description>TPM-sealed volatile storage via a Tpm2Provider (key survives platform sleep, not power-off)</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Anti-patterns (MUST NOT):</strong>
/// <list type="bullet">
///   <item><description>No KMS backup of ephemeral keys</description></item>
///   <item><description>No disk serialization of key material</description></item>
///   <item><description>No logging of raw key bytes</description></item>
/// </list>
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87-20: Volatile key ring contract for EKEY module (VOPT-33)")]
public interface IVolatileKeyRing
{
    /// <summary>
    /// Generates a new ephemeral AES-256 key, stores it in the volatile ring, and
    /// returns the resulting <see cref="VolatileKeyRingEntry"/>.
    /// </summary>
    /// <param name="ttlEpoch">
    /// The MVCC epoch at which this key expires. When the current epoch reaches or
    /// exceeds this value, the key is eligible for automatic reaping.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="VolatileKeyRingEntry"/> containing the generated key material,
    /// its unique <see cref="VolatileKeyRingEntry.EphemeralKeyId"/>, and the assigned
    /// <see cref="VolatileKeyRingEntry.SlotIndex"/>.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The ring is at capacity (all <see cref="Capacity"/> slots are occupied).
    /// </exception>
    Task<VolatileKeyRingEntry> AllocateKeyAsync(ulong ttlEpoch, CancellationToken ct = default);

    /// <summary>
    /// Retrieves the key entry for the given <paramref name="keyId"/>.
    /// Returns <see langword="null"/> if the key has been dropped or has expired.
    /// </summary>
    /// <param name="keyId">The unique identifier of the ephemeral key to retrieve.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The <see cref="VolatileKeyRingEntry"/> if found and not yet reaped;
    /// <see langword="null"/> if the key has been dropped or does not exist.
    /// </returns>
    Task<VolatileKeyRingEntry?> GetKeyAsync(Guid keyId, CancellationToken ct = default);

    /// <summary>
    /// Performs O(1) cryptographic shredding by removing the specified key from the ring.
    /// Once dropped, the ciphertext protected by this key is permanently irrecoverable.
    /// </summary>
    /// <param name="keyId">The unique identifier of the ephemeral key to drop.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> if the key was found and removed;
    /// <see langword="false"/> if the key was not present (already dropped or never allocated).
    /// </returns>
    Task<bool> DropKeyAsync(Guid keyId, CancellationToken ct = default);

    /// <summary>
    /// Removes all keys whose <see cref="VolatileKeyRingEntry.TtlEpoch"/> is less than or
    /// equal to <paramref name="currentEpoch"/>. Intended for background reaping to reclaim ring slots.
    /// </summary>
    /// <param name="currentEpoch">The current MVCC epoch; all keys with TtlEpoch &lt;= this value are reaped.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of expired keys removed from the ring.</returns>
    Task<int> ReapExpiredAsync(ulong currentEpoch, CancellationToken ct = default);

    /// <summary>Number of live (non-expired, non-dropped) keys currently in the ring.</summary>
    int ActiveKeyCount { get; }

    /// <summary>Maximum number of key slots in this ring.</summary>
    int Capacity { get; }
}
