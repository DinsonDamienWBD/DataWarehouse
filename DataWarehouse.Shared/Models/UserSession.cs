// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace DataWarehouse.Shared.Models;

/// <summary>
/// Represents an authenticated user session with context for AI operations.
/// </summary>
public sealed class UserSession
{
    /// <summary>
    /// Unique session identifier.
    /// </summary>
    public string SessionId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Unique user identifier.
    /// </summary>
    public required string UserId { get; init; }

    /// <summary>
    /// User display name.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// User email address.
    /// </summary>
    public string? Email { get; init; }

    /// <summary>
    /// Organization the user belongs to (for Organization-scoped credentials).
    /// </summary>
    public string? OrganizationId { get; init; }

    /// <summary>
    /// Organization display name.
    /// </summary>
    public string? OrganizationName { get; init; }

    /// <summary>
    /// Tenant identifier for enterprise deployments.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// User roles for authorization.
    /// </summary>
    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Timestamp when the session was created.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Timestamp when the session expires.
    /// </summary>
    public DateTime? ExpiresAt { get; init; }

    /// <summary>
    /// Last activity timestamp for session timeout tracking.
    /// </summary>
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Authentication method used (e.g., "password", "sso", "oauth", "apikey").
    /// </summary>
    public string AuthenticationMethod { get; init; } = "unknown";

    /// <summary>
    /// SSO provider name if authenticated via SSO (e.g., "azure-ad", "okta", "google").
    /// </summary>
    public string? SsoProvider { get; init; }

    /// <summary>
    /// IP address of the client.
    /// </summary>
    public string? ClientIpAddress { get; init; }

    /// <summary>
    /// User agent string from the client.
    /// </summary>
    public string? UserAgent { get; init; }

    /// <summary>
    /// Additional claims or attributes from the identity provider.
    /// </summary>
    public IReadOnlyDictionary<string, object> Claims { get; init; } = new Dictionary<string, object>();

    /// <summary>
    /// Whether the session is currently valid.
    /// </summary>
    public bool IsValid => ExpiresAt == null || ExpiresAt > DateTime.UtcNow;

    /// <summary>
    /// Whether the user has a specific role.
    /// </summary>
    /// <param name="role">Role to check.</param>
    /// <returns>True if user has the role.</returns>
    public bool HasRole(string role) => Roles.Contains(role, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the user is in an organization.
    /// </summary>
    public bool IsOrganizationMember => !string.IsNullOrEmpty(OrganizationId);

    /// <summary>
    /// Whether the user is an administrator.
    /// </summary>
    public bool IsAdmin => HasRole("admin") || HasRole("administrator");

    /// <summary>
    /// Updates the last activity timestamp.
    /// </summary>
    public void Touch() => LastActivityAt = DateTime.UtcNow;

    /// <summary>
    /// Creates an anonymous session for unauthenticated access.
    /// </summary>
    /// <returns>Anonymous user session.</returns>
    public static UserSession CreateAnonymous() => new()
    {
        UserId = "anonymous",
        DisplayName = "Anonymous User",
        AuthenticationMethod = "anonymous",
        Roles = new[] { "anonymous" },
        ExpiresAt = DateTime.UtcNow.AddHours(1)
    };
}
