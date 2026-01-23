using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Utilities;
using System.Security.Cryptography;
using System.Text;

namespace DataWarehouse.Plugins.ChaCha20Encryption
{
    /// <summary>
    /// ChaCha20-Poly1305 authenticated encryption plugin for DataWarehouse pipeline.
    /// Extends PipelinePluginBase for bidirectional stream transformation.
    /// Uses IKeyStore for key management with automatic nonce generation.
    ///
    /// Security features:
    /// - ChaCha20-Poly1305 authenticated encryption (AEAD)
    /// - Automatic nonce generation (96-bit)
    /// - Key ID stored with ciphertext for key rotation support
    /// - Poly1305 authentication tag verification on decryption
    /// - Secure memory cleanup using CryptographicOperations.ZeroMemory
    ///
    /// Performance characteristics:
    /// - Often faster than AES-GCM on systems without AES-NI hardware support
    /// - Constant-time operations for side-channel resistance
    /// - No padding required (stream cipher)
    ///
    /// Header format: [KeyIdLength:4][KeyId:32][Nonce:12][Tag:16][Ciphertext]
    ///
    /// Message Commands:
    /// - encryption.chacha20.configure: Configure encryption settings
    /// - encryption.chacha20.rotate: Trigger key rotation
    /// - encryption.chacha20.stats: Get encryption statistics
    /// </summary>
    public sealed class ChaCha20EncryptionPlugin : PipelinePluginBase, IDisposable
    {
        private readonly ChaCha20EncryptionConfig _config;
        private readonly object _statsLock = new();
        private IKeyStore? _keyStore;
        private ISecurityContext? _securityContext;
        private long _encryptionCount;
        private long _decryptionCount;
        private long _totalBytesEncrypted;
        private long _totalBytesDecrypted;
        private long _encryptionErrors;
        private long _decryptionErrors;
        private bool _disposed;

        /// <summary>
        /// Nonce size for ChaCha20-Poly1305 (96 bits / 12 bytes).
        /// </summary>
        private const int NonceSize = 12;

        /// <summary>
        /// Authentication tag size (128 bits / 16 bytes).
        /// </summary>
        private const int TagSize = 16;

        /// <summary>
        /// Maximum key ID size in bytes.
        /// </summary>
        private const int KeyIdSize = 32;

        /// <summary>
        /// Required key size for ChaCha20-Poly1305 (256 bits / 32 bytes).
        /// </summary>
        private const int RequiredKeySize = 32;

        /// <summary>
        /// Minimum header size: KeyIdLength(4) + KeyId(32) + Nonce(12) + Tag(16) = 64 bytes.
        /// </summary>
        private const int MinHeaderSize = 4 + KeyIdSize + NonceSize + TagSize;

        /// <inheritdoc/>
        public override string Id => "datawarehouse.plugins.encryption.chacha20poly1305";

        /// <inheritdoc/>
        public override string Name => "ChaCha20-Poly1305 Encryption";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <inheritdoc/>
        public override string SubCategory => "Encryption";

        /// <inheritdoc/>
        public override int QualityLevel => 85;

        /// <inheritdoc/>
        public override int DefaultOrder => 90;

        /// <inheritdoc/>
        public override bool AllowBypass => false;

        /// <inheritdoc/>
        public override string[] RequiredPrecedingStages => ["Compression"];

        /// <inheritdoc/>
        public override string[] IncompatibleStages => ["encryption.aes256", "encryption.zeroknowledge", "encryption.fips"];

        /// <summary>
        /// Initializes a new instance of the ChaCha20-Poly1305 encryption plugin.
        /// </summary>
        /// <param name="config">Optional configuration. If null, defaults are used.</param>
        public ChaCha20EncryptionPlugin(ChaCha20EncryptionConfig? config = null)
        {
            _config = config ?? new ChaCha20EncryptionConfig();
        }

        /// <inheritdoc/>
        public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var response = await base.OnHandshakeAsync(request);

            if (_config.KeyStore != null)
            {
                _keyStore = _config.KeyStore;
            }

            if (_config.SecurityContext != null)
            {
                _securityContext = _config.SecurityContext;
            }

