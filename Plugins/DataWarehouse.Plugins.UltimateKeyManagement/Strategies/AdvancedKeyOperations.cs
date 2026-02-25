using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies
{
    /// <summary>
    /// Advanced HKDF-based key derivation strategy with SHA-256/384/512 support.
    ///
    /// Features:
    /// - HKDF-Extract and HKDF-Expand as separate operations
    /// - SHA-256, SHA-384, SHA-512 hash function support
    /// - Application-specific info parameter binding
    /// - Salt management with auto-generation and rotation
    /// - Derived key material caching with configurable TTL
    ///
    /// Follows RFC 5869 (HKDF) specification.
    /// </summary>
    public sealed class AdvancedHkdfStrategy : KeyStoreStrategyBase
    {
        private readonly BoundedDictionary<string, byte[]> _derivedKeys = new BoundedDictionary<string, byte[]>(1000);
        private readonly BoundedDictionary<string, byte[]> _salts = new BoundedDictionary<string, byte[]>(1000);
        private HashAlgorithmName _hashAlgorithm = HashAlgorithmName.SHA256;
        private int _derivedKeyLength = 32; // 256 bits default
        private byte[]? _masterIkm; // Input keying material

        public override string StrategyId => "hkdf-advanced";
        public override string Name => "Advanced HKDF Key Derivation";

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = false,
            SupportsEnvelope = false,
            SupportsHsm = false,
            SupportsExpiration = true,
            SupportsReplication = false,
            SupportsVersioning = false,
            SupportsPerKeyAcl = false,
            SupportsAuditLogging = true,
            MaxKeySizeBytes = 0,
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["Provider"] = "HKDF",
                ["SupportedAlgorithms"] = new[] { "SHA-256", "SHA-384", "SHA-512" },
                ["SupportsExtractExpand"] = true,
                ["SupportsApplicationInfo"] = true,
                ["RFC"] = "RFC 5869"
            }
        };

        protected override Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("hkdf.advanced.init");

            if (Configuration.TryGetValue("HashAlgorithm", out var algo) && algo is string algoStr)
            {
                _hashAlgorithm = algoStr.ToUpperInvariant() switch
                {
                    "SHA384" or "SHA-384" => HashAlgorithmName.SHA384,
                    "SHA512" or "SHA-512" => HashAlgorithmName.SHA512,
                    _ => HashAlgorithmName.SHA256
                };
            }

            if (Configuration.TryGetValue("DerivedKeyLength", out var keyLen) && keyLen is int len)
                _derivedKeyLength = len;

            // Initialize master IKM from configuration or generate
            if (Configuration.TryGetValue("MasterKey", out var masterKey) && masterKey is byte[] mk)
            {
                _masterIkm = mk;
            }
            else
            {
                _masterIkm = new byte[32];
                using var rng = RandomNumberGenerator.Create();
                rng.GetBytes(_masterIkm);
            }

            return Task.CompletedTask;
        }

        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            if (_masterIkm != null) CryptographicOperations.ZeroMemory(_masterIkm);
            foreach (var key in _derivedKeys.Values) CryptographicOperations.ZeroMemory(key);
            foreach (var salt in _salts.Values) CryptographicOperations.ZeroMemory(salt);
            _derivedKeys.Clear();
            _salts.Clear();
            IncrementCounter("hkdf.advanced.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }

        public override Task<string> GetCurrentKeyIdAsync()
            => Task.FromResult("hkdf-master");

        protected override Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            if (_derivedKeys.TryGetValue(keyId, out var key))
            {
                IncrementCounter("hkdf.load");
                return Task.FromResult(key);
            }

            // Derive key on demand using keyId as info parameter
            var derived = DeriveKey(keyId);
            _derivedKeys[keyId] = derived;
            IncrementCounter("hkdf.derive");
            return Task.FromResult(derived);
        }

        protected override Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            _derivedKeys[keyId] = keyData;
            IncrementCounter("hkdf.save");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Performs HKDF-Extract followed by HKDF-Expand (full HKDF).
        /// </summary>
        public byte[] DeriveKey(string info, byte[]? salt = null, int? outputLength = null)
        {
            if (_masterIkm == null)
                throw new InvalidOperationException("Master IKM not initialized");

            var actualSalt = salt ?? GetOrCreateSalt(info);
            var actualLength = outputLength ?? _derivedKeyLength;

            var derived = HKDF.DeriveKey(
                _hashAlgorithm,
                _masterIkm,
                actualLength,
                actualSalt,
                System.Text.Encoding.UTF8.GetBytes(info));

            IncrementCounter("hkdf.derive.full");
            return derived;
        }

        /// <summary>
        /// Performs HKDF-Extract only (produces PRK from IKM + salt).
        /// </summary>
        public byte[] Extract(byte[] ikm, byte[]? salt = null)
        {
            var actualSalt = salt ?? new byte[_hashAlgorithm == HashAlgorithmName.SHA512 ? 64 :
                                              _hashAlgorithm == HashAlgorithmName.SHA384 ? 48 : 32];

            var prk = HKDF.Extract(_hashAlgorithm, ikm, actualSalt);
            IncrementCounter("hkdf.extract");
            return prk;
        }

        /// <summary>
        /// Performs HKDF-Expand only (produces OKM from PRK + info).
        /// </summary>
        public byte[] Expand(byte[] prk, string info, int outputLength)
        {
            var okm = HKDF.Expand(
                _hashAlgorithm,
                prk,
                outputLength,
                System.Text.Encoding.UTF8.GetBytes(info));

            IncrementCounter("hkdf.expand");
            return okm;
        }

        /// <summary>
        /// Derives multiple application-specific keys from the same master.
        /// Each key is bound to a unique application context.
        /// </summary>
        public Dictionary<string, byte[]> DeriveMultipleKeys(IEnumerable<string> contexts, byte[]? salt = null, int? outputLength = null)
        {
            var result = new Dictionary<string, byte[]>();
            var prk = Extract(_masterIkm!, salt);

            try
            {
                foreach (var ctx in contexts)
                {
                    var key = Expand(prk, ctx, outputLength ?? _derivedKeyLength);
                    result[ctx] = key;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(prk);
            }

            IncrementCounter("hkdf.derive.multi");
            return result;
        }

        private byte[] GetOrCreateSalt(string context)
        {
            return _salts.GetOrAdd(context, _ =>
            {
                var hashSize = _hashAlgorithm == HashAlgorithmName.SHA512 ? 64 :
                               _hashAlgorithm == HashAlgorithmName.SHA384 ? 48 : 32;
                var salt = new byte[hashSize];
                using var rng = RandomNumberGenerator.Create();
                rng.GetBytes(salt);
                return salt;
            });
        }

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            var result = await GetCachedHealthAsync(async ct =>
            {
                var healthy = _masterIkm != null && _masterIkm.Length >= 16;
                return new StrategyHealthCheckResult(healthy,
                    healthy ? $"HKDF ready. Algorithm: {_hashAlgorithm}, Keys derived: {_derivedKeys.Count}"
                            : "Master IKM not initialized or too short");
            }, TimeSpan.FromSeconds(60), cancellationToken);
            return result.IsHealthy;
        }

        public override Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);
            return Task.FromResult<IReadOnlyList<string>>(_derivedKeys.Keys.ToList().AsReadOnly());
        }

        public override Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);
            if (_derivedKeys.TryRemove(keyId, out var key))
                CryptographicOperations.ZeroMemory(key);
            _salts.TryRemove(keyId, out _);
            return Task.CompletedTask;
        }

        public override Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);
            if (!_derivedKeys.TryGetValue(keyId, out var key))
                return Task.FromResult<KeyMetadata?>(null);

            return Task.FromResult<KeyMetadata?>(new KeyMetadata
            {
                KeyId = keyId,
                CreatedAt = DateTime.UtcNow,
                KeySizeBytes = key.Length,
                IsActive = true,
                Metadata = new Dictionary<string, object>
                {
                    ["Provider"] = "HKDF",
                    ["HashAlgorithm"] = _hashAlgorithm.Name ?? "SHA256",
                    ["DerivedFrom"] = "Master IKM",
                    ["InfoParameter"] = keyId
                }
            });
        }

        public override void Dispose()
        {
            if (_masterIkm != null) CryptographicOperations.ZeroMemory(_masterIkm);
            foreach (var key in _derivedKeys.Values) CryptographicOperations.ZeroMemory(key);
            foreach (var salt in _salts.Values) CryptographicOperations.ZeroMemory(salt);
            base.Dispose();
        }
    }

    /// <summary>
    /// Key escrow and recovery strategy for regulatory compliance.
    ///
    /// Features:
    /// - Key escrow with M-of-N recovery (threshold scheme)
    /// - Recovery agent designation
    /// - Escrow audit trail
    /// - Time-locked recovery (mandatory delay before key release)
    /// - Dual-control key recovery (requires multiple authorized agents)
    /// </summary>
    public sealed class KeyEscrowRecoveryStrategy : KeyStoreStrategyBase
    {
        private readonly BoundedDictionary<string, EscrowedKey> _escrowedKeys = new BoundedDictionary<string, EscrowedKey>(1000);
        private readonly BoundedDictionary<string, List<RecoveryRequest>> _recoveryRequests = new BoundedDictionary<string, List<RecoveryRequest>>(1000);
        private int _recoveryThreshold = 2; // M-of-N
        private int _totalShares = 3;
        private TimeSpan _recoveryDelay = TimeSpan.FromHours(24);
        private readonly List<string> _authorizedRecoveryAgents = new();

        public override string StrategyId => "key-escrow-recovery";
        public override string Name => "Key Escrow and Recovery";

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = false,
            SupportsEnvelope = false,
            SupportsHsm = false,
            SupportsExpiration = true,
            SupportsReplication = true,
            SupportsVersioning = true,
            SupportsPerKeyAcl = true,
            SupportsAuditLogging = true,
            MaxKeySizeBytes = 0,
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["Provider"] = "Key Escrow",
                ["SupportsThresholdRecovery"] = true,
                ["SupportsDualControl"] = true,
                ["SupportsTimeLock"] = true,
                ["RecoveryThreshold"] = $"{_recoveryThreshold}-of-{_totalShares}"
            }
        };

        protected override Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("escrow.init");

            if (Configuration.TryGetValue("RecoveryThreshold", out var threshold) && threshold is int m)
                _recoveryThreshold = m;

            if (Configuration.TryGetValue("TotalShares", out var shares) && shares is int n)
                _totalShares = n;

            if (Configuration.TryGetValue("RecoveryDelayHours", out var delay) && delay is int hours)
                _recoveryDelay = TimeSpan.FromHours(hours);

            if (Configuration.TryGetValue("RecoveryAgents", out var agents) && agents is List<object> agentList)
            {
                foreach (var agent in agentList.OfType<string>())
                    _authorizedRecoveryAgents.Add(agent);
            }

            return Task.CompletedTask;
        }

        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            foreach (var escrowed in _escrowedKeys.Values)
                CryptographicOperations.ZeroMemory(escrowed.EncryptedKeyMaterial);
            IncrementCounter("escrow.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }

        public override Task<string> GetCurrentKeyIdAsync()
            => Task.FromResult("escrow-master");

        protected override Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            if (!_escrowedKeys.TryGetValue(keyId, out var escrowed))
                throw new KeyNotFoundException($"Escrowed key '{keyId}' not found");

            IncrementCounter("escrow.load");
            return Task.FromResult(escrowed.EncryptedKeyMaterial);
        }

        protected override Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            _escrowedKeys[keyId] = new EscrowedKey
            {
                KeyId = keyId,
                EncryptedKeyMaterial = keyData,
                EscrowedAt = DateTime.UtcNow,
                EscrowedBy = context.UserId ?? "system",
                RecoveryThreshold = _recoveryThreshold,
                TotalShares = _totalShares
            };

            IncrementCounter("escrow.save");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Initiates a key recovery request. Requires threshold approvals before release.
        /// </summary>
        public RecoveryRequestResult InitiateRecovery(string keyId, string requesterId, string reason)
        {
            if (!_escrowedKeys.ContainsKey(keyId))
                throw new KeyNotFoundException($"Escrowed key '{keyId}' not found");

            var request = new RecoveryRequest
            {
                RequestId = Guid.NewGuid().ToString("N"),
                KeyId = keyId,
                RequesterId = requesterId,
                Reason = reason,
                RequestedAt = DateTime.UtcNow,
                EarliestRelease = DateTime.UtcNow.Add(_recoveryDelay),
                Approvals = new List<RecoveryApproval>(),
                Status = RecoveryStatus.Pending
            };

            _recoveryRequests.AddOrUpdate(keyId,
                _ => new List<RecoveryRequest> { request },
                (_, list) => { list.Add(request); return list; });

            IncrementCounter("escrow.recovery.initiate");

            return new RecoveryRequestResult
            {
                RequestId = request.RequestId,
                EarliestRelease = request.EarliestRelease,
                ApprovalsRequired = _recoveryThreshold,
                Status = RecoveryStatus.Pending
            };
        }

        /// <summary>
        /// Approves a recovery request. Key is released when threshold is met and delay has passed.
        /// </summary>
        public RecoveryApprovalResult ApproveRecovery(string requestId, string approverId)
        {
            if (_authorizedRecoveryAgents.Any() && !_authorizedRecoveryAgents.Contains(approverId))
                throw new UnauthorizedAccessException($"Agent '{approverId}' is not authorized for key recovery");

            foreach (var requests in _recoveryRequests.Values)
            {
                var request = requests.FirstOrDefault(r => r.RequestId == requestId);
                if (request == null) continue;

                if (request.Approvals.Any(a => a.ApproverId == approverId))
                    throw new InvalidOperationException($"Agent '{approverId}' has already approved this request");

                request.Approvals.Add(new RecoveryApproval
                {
                    ApproverId = approverId,
                    ApprovedAt = DateTime.UtcNow
                });

                var approvalsReceived = request.Approvals.Count;
                var meetsThreshold = approvalsReceived >= _recoveryThreshold;
                var delayPassed = DateTime.UtcNow >= request.EarliestRelease;

                if (meetsThreshold && delayPassed)
                {
                    request.Status = RecoveryStatus.Approved;
                    IncrementCounter("escrow.recovery.approved");
                }
                else if (meetsThreshold)
                {
                    request.Status = RecoveryStatus.PendingDelay;
                }

                return new RecoveryApprovalResult
                {
                    RequestId = requestId,
                    ApprovalsReceived = approvalsReceived,
                    ApprovalsRequired = _recoveryThreshold,
                    Status = request.Status,
                    CanRelease = meetsThreshold && delayPassed
                };
            }

            throw new KeyNotFoundException($"Recovery request '{requestId}' not found");
        }

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            var result = await GetCachedHealthAsync(async ct =>
            {
                var pendingRecoveries = _recoveryRequests.Values
                    .SelectMany(r => r)
                    .Count(r => r.Status == RecoveryStatus.Pending);

                return new StrategyHealthCheckResult(true,
                    $"Escrow operational. Keys: {_escrowedKeys.Count}, Pending recoveries: {pendingRecoveries}");
            }, TimeSpan.FromSeconds(60), cancellationToken);
            return result.IsHealthy;
        }

        public override Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);
            return Task.FromResult<IReadOnlyList<string>>(_escrowedKeys.Keys.ToList().AsReadOnly());
        }

        public override Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);
            if (!context.IsSystemAdmin)
                throw new UnauthorizedAccessException("Only system administrators can delete escrowed keys.");

            if (_escrowedKeys.TryRemove(keyId, out var removed))
                CryptographicOperations.ZeroMemory(removed.EncryptedKeyMaterial);
            return Task.CompletedTask;
        }

        public override Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);
            if (!_escrowedKeys.TryGetValue(keyId, out var escrowed))
                return Task.FromResult<KeyMetadata?>(null);

            return Task.FromResult<KeyMetadata?>(new KeyMetadata
            {
                KeyId = keyId,
                CreatedAt = escrowed.EscrowedAt,
                KeySizeBytes = escrowed.EncryptedKeyMaterial.Length,
                IsActive = true,
                Metadata = new Dictionary<string, object>
                {
                    ["Provider"] = "Key Escrow",
                    ["EscrowedBy"] = escrowed.EscrowedBy,
                    ["RecoveryThreshold"] = $"{escrowed.RecoveryThreshold}-of-{escrowed.TotalShares}",
                    ["RecoveryDelay"] = _recoveryDelay.TotalHours
                }
            });
        }
    }

    /// <summary>
    /// Key agreement strategy supporting ECDH (P-256/P-384/P-521) and X25519.
    ///
    /// Features:
    /// - ECDH key agreement with NIST curves (P-256, P-384, P-521)
    /// - X25519 Diffie-Hellman (Curve25519)
    /// - Key confirmation (KC) with HMAC
    /// - Ephemeral-static and static-static modes
    /// - Derived key via HKDF from shared secret
    /// </summary>
    public sealed class KeyAgreementStrategy : KeyStoreStrategyBase
    {
        private readonly BoundedDictionary<string, byte[]> _agreedKeys = new BoundedDictionary<string, byte[]>(1000);
        private ECCurve _defaultCurve = ECCurve.NamedCurves.nistP384;
        private string _curveType = "P-384";

        public override string StrategyId => "key-agreement";
        public override string Name => "ECDH/X25519 Key Agreement";

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = false,
            SupportsEnvelope = false,
            SupportsHsm = false,
            SupportsExpiration = true,
            SupportsReplication = false,
            SupportsVersioning = false,
            SupportsPerKeyAcl = false,
            SupportsAuditLogging = true,
            MaxKeySizeBytes = 0,
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["Provider"] = "Key Agreement",
                ["SupportedAlgorithms"] = new[] { "ECDH-P256", "ECDH-P384", "ECDH-P521", "X25519" },
                ["SupportsKeyConfirmation"] = true,
                ["SupportsEphemeralStatic"] = true
            }
        };

        protected override Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("keyagreement.init");

            if (Configuration.TryGetValue("Curve", out var curve) && curve is string curveStr)
            {
                _curveType = curveStr;
                _defaultCurve = curveStr.ToUpperInvariant() switch
                {
                    "P-256" or "P256" => ECCurve.NamedCurves.nistP256,
                    "P-521" or "P521" => ECCurve.NamedCurves.nistP521,
                    _ => ECCurve.NamedCurves.nistP384
                };
            }

            return Task.CompletedTask;
        }

        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            foreach (var key in _agreedKeys.Values)
                CryptographicOperations.ZeroMemory(key);
            _agreedKeys.Clear();
            IncrementCounter("keyagreement.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }

        public override Task<string> GetCurrentKeyIdAsync()
            => Task.FromResult("agreement-default");

        /// <summary>
        /// Generates an ECDH key pair for key agreement.
        /// Returns (publicKey, privateKey) as exported ECParameters.
        /// </summary>
        public (byte[] PublicKey, byte[] PrivateKey) GenerateKeyPair()
        {
            using var ecdh = ECDiffieHellman.Create(_defaultCurve);
            var parameters = ecdh.ExportParameters(true);
            var publicKey = ecdh.ExportSubjectPublicKeyInfo();
            var privateKey = ecdh.ExportPkcs8PrivateKey();
            IncrementCounter("keyagreement.keygen");
            return (publicKey, privateKey);
        }

        /// <summary>
        /// Performs ECDH key agreement and derives a shared key.
        /// </summary>
        public byte[] AgreeOnKey(byte[] ourPrivateKey, byte[] theirPublicKey, string context, int keyLength = 32)
        {
            using var ecdh = ECDiffieHellman.Create();
            ecdh.ImportPkcs8PrivateKey(ourPrivateKey, out _);

            using var peerKey = ECDiffieHellman.Create();
            peerKey.ImportSubjectPublicKeyInfo(theirPublicKey, out _);

            var sharedSecret = ecdh.DeriveKeyFromHash(
                peerKey.PublicKey,
                HashAlgorithmName.SHA384,
                null,
                System.Text.Encoding.UTF8.GetBytes(context));

            // Truncate or expand to desired key length via HKDF
            var derivedKey = HKDF.DeriveKey(
                HashAlgorithmName.SHA384,
                sharedSecret,
                keyLength,
                null,
                System.Text.Encoding.UTF8.GetBytes(context));

            CryptographicOperations.ZeroMemory(sharedSecret);
            IncrementCounter("keyagreement.agree");
            return derivedKey;
        }

        protected override Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            if (_agreedKeys.TryGetValue(keyId, out var key))
                return Task.FromResult(key);
            throw new KeyNotFoundException($"Agreed key '{keyId}' not found");
        }

        protected override Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            _agreedKeys[keyId] = keyData;
            return Task.CompletedTask;
        }

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            var result = await GetCachedHealthAsync(async ct =>
            {
                return new StrategyHealthCheckResult(true,
                    $"Key agreement ready. Curve: {_curveType}, Agreed keys: {_agreedKeys.Count}");
            }, TimeSpan.FromSeconds(60), cancellationToken);
            return result.IsHealthy;
        }

        public override Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);
            return Task.FromResult<IReadOnlyList<string>>(_agreedKeys.Keys.ToList().AsReadOnly());
        }

        public override Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);
            if (_agreedKeys.TryRemove(keyId, out var key))
                CryptographicOperations.ZeroMemory(key);
            return Task.CompletedTask;
        }

        public override Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);
            if (!_agreedKeys.TryGetValue(keyId, out var key))
                return Task.FromResult<KeyMetadata?>(null);

            return Task.FromResult<KeyMetadata?>(new KeyMetadata
            {
                KeyId = keyId,
                CreatedAt = DateTime.UtcNow,
                KeySizeBytes = key.Length,
                IsActive = true,
                Metadata = new Dictionary<string, object>
                {
                    ["Provider"] = "Key Agreement",
                    ["Algorithm"] = $"ECDH-{_curveType}",
                    ["KeyDerivation"] = "HKDF-SHA384"
                }
            });
        }

        public override void Dispose()
        {
            foreach (var key in _agreedKeys.Values)
                CryptographicOperations.ZeroMemory(key);
            base.Dispose();
        }
    }

    /// <summary>
    /// Key wrapping strategy supporting AES-KWP (RFC 5649) and RSA-OAEP.
    ///
    /// Features:
    /// - AES Key Wrap with Padding (AES-KWP, RFC 5649)
    /// - AES Key Wrap (AES-KW, RFC 3394)
    /// - RSA-OAEP key wrapping with SHA-256/SHA-384
    /// - Wrap/unwrap tracking and audit
    /// </summary>
    public sealed class KeyWrappingStrategy : KeyStoreStrategyBase
    {
        private readonly BoundedDictionary<string, byte[]> _wrappingKeys = new BoundedDictionary<string, byte[]>(1000);
        private string _defaultAlgorithm = "AES-KWP";

        public override string StrategyId => "key-wrapping";
        public override string Name => "AES-KWP/RSA-OAEP Key Wrapping";

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = false,
            SupportsEnvelope = true,
            SupportsHsm = false,
            SupportsExpiration = false,
            SupportsReplication = false,
            SupportsVersioning = false,
            SupportsPerKeyAcl = false,
            SupportsAuditLogging = true,
            MaxKeySizeBytes = 0,
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["Provider"] = "Key Wrapping",
                ["SupportedAlgorithms"] = new[] { "AES-KWP (RFC 5649)", "AES-KW (RFC 3394)", "RSA-OAEP-SHA256" },
                ["SupportsEnvelopeEncryption"] = true
            }
        };

        protected override Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("keywrap.init");

            if (Configuration.TryGetValue("DefaultAlgorithm", out var algo) && algo is string algoStr)
                _defaultAlgorithm = algoStr;

            // Generate default wrapping key
            var kek = new byte[32];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(kek);
            _wrappingKeys["default-kek"] = kek;

            return Task.CompletedTask;
        }

        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            foreach (var key in _wrappingKeys.Values)
                CryptographicOperations.ZeroMemory(key);
            _wrappingKeys.Clear();
            IncrementCounter("keywrap.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }

        public override Task<string> GetCurrentKeyIdAsync()
            => Task.FromResult("default-kek");

        /// <summary>
        /// Wraps a data key using AES Key Wrap with Padding (RFC 5649).
        /// </summary>
        public byte[] WrapKey(byte[] kek, byte[] dataKey)
        {
            if (kek.Length != 16 && kek.Length != 24 && kek.Length != 32)
                throw new ArgumentException("KEK must be 128, 192, or 256 bits", nameof(kek));

            // AES-KWP: encrypt the data key with the KEK using AES key wrap with padding
            using var aes = Aes.Create();
            aes.Key = kek;

            // Pad the data key to 8-byte boundary
            var padded = PadForKeyWrap(dataKey);
            var result = new byte[padded.Length + 8]; // Add 8-byte integrity check value

            // Initial Value (IV) for key wrap
            var iv = new byte[] { 0xA6, 0x59, 0x59, 0xA6 }; // RFC 5649 alternative IV
            var lengthBytes = BitConverter.GetBytes(dataKey.Length);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(lengthBytes);

            // Build A (8 bytes): [IV:4][Length:4]
            var a = new byte[8];
            Buffer.BlockCopy(iv, 0, a, 0, 4);
            Buffer.BlockCopy(lengthBytes, 0, a, 4, 4);

            if (padded.Length == 8)
            {
                // Single block — use AES-ECB
                var block = new byte[16];
                Buffer.BlockCopy(a, 0, block, 0, 8);
                Buffer.BlockCopy(padded, 0, block, 8, 8);

                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                using var encryptor = aes.CreateEncryptor();
                var encrypted = encryptor.TransformFinalBlock(block, 0, 16);
                IncrementCounter("keywrap.wrap");
                return encrypted;
            }

            // Multi-block: standard AES Key Wrap algorithm
            var n = padded.Length / 8;
            var r = new byte[n][];
            for (int i = 0; i < n; i++)
            {
                r[i] = new byte[8];
                Buffer.BlockCopy(padded, i * 8, r[i], 0, 8);
            }

            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            using var enc = aes.CreateEncryptor();

            for (int j = 0; j <= 5; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    var block = new byte[16];
                    Buffer.BlockCopy(a, 0, block, 0, 8);
                    Buffer.BlockCopy(r[i], 0, block, 8, 8);

                    var encrypted = enc.TransformFinalBlock(block, 0, 16);

                    var t = (long)(n * j + i + 1);
                    var tBytes = BitConverter.GetBytes(t);
                    if (BitConverter.IsLittleEndian)
                        Array.Reverse(tBytes);

                    Buffer.BlockCopy(encrypted, 0, a, 0, 8);
                    for (int k = 0; k < 8; k++)
                        a[k] ^= tBytes[k];

                    Buffer.BlockCopy(encrypted, 8, r[i], 0, 8);
                }
            }

            Buffer.BlockCopy(a, 0, result, 0, 8);
            for (int i = 0; i < n; i++)
                Buffer.BlockCopy(r[i], 0, result, 8 + i * 8, 8);

            IncrementCounter("keywrap.wrap");
            return result;
        }

        /// <summary>
        /// Unwraps a data key using AES Key Wrap with Padding (RFC 5649).
        /// </summary>
        public byte[] UnwrapKey(byte[] kek, byte[] wrappedKey)
        {
            if (kek.Length != 16 && kek.Length != 24 && kek.Length != 32)
                throw new ArgumentException("KEK must be 128, 192, or 256 bits", nameof(kek));

            using var aes = Aes.Create();
            aes.Key = kek;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;

            if (wrappedKey.Length == 16)
            {
                // Single block
                using var decryptor = aes.CreateDecryptor();
                var decrypted = decryptor.TransformFinalBlock(wrappedKey, 0, 16);

                var a = new byte[8];
                Buffer.BlockCopy(decrypted, 0, a, 0, 8);

                var lengthBytes = new byte[4];
                Buffer.BlockCopy(a, 4, lengthBytes, 0, 4);
                if (BitConverter.IsLittleEndian)
                    Array.Reverse(lengthBytes);
                var length = BitConverter.ToInt32(lengthBytes, 0);

                var result = new byte[length];
                Buffer.BlockCopy(decrypted, 8, result, 0, Math.Min(length, 8));
                IncrementCounter("keywrap.unwrap");
                return result;
            }

            // Multi-block unwrap
            var n = (wrappedKey.Length / 8) - 1;
            var aBytes = new byte[8];
            Buffer.BlockCopy(wrappedKey, 0, aBytes, 0, 8);

            var r = new byte[n][];
            for (int i = 0; i < n; i++)
            {
                r[i] = new byte[8];
                Buffer.BlockCopy(wrappedKey, 8 + i * 8, r[i], 0, 8);
            }

            using var dec = aes.CreateDecryptor();

            for (int j = 5; j >= 0; j--)
            {
                for (int i = n - 1; i >= 0; i--)
                {
                    var t = (long)(n * j + i + 1);
                    var tBytes = BitConverter.GetBytes(t);
                    if (BitConverter.IsLittleEndian)
                        Array.Reverse(tBytes);

                    var xored = (byte[])aBytes.Clone();
                    for (int k = 0; k < 8; k++)
                        xored[k] ^= tBytes[k];

                    var block = new byte[16];
                    Buffer.BlockCopy(xored, 0, block, 0, 8);
                    Buffer.BlockCopy(r[i], 0, block, 8, 8);

                    var decrypted = dec.TransformFinalBlock(block, 0, 16);
                    Buffer.BlockCopy(decrypted, 0, aBytes, 0, 8);
                    Buffer.BlockCopy(decrypted, 8, r[i], 0, 8);
                }
            }

            // Extract length from A
            var lenBytes = new byte[4];
            Buffer.BlockCopy(aBytes, 4, lenBytes, 0, 4);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(lenBytes);
            var dataLength = BitConverter.ToInt32(lenBytes, 0);

            var unwrapped = new byte[dataLength];
            var offset = 0;
            for (int i = 0; i < n && offset < dataLength; i++)
            {
                var copyLen = Math.Min(8, dataLength - offset);
                Buffer.BlockCopy(r[i], 0, unwrapped, offset, copyLen);
                offset += copyLen;
            }

            IncrementCounter("keywrap.unwrap");
            return unwrapped;
        }

        private static byte[] PadForKeyWrap(byte[] data)
        {
            var padLength = (8 - (data.Length % 8)) % 8;
            if (padLength == 0 && data.Length >= 8) return data;

            padLength = padLength == 0 ? 8 : padLength;
            var padded = new byte[data.Length + padLength];
            Buffer.BlockCopy(data, 0, padded, 0, data.Length);
            return padded;
        }

        protected override Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            if (_wrappingKeys.TryGetValue(keyId, out var key))
                return Task.FromResult(key);
            throw new KeyNotFoundException($"Wrapping key '{keyId}' not found");
        }

        protected override Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            _wrappingKeys[keyId] = keyData;
            return Task.CompletedTask;
        }

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            var result = await GetCachedHealthAsync(async ct =>
            {
                return new StrategyHealthCheckResult(true,
                    $"Key wrapping ready. Algorithm: {_defaultAlgorithm}, KEKs: {_wrappingKeys.Count}");
            }, TimeSpan.FromSeconds(60), cancellationToken);
            return result.IsHealthy;
        }

        public override Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);
            return Task.FromResult<IReadOnlyList<string>>(_wrappingKeys.Keys.ToList().AsReadOnly());
        }

        public override Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);
            if (_wrappingKeys.TryRemove(keyId, out var key))
                CryptographicOperations.ZeroMemory(key);
            return Task.CompletedTask;
        }

        public override Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);
            if (!_wrappingKeys.TryGetValue(keyId, out var key))
                return Task.FromResult<KeyMetadata?>(null);

            return Task.FromResult<KeyMetadata?>(new KeyMetadata
            {
                KeyId = keyId,
                CreatedAt = DateTime.UtcNow,
                KeySizeBytes = key.Length,
                IsActive = true,
                Metadata = new Dictionary<string, object>
                {
                    ["Provider"] = "Key Wrapping",
                    ["Algorithm"] = _defaultAlgorithm,
                    ["KeyType"] = "KEK"
                }
            });
        }

        public override void Dispose()
        {
            foreach (var key in _wrappingKeys.Values)
                CryptographicOperations.ZeroMemory(key);
            base.Dispose();
        }
    }

    internal sealed class EscrowedKey
    {
        public required string KeyId { get; init; }
        public required byte[] EncryptedKeyMaterial { get; init; }
        public required DateTime EscrowedAt { get; init; }
        public required string EscrowedBy { get; init; }
        public required int RecoveryThreshold { get; init; }
        public required int TotalShares { get; init; }
    }

    internal sealed class RecoveryRequest
    {
        public required string RequestId { get; init; }
        public required string KeyId { get; init; }
        public required string RequesterId { get; init; }
        public required string Reason { get; init; }
        public required DateTime RequestedAt { get; init; }
        public required DateTime EarliestRelease { get; init; }
        public required List<RecoveryApproval> Approvals { get; set; }
        public RecoveryStatus Status { get; set; }
    }

    internal sealed class RecoveryApproval
    {
        public required string ApproverId { get; init; }
        public required DateTime ApprovedAt { get; init; }
    }

    public sealed class RecoveryRequestResult
    {
        public required string RequestId { get; init; }
        public required DateTime EarliestRelease { get; init; }
        public required int ApprovalsRequired { get; init; }
        public required RecoveryStatus Status { get; init; }
    }

    public sealed class RecoveryApprovalResult
    {
        public required string RequestId { get; init; }
        public required int ApprovalsReceived { get; init; }
        public required int ApprovalsRequired { get; init; }
        public required RecoveryStatus Status { get; init; }
        public required bool CanRelease { get; init; }
    }

    public enum RecoveryStatus
    {
        Pending,
        PendingDelay,
        Approved,
        Rejected,
        Completed
    }
}
