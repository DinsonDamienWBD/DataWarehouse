// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json.Serialization;

namespace DataWarehouse.Shared.Models;

/// <summary>
/// Scope at which a credential applies.
/// </summary>
public enum CredentialScope
{
    /// <summary>Personal credential - belongs to a single user.</summary>
    Personal = 0,

    /// <summary>Organization credential - shared across organization members.</summary>
    Organization = 1,

    /// <summary>Instance credential - system-wide default.</summary>
    Instance = 2
}

/// <summary>
/// OAuth token information for SSO-based authentication.
/// </summary>
public sealed class OAuthToken
{
    /// <summary>
    /// The access token.
    /// </summary>
    public required string AccessToken { get; init; }

    /// <summary>
    /// Token type (typically "Bearer").
    /// </summary>
    public string TokenType { get; init; } = "Bearer";

    /// <summary>
    /// Refresh token for obtaining new access tokens.
    /// </summary>
    public string? RefreshToken { get; init; }

    /// <summary>
    /// When the access token expires.
    /// </summary>
    public DateTime? ExpiresAt { get; init; }

    /// <summary>
    /// OAuth scopes granted.
    /// </summary>
    public IReadOnlyList<string> Scopes { get; init; } = Array.Empty<string>();

    /// <summary>
    /// ID token for OpenID Connect.
    /// </summary>
    public string? IdToken { get; init; }

    /// <summary>
    /// Whether the access token has expired.
    /// </summary>
    [JsonIgnore]
    public bool IsExpired => ExpiresAt.HasValue && ExpiresAt.Value <= DateTime.UtcNow;

    /// <summary>
    /// Whether the token can be refreshed.
    /// </summary>
    [JsonIgnore]
    public bool CanRefresh => !string.IsNullOrEmpty(RefreshToken);
}

/// <summary>
/// Represents a stored AI provider credential for a user.
/// Credentials can be API keys (BYOK) or OAuth tokens (SSO).
/// </summary>
public sealed class AICredential
{
    /// <summary>
    /// Unique credential identifier.
    /// </summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// AI provider identifier (e.g., "openai", "anthropic", "azure").
    /// </summary>
    public required string ProviderId { get; init; }

    /// <summary>
    /// Scope of this credential.
    /// </summary>
    public CredentialScope Scope { get; init; } = CredentialScope.Personal;

    /// <summary>
    /// User-friendly name for this credential.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// API key for BYOK (Bring Your Own Key) authentication.
    /// This should be stored encrypted - never log or expose this value.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// OAuth token for SSO authentication.
    /// </summary>
    public OAuthToken? OAuthToken { get; set; }

    /// <summary>
    /// Tenant identifier for enterprise/multi-tenant providers (e.g., Azure).
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// Organization identifier for providers that support org-level access.
    /// </summary>
    public string? OrganizationId { get; init; }

    /// <summary>
    /// Resource endpoint for providers with custom endpoints (e.g., Azure OpenAI).
    /// </summary>
    public string? Endpoint { get; init; }

    /// <summary>
    /// API version for providers that require version specification.
    /// </summary>
    public string? ApiVersion { get; init; }

    /// <summary>
    /// Region for providers with regional endpoints.
    /// </summary>
    public string? Region { get; init; }

    /// <summary>
    /// When this credential expires (null for no expiration).
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// When this credential was created.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// When this credential was last used.
    /// </summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>
    /// When this credential was last rotated/updated.
    /// </summary>
    public DateTime? LastRotatedAt { get; set; }

    /// <summary>
    /// Whether this credential is enabled.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Priority for fallback ordering (lower = higher priority).
    /// </summary>
    public int Priority { get; init; } = 100;

    /// <summary>
    /// Additional provider-specific settings.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } = new Dictionary<string, object>();

    /// <summary>
    /// Whether this credential has expired.
    /// </summary>
    [JsonIgnore]
    public bool IsExpired => ExpiresAt.HasValue && ExpiresAt.Value <= DateTime.UtcNow;

    /// <summary>
    /// Whether this credential is currently usable.
    /// </summary>
    [JsonIgnore]
    public bool IsUsable => IsEnabled && !IsExpired && (HasApiKey || HasValidOAuthToken);

    /// <summary>
    /// Whether this credential has an API key.
    /// </summary>
    [JsonIgnore]
    public bool HasApiKey => !string.IsNullOrEmpty(ApiKey);