            return response;
        }

        /// <inheritdoc/>
        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return
            [
                new() { Name = "encryption.chacha20.configure", DisplayName = "Configure", Description = "Configure ChaCha20-Poly1305 encryption settings" },
                new() { Name = "encryption.chacha20.rotate", DisplayName = "Rotate Key", Description = "Trigger key rotation" },
                new() { Name = "encryption.chacha20.stats", DisplayName = "Statistics", Description = "Get encryption statistics" },
                new() { Name = "encryption.chacha20.encrypt", DisplayName = "Encrypt", Description = "Encrypt data using ChaCha20-Poly1305" },
                new() { Name = "encryption.chacha20.decrypt", DisplayName = "Decrypt", Description = "Decrypt ChaCha20-Poly1305 encrypted data" }
            ];
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["Algorithm"] = "ChaCha20-Poly1305";
            metadata["KeySize"] = RequiredKeySize * 8;
            metadata["NonceSize"] = NonceSize * 8;
            metadata["TagSize"] = TagSize * 8;
            metadata["SupportsKeyRotation"] = true;
            metadata["SupportsStreaming"] = true;
            metadata["RequiresKeyStore"] = true;
            metadata["AEADMode"] = true;
            metadata["SideChannelResistant"] = true;
            return metadata;
        }

        /// <inheritdoc/>
        public override Task OnMessageAsync(PluginMessage message)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            return message.Type switch
            {
                "encryption.chacha20.configure" => HandleConfigureAsync(message),
                "encryption.chacha20.rotate" => HandleRotateAsync(message),
                "encryption.chacha20.stats" => HandleStatsAsync(message),
                "encryption.chacha20.setKeyStore" => HandleSetKeyStoreAsync(message),
                _ => base.OnMessageAsync(message)
            };
        }

        /// <summary>
        /// Encrypts data from the input stream using ChaCha20-Poly1305.
        /// </summary>
        /// <remarks>
        /// IMPORTANT: This method is synchronous due to the PipelinePluginBase interface constraint.
        /// The OnWrite method signature is defined as returning Stream (not Task&lt;Stream&gt;),
        /// which means we cannot use async/await here. We use RunSyncWithErrorHandling to safely
        /// execute async key store operations with proper error handling and context preservation.
        ///
        /// Output format: [KeyIdLength:4][KeyId:32][Nonce:12][Tag:16][Ciphertext]
        /// </remarks>
        /// <param name="input">The plaintext input stream.</param>
        /// <param name="context">The kernel context for logging and plugin discovery.</param>
        /// <param name="args">
        /// Optional arguments:
        /// - keyStore: IKeyStore instance for key management
        /// - securityContext: ISecurityContext for key access authorization
        /// </param>
        /// <returns>Stream containing the encrypted data with header.</returns>
        /// <exception cref="CryptographicException">Thrown when encryption fails.</exception>
        /// <exception cref="InvalidOperationException">Thrown when no key store is available.</exception>
        public override Stream OnWrite(Stream input, IKernelContext context, Dictionary<string, object> args)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var keyStore = GetKeyStore(args, context);
            var securityContext = GetSecurityContext(args);

            // Interface constraint: OnWrite must be synchronous, but IKeyStore methods are async.
            // Using helper method with proper error handling for sync-over-async calls.
            var keyId = RunSyncWithErrorHandling(
                () => keyStore.GetCurrentKeyIdAsync(),
                "Failed to retrieve current key ID from key store");

            var key = RunSyncWithErrorHandling(
                () => keyStore.GetKeyAsync(keyId, securityContext),
                $"Failed to retrieve key '{TruncateKeyId(keyId)}...' from key store");

            if (key.Length != RequiredKeySize)
            {
                CryptographicOperations.ZeroMemory(key);
                throw new CryptographicException($"ChaCha20-Poly1305 requires a {RequiredKeySize * 8}-bit ({RequiredKeySize}-byte) key. Got {key.Length} bytes.");
            }

            byte[]? plaintext = null;
            byte[]? ciphertext = null;
            byte[]? nonce = null;
            byte[]? tag = null;

            try
            {
                // Read all plaintext into memory
                using var inputMs = new MemoryStream();
                input.CopyTo(inputMs);
                plaintext = inputMs.ToArray();

                // Generate cryptographically secure random nonce
                nonce = RandomNumberGenerator.GetBytes(NonceSize);
                tag = new byte[TagSize];
                ciphertext = new byte[plaintext.Length];

                // Perform authenticated encryption
                using var chacha = new ChaCha20Poly1305(key);
                chacha.Encrypt(nonce, plaintext, ciphertext, tag);

                // Prepare key ID bytes (padded to KeyIdSize)
                var keyIdBytes = new byte[KeyIdSize];
                var keyIdUtf8 = Encoding.UTF8.GetBytes(keyId);
                var actualKeyIdLength = Math.Min(keyIdUtf8.Length, KeyIdSize);
                Array.Copy(keyIdUtf8, keyIdBytes, actualKeyIdLength);

                // Build output: [KeyIdLength:4][KeyId:32][Nonce:12][Tag:16][Ciphertext]
                var result = new byte[4 + KeyIdSize + NonceSize + TagSize + ciphertext.Length];
                var pos = 0;

                // Write key ID length (4 bytes, little-endian)
                BitConverter.GetBytes(keyIdUtf8.Length).CopyTo(result, pos);
                pos += 4;

                // Write key ID (padded to KeyIdSize)
                keyIdBytes.CopyTo(result, pos);
                pos += KeyIdSize;

                // Write nonce
                nonce.CopyTo(result, pos);
                pos += NonceSize;

                // Write authentication tag
                tag.CopyTo(result, pos);
                pos += TagSize;

                // Write ciphertext
                ciphertext.CopyTo(result, pos);

                // Update statistics (thread-safe)
                lock (_statsLock)
                {
                    _encryptionCount++;
                    _totalBytesEncrypted += plaintext.Length;
                }

                context.LogDebug($"ChaCha20-Poly1305 encrypted {plaintext.Length} bytes with key {TruncateKeyId(keyId)}...");

                return new MemoryStream(result);
            }
            catch (Exception ex) when (ex is not CryptographicException and not InvalidOperationException)
            {
                Interlocked.Increment(ref _encryptionErrors);
                throw new CryptographicException($"ChaCha20-Poly1305 encryption failed: {ex.Message}", ex);
            }
            finally
            {
                // Security: Clear sensitive data from memory (PCI-DSS requirement)
                if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext);
                if (ciphertext != null) CryptographicOperations.ZeroMemory(ciphertext);
                if (nonce != null) CryptographicOperations.ZeroMemory(nonce);
                if (tag != null) CryptographicOperations.ZeroMemory(tag);
                CryptographicOperations.ZeroMemory(key);
            }
        }

        /// <summary>
        /// Decrypts data from the stored stream using ChaCha20-Poly1305.
        /// </summary>
        /// <remarks>
        /// IMPORTANT: This method is synchronous due to the PipelinePluginBase interface constraint.
        /// The OnRead method signature is defined as returning Stream (not Task&lt;Stream&gt;),
        /// which means we cannot use async/await here. We use RunSyncWithErrorHandling to safely
        /// execute async key store operations with proper error handling and context preservation.
        ///
        /// Expected input format: [KeyIdLength:4][KeyId:32][Nonce:12][Tag:16][Ciphertext]
        /// </remarks>
        /// <param name="stored">The encrypted input stream with header.</param>
        /// <param name="context">The kernel context for logging and plugin discovery.</param>
        /// <param name="args">
        /// Optional arguments:
        /// - keyStore: IKeyStore instance for key management
        /// - securityContext: ISecurityContext for key access authorization
        /// </param>
        /// <returns>Stream containing the decrypted plaintext.</returns>
        /// <exception cref="CryptographicException">Thrown when decryption or authentication fails.</exception>
        /// <exception cref="InvalidOperationException">Thrown when no key store is available.</exception>
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
                // Read all encrypted data into memory
                using var inputMs = new MemoryStream();
                stored.CopyTo(inputMs);
                encryptedData = inputMs.ToArray();

                // Validate minimum header size
                if (encryptedData.Length < MinHeaderSize)
                {
                    throw new CryptographicException($"Ciphertext too short. Expected at least {MinHeaderSize} bytes, got {encryptedData.Length} bytes.");
                }

                var pos = 0;

                // Read key ID length
                var keyIdLength = BitConverter.ToInt32(encryptedData, pos);
                pos += 4;

                // Validate key ID length
                if (keyIdLength <= 0 || keyIdLength > KeyIdSize)
                {
                    throw new CryptographicException($"Invalid key ID length in header: {keyIdLength}. Expected 1-{KeyIdSize}.");
                }

                // Read and parse key ID
                var keyId = Encoding.UTF8.GetString(encryptedData, pos, keyIdLength).TrimEnd('\0');
                pos += KeyIdSize;

                // Read nonce
                var nonce = new byte[NonceSize];
                Array.Copy(encryptedData, pos, nonce, 0, NonceSize);
                pos += NonceSize;

                // Read authentication tag
                var tag = new byte[TagSize];
                Array.Copy(encryptedData, pos, tag, 0, TagSize);
                pos += TagSize;

                // Calculate and read ciphertext
                var ciphertextLength = encryptedData.Length - pos;
                if (ciphertextLength < 0)
                {
                    throw new CryptographicException("Invalid ciphertext length in encrypted data.");
                }

                var ciphertext = new byte[ciphertextLength];
                Array.Copy(encryptedData, pos, ciphertext, 0, ciphertextLength);

                // Interface constraint: OnRead must be synchronous, but IKeyStore methods are async.
                // Using helper method with proper error handling for sync-over-async calls.
                key = RunSyncWithErrorHandling(
                    () => keyStore.GetKeyAsync(keyId, securityContext),
                    $"Failed to retrieve key '{TruncateKeyId(keyId)}...' from key store for decryption");

                if (key.Length != RequiredKeySize)
                {
                    throw new CryptographicException($"ChaCha20-Poly1305 requires a {RequiredKeySize * 8}-bit ({RequiredKeySize}-byte) key. Got {key.Length} bytes.");
                }

                // Perform authenticated decryption
                plaintext = new byte[ciphertextLength];
                using var chacha = new ChaCha20Poly1305(key);

                try
                {
                    chacha.Decrypt(nonce, ciphertext, tag, plaintext);
                }
                catch (AuthenticationTagMismatchException ex)
                {
                    Interlocked.Increment(ref _decryptionErrors);
                    throw new CryptographicException("Authentication tag verification failed. Data may have been tampered with.", ex);
                }

                // Update statistics (thread-safe)
                lock (_statsLock)
                {
                    _decryptionCount++;
                    _totalBytesDecrypted += plaintext.Length;
                }

                context.LogDebug($"ChaCha20-Poly1305 decrypted {ciphertextLength} bytes with key {TruncateKeyId(keyId)}...");

                // Return a copy since we'll zero the original
                var result = new byte[plaintext.Length];
                Array.Copy(plaintext, result, plaintext.Length);
                return new MemoryStream(result);
            }
            catch (Exception ex) when (ex is not CryptographicException and not InvalidOperationException)
            {
                Interlocked.Increment(ref _decryptionErrors);
                throw new CryptographicException($"ChaCha20-Poly1305 decryption failed: {ex.Message}", ex);
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
        /// Gets the key store from arguments, configuration, or context plugins.
        /// </summary>
        private IKeyStore GetKeyStore(Dictionary<string, object> args, IKernelContext context)
        {
            // First, check if a key store was passed in arguments
            if (args.TryGetValue("keyStore", out var ksObj) && ksObj is IKeyStore ks)
            {
                return ks;
            }

            // Second, check if we have a configured key store
            if (_keyStore != null)
            {
                return _keyStore;
            }

            // Third, search for a plugin that implements IKeyStore
            var keyStorePlugin = context.GetPlugins<IPlugin>()
                .OfType<IKeyStore>()
                .FirstOrDefault();

            if (keyStorePlugin != null)
            {
                return keyStorePlugin;
            }

            throw new InvalidOperationException("No IKeyStore available. Configure a key store before using ChaCha20-Poly1305 encryption.");
        }

        /// <summary>
        /// Gets the security context from arguments or configuration.
        /// </summary>
        private ISecurityContext GetSecurityContext(Dictionary<string, object> args)
        {
            if (args.TryGetValue("securityContext", out var scObj) && scObj is ISecurityContext sc)
            {
                return sc;
            }

            return _securityContext ?? new ChaCha20SecurityContext();
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

                // Preserve InvalidOperationException for key store errors
                if (innerException is InvalidOperationException)
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
            catch (InvalidOperationException)
            {
                // Re-throw InvalidOperationExceptions without wrapping
                throw;
            }
            catch (Exception ex)
            {
                // Wrap unexpected exceptions with context
                throw new CryptographicException($"{errorContext}: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Truncates a key ID for safe logging (shows first 8 characters).
        /// </summary>
        private static string TruncateKeyId(string keyId)
        {
            return keyId.Length > 8 ? keyId[..8] : keyId;
        }

        /// <summary>
        /// Handles the configure message to update plugin settings.
        /// </summary>
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

        /// <summary>
        /// Handles the key rotation message.
        /// </summary>
        private async Task HandleRotateAsync(PluginMessage message)
        {
            if (_keyStore == null)
            {
                throw new InvalidOperationException("No key store configured for key rotation.");
            }

            var securityContext = GetSecurityContext(message.Payload as Dictionary<string, object> ?? new Dictionary<string, object>());
            var newKeyId = Guid.NewGuid().ToString("N");

            await _keyStore.CreateKeyAsync(newKeyId, securityContext);

            message.Payload["newKeyId"] = newKeyId;
            message.Payload["rotatedAt"] = DateTime.UtcNow;
        }

        /// <summary>
        /// Handles the statistics request message.
        /// </summary>
        private Task HandleStatsAsync(PluginMessage message)
        {
            lock (_statsLock)
            {
                message.Payload["EncryptionCount"] = _encryptionCount;
                message.Payload["DecryptionCount"] = _decryptionCount;
                message.Payload["TotalBytesEncrypted"] = _totalBytesEncrypted;
                message.Payload["TotalBytesDecrypted"] = _totalBytesDecrypted;
                message.Payload["EncryptionErrors"] = Interlocked.Read(ref _encryptionErrors);
                message.Payload["DecryptionErrors"] = Interlocked.Read(ref _decryptionErrors);
                message.Payload["Algorithm"] = "ChaCha20-Poly1305";
                message.Payload["KeySize"] = RequiredKeySize * 8;
                message.Payload["NonceSize"] = NonceSize * 8;
                message.Payload["TagSize"] = TagSize * 8;
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Handles the set key store message.
        /// </summary>
        private Task HandleSetKeyStoreAsync(PluginMessage message)
        {
            if (message.Payload.TryGetValue("keyStore", out var ksObj) && ksObj is IKeyStore ks)
            {
                _keyStore = ks;
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // Clear any cached references
            _keyStore = null;
            _securityContext = null;
        }
    }

    /// <summary>
    /// Configuration for the ChaCha20-Poly1305 encryption plugin.
    /// </summary>
    public sealed class ChaCha20EncryptionConfig
    {
        /// <summary>
        /// Gets or sets the key store for encryption key management.
        /// </summary>
        public IKeyStore? KeyStore { get; set; }

        /// <summary>
        /// Gets or sets the security context for key access authorization.
        /// </summary>
        public ISecurityContext? SecurityContext { get; set; }
    }

    /// <summary>
    /// Default security context for ChaCha20 encryption operations.
    /// Provides basic user identification for key access.
    /// </summary>
    internal sealed class ChaCha20SecurityContext : ISecurityContext
    {
        /// <inheritdoc/>
        public string UserId => Environment.UserName;

        /// <inheritdoc/>
        public string? TenantId => "chacha20-local";

        /// <inheritdoc/>
        public IEnumerable<string> Roles => ["user"];

        /// <inheritdoc/>
        public bool IsSystemAdmin => false;
    }
}
