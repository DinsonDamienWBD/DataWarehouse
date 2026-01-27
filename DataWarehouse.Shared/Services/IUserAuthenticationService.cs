// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.Shared.Models;

namespace DataWarehouse.Shared.Services;

/// <summary>
/// Provides user identity management and authentication services.
/// Supports local authentication, SSO, and API key-based access.
/// </summary>
public interface IUserAuthenticationService
{
    /// <summary>
    /// Gets the current authenticated user's ID, or null if not authenticated.
    /// </summary>
    string? CurrentUserId { get; }

    /// <summary>
    /// Gets the current user session, or null if not authenticated.
    /// </summary>
    UserSession? CurrentSession { get; }

    /// <summary>
    /// Whether a user is currently authenticated.
    /// </summary>
    bool IsAuthenticated { get; }

    /// <summary>
    /// Event raised when authentication state changes.
    /// </summary>
    event EventHandler<AuthenticationStateChangedEventArgs>? AuthenticationStateChanged;

    /// <summary>
    /// Authenticates a user with username and password.
    /// </summary>
    /// <param name="username">Username or email.</param>
    /// <param name="password">User password.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>User session if authentication succeeds.</returns>
    /// <exception cref="AuthenticationException">If authentication fails.</exception>
    Task<UserSession> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Authenticates a user with an SSO provider.
    /// </summary>
    /// <param name="provider">SSO provider name (e.g., "azure-ad", "okta", "google").</param>
    /// <param name="token">SSO token or authorization code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>User session if authentication succeeds.</returns>
    /// <exception cref="AuthenticationException">If authentication fails.</exception>
    Task<UserSession> AuthenticateWithSsoAsync(
        string provider,
        string token,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Authenticates a user with an API key.
    /// </summary>
    /// <param name="apiKey">API key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>User session if authentication succeeds.</returns>
    /// <exception cref="AuthenticationException">If authentication fails.</exception>
    Task<UserSession> AuthenticateWithApiKeyAsync(
        string apiKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current session asynchronously.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Current session or null if not authenticated.</returns>
    Task<UserSession?> GetCurrentSessionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates and optionally refreshes the current session.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if session is valid.</returns>
    Task<bool> ValidateSessionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Refreshes the current session (extends expiration).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Refreshed session.</returns>
    /// <exception cref="AuthenticationException">If session cannot be refreshed.</exception>
    Task<UserSession> RefreshSessionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Logs out the current user and invalidates the session.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task LogoutAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Impersonates another user (requires admin privileges).
    /// </summary>
    /// <param name="targetUserId">User ID to impersonate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Impersonation session.</returns>
    /// <exception cref="UnauthorizedAccessException">If caller lacks permission.</exception>
    Task<UserSession> ImpersonateAsync(
        string targetUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends impersonation and returns to original user session.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Original user session.</returns>
    Task<UserSession> EndImpersonationAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Event arguments for authentication state changes.
/// </summary>
public sealed class AuthenticationStateChangedEventArgs : EventArgs
{
    /// <summary>
    /// Whether a user is now authenticated.
    /// </summary>
    public bool IsAuthenticated { get; init; }

    /// <summary>
    /// The new session (null if logged out).
    /// </summary>
    public UserSession? Session { get; init; }

    /// <summary>
    /// The previous session (null if was not authenticated).
    /// </summary>
    public UserSession? PreviousSession { get; init; }

    /// <summary>
    /// Reason for the state change.
    /// </summary>
    public AuthenticationStateChangeReason Reason { get; init; }
}

/// <summary>
/// Reason for authentication state change.
/// </summary>
public enum AuthenticationStateChangeReason
{
    /// <summary>User logged in.</summary>
    Login,

    /// <summary>User logged out.</summary>
    Logout,

    /// <summary>Session expired.</summary>
    SessionExpired,

    /// <summary>Session was refreshed.</summary>
    SessionRefreshed,

    /// <summary>User was impersonated.</summary>
    Impersonation,

    /// <summary>Impersonation ended.</summary>
    ImpersonationEnded,

    /// <summary>Session was invalidated (e.g., by admin).</summary>
    SessionInvalidated
}

/// <summary>
/// Exception thrown when authentication fails.
/// </summary>
public class AuthenticationException : Exception
{
    /// <summary>
    /// Error code for the authentication failure.
    /// </summary>
    public AuthenticationErrorCode ErrorCode { get; }

    /// <summary>
    /// Creates a new authentication exception.
    /// </summary>
    public AuthenticationException(string message, AuthenticationErrorCode errorCode = AuthenticationErrorCode.Unknown)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Creates a new authentication exception with inner exception.
    /// </summary>
    public AuthenticationException(string message, Exception innerException, AuthenticationErrorCode errorCode = AuthenticationErrorCode.Unknown)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }
}

/// <summary>
/// Error codes for authentication failures.
/// </summary>
public enum AuthenticationErrorCode
{
    /// <summary>Unknown error.</summary>
    Unknown = 0,

    /// <summary>Invalid credentials (username/password).</summary>
    InvalidCredentials = 1,

    /// <summary>User account is locked.</summary>
    AccountLocked = 2,

    /// <summary>User account is disabled.</summary>
    AccountDisabled = 3,

    /// <summary>Password has expired.</summary>
    PasswordExpired = 4,

    /// <summary>MFA required but not provided.</summary>
    MfaRequired = 5,

    /// <summary>Invalid MFA code.</summary>
    InvalidMfaCode = 6,

    /// <summary>Invalid SSO token.</summary>
    InvalidSsoToken = 7,

    /// <summary>SSO provider error.</summary>
    SsoProviderError = 8,

    /// <summary>Invalid API key.</summary>
    InvalidApiKey = 9,

    /// <summary>API key has expired.</summary>
    ApiKeyExpired = 10,

    /// <summary>Session has expired.</summary>
    SessionExpired = 11,

    /// <summary>Session was invalidated.</summary>
    SessionInvalidated = 12,

    /// <summary>Too many failed attempts.</summary>
    TooManyAttempts = 13,

    /// <summary>User not found.</summary>
    UserNotFound = 14,

    /// <summary>Insufficient permissions.</summary>
    InsufficientPermissions = 15
}