    /// <summary>
    /// Whether this credential has a valid OAuth token.
    /// </summary>
    [JsonIgnore]
    public bool HasValidOAuthToken => OAuthToken != null && !OAuthToken.IsExpired;

    /// <summary>
    /// Whether this credential needs OAuth token refresh.
    /// </summary>
    [JsonIgnore]
    public bool NeedsTokenRefresh => OAuthToken != null && OAuthToken.IsExpired && OAuthToken.CanRefresh;

    /// <summary>
    /// Gets a masked version of the API key for display purposes.
    /// Never logs or exposes the actual key.
    /// </summary>
    /// <returns>Masked API key or null.</returns>
    public string? GetMaskedApiKey()
    {
        if (string.IsNullOrEmpty(ApiKey)) return null;
        if (ApiKey.Length <= 8) return "****";
        return $"{ApiKey[..4]}...{ApiKey[^4..]}";
    }

    /// <summary>
    /// Records usage of this credential.
    /// </summary>
    public void RecordUsage() => LastUsedAt = DateTime.UtcNow;

    /// <summary>
    /// Creates a copy of this credential without sensitive data.
    /// Safe for logging and API responses.
    /// </summary>
    /// <returns>Redacted credential copy.</returns>
    public AICredential ToRedacted() => new()
    {
        Id = Id,
        ProviderId = ProviderId,
        Scope = Scope,
        DisplayName = DisplayName,
        ApiKey = GetMaskedApiKey(),
        OAuthToken = OAuthToken != null ? new OAuthToken
        {
            AccessToken = "****",
            TokenType = OAuthToken.TokenType,
            RefreshToken = OAuthToken.RefreshToken != null ? "****" : null,
            ExpiresAt = OAuthToken.ExpiresAt,
            Scopes = OAuthToken.Scopes
        } : null,
        TenantId = TenantId,
        OrganizationId = OrganizationId,
        Endpoint = Endpoint,
        ApiVersion = ApiVersion,
        Region = Region,
        ExpiresAt = ExpiresAt,
        CreatedAt = CreatedAt,
        LastUsedAt = LastUsedAt,
        LastRotatedAt = LastRotatedAt,
        IsEnabled = IsEnabled,
        Priority = Priority,
        Metadata = Metadata
    };
}

/// <summary>
/// Configuration for registering an AI provider with user-specific settings.
/// </summary>
public sealed class AIProviderConfig
{
    /// <summary>
    /// Provider identifier (e.g., "openai", "anthropic").
    /// </summary>
    public required string ProviderId { get; init; }

    /// <summary>
    /// Human-readable provider name.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Provider type for factory resolution.
    /// </summary>
    public string? ProviderType { get; init; }

    /// <summary>
    /// Default model to use.
    /// </summary>
    public string? DefaultModel { get; init; }

    /// <summary>
    /// Whether this provider is enabled.
    /// </summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>
    /// Priority for provider selection (lower = higher priority).
    /// </summary>
    public int Priority { get; init; } = 100;

    /// <summary>
    /// Whether to use this provider in automatic failover.
    /// </summary>
    public bool UseInFailover { get; init; } = true;

    /// <summary>
    /// Custom endpoint URL (for self-hosted or regional endpoints).
    /// </summary>
    public string? Endpoint { get; init; }

    /// <summary>
    /// Request timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 60;

    /// <summary>
    /// Maximum retries on transient failures.
    /// </summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// Additional provider-specific options.
    /// </summary>
    public IReadOnlyDictionary<string, object> Options { get; init; } = new Dictionary<string, object>();
}

/// <summary>
/// Result of a credential access audit.
/// </summary>
public sealed record CredentialAccessAudit
{
    /// <summary>Audit record identifier.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Credential that was accessed.</summary>
    public required string CredentialId { get; init; }

    /// <summary>User who accessed the credential.</summary>
    public required string UserId { get; init; }

    /// <summary>Type of access (read, use, update, delete).</summary>
    public required string AccessType { get; init; }

    /// <summary>Provider the credential was used with.</summary>
    public string? ProviderId { get; init; }

    /// <summary>Whether the access was successful.</summary>
    public bool Success { get; init; }

    /// <summary>Error message if access failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>IP address of the requester.</summary>
    public string? ClientIpAddress { get; init; }

    /// <summary>User agent of the requester.</summary>
    public string? UserAgent { get; init; }

    /// <summary>Timestamp of the access.</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>Additional context about the access.</summary>
    public IReadOnlyDictionary<string, object>? Context { get; init; }
}
