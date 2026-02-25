using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace DataWarehouse.SDK.Contracts.Ecosystem;

/// <summary>
/// Proto versioning and backward compatibility strategy for the DataWarehouse
/// gRPC service definitions. Ensures clients using older proto versions can
/// still communicate with newer server versions via field migration.
/// </summary>
/// <remarks>
/// <para>
/// Version compatibility follows semantic versioning rules:
/// <list type="bullet">
///   <item><description>Major version mismatch = incompatible (e.g., v1 vs v2)</description></item>
///   <item><description>Minor version mismatch = compatible with potential feature gaps</description></item>
///   <item><description>Patch version mismatch = fully compatible</description></item>
/// </list>
/// </para>
/// <para>
/// Deprecated fields are tracked per-service/per-method to enable progressive
/// API evolution without breaking existing clients.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Ecosystem proto definitions (ECOS-12)")]
public sealed class ProtoVersioningStrategy
{
    private static readonly Version CurrentVersionParsed = new(1, 0, 0);

    /// <summary>
    /// Deprecated field registry keyed by "{service}.{method}" with values being
    /// the set of deprecated field numbers for that method's request message.
    /// </summary>
    private static readonly FrozenDictionary<string, int[]> DeprecatedFieldRegistry =
        new Dictionary<string, int[]>
        {
            // No deprecated fields in v1.0.0 — registry prepared for future evolution.
            // Example: ["StorageService.Store"] = new[] { 7 } when field 7 is deprecated.
        }.ToFrozenDictionary();

    /// <summary>
    /// Gets the current proto API version.
    /// </summary>
    public string CurrentVersion => "1.0.0";

    /// <summary>
    /// Checks whether a client version is compatible with the current server version.
    /// Compatibility requires matching major version numbers.
    /// </summary>
    /// <param name="clientVersion">The client's proto API version string (e.g., "1.0.0").</param>
    /// <returns><c>true</c> if the client can communicate with this server; otherwise <c>false</c>.</returns>
    public bool IsCompatible(string clientVersion)
    {
        if (string.IsNullOrWhiteSpace(clientVersion))
            return false;

        if (!Version.TryParse(clientVersion, out var parsed))
            return false;

        // Major version must match for compatibility
        return parsed.Major == CurrentVersionParsed.Major;
    }

    /// <summary>
    /// Returns the set of deprecated field numbers for a given service method.
    /// Clients should stop sending these fields; servers will ignore them.
    /// </summary>
    /// <param name="service">Service name (e.g., "StorageService").</param>
    /// <param name="method">Method name (e.g., "Store").</param>
    /// <returns>Array of deprecated proto field numbers, empty if none.</returns>
    public int[] GetDeprecatedFields(string service, string method)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(method);

        var key = $"{service}.{method}";
        return DeprecatedFieldRegistry.TryGetValue(key, out var fields)
            ? fields
            : Array.Empty<int>();
    }

    /// <summary>
    /// Migrates a request payload from an older client version to the current version.
    /// This enables forward migration of old requests by filling in default values
    /// for new fields and removing deprecated fields.
    /// </summary>
    /// <param name="service">Service name (e.g., "StorageService").</param>
    /// <param name="method">Method name (e.g., "Store").</param>
    /// <param name="oldPayload">The raw request payload from the older client.</param>
    /// <param name="fromVersion">The client's proto API version string.</param>
    /// <returns>
    /// The migrated payload bytes. If no migration is needed (same version),
    /// returns the original payload unchanged.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="fromVersion"/> is not a valid version string.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the client version is incompatible (different major version).
    /// </exception>
    public ReadOnlyMemory<byte> MigrateRequest(
        string service,
        string method,
        ReadOnlyMemory<byte> oldPayload,
        string fromVersion)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(method);

        if (!Version.TryParse(fromVersion, out var clientVersion))
            throw new ArgumentException($"Invalid version string: '{fromVersion}'", nameof(fromVersion));

        if (clientVersion.Major != CurrentVersionParsed.Major)
            throw new InvalidOperationException(
                $"Cannot migrate from v{clientVersion.Major} to v{CurrentVersionParsed.Major}: " +
                "major version mismatch requires a full protocol upgrade.");

        // Same version — no migration needed
        if (clientVersion.Minor == CurrentVersionParsed.Minor &&
            clientVersion.Build == CurrentVersionParsed.Build)
        {
            return oldPayload;
        }

        // For minor/patch version differences within the same major version,
        // the payload is wire-compatible. Proto3 unknown fields are preserved,
        // missing fields get default values. No transformation needed at the
        // binary level — the C# mirror types handle defaults on deserialization.
        //
        // Future migrations (e.g., v1.1.0 -> v1.2.0) would add transformation
        // logic here for renamed or restructured fields.
        return oldPayload;
    }

    /// <summary>
    /// Gets the minimum supported client version for a given service.
    /// </summary>
    /// <param name="service">Service name.</param>
    /// <returns>The minimum supported version string.</returns>
    public string GetMinimumClientVersion(string service)
    {
        // All services currently support from v1.0.0
        _ = service;
        return "1.0.0";
    }

    /// <summary>
    /// Checks whether a specific proto field is deprecated for a given method.
    /// </summary>
    /// <param name="service">Service name.</param>
    /// <param name="method">Method name.</param>
    /// <param name="fieldNumber">Proto field number to check.</param>
    /// <returns><c>true</c> if the field is deprecated; otherwise <c>false</c>.</returns>
    public bool IsFieldDeprecated(string service, string method, int fieldNumber)
    {
        var deprecated = GetDeprecatedFields(service, method);
        return Array.IndexOf(deprecated, fieldNumber) >= 0;
    }
}
