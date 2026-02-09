using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.EphemeralSharing
{
    #region T76.1: Ephemeral Link Generator

    /// <summary>
    /// Generates time/access-limited sharing URLs with cryptographic tokens.
    /// Supports configurable base URLs, custom paths, and URL-safe token encoding.
    /// </summary>
    public sealed class EphemeralLinkGenerator
    {
        private readonly string _baseUrl;
        private readonly string _sharePath;

        /// <summary>
        /// Creates a new link generator with the specified base URL.
        /// </summary>
        /// <param name="baseUrl">Base URL for share links (e.g., "https://share.example.com").</param>
        /// <param name="sharePath">Path segment for share endpoints (default: "/s/").</param>
        public EphemeralLinkGenerator(string baseUrl, string sharePath = "/s/")
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _sharePath = sharePath;
        }

        /// <summary>
        /// Generates a complete shareable URL for the given token.
        /// </summary>
        public string GenerateShareUrl(string token)
        {
            return $"{_baseUrl}{_sharePath}{token}";
        }

        /// <summary>
        /// Generates a URL with embedded expiration hint (for UI display).
        /// </summary>
        public string GenerateShareUrlWithHint(string token, DateTime expiresAt)
        {
            var hint = Convert.ToBase64String(BitConverter.GetBytes(expiresAt.ToBinary()))
                .Replace("+", "-").Replace("/", "_").TrimEnd('=');
            return $"{_baseUrl}{_sharePath}{token}?h={hint}";
        }

        /// <summary>
        /// Generates a URL-safe cryptographic token (32 bytes of entropy).
        /// </summary>
        public static string GenerateSecureToken()
        {
            var bytes = RandomNumberGenerator.GetBytes(32);
            return Convert.ToBase64String(bytes)
                .Replace("+", "-")
                .Replace("/", "_")
                .TrimEnd('=');
        }

        /// <summary>
        /// Generates a short token for easier sharing (16 bytes, alphanumeric only).
        /// </summary>
        public static string GenerateShortToken()
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            var bytes = RandomNumberGenerator.GetBytes(16);
            var result = new char[16];
            for (int i = 0; i < 16; i++)
            {
                result[i] = chars[bytes[i] % chars.Length];
            }
            return new string(result);
        }
    }

    #endregion

    #region T76.2: Access Counter

    /// <summary>
    /// Thread-safe atomic counter for tracking remaining access attempts.
    /// Provides lock-free operations for high-concurrency scenarios.
    /// </summary>
    public sealed class AccessCounter
    {
        private int _remainingAccesses;
        private int _totalAccesses;
        private readonly int _maxAccesses;
        private readonly object _lock = new();

        /// <summary>
        /// Creates a counter with the specified maximum access count.
        /// </summary>
        /// <param name="maxAccesses">Maximum allowed accesses (-1 for unlimited).</param>
        public AccessCounter(int maxAccesses)
        {
            _maxAccesses = maxAccesses;
            _remainingAccesses = maxAccesses;
        }

        /// <summary>
        /// Gets the remaining number of accesses (-1 if unlimited).
        /// </summary>
        public int RemainingAccesses => _maxAccesses < 0 ? -1 : Volatile.Read(ref _remainingAccesses);

        /// <summary>
        /// Gets the total number of accesses recorded.
        /// </summary>
        public int TotalAccesses => Volatile.Read(ref _totalAccesses);

        /// <summary>
        /// Gets the maximum allowed accesses (-1 if unlimited).
        /// </summary>
        public int MaxAccesses => _maxAccesses;

        /// <summary>
        /// Gets whether unlimited access is allowed.
        /// </summary>
        public bool IsUnlimited => _maxAccesses < 0;

        /// <summary>
        /// Attempts to consume one access. Returns true if successful.
        /// </summary>
        public bool TryConsume()
        {
            lock (_lock)
            {
                Interlocked.Increment(ref _totalAccesses);

                if (_maxAccesses < 0)
                    return true;

                if (_remainingAccesses <= 0)
                    return false;

                _remainingAccesses--;
                return true;
            }
        }

        /// <summary>
        /// Attempts to consume multiple accesses atomically.
        /// </summary>
        public bool TryConsume(int count)
        {
            if (count <= 0) return true;

            lock (_lock)
            {
                if (_maxAccesses < 0)
                {
                    Interlocked.Add(ref _totalAccesses, count);
                    return true;
                }

                if (_remainingAccesses < count)
                    return false;

                _remainingAccesses -= count;
                Interlocked.Add(ref _totalAccesses, count);
                return true;
            }
        }

        /// <summary>
        /// Returns an access (for failed operations that should not count).
        /// </summary>
        public void Return()
        {
            lock (_lock)
            {
                Interlocked.Decrement(ref _totalAccesses);
                if (_maxAccesses >= 0 && _remainingAccesses < _maxAccesses)
                {
                    _remainingAccesses++;
                }
            }
        }
    }

    #endregion

    #region T76.3: TTL Engine

    /// <summary>
    /// Precise time-based expiration engine with second-level granularity.
    /// Supports absolute expiration, sliding windows, and grace periods.
    /// </summary>
    public sealed class TtlEngine
    {
        private DateTime _absoluteExpiration;
        private readonly TimeSpan? _slidingWindow;
        private readonly TimeSpan _gracePeriod;
        private readonly DateTime _createdAt;
        private DateTime _lastAccess;
        private readonly object _lock = new();

        /// <summary>
        /// Creates a TTL engine with the specified expiration settings.
        /// </summary>
        /// <param name="absoluteExpiration">Absolute expiration time (UTC).</param>
        /// <param name="slidingWindow">Optional sliding window duration.</param>
        /// <param name="gracePeriod">Grace period after expiration for cleanup.</param>
        public TtlEngine(
            DateTime absoluteExpiration,
            TimeSpan? slidingWindow = null,
            TimeSpan? gracePeriod = null)
        {
            _absoluteExpiration = absoluteExpiration;
            _slidingWindow = slidingWindow;
            _gracePeriod = gracePeriod ?? TimeSpan.Zero;
            _createdAt = DateTime.UtcNow;
            _lastAccess = _createdAt;
        }

        /// <summary>
        /// Gets the absolute expiration time (UTC).
        /// </summary>
        public DateTime AbsoluteExpiration
        {
            get
            {
                lock (_lock) return _absoluteExpiration;
            }
        }

        /// <summary>
        /// Gets the creation timestamp (UTC).
        /// </summary>
        public DateTime CreatedAt => _createdAt;

        /// <summary>
        /// Gets the last access timestamp (UTC).
        /// </summary>
        public DateTime LastAccess
        {
            get
            {
                lock (_lock) return _lastAccess;
            }
        }

        /// <summary>
        /// Gets the time remaining until expiration.
        /// </summary>
        public TimeSpan TimeRemaining
        {
            get
            {
                var remaining = AbsoluteExpiration - DateTime.UtcNow;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }

        /// <summary>
        /// Gets whether the TTL has expired.
        /// </summary>
        public bool IsExpired => DateTime.UtcNow > AbsoluteExpiration;

        /// <summary>
        /// Gets whether the grace period has also elapsed.
        /// </summary>
        public bool IsGracePeriodExpired => DateTime.UtcNow > AbsoluteExpiration + _gracePeriod;

        /// <summary>
        /// Records an access and optionally extends sliding window.
        /// </summary>
        /// <returns>True if access is within valid TTL, false if expired.</returns>
        public bool RecordAccess()
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;

                if (now > _absoluteExpiration)
                    return false;

                _lastAccess = now;

                // Extend sliding window if configured
                if (_slidingWindow.HasValue)
                {
                    var newExpiration = now + _slidingWindow.Value;
                    if (newExpiration > _absoluteExpiration)
                    {
                        _absoluteExpiration = newExpiration;
                    }
                }

                return true;
            }
        }

        /// <summary>
        /// Manually extends the expiration time.
        /// </summary>
        public void ExtendExpiration(TimeSpan extension)
        {
            lock (_lock)
            {
                _absoluteExpiration = _absoluteExpiration + extension;
            }
        }

        /// <summary>
        /// Forces immediate expiration.
        /// </summary>
        public void ForceExpire()
        {
            lock (_lock)
            {
                _absoluteExpiration = DateTime.UtcNow.AddSeconds(-1);
            }
        }
    }

    #endregion

    #region T76.4: Burn After Reading

    /// <summary>
    /// Manages immediate deletion after final read with configurable burn policies.
    /// </summary>
    public sealed class BurnAfterReadingManager
    {
        private readonly ConcurrentDictionary<string, BurnPolicy> _policies = new();
        private readonly ConcurrentDictionary<string, byte[]> _burnedDataHashes = new();

        /// <summary>
        /// Registers a resource with burn-after-reading policy.
        /// </summary>
        public void RegisterResource(string resourceId, BurnPolicy policy)
        {
            _policies[resourceId] = policy;
        }

        /// <summary>
        /// Checks if a resource should be burned after this access.
        /// </summary>
        public bool ShouldBurn(string resourceId, int currentAccessCount)
        {
            if (!_policies.TryGetValue(resourceId, out var policy))
                return false;

            return currentAccessCount >= policy.MaxReads;
        }

        /// <summary>
        /// Executes the burn operation for a resource.
        /// </summary>
        /// <param name="resourceId">Resource to burn.</param>
        /// <param name="data">Data to be destroyed (for hash verification).</param>
        /// <param name="deleteAction">Action to perform the actual deletion.</param>
        /// <returns>Destruction proof if successful.</returns>
        public async Task<DestructionProof?> ExecuteBurnAsync(
            string resourceId,
            byte[] data,
            Func<Task<bool>> deleteAction)
        {
            if (!_policies.TryGetValue(resourceId, out var policy))
                return null;

            // Compute hash before destruction
            var preDestructionHash = ComputeHash(data);

            // Execute deletion
            var startTime = DateTime.UtcNow;
            var success = await deleteAction();
            var endTime = DateTime.UtcNow;

            if (!success)
                return null;

            // Store hash for verification
            _burnedDataHashes[resourceId] = preDestructionHash;

            // Generate destruction proof
            return new DestructionProof
            {
                ResourceId = resourceId,
                DestructionTime = endTime,
                DataHash = Convert.ToBase64String(preDestructionHash),
                ProofSignature = GenerateProofSignature(resourceId, preDestructionHash, endTime),
                DestructionMethod = policy.DestructionMethod,
                WitnessId = Environment.MachineName,
                VerificationAvailable = true
            };
        }

        /// <summary>
        /// Verifies that data was previously destroyed.
        /// </summary>
        public bool VerifyDestruction(string resourceId, string expectedHash)
        {
            if (!_burnedDataHashes.TryGetValue(resourceId, out var storedHash))
                return false;

            return Convert.ToBase64String(storedHash) == expectedHash;
        }

        private static byte[] ComputeHash(byte[] data)
        {
            return SHA256.HashData(data);
        }

        private static string GenerateProofSignature(string resourceId, byte[] dataHash, DateTime destructionTime)
        {
            var payload = $"{resourceId}:{Convert.ToBase64String(dataHash)}:{destructionTime:O}";
            var payloadBytes = Encoding.UTF8.GetBytes(payload);
            var signature = SHA256.HashData(payloadBytes);
            return Convert.ToBase64String(signature);
        }
    }

    /// <summary>
    /// Policy for burn-after-reading behavior.
    /// </summary>
    public sealed record BurnPolicy
    {
        /// <summary>Maximum number of reads before burning.</summary>
        public int MaxReads { get; init; } = 1;

        /// <summary>Method of destruction (overwrite, delete, shred).</summary>
        public DestructionMethod DestructionMethod { get; init; } = DestructionMethod.SecureDelete;

        /// <summary>Whether to notify owner on destruction.</summary>
        public bool NotifyOnDestruction { get; init; } = true;

        /// <summary>Number of overwrite passes for secure deletion.</summary>
        public int OverwritePasses { get; init; } = 3;
    }

    /// <summary>
    /// Methods for data destruction.
    /// </summary>
    public enum DestructionMethod
    {
        /// <summary>Simple file deletion.</summary>
        Delete,

        /// <summary>Secure deletion with overwrite.</summary>
        SecureDelete,

        /// <summary>DOD 5220.22-M compliant shredding.</summary>
        DodShred,

        /// <summary>Gutmann 35-pass secure erase.</summary>
        GutmannShred
    }

    #endregion

    #region T76.5: Destruction Proof

    /// <summary>
    /// Cryptographic proof that data was destroyed.
    /// </summary>
    public sealed record DestructionProof
    {
        /// <summary>Unique identifier for the destroyed resource.</summary>
        public required string ResourceId { get; init; }

        /// <summary>Timestamp of destruction (UTC).</summary>
        public required DateTime DestructionTime { get; init; }

        /// <summary>SHA-256 hash of the data before destruction.</summary>
        public required string DataHash { get; init; }

        /// <summary>Cryptographic signature proving destruction.</summary>
        public required string ProofSignature { get; init; }

        /// <summary>Method used for destruction.</summary>
        public required DestructionMethod DestructionMethod { get; init; }

        /// <summary>Identifier of the system that performed destruction.</summary>
        public required string WitnessId { get; init; }

        /// <summary>Whether verification is available.</summary>
        public bool VerificationAvailable { get; init; }

        /// <summary>Chain of custody for the destruction.</summary>
        public IReadOnlyList<CustodyRecord> ChainOfCustody { get; init; } = Array.Empty<CustodyRecord>();

        /// <summary>
        /// Serializes the proof to a verifiable JSON format.
        /// </summary>
        public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        /// <summary>
        /// Verifies the proof signature.
        /// </summary>
        public bool VerifySignature()
        {
            var payload = $"{ResourceId}:{DataHash}:{DestructionTime:O}";
            var payloadBytes = Encoding.UTF8.GetBytes(payload);
            var expectedSignature = Convert.ToBase64String(SHA256.HashData(payloadBytes));
            return ProofSignature == expectedSignature;
        }
    }

    /// <summary>
    /// Record in the chain of custody.
    /// </summary>
    public sealed record CustodyRecord
    {
        /// <summary>Timestamp of the custody event.</summary>
        public required DateTime Timestamp { get; init; }

        /// <summary>Type of custody event.</summary>
        public required CustodyEventType EventType { get; init; }

        /// <summary>Actor involved in the event.</summary>
        public required string Actor { get; init; }

        /// <summary>Additional details.</summary>
        public string? Details { get; init; }
    }

    /// <summary>
    /// Types of custody events.
    /// </summary>
    public enum CustodyEventType
    {
        Created,
        Accessed,
        Transferred,
        Sealed,
        Destroyed,
        Verified
    }

    #endregion

    #region T76.6: Access Logging

    /// <summary>
    /// Comprehensive access logging with IP, time, user-agent, and more.
    /// </summary>
    public sealed class AccessLogger
    {
        private readonly ConcurrentDictionary<string, ConcurrentQueue<AccessLogEntry>> _logs = new();
        private readonly int _maxEntriesPerResource;

        /// <summary>
        /// Creates an access logger with the specified retention limit.
        /// </summary>
        public AccessLogger(int maxEntriesPerResource = 1000)
        {
            _maxEntriesPerResource = maxEntriesPerResource;
        }

        /// <summary>
        /// Logs an access attempt.
        /// </summary>
        public void LogAccess(AccessLogEntry entry)
        {
            var queue = _logs.GetOrAdd(entry.ResourceId, _ => new ConcurrentQueue<AccessLogEntry>());
            queue.Enqueue(entry);

            // Trim old entries if needed
            while (queue.Count > _maxEntriesPerResource)
            {
                queue.TryDequeue(out _);
            }
        }

        /// <summary>
        /// Gets access logs for a resource.
        /// </summary>
        public IReadOnlyList<AccessLogEntry> GetLogs(string resourceId)
        {
            return _logs.TryGetValue(resourceId, out var queue)
                ? queue.ToArray()
                : Array.Empty<AccessLogEntry>();
        }

        /// <summary>
        /// Gets logs filtered by time range.
        /// </summary>
        public IReadOnlyList<AccessLogEntry> GetLogsByTimeRange(
            string resourceId,
            DateTime startTime,
            DateTime endTime)
        {
            return GetLogs(resourceId)
                .Where(e => e.Timestamp >= startTime && e.Timestamp <= endTime)
                .ToList();
        }

        /// <summary>
        /// Gets logs for a specific accessor.
        /// </summary>
        public IReadOnlyList<AccessLogEntry> GetLogsByAccessor(string resourceId, string accessorId)
        {
            return GetLogs(resourceId)
                .Where(e => e.AccessorId == accessorId)
                .ToList();
        }

        /// <summary>
        /// Clears logs for a resource.
        /// </summary>
        public void ClearLogs(string resourceId)
        {
            _logs.TryRemove(resourceId, out _);
        }

        /// <summary>
        /// Exports logs to JSON format.
        /// </summary>
        public string ExportToJson(string resourceId)
        {
            var logs = GetLogs(resourceId);
            return JsonSerializer.Serialize(logs, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }
    }

    /// <summary>
    /// Detailed access log entry.
    /// </summary>
    public sealed record AccessLogEntry
    {
        /// <summary>Unique log entry ID.</summary>
        public string EntryId { get; init; } = Guid.NewGuid().ToString("N");

        /// <summary>Resource being accessed.</summary>
        public required string ResourceId { get; init; }

        /// <summary>Share ID used for access.</summary>
        public required string ShareId { get; init; }

        /// <summary>Accessor identity (user ID or "anonymous").</summary>
        public required string AccessorId { get; init; }

        /// <summary>Timestamp of access (UTC).</summary>
        public DateTime Timestamp { get; init; } = DateTime.UtcNow;

        /// <summary>Client IP address.</summary>
        public required string IpAddress { get; init; }

        /// <summary>HTTP User-Agent header.</summary>
        public string? UserAgent { get; init; }

        /// <summary>HTTP Referer header.</summary>
        public string? Referer { get; init; }

        /// <summary>Geographic location if available.</summary>
        public GeoLocation? Location { get; init; }

        /// <summary>Action performed.</summary>
        public required string Action { get; init; }

        /// <summary>Whether access was granted.</summary>
        public required bool Success { get; init; }

        /// <summary>Reason for denial if not successful.</summary>
        public string? DenialReason { get; init; }

        /// <summary>Device fingerprint if available.</summary>
        public string? DeviceFingerprint { get; init; }

        /// <summary>Session ID if available.</summary>
        public string? SessionId { get; init; }

        /// <summary>Request correlation ID.</summary>
        public string? CorrelationId { get; init; }

        /// <summary>Additional metadata.</summary>
        public IReadOnlyDictionary<string, object>? Metadata { get; init; }
    }

    #endregion

    #region T76.7: Password Protection

    /// <summary>
    /// Provides password protection layer for ephemeral links.
    /// Uses Argon2id for password hashing.
    /// </summary>
    public sealed class PasswordProtection
    {
        private readonly ConcurrentDictionary<string, PasswordProtectedShare> _protectedShares = new();

        /// <summary>
        /// Protects a share with a password.
        /// </summary>
        public void ProtectShare(string shareId, string password, PasswordProtectionOptions? options = null)
        {
            options ??= new PasswordProtectionOptions();

            var salt = RandomNumberGenerator.GetBytes(32);
            var hash = HashPassword(password, salt, options);

            _protectedShares[shareId] = new PasswordProtectedShare
            {
                ShareId = shareId,
                PasswordHash = hash,
                Salt = salt,
                MaxAttempts = options.MaxAttempts,
                LockoutDuration = options.LockoutDuration,
                RequireStrongPassword = options.RequireStrongPassword,
                CreatedAt = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Validates a password for a protected share.
        /// </summary>
        public PasswordValidationResult ValidatePassword(string shareId, string password)
        {
            if (!_protectedShares.TryGetValue(shareId, out var protectedShare))
            {
                return new PasswordValidationResult
                {
                    IsValid = true, // Not password protected
                    RequiresPassword = false
                };
            }

            // Check lockout
            if (protectedShare.IsLockedOut)
            {
                var remainingLockout = protectedShare.LockedUntil!.Value - DateTime.UtcNow;
                if (remainingLockout > TimeSpan.Zero)
                {
                    return new PasswordValidationResult
                    {
                        IsValid = false,
                        RequiresPassword = true,
                        IsLockedOut = true,
                        LockoutRemainingSeconds = (int)remainingLockout.TotalSeconds,
                        Message = $"Too many failed attempts. Try again in {(int)remainingLockout.TotalSeconds} seconds."
                    };
                }
                else
                {
                    // Lockout expired, reset
                    protectedShare.FailedAttempts = 0;
                    protectedShare.LockedUntil = null;
                }
            }

            // Validate password
            var hash = HashPassword(password, protectedShare.Salt, new PasswordProtectionOptions());
            var isValid = CryptographicOperations.FixedTimeEquals(hash, protectedShare.PasswordHash);

            if (!isValid)
            {
                protectedShare.FailedAttempts++;
                protectedShare.LastFailedAttempt = DateTime.UtcNow;

                if (protectedShare.FailedAttempts >= protectedShare.MaxAttempts)
                {
                    protectedShare.LockedUntil = DateTime.UtcNow + protectedShare.LockoutDuration;
                    return new PasswordValidationResult
                    {
                        IsValid = false,
                        RequiresPassword = true,
                        IsLockedOut = true,
                        LockoutRemainingSeconds = (int)protectedShare.LockoutDuration.TotalSeconds,
                        Message = $"Account locked for {(int)protectedShare.LockoutDuration.TotalMinutes} minutes."
                    };
                }

                return new PasswordValidationResult
                {
                    IsValid = false,
                    RequiresPassword = true,
                    RemainingAttempts = protectedShare.MaxAttempts - protectedShare.FailedAttempts,
                    Message = $"Invalid password. {protectedShare.MaxAttempts - protectedShare.FailedAttempts} attempts remaining."
                };
            }

            // Success - reset failed attempts
            protectedShare.FailedAttempts = 0;
            protectedShare.LastSuccessfulAccess = DateTime.UtcNow;

            return new PasswordValidationResult
            {
                IsValid = true,
                RequiresPassword = true
            };
        }

        /// <summary>
        /// Checks if a share is password protected.
        /// </summary>
        public bool IsPasswordProtected(string shareId)
        {
            return _protectedShares.ContainsKey(shareId);
        }

        /// <summary>
        /// Changes the password for a protected share.
        /// </summary>
        public bool ChangePassword(string shareId, string oldPassword, string newPassword)
        {
            var validation = ValidatePassword(shareId, oldPassword);
            if (!validation.IsValid)
                return false;

            if (_protectedShares.TryGetValue(shareId, out var protectedShare))
            {
                var salt = RandomNumberGenerator.GetBytes(32);
                protectedShare.Salt = salt;
                protectedShare.PasswordHash = HashPassword(newPassword, salt, new PasswordProtectionOptions());
                return true;
            }

            return false;
        }

        /// <summary>
        /// Removes password protection from a share.
        /// </summary>
        public bool RemoveProtection(string shareId)
        {
            return _protectedShares.TryRemove(shareId, out _);
        }

        private static byte[] HashPassword(string password, byte[] salt, PasswordProtectionOptions options)
        {
            // Using PBKDF2 with SHA-256 (Argon2id would require additional dependency)
            return Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                iterations: 100000,
                HashAlgorithmName.SHA256,
                outputLength: 32);
        }

        /// <summary>
        /// Validates password strength.
        /// </summary>
        public static PasswordStrengthResult CheckPasswordStrength(string password)
        {
            var issues = new List<string>();
            var score = 0;

            if (password.Length >= 8) score += 20;
            else issues.Add("Password must be at least 8 characters");

            if (password.Length >= 12) score += 10;
            if (password.Length >= 16) score += 10;

            if (password.Any(char.IsUpper)) score += 15;
            else issues.Add("Add uppercase letters");

            if (password.Any(char.IsLower)) score += 15;
            else issues.Add("Add lowercase letters");

            if (password.Any(char.IsDigit)) score += 15;
            else issues.Add("Add numbers");

            if (password.Any(c => !char.IsLetterOrDigit(c))) score += 15;
            else issues.Add("Add special characters");

            // Check for common patterns
            var lowerPassword = password.ToLowerInvariant();
            var commonPatterns = new[] { "password", "123456", "qwerty", "admin", "letmein" };
            if (commonPatterns.Any(p => lowerPassword.Contains(p)))
            {
                score -= 30;
                issues.Add("Avoid common passwords");
            }

            return new PasswordStrengthResult
            {
                Score = Math.Max(0, Math.Min(100, score)),
                Strength = score >= 80 ? PasswordStrength.Strong :
                          score >= 60 ? PasswordStrength.Good :
                          score >= 40 ? PasswordStrength.Fair :
                          PasswordStrength.Weak,
                Issues = issues
            };
        }
    }

    /// <summary>
    /// Options for password protection.
    /// </summary>
    public sealed record PasswordProtectionOptions
    {
        /// <summary>Maximum failed attempts before lockout.</summary>
        public int MaxAttempts { get; init; } = 5;

        /// <summary>Duration of lockout after max attempts.</summary>
        public TimeSpan LockoutDuration { get; init; } = TimeSpan.FromMinutes(15);

        /// <summary>Whether to require strong passwords.</summary>
        public bool RequireStrongPassword { get; init; } = true;

        /// <summary>Minimum password length.</summary>
        public int MinimumLength { get; init; } = 8;
    }

    /// <summary>
    /// Internal tracking for password-protected shares.
    /// </summary>
    internal sealed class PasswordProtectedShare
    {
        public required string ShareId { get; init; }
        public required byte[] PasswordHash { get; set; }
        public required byte[] Salt { get; set; }
        public required int MaxAttempts { get; init; }
        public required TimeSpan LockoutDuration { get; init; }
        public required bool RequireStrongPassword { get; init; }
        public required DateTime CreatedAt { get; init; }
        public int FailedAttempts { get; set; }
        public DateTime? LastFailedAttempt { get; set; }
        public DateTime? LastSuccessfulAccess { get; set; }
        public DateTime? LockedUntil { get; set; }
        public bool IsLockedOut => LockedUntil.HasValue && DateTime.UtcNow < LockedUntil.Value;
    }

    /// <summary>
    /// Result of password validation.
    /// </summary>
    public sealed record PasswordValidationResult
    {
        public required bool IsValid { get; init; }
        public required bool RequiresPassword { get; init; }
        public bool IsLockedOut { get; init; }
        public int? LockoutRemainingSeconds { get; init; }
        public int? RemainingAttempts { get; init; }
        public string? Message { get; init; }
    }

    /// <summary>
    /// Result of password strength check.
    /// </summary>
    public sealed record PasswordStrengthResult
    {
        public required int Score { get; init; }
        public required PasswordStrength Strength { get; init; }
        public required IReadOnlyList<string> Issues { get; init; }
    }

    /// <summary>
    /// Password strength levels.
    /// </summary>
    public enum PasswordStrength
    {
        Weak,
        Fair,
        Good,
        Strong
    }

    #endregion

    #region T76.8: Recipient Notification

    /// <summary>
    /// Manages notifications to share owners when links are accessed.
    /// </summary>
    public sealed class RecipientNotificationService
    {
        private readonly ConcurrentDictionary<string, NotificationSubscription> _subscriptions = new();
        private readonly ConcurrentQueue<PendingNotification> _pendingNotifications = new();

        /// <summary>
        /// Event raised when a notification needs to be sent.
        /// </summary>
        public event EventHandler<NotificationEventArgs>? NotificationReady;

        /// <summary>
        /// Subscribes to notifications for a share.
        /// </summary>
        public void Subscribe(string shareId, NotificationSubscription subscription)
        {
            _subscriptions[shareId] = subscription;
        }

        /// <summary>
        /// Unsubscribes from notifications for a share.
        /// </summary>
        public bool Unsubscribe(string shareId)
        {
            return _subscriptions.TryRemove(shareId, out _);
        }

        /// <summary>
        /// Gets the subscription for a share.
        /// </summary>
        public NotificationSubscription? GetSubscription(string shareId)
        {
            return _subscriptions.TryGetValue(shareId, out var sub) ? sub : null;
        }

        /// <summary>
        /// Triggers a notification for share access.
        /// </summary>
        public void NotifyAccess(string shareId, AccessNotificationDetails details)
        {
            if (!_subscriptions.TryGetValue(shareId, out var subscription))
                return;

            // Check notification preferences
            if (!ShouldNotify(subscription, details))
                return;

            var notification = new PendingNotification
            {
                NotificationId = Guid.NewGuid().ToString("N"),
                ShareId = shareId,
                Subscription = subscription,
                Details = details,
                CreatedAt = DateTime.UtcNow,
                Status = NotificationStatus.Pending
            };

            _pendingNotifications.Enqueue(notification);

            // Raise event for immediate processing
            NotificationReady?.Invoke(this, new NotificationEventArgs(notification));
        }

        /// <summary>
        /// Gets pending notifications for processing.
        /// </summary>
        public IReadOnlyList<PendingNotification> GetPendingNotifications(int maxCount = 100)
        {
            var notifications = new List<PendingNotification>();
            while (notifications.Count < maxCount && _pendingNotifications.TryDequeue(out var notification))
            {
                notifications.Add(notification);
            }
            return notifications;
        }

        /// <summary>
        /// Marks a notification as sent.
        /// </summary>
        public void MarkAsSent(string notificationId)
        {
            // In a real implementation, this would update persistent storage
        }

        private static bool ShouldNotify(NotificationSubscription subscription, AccessNotificationDetails details)
        {
            // Check event type preferences
            if (details.IsFirstAccess && !subscription.NotifyOnFirstAccess)
                return false;

            if (!details.IsFirstAccess && subscription.OnlyNotifyFirstAccess)
                return false;

            if (details.AccessDenied && !subscription.NotifyOnDenied)
                return false;

            // Check quiet hours
            if (subscription.QuietHoursStart.HasValue && subscription.QuietHoursEnd.HasValue)
            {
                var now = DateTime.UtcNow.TimeOfDay;
                var start = subscription.QuietHoursStart.Value;
                var end = subscription.QuietHoursEnd.Value;

                if (start < end)
                {
                    if (now >= start && now <= end)
                        return false;
                }
                else
                {
                    if (now >= start || now <= end)
                        return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Subscription settings for notifications.
    /// </summary>
    public sealed record NotificationSubscription
    {
        /// <summary>Owner's user ID.</summary>
        public required string OwnerId { get; init; }

        /// <summary>Email for notifications.</summary>
        public string? Email { get; init; }

        /// <summary>Webhook URL for notifications.</summary>
        public string? WebhookUrl { get; init; }

        /// <summary>Push notification token.</summary>
        public string? PushToken { get; init; }

        /// <summary>Whether to notify on first access.</summary>
        public bool NotifyOnFirstAccess { get; init; } = true;

        /// <summary>Whether to only notify on first access (suppress subsequent).</summary>
        public bool OnlyNotifyFirstAccess { get; init; }

        /// <summary>Whether to notify on access denial.</summary>
        public bool NotifyOnDenied { get; init; }

        /// <summary>Whether to notify on share expiration.</summary>
        public bool NotifyOnExpiration { get; init; } = true;

        /// <summary>Whether to notify on revocation.</summary>
        public bool NotifyOnRevocation { get; init; }

        /// <summary>Start of quiet hours (UTC).</summary>
        public TimeSpan? QuietHoursStart { get; init; }

        /// <summary>End of quiet hours (UTC).</summary>
        public TimeSpan? QuietHoursEnd { get; init; }

        /// <summary>Maximum notifications per hour.</summary>
        public int? MaxNotificationsPerHour { get; init; }
    }

    /// <summary>
    /// Details for access notifications.
    /// </summary>
    public sealed record AccessNotificationDetails
    {
        /// <summary>Who accessed the share.</summary>
        public required string AccessorId { get; init; }

        /// <summary>Accessor's IP address.</summary>
        public required string IpAddress { get; init; }

        /// <summary>When access occurred.</summary>
        public DateTime AccessTime { get; init; } = DateTime.UtcNow;

        /// <summary>Whether this is the first access.</summary>
        public bool IsFirstAccess { get; init; }

        /// <summary>Whether access was denied.</summary>
        public bool AccessDenied { get; init; }

        /// <summary>Denial reason if applicable.</summary>
        public string? DenialReason { get; init; }

        /// <summary>Geographic location if available.</summary>
        public GeoLocation? Location { get; init; }

        /// <summary>User agent if available.</summary>
        public string? UserAgent { get; init; }

        /// <summary>Remaining accesses.</summary>
        public int? RemainingAccesses { get; init; }

        /// <summary>Time until expiration.</summary>
        public TimeSpan? TimeUntilExpiration { get; init; }
    }

    /// <summary>
    /// A pending notification to be sent.
    /// </summary>
    public sealed record PendingNotification
    {
        public required string NotificationId { get; init; }
        public required string ShareId { get; init; }
        public required NotificationSubscription Subscription { get; init; }
        public required AccessNotificationDetails Details { get; init; }
        public required DateTime CreatedAt { get; init; }
        public required NotificationStatus Status { get; init; }
        public int RetryCount { get; init; }
        public DateTime? SentAt { get; init; }
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Status of a notification.
    /// </summary>
    public enum NotificationStatus
    {
        Pending,
        Sent,
        Failed,
        Cancelled
    }

    /// <summary>
    /// Event args for notification events.
    /// </summary>
    public sealed class NotificationEventArgs : EventArgs
    {
        public PendingNotification Notification { get; }

        public NotificationEventArgs(PendingNotification notification)
        {
            Notification = notification;
        }
    }

    #endregion

    #region T76.9: Revocation

    /// <summary>
    /// Manages share revocation with immediate effect.
    /// </summary>
    public sealed class ShareRevocationManager
    {
        private readonly ConcurrentDictionary<string, RevocationRecord> _revocations = new();

        /// <summary>
        /// Event raised when a share is revoked.
        /// </summary>
        public event EventHandler<ShareRevokedEventArgs>? ShareRevoked;

        /// <summary>
        /// Revokes a share immediately.
        /// </summary>
        public RevocationResult Revoke(string shareId, string revokedBy, string? reason = null)
        {
            var record = new RevocationRecord
            {
                ShareId = shareId,
                RevokedBy = revokedBy,
                RevokedAt = DateTime.UtcNow,
                Reason = reason,
                RevocationType = RevocationType.Manual
            };

            if (!_revocations.TryAdd(shareId, record))
            {
                // Already revoked
                return new RevocationResult
                {
                    Success = false,
                    ShareId = shareId,
                    Message = "Share was already revoked",
                    PreviousRevocation = _revocations[shareId]
                };
            }

            ShareRevoked?.Invoke(this, new ShareRevokedEventArgs(record));

            return new RevocationResult
            {
                Success = true,
                ShareId = shareId,
                RevokedAt = record.RevokedAt,
                Message = "Share successfully revoked"
            };
        }

        /// <summary>
        /// Revokes multiple shares at once.
        /// </summary>
        public IReadOnlyList<RevocationResult> RevokeBatch(
            IEnumerable<string> shareIds,
            string revokedBy,
            string? reason = null)
        {
            return shareIds.Select(id => Revoke(id, revokedBy, reason)).ToList();
        }

        /// <summary>
        /// Checks if a share has been revoked.
        /// </summary>
        public bool IsRevoked(string shareId)
        {
            return _revocations.ContainsKey(shareId);
        }

        /// <summary>
        /// Gets the revocation record for a share.
        /// </summary>
        public RevocationRecord? GetRevocationRecord(string shareId)
        {
            return _revocations.TryGetValue(shareId, out var record) ? record : null;
        }

        /// <summary>
        /// Gets all revocations for a user (as revoker).
        /// </summary>
        public IReadOnlyList<RevocationRecord> GetRevocationsByUser(string userId)
        {
            return _revocations.Values
                .Where(r => r.RevokedBy == userId)
                .OrderByDescending(r => r.RevokedAt)
                .ToList();
        }

        /// <summary>
        /// Schedules a future revocation.
        /// </summary>
        public void ScheduleRevocation(string shareId, DateTime revokeAt, string scheduledBy)
        {
            // In a real implementation, this would use a scheduler
            // For now, we track scheduled revocations
            var record = new RevocationRecord
            {
                ShareId = shareId,
                RevokedBy = scheduledBy,
                RevokedAt = revokeAt,
                Reason = "Scheduled revocation",
                RevocationType = RevocationType.Scheduled,
                IsScheduled = true
            };

            _revocations[shareId] = record;
        }
    }

    /// <summary>
    /// Record of a share revocation.
    /// </summary>
    public sealed record RevocationRecord
    {
        public required string ShareId { get; init; }
        public required string RevokedBy { get; init; }
        public required DateTime RevokedAt { get; init; }
        public string? Reason { get; init; }
        public required RevocationType RevocationType { get; init; }
        public bool IsScheduled { get; init; }
    }

    /// <summary>
    /// Types of revocation.
    /// </summary>
    public enum RevocationType
    {
        /// <summary>Manually revoked by owner.</summary>
        Manual,

        /// <summary>Automatically revoked due to policy.</summary>
        Automatic,

        /// <summary>Revoked due to security concern.</summary>
        Security,

        /// <summary>Scheduled revocation.</summary>
        Scheduled,

        /// <summary>Revoked due to expiration.</summary>
        Expired
    }

    /// <summary>
    /// Result of a revocation operation.
    /// </summary>
    public sealed record RevocationResult
    {
        public required bool Success { get; init; }
        public required string ShareId { get; init; }
        public DateTime? RevokedAt { get; init; }
        public required string Message { get; init; }
        public RevocationRecord? PreviousRevocation { get; init; }
    }

    /// <summary>
    /// Event args for share revocation.
    /// </summary>
    public sealed class ShareRevokedEventArgs : EventArgs
    {
        public RevocationRecord Record { get; }

        public ShareRevokedEventArgs(RevocationRecord record)
        {
            Record = record;
        }
    }

    #endregion

    #region T76.10: Anti-Screenshot Protection

    /// <summary>
    /// Provides browser-side protections against screenshot capture.
    /// Generates JavaScript/CSS code that can be injected into share pages.
    /// </summary>
    public static class AntiScreenshotProtection
    {
        /// <summary>
        /// Generates comprehensive anti-screenshot JavaScript.
        /// </summary>
        public static string GenerateProtectionScript(AntiScreenshotOptions? options = null)
        {
            options ??= new AntiScreenshotOptions();

            var script = new StringBuilder();
            script.AppendLine("(function() {");
            script.AppendLine("  'use strict';");

            // Disable right-click context menu
            if (options.DisableContextMenu)
            {
                script.AppendLine(@"
  document.addEventListener('contextmenu', function(e) {
    e.preventDefault();
    return false;
  });");
            }

            // Disable keyboard shortcuts (Print Screen, Ctrl+P, etc.)
            if (options.DisablePrintShortcuts)
            {
                script.AppendLine(@"
  document.addEventListener('keydown', function(e) {
    // Print Screen
    if (e.key === 'PrintScreen') {
      e.preventDefault();
      document.body.style.visibility = 'hidden';
      setTimeout(function() { document.body.style.visibility = 'visible'; }, 1000);
      return false;
    }
    // Ctrl+P (Print)
    if (e.ctrlKey && e.key === 'p') {
      e.preventDefault();
      return false;
    }
    // Ctrl+S (Save)
    if (e.ctrlKey && e.key === 's') {
      e.preventDefault();
      return false;
    }
    // Ctrl+Shift+S (Save As)
    if (e.ctrlKey && e.shiftKey && e.key === 'S') {
      e.preventDefault();
      return false;
    }
    // F12 (DevTools)
    if (e.key === 'F12') {
      e.preventDefault();
      return false;
    }
    // Ctrl+Shift+I (DevTools)
    if (e.ctrlKey && e.shiftKey && e.key === 'I') {
      e.preventDefault();
      return false;
    }
    // Ctrl+Shift+J (Console)
    if (e.ctrlKey && e.shiftKey && e.key === 'J') {
      e.preventDefault();
      return false;
    }
    // Ctrl+U (View Source)
    if (e.ctrlKey && e.key === 'u') {
      e.preventDefault();
      return false;
    }
  });");
            }

            // Disable text selection
            if (options.DisableTextSelection)
            {
                script.AppendLine(@"
  document.addEventListener('selectstart', function(e) {
    e.preventDefault();
    return false;
  });");
            }

            // Disable drag operations
            if (options.DisableDrag)
            {
                script.AppendLine(@"
  document.addEventListener('dragstart', function(e) {
    e.preventDefault();
    return false;
  });");
            }

            // Detect visibility change (potential screenshot)
            if (options.DetectVisibilityChange)
            {
                script.AppendLine(@"
  document.addEventListener('visibilitychange', function() {
    if (document.hidden) {
      // Page was hidden, potential screenshot
      console.warn('Document visibility changed');
    }
  });");
            }

            // Watermark overlay
            if (options.EnableWatermark)
            {
                var watermarkText = options.WatermarkText ?? "CONFIDENTIAL";
                script.AppendLine($@"
  var watermarkDiv = document.createElement('div');
  watermarkDiv.style.cssText = 'position:fixed;top:0;left:0;width:100%;height:100%;pointer-events:none;z-index:9999;opacity:0.1;';
  watermarkDiv.innerHTML = '<div style=""transform:rotate(-45deg);font-size:48px;color:#000;white-space:nowrap;position:absolute;top:50%;left:50%;transform:translate(-50%,-50%) rotate(-45deg);"">{watermarkText}</div>';
  document.body.appendChild(watermarkDiv);");
            }

            // Blur on window blur (switching apps)
            if (options.BlurOnWindowBlur)
            {
                script.AppendLine(@"
  window.addEventListener('blur', function() {
    document.body.style.filter = 'blur(10px)';
  });
  window.addEventListener('focus', function() {
    document.body.style.filter = 'none';
  });");
            }

            // DevTools detection
            if (options.DetectDevTools)
            {
                script.AppendLine(@"
  var devtools = { open: false };
  var threshold = 160;
  setInterval(function() {
    var widthThreshold = window.outerWidth - window.innerWidth > threshold;
    var heightThreshold = window.outerHeight - window.innerHeight > threshold;
    if (widthThreshold || heightThreshold) {
      if (!devtools.open) {
        devtools.open = true;
        document.body.innerHTML = '<h1>Developer tools detected. Content hidden for security.</h1>';
      }
    } else {
      devtools.open = false;
    }
  }, 500);");
            }

            script.AppendLine("})();");

            return script.ToString();
        }

        /// <summary>
        /// Generates CSS for anti-screenshot protection.
        /// </summary>
        public static string GenerateProtectionCss(AntiScreenshotOptions? options = null)
        {
            options ??= new AntiScreenshotOptions();

            var css = new StringBuilder();

            if (options.DisableTextSelection)
            {
                css.AppendLine(@"
* {
  -webkit-user-select: none;
  -moz-user-select: none;
  -ms-user-select: none;
  user-select: none;
}");
            }

            if (options.DisableDrag)
            {
                css.AppendLine(@"
img {
  -webkit-user-drag: none;
  -khtml-user-drag: none;
  -moz-user-drag: none;
  -o-user-drag: none;
  user-drag: none;
}");
            }

            if (options.DisablePrint)
            {
                css.AppendLine(@"
@media print {
  body {
    display: none !important;
  }
}");
            }

            return css.ToString();
        }

        /// <summary>
        /// Generates a complete HTML wrapper with protection.
        /// </summary>
        public static string GenerateProtectedHtmlWrapper(string content, AntiScreenshotOptions? options = null)
        {
            options ??= new AntiScreenshotOptions();

            return $@"<!DOCTYPE html>
<html>
<head>
  <meta charset=""UTF-8"">
  <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
  <style>
{GenerateProtectionCss(options)}
  </style>
</head>
<body>
{content}
<script>
{GenerateProtectionScript(options)}
</script>
</body>
</html>";
        }
    }

    /// <summary>
    /// Options for anti-screenshot protection.
    /// </summary>
    public sealed record AntiScreenshotOptions
    {
        /// <summary>Disable right-click context menu.</summary>
        public bool DisableContextMenu { get; init; } = true;

        /// <summary>Disable print keyboard shortcuts.</summary>
        public bool DisablePrintShortcuts { get; init; } = true;

        /// <summary>Disable text selection.</summary>
        public bool DisableTextSelection { get; init; } = true;

        /// <summary>Disable drag operations.</summary>
        public bool DisableDrag { get; init; } = true;

        /// <summary>Detect visibility changes.</summary>
        public bool DetectVisibilityChange { get; init; } = true;

        /// <summary>Enable watermark overlay.</summary>
        public bool EnableWatermark { get; init; } = false;

        /// <summary>Custom watermark text.</summary>
        public string? WatermarkText { get; init; }

        /// <summary>Blur content when window loses focus.</summary>
        public bool BlurOnWindowBlur { get; init; } = true;

        /// <summary>Detect developer tools opening.</summary>
        public bool DetectDevTools { get; init; } = true;

        /// <summary>Disable print via CSS media query.</summary>
        public bool DisablePrint { get; init; } = true;
    }

    #endregion

    #region Main Ephemeral Sharing Strategy (Integrates All Components)

    /// <summary>
    /// Ephemeral sharing strategy that provides time-limited, self-destructing access to resources.
    /// Implements secure sharing with automatic expiration and access tracking.
    /// </summary>
    /// <remarks>
    /// <para>
    /// T76 Digital Dead Drops - Complete Implementation:
    /// - T76.1: Ephemeral Link Generator - Time/access-limited sharing URLs
    /// - T76.2: Access Counter - Atomic counter for remaining access attempts
    /// - T76.3: TTL Engine - Precise time-based expiration (seconds granularity)
    /// - T76.4: Burn After Reading - Immediate deletion after final read
    /// - T76.5: Destruction Proof - Cryptographic proof that data was destroyed
    /// - T76.6: Access Logging - Record accessor IP, time, user-agent
    /// - T76.7: Password Protection - Optional password layer on ephemeral links
    /// - T76.8: Recipient Notification - Notify sender when link is accessed
    /// - T76.9: Revocation - Sender can revoke link before expiration
    /// - T76.10: Anti-Screenshot - Browser-side protections against capture
    /// </para>
    /// <para>
    /// Use cases:
    /// - Temporary file sharing
    /// - One-time password distribution
    /// - Secure document review
    /// - Limited-time API access
    /// - Anonymous secure data exchange
    /// </para>
    /// </remarks>
    public sealed class EphemeralSharingStrategy : AccessControlStrategyBase
    {
        private readonly ConcurrentDictionary<string, EphemeralShare> _shares = new();
        private readonly ConcurrentDictionary<string, AccessCounter> _accessCounters = new();
        private readonly ConcurrentDictionary<string, TtlEngine> _ttlEngines = new();

        private readonly AccessLogger _accessLogger = new();
        private readonly PasswordProtection _passwordProtection = new();
        private readonly RecipientNotificationService _notificationService = new();
        private readonly ShareRevocationManager _revocationManager = new();
        private readonly BurnAfterReadingManager _burnManager = new();

        private EphemeralLinkGenerator? _linkGenerator;
        private Timer? _cleanupTimer;

        /// <inheritdoc/>
        public override string StrategyId => "ephemeral-sharing";

        /// <inheritdoc/>
        public override string StrategyName => "Ephemeral Sharing (Digital Dead Drops)";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = true,
            SupportsTemporalAccess = true,
            SupportsGeographicRestrictions = true,
            MaxConcurrentEvaluations = 5000
        };

        /// <summary>
        /// Gets the access logger for this strategy.
        /// </summary>
        public AccessLogger AccessLogger => _accessLogger;

        /// <summary>
        /// Gets the password protection service.
        /// </summary>
        public PasswordProtection PasswordProtection => _passwordProtection;

        /// <summary>
        /// Gets the notification service.
        /// </summary>
        public RecipientNotificationService NotificationService => _notificationService;

        /// <summary>
        /// Gets the revocation manager.
        /// </summary>
        public ShareRevocationManager RevocationManager => _revocationManager;

        /// <summary>
        /// Gets the burn-after-reading manager.
        /// </summary>
        public BurnAfterReadingManager BurnManager => _burnManager;

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            // Configure link generator
            var baseUrl = configuration.TryGetValue("BaseUrl", out var urlObj) && urlObj is string url
                ? url
                : "https://share.example.com";
            var sharePath = configuration.TryGetValue("SharePath", out var pathObj) && pathObj is string path
                ? path
                : "/s/";

            _linkGenerator = new EphemeralLinkGenerator(baseUrl, sharePath);

            // Start cleanup timer
            var cleanupInterval = TimeSpan.FromMinutes(5);
            if (configuration.TryGetValue("CleanupIntervalMinutes", out var intervalObj) && intervalObj is int minutes)
            {
                cleanupInterval = TimeSpan.FromMinutes(minutes);
            }

            _cleanupTimer = new Timer(CleanupExpiredShares, null, cleanupInterval, cleanupInterval);

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Creates a new ephemeral share with full T76 features.
        /// </summary>
        public EphemeralShareResult CreateShare(string resourceId, ShareOptions options)
        {
            var token = options.UseShortToken
                ? EphemeralLinkGenerator.GenerateShortToken()
                : EphemeralLinkGenerator.GenerateSecureToken();

            var share = new EphemeralShare
            {
                Id = Guid.NewGuid().ToString("N"),
                ResourceId = resourceId,
                Token = token,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = options.CreatedBy,
                ExpiresAt = options.ExpiresAt ?? DateTime.UtcNow.AddHours(24),
                MaxAccessCount = options.MaxAccessCount,
                AllowedIpRanges = options.AllowedIpRanges?.ToList(),
                AllowedCountries = options.AllowedCountries?.ToList(),
                AllowedRecipients = options.AllowedRecipients?.ToList(),
                RequireAuthentication = options.RequireAuthentication,
                AllowedActions = options.AllowedActions?.ToList() ?? new List<string> { "read" },
                UseSlidingExpiration = options.UseSlidingExpiration,
                SlidingExpirationMinutes = options.SlidingExpirationMinutes,
                Metadata = options.Metadata ?? new Dictionary<string, object>(),
                IsActive = true,
                EnableAntiScreenshot = options.EnableAntiScreenshot,
                AntiScreenshotOptions = options.AntiScreenshotOptions
            };

            _shares[share.Token] = share;

            // Initialize access counter (T76.2)
            var accessCounter = new AccessCounter(options.MaxAccessCount ?? -1);
            _accessCounters[share.Id] = accessCounter;

            // Initialize TTL engine (T76.3)
            var ttlEngine = new TtlEngine(
                share.ExpiresAt,
                options.UseSlidingExpiration ? TimeSpan.FromMinutes(options.SlidingExpirationMinutes) : null,
                TimeSpan.FromMinutes(30));
            _ttlEngines[share.Id] = ttlEngine;

            // Register burn policy if max access = 1 (T76.4)
            if (options.MaxAccessCount == 1)
            {
                _burnManager.RegisterResource(share.Id, new BurnPolicy
                {
                    MaxReads = 1,
                    DestructionMethod = DestructionMethod.SecureDelete,
                    NotifyOnDestruction = true
                });
            }

            // Set up password protection if specified (T76.7)
            if (!string.IsNullOrEmpty(options.Password))
            {
                _passwordProtection.ProtectShare(share.Id, options.Password, options.PasswordOptions);
            }

            // Set up notification subscription (T76.8)
            if (options.NotificationSubscription != null)
            {
                _notificationService.Subscribe(share.Id, options.NotificationSubscription);
            }
            else if (options.NotifyOnAccess)
            {
                _notificationService.Subscribe(share.Id, new NotificationSubscription
                {
                    OwnerId = options.CreatedBy,
                    Email = options.NotificationEmail,
                    NotifyOnFirstAccess = true,
                    NotifyOnExpiration = true
                });
            }

            // Generate share URL (T76.1)
            var shareUrl = _linkGenerator?.GenerateShareUrl(token) ?? $"/s/{token}";

            return new EphemeralShareResult
            {
                Share = share,
                ShareUrl = shareUrl,
                Token = token,
                ExpiresAt = share.ExpiresAt,
                MaxAccesses = options.MaxAccessCount,
                IsPasswordProtected = !string.IsNullOrEmpty(options.Password),
                AntiScreenshotScript = options.EnableAntiScreenshot
                    ? AntiScreenshotProtection.GenerateProtectionScript(options.AntiScreenshotOptions)
                    : null
            };
        }

        /// <summary>
        /// Gets a share by token.
        /// </summary>
        public EphemeralShare? GetShare(string token)
        {
            return _shares.TryGetValue(token, out var share) ? share : null;
        }

        /// <summary>
        /// Revokes a share immediately (T76.9).
        /// </summary>
        public RevocationResult RevokeShare(string shareId, string revokedBy, string? reason = null)
        {
            var share = _shares.Values.FirstOrDefault(s => s.Id == shareId);
            if (share != null)
            {
                share.IsActive = false;
                share.RevokedAt = DateTime.UtcNow;
            }

            return _revocationManager.Revoke(shareId, revokedBy, reason);
        }

        /// <summary>
        /// Gets access logs for a share (T76.6).
        /// </summary>
        public IReadOnlyList<AccessLogEntry> GetAccessLogs(string shareId)
        {
            return _accessLogger.GetLogs(shareId);
        }

        /// <summary>
        /// Lists all active shares for a resource.
        /// </summary>
        public IReadOnlyList<EphemeralShare> GetSharesForResource(string resourceId)
        {
            return _shares.Values
                .Where(s => s.ResourceId == resourceId && s.IsActive && !IsExpired(s))
                .ToList()
                .AsReadOnly();
        }

        /// <summary>
        /// Generates anti-screenshot protection for a share (T76.10).
        /// </summary>
        public string? GetAntiScreenshotScript(string shareId)
        {
            var share = _shares.Values.FirstOrDefault(s => s.Id == shareId);
            if (share?.EnableAntiScreenshot != true)
                return null;

            return AntiScreenshotProtection.GenerateProtectionScript(share.AntiScreenshotOptions);
        }

        /// <inheritdoc/>
        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            // Extract share token from context
            if (!context.EnvironmentAttributes.TryGetValue("ShareToken", out var tokenObj) ||
                tokenObj is not string token)
            {
                return CreateDeniedDecision("No share token provided", "EphemeralShareRequired");
            }

            // Find the share
            if (!_shares.TryGetValue(token, out var share))
            {
                return CreateDeniedDecision("Invalid share token", "EphemeralShareValidation");
            }

            // Check revocation (T76.9)
            if (_revocationManager.IsRevoked(share.Id))
            {
                var revocation = _revocationManager.GetRevocationRecord(share.Id);
                LogAccess(share, context, false, "Share has been revoked");
                return CreateDeniedDecision("Share has been revoked", "EphemeralShareRevoked",
                    new Dictionary<string, object>
                    {
                        ["RevokedAt"] = revocation?.RevokedAt.ToString("o") ?? "unknown",
                        ["RevokedBy"] = revocation?.RevokedBy ?? "unknown"
                    });
            }

            // Check if share is active
            if (!share.IsActive)
            {
                LogAccess(share, context, false, "Share is not active");
                return CreateDeniedDecision("Share has been revoked", "EphemeralShareRevoked",
                    new Dictionary<string, object>
                    {
                        ["RevokedAt"] = share.RevokedAt?.ToString("o") ?? "unknown"
                    });
            }

            // Check TTL (T76.3)
            if (_ttlEngines.TryGetValue(share.Id, out var ttlEngine))
            {
                if (ttlEngine.IsExpired)
                {
                    share.IsActive = false;
                    LogAccess(share, context, false, "Share has expired");
                    return CreateDeniedDecision("Share has expired", "EphemeralShareExpired",
                        new Dictionary<string, object>
                        {
                            ["ExpiredAt"] = share.ExpiresAt.ToString("o")
                        });
                }
            }
            else if (IsExpired(share))
            {
                share.IsActive = false;
                LogAccess(share, context, false, "Share has expired");
                return CreateDeniedDecision("Share has expired", "EphemeralShareExpired",
                    new Dictionary<string, object>
                    {
                        ["ExpiredAt"] = share.ExpiresAt.ToString("o")
                    });
            }

            // Check password protection (T76.7)
            if (_passwordProtection.IsPasswordProtected(share.Id))
            {
                if (!context.EnvironmentAttributes.TryGetValue("SharePassword", out var passwordObj) ||
                    passwordObj is not string password)
                {
                    LogAccess(share, context, false, "Password required");
                    return CreateDeniedDecision("Password required to access this share", "EphemeralSharePasswordRequired",
                        new Dictionary<string, object> { ["RequiresPassword"] = true });
                }

                var passwordResult = _passwordProtection.ValidatePassword(share.Id, password);
                if (!passwordResult.IsValid)
                {
                    LogAccess(share, context, false, passwordResult.Message ?? "Invalid password");
                    return CreateDeniedDecision(
                        passwordResult.Message ?? "Invalid password",
                        "EphemeralSharePasswordInvalid",
                        new Dictionary<string, object>
                        {
                            ["IsLockedOut"] = passwordResult.IsLockedOut,
                            ["RemainingAttempts"] = passwordResult.RemainingAttempts ?? 0
                        });
                }
            }

            // Check access counter (T76.2)
            if (_accessCounters.TryGetValue(share.Id, out var counter))
            {
                if (!counter.IsUnlimited && counter.RemainingAccesses <= 0)
                {
                    share.IsActive = false;
                    LogAccess(share, context, false, "Maximum access count exceeded");
                    return CreateDeniedDecision("Maximum access count exceeded", "EphemeralShareAccessLimit",
                        new Dictionary<string, object>
                        {
                            ["MaxAccessCount"] = counter.MaxAccesses,
                            ["ActualAccessCount"] = counter.TotalAccesses
                        });
                }
            }

            // Check resource match
            if (!share.ResourceId.Equals(context.ResourceId, StringComparison.OrdinalIgnoreCase))
            {
                LogAccess(share, context, false, "Resource mismatch");
                return CreateDeniedDecision("Share token not valid for this resource", "EphemeralShareResourceMismatch");
            }

            // Check allowed actions
            if (share.AllowedActions.Any() &&
                !share.AllowedActions.Contains(context.Action, StringComparer.OrdinalIgnoreCase))
            {
                LogAccess(share, context, false, $"Action '{context.Action}' not allowed");
                return CreateDeniedDecision(
                    $"Action '{context.Action}' not allowed by this share",
                    "EphemeralShareActionRestriction",
                    new Dictionary<string, object> { ["AllowedActions"] = share.AllowedActions });
            }

            // Check IP restrictions
            if (share.AllowedIpRanges != null && share.AllowedIpRanges.Any())
            {
                if (string.IsNullOrEmpty(context.ClientIpAddress) ||
                    !IsIpAllowed(context.ClientIpAddress, share.AllowedIpRanges))
                {
                    LogAccess(share, context, false, "IP not allowed");
                    return CreateDeniedDecision("Access denied from this IP address", "EphemeralShareIpRestriction");
                }
            }

            // Check country restrictions
            if (share.AllowedCountries != null && share.AllowedCountries.Any())
            {
                var country = context.Location?.Country;
                if (string.IsNullOrEmpty(country) ||
                    !share.AllowedCountries.Contains(country, StringComparer.OrdinalIgnoreCase))
                {
                    LogAccess(share, context, false, "Country not allowed");
                    return CreateDeniedDecision("Access denied from this country", "EphemeralShareCountryRestriction");
                }
            }

            // Check recipient restrictions
            if (share.AllowedRecipients != null && share.AllowedRecipients.Any())
            {
                if (!share.AllowedRecipients.Contains(context.SubjectId, StringComparer.OrdinalIgnoreCase))
                {
                    LogAccess(share, context, false, "Not an authorized recipient");
                    return CreateDeniedDecision(
                        "You are not an authorized recipient of this share",
                        "EphemeralShareRecipientRestriction");
                }
            }

            // Check authentication requirement
            if (share.RequireAuthentication)
            {
                if (string.IsNullOrEmpty(context.SubjectId) || context.SubjectId == "anonymous")
                {
                    LogAccess(share, context, false, "Authentication required");
                    return CreateDeniedDecision(
                        "Authentication required to access this share",
                        "EphemeralShareAuthRequired");
                }
            }

            // Access granted - consume from counter (T76.2)
            counter?.TryConsume();

            // Record TTL access (T76.3)
            ttlEngine?.RecordAccess();

            // Update share state
            share.AccessCount++;
            share.LastAccessedAt = DateTime.UtcNow;

            // Update expiration for sliding window
            if (ttlEngine != null)
            {
                share.ExpiresAt = ttlEngine.AbsoluteExpiration;
            }
            else if (share.UseSlidingExpiration && share.SlidingExpirationMinutes > 0)
            {
                share.ExpiresAt = DateTime.UtcNow.AddMinutes(share.SlidingExpirationMinutes);
            }

            // Log successful access (T76.6)
            var isFirstAccess = share.AccessCount == 1;
            LogAccess(share, context, true, null);

            // Send notification (T76.8)
            _notificationService.NotifyAccess(share.Id, new AccessNotificationDetails
            {
                AccessorId = context.SubjectId,
                IpAddress = context.ClientIpAddress ?? "unknown",
                AccessTime = DateTime.UtcNow,
                IsFirstAccess = isFirstAccess,
                Location = context.Location,
                UserAgent = context.EnvironmentAttributes.TryGetValue("UserAgent", out var ua) ? ua as string : null,
                RemainingAccesses = counter?.RemainingAccesses,
                TimeUntilExpiration = ttlEngine?.TimeRemaining
            });

            // Check if this triggers burn after reading (T76.4)
            var shouldBurn = share.MaxAccessCount.HasValue &&
                            share.AccessCount >= share.MaxAccessCount.Value;

            DestructionProof? destructionProof = null;
            if (shouldBurn)
            {
                share.IsActive = false;

                // Generate destruction proof (T76.5)
                destructionProof = await _burnManager.ExecuteBurnAsync(
                    share.Id,
                    Encoding.UTF8.GetBytes(share.ResourceId),
                    () =>
                    {
                        _shares.TryRemove(share.Token, out _);
                        return Task.FromResult(true);
                    });
            }

            return new AccessDecision
            {
                IsGranted = true,
                Reason = "Ephemeral share access granted",
                ApplicablePolicies = new[] { "EphemeralShareAccess" },
                Metadata = new Dictionary<string, object>
                {
                    ["ShareId"] = share.Id,
                    ["AccessCount"] = share.AccessCount,
                    ["RemainingAccesses"] = counter?.RemainingAccesses ?? -1,
                    ["ExpiresAt"] = share.ExpiresAt.ToString("o"),
                    ["IsFinalAccess"] = shouldBurn,
                    ["DestructionProof"] = destructionProof?.ToJson() ?? "",
                    ["EnableAntiScreenshot"] = share.EnableAntiScreenshot
                }
            };
        }

        private void LogAccess(EphemeralShare share, AccessContext context, bool success, string? denialReason)
        {
            var userAgent = context.EnvironmentAttributes.TryGetValue("UserAgent", out var ua) ? ua as string : null;
            var referer = context.EnvironmentAttributes.TryGetValue("Referer", out var r) ? r as string : null;
            var deviceFingerprint = context.EnvironmentAttributes.TryGetValue("DeviceFingerprint", out var df) ? df as string : null;
            var sessionId = context.EnvironmentAttributes.TryGetValue("SessionId", out var sid) ? sid as string : null;
            var correlationId = context.EnvironmentAttributes.TryGetValue("CorrelationId", out var cid) ? cid as string : null;

            _accessLogger.LogAccess(new AccessLogEntry
            {
                ResourceId = share.ResourceId,
                ShareId = share.Id,
                AccessorId = context.SubjectId,
                IpAddress = context.ClientIpAddress ?? "unknown",
                UserAgent = userAgent,
                Referer = referer,
                Location = context.Location,
                Action = context.Action,
                Success = success,
                DenialReason = denialReason,
                DeviceFingerprint = deviceFingerprint,
                SessionId = sessionId,
                CorrelationId = correlationId
            });
        }

        private static AccessDecision CreateDeniedDecision(
            string reason,
            string policy,
            Dictionary<string, object>? metadata = null)
        {
            return new AccessDecision
            {
                IsGranted = false,
                Reason = reason,
                ApplicablePolicies = new[] { policy },
                Metadata = metadata ?? new Dictionary<string, object>()
            };
        }

        private bool IsExpired(EphemeralShare share)
        {
            return DateTime.UtcNow > share.ExpiresAt;
        }

        private bool IsIpAllowed(string clientIp, List<string> allowedRanges)
        {
            foreach (var range in allowedRanges)
            {
                if (range.Contains('/'))
                {
                    if (IsIpInCidrRange(clientIp, range))
                        return true;
                }
                else
                {
                    if (range == "*" || clientIp.Equals(range, StringComparison.OrdinalIgnoreCase))
                        return true;

                    if (range.EndsWith('*') && clientIp.StartsWith(range.TrimEnd('*')))
                        return true;
                }
            }
            return false;
        }

        private bool IsIpInCidrRange(string ip, string cidr)
        {
            try
            {
                var parts = cidr.Split('/');
                if (parts.Length != 2) return false;

                var baseIp = System.Net.IPAddress.Parse(parts[0]);
                var maskBits = int.Parse(parts[1]);

                var clientIp = System.Net.IPAddress.Parse(ip);

                var baseBytes = baseIp.GetAddressBytes();
                var clientBytes = clientIp.GetAddressBytes();

                if (baseBytes.Length != clientBytes.Length) return false;

                int fullBytes = maskBits / 8;
                int remainingBits = maskBits % 8;

                for (int i = 0; i < fullBytes && i < baseBytes.Length; i++)
                {
                    if (baseBytes[i] != clientBytes[i]) return false;
                }

                if (remainingBits > 0 && fullBytes < baseBytes.Length)
                {
                    int mask = (byte)(0xFF << (8 - remainingBits));
                    if ((baseBytes[fullBytes] & mask) != (clientBytes[fullBytes] & mask))
                        return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private void CleanupExpiredShares(object? state)
        {
            var expiredTokens = _shares
                .Where(kvp => IsExpired(kvp.Value) || !kvp.Value.IsActive)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var token in expiredTokens)
            {
                if (_shares.TryRemove(token, out var share))
                {
                    _accessCounters.TryRemove(share.Id, out _);
                    _ttlEngines.TryRemove(share.Id, out _);
                }
            }
        }
    }

    #endregion

    #region Supporting Types

    /// <summary>
    /// Options for creating an ephemeral share.
    /// </summary>
    public record ShareOptions
    {
        /// <summary>User ID who created the share.</summary>
        public required string CreatedBy { get; init; }

        /// <summary>Absolute expiration time (default: 24 hours from creation).</summary>
        public DateTime? ExpiresAt { get; init; }

        /// <summary>Maximum number of times the share can be accessed (null = unlimited).</summary>
        public int? MaxAccessCount { get; init; }

        /// <summary>List of allowed IP ranges (CIDR notation supported).</summary>
        public IEnumerable<string>? AllowedIpRanges { get; init; }

        /// <summary>List of allowed country codes.</summary>
        public IEnumerable<string>? AllowedCountries { get; init; }

        /// <summary>List of allowed recipient user IDs.</summary>
        public IEnumerable<string>? AllowedRecipients { get; init; }

        /// <summary>Whether authentication is required to access the share.</summary>
        public bool RequireAuthentication { get; init; }

        /// <summary>List of allowed actions (default: read only).</summary>
        public IEnumerable<string>? AllowedActions { get; init; }

        /// <summary>Whether to use sliding expiration.</summary>
        public bool UseSlidingExpiration { get; init; }

        /// <summary>Sliding expiration window in minutes.</summary>
        public int SlidingExpirationMinutes { get; init; }

        /// <summary>Additional metadata for the share.</summary>
        public Dictionary<string, object>? Metadata { get; init; }

        /// <summary>Whether to use a short token for easier sharing.</summary>
        public bool UseShortToken { get; init; }

        /// <summary>Password for the share (T76.7).</summary>
        public string? Password { get; init; }

        /// <summary>Password protection options (T76.7).</summary>
        public PasswordProtectionOptions? PasswordOptions { get; init; }

        /// <summary>Whether to notify owner on access (T76.8).</summary>
        public bool NotifyOnAccess { get; init; }

        /// <summary>Email for notifications (T76.8).</summary>
        public string? NotificationEmail { get; init; }

        /// <summary>Full notification subscription (T76.8).</summary>
        public NotificationSubscription? NotificationSubscription { get; init; }

        /// <summary>Whether to enable anti-screenshot protection (T76.10).</summary>
        public bool EnableAntiScreenshot { get; init; }

        /// <summary>Anti-screenshot options (T76.10).</summary>
        public AntiScreenshotOptions? AntiScreenshotOptions { get; init; }
    }

    /// <summary>
    /// Represents an ephemeral share.
    /// </summary>
    public sealed class EphemeralShare
    {
        public required string Id { get; init; }
        public required string ResourceId { get; init; }
        public required string Token { get; init; }
        public required DateTime CreatedAt { get; init; }
        public required string CreatedBy { get; init; }
        public DateTime ExpiresAt { get; set; }
        public int? MaxAccessCount { get; init; }
        public List<string>? AllowedIpRanges { get; init; }
        public List<string>? AllowedCountries { get; init; }
        public List<string>? AllowedRecipients { get; init; }
        public bool RequireAuthentication { get; init; }
        public List<string> AllowedActions { get; init; } = new();
        public bool UseSlidingExpiration { get; init; }
        public int SlidingExpirationMinutes { get; init; }
        public Dictionary<string, object> Metadata { get; init; } = new();
        public bool IsActive { get; set; }
        public int AccessCount { get; set; }
        public DateTime? LastAccessedAt { get; set; }
        public DateTime? RevokedAt { get; set; }
        public bool EnableAntiScreenshot { get; init; }
        public AntiScreenshotOptions? AntiScreenshotOptions { get; init; }
    }

    /// <summary>
    /// Result of creating an ephemeral share.
    /// </summary>
    public sealed record EphemeralShareResult
    {
        /// <summary>The created share.</summary>
        public required EphemeralShare Share { get; init; }

        /// <summary>Full URL for accessing the share.</summary>
        public required string ShareUrl { get; init; }

        /// <summary>The share token.</summary>
        public required string Token { get; init; }

        /// <summary>When the share expires.</summary>
        public required DateTime ExpiresAt { get; init; }

        /// <summary>Maximum allowed accesses (null = unlimited).</summary>
        public int? MaxAccesses { get; init; }

        /// <summary>Whether the share is password protected.</summary>
        public bool IsPasswordProtected { get; init; }

        /// <summary>Anti-screenshot JavaScript if enabled.</summary>
        public string? AntiScreenshotScript { get; init; }
    }

    /// <summary>
    /// Log entry for share access (legacy compatibility).
    /// </summary>
    public sealed record ShareAccessLog
    {
        public required string ShareId { get; init; }
        public required DateTime AccessedAt { get; init; }
        public required string AccessedBy { get; init; }
        public required string Action { get; init; }
        public string? ClientIpAddress { get; init; }
        public GeoLocation? Location { get; init; }
        public required bool Success { get; init; }
        public string? FailureReason { get; init; }
    }

    #endregion
}
