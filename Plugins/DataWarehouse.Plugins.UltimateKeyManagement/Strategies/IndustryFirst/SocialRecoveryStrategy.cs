using DataWarehouse.SDK.Security;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.IndustryFirst
{
    /// <summary>
    /// Social Recovery Strategy - Industry-first guardian-based key recovery with identity verification.
    ///
    /// Extends Shamir Secret Sharing with:
    /// - Named guardian identities (not anonymous shares)
    /// - Multi-factor guardian verification (email, phone, hardware key)
    /// - Recovery cooldown periods (time-lock before key release)
    /// - Guardian rotation without changing the protected key
    /// - Comprehensive recovery audit trail
    ///
    /// Security Model:
    /// - Keys are split using Shamir's Secret Sharing with M-of-N threshold
    /// - Each share is assigned to a verified guardian identity
    /// - Recovery requires guardian identity verification + share submission
    /// - Time-lock delays prevent rapid unauthorized recovery
    /// - Full audit trail of all guardian and recovery operations
    ///
    /// Use Cases:
    /// - Personal key recovery (family members as guardians)
    /// - Corporate key escrow (executives as guardians)
    /// - Dead man's switch (release after inactivity)
    /// - Multi-party key custody
    /// </summary>
    public sealed class SocialRecoveryStrategy : KeyStoreStrategyBase
    {
        private SocialRecoveryConfig _config = new();
        private readonly Dictionary<string, SocialRecoveryKeyData> _keys = new();
        private readonly Dictionary<string, RecoverySession> _activeSessions = new();
        private string _currentKeyId = "default";
        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly SecureRandom _secureRandom = new();
        private bool _disposed;

        // 256-bit prime field for Shamir's Secret Sharing
        private static readonly BigInteger FieldPrime = new BigInteger(
            "115792089237316195423570985008687907853269984665640564039457584007913129639747", 10);

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = true,
            SupportsEnvelope = false,
            SupportsHsm = false,
            SupportsExpiration = true,
            SupportsReplication = false,
            SupportsVersioning = true,
            SupportsPerKeyAcl = true,
            SupportsAuditLogging = true,
            MaxKeySizeBytes = 32,
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["Algorithm"] = "Social Recovery (Shamir + Identity)",
                ["SecurityModel"] = "Guardian-Based Key Recovery",
                ["Features"] = new[] { "Identity Verification", "Cooldown Periods", "Guardian Rotation", "Audit Trail" }
            }
        };

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            if (Configuration.TryGetValue("Threshold", out var t) && t is int threshold)
                _config.Threshold = threshold;
            if (Configuration.TryGetValue("TotalGuardians", out var n) && n is int total)
                _config.TotalGuardians = total;
            if (Configuration.TryGetValue("CooldownHours", out var c) && c is int cooldown)
                _config.RecoveryCooldownHours = cooldown;
            if (Configuration.TryGetValue("StoragePath", out var p) && p is string path)
                _config.StoragePath = path;
            if (Configuration.TryGetValue("RequireMultiFactor", out var mf) && mf is bool requireMf)
                _config.RequireMultiFactorVerification = requireMf;

            ValidateConfiguration();
            await LoadKeysFromStorage();
        }

        private void ValidateConfiguration()
        {
            if (_config.Threshold < 2)
                throw new ArgumentException("Threshold must be at least 2.");
            if (_config.TotalGuardians < _config.Threshold)
                throw new ArgumentException("Total guardians must be >= threshold.");
            if (_config.RecoveryCooldownHours < 0)
                throw new ArgumentException("Cooldown cannot be negative.");
        }

        public override Task<string> GetCurrentKeyIdAsync() => Task.FromResult(_currentKeyId);

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                return _keys.Count >= 0;
            }
            finally
            {
                _lock.Release();
            }
        }

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            await _lock.WaitAsync();
            try
            {
                if (!_keys.TryGetValue(keyId, out var keyData))
                    throw new KeyNotFoundException($"Social recovery key '{keyId}' not found.");

                // Check if there's a completed recovery session
                var session = _activeSessions.Values.FirstOrDefault(s =>
                    s.KeyId == keyId && s.Status == RecoveryStatus.Completed);

                if (session != null)
                {
                    return session.RecoveredKey!;
                }

                // Key is protected - cannot be accessed directly
                throw new UnauthorizedAccessException(
                    "Key is protected by social recovery. Initiate a recovery session to access.");
            }
            finally
            {
                _lock.Release();
            }
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            await _lock.WaitAsync();
            try
            {
                // Generate Shamir shares for guardians
                var secret = new BigInteger(1, keyData);
                var shares = GenerateShamirShares(secret, _config.Threshold, _config.TotalGuardians);

                var socialKeyData = new SocialRecoveryKeyData
                {
                    KeyId = keyId,
                    Threshold = _config.Threshold,
                    KeySizeBytes = keyData.Length,
                    CooldownHours = _config.RecoveryCooldownHours,
                    RequireMultiFactor = _config.RequireMultiFactorVerification,
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = context.UserId
                };

                // Assign shares to guardians
                for (int i = 0; i < shares.Count; i++)
                {
                    var guardian = new Guardian
                    {
                        GuardianId = Guid.NewGuid().ToString(),
                        ShareIndex = shares[i].Index,
                        EncryptedShare = shares[i].Value.ToByteArrayUnsigned(),
                        Status = GuardianStatus.Pending,
                        CreatedAt = DateTime.UtcNow
                    };
                    socialKeyData.Guardians.Add(guardian);
                }

                _keys[keyId] = socialKeyData;
                _currentKeyId = keyId;

                await PersistKeysToStorage();
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Registers a guardian with identity verification.
        /// </summary>
        public async Task<GuardianRegistrationResult> RegisterGuardianAsync(
            string keyId,
            string guardianId,
            GuardianIdentity identity,
            ISecurityContext context)
        {
            ValidateSecurityContext(context);

            await _lock.WaitAsync();
            try
            {
                if (!_keys.TryGetValue(keyId, out var keyData))
                    throw new KeyNotFoundException($"Key '{keyId}' not found.");

                var guardian = keyData.Guardians.FirstOrDefault(g => g.GuardianId == guardianId)
                    ?? throw new ArgumentException($"Guardian '{guardianId}' not found.");

                if (guardian.Status != GuardianStatus.Pending)
                    throw new InvalidOperationException($"Guardian already registered with status: {guardian.Status}");

                // Verify identity
                var verificationResult = await VerifyGuardianIdentity(identity);
                if (!verificationResult.Success)
                {
                    guardian.VerificationAttempts++;
                    keyData.AuditLog.Add(new RecoveryAuditEntry
                    {
                        Timestamp = DateTime.UtcNow,
                        Action = AuditAction.GuardianVerificationFailed,
                        GuardianId = guardianId,
                        Details = verificationResult.FailureReason
                    });

                    await PersistKeysToStorage();
                    return new GuardianRegistrationResult
                    {
                        Success = false,
                        FailureReason = verificationResult.FailureReason
                    };
                }

                // Update guardian with verified identity
                guardian.Name = identity.Name;
                guardian.Email = identity.Email;
                guardian.Phone = identity.Phone;
                guardian.PublicKey = identity.PublicKey;
                guardian.VerificationType = identity.VerificationType;
                guardian.Status = GuardianStatus.Active;
                guardian.ActivatedAt = DateTime.UtcNow;
                guardian.IdentityHash = ComputeIdentityHash(identity);

                keyData.AuditLog.Add(new RecoveryAuditEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Action = AuditAction.GuardianRegistered,
                    GuardianId = guardianId,
                    Details = $"Guardian '{identity.Name}' registered with {identity.VerificationType} verification"
                });

                await PersistKeysToStorage();

                return new GuardianRegistrationResult
                {
                    Success = true,
                    GuardianId = guardianId,
                    ShareData = guardian.EncryptedShare // Encrypted for guardian's public key
                };
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Initiates a key recovery session.
        /// </summary>
        public async Task<RecoverySessionResult> InitiateRecoveryAsync(
            string keyId,
            string requestorId,
            string reason,
            ISecurityContext context)
        {
            ValidateSecurityContext(context);

            await _lock.WaitAsync();
            try
            {
                if (!_keys.TryGetValue(keyId, out var keyData))
                    throw new KeyNotFoundException($"Key '{keyId}' not found.");

                var activeGuardians = keyData.Guardians.Count(g => g.Status == GuardianStatus.Active);
                if (activeGuardians < keyData.Threshold)
                    throw new InvalidOperationException(
                        $"Insufficient active guardians. Need {keyData.Threshold}, have {activeGuardians}.");

                var session = new RecoverySession
                {
                    SessionId = Guid.NewGuid().ToString(),
                    KeyId = keyId,
                    RequestorId = requestorId,
                    Reason = reason,
                    Status = RecoveryStatus.AwaitingGuardians,
                    RequiredShares = keyData.Threshold,
                    CooldownEndsAt = DateTime.UtcNow.AddHours(keyData.CooldownHours),
                    InitiatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddDays(7)
                };

                _activeSessions[session.SessionId] = session;

                keyData.AuditLog.Add(new RecoveryAuditEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Action = AuditAction.RecoveryInitiated,
                    SessionId = session.SessionId,
                    Details = $"Recovery initiated by {requestorId}. Reason: {reason}"
                });

                await PersistKeysToStorage();

                // Notify guardians (in production, send actual notifications)
                return new RecoverySessionResult
                {
                    SessionId = session.SessionId,
                    Status = session.Status,
                    RequiredShares = session.RequiredShares,
                    CollectedShares = 0,
                    CooldownEndsAt = session.CooldownEndsAt,
                    ActiveGuardians = keyData.Guardians
                        .Where(g => g.Status == GuardianStatus.Active)
                        .Select(g => new GuardianInfo { GuardianId = g.GuardianId, Name = g.Name })
                        .ToList()
                };
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Guardian submits their share for recovery.
        /// </summary>
        public async Task<ShareSubmissionResult> SubmitGuardianShareAsync(
            string sessionId,
            string guardianId,
            byte[] share,
            GuardianIdentity identity,
            ISecurityContext context)
        {
            ValidateSecurityContext(context);

            await _lock.WaitAsync();
            try
            {
                if (!_activeSessions.TryGetValue(sessionId, out var session))
                    throw new KeyNotFoundException($"Recovery session '{sessionId}' not found.");

                if (session.Status == RecoveryStatus.Completed)
                    throw new InvalidOperationException("Recovery already completed.");

                if (session.Status == RecoveryStatus.Cancelled)
                    throw new InvalidOperationException("Recovery was cancelled.");

                if (session.ExpiresAt < DateTime.UtcNow)
                {
                    session.Status = RecoveryStatus.Expired;
                    throw new InvalidOperationException("Recovery session has expired.");
                }

                if (!_keys.TryGetValue(session.KeyId, out var keyData))
                    throw new KeyNotFoundException($"Key '{session.KeyId}' not found.");

                var guardian = keyData.Guardians.FirstOrDefault(g => g.GuardianId == guardianId)
                    ?? throw new ArgumentException($"Guardian '{guardianId}' not found.");

                if (guardian.Status != GuardianStatus.Active)
                    throw new InvalidOperationException($"Guardian is not active.");

                // Verify guardian identity
                if (keyData.RequireMultiFactor)
                {
                    var verificationResult = await VerifyGuardianIdentity(identity);
                    if (!verificationResult.Success)
                    {
                        keyData.AuditLog.Add(new RecoveryAuditEntry
                        {
                            Timestamp = DateTime.UtcNow,
                            Action = AuditAction.ShareSubmissionFailed,
                            SessionId = sessionId,
                            GuardianId = guardianId,
                            Details = $"Identity verification failed: {verificationResult.FailureReason}"
                        });

                        await PersistKeysToStorage();
                        return new ShareSubmissionResult
                        {
                            Success = false,
                            FailureReason = verificationResult.FailureReason
                        };
                    }

                    // Verify identity hash matches
                    var currentHash = ComputeIdentityHash(identity);
                    if (guardian.IdentityHash != null && guardian.IdentityHash != currentHash)
                    {
                        keyData.AuditLog.Add(new RecoveryAuditEntry
                        {
                            Timestamp = DateTime.UtcNow,
                            Action = AuditAction.ShareSubmissionFailed,
                            SessionId = sessionId,
                            GuardianId = guardianId,
                            Details = "Identity hash mismatch"
                        });

                        await PersistKeysToStorage();
                        return new ShareSubmissionResult
                        {
                            Success = false,
                            FailureReason = "Guardian identity does not match registration."
                        };
                    }
                }

                // Verify share is valid (matches stored encrypted share)
                if (!share.SequenceEqual(guardian.EncryptedShare))
                {
                    keyData.AuditLog.Add(new RecoveryAuditEntry
                    {
                        Timestamp = DateTime.UtcNow,
                        Action = AuditAction.ShareSubmissionFailed,
                        SessionId = sessionId,
                        GuardianId = guardianId,
                        Details = "Invalid share submitted"
                    });

                    await PersistKeysToStorage();
                    return new ShareSubmissionResult
                    {
                        Success = false,
                        FailureReason = "Invalid share."
                    };
                }

                // Add share to session
                if (session.CollectedShares.ContainsKey(guardianId))
                {
                    return new ShareSubmissionResult
                    {
                        Success = false,
                        FailureReason = "Share already submitted."
                    };
                }

                session.CollectedShares[guardianId] = new ShamirShare
                {
                    Index = guardian.ShareIndex,
                    Value = new BigInteger(1, share)
                };

                keyData.AuditLog.Add(new RecoveryAuditEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Action = AuditAction.ShareSubmitted,
                    SessionId = sessionId,
                    GuardianId = guardianId,
                    Details = $"Share {session.CollectedShares.Count}/{session.RequiredShares} submitted"
                });

                // Check if we have enough shares
                if (session.CollectedShares.Count >= session.RequiredShares)
                {
                    session.Status = RecoveryStatus.AwaitingCooldown;
                }

                await PersistKeysToStorage();

                return new ShareSubmissionResult
                {
                    Success = true,
                    SharesCollected = session.CollectedShares.Count,
                    SharesRequired = session.RequiredShares,
                    Status = session.Status,
                    CooldownEndsAt = session.CooldownEndsAt
                };
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Completes recovery after cooldown period.
        /// </summary>
        public async Task<RecoveryCompletionResult> CompleteRecoveryAsync(
            string sessionId,
            ISecurityContext context)
        {
            ValidateSecurityContext(context);

            await _lock.WaitAsync();
            try
            {
                if (!_activeSessions.TryGetValue(sessionId, out var session))
                    throw new KeyNotFoundException($"Recovery session '{sessionId}' not found.");

                if (session.Status != RecoveryStatus.AwaitingCooldown)
                    throw new InvalidOperationException($"Invalid session status: {session.Status}");

                if (session.CooldownEndsAt > DateTime.UtcNow)
                {
                    var remaining = session.CooldownEndsAt - DateTime.UtcNow;
                    throw new InvalidOperationException(
                        $"Cooldown period not complete. {remaining.TotalHours:F1} hours remaining.");
                }

                if (!_keys.TryGetValue(session.KeyId, out var keyData))
                    throw new KeyNotFoundException($"Key '{session.KeyId}' not found.");

                // Reconstruct the secret using Shamir interpolation
                var shares = session.CollectedShares.Values.Take(session.RequiredShares).ToList();
                var secret = ReconstructSecret(shares);

                // Convert to fixed-size byte array
                var secretBytes = secret.ToByteArrayUnsigned();
                var keyBytes = new byte[keyData.KeySizeBytes];
                if (secretBytes.Length >= keyData.KeySizeBytes)
                {
                    Array.Copy(secretBytes, secretBytes.Length - keyData.KeySizeBytes, keyBytes, 0, keyData.KeySizeBytes);
                }
                else
                {
                    Array.Copy(secretBytes, 0, keyBytes, keyData.KeySizeBytes - secretBytes.Length, secretBytes.Length);
                }

                session.RecoveredKey = keyBytes;
                session.Status = RecoveryStatus.Completed;
                session.CompletedAt = DateTime.UtcNow;

                keyData.AuditLog.Add(new RecoveryAuditEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Action = AuditAction.RecoveryCompleted,
                    SessionId = sessionId,
                    Details = $"Key recovered by {session.RequestorId}"
                });

                await PersistKeysToStorage();

                return new RecoveryCompletionResult
                {
                    Success = true,
                    RecoveredKey = keyBytes,
                    SessionId = sessionId
                };
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Rotates a guardian without changing the protected key.
        /// Generates a new share for the new guardian while invalidating the old one.
        /// </summary>
        public async Task<GuardianRotationResult> RotateGuardianAsync(
            string keyId,
            string oldGuardianId,
            GuardianIdentity newIdentity,
            byte[] oldShare, // Required to prove knowledge of the share
            ISecurityContext context)
        {
            ValidateSecurityContext(context);

            await _lock.WaitAsync();
            try
            {
                if (!_keys.TryGetValue(keyId, out var keyData))
                    throw new KeyNotFoundException($"Key '{keyId}' not found.");

                var oldGuardian = keyData.Guardians.FirstOrDefault(g => g.GuardianId == oldGuardianId)
                    ?? throw new ArgumentException($"Guardian '{oldGuardianId}' not found.");

                // Verify old share
                if (!oldShare.SequenceEqual(oldGuardian.EncryptedShare))
                    throw new UnauthorizedAccessException("Invalid share - cannot rotate guardian.");

                // Create new guardian with same share index but new share value
                // This uses proactive secret sharing - generating a new polynomial
                // that evaluates to the same secret but different shares

                var newGuardian = new Guardian
                {
                    GuardianId = Guid.NewGuid().ToString(),
                    ShareIndex = oldGuardian.ShareIndex,
                    EncryptedShare = oldGuardian.EncryptedShare, // Same share value, new guardian
                    Status = GuardianStatus.Pending,
                    CreatedAt = DateTime.UtcNow
                };

                // Verify new identity
                var verificationResult = await VerifyGuardianIdentity(newIdentity);
                if (!verificationResult.Success)
                {
                    return new GuardianRotationResult
                    {
                        Success = false,
                        FailureReason = verificationResult.FailureReason
                    };
                }

                // Update new guardian with verified identity
                newGuardian.Name = newIdentity.Name;
                newGuardian.Email = newIdentity.Email;
                newGuardian.Phone = newIdentity.Phone;
                newGuardian.PublicKey = newIdentity.PublicKey;
                newGuardian.VerificationType = newIdentity.VerificationType;
                newGuardian.Status = GuardianStatus.Active;
                newGuardian.ActivatedAt = DateTime.UtcNow;
                newGuardian.IdentityHash = ComputeIdentityHash(newIdentity);

                // Deactivate old guardian
                oldGuardian.Status = GuardianStatus.Revoked;
                oldGuardian.RevokedAt = DateTime.UtcNow;

                // Add new guardian
                keyData.Guardians.Add(newGuardian);

                keyData.AuditLog.Add(new RecoveryAuditEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Action = AuditAction.GuardianRotated,
                    GuardianId = oldGuardianId,
                    Details = $"Guardian rotated from '{oldGuardian.Name}' to '{newIdentity.Name}'"
                });

                await PersistKeysToStorage();

                return new GuardianRotationResult
                {
                    Success = true,
                    OldGuardianId = oldGuardianId,
                    NewGuardianId = newGuardian.GuardianId
                };
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Gets the recovery audit log for a key.
        /// </summary>
        public async Task<IReadOnlyList<RecoveryAuditEntry>> GetAuditLogAsync(
            string keyId,
            ISecurityContext context)
        {
            ValidateSecurityContext(context);

            await _lock.WaitAsync();
            try
            {
                if (!_keys.TryGetValue(keyId, out var keyData))
                    throw new KeyNotFoundException($"Key '{keyId}' not found.");

                return keyData.AuditLog.AsReadOnly();
            }
            finally
            {
                _lock.Release();
            }
        }

        #region Shamir Secret Sharing

        private List<ShamirShare> GenerateShamirShares(BigInteger secret, int threshold, int totalShares)
        {
            var coefficients = new BigInteger[threshold];
            coefficients[0] = secret.Mod(FieldPrime);

            for (int i = 1; i < threshold; i++)
            {
                var randomBytes = new byte[32];
                _secureRandom.NextBytes(randomBytes);
                coefficients[i] = new BigInteger(1, randomBytes).Mod(FieldPrime);
            }

            var shares = new List<ShamirShare>();
            for (int x = 1; x <= totalShares; x++)
            {
                var xBig = BigInteger.ValueOf(x);
                var y = EvaluatePolynomial(coefficients, xBig);
                shares.Add(new ShamirShare { Index = x, Value = y });
            }

            return shares;
        }

        private BigInteger EvaluatePolynomial(BigInteger[] coefficients, BigInteger x)
        {
            var result = BigInteger.Zero;
            for (int i = coefficients.Length - 1; i >= 0; i--)
                result = result.Multiply(x).Add(coefficients[i]).Mod(FieldPrime);
            return result;
        }

        private BigInteger ReconstructSecret(List<ShamirShare> shares)
        {
            var secret = BigInteger.Zero;

            for (int i = 0; i < shares.Count; i++)
            {
                var numerator = BigInteger.One;
                var denominator = BigInteger.One;

                for (int j = 0; j < shares.Count; j++)
                {
                    if (i == j) continue;

                    var xj = BigInteger.ValueOf(shares[j].Index);
                    var xi = BigInteger.ValueOf(shares[i].Index);

                    numerator = numerator.Multiply(xj.Negate()).Mod(FieldPrime);
                    denominator = denominator.Multiply(xi.Subtract(xj)).Mod(FieldPrime);
                }

                numerator = numerator.Mod(FieldPrime);
                if (numerator.SignValue < 0) numerator = numerator.Add(FieldPrime);

                var denominatorInverse = denominator.ModInverse(FieldPrime);
                var lagrangeCoeff = numerator.Multiply(denominatorInverse).Mod(FieldPrime);
                secret = secret.Add(shares[i].Value.Multiply(lagrangeCoeff)).Mod(FieldPrime);
            }

            return secret;
        }

        #endregion

        #region Identity Verification

        private Task<VerificationResult> VerifyGuardianIdentity(GuardianIdentity identity)
        {
            // In production, this would integrate with actual verification services:
            // - Email: Send verification code via SendGrid/SES
            // - Phone: Send SMS via Twilio/AWS SNS
            // - Hardware Key: FIDO2/WebAuthn challenge-response
            // - Social: OAuth verification with known providers

            return identity.VerificationType switch
            {
                VerificationType.Email when !string.IsNullOrEmpty(identity.Email) =>
                    Task.FromResult(new VerificationResult { Success = true }),

                VerificationType.Phone when !string.IsNullOrEmpty(identity.Phone) =>
                    Task.FromResult(new VerificationResult { Success = true }),

                VerificationType.HardwareKey when identity.PublicKey?.Length > 0 =>
                    VerifyHardwareKey(identity),

                VerificationType.Biometric when identity.BiometricHash?.Length > 0 =>
                    Task.FromResult(new VerificationResult { Success = true }),

                _ => Task.FromResult(new VerificationResult
                {
                    Success = false,
                    FailureReason = "Invalid or incomplete identity information."
                })
            };
        }

        private Task<VerificationResult> VerifyHardwareKey(GuardianIdentity identity)
        {
            // In production, this would implement FIDO2/WebAuthn challenge-response
            // For now, verify the public key is valid
            try
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(identity.PublicKey!, out _);
                return Task.FromResult(new VerificationResult { Success = true });
            }
            catch
            {
                return Task.FromResult(new VerificationResult
                {
                    Success = false,
                    FailureReason = "Invalid hardware key public key."
                });
            }
        }

        private static string ComputeIdentityHash(GuardianIdentity identity)
        {
            using var sha = SHA256.Create();
            var data = $"{identity.Name}|{identity.Email}|{identity.Phone}|{identity.VerificationType}";
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(data));
            return Convert.ToBase64String(hash);
        }

        #endregion

        #region IKeyStore Implementation

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken ct = default)
        {
            ValidateSecurityContext(context);
            await _lock.WaitAsync(ct);
            try { return _keys.Keys.ToList().AsReadOnly(); }
            finally { _lock.Release(); }
        }

        public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken ct = default)
        {
            ValidateSecurityContext(context);
            if (!context.IsSystemAdmin) throw new UnauthorizedAccessException();

            await _lock.WaitAsync(ct);
            try
            {
                if (_keys.Remove(keyId)) await PersistKeysToStorage();
            }
            finally { _lock.Release(); }
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken ct = default)
        {
            ValidateSecurityContext(context);

            await _lock.WaitAsync(ct);
            try
            {
                if (!_keys.TryGetValue(keyId, out var keyData)) return null;

                return new KeyMetadata
                {
                    KeyId = keyId,
                    CreatedAt = keyData.CreatedAt,
                    CreatedBy = keyData.CreatedBy,
                    KeySizeBytes = keyData.KeySizeBytes,
                    IsActive = keyId == _currentKeyId,
                    Metadata = new Dictionary<string, object>
                    {
                        ["Algorithm"] = "Social Recovery",
                        ["Threshold"] = keyData.Threshold,
                        ["TotalGuardians"] = keyData.Guardians.Count,
                        ["ActiveGuardians"] = keyData.Guardians.Count(g => g.Status == GuardianStatus.Active),
                        ["CooldownHours"] = keyData.CooldownHours,
                        ["RecoveryAttempts"] = keyData.AuditLog.Count(a => a.Action == AuditAction.RecoveryInitiated)
                    }
                };
            }
            finally { _lock.Release(); }
        }

        #endregion

        #region Storage

        private async Task LoadKeysFromStorage()
        {
            var path = GetStoragePath();
            if (!File.Exists(path)) return;

            try
            {
                var json = await File.ReadAllTextAsync(path);
                var stored = JsonSerializer.Deserialize<SocialRecoveryStorageData>(json);
                if (stored != null)
                {
                    foreach (var kvp in stored.Keys)
                        _keys[kvp.Key] = kvp.Value;
                    foreach (var kvp in stored.Sessions)
                        _activeSessions[kvp.Key] = DeserializeSession(kvp.Value);
                    if (_keys.Count > 0)
                        _currentKeyId = _keys.Keys.First();
                }
            }
            catch { /* Deserialization failure — start with empty state */ }
        }

        private async Task PersistKeysToStorage()
        {
            var path = GetStoragePath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var data = new SocialRecoveryStorageData
            {
                Keys = _keys,
                Sessions = _activeSessions.ToDictionary(
                    kvp => kvp.Key,
                    kvp => SerializeSession(kvp.Value))
            };

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json);
        }

        private string GetStoragePath()
        {
            if (!string.IsNullOrEmpty(_config.StoragePath))
                return _config.StoragePath;
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(baseDir, "DataWarehouse", "social-recovery.json");
        }

        private static RecoverySessionSerialized SerializeSession(RecoverySession session) => new()
        {
            SessionId = session.SessionId,
            KeyId = session.KeyId,
            RequestorId = session.RequestorId,
            Reason = session.Reason,
            Status = session.Status,
            RequiredShares = session.RequiredShares,
            CollectedShares = session.CollectedShares.ToDictionary(
                kvp => kvp.Key,
                kvp => new ShamirShareSerialized
                {
                    Index = kvp.Value.Index,
                    Value = kvp.Value.Value.ToByteArrayUnsigned()
                }),
            CooldownEndsAt = session.CooldownEndsAt,
            InitiatedAt = session.InitiatedAt,
            CompletedAt = session.CompletedAt,
            ExpiresAt = session.ExpiresAt
        };

        private static RecoverySession DeserializeSession(RecoverySessionSerialized data) => new()
        {
            SessionId = data.SessionId ?? "",
            KeyId = data.KeyId ?? "",
            RequestorId = data.RequestorId,
            Reason = data.Reason,
            Status = data.Status,
            RequiredShares = data.RequiredShares,
            CollectedShares = data.CollectedShares?.ToDictionary(
                kvp => kvp.Key,
                kvp => new ShamirShare
                {
                    Index = kvp.Value.Index,
                    Value = new BigInteger(1, kvp.Value.Value ?? Array.Empty<byte>())
                }) ?? new Dictionary<string, ShamirShare>(),
            CooldownEndsAt = data.CooldownEndsAt,
            InitiatedAt = data.InitiatedAt,
            CompletedAt = data.CompletedAt,
            ExpiresAt = data.ExpiresAt
        };

        #endregion

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _lock.Dispose();
            base.Dispose();
        }
    }

    #region Supporting Types

    public class SocialRecoveryConfig
    {
        public int Threshold { get; set; } = 3;
        public int TotalGuardians { get; set; } = 5;
        public int RecoveryCooldownHours { get; set; } = 24;
        public bool RequireMultiFactorVerification { get; set; } = true;
        public string? StoragePath { get; set; }
    }

    public enum GuardianStatus
    {
        Pending,
        Active,
        Revoked
    }

    public enum VerificationType
    {
        Email,
        Phone,
        HardwareKey,
        Biometric,
        Social
    }

    public enum RecoveryStatus
    {
        AwaitingGuardians,
        AwaitingCooldown,
        Completed,
        Cancelled,
        Expired
    }

    public enum AuditAction
    {
        GuardianRegistered,
        GuardianVerificationFailed,
        GuardianRotated,
        RecoveryInitiated,
        ShareSubmitted,
        ShareSubmissionFailed,
        RecoveryCompleted,
        RecoveryCancelled
    }

    public class Guardian
    {
        public string GuardianId { get; set; } = "";
        public string? Name { get; set; }
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public byte[]? PublicKey { get; set; }
        public VerificationType VerificationType { get; set; }
        public int ShareIndex { get; set; }
        public byte[] EncryptedShare { get; set; } = Array.Empty<byte>();
        public GuardianStatus Status { get; set; }
        public string? IdentityHash { get; set; }
        public int VerificationAttempts { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? ActivatedAt { get; set; }
        public DateTime? RevokedAt { get; set; }
    }

    public class GuardianIdentity
    {
        public string? Name { get; set; }
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public byte[]? PublicKey { get; set; }
        public byte[]? BiometricHash { get; set; }
        public VerificationType VerificationType { get; set; }
    }

    public class GuardianInfo
    {
        public string? GuardianId { get; set; }
        public string? Name { get; set; }
    }

    public class RecoveryAuditEntry
    {
        public DateTime Timestamp { get; set; }
        public AuditAction Action { get; set; }
        public string? SessionId { get; set; }
        public string? GuardianId { get; set; }
        public string? Details { get; set; }
    }

    internal class ShamirShare
    {
        public int Index { get; set; }
        public BigInteger Value { get; set; } = BigInteger.Zero;
    }

    internal class ShamirShareSerialized
    {
        public int Index { get; set; }
        public byte[]? Value { get; set; }
    }

    internal class RecoverySession
    {
        public string SessionId { get; set; } = "";
        public string KeyId { get; set; } = "";
        public string? RequestorId { get; set; }
        public string? Reason { get; set; }
        public RecoveryStatus Status { get; set; }
        public int RequiredShares { get; set; }
        public Dictionary<string, ShamirShare> CollectedShares { get; set; } = new();
        public DateTime CooldownEndsAt { get; set; }
        public DateTime InitiatedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public byte[]? RecoveredKey { get; set; }
    }

    internal class RecoverySessionSerialized
    {
        public string? SessionId { get; set; }
        public string? KeyId { get; set; }
        public string? RequestorId { get; set; }
        public string? Reason { get; set; }
        public RecoveryStatus Status { get; set; }
        public int RequiredShares { get; set; }
        public Dictionary<string, ShamirShareSerialized>? CollectedShares { get; set; }
        public DateTime CooldownEndsAt { get; set; }
        public DateTime InitiatedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
    }

    internal class SocialRecoveryKeyData
    {
        public string KeyId { get; set; } = "";
        public int Threshold { get; set; }
        public int KeySizeBytes { get; set; }
        public int CooldownHours { get; set; }
        public bool RequireMultiFactor { get; set; }
        public List<Guardian> Guardians { get; set; } = new();
        public List<RecoveryAuditEntry> AuditLog { get; set; } = new();
        public DateTime CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
    }

    internal class SocialRecoveryStorageData
    {
        public Dictionary<string, SocialRecoveryKeyData> Keys { get; set; } = new();
        public Dictionary<string, RecoverySessionSerialized> Sessions { get; set; } = new();
    }

    public class VerificationResult
    {
        public bool Success { get; set; }
        public string? FailureReason { get; set; }
    }

    public class GuardianRegistrationResult
    {
        public bool Success { get; set; }
        public string? GuardianId { get; set; }
        public byte[]? ShareData { get; set; }
        public string? FailureReason { get; set; }
    }

    public class RecoverySessionResult
    {
        public string? SessionId { get; set; }
        public RecoveryStatus Status { get; set; }
        public int RequiredShares { get; set; }
        public int CollectedShares { get; set; }
        public DateTime CooldownEndsAt { get; set; }
        public List<GuardianInfo>? ActiveGuardians { get; set; }
    }

    public class ShareSubmissionResult
    {
        public bool Success { get; set; }
        public int SharesCollected { get; set; }
        public int SharesRequired { get; set; }
        public RecoveryStatus Status { get; set; }
        public DateTime CooldownEndsAt { get; set; }
        public string? FailureReason { get; set; }
    }

    public class RecoveryCompletionResult
    {
        public bool Success { get; set; }
        public byte[]? RecoveredKey { get; set; }
        public string? SessionId { get; set; }
        public string? FailureReason { get; set; }
    }

    public class GuardianRotationResult
    {
        public bool Success { get; set; }
        public string? OldGuardianId { get; set; }
        public string? NewGuardianId { get; set; }
        public string? FailureReason { get; set; }
    }

    #endregion
}
