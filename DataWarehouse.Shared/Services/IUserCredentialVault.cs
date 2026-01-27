// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.Shared.Models;

namespace DataWarehouse.Shared.Services;

/// <summary>
/// Provides secure, encrypted storage for user-bound AI credentials.
/// Supports BYOK (Bring Your Own Key) and SSO-based authentication.
/// </summary>
/// <remarks>
/// Security requirements:
/// - All credentials must be encrypted at rest with user-specific keys
/// - API keys must never be logged or exposed in error messages
/// - All credential access must be audited
/// - Support for credential rotation without service interruption
/// </remarks>
public interface IUserCredentialVault
{
    /// <summary>
    /// Stores a credential for a user.
    /// </summary>
    /// <param name="userId">User identifier.</param>
    /// <param name="providerId">AI provider identifier.</param>
    /// <param name="credential">Credential to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task completing when credential is stored.</returns>
    /// <exception cref="ArgumentNullException">If userId, providerId, or credential is null.</exception>
    /// <exception cref="CredentialVaultException">If storage fails.</exception>
    Task StoreCredentialAsync(
        string userId,
        string providerId,
        AICredential credential,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a credential for a user and provider.
    /// </summary>
    /// <param name="userId">User identifier.</param>
    /// <param name="providerId">AI provider identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Credential if found, null otherwise.</returns>
    /// <exception cref="ArgumentNullException">If userId or providerId is null.</exception>
    Task<AICredential?> GetCredentialAsync(
        string userId,
        string providerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a credential for a user and provider.
    /// </summary>
    /// <param name="userId">User identifier.</param>
    /// <param name="providerId">AI provider identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if credential was deleted, false if not found.</returns>
    /// <exception cref="ArgumentNullException">If userId or providerId is null.</exception>
    Task<bool> DeleteCredentialAsync(
        string userId,
        string providerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all provider IDs that have credentials stored for a user.
    /// </summary>
    /// <param name="userId">User identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Enumerable of provider identifiers.</returns>
    /// <exception cref="ArgumentNullException">If userId is null.</exception>
    Task<IEnumerable<string>> ListProvidersAsync(
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all credentials for a user (with sensitive data redacted).
    /// </summary>
    /// <param name="userId">User identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Enumerable of redacted credentials.</returns>
    /// <exception cref="ArgumentNullException">If userId is null.</exception>
    Task<IEnumerable<AICredential>> ListCredentialsAsync(
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rotates a credential by updating it with a new key/token.
    /// </summary>
    /// <param name="userId">User identifier.</param>
    /// <param name="providerId">AI provider identifier.</param>
    /// <param name="newCredential">New credential to replace the existing one.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task completing when rotation is complete.</returns>
    /// <exception cref="ArgumentNullException">If any parameter is null.</exception>
    /// <exception cref="CredentialNotFoundException">If no existing credential found.</exception>
    Task RotateCredentialAsync(
        string userId,
        string providerId,
        AICredential newCredential,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Refreshes an OAuth token for a credential.
    /// </summary>
    /// <param name="userId">User identifier.</param>
    /// <param name="providerId">AI provider identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Updated credential with refreshed token.</returns>
    /// <exception cref="CredentialNotFoundException">If no credential found.</exception>
    /// <exception cref="TokenRefreshException">If token refresh fails.</exception>
    Task<AICredential> RefreshOAuthTokenAsync(
        string userId,
        string providerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates that a credential is usable.
    /// </summary>
    /// <param name="userId">User identifier.</param>
    /// <param name="providerId">AI provider identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Validation result with any issues.</returns>
    Task<CredentialValidationResult> ValidateCredentialAsync(
        string userId,
        string providerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the effective credential for a user and provider, considering scope inheritance.
    /// Order: Personal -> Organization -> Instance
    /// </summary>
    /// <param name="userId">User identifier.</param>
    /// <param name="providerId">AI provider identifier.</param>
    /// <param name="organizationId">Optional organization identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Effective credential or null if none found at any scope.</returns>
    Task<AICredential?> GetEffectiveCredentialAsync(
        string userId,
        string providerId,
        string? organizationId = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of credential validation.
/// </summary>
public sealed record CredentialValidationResult
{
    /// <summary>Whether the credential is valid.</summary>
    public bool IsValid { get; init; }

    /// <summary>Validation errors if not valid.</summary>
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    /// <summary>Warnings that don't prevent usage.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>Whether the credential needs token refresh.</summary>
    public bool NeedsRefresh { get; init; }

    /// <summary>Whether the credential is expiring soon (within 24 hours).</summary>
    public bool ExpiringSoon { get; init; }

    /// <summary>Creates a successful validation result.</summary>
    public static CredentialValidationResult Success(IReadOnlyList<string>? warnings = null) => new()
    {
        IsValid = true,
        Warnings = warnings ?? Array.Empty<string>()
    };

    /// <summary>Creates a failed validation result.</summary>
    public static CredentialValidationResult Failure(params string[] errors) => new()
    {
        IsValid = false,
        Errors = errors
    };
}

/// <summary>
/// Exception thrown when a credential is not found.
/// </summary>
public class CredentialNotFoundException : Exception
{
    /// <summary>User identifier.</summary>
    public string UserId { get; }

    /// <summary>Provider identifier.</summary>
    public string ProviderId { get; }

    /// <summary>
    /// Creates a new credential not found exception.
    /// </summary>
    public CredentialNotFoundException(string userId, string providerId)
        : base($"Credential not found for user '{userId}' and provider '{providerId}'")
    {
        UserId = userId;
        ProviderId = providerId;
    }
}

/// <summary>
/// Exception thrown when credential vault operations fail.
/// </summary>
public class CredentialVaultException : Exception
{
    /// <summary>
    /// Creates a new credential vault exception.
    /// </summary>
    public CredentialVaultException(string message) : base(message) { }

    /// <summary>
    /// Creates a new credential vault exception with inner exception.
    /// </summary>
    public CredentialVaultException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>
/// Exception thrown when OAuth token refresh fails.
/// </summary>
public class TokenRefreshException : Exception
{
    /// <summary>Provider that failed refresh.</summary>
    public string ProviderId { get; }

    /// <summary>
    /// Creates a new token refresh exception.
    /// </summary>
    public TokenRefreshException(string providerId, string message)
        : base($"Failed to refresh OAuth token for provider '{providerId}': {message}")
    {
        ProviderId = providerId;
    }

    /// <summary>
    /// Creates a new token refresh exception with inner exception.
    /// </summary>
    public TokenRefreshException(string providerId, string message, Exception innerException)
        : base($"Failed to refresh OAuth token for provider '{providerId}': {message}", innerException)
    {
        ProviderId = providerId;
    }
}
