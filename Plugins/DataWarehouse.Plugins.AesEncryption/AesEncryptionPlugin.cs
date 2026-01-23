using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace DataWarehouse.Plugins.AesEncryption
{
    /// <summary>
    /// Production-ready AES-256-GCM authenticated encryption plugin for DataWarehouse pipeline.
    /// Extends PipelinePluginBase for bidirectional stream transformation with IKeyStore integration.
    ///
    /// Security Features:
    /// - AES-256-GCM authenticated encryption (NIST SP 800-38D compliant)
    /// - Cryptographically secure random IV generation (96-bit / 12 bytes for GCM)
    /// - 128-bit authentication tag for integrity and authenticity verification
    /// - Key ID stored with ciphertext for seamless key rotation support
    /// - Secure memory clearing for sensitive data (PCI-DSS compliance)
    /// - Thread-safe statistics tracking
    ///
    /// Header Format: [KeyIdLength:4][KeyId:32][IV:12][Tag:16][Ciphertext:...]
    ///
    /// Thread Safety: All operations are thread-safe.
    ///
    /// Message Commands:
    /// - encryption.aes256gcm.configure: Configure encryption settings
    /// - encryption.aes256gcm.rotate: Trigger key rotation
    /// - encryption.aes256gcm.stats: Get encryption statistics
    /// - encryption.aes256gcm.setKeyStore: Set the key store
    /// </summary>
    public sealed class AesEncryptionPlugin : PipelinePluginBase, IDisposable
    {
        private readonly AesEncryptionConfig _config;
        private readonly object _statsLock = new();
        private readonly ConcurrentDictionary<string, DateTime> _keyAccessLog = new();
        private IKeyStore? _keyStore;
        private ISecurityContext? _securityContext;
        private long _encryptionCount;
        private long _decryptionCount;
        private long _totalBytesEncrypted;
        private long _totalBytesDecrypted;
        private bool _disposed;

        /// <summary>
        /// IV size for AES-GCM (96 bits / 12 bytes as per NIST SP 800-38D).
        /// </summary>
        private const int IvSizeBytes = 12;

        /// <summary>
        /// Authentication tag size (128 bits / 16 bytes for maximum security).
        /// </summary>
        private const int TagSizeBytes = 16;

        /// <summary>
        /// Key ID field size in header (fixed 32 bytes).
        /// </summary>
        private const int KeyIdFieldSize = 32;

        /// <summary>
        /// Size of key ID length prefix (4 bytes for Int32).
        /// </summary>
        private const int KeyIdLengthSize = 4;

        /// <summary>
        /// Total header size: KeyIdLength(4) + KeyId(32) + IV(12) + Tag(16) = 64 bytes.
        /// </summary>
        private const int HeaderSize = KeyIdLengthSize + KeyIdFieldSize + IvSizeBytes + TagSizeBytes;

        /// <inheritdoc/>
        public override string Id => "datawarehouse.plugins.encryption.aes256gcm";

        /// <inheritdoc/>
        public override string Name => "AES-256-GCM Encryption";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <inheritdoc/>
        public override string SubCategory => "Encryption";

        /// <inheritdoc/>
        public override int QualityLevel => 90;

        /// <inheritdoc/>
        public override int DefaultOrder => 90;

        /// <inheritdoc/>
        public override bool AllowBypass => false;

        /// <inheritdoc/>
        public override string[] RequiredPrecedingStages => ["Compression"];

        /// <inheritdoc/>
        public override string[] IncompatibleStages => ["encryption.chacha20", "encryption.fips"];

        /// <summary>
        /// Initializes a new instance of the AES-256-GCM encryption plugin.
        /// </summary>
        /// <param name="config">Optional configuration. If null, defaults are used.</param>
        public AesEncryptionPlugin(AesEncryptionConfig? config = null)
        {
            _config = config ?? new AesEncryptionConfig();
        }

        /// <inheritdoc/>
        public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
        {
            var response = await base.OnHandshakeAsync(request);

            if (_config.KeyStore != null)
            {
                _keyStore = _config.KeyStore;
            }

            if (_config.SecurityContext != null)
            {
                _securityContext = _config.SecurityContext;
            }

            // Verify AES-GCM is available on this platform
            try
            {
                VerifyAesGcmAvailability();
            }
            catch (PlatformNotSupportedException)
            {
                response.Success = false;
                response.ReadyState = PluginReadyState.Failed;
            }

            return response;
        }

        /// <inheritdoc/>
        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return
            [
                new() { Name = "encryption.aes256gcm.configure", DisplayName = "Configure", Description = "Configure AES-256-GCM encryption settings" },
                new() { Name = "encryption.aes256gcm.rotate", DisplayName = "Rotate Key", Description = "Trigger key rotation" },
                new() { Name = "encryption.aes256gcm.stats", DisplayName = "Statistics", Description = "Get encryption statistics" },
                new() { Name = "encryption.aes256gcm.setKeyStore", DisplayName = "Set Key Store", Description = "Configure the key store" },
                new() { Name = "encryption.aes256gcm.encrypt", DisplayName = "Encrypt", Description = "Encrypt data using AES-256-GCM" },
                new() { Name = "encryption.aes256gcm.decrypt", DisplayName = "Decrypt", Description = "Decrypt AES-256-GCM encrypted data" }
            ];
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["Algorithm"] = "AES-256-GCM";
            metadata["KeySize"] = 256;
            metadata["IVSize"] = IvSizeBytes * 8;
            metadata["TagSize"] = TagSizeBytes * 8;
            metadata["HeaderSize"] = HeaderSize;
            metadata["SupportsKeyRotation"] = true;
            metadata["SupportsStreaming"] = true;
            metadata["RequiresKeyStore"] = true;
            metadata["NistCompliant"] = true;
            return metadata;
        }

        /// <inheritdoc/>
        public override Task OnMessageAsync(PluginMessage message)
        {
            return message.Type switch
            {
                "encryption.aes256gcm.configure" => HandleConfigureAsync(message),
                "encryption.aes256gcm.rotate" => HandleRotateAsync(message),
                "encryption.aes256gcm.stats" => HandleStatsAsync(message),
                "encryption.aes256gcm.setKeyStore" => HandleSetKeyStoreAsync(message),
                _ => base.OnMessageAsync(message)
            };
        }

        /// <summary>
        /// Encrypts data from the input stream using AES-256-GCM authenticated encryption.
        /// </summary>
        /// <param name="input">The plaintext input stream.</param>
        /// <param name="context">The kernel context for logging and plugin access.</param>
        /// <param name="args">
        /// Optional arguments:
        /// - keyStore: IKeyStore instance
        /// - securityContext: ISecurityContext for key access
        /// - keyId: Specific key ID to use (otherwise current key is used)
        /// </param>
        /// <returns>A stream containing the encrypted data with header.</returns>
        /// <exception cref="InvalidOperationException">Thrown when no key store is configured.</exception>
        /// <exception cref="CryptographicException">Thrown on encryption failure.</exception>
        /// <remarks>
        /// IMPORTANT: This method is synchronous due to the PipelinePluginBase interface constraint.
        /// The OnWrite method signature is defined as returning Stream (not Task&lt;Stream&gt;),
        /// which means we cannot use async/await here. We use RunSyncWithErrorHandling to safely
        /// execute async key store operations with proper error handling and context preservation.
        ///
        /// Output format: [KeyIdLength:4][KeyId:32][IV:12][Tag:16][Ciphertext:...]
        /// </remarks>
        public override Stream OnWrite(Stream input, IKernelContext context, Dictionary<string, object> args)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var keyStore = GetKeyStore(args, context);
            var securityContext = GetSecurityContext(args);

            // Determine key ID to use
            string keyId;
            if (args.TryGetValue("keyId", out var kidObj) && kidObj is string specificKeyId)
            {
                keyId = specificKeyId;
            }
            else
            {
                // Interface constraint: OnWrite must be synchronous, but IKeyStore methods are async.
                // Using helper method with proper error handling for sync-over-async calls.
                keyId = RunSyncWithErrorHandling(
                    () => keyStore.GetCurrentKeyIdAsync(),
                    "Failed to retrieve current key ID from key store");
            }

            var key = RunSyncWithErrorHandling(
                () => keyStore.GetKeyAsync(keyId, securityContext),
                $"Failed to retrieve key from key store for encryption");

            if (key.Length != 32)
            {
                CryptographicOperations.ZeroMemory(key);
                throw new CryptographicException($"AES-256 requires a 256-bit (32-byte) key. Received {key.Length * 8}-bit key.");
            }

            byte[]? plaintext = null;
            byte[]? ciphertext = null;
            byte[]? iv = null;
            byte[]? tag = null;

            try
            {
                // Read all input data
                using var inputMs = new MemoryStream();
                input.CopyTo(inputMs);
                plaintext = inputMs.ToArray();

                // Generate cryptographically secure random IV
                iv = RandomNumberGenerator.GetBytes(IvSizeBytes);
                tag = new byte[TagSizeBytes];
                ciphertext = new byte[plaintext.Length];

                // Perform AES-256-GCM encryption
                using var aesGcm = new AesGcm(key, TagSizeBytes);
                aesGcm.Encrypt(iv, plaintext, ciphertext, tag);

                // Prepare key ID bytes (UTF-8 encoded, padded to KeyIdFieldSize)
                var keyIdUtf8 = Encoding.UTF8.GetBytes(keyId);
                if (keyIdUtf8.Length > KeyIdFieldSize)
                {
                    throw new CryptographicException($"Key ID exceeds maximum length of {KeyIdFieldSize} bytes when UTF-8 encoded");
                }

                var keyIdBytes = new byte[KeyIdFieldSize];
                Array.Copy(keyIdUtf8, keyIdBytes, keyIdUtf8.Length);

                // Build output: [KeyIdLength:4][KeyId:32][IV:12][Tag:16][Ciphertext]
                var outputLength = HeaderSize + ciphertext.Length;
                var output = new byte[outputLength];
                var pos = 0;

                // Write key ID length (4 bytes, little-endian)
                BitConverter.GetBytes(keyIdUtf8.Length).CopyTo(output, pos);
                pos += KeyIdLengthSize;

                // Write key ID (32 bytes, null-padded)
                keyIdBytes.CopyTo(output, pos);
                pos += KeyIdFieldSize;

                // Write IV (12 bytes)
                iv.CopyTo(output, pos);
                pos += IvSizeBytes;

                // Write authentication tag (16 bytes)
                tag.CopyTo(output, pos);
                pos += TagSizeBytes;

                // Write ciphertext
                ciphertext.CopyTo(output, pos);

                // Update statistics (thread-safe)
                lock (_statsLock)
                {
                    _encryptionCount++;
                    _totalBytesEncrypted += plaintext.Length;
                }

                LogKeyAccess(keyId);
                context.LogDebug($"AES-256-GCM encrypted {plaintext.Length} bytes with key {TruncateKeyId(keyId)}");

                return new MemoryStream(output);
            }
            finally
            {
                // Security: Clear sensitive data from memory (PCI-DSS requirement)
                if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext);
                if (ciphertext != null) CryptographicOperations.ZeroMemory(ciphertext);
                CryptographicOperations.ZeroMemory(key);
            }
        }

        /// <summary>
        /// Decrypts data from the stored stream using AES-256-GCM authenticated encryption.
        /// </summary>
        /// <param name="stored">The encrypted input stream with header.</param>
        /// <param name="context">The kernel context for logging and plugin access.</param>
        /// <param name="args">
        /// Optional arguments:
        /// - keyStore: IKeyStore instance
        /// - securityContext: ISecurityContext for key access
        /// </param>
        /// <returns>A stream containing the decrypted plaintext.</returns>
        /// <exception cref="InvalidOperationException">Thrown when no key store is configured.</exception>
        /// <exception cref="CryptographicException">
        /// Thrown on decryption failure or authentication tag verification failure.
        /// </exception>
        /// <remarks>
        /// IMPORTANT: This method is synchronous due to the PipelinePluginBase interface constraint.
        /// The OnRead method signature is defined as returning Stream (not Task&lt;Stream&gt;),
        /// which means we cannot use async/await here. We use RunSyncWithErrorHandling to safely
        /// execute async key store operations with proper error handling and context preservation.
        /// </remarks>
        public override Stream OnRead(Stream stored, IKernelContext context, Dictionary<string, object> args)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var keyStore = GetKeyStore(args, context);
            var securityContext = GetSecurityContext(args);

            byte[]? encryptedData = null;
            byte[]? plaintext = null;
            byte[]? key = null;

            try
            {
                // Read all encrypted data
                using var inputMs = new MemoryStream();
                stored.CopyTo(inputMs);
                encryptedData = inputMs.ToArray();

                // Validate minimum size
                if (encryptedData.Length < HeaderSize)
                {
                    throw new CryptographicException($"Encrypted data too short. Minimum size is {HeaderSize} bytes (header only). Received {encryptedData.Length} bytes.");
                }

                var pos = 0;

                // Read key ID length (4 bytes)
                var keyIdLength = BitConverter.ToInt32(encryptedData, pos);
                pos += KeyIdLengthSize;

                if (keyIdLength <= 0 || keyIdLength > KeyIdFieldSize)
                {
                    throw new CryptographicException($"Invalid key ID length in header: {keyIdLength}. Expected 1-{KeyIdFieldSize}.");
                }

                // Read key ID (extract actual length from padded field)
                var keyId = Encoding.UTF8.GetString(encryptedData, pos, keyIdLength).TrimEnd('\0');
                pos += KeyIdFieldSize;

                // Read IV (12 bytes)
                var iv = new byte[IvSizeBytes];
                Array.Copy(encryptedData, pos, iv, 0, IvSizeBytes);
                pos += IvSizeBytes;

                // Read authentication tag (16 bytes)
                var tag = new byte[TagSizeBytes];
                Array.Copy(encryptedData, pos, tag, 0, TagSizeBytes);
                pos += TagSizeBytes;

                // Read ciphertext (remaining bytes)
                var ciphertextLength = encryptedData.Length - pos;
                if (ciphertextLength < 0)
                {
                    throw new CryptographicException("Encrypted data truncated: no ciphertext present");
                }

                var ciphertext = new byte[ciphertextLength];
                if (ciphertextLength > 0)
                {
                    Array.Copy(encryptedData, pos, ciphertext, 0, ciphertextLength);
                }

                // Retrieve decryption key
                // Interface constraint: OnRead must be synchronous, but IKeyStore methods are async.
                // Using helper method with proper error handling for sync-over-async calls.
                key = RunSyncWithErrorHandling(
                    () => keyStore.GetKeyAsync(keyId, securityContext),
                    $"Failed to retrieve key from key store for decryption");

                if (key.Length != 32)
                {
                    throw new CryptographicException($"AES-256 requires a 256-bit (32-byte) key. Retrieved key is {key.Length * 8}-bit.");
                }

                // Perform AES-256-GCM decryption with authentication
                plaintext = new byte[ciphertextLength];

                using var aesGcm = new AesGcm(key, TagSizeBytes);
                aesGcm.Decrypt(iv, ciphertext, tag, plaintext);

                // Update statistics (thread-safe)
                lock (_statsLock)
                {
                    _decryptionCount++;
                    _totalBytesDecrypted += plaintext.Length;
                }

                LogKeyAccess(keyId);
                context.LogDebug($"AES-256-GCM decrypted {ciphertextLength} bytes with key {TruncateKeyId(keyId)}");

                // Return a copy since we'll zero the original
                var result = new byte[plaintext.Length];
                Array.Copy(plaintext, result, plaintext.Length);
                return new MemoryStream(result);
            }
            catch (AuthenticationTagMismatchException ex)
            {
                throw new CryptographicException("Authentication tag verification failed. Data may be corrupted or tampered with.", ex);
            }
            finally
            {
                // Security: Clear sensitive data from memory (PCI-DSS requirement)
                if (encryptedData != null) CryptographicOperations.ZeroMemory(encryptedData);
                if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext);
                if (key != null) CryptographicOperations.ZeroMemory(key);
            }
        }

        /// <summary>
        /// Verifies that AES-GCM is available on this platform.
        /// </summary>
        /// <exception cref="PlatformNotSupportedException">Thrown if AES-GCM is not available.</exception>
        private static void VerifyAesGcmAvailability()
        {
            var testKey = RandomNumberGenerator.GetBytes(32);
            var testIv = RandomNumberGenerator.GetBytes(IvSizeBytes);
            var testData = new byte[16];
            var testCiphertext = new byte[16];
            var testTag = new byte[TagSizeBytes];

            try
            {
                using var aesGcm = new AesGcm(testKey, TagSizeBytes);
                aesGcm.Encrypt(testIv, testData, testCiphertext, testTag);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(testKey);
                CryptographicOperations.ZeroMemory(testCiphertext);
                CryptographicOperations.ZeroMemory(testTag);
            }
        }

        /// <summary>
        /// Gets the key store from arguments, plugin configuration, or kernel context.
        /// </summary>
        private IKeyStore GetKeyStore(Dictionary<string, object> args, IKernelContext context)
        {
            if (args.TryGetValue("keyStore", out var ksObj) && ksObj is IKeyStore ks)
            {
                return ks;
            }

            if (_keyStore != null)
            {
                return _keyStore;
            }

            // Search for a plugin that implements IKeyStore
            var keyStorePlugin = context.GetPlugins<IPlugin>()
                .OfType<IKeyStore>()
                .FirstOrDefault();

            if (keyStorePlugin != null)
            {
                return keyStorePlugin;
            }

            throw new InvalidOperationException(
                "No IKeyStore available. Configure a key store before using encryption. " +
                "Use the 'encryption.aes256gcm.setKeyStore' message or pass 'keyStore' in args.");
        }

        /// <summary>
        /// Gets the security context from arguments or plugin configuration.
        /// </summary>
        private ISecurityContext GetSecurityContext(Dictionary<string, object> args)
        {
            if (args.TryGetValue("securityContext", out var scObj) && scObj is ISecurityContext sc)
            {
                return sc;
            }

            return _securityContext ?? new DefaultSecurityContext();
        }

        /// <summary>
        /// Safely executes an async operation synchronously with proper error handling.
        /// </summary>
        /// <remarks>
        /// This helper is necessary because the PipelinePluginBase interface defines OnWrite/OnRead
        /// as synchronous methods (returning Stream), but key store operations are async.
        ///
        /// We use Task.Run to avoid potential deadlocks that can occur when calling
        /// .GetAwaiter().GetResult() directly on a task that may use the current synchronization context.
        /// This approach schedules the async work on the thread pool, avoiding context capture issues.
        ///
        /// Error handling preserves the original exception type when possible, wrapping in
        /// CryptographicException for consistent error handling in encryption operations.
        /// </remarks>
        /// <typeparam name="T">The return type of the async operation.</typeparam>
        /// <param name="asyncOperation">The async operation to execute.</param>
        /// <param name="errorContext">Context message for error reporting.</param>
        /// <returns>The result of the async operation.</returns>
        /// <exception cref="CryptographicException">Thrown when the async operation fails.</exception>
        private static T RunSyncWithErrorHandling<T>(Func<Task<T>> asyncOperation, string errorContext)
        {
            try
            {
                // Use Task.Run to avoid synchronization context deadlocks
                // This schedules the async work on the thread pool
                return Task.Run(asyncOperation).GetAwaiter().GetResult();
            }
            catch (AggregateException ae) when (ae.InnerException != null)
            {
                // Unwrap AggregateException to get the actual exception
                var innerException = ae.InnerException;

                // Preserve CryptographicException as-is for consistent error handling
                if (innerException is CryptographicException)
                {
                    throw innerException;
                }

                // Wrap other exceptions with context
                throw new CryptographicException($"{errorContext}: {innerException.Message}", innerException);
            }
            catch (CryptographicException)
            {
                // Re-throw CryptographicExceptions without wrapping
                throw;
            }
            catch (Exception ex)
            {
                // Wrap unexpected exceptions with context
                throw new CryptographicException($"{errorContext}: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Logs key access for auditing purposes.
        /// </summary>
        private void LogKeyAccess(string keyId)
        {
            _keyAccessLog[keyId] = DateTime.UtcNow;
        }

        /// <summary>
        /// Truncates a key ID for safe logging (shows first 8 characters).
        /// </summary>
        private static string TruncateKeyId(string keyId)
        {
            return keyId.Length > 8 ? $"{keyId[..8]}..." : keyId;
        }

        private Task HandleConfigureAsync(PluginMessage message)
        {
            if (message.Payload.TryGetValue("keyStore", out var ksObj) && ksObj is IKeyStore ks)
            {
                _keyStore = ks;
            }

            if (message.Payload.TryGetValue("securityContext", out var scObj) && scObj is ISecurityContext sc)
            {
                _securityContext = sc;
            }

            return Task.CompletedTask;
        }

        private async Task HandleRotateAsync(PluginMessage message)
        {
            if (_keyStore == null)
            {
                throw new InvalidOperationException("No key store configured. Cannot rotate keys.");
            }

            var context = GetSecurityContext(message.Payload as Dictionary<string, object> ?? new());
            var newKeyId = Guid.NewGuid().ToString("N");
            await _keyStore.CreateKeyAsync(newKeyId, context);

            message.Payload["NewKeyId"] = newKeyId;
            message.Payload["RotatedAt"] = DateTime.UtcNow;
        }

        private Task HandleStatsAsync(PluginMessage message)
        {
            lock (_statsLock)
            {
                message.Payload["EncryptionCount"] = _encryptionCount;
                message.Payload["DecryptionCount"] = _decryptionCount;
                message.Payload["TotalBytesEncrypted"] = _totalBytesEncrypted;
                message.Payload["TotalBytesDecrypted"] = _totalBytesDecrypted;
                message.Payload["UniqueKeysUsed"] = _keyAccessLog.Count;
                message.Payload["Algorithm"] = "AES-256-GCM";
                message.Payload["IVSizeBits"] = IvSizeBytes * 8;
                message.Payload["TagSizeBits"] = TagSizeBytes * 8;
            }

            return Task.CompletedTask;
        }

        private Task HandleSetKeyStoreAsync(PluginMessage message)
        {
            if (message.Payload.TryGetValue("keyStore", out var ksObj) && ksObj is IKeyStore ks)
            {
                _keyStore = ks;
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// Releases all resources used by this plugin.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _keyAccessLog.Clear();
        }
    }

    /// <summary>
    /// Configuration for AES-256-GCM encryption plugin.
    /// </summary>
    public sealed class AesEncryptionConfig
    {
        /// <summary>
        /// Gets or sets the key store to use for encryption keys.
        /// </summary>
        public IKeyStore? KeyStore { get; set; }

        /// <summary>
        /// Gets or sets the security context for key access.
        /// </summary>
        public ISecurityContext? SecurityContext { get; set; }
    }

    /// <summary>
    /// Default security context for AES encryption operations.
    /// </summary>
    internal sealed class DefaultSecurityContext : ISecurityContext
    {
        /// <inheritdoc/>
        public string UserId => Environment.UserName;

        /// <inheritdoc/>
        public string? TenantId => "local";

        /// <inheritdoc/>
        public IEnumerable<string> Roles => ["user"];

        /// <inheritdoc/>
        public bool IsSystemAdmin => false;
    }
}
