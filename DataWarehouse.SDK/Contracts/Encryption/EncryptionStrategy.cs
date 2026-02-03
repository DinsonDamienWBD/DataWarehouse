using DataWarehouse.SDK.Security;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Encryption
{
    /// <summary>
    /// Security level classification for encryption strategies.
    /// Determines the strength and compliance requirements for encryption operations.
    /// </summary>
    public enum SecurityLevel
    {
        /// <summary>
        /// Standard commercial-grade encryption (AES-128, RSA-2048).
        /// Suitable for general business data.
        /// </summary>
        Standard,

        /// <summary>
        /// High-security encryption (AES-256, RSA-4096, ChaCha20-Poly1305).
        /// Suitable for sensitive business data, financial records.
        /// </summary>
        High,

        /// <summary>
        /// Military-grade encryption (AES-256-GCM with HSM, Twofish-256).
        /// Suitable for classified data, government use, critical infrastructure.
        /// </summary>
        Military,

        /// <summary>
        /// Post-quantum cryptography algorithms (CRYSTALS-Kyber, CRYSTALS-Dilithium).
        /// Future-proof against quantum computing attacks.
        /// </summary>
        QuantumSafe,

        /// <summary>
        /// Experimental algorithms under evaluation (not for production use).
        /// Research and development purposes only.
        /// </summary>
        Experimental
    }

    /// <summary>
    /// Describes the capabilities of a cipher algorithm.
    /// Used for algorithm selection and configuration validation.
    /// </summary>
    public record CipherCapabilities
    {
        /// <summary>
        /// Indicates whether the cipher provides authenticated encryption.
        /// Authenticated ciphers (AEAD) verify both confidentiality and integrity.
        /// </summary>
        public bool IsAuthenticated { get; init; }

        /// <summary>
        /// Indicates whether the cipher supports streaming mode for large data.
        /// Non-streaming ciphers require buffering entire payload in memory.
        /// </summary>
        public bool IsStreamable { get; init; }

        /// <summary>
        /// Indicates whether hardware acceleration is available (AES-NI, AVX2, etc.).
        /// Hardware-accelerated ciphers provide significantly better performance.
        /// </summary>
        public bool IsHardwareAcceleratable { get; init; }

        /// <summary>
        /// Indicates whether this is an AEAD (Authenticated Encryption with Associated Data) cipher.
        /// AEAD ciphers can authenticate additional data without encrypting it.
        /// </summary>
        public bool SupportsAead { get; init; }

        /// <summary>
        /// Indicates whether parallel encryption/decryption is supported.
        /// Parallel operations can significantly improve throughput for large datasets.
        /// </summary>
        public bool SupportsParallelism { get; init; }

        /// <summary>
        /// Recommended minimum security level for this cipher.
        /// Deployment at lower security levels may violate compliance requirements.
        /// </summary>
        public SecurityLevel MinimumSecurityLevel { get; init; }

        /// <summary>
        /// Creates default capabilities for a basic block cipher.
        /// </summary>
        public static CipherCapabilities BasicBlockCipher => new()
        {
            IsAuthenticated = false,
            IsStreamable = false,
            IsHardwareAcceleratable = false,
            SupportsAead = false,
            SupportsParallelism = false,
            MinimumSecurityLevel = SecurityLevel.Standard
        };

        /// <summary>
        /// Creates default capabilities for an AEAD cipher like AES-GCM.
        /// </summary>
        public static CipherCapabilities AeadCipher => new()
        {
            IsAuthenticated = true,
            IsStreamable = true,
            IsHardwareAcceleratable = true,
            SupportsAead = true,
            SupportsParallelism = true,
            MinimumSecurityLevel = SecurityLevel.High
        };
    }

    /// <summary>
    /// Contains detailed information about a cipher algorithm.
    /// Immutable record for thread-safe sharing across components.
    /// </summary>
    public record CipherInfo
    {
        /// <summary>
        /// The standard algorithm name (e.g., "AES-256-GCM", "ChaCha20-Poly1305", "Twofish-256-CBC").
        /// </summary>
        public string AlgorithmName { get; init; } = "";

        /// <summary>
        /// Key size in bits (128, 192, 256, 512, etc.).
        /// </summary>
        public int KeySizeBits { get; init; }

        /// <summary>
        /// Block size in bytes (16 for AES, 64 for ChaCha20).
        /// Zero for stream ciphers without fixed block size.
        /// </summary>
        public int BlockSizeBytes { get; init; }

        /// <summary>
        /// Initialization Vector (IV) or nonce size in bytes.
        /// GCM typically uses 12 bytes, CBC uses block size.
        /// </summary>
        public int IvSizeBytes { get; init; }

        /// <summary>
        /// Authentication tag size in bytes (for AEAD ciphers).
        /// Typically 16 bytes for GCM/Poly1305. Zero for non-authenticated ciphers.
        /// </summary>
        public int TagSizeBytes { get; init; }

        /// <summary>
        /// Cipher capabilities and features.
        /// </summary>
        public CipherCapabilities Capabilities { get; init; } = CipherCapabilities.BasicBlockCipher;

        /// <summary>
        /// Security level of this cipher configuration.
        /// </summary>
        public SecurityLevel SecurityLevel { get; init; }

        /// <summary>
        /// Additional algorithm-specific parameters (salt size, rounds, etc.).
        /// </summary>
        public IReadOnlyDictionary<string, object> Parameters { get; init; } =
            new Dictionary<string, object>();

        /// <summary>
        /// Validates that the cipher configuration is consistent.
        /// </summary>
        /// <returns>True if valid, false otherwise.</returns>
        public bool IsValid()
        {
            if (string.IsNullOrWhiteSpace(AlgorithmName))
                return false;

            if (KeySizeBits <= 0 || KeySizeBits % 8 != 0)
                return false;

            if (IvSizeBytes < 0)
                return false;

            if (Capabilities.IsAuthenticated && TagSizeBytes <= 0)
                return false;

            if (!Capabilities.IsAuthenticated && TagSizeBytes > 0)
                return false;

            return true;
        }

        /// <summary>
        /// Calculates the total header size needed for ciphertext storage.
        /// Includes IV + tag + any additional metadata.
        /// </summary>
        public int GetHeaderSize() => IvSizeBytes + TagSizeBytes;
    }

    /// <summary>
    /// Core interface for encryption strategy implementations.
    /// All encryption algorithms must implement this interface.
    /// </summary>
    public interface IEncryptionStrategy
    {
        /// <summary>
        /// Gets information about the cipher algorithm and configuration.
        /// </summary>
        CipherInfo CipherInfo { get; }

        /// <summary>
        /// Gets the unique identifier for this encryption strategy.
        /// Should match the plugin ID for consistency (e.g., "aes256gcm", "chacha20poly1305").
        /// </summary>
        string StrategyId { get; }

        /// <summary>
        /// Gets the human-readable name of this encryption strategy.
        /// </summary>
        string StrategyName { get; }

        /// <summary>
        /// Encrypts plaintext data using the provided key and optional additional authenticated data.
        /// </summary>
        /// <param name="plaintext">The data to encrypt.</param>
        /// <param name="key">The encryption key (must match CipherInfo.KeySizeBits).</param>
        /// <param name="associatedData">Optional associated data for AEAD ciphers (not encrypted but authenticated).</param>
        /// <param name="cancellationToken">Cancellation token for long-running operations.</param>
        /// <returns>Encrypted ciphertext including IV and authentication tag.</returns>
        /// <exception cref="ArgumentNullException">If plaintext or key is null.</exception>
        /// <exception cref="ArgumentException">If key size is invalid.</exception>
        /// <exception cref="CryptographicException">If encryption fails.</exception>
        Task<byte[]> EncryptAsync(
            byte[] plaintext,
            byte[] key,
            byte[]? associatedData = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Decrypts ciphertext data using the provided key and optional additional authenticated data.
        /// </summary>
        /// <param name="ciphertext">The encrypted data (including IV and tag).</param>
        /// <param name="key">The decryption key (must match CipherInfo.KeySizeBits).</param>
        /// <param name="associatedData">Optional associated data for AEAD verification.</param>
        /// <param name="cancellationToken">Cancellation token for long-running operations.</param>
        /// <returns>Decrypted plaintext.</returns>
        /// <exception cref="ArgumentNullException">If ciphertext or key is null.</exception>
        /// <exception cref="ArgumentException">If key size or ciphertext format is invalid.</exception>
        /// <exception cref="CryptographicException">If decryption or authentication fails.</exception>
        Task<byte[]> DecryptAsync(
            byte[] ciphertext,
            byte[] key,
            byte[]? associatedData = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Generates a cryptographically secure random key suitable for this cipher.
        /// </summary>
        /// <returns>A new random key of the correct size.</returns>
        byte[] GenerateKey();

        /// <summary>
        /// Generates a cryptographically secure random IV/nonce.
        /// </summary>
        /// <returns>A new random IV of the correct size.</returns>
        byte[] GenerateIv();

        /// <summary>
        /// Validates that a key is suitable for this encryption strategy.
        /// </summary>
        /// <param name="key">The key to validate.</param>
        /// <returns>True if valid, false otherwise.</returns>
        bool ValidateKey(byte[] key);

        /// <summary>
        /// Gets encryption/decryption statistics for monitoring and auditing.
        /// </summary>
        EncryptionStatistics GetStatistics();

        /// <summary>
        /// Resets statistics counters (useful for testing or after reporting).
        /// </summary>
        void ResetStatistics();
    }

    /// <summary>
    /// Statistics tracking for encryption operations.
    /// Used for monitoring, auditing, and performance analysis.
    /// </summary>
    public sealed class EncryptionStatistics
    {
        /// <summary>
        /// Total number of encryption operations performed.
        /// </summary>
        public long EncryptionCount { get; init; }

        /// <summary>
        /// Total number of decryption operations performed.
        /// </summary>
        public long DecryptionCount { get; init; }

        /// <summary>
        /// Total bytes encrypted across all operations.
        /// </summary>
        public long TotalBytesEncrypted { get; init; }

        /// <summary>
        /// Total bytes decrypted across all operations.
        /// </summary>
        public long TotalBytesDecrypted { get; init; }

        /// <summary>
        /// Number of encryption failures.
        /// </summary>
        public long EncryptionFailures { get; init; }

        /// <summary>
        /// Number of decryption failures.
        /// </summary>
        public long DecryptionFailures { get; init; }

        /// <summary>
        /// Number of authentication tag verification failures (AEAD ciphers).
        /// High values may indicate tampering attempts.
        /// </summary>
        public long AuthenticationFailures { get; init; }

        /// <summary>
        /// Timestamp when statistics tracking started.
        /// </summary>
        public DateTime StartTime { get; init; }

        /// <summary>
        /// Timestamp of last statistic update.
        /// </summary>
        public DateTime LastUpdateTime { get; init; }

        /// <summary>
        /// Creates an empty statistics object.
        /// </summary>
        public static EncryptionStatistics Empty => new()
        {
            StartTime = DateTime.UtcNow,
            LastUpdateTime = DateTime.UtcNow
        };

        /// <summary>
        /// Calculates average encryption throughput in bytes per second.
        /// </summary>
        public double GetEncryptionThroughput()
        {
            var duration = (LastUpdateTime - StartTime).TotalSeconds;
            return duration > 0 ? TotalBytesEncrypted / duration : 0;
        }

        /// <summary>
        /// Calculates average decryption throughput in bytes per second.
        /// </summary>
        public double GetDecryptionThroughput()
        {
            var duration = (LastUpdateTime - StartTime).TotalSeconds;
            return duration > 0 ? TotalBytesDecrypted / duration : 0;
        }
    }

    /// <summary>
    /// Abstract base class for encryption strategies.
    /// Provides common functionality for key validation, IV generation, statistics tracking,
    /// key store integration, and audit logging.
    /// </summary>
    public abstract class EncryptionStrategyBase : IEncryptionStrategy
    {
        private long _encryptionCount;
        private long _decryptionCount;
        private long _totalBytesEncrypted;
        private long _totalBytesDecrypted;
        private long _encryptionFailures;
        private long _decryptionFailures;
        private long _authenticationFailures;
        private readonly DateTime _startTime;
        private DateTime _lastUpdateTime;

        /// <summary>
        /// Thread-safe dictionary for tracking key access patterns (keyId -> access count).
        /// Used for audit trails and key rotation analysis.
        /// </summary>
        protected readonly ConcurrentDictionary<string, long> KeyAccessLog = new();

        /// <summary>
        /// Optional key store registry for resolving key store instances.
        /// Set this to enable integration with IKeyStore infrastructure.
        /// </summary>
        protected IKeyStoreRegistry? KeyStoreRegistry { get; set; }

        /// <summary>
        /// Optional key management config provider for user/tenant-specific settings.
        /// </summary>
        protected IKeyManagementConfigProvider? KeyManagementConfigProvider { get; set; }

        /// <inheritdoc/>
        public abstract CipherInfo CipherInfo { get; }

        /// <inheritdoc/>
        public abstract string StrategyId { get; }

        /// <inheritdoc/>
        public abstract string StrategyName { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="EncryptionStrategyBase"/> class.
        /// </summary>
        protected EncryptionStrategyBase()
        {
            _startTime = DateTime.UtcNow;
            _lastUpdateTime = DateTime.UtcNow;
        }

        /// <inheritdoc/>
        public virtual byte[] GenerateKey()
        {
            ValidateCipherInfo();
            var keySizeBytes = CipherInfo.KeySizeBits / 8;
            return RandomNumberGenerator.GetBytes(keySizeBytes);
        }

        /// <inheritdoc/>
        public virtual byte[] GenerateIv()
        {
            ValidateCipherInfo();
            if (CipherInfo.IvSizeBytes <= 0)
            {
                throw new InvalidOperationException(
                    $"Cipher {CipherInfo.AlgorithmName} does not use an IV/nonce");
            }
            return RandomNumberGenerator.GetBytes(CipherInfo.IvSizeBytes);
        }

        /// <inheritdoc/>
        public virtual bool ValidateKey(byte[] key)
        {
            if (key == null)
                return false;

            var expectedBytes = CipherInfo.KeySizeBits / 8;
            if (key.Length != expectedBytes)
                return false;

            // Check for all-zero key (weak key)
            bool allZero = true;
            foreach (var b in key)
            {
                if (b != 0)
                {
                    allZero = false;
                    break;
                }
            }

            return !allZero;
        }

        /// <inheritdoc/>
        public virtual EncryptionStatistics GetStatistics()
        {
            return new EncryptionStatistics
            {
                EncryptionCount = Interlocked.Read(ref _encryptionCount),
                DecryptionCount = Interlocked.Read(ref _decryptionCount),
                TotalBytesEncrypted = Interlocked.Read(ref _totalBytesEncrypted),
                TotalBytesDecrypted = Interlocked.Read(ref _totalBytesDecrypted),
                EncryptionFailures = Interlocked.Read(ref _encryptionFailures),
                DecryptionFailures = Interlocked.Read(ref _decryptionFailures),
                AuthenticationFailures = Interlocked.Read(ref _authenticationFailures),
                StartTime = _startTime,
                LastUpdateTime = _lastUpdateTime
            };
        }

        /// <inheritdoc/>
        public virtual void ResetStatistics()
        {
            Interlocked.Exchange(ref _encryptionCount, 0);
            Interlocked.Exchange(ref _decryptionCount, 0);
            Interlocked.Exchange(ref _totalBytesEncrypted, 0);
            Interlocked.Exchange(ref _totalBytesDecrypted, 0);
            Interlocked.Exchange(ref _encryptionFailures, 0);
            Interlocked.Exchange(ref _decryptionFailures, 0);
            Interlocked.Exchange(ref _authenticationFailures, 0);
            _lastUpdateTime = DateTime.UtcNow;
            KeyAccessLog.Clear();
        }

        /// <inheritdoc/>
        public async Task<byte[]> EncryptAsync(
            byte[] plaintext,
            byte[] key,
            byte[]? associatedData = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(plaintext);
            ArgumentNullException.ThrowIfNull(key);

            if (!ValidateKey(key))
            {
                Interlocked.Increment(ref _encryptionFailures);
                throw new ArgumentException(
                    $"Invalid key size. Expected {CipherInfo.KeySizeBits / 8} bytes, got {key.Length} bytes",
                    nameof(key));
            }

            try
            {
                var result = await EncryptCoreAsync(plaintext, key, associatedData, cancellationToken);

                // Update statistics
                Interlocked.Increment(ref _encryptionCount);
                Interlocked.Add(ref _totalBytesEncrypted, plaintext.Length);
                _lastUpdateTime = DateTime.UtcNow;

                return result;
            }
            catch
            {
                Interlocked.Increment(ref _encryptionFailures);
                throw;
            }
        }

        /// <inheritdoc/>
        public async Task<byte[]> DecryptAsync(
            byte[] ciphertext,
            byte[] key,
            byte[]? associatedData = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(ciphertext);
            ArgumentNullException.ThrowIfNull(key);

            if (!ValidateKey(key))
            {
                Interlocked.Increment(ref _decryptionFailures);
                throw new ArgumentException(
                    $"Invalid key size. Expected {CipherInfo.KeySizeBits / 8} bytes, got {key.Length} bytes",
                    nameof(key));
            }

            var minSize = CipherInfo.GetHeaderSize();
            if (ciphertext.Length < minSize)
            {
                Interlocked.Increment(ref _decryptionFailures);
                throw new ArgumentException(
                    $"Ciphertext too short. Minimum size: {minSize} bytes, got: {ciphertext.Length} bytes",
                    nameof(ciphertext));
            }

            try
            {
                var result = await DecryptCoreAsync(ciphertext, key, associatedData, cancellationToken);

                // Update statistics
                Interlocked.Increment(ref _decryptionCount);
                Interlocked.Add(ref _totalBytesDecrypted, result.Length);
                _lastUpdateTime = DateTime.UtcNow;

                return result;
            }
            catch (CryptographicException ex) when (ex.Message.Contains("authentication") || ex.Message.Contains("tag"))
            {
                Interlocked.Increment(ref _authenticationFailures);
                Interlocked.Increment(ref _decryptionFailures);
                throw;
            }
            catch
            {
                Interlocked.Increment(ref _decryptionFailures);
                throw;
            }
        }

        /// <summary>
        /// Core encryption implementation. Must be implemented by derived classes.
        /// </summary>
        /// <param name="plaintext">The data to encrypt.</param>
        /// <param name="key">The encryption key (already validated).</param>
        /// <param name="associatedData">Optional AEAD associated data.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Encrypted ciphertext (including IV and tag).</returns>
        protected abstract Task<byte[]> EncryptCoreAsync(
            byte[] plaintext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken);

        /// <summary>
        /// Core decryption implementation. Must be implemented by derived classes.
        /// </summary>
        /// <param name="ciphertext">The encrypted data (already validated).</param>
        /// <param name="key">The decryption key (already validated).</param>
        /// <param name="associatedData">Optional AEAD associated data.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Decrypted plaintext.</returns>
        protected abstract Task<byte[]> DecryptCoreAsync(
            byte[] ciphertext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken);

        /// <summary>
        /// Retrieves a key from the key store in Direct mode.
        /// Logs access for audit trails.
        /// </summary>
        /// <param name="keyId">The key identifier.</param>
        /// <param name="securityContext">Security context for ACL validation.</param>
        /// <returns>The encryption key.</returns>
        /// <exception cref="InvalidOperationException">If key store registry is not configured.</exception>
        protected async Task<byte[]> GetKeyFromStoreAsync(string keyId, ISecurityContext securityContext)
        {
            if (KeyStoreRegistry == null)
            {
                throw new InvalidOperationException(
                    "KeyStoreRegistry must be configured to use key store integration");
            }

            // Get user configuration
            var config = await GetResolvedKeyManagementConfigAsync(securityContext);

            if (config.Mode == KeyManagementMode.Direct)
            {
                if (config.KeyStore == null)
                {
                    throw new InvalidOperationException(
                        "Key store is not configured for Direct mode");
                }

                // Log access
                LogKeyAccess(keyId, securityContext.UserId);

                return await config.KeyStore.GetKeyAsync(keyId, securityContext);
            }
            else if (config.Mode == KeyManagementMode.Envelope)
            {
                throw new InvalidOperationException(
                    "Use GetKeyFromEnvelopeStoreAsync for Envelope mode");
            }

            throw new InvalidOperationException($"Unsupported key management mode: {config.Mode}");
        }

        /// <summary>
        /// Retrieves and unwraps a DEK from the envelope key store.
        /// Logs access for audit trails.
        /// </summary>
        /// <param name="wrappedDek">The wrapped Data Encryption Key.</param>
        /// <param name="kekId">The Key Encryption Key identifier.</param>
        /// <param name="securityContext">Security context for ACL validation.</param>
        /// <returns>The unwrapped DEK.</returns>
        protected async Task<byte[]> GetKeyFromEnvelopeStoreAsync(
            byte[] wrappedDek,
            string kekId,
            ISecurityContext securityContext)
        {
            var config = await GetResolvedKeyManagementConfigAsync(securityContext);

            if (config.Mode != KeyManagementMode.Envelope)
            {
                throw new InvalidOperationException(
                    "Envelope key store requires KeyManagementMode.Envelope");
            }

            if (config.EnvelopeKeyStore == null)
            {
                throw new InvalidOperationException(
                    "Envelope key store is not configured");
            }

            // Log access
            LogKeyAccess(kekId, securityContext.UserId);

            return await config.EnvelopeKeyStore.UnwrapKeyAsync(kekId, wrappedDek, securityContext);
        }

        /// <summary>
        /// Wraps a DEK using the envelope key store.
        /// </summary>
        /// <param name="dek">The Data Encryption Key to wrap.</param>
        /// <param name="kekId">The Key Encryption Key identifier.</param>
        /// <param name="securityContext">Security context for ACL validation.</param>
        /// <returns>The wrapped DEK.</returns>
        protected async Task<byte[]> WrapKeyAsync(
            byte[] dek,
            string kekId,
            ISecurityContext securityContext)
        {
            var config = await GetResolvedKeyManagementConfigAsync(securityContext);

            if (config.Mode != KeyManagementMode.Envelope)
            {
                throw new InvalidOperationException(
                    "Key wrapping requires KeyManagementMode.Envelope");
            }

            if (config.EnvelopeKeyStore == null)
            {
                throw new InvalidOperationException(
                    "Envelope key store is not configured");
            }

            return await config.EnvelopeKeyStore.WrapKeyAsync(kekId, dek, securityContext);
        }

        /// <summary>
        /// Gets the resolved key management configuration for a security context.
        /// </summary>
        private async Task<ResolvedKeyManagementConfig> GetResolvedKeyManagementConfigAsync(
            ISecurityContext securityContext)
        {
            if (KeyManagementConfigProvider == null)
            {
                throw new InvalidOperationException(
                    "KeyManagementConfigProvider must be configured");
            }

            var config = await KeyManagementConfigProvider.GetConfigAsync(securityContext);
            if (config == null)
            {
                throw new InvalidOperationException(
                    $"No key management configuration found for user {securityContext.UserId}");
            }

            // Resolve to actual instances
            return new ResolvedKeyManagementConfig
            {
                Mode = config.Mode,
                KeyStore = config.KeyStore ?? KeyStoreRegistry?.GetKeyStore(config.KeyStorePluginId),
                KeyId = config.KeyId,
                EnvelopeKeyStore = config.EnvelopeKeyStore ?? KeyStoreRegistry?.GetEnvelopeKeyStore(config.EnvelopeKeyStorePluginId),
                KekKeyId = config.KekKeyId,
                KeyStorePluginId = config.KeyStorePluginId,
                EnvelopeKeyStorePluginId = config.EnvelopeKeyStorePluginId
            };
        }

        /// <summary>
        /// Logs a key access event for audit trails.
        /// Thread-safe implementation using ConcurrentDictionary.
        /// </summary>
        /// <param name="keyId">The key identifier accessed.</param>
        /// <param name="userId">The user who accessed the key.</param>
        protected void LogKeyAccess(string keyId, string userId)
        {
            var logKey = $"{keyId}:{userId}";
            KeyAccessLog.AddOrUpdate(logKey, 1, (_, count) => count + 1);
        }

        /// <summary>
        /// Gets the key access log for a specific key.
        /// </summary>
        /// <param name="keyId">The key identifier.</param>
        /// <returns>Dictionary of userId -> access count.</returns>
        public IReadOnlyDictionary<string, long> GetKeyAccessLog(string keyId)
        {
            var result = new Dictionary<string, long>();
            foreach (var kvp in KeyAccessLog)
            {
                if (kvp.Key.StartsWith($"{keyId}:"))
                {
                    var userId = kvp.Key.Substring(keyId.Length + 1);
                    result[userId] = kvp.Value;
                }
            }
            return result;
        }

        /// <summary>
        /// Validates that CipherInfo is properly configured.
        /// </summary>
        protected void ValidateCipherInfo()
        {
            if (!CipherInfo.IsValid())
            {
                throw new InvalidOperationException(
                    $"Invalid CipherInfo configuration for {StrategyId}");
            }
        }

        /// <summary>
        /// Helper method to combine IV and ciphertext into a single byte array.
        /// Format: [IV][Ciphertext][Tag] for AEAD ciphers.
        /// </summary>
        protected static byte[] CombineIvAndCiphertext(byte[] iv, byte[] ciphertext, byte[]? tag = null)
        {
            var totalLength = iv.Length + ciphertext.Length + (tag?.Length ?? 0);
            var result = new byte[totalLength];

            Buffer.BlockCopy(iv, 0, result, 0, iv.Length);
            Buffer.BlockCopy(ciphertext, 0, result, iv.Length, ciphertext.Length);

            if (tag != null && tag.Length > 0)
            {
                Buffer.BlockCopy(tag, 0, result, iv.Length + ciphertext.Length, tag.Length);
            }

            return result;
        }

        /// <summary>
        /// Helper method to extract IV, ciphertext, and tag from combined byte array.
        /// </summary>
        protected (byte[] iv, byte[] ciphertext, byte[]? tag) SplitCiphertext(byte[] combined)
        {
            var ivSize = CipherInfo.IvSizeBytes;
            var tagSize = CipherInfo.TagSizeBytes;
            var ciphertextSize = combined.Length - ivSize - tagSize;

            if (ciphertextSize < 0)
            {
                throw new ArgumentException(
                    $"Invalid ciphertext length. Expected at least {ivSize + tagSize} bytes",
                    nameof(combined));
            }

            var iv = new byte[ivSize];
            var ciphertext = new byte[ciphertextSize];
            byte[]? tag = tagSize > 0 ? new byte[tagSize] : null;

            Buffer.BlockCopy(combined, 0, iv, 0, ivSize);
            Buffer.BlockCopy(combined, ivSize, ciphertext, 0, ciphertextSize);

            if (tag != null)
            {
                Buffer.BlockCopy(combined, ivSize + ciphertextSize, tag, 0, tagSize);
            }

            return (iv, ciphertext, tag);
        }
    }
}
