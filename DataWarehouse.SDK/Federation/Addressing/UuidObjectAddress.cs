using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Storage;
using System;

namespace DataWarehouse.SDK.Federation.Addressing;

/// <summary>
/// Extension methods for UUID-based storage addressing.
/// </summary>
/// <remarks>
/// <para>
/// This class provides helpers for creating and manipulating <see cref="StorageAddress"/>
/// instances that use UUID-based object keys. UUIDs are stored using the existing
/// <see cref="ObjectKeyAddress"/> variant from Phase 32.
/// </para>
/// <para>
/// <strong>Integration with StorageAddress:</strong> UUID-based addresses use the
/// ObjectKeyAddress variant (not a new StorageAddress type). This ensures backward
/// compatibility with existing storage strategies while providing UUID-specific helpers.
/// </para>
/// <para>
/// <strong>Usage Pattern:</strong>
/// <code>
/// // Generate new UUID-based address
/// var address = UuidObjectAddress.Generate();
///
/// // Create address from existing UUID
/// var identity = new ObjectIdentity(Guid.NewGuid());
/// var address2 = UuidObjectAddress.FromUuid(identity);
///
/// // Extract UUID from address
/// if (UuidObjectAddress.TryGetUuid(address, out var extractedId))
/// {
///     Console.WriteLine($"Object timestamp: {extractedId.Timestamp}");
/// }
/// </code>
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: UUID-aware storage address helpers (FOS-02)")]
public static class UuidObjectAddress
{
    /// <summary>
    /// Creates a StorageAddress from an ObjectIdentity (UUID).
    /// </summary>
    /// <param name="identity">The object identity to convert.</param>
    /// <returns>
    /// A <see cref="StorageAddress"/> using the ObjectKeyAddress variant with the UUID
    /// string representation as the key.
    /// </returns>
    /// <remarks>
    /// The UUID is formatted using standard format (xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx).
    /// This format is round-trippable: the same UUID can be extracted via <see cref="TryGetUuid"/>.
    /// </remarks>
    public static StorageAddress FromUuid(ObjectIdentity identity)
    {
        return StorageAddress.FromObjectKey(identity.ToString());
    }

    /// <summary>
    /// Attempts to extract an ObjectIdentity from a StorageAddress.
    /// </summary>
    /// <param name="address">The storage address to inspect.</param>
    /// <param name="identity">
    /// When this method returns, contains the extracted identity if the address is a
    /// UUID-based ObjectKeyAddress; otherwise <see cref="ObjectIdentity.Empty"/>.
    /// </param>
    /// <returns>
    /// <c>true</c> if the address is an ObjectKeyAddress containing a valid UUID;
    /// otherwise <c>false</c>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This method only succeeds if:
    /// <list type="bullet">
    ///   <item><description>The address is an ObjectKeyAddress (not FilePathAddress, NvmeNamespaceAddress, etc.)</description></item>
    ///   <item><description>The key string is a valid UUID format</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Non-UUID ObjectKeyAddress instances (e.g., S3 keys, custom keys) will return false.
    /// </para>
    /// </remarks>
    public static bool TryGetUuid(StorageAddress address, out ObjectIdentity identity)
    {
        if (address is ObjectKeyAddress keyAddr)
        {
            return ObjectIdentity.TryParse(keyAddr.Key, out identity);
        }

        identity = ObjectIdentity.Empty;
        return false;
    }

    /// <summary>
    /// Checks if a StorageAddress represents a UUID-based object.
    /// </summary>
    /// <param name="address">The storage address to check.</param>
    /// <returns>
    /// <c>true</c> if the address is an ObjectKeyAddress containing a valid UUID;
    /// otherwise <c>false</c>.
    /// </returns>
    public static bool IsUuidAddress(StorageAddress address)
    {
        return TryGetUuid(address, out _);
    }

    /// <summary>
    /// Creates a StorageAddress from a UUID string.
    /// </summary>
    /// <param name="uuid">The UUID string (standard format: xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx).</param>
    /// <returns>A <see cref="StorageAddress"/> using the ObjectKeyAddress variant.</returns>
    /// <exception cref="ArgumentException">Thrown if the UUID string format is invalid.</exception>
    /// <remarks>
    /// This method validates the UUID format before creating the address. If validation fails,
    /// an exception is thrown. For non-throwing behavior, use <see cref="ObjectIdentity.TryParse"/>
    /// followed by <see cref="FromUuid"/>.
    /// </remarks>
    public static StorageAddress FromUuidString(string uuid)
    {
        if (!ObjectIdentity.TryParse(uuid, out var identity))
        {
            throw new ArgumentException($"Invalid UUID format: {uuid}", nameof(uuid));
        }

        return FromUuid(identity);
    }

    /// <summary>
    /// Generates a new UUID-based StorageAddress using UUID v7 format.
    /// </summary>
    /// <param name="provider">
    /// Optional UUID generator. If null, uses a new <see cref="UuidGenerator"/> instance.
    /// </param>
    /// <returns>
    /// A new <see cref="StorageAddress"/> using the ObjectKeyAddress variant with a
    /// freshly generated UUID v7.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This is a convenience method for one-off UUID generation. For repeated generation,
    /// consider creating a shared <see cref="UuidGenerator"/> instance and passing it
    /// to this method to avoid allocating a generator on each call.
    /// </para>
    /// <para>
    /// <strong>Thread Safety:</strong> If no provider is supplied, a new UuidGenerator
    /// is created on each call. UuidGenerator is thread-safe, so concurrent calls are safe.
    /// </para>
    /// </remarks>
    public static StorageAddress Generate(IObjectIdentityProvider? provider = null)
    {
        provider ??= new UuidGenerator();
        var identity = provider.Generate();
        return FromUuid(identity);
    }
}
