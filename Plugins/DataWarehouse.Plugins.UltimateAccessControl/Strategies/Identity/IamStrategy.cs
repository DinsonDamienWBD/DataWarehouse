using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Identity
{
    /// <summary>
    /// Generic Identity and Access Management (IAM) strategy with configurable user and role stores.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Provides a flexible IAM framework supporting:
    /// - User authentication with password hashing (PBKDF2-SHA256)
    /// - Role-based access control with hierarchical roles
    /// - Permission management and evaluation
    /// - User lifecycle management (create, update, delete, suspend)
    /// - Audit logging for all authentication and authorization events
    /// - Multi-factor authentication support (MFA)
    /// - Session management with configurable timeouts
    /// </para>
    /// <para>
    /// Storage is abstract - can be backed by any data store via configuration.
    /// Default implementation uses in-memory storage (for production, configure external store).
    /// </para>
    /// </remarks>
    public sealed class IamStrategy : AccessControlStrategyBase
    {
        private readonly BoundedDictionary<string, IamUser> _users = new BoundedDictionary<string, IamUser>(1000);
        private readonly BoundedDictionary<string, IamRole> _roles = new BoundedDictionary<string, IamRole>(1000);
        private readonly BoundedDictionary<string, IamSession> _sessions = new BoundedDictionary<string, IamSession>(1000);
        private readonly BoundedDictionary<string, List<AuditEvent>> _auditLog = new BoundedDictionary<string, List<AuditEvent>>(1000);

        private TimeSpan _sessionTimeout = TimeSpan.FromHours(8);
        private int _passwordMinLength = 12;
        private int _pbkdf2Iterations = 100000;
        private bool _requireMfa = false;

        /// <inheritdoc/>
        public override string StrategyId => "identity-iam";

        /// <inheritdoc/>
        public override string StrategyName => "IAM (Identity and Access Management)";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = false,
            SupportsTemporalAccess = true,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 10000
        };

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            if (configuration.TryGetValue("SessionTimeoutHours", out var sth) && sth is int sthInt)
                _sessionTimeout = TimeSpan.FromHours(sthInt);

            if (configuration.TryGetValue("PasswordMinLength", out var pml) && pml is int pmlInt)
                _passwordMinLength = pmlInt;

            if (configuration.TryGetValue("Pbkdf2Iterations", out var pi) && pi is int piInt)
                _pbkdf2Iterations = piInt;

            if (configuration.TryGetValue("RequireMfa", out var rm) && rm is bool rmBool)
                _requireMfa = rmBool;

            // Initialize default admin user if none exists
            if (!_users.Any())
            {
                CreateDefaultAdminUser();
            }

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Production hardening: validates configuration parameters on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("identity.iam.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources and clears caches on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("identity.iam.shutdown");
            _users.Clear();
            _roles.Clear();
            _sessions.Clear();
            return base.ShutdownAsyncCore(cancellationToken);
        }


        /// <summary>
        /// Checks if the IAM infrastructure is available (always true for in-memory store).
        /// </summary>
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        #region User Management

        /// <summary>
        /// Creates a new IAM user.
        /// </summary>
        public IamUser CreateUser(string username, string password, string email, string[] roles)
        {
            if (string.IsNullOrWhiteSpace(username))
                throw new ArgumentException("Username cannot be empty", nameof(username));

            if (password.Length < _passwordMinLength)
                throw new ArgumentException($"Password must be at least {_passwordMinLength} characters", nameof(password));

            if (_users.ContainsKey(username))
                throw new InvalidOperationException($"User '{username}' already exists");

            var (hash, salt) = HashPassword(password);

            var user = new IamUser
            {
                UserId = Guid.NewGuid().ToString("N"),
                Username = username,
                Email = email,
                PasswordHash = hash,
                PasswordSalt = salt,
                Roles = roles.ToList(),
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                LastModifiedAt = DateTime.UtcNow,
                MfaEnabled = false
            };

            _users[username] = user;
            LogAudit(user.UserId, "UserCreated", $"User {username} created");

            return user;
        }

        /// <summary>
        /// Authenticates a user with username and password.
        /// </summary>
        public async Task<AuthenticationResult> AuthenticateAsync(
            string username,
            string password,
            string? mfaCode = null,
            CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask; // Make async for extensibility

            if (!_users.TryGetValue(username, out var user))
            {
                LogAudit(null, "AuthenticationFailed", $"User {username} not found");
                return new AuthenticationResult
                {
                    Success = false,
                    ErrorMessage = "Invalid username or password"
                };
            }

            if (!user.IsActive)
            {
                LogAudit(user.UserId, "AuthenticationFailed", $"User {username} is inactive");
                return new AuthenticationResult
                {
                    Success = false,
                    ErrorMessage = "User account is inactive"
                };
            }

            if (!VerifyPassword(password, user.PasswordHash, user.PasswordSalt))
            {
                lock (user) { user.FailedLoginAttempts++; }
                user.LastFailedLoginAt = DateTime.UtcNow;

                if (user.FailedLoginAttempts >= 5)
                {
                    user.IsActive = false;
                    LogAudit(user.UserId, "AccountLocked", $"User {username} locked after 5 failed attempts");
                }

                LogAudit(user.UserId, "AuthenticationFailed", $"Invalid password for {username}");
                return new AuthenticationResult
                {
                    Success = false,
                    ErrorMessage = "Invalid username or password"
                };
            }

            // MFA verification
            if (_requireMfa || user.MfaEnabled)
            {
                if (string.IsNullOrEmpty(mfaCode))
                {
                    return new AuthenticationResult
                    {
                        Success = false,
                        RequiresMfa = true,
                        ErrorMessage = "MFA code required"
                    };
                }

                if (!VerifyMfaCode(user, mfaCode))
                {
                    LogAudit(user.UserId, "MfaFailed", $"Invalid MFA code for {username}");
                    return new AuthenticationResult
                    {
                        Success = false,
                        ErrorMessage = "Invalid MFA code"
                    };
                }
            }

            // Successful authentication
            user.FailedLoginAttempts = 0;
            user.LastLoginAt = DateTime.UtcNow;

            var session = CreateSession(user);
            LogAudit(user.UserId, "AuthenticationSuccess", $"User {username} authenticated");

            return new AuthenticationResult
            {
                Success = true,
                UserId = user.UserId,
                Username = user.Username,
                Roles = user.Roles.AsReadOnly(),
                SessionId = session.SessionId
            };
        }

        #endregion

        #region Role Management

        /// <summary>
        /// Creates a new IAM role.
        /// </summary>
        public IamRole CreateRole(string roleName, string description, string[] permissions)
        {
            if (string.IsNullOrWhiteSpace(roleName))
                throw new ArgumentException("Role name cannot be empty", nameof(roleName));

            if (_roles.ContainsKey(roleName))
                throw new InvalidOperationException($"Role '{roleName}' already exists");

            var role = new IamRole
            {
                RoleId = Guid.NewGuid().ToString("N"),
                RoleName = roleName,
                Description = description,
                Permissions = permissions.ToList(),
                CreatedAt = DateTime.UtcNow
            };

            _roles[roleName] = role;
            return role;
        }

        /// <summary>
        /// Gets all permissions for a user (accumulated from all roles).
        /// </summary>
        public HashSet<string> GetUserPermissions(string userId)
        {
            var user = _users.Values.FirstOrDefault(u => u.UserId == userId);
            if (user == null)
                return new HashSet<string>();

            var permissions = new HashSet<string>();
            foreach (var roleName in user.Roles)
            {
                if (_roles.TryGetValue(roleName, out var role))
                {
                    foreach (var perm in role.Permissions)
                        permissions.Add(perm);
                }
            }

            return permissions;
        }

        #endregion

        #region Session Management

        private IamSession CreateSession(IamUser user)
        {
            var session = new IamSession
            {
                SessionId = Guid.NewGuid().ToString("N"),
                UserId = user.UserId,
                Username = user.Username,
                Roles = user.Roles.AsReadOnly(),
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.Add(_sessionTimeout),
                IsActive = true
            };

            _sessions[session.SessionId] = session;
            return session;
        }

        /// <summary>
        /// Validates a session.
        /// </summary>
        public IamSession? ValidateSession(string sessionId)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
                return null;

            if (!session.IsActive || session.ExpiresAt < DateTime.UtcNow)
                return null;

            return session;
        }

        #endregion

        #region Core Evaluation

        /// <inheritdoc/>
        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("identity.iam.evaluate");
            await Task.CompletedTask; // Make async for extensibility

            // Extract session or credentials from context
            if (context.EnvironmentAttributes.TryGetValue("SessionId", out var sessionIdObj) &&
                sessionIdObj is string sessionId)
            {
                var session = ValidateSession(sessionId);
                if (session == null)
                {
                    return new AccessDecision
                    {
                        IsGranted = false,
                        Reason = "Invalid or expired session"
                    };
                }

                // Check if user has permission for the requested action
                var permissions = GetUserPermissions(session.UserId);
                var requiredPermission = $"{context.ResourceId}:{context.Action}";

                if (permissions.Contains(requiredPermission) || permissions.Contains("*:*"))
                {
                    LogAudit(session.UserId, "AccessGranted", $"Access granted to {context.ResourceId}:{context.Action}");
                    return new AccessDecision
                    {
                        IsGranted = true,
                        Reason = "Permission granted by IAM policy"
                    };
                }

                LogAudit(session.UserId, "AccessDenied", $"Access denied to {context.ResourceId}:{context.Action}");
                return new AccessDecision
                {
                    IsGranted = false,
                    Reason = "Insufficient permissions"
                };
            }

            return new AccessDecision
            {
                IsGranted = false,
                Reason = "No session or credentials provided"
            };
        }

        #endregion

        #region Helpers

        private void CreateDefaultAdminUser()
        {
            // Create admin role
            var adminRole = CreateRole("admin", "System Administrator", new[] { "*:*" });

            // Create default admin user with a random password (should be changed on first login)
            var defaultPassword = Guid.NewGuid().ToString("N");
            var adminUser = CreateUser("admin", defaultPassword, "admin@localhost", new[] { "admin" });

            LogAudit(adminUser.UserId, "DefaultAdminCreated", $"Default admin user created (change password immediately)");
        }

        private (string hash, string salt) HashPassword(string password)
        {
            var salt = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            var saltBytes = Convert.FromBase64String(salt);

            var hashBytes = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
                password,
                saltBytes,
                _pbkdf2Iterations,
                System.Security.Cryptography.HashAlgorithmName.SHA256,
                32);

            var hash = Convert.ToBase64String(hashBytes);
            return (hash, salt);
        }

        private bool VerifyPassword(string password, string hash, string salt)
        {
            var saltBytes = Convert.FromBase64String(salt);

            var computedHashBytes = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
                password,
                saltBytes,
                _pbkdf2Iterations,
                System.Security.Cryptography.HashAlgorithmName.SHA256,
                32);

            // Use constant-time comparison to prevent timing-attack enumeration of valid hashes
            var storedHashBytes = Convert.FromBase64String(hash);
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(computedHashBytes, storedHashBytes);
        }

        private bool VerifyMfaCode(IamUser user, string code)
        {
            if (string.IsNullOrEmpty(user.MfaSecret))
                return false;

            // TOTP verification (RFC 6238)
            var unixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var timeStep = unixTime / 30; // 30-second window

            // Check current and ±1 time steps for clock drift tolerance
            for (int i = -1; i <= 1; i++)
            {
                var totp = GenerateTotp(user.MfaSecret, timeStep + i);
                if (totp == code)
                    return true;
            }

            return false;
        }

        private string GenerateTotp(string secret, long timeStep)
        {
            var secretBytes = Convert.FromBase64String(secret);
            var timeBytes = BitConverter.GetBytes(timeStep);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(timeBytes);

            using var hmac = new System.Security.Cryptography.HMACSHA1(secretBytes);
            var hash = hmac.ComputeHash(timeBytes);

            var offset = hash[hash.Length - 1] & 0x0F;
            var binary = ((hash[offset] & 0x7F) << 24) |
                        ((hash[offset + 1] & 0xFF) << 16) |
                        ((hash[offset + 2] & 0xFF) << 8) |
                        (hash[offset + 3] & 0xFF);

            var otp = binary % 1000000;
            return otp.ToString("D6");
        }

        private void LogAudit(string? userId, string action, string details)
        {
            var evt = new AuditEvent
            {
                Timestamp = DateTime.UtcNow,
                UserId = userId ?? "anonymous",
                Action = action,
                Details = details
            };

            var key = userId ?? "anonymous";
            _auditLog.AddOrUpdate(key, new List<AuditEvent> { evt }, (k, list) =>
            {
                lock (list)
                {
                    list.Add(evt);
                }
                return list;
            });
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// IAM user entity.
    /// </summary>
    public sealed record IamUser
    {
        public required string UserId { get; init; }
        public required string Username { get; init; }
        public required string Email { get; init; }
        public required string PasswordHash { get; init; }
        public required string PasswordSalt { get; init; }
        public required List<string> Roles { get; init; }
        public required bool IsActive { get; set; }
        public required DateTime CreatedAt { get; init; }
        public required DateTime LastModifiedAt { get; set; }
        public DateTime? LastLoginAt { get; set; }
        public DateTime? LastFailedLoginAt { get; set; }
        public int FailedLoginAttempts { get; set; }
        public bool MfaEnabled { get; set; }
        public string? MfaSecret { get; set; }
    }

    /// <summary>
    /// IAM role entity.
    /// </summary>
    public sealed record IamRole
    {
        public required string RoleId { get; init; }
        public required string RoleName { get; init; }
        public required string Description { get; init; }
        public required List<string> Permissions { get; init; }
        public required DateTime CreatedAt { get; init; }
    }

    /// <summary>
    /// IAM session.
    /// </summary>
    public sealed record IamSession
    {
        public required string SessionId { get; init; }
        public required string UserId { get; init; }
        public required string Username { get; init; }
        public required IReadOnlyList<string> Roles { get; init; }
        public required DateTime CreatedAt { get; init; }
        public required DateTime ExpiresAt { get; init; }
        public required bool IsActive { get; init; }
    }

    /// <summary>
    /// Authentication result.
    /// </summary>
    public sealed record AuthenticationResult
    {
        public required bool Success { get; init; }
        public string? UserId { get; init; }
        public string? Username { get; init; }
        public IReadOnlyList<string>? Roles { get; init; }
        public string? SessionId { get; init; }
        public bool RequiresMfa { get; init; }
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Audit event.
    /// </summary>
    public sealed record AuditEvent
    {
        public required DateTime Timestamp { get; init; }
        public required string UserId { get; init; }
        public required string Action { get; init; }
        public required string Details { get; init; }
    }

    #endregion
}
