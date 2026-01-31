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
    /// Extends EncryptionPluginBase for composable key management with Direct and Envelope modes.
    ///
    /// Security features:
    /// - ChaCha20-Poly1305 authenticated encryption (AEAD)
    /// - Automatic nonce generation (96-bit)
    /// - Composable key management: Direct mode (IKeyStore) or Envelope mode (IEnvelopeKeyStore)
    /// - Poly1305 authentication tag verification on decryption
    /// - Secure memory cleanup using CryptographicOperations.ZeroMemory
    ///
    /// Performance characteristics:
    /// - Often faster than AES-GCM on systems without AES-NI hardware support
    /// - Constant-time operations for side-channel resistance
    /// - No padding required (stream cipher)
    ///
    /// Data Format (when using manifest-based metadata):
    /// [IV:12][Tag:16][Ciphertext:...]
    ///
    /// Legacy Format (backward compatibility for old encrypted files):
    /// [KeyIdLength:4][KeyId:32][Nonce:12][Tag:16][Ciphertext:...]
    ///
    /// Thread Safety: All operations are thread-safe.
    ///
    /// Message Commands:
    /// - encryption.chacha20.configure: Configure encryption settings
    /// - encryption.chacha20.rotate: Trigger key rotation
    /// - encryption.chacha20.stats: Get encryption statistics
    /// - encryption.chacha20.setKeyStore: Set the key store
    /// </summary>
    public sealed class ChaCha20EncryptionPlugin : EncryptionPluginBase
    {
        private readonly ChaCha20EncryptionConfig _config;

        /// <summary>
        /// Key ID field size in header (fixed 32 bytes) - for legacy format only.
        /// </summary>
        private const int KeyIdFieldSize = 32;

        /// <summary>
        /// Size of key ID length prefix (4 bytes for Int32) - for legacy format only.
        /// </summary>
        private const int KeyIdLengthSize = 4;

        /// <summary>
        /// Total legacy header size: KeyIdLength(4) + KeyId(32) + Nonce(12) + Tag(16) = 64 bytes.
        /// </summary>
        private int LegacyHeaderSize => KeyIdLengthSize + KeyIdFieldSize + IvSizeBytes + TagSizeBytes;

        #region Abstract Property Overrides

        /// <inheritdoc/>
        protected override int KeySizeBytes => 32; // 256 bits

        /// <inheritdoc/>
        protected override int IvSizeBytes => 12; // 96-bit nonce

        /// <inheritdoc/>
        protected override int TagSizeBytes => 16; // 128-bit Poly1305 tag

        /// <inheritdoc/>
        protected override string AlgorithmId => "ChaCha20-Poly1305";

        #endregion

        #region Plugin Identity


        /// <inheritdoc/>
        public override string Id => "datawarehouse.plugins.encryption.chacha20poly1305";

        /// <inheritdoc/>
        public override string Name => "ChaCha20-Poly1305 Encryption";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

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

        #endregion

        /// <summary>
        /// Initializes a new instance of the ChaCha20-Poly1305 encryption plugin.
        /// </summary>
        /// <param name="config">Optional configuration. If null, defaults are used.</param>
        public ChaCha20EncryptionPlugin(ChaCha20EncryptionConfig? config = null)
        {
            _config = config ?? new ChaCha20EncryptionConfig();

            // Set defaults from config (backward compatibility)
            if (_config.KeyStore != null)
            {
                DefaultKeyStore = _config.KeyStore;
            }
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
            metadata["Algorithm"] = AlgorithmId;
            metadata["KeySize"] = KeySizeBytes * 8;
            metadata["NonceSize"] = IvSizeBytes * 8;
            metadata["TagSize"] = TagSizeBytes * 8;
            metadata["SupportsKeyRotation"] = true;
            metadata["SupportsStreaming"] = true;
            metadata["RequiresKeyStore"] = true;
            metadata["AEADMode"] = true;
            metadata["SideChannelResistant"] = true;
            metadata["SupportedModes"] = new[] { "Direct", "Envelope" };
            return metadata;
        }

        /// <inheritdoc/>
        public override Task OnMessageAsync(PluginMessage message)
        {
            return message.Type switch
            {
                "encryption.chacha20.configure" => HandleConfigureAsync(message),
                "encryption.chacha20.rotate" => HandleRotateAsync(message),
                "encryption.chacha20.stats" => HandleStatsAsync(message),
                "encryption.chacha20.setKeyStore" => HandleSetKeyStoreAsync(message),
                _ => base.OnMessageAsync(message)
            };
        }

        #region Core Encryption/Decryption (Algorithm-Specific)

        /// <summary>
        /// Performs ChaCha20-Poly1305 encryption on the input stream.
        /// Base class handles key resolution, config resolution, and statistics.
        /// </summary>
        /// <param name="input">The plaintext input stream.</param>
        /// <param name="key">The encryption key (provided by base class).</param>
        /// <param name="iv">The initialization vector (nonce, provided by base class).</param>
        /// <param name="context">The kernel context for logging.</param>
        /// <returns>A stream containing [IV:12][Tag:16][Ciphertext].</returns>
        /// <exception cref="CryptographicException">Thrown on encryption failure.</exception>
        protected override async Task<Stream> EncryptCoreAsync(Stream input, byte[] key, byte[] iv, IKernelContext context)
        {
            byte[]? plaintext = null;
            byte[]? ciphertext = null;
            byte[]? tag = null;

            try
            {
                // Read all input data
                using var inputMs = new MemoryStream();
                await input.CopyToAsync(inputMs);
                plaintext = inputMs.ToArray();

                tag = new byte[TagSizeBytes];
                ciphertext = new byte[plaintext.Length];

                // Perform ChaCha20-Poly1305 encryption
                using var chacha = new ChaCha20Poly1305(key);
                chacha.Encrypt(iv, plaintext, ciphertext, tag);

                // Build output: [IV:12][Tag:16][Ciphertext]
                // Note: Key info is now stored in EncryptionMetadata by base class
                var outputLength = IvSizeBytes + TagSizeBytes + ciphertext.Length;
                var output = new byte[outputLength];
                var pos = 0;

                // Write IV (12 bytes)
                iv.CopyTo(output, pos);
                pos += IvSizeBytes;

                // Write authentication tag (16 bytes)
                tag.CopyTo(output, pos);
                pos += TagSizeBytes;

                // Write ciphertext
                ciphertext.CopyTo(output, pos);

                context.LogDebug($"ChaCha20-Poly1305 encrypted {plaintext.Length} bytes");

                return new MemoryStream(output);
            }
            finally
            {
                // Security: Clear sensitive data from memory (PCI-DSS requirement)
                if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext);
                if (ciphertext != null) CryptographicOperations.ZeroMemory(ciphertext);
            }
        }

        /// <summary>
        /// Performs ChaCha20-Poly1305 decryption on the input stream.
        /// Base class handles key resolution, config resolution, and statistics.
        /// Supports both new format [IV:12][Tag:16][Ciphertext] and legacy format with key ID header.
        /// </summary>
        /// <param name="input">The encrypted input stream.</param>
        /// <param name="key">The decryption key (provided by base class).</param>
        /// <param name="iv">The initialization vector (nonce, null if embedded in ciphertext).</param>
        /// <param name="context">The kernel context for logging.</param>
        /// <returns>The decrypted stream and authentication tag.</returns>
        /// <exception cref="CryptographicException">
        /// Thrown on decryption failure or authentication tag verification failure.
        /// </exception>
        protected override async Task<(Stream data, byte[]? tag)> DecryptCoreAsync(Stream input, byte[] key, byte[]? iv, IKernelContext context)
        {
            byte[]? encryptedData = null;
            byte[]? plaintext = null;

            try
            {
                // Read all encrypted data
                using var inputMs = new MemoryStream();
                await input.CopyToAsync(inputMs);
                encryptedData = inputMs.ToArray();

                // Check if this is legacy format (has key ID header)
                var isLegacyFormat = IsLegacyFormat(encryptedData);

                var pos = 0;

                if (isLegacyFormat)
                {
                    // Legacy format: [KeyIdLength:4][KeyId:32][IV:12][Tag:16][Ciphertext]
                    if (encryptedData.Length < LegacyHeaderSize)
                        throw new CryptographicException($"Legacy encrypted data too short. Minimum size is {LegacyHeaderSize} bytes.");

                    // Skip key ID header (already resolved by base class)
                    pos += KeyIdLengthSize + KeyIdFieldSize;
                }

                // Parse: [IV:12][Tag:16][Ciphertext]
                var remainingLength = encryptedData.Length - pos;
                if (remainingLength < IvSizeBytes + TagSizeBytes)
                    throw new CryptographicException("Encrypted data too short");

                // If IV not provided by base class, read from data
                if (iv == null)
                {
                    iv = new byte[IvSizeBytes];
                    Array.Copy(encryptedData, pos, iv, 0, IvSizeBytes);
                    pos += IvSizeBytes;
                }
                else
                {
                    // IV provided by base class (from metadata), skip in data
                    pos += IvSizeBytes;
                }

                // Read authentication tag (16 bytes)
                var tag = new byte[TagSizeBytes];
                Array.Copy(encryptedData, pos, tag, 0, TagSizeBytes);
                pos += TagSizeBytes;

                // Read ciphertext (remaining bytes)
                var ciphertextLength = encryptedData.Length - pos;
                var ciphertext = new byte[ciphertextLength];
                if (ciphertextLength > 0)
                {
                    Array.Copy(encryptedData, pos, ciphertext, 0, ciphertextLength);
                }

                // Perform ChaCha20-Poly1305 decryption with authentication
                plaintext = new byte[ciphertextLength];

                using var chacha = new ChaCha20Poly1305(key);
                chacha.Decrypt(iv, ciphertext, tag, plaintext);

                context.LogDebug($"ChaCha20-Poly1305 decrypted {ciphertextLength} bytes");

                // Return a copy since we'll zero the original
                var result = new byte[plaintext.Length];
                Array.Copy(plaintext, result, plaintext.Length);
                return (new MemoryStream(result), tag);
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
            }
        }

        /// <summary>
        /// Checks if the encrypted data uses the legacy format with key ID header.
        /// </summary>
        private bool IsLegacyFormat(byte[] data)
        {
            if (data.Length < KeyIdLengthSize + KeyIdFieldSize)
                return false;

            // Read key ID length from header
            var keyIdLength = BitConverter.ToInt32(data, 0);

            // Legacy format has a valid key ID length (1 to KeyIdFieldSize)
            return keyIdLength > 0 && keyIdLength <= KeyIdFieldSize;
        }

        #endregion

        #region Helper Methods

        #endregion

        #region Message Handlers

        private Task HandleConfigureAsync(PluginMessage message)
        {
            // Use base class configuration methods
            if (message.Payload.TryGetValue("keyStore", out var ksObj) && ksObj is IKeyStore ks)
            {
                SetDefaultKeyStore(ks);
            }

            if (message.Payload.TryGetValue("envelopeKeyStore", out var eksObj) && eksObj is IEnvelopeKeyStore eks &&
                message.Payload.TryGetValue("kekKeyId", out var kekObj) && kekObj is string kek)
            {
                SetDefaultEnvelopeKeyStore(eks, kek);
            }

            if (message.Payload.TryGetValue("mode", out var modeObj))
            {
                if (modeObj is KeyManagementMode mode)
                {
                    SetDefaultMode(mode);
                }
                else if (modeObj is string modeStr && Enum.TryParse<KeyManagementMode>(modeStr, true, out var parsedMode))
                {
                    SetDefaultMode(parsedMode);
                }
            }

            return Task.CompletedTask;
        }

        private async Task HandleRotateAsync(PluginMessage message)
        {
            if (DefaultKeyStore == null)
            {
                throw new InvalidOperationException("No key store configured. Cannot rotate keys.");
            }

            // Get security context from message
            var securityContext = message.Payload.TryGetValue("securityContext", out var scObj) && scObj is ISecurityContext sc
                ? sc
                : new DefaultSecurityContext();

            var newKeyId = Guid.NewGuid().ToString("N");
            await DefaultKeyStore.CreateKeyAsync(newKeyId, securityContext);

            message.Payload["NewKeyId"] = newKeyId;
            message.Payload["RotatedAt"] = DateTime.UtcNow;
        }

        private Task HandleStatsAsync(PluginMessage message)
        {
            // Use base class statistics
            var stats = GetStatistics();

            message.Payload["EncryptionCount"] = stats.EncryptionCount;
            message.Payload["DecryptionCount"] = stats.DecryptionCount;
            message.Payload["TotalBytesEncrypted"] = stats.TotalBytesEncrypted;
            message.Payload["TotalBytesDecrypted"] = stats.TotalBytesDecrypted;
            message.Payload["UniqueKeysUsed"] = stats.UniqueKeysUsed;
            message.Payload["Algorithm"] = AlgorithmId;
            message.Payload["IVSizeBits"] = IvSizeBytes * 8;
            message.Payload["TagSizeBits"] = TagSizeBytes * 8;

            return Task.CompletedTask;
        }

        private Task HandleSetKeyStoreAsync(PluginMessage message)
        {
            if (message.Payload.TryGetValue("keyStore", out var ksObj) && ksObj is IKeyStore ks)
            {
                SetDefaultKeyStore(ks);
            }
            return Task.CompletedTask;
        }

        #endregion
    }

    /// <summary>
    /// Configuration for ChaCha20-Poly1305 encryption plugin.
    /// </summary>
    public sealed class ChaCha20EncryptionConfig
    {
        /// <summary>
        /// Gets or sets the key store to use for encryption keys.
        /// This is used as the default when not explicitly specified per operation.
        /// </summary>
        public IKeyStore? KeyStore { get; set; }
    }
}
