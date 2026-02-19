// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace DataWarehouse.SDK.Security;

/// <summary>
/// AUTH-12 (CVSS 3.4): Write protection for security-sensitive configuration.
/// After initial configuration, security settings become immutable unless explicitly unlocked
/// by an admin action with audit trail logging.
///
/// Security-sensitive settings include:
/// - Authentication configuration (providers, MFA, session timeouts)
/// - Encryption settings (algorithms, key lengths, FIPS mode)
/// - Access control policies (RBAC, ABAC rules)
/// - Plugin signing requirements
/// - TLS/SSL configuration
///
/// The lock is engaged automatically after kernel initialization completes.
/// Modification requires explicit Unlock() call which is logged to the audit trail.
/// </summary>
public sealed class SecurityConfigLock
{
    private volatile bool _isLocked;
    private readonly object _lock = new();

    /// <summary>
    /// Whether security configuration is currently locked (immutable).
    /// </summary>
    public bool IsLocked => _isLocked;

    /// <summary>
    /// Event fired when a security config modification is attempted while locked.
    /// </summary>
    public event EventHandler<SecurityConfigViolationEventArgs>? ViolationAttempted;

    /// <summary>
    /// Event fired when the lock state changes (locked or unlocked).
    /// </summary>
    public event EventHandler<SecurityConfigLockStateChangedEventArgs>? LockStateChanged;

    /// <summary>
    /// Configuration paths considered security-sensitive.
    /// Any path starting with one of these prefixes is protected when the lock is engaged.
    /// </summary>
    private static readonly string[] SecurityPrefixes =
    {
        "security.",
        "auth.",
        "encryption.",
        "access.",
        "tls.",
        "ssl.",
        "signing.",
        "fips.",
        "compliance.",
        "plugin.signing",
        "kernel.requireSignedAssemblies"
    };

    /// <summary>
    /// Engages the security configuration lock.
    /// After this call, all security-sensitive configuration modifications are blocked
    /// until <see cref="Unlock"/> is called with proper authorization.
    /// </summary>
    /// <param name="reason">Reason for engaging the lock (logged for audit).</param>
    public void Lock(string reason = "Post-initialization security lock engaged")
    {
        lock (_lock)
        {
            _isLocked = true;
            LockStateChanged?.Invoke(this, new SecurityConfigLockStateChangedEventArgs(
                true, "System", reason));
        }
    }

    /// <summary>
    /// Temporarily disengages the security configuration lock for authorized modifications.
    /// The caller must provide a valid admin identity and reason for the audit trail.
    /// Returns a disposable scope that automatically re-locks when disposed.
    /// </summary>
    /// <param name="adminIdentity">The CommandIdentity of the admin performing the unlock.</param>
    /// <param name="reason">Reason for the unlock (logged for audit).</param>
    /// <returns>A disposable scope that re-locks automatically when disposed.</returns>
    /// <exception cref="ArgumentNullException">Thrown when adminIdentity is null.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the identity is not a system or admin principal.</exception>
    public IDisposable Unlock(CommandIdentity adminIdentity, string reason)
    {
        ArgumentNullException.ThrowIfNull(adminIdentity);

        // Only system principals or identities with admin roles can unlock
        var isAuthorized = adminIdentity.PrincipalType == PrincipalType.System ||
                           adminIdentity.Roles.Any(r =>
                               r.Equals("admin", StringComparison.OrdinalIgnoreCase) ||
                               r.Equals("security-admin", StringComparison.OrdinalIgnoreCase));

        if (!isAuthorized)
        {
            ViolationAttempted?.Invoke(this, new SecurityConfigViolationEventArgs(
                "unlock", "security.config.lock", adminIdentity.EffectivePrincipalId,
                $"Unauthorized unlock attempt by {adminIdentity.EffectivePrincipalId}"));
            throw new UnauthorizedAccessException(
                "Only system principals or admin/security-admin roles can unlock security configuration (AUTH-12).");
        }

        lock (_lock)
        {
            _isLocked = false;
            LockStateChanged?.Invoke(this, new SecurityConfigLockStateChangedEventArgs(
                false, adminIdentity.EffectivePrincipalId, reason));
        }

        return new SecurityConfigLockScope(this, reason);
    }

    /// <summary>
    /// Checks if a configuration path is security-sensitive and currently protected.
    /// </summary>
    /// <param name="settingPath">The configuration path to check.</param>
    /// <param name="requestingPrincipal">The principal attempting the modification.</param>
    /// <returns>True if the modification is allowed, false if blocked.</returns>
    public bool IsModificationAllowed(string settingPath, string? requestingPrincipal = null)
    {
        if (!_isLocked)
            return true;

        if (!IsSecuritySensitive(settingPath))
            return true;

        // Locked and security-sensitive -- block
        ViolationAttempted?.Invoke(this, new SecurityConfigViolationEventArgs(
            "modify", settingPath, requestingPrincipal ?? "unknown",
            $"Attempted modification of locked security setting: {settingPath}"));

        return false;
    }

    /// <summary>
    /// Determines if a configuration path is security-sensitive.
    /// </summary>
    public static bool IsSecuritySensitive(string settingPath)
    {
        if (string.IsNullOrEmpty(settingPath))
            return false;

        return SecurityPrefixes.Any(prefix =>
            settingPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Disposable scope that automatically re-locks security configuration when disposed.
    /// </summary>
    private sealed class SecurityConfigLockScope : IDisposable
    {
        private readonly SecurityConfigLock _configLock;
        private readonly string _reason;
        private bool _disposed;

        public SecurityConfigLockScope(SecurityConfigLock configLock, string reason)
        {
            _configLock = configLock;
            _reason = reason;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _configLock.Lock($"Auto-relocked after: {_reason}");
        }
    }
}

/// <summary>
/// Event arguments for security configuration violation attempts.
/// </summary>
public sealed class SecurityConfigViolationEventArgs : EventArgs
{
    public string Operation { get; }
    public string SettingPath { get; }
    public string PrincipalId { get; }
    public string Message { get; }
    public DateTimeOffset Timestamp { get; } = DateTimeOffset.UtcNow;

    public SecurityConfigViolationEventArgs(
        string operation, string settingPath, string principalId, string message)
    {
        Operation = operation;
        SettingPath = settingPath;
        PrincipalId = principalId;
        Message = message;
    }
}

/// <summary>
/// Event arguments for security configuration lock state changes.
/// </summary>
public sealed class SecurityConfigLockStateChangedEventArgs : EventArgs
{
    public bool IsLocked { get; }
    public string PrincipalId { get; }
    public string Reason { get; }
    public DateTimeOffset Timestamp { get; } = DateTimeOffset.UtcNow;

    public SecurityConfigLockStateChangedEventArgs(bool isLocked, string principalId, string reason)
    {
        IsLocked = isLocked;
        PrincipalId = principalId;
        Reason = reason;
    }
}
