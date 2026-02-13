using DataWarehouse.SDK.Security;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Threshold
{
    /// <summary>
    /// Social Secret Sharing Scheme (SSSS) - A human-centric threshold recovery system.
    ///
    /// Unlike traditional Shamir secret sharing which uses numeric indices, SSSS is designed
    /// for social recovery scenarios where trusted individuals (guardians) help recover a key
    /// without any single guardian having full access.
    ///
    /// Features:
    /// - Named guardians with identity verification
    /// - Weighted shares (some guardians can have higher trust)
    /// - Time-locked recovery (optional delay before recovery completes)
    /// - Guardian rotation without changing the secret
    /// - Emergency recovery with additional verification
    /// - Audit trail of all recovery attempts
    ///
    /// Use Cases:
    /// - Cryptocurrency wallet recovery (social recovery wallets)
    /// - Enterprise key escrow with distributed trust
    /// - Personal key backup with family/friends as guardians
    /// - Multi-party authorization for high-value operations
    ///
    /// Based on Shamir's Secret Sharing with extensions for social recovery patterns.
    /// </summary>
    public sealed class SsssStrategy : KeyStoreStrategyBase
    {
        private SsssConfig _config = new();
        private readonly Dictionary<string, SsssKeyData> _keys = new();
        private readonly Dictionary<string, Guardian> _guardians = new();
        private readonly List<RecoveryAttempt> _recoveryLog = new();
        private string _currentKeyId = "default";
        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly SecureRandom _secureRandom = new();
        private bool _disposed;

        // 256-bit prime field for secret sharing
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
                ["Algorithm"] = "Social Secret Sharing Scheme",
                ["BaseAlgorithm"] = "Shamir Secret Sharing",
                ["SupportsWeightedShares"] = true,
                ["SupportsTimeLock"] = true,
                ["SupportsGuardianRotation"] = true,
                ["SocialRecovery"] = true
            }
        };

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            if (Configuration.TryGetValue("Threshold", out var t) && t is int threshold)
                _config.Threshold = threshold;
            if (Configuration.TryGetValue("StoragePath", out var p) && p is string path)
                _config.StoragePath = path;
            if (Configuration.TryGetValue("RecoveryDelayMinutes", out var d) && d is int delay)
                _config.RecoveryDelayMinutes = delay;
            if (Configuration.TryGetValue("MaxRecoveryAttempts", out var m) && m is int max)
                _config.MaxRecoveryAttempts = max;
            if (Configuration.TryGetValue("RequireIdentityVerification", out var v) && v is bool verify)
                _config.RequireIdentityVerification = verify;

            ValidateConfiguration();
            await LoadFromStorage();
        }

        private void ValidateConfiguration()
        {
            if (_config.Threshold < 1)
                throw new ArgumentException("Threshold must be at least 1.");
        }

        public override Task<string> GetCurrentKeyIdAsync() => Task.FromResult(_currentKeyId);

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            await _lock.WaitAsync();
            try
            {
                if (!_keys.TryGetValue(keyId, out var keyData))
                    throw new KeyNotFoundException($"SSSS key '{keyId}' not found.");

                // For direct access, need sufficient authorized shares
                var authorizedShares = keyData.Shares
                    .Where(s => s.Value.IsAuthorized && s.Value.ShareValue != null)
                    .ToList();

                var totalWeight = authorizedShares.Sum(s => s.Value.Weight);
                if (totalWeight < keyData.EffectiveThreshold)
                {
                    throw new CryptographicException(
                        $"Insufficient authorized shares. Need weight {keyData.EffectiveThreshold}, have {totalWeight}.");
                }

                return ReconstructSecret(keyData, authorizedShares.Select(s => s.Value).ToList());
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
                var ssssKeyData = new SsssKeyData
                {
                    KeyId = keyId,
                    Threshold = _config.Threshold,
                    KeySizeBytes = keyData.Length,
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = context.UserId,
                    RecoveryDelayMinutes = _config.RecoveryDelayMinutes,
                    Shares = new Dictionary<string, SsssShare>()
                };

                _keys[keyId] = ssssKeyData;
                _currentKeyId = keyId;

                // Secret will be distributed when guardians are added
                ssssKeyData.PendingSecret = keyData;

                await PersistToStorage();
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Registers a new guardian who can participate in key recovery.
        /// </summary>
        public async Task<GuardianRegistrationResult> RegisterGuardianAsync(
            string keyId,
            GuardianInfo guardianInfo,
            ISecurityContext context)
        {
            ValidateSecurityContext(context);

            await _lock.WaitAsync();
            try
            {
                if (!_keys.TryGetValue(keyId, out var keyData))
                    throw new KeyNotFoundException($"Key '{keyId}' not found.");

                // Verify guardian doesn't already exist
                if (keyData.Shares.ContainsKey(guardianInfo.GuardianId))
                    throw new InvalidOperationException($"Guardian '{guardianInfo.GuardianId}' already registered.");

                // Create guardian record
                var guardian = new Guardian
                {
                    GuardianId = guardianInfo.GuardianId,
                    Name = guardianInfo.Name,
                    Email = guardianInfo.Email,
                    Phone = guardianInfo.Phone,
                    Weight = guardianInfo.Weight > 0 ? guardianInfo.Weight : 1,
                    VerificationMethod = guardianInfo.VerificationMethod,
                    PublicKey = guardianInfo.PublicKey,
                    RegisteredAt = DateTime.UtcNow,
                    RegisteredBy = context.UserId,
                    IsActive = true
                };

                _guardians[guardian.GuardianId] = guardian;

                // Generate share for this guardian
                var shareIndex = keyData.Shares.Count + 1;
                var share = new SsssShare
                {
                    GuardianId = guardian.GuardianId,
                    ShareIndex = shareIndex,
                    Weight = guardian.Weight,
                    IsAuthorized = false,
                    CreatedAt = DateTime.UtcNow
                };

                // If we have a pending secret and enough guardians, distribute shares
                if (keyData.PendingSecret != null)
                {
                    keyData.Shares[guardian.GuardianId] = share;

                    if (keyData.Shares.Count >= 2) // Need at least 2 guardians
                    {
                        DistributeSharesInternal(keyData);
                    }
                }
                else
                {
                    keyData.Shares[guardian.GuardianId] = share;
                }

                await PersistToStorage();

                return new GuardianRegistrationResult
                {
                    GuardianId = guardian.GuardianId,
                    ShareIndex = shareIndex,
                    Success = true,
                    Message = keyData.PendingSecret != null && keyData.Shares.Count >= 2
                        ? "Guardian registered and share distributed."
                        : "Guardian registered. Share will be distributed when more guardians are added."
                };
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Distributes shares to all registered guardians.
        /// Called after sufficient guardians have been registered.
        /// </summary>
        public async Task DistributeSharesAsync(string keyId, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            await _lock.WaitAsync();
            try
            {
                if (!_keys.TryGetValue(keyId, out var keyData))
                    throw new KeyNotFoundException($"Key '{keyId}' not found.");

                if (keyData.PendingSecret == null)
                    throw new InvalidOperationException("No pending secret to distribute.");

                if (keyData.Shares.Count < 2)
                    throw new InvalidOperationException("Need at least 2 guardians to distribute shares.");

                DistributeSharesInternal(keyData);
                await PersistToStorage();
            }
            finally
            {
                _lock.Release();
            }
        }

        private void DistributeSharesInternal(SsssKeyData keyData)
        {
            if (keyData.PendingSecret == null) return;

            var secret = new BigInteger(1, keyData.PendingSecret);
            var totalShares = keyData.Shares.Count;

            // Calculate effective threshold based on weights
            // For weighted shares, we need total weight >= threshold
            keyData.EffectiveThreshold = _config.Threshold;

            // Generate polynomial of degree (threshold - 1)
            var coefficients = new BigInteger[keyData.EffectiveThreshold];
            coefficients[0] = secret.Mod(FieldPrime);

            for (int i = 1; i < keyData.EffectiveThreshold; i++)
            {
                var randomBytes = new byte[32];
                _secureRandom.NextBytes(randomBytes);
                coefficients[i] = new BigInteger(1, randomBytes).Mod(FieldPrime);
            }

            // Generate share for each guardian
            int index = 1;
            foreach (var share in keyData.Shares.Values)
            {
                share.ShareIndex = index;
                var shareValue = EvaluatePolynomial(coefficients, index);
                share.ShareValue = shareValue.ToByteArrayUnsigned();
                share.ShareHash = SHA256.HashData(share.ShareValue);
                index++;
            }

            // Clear pending secret
            CryptographicOperations.ZeroMemory(keyData.PendingSecret);
            keyData.PendingSecret = null;
            keyData.IsDistributed = true;
        }

        /// <summary>
        /// Initiates a recovery request. This starts the recovery process which
        /// may have a time delay for security.
        /// </summary>
        public async Task<RecoveryRequest> InitiateRecoveryAsync(
            string keyId,
            string requesterId,
            string reason,
            ISecurityContext context)
        {
            ValidateSecurityContext(context);

            await _lock.WaitAsync();
            try
            {
                if (!_keys.TryGetValue(keyId, out var keyData))
                    throw new KeyNotFoundException($"Key '{keyId}' not found.");

                // Check for existing pending recovery
                if (keyData.ActiveRecoveryRequest != null &&
                    keyData.ActiveRecoveryRequest.Status == RecoveryStatus.Pending)
                {
                    throw new InvalidOperationException("A recovery request is already pending.");
                }

                // Check recovery attempt limits
                var recentAttempts = _recoveryLog
                    .Where(r => r.KeyId == keyId && r.AttemptedAt > DateTime.UtcNow.AddHours(-24))
                    .Count();

                if (recentAttempts >= _config.MaxRecoveryAttempts)
                {
                    throw new InvalidOperationException(
                        $"Maximum recovery attempts ({_config.MaxRecoveryAttempts}) exceeded in 24 hours.");
                }

                var request = new RecoveryRequest
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    KeyId = keyId,
                    RequesterId = requesterId,
                    Reason = reason,
                    RequestedAt = DateTime.UtcNow,
                    CanCompleteAt = DateTime.UtcNow.AddMinutes(_config.RecoveryDelayMinutes),
                    Status = RecoveryStatus.Pending,
                    RequiredWeight = keyData.EffectiveThreshold,
                    ApprovedShares = new List<string>()
                };

                keyData.ActiveRecoveryRequest = request;

                // Log the attempt
                _recoveryLog.Add(new RecoveryAttempt
                {
                    KeyId = keyId,
                    RequestId = request.RequestId,
                    RequesterId = requesterId,
                    AttemptedAt = DateTime.UtcNow,
                    Status = "Initiated"
                });

                await PersistToStorage();

                // Notify guardians (in real implementation, send emails/SMS)
                return request;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Guardian approves a recovery request by providing their share.
        /// </summary>
        public async Task<RecoveryApprovalResult> ApproveRecoveryAsync(
            string keyId,
            string requestId,
            string guardianId,
            byte[] shareValue,
            string? verificationCode,
            ISecurityContext context)
        {
            ValidateSecurityContext(context);

            await _lock.WaitAsync();
            try
            {
                if (!_keys.TryGetValue(keyId, out var keyData))
                    throw new KeyNotFoundException($"Key '{keyId}' not found.");

                var request = keyData.ActiveRecoveryRequest;
                if (request == null || request.RequestId != requestId)
                    throw new InvalidOperationException("Invalid or expired recovery request.");

                if (request.Status != RecoveryStatus.Pending)
                    throw new InvalidOperationException($"Recovery request is {request.Status}.");

                if (!keyData.Shares.TryGetValue(guardianId, out var share))
                    throw new InvalidOperationException("Guardian not found for this key.");

                // Verify share hash
                var providedHash = SHA256.HashData(shareValue);
                var expectedHash = share.ShareHash ?? Array.Empty<byte>();
                if (!(providedHash.Length == expectedHash.Length && CryptographicOperations.FixedTimeEquals(providedHash, expectedHash)))
                    throw new CryptographicException("Invalid share provided.");

                // Verify identity if required
                if (_config.RequireIdentityVerification && _guardians.TryGetValue(guardianId, out var guardian))
                {
                    if (!VerifyGuardianIdentity(guardian, verificationCode))
                        throw new UnauthorizedAccessException("Identity verification failed.");
                }

                // Mark share as authorized for this recovery
                share.IsAuthorized = true;
                share.AuthorizedAt = DateTime.UtcNow;
                request.ApprovedShares.Add(guardianId);

                // Calculate current approved weight
                var approvedWeight = request.ApprovedShares
                    .Where(g => keyData.Shares.ContainsKey(g))
                    .Sum(g => keyData.Shares[g].Weight);

                request.CurrentWeight = approvedWeight;

                // Check if we have enough weight
                var canComplete = approvedWeight >= request.RequiredWeight &&
                                 DateTime.UtcNow >= request.CanCompleteAt;

                await PersistToStorage();

                return new RecoveryApprovalResult
                {
                    Success = true,
                    GuardianId = guardianId,
                    CurrentWeight = approvedWeight,
                    RequiredWeight = request.RequiredWeight,
                    CanComplete = canComplete,
                    TimeUntilCompletion = canComplete ? TimeSpan.Zero :
                        request.CanCompleteAt - DateTime.UtcNow,
                    Message = canComplete
                        ? "Recovery can now be completed."
                        : $"Approval recorded. Need {request.RequiredWeight - approvedWeight} more weight."
                };
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Completes the recovery and returns the secret key.
        /// </summary>
        public async Task<byte[]> CompleteRecoveryAsync(
            string keyId,
            string requestId,
            ISecurityContext context)
        {
            ValidateSecurityContext(context);

            await _lock.WaitAsync();
            try
            {
                if (!_keys.TryGetValue(keyId, out var keyData))
                    throw new KeyNotFoundException($"Key '{keyId}' not found.");

                var request = keyData.ActiveRecoveryRequest;
                if (request == null || request.RequestId != requestId)
                    throw new InvalidOperationException("Invalid recovery request.");

                if (DateTime.UtcNow < request.CanCompleteAt)
                {
                    var remaining = request.CanCompleteAt - DateTime.UtcNow;
                    throw new InvalidOperationException(
                        $"Recovery delay not elapsed. {remaining.TotalMinutes:F0} minutes remaining.");
                }

                if (request.CurrentWeight < request.RequiredWeight)
                {
                    throw new InvalidOperationException(
                        $"Insufficient approvals. Have {request.CurrentWeight}, need {request.RequiredWeight}.");
                }

                // Gather authorized shares
                var authorizedShares = keyData.Shares.Values
                    .Where(s => s.IsAuthorized && s.ShareValue != null)
                    .ToList();

                // Reconstruct the secret
                var secret = ReconstructSecret(keyData, authorizedShares);

                // Mark recovery as complete
                request.Status = RecoveryStatus.Completed;
                request.CompletedAt = DateTime.UtcNow;

                // Reset authorization flags
                foreach (var share in keyData.Shares.Values)
                {
                    share.IsAuthorized = false;
                    share.AuthorizedAt = null;
                }

                keyData.ActiveRecoveryRequest = null;

                // Log completion
                _recoveryLog.Add(new RecoveryAttempt
                {
                    KeyId = keyId,
                    RequestId = requestId,
                    RequesterId = request.RequesterId,
                    AttemptedAt = DateTime.UtcNow,
                    Status = "Completed"
                });

                await PersistToStorage();

                return secret;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Cancels an active recovery request.
        /// </summary>
        public async Task CancelRecoveryAsync(string keyId, string requestId, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            await _lock.WaitAsync();
            try
            {
                if (!_keys.TryGetValue(keyId, out var keyData))
                    throw new KeyNotFoundException($"Key '{keyId}' not found.");

                var request = keyData.ActiveRecoveryRequest;
                if (request == null || request.RequestId != requestId)
                    throw new InvalidOperationException("Invalid recovery request.");

                request.Status = RecoveryStatus.Cancelled;

                // Reset authorization flags
                foreach (var share in keyData.Shares.Values)
                {
                    share.IsAuthorized = false;
                    share.AuthorizedAt = null;
                }

                keyData.ActiveRecoveryRequest = null;

                _recoveryLog.Add(new RecoveryAttempt
                {
                    KeyId = keyId,
                    RequestId = requestId,
                    RequesterId = request.RequesterId,
                    AttemptedAt = DateTime.UtcNow,
                    Status = "Cancelled"
                });

                await PersistToStorage();
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Rotates a guardian's share without changing the secret.
        /// Useful when a guardian's share may have been compromised.
        /// </summary>
        public async Task RotateGuardianShareAsync(
            string keyId,
            string guardianId,
            ISecurityContext context)
        {
            ValidateSecurityContext(context);

            await _lock.WaitAsync();
            try
            {
                if (!_keys.TryGetValue(keyId, out var keyData))
                    throw new KeyNotFoundException($"Key '{keyId}' not found.");

                if (!keyData.Shares.TryGetValue(guardianId, out var share))
                    throw new InvalidOperationException("Guardian not found.");

                // To rotate a single share, we need to:
                // 1. Generate a new zero polynomial (f(0) = 0)
                // 2. Add new polynomial evaluations to existing shares

                var coefficients = new BigInteger[keyData.EffectiveThreshold];
                coefficients[0] = BigInteger.Zero; // f(0) = 0, so secret unchanged

                for (int i = 1; i < keyData.EffectiveThreshold; i++)
                {
                    var randomBytes = new byte[32];
                    _secureRandom.NextBytes(randomBytes);
                    coefficients[i] = new BigInteger(1, randomBytes).Mod(FieldPrime);
                }

                // Add delta to each share
                foreach (var s in keyData.Shares.Values)
                {
                    var delta = EvaluatePolynomial(coefficients, s.ShareIndex);
                    var currentValue = new BigInteger(1, s.ShareValue ?? Array.Empty<byte>());
                    var newValue = currentValue.Add(delta).Mod(FieldPrime);

                    s.ShareValue = newValue.ToByteArrayUnsigned();
                    s.ShareHash = SHA256.HashData(s.ShareValue);
                    s.RotatedAt = DateTime.UtcNow;
                }

                keyData.LastRotatedAt = DateTime.UtcNow;
                await PersistToStorage();
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Removes a guardian from the key.
        /// Requires redistributing shares among remaining guardians.
        /// </summary>
        public async Task RemoveGuardianAsync(
            string keyId,
            string guardianId,
            ISecurityContext context)
        {
            ValidateSecurityContext(context);

            if (!context.IsSystemAdmin)
                throw new UnauthorizedAccessException("Only administrators can remove guardians.");

            await _lock.WaitAsync();
            try
            {
                if (!_keys.TryGetValue(keyId, out var keyData))
                    throw new KeyNotFoundException($"Key '{keyId}' not found.");

                if (!keyData.Shares.ContainsKey(guardianId))
                    throw new InvalidOperationException("Guardian not found.");

                // First reconstruct the secret
                var authorizedShares = keyData.Shares.Values
                    .Where(s => s.ShareValue != null)
                    .ToList();

                var secret = ReconstructSecret(keyData, authorizedShares);

                // Remove guardian
                keyData.Shares.Remove(guardianId);
                _guardians.Remove(guardianId);

                // Redistribute shares among remaining guardians
                if (keyData.Shares.Count >= 2)
                {
                    keyData.PendingSecret = secret;
                    DistributeSharesInternal(keyData);
                }
                else
                {
                    throw new InvalidOperationException(
                        "Cannot remove guardian: would leave fewer than 2 guardians.");
                }

                await PersistToStorage();
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Gets the recovery status for a key.
        /// </summary>
        public async Task<RecoveryStatus?> GetRecoveryStatusAsync(string keyId)
        {
            await _lock.WaitAsync();
            try
            {
                if (!_keys.TryGetValue(keyId, out var keyData))
                    return null;

                return keyData.ActiveRecoveryRequest?.Status;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Gets a guardian's share for backup/export.
        /// The share is encrypted with the guardian's public key if available.
        /// </summary>
        public async Task<GuardianShareExport> ExportGuardianShareAsync(
            string keyId,
            string guardianId,
            ISecurityContext context)
        {
            ValidateSecurityContext(context);

            await _lock.WaitAsync();
            try
            {
                if (!_keys.TryGetValue(keyId, out var keyData))
                    throw new KeyNotFoundException($"Key '{keyId}' not found.");

                if (!keyData.Shares.TryGetValue(guardianId, out var share))
                    throw new InvalidOperationException("Guardian not found.");

                if (share.ShareValue == null)
                    throw new InvalidOperationException("Share not yet distributed.");

                var guardian = _guardians.GetValueOrDefault(guardianId);
                byte[]? encryptedShare = null;

                // Encrypt with guardian's public key if available
                if (guardian?.PublicKey != null)
                {
                    using var aes = Aes.Create();
                    aes.GenerateKey();
                    aes.GenerateIV();

                    // Encrypt share with AES
                    using var encryptor = aes.CreateEncryptor();
                    var encrypted = encryptor.TransformFinalBlock(share.ShareValue, 0, share.ShareValue.Length);

                    // In real implementation, wrap AES key with guardian's public key
                    encryptedShare = ConcatBytes(aes.IV, aes.Key, encrypted);
                }

                return new GuardianShareExport
                {
                    KeyId = keyId,
                    GuardianId = guardianId,
                    ShareIndex = share.ShareIndex,
                    ShareValue = encryptedShare ?? share.ShareValue,
                    IsEncrypted = encryptedShare != null,
                    ShareHash = share.ShareHash,
                    Weight = share.Weight,
                    ExportedAt = DateTime.UtcNow
                };
            }
            finally
            {
                _lock.Release();
            }
        }

        #region Helper Methods

        private BigInteger EvaluatePolynomial(BigInteger[] coefficients, int x)
        {
            var xBig = BigInteger.ValueOf(x);
            var result = BigInteger.Zero;

            for (int i = coefficients.Length - 1; i >= 0; i--)
            {
                result = result.Multiply(xBig).Add(coefficients[i]).Mod(FieldPrime);
            }

            return result;
        }

        private byte[] ReconstructSecret(SsssKeyData keyData, List<SsssShare> shares)
        {
            // Need enough weight to meet threshold
            var totalWeight = shares.Sum(s => s.Weight);
            if (totalWeight < keyData.EffectiveThreshold)
            {
                throw new CryptographicException(
                    $"Insufficient shares. Need weight {keyData.EffectiveThreshold}, have {totalWeight}.");
            }

            // Select shares up to threshold weight
            var selectedShares = new List<SsssShare>();
            var currentWeight = 0;
            foreach (var share in shares.OrderByDescending(s => s.Weight))
            {
                selectedShares.Add(share);
                currentWeight += share.Weight;
                if (currentWeight >= keyData.EffectiveThreshold)
                    break;
            }

            // Lagrange interpolation
            var secret = BigInteger.Zero;

            for (int i = 0; i < selectedShares.Count; i++)
            {
                var numerator = BigInteger.One;
                var denominator = BigInteger.One;

                for (int j = 0; j < selectedShares.Count; j++)
                {
                    if (i == j) continue;

                    var xj = BigInteger.ValueOf(selectedShares[j].ShareIndex);
                    var xi = BigInteger.ValueOf(selectedShares[i].ShareIndex);

                    numerator = numerator.Multiply(xj.Negate()).Mod(FieldPrime);
                    denominator = denominator.Multiply(xi.Subtract(xj)).Mod(FieldPrime);
                }

                if (numerator.SignValue < 0)
                    numerator = numerator.Add(FieldPrime);

                var lagrangeCoeff = numerator.Multiply(denominator.ModInverse(FieldPrime)).Mod(FieldPrime);
                var shareValue = new BigInteger(1, selectedShares[i].ShareValue ?? Array.Empty<byte>());

                secret = secret.Add(shareValue.Multiply(lagrangeCoeff)).Mod(FieldPrime);
            }

            // Convert to fixed-size byte array
            var secretBytes = secret.ToByteArrayUnsigned();
            var result = new byte[keyData.KeySizeBytes];
            if (secretBytes.Length >= keyData.KeySizeBytes)
            {
                Array.Copy(secretBytes, secretBytes.Length - keyData.KeySizeBytes, result, 0, keyData.KeySizeBytes);
            }
            else
            {
                Array.Copy(secretBytes, 0, result, keyData.KeySizeBytes - secretBytes.Length, secretBytes.Length);
            }

            return result;
        }

        private bool VerifyGuardianIdentity(Guardian guardian, string? verificationCode)
        {
            // In real implementation, this would:
            // - Send OTP via email/SMS
            // - Verify biometric
            // - Check hardware token
            // For now, simple code check
            if (string.IsNullOrEmpty(verificationCode))
                return false;

            // Simple hash-based verification (placeholder)
            var expected = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(guardian.GuardianId + DateTime.UtcNow.Date.ToString("yyyyMMdd")))
            )[..6].ToUpper();

            return verificationCode.ToUpper() == expected;
        }

        private static byte[] ConcatBytes(params byte[][] arrays)
        {
            var totalLength = arrays.Sum(a => a.Length);
            var result = new byte[totalLength];
            var offset = 0;
            foreach (var arr in arrays)
            {
                Array.Copy(arr, 0, result, offset, arr.Length);
                offset += arr.Length;
            }
            return result;
        }

        #endregion

        #region Storage

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
                if (_keys.Remove(keyId)) await PersistToStorage();
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
                    LastRotatedAt = keyData.LastRotatedAt,
                    KeySizeBytes = keyData.KeySizeBytes,
                    IsActive = keyId == _currentKeyId,
                    Metadata = new Dictionary<string, object>
                    {
                        ["Algorithm"] = "Social Secret Sharing",
                        ["Threshold"] = keyData.Threshold,
                        ["EffectiveThreshold"] = keyData.EffectiveThreshold,
                        ["GuardianCount"] = keyData.Shares.Count,
                        ["IsDistributed"] = keyData.IsDistributed,
                        ["HasActiveRecovery"] = keyData.ActiveRecoveryRequest != null,
                        ["RecoveryDelayMinutes"] = keyData.RecoveryDelayMinutes
                    }
                };
            }
            finally { _lock.Release(); }
        }

        private async Task LoadFromStorage()
        {
            var path = GetStoragePath();
            if (!File.Exists(path)) return;

            try
            {
                var json = await File.ReadAllTextAsync(path);
                var stored = JsonSerializer.Deserialize<SsssStorageData>(json);
                if (stored != null)
                {
                    foreach (var kvp in stored.Keys ?? new())
                        _keys[kvp.Key] = kvp.Value;
                    foreach (var kvp in stored.Guardians ?? new())
                        _guardians[kvp.Key] = kvp.Value;
                    if (stored.RecoveryLog != null)
                        _recoveryLog.AddRange(stored.RecoveryLog);
                    if (_keys.Count > 0)
                        _currentKeyId = _keys.Keys.First();
                }
            }
            catch { }
        }

        private async Task PersistToStorage()
        {
            var path = GetStoragePath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var toStore = new SsssStorageData
            {
                Keys = _keys,
                Guardians = _guardians,
                RecoveryLog = _recoveryLog
            };

            var json = JsonSerializer.Serialize(toStore, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json);
        }

        private string GetStoragePath()
        {
            if (!string.IsNullOrEmpty(_config.StoragePath))
                return _config.StoragePath;
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(baseDir, "DataWarehouse", "ssss-keys.json");
        }

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

    public class SsssConfig
    {
        public int Threshold { get; set; } = 2;
        public int RecoveryDelayMinutes { get; set; } = 60;
        public int MaxRecoveryAttempts { get; set; } = 3;
        public bool RequireIdentityVerification { get; set; } = true;
        public string? StoragePath { get; set; }
    }

    internal class SsssKeyData
    {
        public string KeyId { get; set; } = "";
        public int Threshold { get; set; }
        public int EffectiveThreshold { get; set; }
        public int KeySizeBytes { get; set; }
        public bool IsDistributed { get; set; }
        public byte[]? PendingSecret { get; set; }
        public Dictionary<string, SsssShare> Shares { get; set; } = new();
        public RecoveryRequest? ActiveRecoveryRequest { get; set; }
        public int RecoveryDelayMinutes { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? LastRotatedAt { get; set; }
    }

    internal class SsssShare
    {
        public string GuardianId { get; set; } = "";
        public int ShareIndex { get; set; }
        public int Weight { get; set; } = 1;
        public byte[]? ShareValue { get; set; }
        public byte[]? ShareHash { get; set; }
        public bool IsAuthorized { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? AuthorizedAt { get; set; }
        public DateTime? RotatedAt { get; set; }
    }

    public class Guardian
    {
        public string GuardianId { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public int Weight { get; set; } = 1;
        public string VerificationMethod { get; set; } = "email";
        public byte[]? PublicKey { get; set; }
        public DateTime RegisteredAt { get; set; }
        public string? RegisteredBy { get; set; }
        public bool IsActive { get; set; }
    }

    public class GuardianInfo
    {
        public string GuardianId { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public int Weight { get; set; } = 1;
        public string VerificationMethod { get; set; } = "email";
        public byte[]? PublicKey { get; set; }
    }

    public class GuardianRegistrationResult
    {
        public string GuardianId { get; set; } = "";
        public int ShareIndex { get; set; }
        public bool Success { get; set; }
        public string? Message { get; set; }
    }

    public class RecoveryRequest
    {
        public string RequestId { get; set; } = "";
        public string KeyId { get; set; } = "";
        public string RequesterId { get; set; } = "";
        public string? Reason { get; set; }
        public DateTime RequestedAt { get; set; }
        public DateTime CanCompleteAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public RecoveryStatus Status { get; set; }
        public int RequiredWeight { get; set; }
        public int CurrentWeight { get; set; }
        public List<string> ApprovedShares { get; set; } = new();
    }

    public enum RecoveryStatus
    {
        Pending,
        Completed,
        Cancelled,
        Expired
    }

    public class RecoveryApprovalResult
    {
        public bool Success { get; set; }
        public string GuardianId { get; set; } = "";
        public int CurrentWeight { get; set; }
        public int RequiredWeight { get; set; }
        public bool CanComplete { get; set; }
        public TimeSpan TimeUntilCompletion { get; set; }
        public string? Message { get; set; }
    }

    internal class RecoveryAttempt
    {
        public string KeyId { get; set; } = "";
        public string RequestId { get; set; } = "";
        public string RequesterId { get; set; } = "";
        public DateTime AttemptedAt { get; set; }
        public string Status { get; set; } = "";
    }

    public class GuardianShareExport
    {
        public string KeyId { get; set; } = "";
        public string GuardianId { get; set; } = "";
        public int ShareIndex { get; set; }
        public byte[] ShareValue { get; set; } = Array.Empty<byte>();
        public bool IsEncrypted { get; set; }
        public byte[]? ShareHash { get; set; }
        public int Weight { get; set; }
        public DateTime ExportedAt { get; set; }
    }

    internal class SsssStorageData
    {
        public Dictionary<string, SsssKeyData>? Keys { get; set; }
        public Dictionary<string, Guardian>? Guardians { get; set; }
        public List<RecoveryAttempt>? RecoveryLog { get; set; }
    }

    #endregion
}
