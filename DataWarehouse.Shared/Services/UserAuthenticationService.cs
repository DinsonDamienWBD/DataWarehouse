// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using DataWarehouse.Shared.Models;

namespace DataWarehouse.Shared.Services;

/// <summary>
/// In-memory implementation of <see cref="IUserAuthenticationService"/>.
/// For production, replace with identity provider integration (Azure AD, Okta, etc.).
/// </summary>
public sealed class UserAuthenticationService : IUserAuthenticationService, IDisposable
{
    // Session storage: sessionId -> session
    private readonly ConcurrentDictionary<string, UserSession> _sessions = new();

    // User storage: userId -> user info (for demo purposes)
    private readonly ConcurrentDictionary<string, StoredUser> _users = new();

    // API key storage: apiKey hash -> userId
    private readonly ConcurrentDictionary<string, string> _apiKeys = new();

    // Current session per context (thread-local for multi-threaded scenarios)
    private readonly AsyncLocal<UserSession?> _currentSession = new();

    // Impersonation stack per session
    private readonly ConcurrentDictionary<string, Stack<UserSession>> _impersonationStack = new();

    // Rate limiting: userId -> (attempt count, window start)
    private readonly ConcurrentDictionary<string, (int Count, DateTime WindowStart)> _loginAttempts = new();

    // SSO provider handlers
    private readonly ConcurrentDictionary<string, ISsoProviderHandler> _ssoProviders = new();

    // Configuration
    private readonly AuthenticationServiceConfig _config;

    /// <inheritdoc />
    public string? CurrentUserId => _currentSession.Value?.UserId;

    /// <inheritdoc />
    public UserSession? CurrentSession => _currentSession.Value;

    /// <inheritdoc />
    public bool IsAuthenticated => _currentSession.Value?.IsValid == true;

    /// <inheritdoc />
    public event EventHandler<AuthenticationStateChangedEventArgs>? AuthenticationStateChanged;

    /// <summary>
    /// Creates a new authentication service with default configuration.
    /// </summary>
    public UserAuthenticationService() : this(new AuthenticationServiceConfig())
    {
    }

