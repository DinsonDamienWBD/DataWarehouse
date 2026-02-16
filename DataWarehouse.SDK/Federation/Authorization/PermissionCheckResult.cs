using DataWarehouse.SDK.Contracts;
using System;

namespace DataWarehouse.SDK.Federation.Authorization;

/// <summary>
/// Represents the result of a permission check operation.
/// </summary>
/// <remarks>
/// <para>
/// PermissionCheckResult models the outcome of an access control check, including whether
/// permission was granted, the reason for the decision, and cache metadata if the result
/// came from a cached entry.
/// </para>
/// <para>
/// Cache-aware results include <see cref="FromCache"/> flag and <see cref="CacheAge"/> to
/// support observability and debugging of permission caching behavior.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Permission check result model (FOS-03)")]
public sealed record PermissionCheckResult
{
    /// <summary>
    /// Gets whether permission was granted.
    /// </summary>
    /// <remarks>
    /// True if the user/caller has permission to perform the requested operation on the
    /// specified resource. False otherwise.
    /// </remarks>
    public required bool Granted { get; init; }

    /// <summary>
    /// Gets the reason for the permission decision.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Human-readable explanation for why permission was granted or denied. Examples:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>"Access granted"</description></item>
    ///   <item><description>"Cached: Access granted"</description></item>
    ///   <item><description>"Access denied: user not in required group"</description></item>
    ///   <item><description>"Permission check failed: message bus timeout"</description></item>
    /// </list>
    /// </remarks>
    public required string Reason { get; init; }

    /// <summary>
    /// Gets whether this result came from the permission cache.
    /// </summary>
    /// <remarks>
    /// True if the result was retrieved from cache (no message bus round-trip). False if
    /// this was a fresh ACL check via UltimateAccessControl.
    /// </remarks>
    public bool FromCache { get; init; }

    /// <summary>
    /// Gets the UTC timestamp when this permission check was performed.
    /// </summary>
    /// <remarks>
    /// For cached results, this is the timestamp when the cached entry was originally created,
    /// not the timestamp of the cache lookup.
    /// </remarks>
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets the age of the cached result, if applicable.
    /// </summary>
    /// <remarks>
    /// Null if <see cref="FromCache"/> is false. Otherwise, the duration between the original
    /// check and the cache lookup.
    /// </remarks>
    public TimeSpan? CacheAge { get; init; }

    /// <summary>
    /// Creates a permission denied result with the specified reason.
    /// </summary>
    /// <param name="reason">The reason permission was denied.</param>
    /// <returns>A <see cref="PermissionCheckResult"/> with <see cref="Granted"/> = false.</returns>
    public static PermissionCheckResult Denied(string reason) =>
        new() { Granted = false, Reason = reason };

    /// <summary>
    /// Creates a permission granted result with optional reason.
    /// </summary>
    /// <param name="reason">The reason permission was granted (default: "Access granted").</param>
    /// <returns>A <see cref="PermissionCheckResult"/> with <see cref="Granted"/> = true.</returns>
    public static PermissionCheckResult Allowed(string reason = "Access granted") =>
        new() { Granted = true, Reason = reason };
}
