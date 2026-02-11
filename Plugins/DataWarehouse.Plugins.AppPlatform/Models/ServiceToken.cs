namespace DataWarehouse.Plugins.AppPlatform.Models;

/// <summary>
/// Represents a service token issued to a registered application.
/// The raw key is never stored; only the SHA-256 hash is persisted.
/// Tokens are scoped to specific services and have expiration dates.
/// </summary>
public sealed record ServiceToken
{
    /// <summary>
    /// Unique identifier for this token, generated as a GUID without hyphens.
    /// </summary>
    public required string TokenId { get; init; }

    /// <summary>
    /// Identifier of the application this token belongs to.
    /// </summary>
    public required string AppId { get; init; }

    /// <summary>
    /// SHA-256 hash of the raw token key, Base64-encoded.
    /// The raw key is returned exactly once at creation time and is never stored.
    /// </summary>
    public required string TokenHash { get; init; }

    /// <summary>
    /// UTC timestamp when the token was created.
    /// </summary>
    public required DateTime CreatedAt { get; init; }

    /// <summary>
    /// UTC timestamp when the token expires and can no longer be used for authentication.
    /// </summary>
    public required DateTime ExpiresAt { get; init; }

    /// <summary>
    /// Array of service scopes this token is authorized to access.
    /// </summary>
    public required string[] AllowedScopes { get; init; }

    /// <summary>
    /// Whether this token has been revoked. Revoked tokens are invalid regardless of expiration.
    /// </summary>
    public bool IsRevoked { get; init; }

    /// <summary>
    /// UTC timestamp when the token was revoked, or null if not revoked.
    /// </summary>
    public DateTime? RevokedAt { get; init; }

    /// <summary>
    /// Reason for revocation, or null if the token has not been revoked.
    /// </summary>
    public string? RevocationReason { get; init; }
}

/// <summary>
/// Result of validating a service token against the platform.
/// Contains validity status, associated application details, and failure information.
/// </summary>
public sealed record TokenValidationResult
{
    /// <summary>
    /// Whether the token is valid (exists, not revoked, not expired, hash matches).
    /// </summary>
    public bool IsValid { get; init; }

    /// <summary>
    /// Identifier of the application the token belongs to, or null if validation failed.
    /// </summary>
    public string? AppId { get; init; }

    /// <summary>
    /// Identifier of the validated token, or null if validation failed.
    /// </summary>
    public string? TokenId { get; init; }

    /// <summary>
    /// Service scopes the token is authorized for, empty if validation failed.
    /// </summary>
    public string[] AllowedScopes { get; init; } = [];

    /// <summary>
    /// Reason the validation failed, or null if the token is valid.
    /// </summary>
    public string? FailureReason { get; init; }
}