    /// <summary>
    /// Creates a new authentication service with the specified configuration.
    /// </summary>
    /// <param name="config">Service configuration.</param>
    public UserAuthenticationService(AuthenticationServiceConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));

        // Add default admin user for development
        if (_config.CreateDefaultAdminUser)
        {
            AddUser("admin", "Admin User", "admin@localhost", "admin", new[] { "admin" });
        }
    }

    /// <inheritdoc />
    public async Task<UserSession> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(password);

        // Check rate limiting
        CheckRateLimit(username);

        // Find user
        var user = FindUserByUsernameOrEmail(username);
        if (user == null)
        {
            RecordFailedAttempt(username);
            throw new AuthenticationException("Invalid username or password",
                AuthenticationErrorCode.InvalidCredentials);
        }

        // Verify password
        if (!VerifyPassword(password, user.PasswordHash, user.PasswordSalt))
        {
            RecordFailedAttempt(user.UserId);
            throw new AuthenticationException("Invalid username or password",
                AuthenticationErrorCode.InvalidCredentials);
        }

        // Check account status
        if (user.IsLocked)
        {
            throw new AuthenticationException("Account is locked",
                AuthenticationErrorCode.AccountLocked);
        }

        if (!user.IsEnabled)
        {
            throw new AuthenticationException("Account is disabled",
                AuthenticationErrorCode.AccountDisabled);
        }

        // Clear failed attempts on success
        _loginAttempts.TryRemove(user.UserId, out _);

        // Create session
        var session = CreateSession(user, "password");
        SetCurrentSession(session, null, AuthenticationStateChangeReason.Login);

        return await Task.FromResult(session);
    }

    /// <inheritdoc />
    public async Task<UserSession> AuthenticateWithSsoAsync(
        string provider,
        string token,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(token);

        // Get SSO provider handler
        if (!_ssoProviders.TryGetValue(provider.ToLowerInvariant(), out var handler))
        {
            throw new AuthenticationException($"SSO provider '{provider}' is not configured",
                AuthenticationErrorCode.SsoProviderError);
        }

        try
        {
            // Validate token with provider
            var ssoResult = await handler.ValidateTokenAsync(token, cancellationToken);

            if (!ssoResult.IsValid)
            {
                throw new AuthenticationException(ssoResult.ErrorMessage ?? "Invalid SSO token",
                    AuthenticationErrorCode.InvalidSsoToken);
            }

            // Find or create user
            var user = FindUserByUserId(ssoResult.UserId);
            if (user == null && _config.AutoProvisionSsoUsers)
            {
                user = new StoredUser
                {
                    UserId = ssoResult.UserId,
                    Username = ssoResult.Username ?? ssoResult.UserId,
                    DisplayName = ssoResult.DisplayName,
                    Email = ssoResult.Email,
                    OrganizationId = ssoResult.OrganizationId,
                    Roles = ssoResult.Roles?.ToList() ?? new List<string> { "user" },
                    IsEnabled = true,
                    CreatedAt = DateTime.UtcNow
                };
                _users[user.UserId] = user;
            }

            if (user == null)
            {
                throw new AuthenticationException("User not found and auto-provisioning is disabled",
                    AuthenticationErrorCode.UserNotFound);
            }

            // Create session
            var session = CreateSession(user, "sso", provider);
            SetCurrentSession(session, null, AuthenticationStateChangeReason.Login);

            return session;
        }
        catch (Exception ex) when (ex is not AuthenticationException)
        {
            throw new AuthenticationException($"SSO authentication failed: {ex.Message}", ex,
                AuthenticationErrorCode.SsoProviderError);
        }
    }

    /// <inheritdoc />
    public async Task<UserSession> AuthenticateWithApiKeyAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(apiKey);

        var keyHash = HashApiKey(apiKey);
        if (!_apiKeys.TryGetValue(keyHash, out var userId))
        {
            throw new AuthenticationException("Invalid API key",
                AuthenticationErrorCode.InvalidApiKey);
        }

        var user = FindUserByUserId(userId);
        if (user == null)
        {
            throw new AuthenticationException("User not found",
                AuthenticationErrorCode.UserNotFound);
        }

        if (!user.IsEnabled)
        {
            throw new AuthenticationException("Account is disabled",
                AuthenticationErrorCode.AccountDisabled);
        }

        var session = CreateSession(user, "apikey");
        SetCurrentSession(session, null, AuthenticationStateChangeReason.Login);

        return await Task.FromResult(session);
    }

    /// <inheritdoc />
    public Task<UserSession?> GetCurrentSessionAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_currentSession.Value);
    }

    /// <inheritdoc />
    public Task<bool> ValidateSessionAsync(CancellationToken cancellationToken = default)
    {
        var session = _currentSession.Value;
        if (session == null) return Task.FromResult(false);

        if (!session.IsValid)
        {
            SetCurrentSession(null, session, AuthenticationStateChangeReason.SessionExpired);
            return Task.FromResult(false);
        }

        // Update last activity
        session.Touch();
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task<UserSession> RefreshSessionAsync(CancellationToken cancellationToken = default)
    {
        var currentSession = _currentSession.Value
            ?? throw new AuthenticationException("No active session",
                AuthenticationErrorCode.SessionExpired);

        var user = FindUserByUserId(currentSession.UserId)
            ?? throw new AuthenticationException("User not found",
                AuthenticationErrorCode.UserNotFound);

        // Create new session with extended expiration
        var newSession = CreateSession(user, currentSession.AuthenticationMethod, currentSession.SsoProvider);

        // Remove old session
        _sessions.TryRemove(currentSession.SessionId, out _);

        SetCurrentSession(newSession, currentSession, AuthenticationStateChangeReason.SessionRefreshed);

        return Task.FromResult(newSession);
    }

    /// <inheritdoc />
    public Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        var session = _currentSession.Value;
        if (session != null)
        {
            _sessions.TryRemove(session.SessionId, out _);
            _impersonationStack.TryRemove(session.SessionId, out _);
            SetCurrentSession(null, session, AuthenticationStateChangeReason.Logout);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<UserSession> ImpersonateAsync(
        string targetUserId,
        CancellationToken cancellationToken = default)
    {
        var currentSession = _currentSession.Value
            ?? throw new AuthenticationException("No active session",
                AuthenticationErrorCode.SessionExpired);

        // Check permission
        if (!currentSession.IsAdmin)
        {
            throw new UnauthorizedAccessException("Only administrators can impersonate users");
        }

        var targetUser = FindUserByUserId(targetUserId)
            ?? throw new AuthenticationException("Target user not found",
                AuthenticationErrorCode.UserNotFound);

        // Push current session to impersonation stack
        var stack = _impersonationStack.GetOrAdd(currentSession.SessionId, _ => new Stack<UserSession>());
        stack.Push(currentSession);

        // Create impersonation session
        var impersonationSession = new UserSession
        {
            UserId = targetUser.UserId,
            DisplayName = targetUser.DisplayName,
            Email = targetUser.Email,
            OrganizationId = targetUser.OrganizationId,
            Roles = targetUser.Roles,
            AuthenticationMethod = "impersonation",
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            Claims = new Dictionary<string, object>
            {
                ["impersonated_by"] = currentSession.UserId,
                ["original_session_id"] = currentSession.SessionId
            }
        };

        _sessions[impersonationSession.SessionId] = impersonationSession;
        SetCurrentSession(impersonationSession, currentSession, AuthenticationStateChangeReason.Impersonation);

        return Task.FromResult(impersonationSession);
    }

    /// <inheritdoc />
    public Task<UserSession> EndImpersonationAsync(CancellationToken cancellationToken = default)
    {
        var currentSession = _currentSession.Value
            ?? throw new AuthenticationException("No active session",
                AuthenticationErrorCode.SessionExpired);

        // Check if this is an impersonation session
        if (!currentSession.Claims.TryGetValue("original_session_id", out var originalIdObj)
            || originalIdObj is not string originalSessionId)
        {
            throw new AuthenticationException("Not currently impersonating",
                AuthenticationErrorCode.InsufficientPermissions);
        }

        // Get original session from stack
        if (!_impersonationStack.TryGetValue(originalSessionId, out var stack) || !stack.TryPop(out var originalSession))
        {
            throw new AuthenticationException("Original session not found",
                AuthenticationErrorCode.SessionInvalidated);
        }

        // Remove impersonation session
        _sessions.TryRemove(currentSession.SessionId, out _);

        SetCurrentSession(originalSession, currentSession, AuthenticationStateChangeReason.ImpersonationEnded);

        return Task.FromResult(originalSession);
    }

    #region User Management (for demo/testing)

    /// <summary>
    /// Adds a user to the store.
    /// </summary>
    public void AddUser(string userId, string displayName, string email, string password, string[]? roles = null)
    {
        var (hash, salt) = HashPassword(password);
        var user = new StoredUser
        {
            UserId = userId,
            Username = userId,
            DisplayName = displayName,
            Email = email,
            PasswordHash = hash,
            PasswordSalt = salt,
            Roles = roles?.ToList() ?? new List<string> { "user" },
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow
        };
        _users[userId] = user;
    }

    /// <summary>
    /// Creates an API key for a user.
    /// </summary>
    /// <returns>The generated API key.</returns>
    public string CreateApiKey(string userId)
    {
        var keyBytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(keyBytes);

        var apiKey = $"dw_{Convert.ToBase64String(keyBytes).Replace("+", "").Replace("/", "").Replace("=", "")}";
        var keyHash = HashApiKey(apiKey);
        _apiKeys[keyHash] = userId;

        return apiKey;
    }

    /// <summary>
    /// Registers an SSO provider handler.
    /// </summary>
    public void RegisterSsoProvider(string providerName, ISsoProviderHandler handler)
    {
        _ssoProviders[providerName.ToLowerInvariant()] = handler;
    }

    /// <summary>
    /// Sets a session as current (for testing or service-to-service calls).
    /// </summary>
    public void SetSession(UserSession session)
    {
        _sessions[session.SessionId] = session;
        _currentSession.Value = session;
    }

    /// <summary>
    /// Gets a session by ID.
    /// </summary>
    public UserSession? GetSessionById(string sessionId)
    {
        return _sessions.TryGetValue(sessionId, out var session) ? session : null;
    }

    #endregion

    #region Private Methods

    private UserSession CreateSession(StoredUser user, string authMethod, string? ssoProvider = null)
    {
        var session = new UserSession
        {
            UserId = user.UserId,
            DisplayName = user.DisplayName,
            Email = user.Email,
            OrganizationId = user.OrganizationId,
            OrganizationName = user.OrganizationName,
            TenantId = user.TenantId,
            Roles = user.Roles,
            AuthenticationMethod = authMethod,
            SsoProvider = ssoProvider,
            ExpiresAt = DateTime.UtcNow.Add(_config.SessionDuration)
        };

        _sessions[session.SessionId] = session;
        return session;
    }

    private void SetCurrentSession(
        UserSession? newSession,
        UserSession? previousSession,
        AuthenticationStateChangeReason reason)
    {
        _currentSession.Value = newSession;

        AuthenticationStateChanged?.Invoke(this, new AuthenticationStateChangedEventArgs
        {
            IsAuthenticated = newSession?.IsValid == true,
            Session = newSession,
            PreviousSession = previousSession,
            Reason = reason
        });
    }

    private StoredUser? FindUserByUserId(string userId)
    {
        return _users.TryGetValue(userId, out var user) ? user : null;
    }

    private StoredUser? FindUserByUsernameOrEmail(string usernameOrEmail)
    {
        return _users.Values.FirstOrDefault(u =>
            u.Username.Equals(usernameOrEmail, StringComparison.OrdinalIgnoreCase) ||
            (u.Email != null && u.Email.Equals(usernameOrEmail, StringComparison.OrdinalIgnoreCase)));
    }

    private void CheckRateLimit(string identifier)
    {
        if (!_config.EnableRateLimiting) return;

        if (_loginAttempts.TryGetValue(identifier, out var attempts))
        {
            var windowElapsed = DateTime.UtcNow - attempts.WindowStart;
            if (windowElapsed < _config.RateLimitWindow)
            {
                if (attempts.Count >= _config.MaxLoginAttempts)
                {
                    throw new AuthenticationException(
                        $"Too many login attempts. Please try again in {(_config.RateLimitWindow - windowElapsed).TotalMinutes:F0} minutes.",
                        AuthenticationErrorCode.TooManyAttempts);
                }
            }
        }
    }

    private void RecordFailedAttempt(string identifier)
    {
        _loginAttempts.AddOrUpdate(
            identifier,
            _ => (1, DateTime.UtcNow),
            (_, existing) =>
            {
                if (DateTime.UtcNow - existing.WindowStart >= _config.RateLimitWindow)
                {
                    return (1, DateTime.UtcNow);
                }
                return (existing.Count + 1, existing.WindowStart);
            });
    }

    private static (byte[] Hash, byte[] Salt) HashPassword(string password)
    {
        var salt = new byte[16];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(salt);

        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            100000,
            HashAlgorithmName.SHA256,
            32);

        return (hash, salt);
    }

    private static bool VerifyPassword(string password, byte[] storedHash, byte[] salt)
    {
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            100000,
            HashAlgorithmName.SHA256,
            32);

        return CryptographicOperations.FixedTimeEquals(hash, storedHash);
    }

    private static string HashApiKey(string apiKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexString(bytes);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _sessions.Clear();
        _users.Clear();
        _apiKeys.Clear();
        _impersonationStack.Clear();
        _loginAttempts.Clear();
    }

    #endregion

    #region Nested Types

    private sealed class StoredUser
    {
        public string UserId { get; init; } = string.Empty;
        public string Username { get; init; } = string.Empty;
        public string? DisplayName { get; init; }
        public string? Email { get; init; }
        public string? OrganizationId { get; init; }
        public string? OrganizationName { get; init; }
        public string? TenantId { get; init; }
        public byte[] PasswordHash { get; init; } = Array.Empty<byte>();
        public byte[] PasswordSalt { get; init; } = Array.Empty<byte>();
        public List<string> Roles { get; init; } = new();
        public bool IsEnabled { get; set; } = true;
        public bool IsLocked { get; set; }
        public DateTime CreatedAt { get; init; }
    }

    #endregion
}

