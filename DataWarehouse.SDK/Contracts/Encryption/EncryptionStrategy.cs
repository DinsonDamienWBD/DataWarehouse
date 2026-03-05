using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Security;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

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
        /// Number of unique encryption keys used across all operations.
        /// </summary>
        public int UniqueKeysUsed { get; init; }

        /// <summary>
        /// Timestamp of the most recent key access.
        /// </summary>
        public DateTime LastKeyAccess { get; init; }

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
    public abstract class EncryptionStrategyBase : StrategyBase, IEncryptionStrategy
    {
        private long _encryptionCount;
        private long _decryptionCount;
        private long _totalBytesEncrypted;
        private long _totalBytesDecrypted;
        private long _encryptionFailures;
        private long _decryptionFailures;
        private long _authenticationFailures;
        private readonly DateTime _startTime;
        private long _lastUpdateTimeTicks; // DateTime.UtcNow.Ticks stored via Interlocked for thread safety

        /// <summary>
        /// Thread-safe dictionary for tracking key access patterns (keyId -> access count).
        /// Used for audit trails and key rotation analysis.
        /// </summary>
        protected readonly BoundedDictionary<string, long> KeyAccessLog = new BoundedDictionary<string, long>(1000);

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
        public override abstract string StrategyId { get; }

        /// <inheritdoc/>
        public abstract string StrategyName { get; }

        /// <summary>
        /// Gets the human-readable name. Delegates to StrategyName for backward compatibility.
        /// </summary>
        public override string Name => StrategyName;

        /// <summary>
        /// Initializes a new instance of the <see cref="EncryptionStrategyBase"/> class.
        /// </summary>
        protected EncryptionStrategyBase()
        {
            _startTime = DateTime.UtcNow;
            Interlocked.Exchange(ref _lastUpdateTimeTicks, DateTime.UtcNow.Ticks);
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
                LastUpdateTime = new DateTime(Interlocked.Read(ref _lastUpdateTimeTicks), DateTimeKind.Utc)
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
            Interlocked.Exchange(ref _lastUpdateTimeTicks, DateTime.UtcNow.Ticks);
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
                Interlocked.Exchange(ref _lastUpdateTimeTicks, DateTime.UtcNow.Ticks);

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
                Interlocked.Exchange(ref _lastUpdateTimeTicks, DateTime.UtcNow.Ticks);

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

    /// <summary>
    /// Universal encrypted payload envelope for all encryption strategies.
    /// Contains all metadata needed for decryption and verification.
    /// </summary>
    public sealed record EncryptedPayload
    {
        /// <summary>Algorithm identifier (e.g., "aes-256-gcm", "chacha20-poly1305").</summary>
        public required string AlgorithmId { get; init; }

        /// <summary>Strategy version for format evolution.</summary>
        public int Version { get; init; } = 1;

        /// <summary>Initialization vector/nonce.</summary>
        public required byte[] Nonce { get; init; }

        /// <summary>Encrypted ciphertext.</summary>
        public required byte[] Ciphertext { get; init; }

        /// <summary>Authentication tag (for AEAD ciphers).</summary>
        public byte[] Tag { get; init; } = Array.Empty<byte>();

        /// <summary>Key identifier for key retrieval.</summary>
        public string? KeyId { get; init; }

        /// <summary>Wrapped DEK for envelope encryption mode.</summary>
        public byte[]? WrappedDek { get; init; }

        /// <summary>KEK identifier for envelope mode.</summary>
        public string? KekId { get; init; }

        /// <summary>Timestamp when encryption occurred. Set by the encryptor; do not rely on the default value.</summary>
        public DateTime EncryptedAt { get; init; }

        /// <summary>Additional metadata for compliance and auditing.</summary>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } =
            new Dictionary<string, string>();

        /// <summary>
        /// Serializes the payload to a byte array.
        /// Format: [Version:1][AlgIdLen:1][AlgId:var][Nonce:var][TagLen:2][Tag:var][CiphertextLen:4][Ciphertext:var]
        /// </summary>
        public byte[] ToBytes()
        {
            using var ms = new System.IO.MemoryStream();
            using var writer = new System.IO.BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);

            writer.Write((byte)Version);
            var algBytes = System.Text.Encoding.UTF8.GetBytes(AlgorithmId);
            writer.Write((byte)algBytes.Length);
            writer.Write(algBytes);
            writer.Write((byte)Nonce.Length);
            writer.Write(Nonce);
            writer.Write((ushort)Tag.Length);
            writer.Write(Tag);
            writer.Write(Ciphertext.Length);
            writer.Write(Ciphertext);

            // Optional fields
            var hasKeyId = !string.IsNullOrEmpty(KeyId);
            var hasWrappedDek = WrappedDek != null && WrappedDek.Length > 0;
            byte flags = 0;
            if (hasKeyId) flags |= 0x01;
            if (hasWrappedDek) flags |= 0x02;
            writer.Write(flags);

            if (hasKeyId)
            {
                var keyIdBytes = System.Text.Encoding.UTF8.GetBytes(KeyId!);
                writer.Write((byte)keyIdBytes.Length);
                writer.Write(keyIdBytes);
            }

            if (hasWrappedDek)
            {
                writer.Write((ushort)WrappedDek!.Length);
                writer.Write(WrappedDek);
                if (!string.IsNullOrEmpty(KekId))
                {
                    var kekIdBytes = System.Text.Encoding.UTF8.GetBytes(KekId);
                    writer.Write((byte)kekIdBytes.Length);
                    writer.Write(kekIdBytes);
                }
                else
                {
                    writer.Write((byte)0);
                }
            }

            return ms.ToArray();
        }

        /// <summary>
        /// Deserializes an encrypted payload from bytes.
        /// </summary>
        public static EncryptedPayload FromBytes(byte[] data)
        {
            using var ms = new System.IO.MemoryStream(data);
            using var reader = new System.IO.BinaryReader(ms, System.Text.Encoding.UTF8);

            var version = reader.ReadByte();
            var algLen = reader.ReadByte();
            var algorithmId = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(algLen));
            var nonceLen = reader.ReadByte();
            var nonce = reader.ReadBytes(nonceLen);
            var tagLen = reader.ReadUInt16();
            var tag = reader.ReadBytes(tagLen);
            var ciphertextLen = reader.ReadInt32();
            var ciphertext = reader.ReadBytes(ciphertextLen);

            string? keyId = null;
            byte[]? wrappedDek = null;
            string? kekId = null;

            if (ms.Position < ms.Length)
            {
                var flags = reader.ReadByte();
                if ((flags & 0x01) != 0)
                {
                    var keyIdLen = reader.ReadByte();
                    keyId = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(keyIdLen));
                }
                if ((flags & 0x02) != 0)
                {
                    var dekLen = reader.ReadUInt16();
                    wrappedDek = reader.ReadBytes(dekLen);
                    var kekIdLen = reader.ReadByte();
                    if (kekIdLen > 0)
                    {
                        kekId = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(kekIdLen));
                    }
                }
            }

            return new EncryptedPayload
            {
                Version = version,
                AlgorithmId = algorithmId,
                Nonce = nonce,
                Tag = tag,
                Ciphertext = ciphertext,
                KeyId = keyId,
                WrappedDek = wrappedDek,
                KekId = kekId
            };
        }
    }

    /// <summary>
    /// Interface for encryption strategy registry.
    /// Provides auto-discovery and lookup of encryption strategies.
    /// </summary>
    public interface IEncryptionStrategyRegistry
    {
        /// <summary>Registers an encryption strategy.</summary>
        void Register(IEncryptionStrategy strategy);

        /// <summary>Gets a strategy by its ID.</summary>
        IEncryptionStrategy? GetStrategy(string strategyId);

        /// <summary>Gets all registered strategies.</summary>
        IReadOnlyCollection<IEncryptionStrategy> GetAllStrategies();

        /// <summary>Gets strategies matching security level requirements.</summary>
        IReadOnlyCollection<IEncryptionStrategy> GetStrategiesBySecurityLevel(SecurityLevel minLevel);

        /// <summary>Gets strategies that are FIPS compliant.</summary>
        IReadOnlyCollection<IEncryptionStrategy> GetFipsCompliantStrategies();

        /// <summary>Gets the default strategy.</summary>
        IEncryptionStrategy GetDefaultStrategy();

        /// <summary>Sets the default strategy.</summary>
        void SetDefaultStrategy(string strategyId);

        /// <summary>Discovers and registers strategies from assemblies.</summary>
        void DiscoverStrategies(params System.Reflection.Assembly[] assemblies);
    }

    /// <summary>
    /// Default implementation of encryption strategy registry.
    /// Delegates to the generic <see cref="DataWarehouse.SDK.Contracts.StrategyRegistry{TStrategy}"/> internally.
    /// All public APIs are unchanged for backward compatibility.
    /// </summary>
    public sealed class EncryptionStrategyRegistry : IEncryptionStrategyRegistry
    {
        private readonly DataWarehouse.SDK.Contracts.StrategyRegistry<IEncryptionStrategy> _inner =
            new(s => s.StrategyId);
        private volatile string _defaultStrategyId = "aes-256-gcm";

        /// <inheritdoc/>
        public void Register(IEncryptionStrategy strategy)
        {
            _inner.Register(strategy);
        }

        /// <inheritdoc/>
        public IEncryptionStrategy? GetStrategy(string strategyId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
            return _inner.Get(strategyId);
        }

        /// <inheritdoc/>
        public IReadOnlyCollection<IEncryptionStrategy> GetAllStrategies()
        {
            return _inner.GetAll();
        }

        /// <inheritdoc/>
        public IReadOnlyCollection<IEncryptionStrategy> GetStrategiesBySecurityLevel(SecurityLevel minLevel)
        {
            return _inner.GetByPredicate(s => s.CipherInfo.SecurityLevel >= minLevel)
                .OrderByDescending(s => s.CipherInfo.SecurityLevel)
                .ToList()
                .AsReadOnly();
        }

        /// <inheritdoc/>
        public IReadOnlyCollection<IEncryptionStrategy> GetFipsCompliantStrategies()
        {
            return _inner.GetByPredicate(s => IsFipsCompliant(s.StrategyId));
        }

        /// <inheritdoc/>
        public IEncryptionStrategy GetDefaultStrategy()
        {
            return GetStrategy(_defaultStrategyId)
                ?? throw new InvalidOperationException($"Default strategy '{_defaultStrategyId}' not found");
        }

        /// <inheritdoc/>
        public void SetDefaultStrategy(string strategyId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
            if (!_inner.ContainsStrategy(strategyId))
            {
                throw new ArgumentException($"Strategy '{strategyId}' not registered", nameof(strategyId));
            }
            _defaultStrategyId = strategyId;
        }

        /// <inheritdoc/>
        public void DiscoverStrategies(params System.Reflection.Assembly[] assemblies)
        {
            _inner.DiscoverFromAssembly(assemblies);
        }

        /// <summary>
        /// Determines if a strategy ID is FIPS 140-2/140-3 compliant.
        /// </summary>
        private static bool IsFipsCompliant(string strategyId)
        {
            var fipsAlgorithms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "aes-128-gcm", "aes-192-gcm", "aes-256-gcm",
                "aes-128-cbc", "aes-192-cbc", "aes-256-cbc",
                "aes-128-ctr", "aes-192-ctr", "aes-256-ctr",
                "aes-128-ccm", "aes-192-ccm", "aes-256-ccm",
                "3des-cbc", "triple-des",
                "sha256", "sha384", "sha512",
                "hmac-sha256", "hmac-sha384", "hmac-sha512",
                "rsa-2048", "rsa-3072", "rsa-4096",
                "ecdsa-p256", "ecdsa-p384", "ecdsa-p521",
                "ecdh-p256", "ecdh-p384", "ecdh-p521",
                "ml-kem-512", "ml-kem-768", "ml-kem-1024",
                "ml-dsa-44", "ml-dsa-65", "ml-dsa-87",
                "slh-dsa-shake-128f", "slh-dsa-shake-192f", "slh-dsa-shake-256f"
            };
            return fipsAlgorithms.Contains(strategyId);
        }

        /// <summary>
        /// Creates a pre-populated registry with common strategies.
        /// </summary>
        public static EncryptionStrategyRegistry CreateDefault()
        {
            return new EncryptionStrategyRegistry();
        }
    }

    /// <summary>
    /// Key derivation utilities for encryption operations.
    /// Provides PBKDF2, Argon2, scrypt, and HKDF implementations.
    /// </summary>
    public static class KeyDerivationUtilities
    {
        /// <summary>
        /// Derives a key using PBKDF2-SHA256.
        /// </summary>
        public static byte[] DerivePbkdf2(string password, byte[] salt, int iterations, int keyLengthBytes)
        {
            return Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                keyLengthBytes);
        }

        /// <summary>
        /// Derives a key using PBKDF2-SHA512.
        /// </summary>
        public static byte[] DerivePbkdf2Sha512(string password, byte[] salt, int iterations, int keyLengthBytes)
        {
            return Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                iterations,
                HashAlgorithmName.SHA512,
                keyLengthBytes);
        }

        /// <summary>
        /// Derives a key using HKDF-SHA256.
        /// </summary>
        public static byte[] DeriveHkdf(byte[] inputKeyMaterial, byte[] salt, byte[] info, int keyLengthBytes)
        {
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, inputKeyMaterial, keyLengthBytes, salt, info);
        }

        /// <summary>
        /// Derives a key using HKDF-SHA384.
        /// </summary>
        public static byte[] DeriveHkdfSha384(byte[] inputKeyMaterial, byte[] salt, byte[] info, int keyLengthBytes)
        {
            return HKDF.DeriveKey(HashAlgorithmName.SHA384, inputKeyMaterial, keyLengthBytes, salt, info);
        }

        /// <summary>
        /// Generates a cryptographically secure random salt.
        /// </summary>
        public static byte[] GenerateSalt(int lengthBytes = 32)
        {
            return RandomNumberGenerator.GetBytes(lengthBytes);
        }

        /// <summary>
        /// Constant-time comparison of two byte arrays.
        /// </summary>
        public static bool SecureEquals(byte[] a, byte[] b)
        {
            return CryptographicOperations.FixedTimeEquals(a, b);
        }
    }

    /// <summary>
    /// FIPS compliance validation framework.
    /// Validates encryption configurations against FIPS 140-2/140-3 requirements.
    /// </summary>
    public static class FipsComplianceValidator
    {
        /// <summary>
        /// Validates if a cipher configuration is FIPS compliant.
        /// </summary>
        public static FipsValidationResult Validate(CipherInfo cipherInfo)
        {
            var result = new FipsValidationResult { AlgorithmName = cipherInfo.AlgorithmName };

            // Check algorithm
            if (!IsFipsApprovedAlgorithm(cipherInfo.AlgorithmName))
            {
                result.IsCompliant = false;
                result.Violations.Add($"Algorithm '{cipherInfo.AlgorithmName}' is not FIPS approved");
                return result;
            }

            // Check key size
            if (!IsFipsApprovedKeySize(cipherInfo.AlgorithmName, cipherInfo.KeySizeBits))
            {
                result.IsCompliant = false;
                result.Violations.Add($"Key size {cipherInfo.KeySizeBits} bits is not FIPS approved for {cipherInfo.AlgorithmName}");
            }

            // Check mode
            if (!IsFipsApprovedMode(cipherInfo.AlgorithmName))
            {
                result.IsCompliant = false;
                result.Violations.Add($"Cipher mode in '{cipherInfo.AlgorithmName}' is not FIPS approved");
            }

            result.IsCompliant = result.Violations.Count == 0;
            return result;
        }

        private static bool IsFipsApprovedAlgorithm(string algorithmName)
        {
            var name = algorithmName.ToUpperInvariant();
            return name.Contains("AES") ||
                   name.Contains("3DES") ||
                   name.Contains("TRIPLE-DES") ||
                   name.Contains("ML-KEM") ||
                   name.Contains("ML-DSA") ||
                   name.Contains("SLH-DSA") ||
                   name.Contains("SPHINCS");
        }

        private static bool IsFipsApprovedKeySize(string algorithmName, int keySizeBits)
        {
            var name = algorithmName.ToUpperInvariant();
            if (name.Contains("AES"))
            {
                return keySizeBits is 128 or 192 or 256;
            }
            if (name.Contains("3DES") || name.Contains("TRIPLE-DES"))
            {
                return keySizeBits is 168 or 192;
            }
            if (name.Contains("ML-KEM"))
            {
                return keySizeBits >= 128; // Post-quantum has different sizing
            }
            return true;
        }

        private static bool IsFipsApprovedMode(string algorithmName)
        {
            var name = algorithmName.ToUpperInvariant();
            // ECB is not FIPS approved for data encryption
            if (name.Contains("ECB"))
            {
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Result of FIPS compliance validation.
    /// </summary>
    public sealed class FipsValidationResult
    {
        /// <summary>Algorithm name that was validated.</summary>
        public string AlgorithmName { get; init; } = "";

        /// <summary>Whether the configuration is FIPS compliant.</summary>
        public bool IsCompliant { get; set; } = true;

        /// <summary>List of compliance violations.</summary>
        public List<string> Violations { get; } = new();

        /// <summary>FIPS standard version (140-2 or 140-3).</summary>
        public string FipsVersion { get; init; } = "140-3";
    }
}