/// <summary>
/// Configuration for the authentication service.
/// </summary>
public sealed class AuthenticationServiceConfig
{
    /// <summary>
    /// Session duration before expiration.
    /// </summary>
    public TimeSpan SessionDuration { get; init; } = TimeSpan.FromHours(8);

    /// <summary>
    /// Whether to enable login rate limiting.
    /// </summary>
    public bool EnableRateLimiting { get; init; } = true;

    /// <summary>
    /// Maximum login attempts before lockout.
    /// </summary>
    public int MaxLoginAttempts { get; init; } = 5;

    /// <summary>
    /// Rate limit window duration.
    /// </summary>
    public TimeSpan RateLimitWindow { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Whether to automatically provision users from SSO.
    /// </summary>
    public bool AutoProvisionSsoUsers { get; init; } = true;

    /// <summary>
    /// Whether to create a default admin user (for development only).
    /// </summary>
    public bool CreateDefaultAdminUser { get; init; }
}

/// <summary>
/// Interface for SSO provider handlers.
/// </summary>
public interface ISsoProviderHandler
{
    /// <summary>
    /// Validates an SSO token and returns user information.
    /// </summary>
    Task<SsoValidationResult> ValidateTokenAsync(string token, CancellationToken ct);
}

/// <summary>
/// Result of SSO token validation.
/// </summary>
public sealed record SsoValidationResult
{
    /// <summary>Whether the token is valid.</summary>
    public bool IsValid { get; init; }

    /// <summary>User ID from the SSO provider.</summary>
    public string UserId { get; init; } = string.Empty;

    /// <summary>Username from the SSO provider.</summary>
    public string? Username { get; init; }

    /// <summary>Display name from the SSO provider.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Email from the SSO provider.</summary>
    public string? Email { get; init; }

    /// <summary>Organization ID from the SSO provider.</summary>
    public string? OrganizationId { get; init; }

    /// <summary>Roles from the SSO provider.</summary>
    public IReadOnlyList<string>? Roles { get; init; }

    /// <summary>Error message if validation failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Creates a successful validation result.</summary>
    public static SsoValidationResult Success(
        string userId,
        string? username = null,
        string? displayName = null,
        string? email = null,
        string? organizationId = null,
        IReadOnlyList<string>? roles = null) => new()
    {
        IsValid = true,
        UserId = userId,
        Username = username,
        DisplayName = displayName,
        Email = email,
        OrganizationId = organizationId,
        Roles = roles
    };

    /// <summary>Creates a failed validation result.</summary>
    public static SsoValidationResult Failure(string errorMessage) => new()
    {
        IsValid = false,
        ErrorMessage = errorMessage
    };
}
