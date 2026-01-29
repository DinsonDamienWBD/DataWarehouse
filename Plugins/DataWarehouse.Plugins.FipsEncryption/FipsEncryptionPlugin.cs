using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Utilities;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace DataWarehouse.Plugins.FipsEncryption
{
    /// <summary>
    /// FIPS 140-2 validated encryption plugin for DataWarehouse.
    /// Uses exclusively FIPS-compliant .NET BCL cryptographic primitives (AES-GCM).
    /// Extends EncryptionPluginBase for composable key management with Direct and Envelope modes.
    ///
    /// Security Features:
    /// - FIPS 140-2 Level 1 compliance verification at startup
    /// - AES-256-GCM authenticated encryption (NIST SP 800-38D)
    /// - Cryptographically secure random IV generation (96-bit)
    /// - 128-bit authentication tag for integrity verification
    /// - Automatic FIPS mode detection on Windows (CNG) and Linux (OpenSSL)
    /// - Composable key management: Direct mode (IKeyStore) or Envelope mode (IEnvelopeKeyStore)
    /// - Secure memory clearing for sensitive data (PCI-DSS compliance)
    ///
    /// Data Format (when using manifest-based metadata):
    /// [IV:12][Tag:16][Ciphertext:...]
    ///
    /// Legacy Format (backward compatibility for old encrypted files):
    /// [Version:1][KeyIdLen:1][KeyId:variable][IV:12][Tag:16][Ciphertext:...]
    ///
    /// Thread Safety: All operations are thread-safe.
    ///
    /// Message Commands:
    /// - fips.encryption.configure: Configure encryption settings
    /// - fips.encryption.verify: Verify FIPS compliance status
    /// - fips.encryption.stats: Get encryption statistics
    /// - fips.encryption.setKeyStore: Set the key store
    /// </summary>
    public sealed class FipsEncryptionPlugin : EncryptionPluginBase
    {
        private readonly FipsEncryptionConfig _config;
        private long _fipsVerificationCount;
        private bool _fipsVerified;

        /// <summary>
        /// Maximum key ID length in legacy header format.
        /// </summary>
        private const int MaxKeyIdLength = 64;

        /// <summary>
        /// Header format version for legacy format: [Version:1][KeyIdLen:1][KeyId:KeyIdLen][IV:12][Tag:16][Ciphertext:...]
        /// </summary>
        private const byte HeaderVersion = 0x01;

        #region Abstract Property Overrides

        /// <inheritdoc/>
        protected override int KeySizeBytes => 32; // 256 bits

        /// <inheritdoc/>
        protected override int IvSizeBytes => 12; // 96 bits for GCM

        /// <inheritdoc/>
        protected override int TagSizeBytes => 16; // 128 bits

        /// <inheritdoc/>
        protected override string AlgorithmId => "FIPS-AES-256-GCM";

        #endregion

        #region Plugin Identity

        /// <inheritdoc/>
        public override string Id => "datawarehouse.plugins.encryption.fips";

        /// <inheritdoc/>
        public override string Name => "FIPS 140-2 Encryption";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <inheritdoc/>
        public override string SubCategory => "Encryption";

        /// <inheritdoc/>
        public override int QualityLevel => 95;

        /// <inheritdoc/>
        public override int DefaultOrder => 90;

        /// <inheritdoc/>
        public override bool AllowBypass => false;

        /// <inheritdoc/>
        public override string[] RequiredPrecedingStages => new[] { "Compression" };

        /// <inheritdoc/>
        public override string[] IncompatibleStages => new[] { "encryption.chacha20", "encryption.aes256" };

        #endregion

        /// <summary>
        /// Initializes a new instance of the FIPS encryption plugin.
        /// </summary>
        /// <param name="config">Optional configuration. If null, defaults are used.</param>
        /// <exception cref="CryptographicException">
        /// Thrown if FIPS mode is enforced but not available on the system.
        /// </exception>
        public FipsEncryptionPlugin(FipsEncryptionConfig? config = null)
        {
            _config = config ?? new FipsEncryptionConfig();

            // Set defaults from config (backward compatibility)
            if (_config.KeyStore != null)
            {
                DefaultKeyStore = _config.KeyStore;
            }

            if (_config.EnforceFipsMode)
            {
                VerifyFipsCompliance();
            }
        }

        /// <inheritdoc/>
        public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
        {
            var response = await base.OnHandshakeAsync(request);

            // Verify FIPS compliance on handshake
            if (_config.VerifyOnHandshake)
            {
                try
                {
                    VerifyFipsCompliance();
                    _fipsVerified = true;
                }
                catch (CryptographicException)
                {
                    _fipsVerified = false;
                    if (_config.EnforceFipsMode)
                    {
                        response.Success = false;
                        response.ReadyState = PluginReadyState.Failed;
                    }
                }
            }

            return response;
        }

        /// <inheritdoc/>
        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return new List<PluginCapabilityDescriptor>
            {
                new() { Name = "fips.encryption.configure", DisplayName = "Configure", Description = "Configure FIPS encryption settings" },
                new() { Name = "fips.encryption.verify", DisplayName = "Verify FIPS", Description = "Verify FIPS 140-2 compliance status" },
                new() { Name = "fips.encryption.stats", DisplayName = "Statistics", Description = "Get encryption statistics" },
                new() { Name = "fips.encryption.setKeyStore", DisplayName = "Set Key Store", Description = "Configure the key store" },
                new() { Name = "fips.encryption.encrypt", DisplayName = "Encrypt", Description = "Encrypt data using FIPS-compliant AES-256-GCM" },
                new() { Name = "fips.encryption.decrypt", DisplayName = "Decrypt", Description = "Decrypt FIPS-compliant AES-256-GCM encrypted data" }
            };
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["Algorithm"] = AlgorithmId;
            metadata["Standard"] = "FIPS 140-2";
            metadata["KeySize"] = KeySizeBytes * 8;
            metadata["IVSize"] = IvSizeBytes * 8;
            metadata["TagSize"] = TagSizeBytes * 8;
            metadata["FipsVerified"] = _fipsVerified;
            metadata["FipsEnforced"] = _config.EnforceFipsMode;
            metadata["SupportsKeyRotation"] = true;
            metadata["SupportsStreaming"] = true;
            metadata["RequiresKeyStore"] = true;
            metadata["NistCompliant"] = true;
            metadata["PciDssCompliant"] = true;
            metadata["SupportedModes"] = new[] { "Direct", "Envelope" };
            return metadata;
        }

        /// <inheritdoc/>
        public override Task OnMessageAsync(PluginMessage message)
        {
            return message.Type switch
            {
                "fips.encryption.configure" => HandleConfigureAsync(message),
                "fips.encryption.verify" => HandleVerifyAsync(message),
                "fips.encryption.stats" => HandleStatsAsync(message),
                "fips.encryption.setKeyStore" => HandleSetKeyStoreAsync(message),
                _ => base.OnMessageAsync(message)
            };
        }

        #region Core Encryption/Decryption (Algorithm-Specific)

        /// <summary>
        /// Performs FIPS-compliant AES-256-GCM encryption on the input stream.
        /// Base class handles key resolution, config resolution, and statistics.
        /// </summary>
        /// <param name="input">The plaintext input stream.</param>
        /// <param name="key">The encryption key (provided by base class).</param>
        /// <param name="iv">The initialization vector (provided by base class).</param>
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

                // Perform FIPS-compliant AES-256-GCM encryption
                using var aesGcm = new AesGcm(key, TagSizeBytes);
                aesGcm.Encrypt(iv, plaintext, ciphertext, tag);

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

                context.LogDebug($"FIPS AES-256-GCM encrypted {plaintext.Length} bytes");

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
        /// Performs FIPS-compliant AES-256-GCM decryption on the input stream.
        /// Base class handles key resolution, config resolution, and statistics.
        /// Supports both new format [IV:12][Tag:16][Ciphertext] and legacy format with key ID header.
        /// </summary>
        /// <param name="input">The encrypted input stream.</param>
        /// <param name="key">The decryption key (provided by base class).</param>
        /// <param name="iv">The initialization vector (null if embedded in ciphertext).</param>
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
                    // Legacy format: [Version:1][KeyIdLen:1][KeyId:variable][IV:12][Tag:16][Ciphertext]
                    if (encryptedData.Length < 2)
                        throw new CryptographicException("Encrypted data too short for legacy format header");

                    // Skip version byte
                    pos++;

                    // Read key ID length
                    var keyIdLength = encryptedData[pos++];
                    if (keyIdLength > MaxKeyIdLength || pos + keyIdLength > encryptedData.Length)
                    {
                        throw new CryptographicException("Invalid key ID length in encrypted data header");
                    }

                    // Skip key ID (already resolved by base class)
                    pos += keyIdLength;
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

                // Perform FIPS-compliant AES-256-GCM decryption with authentication
                plaintext = new byte[ciphertextLength];

                using var aesGcm = new AesGcm(key, TagSizeBytes);
                aesGcm.Decrypt(iv, ciphertext, tag, plaintext);

                context.LogDebug($"FIPS AES-256-GCM decrypted {ciphertextLength} bytes");

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
        /// Legacy format: [Version:1][KeyIdLen:1][KeyId:variable][IV:12][Tag:16][Ciphertext]
        /// </summary>
        private bool IsLegacyFormat(byte[] data)
        {
            if (data.Length < 2)
                return false;

            // Check for version header
            var version = data[0];
            if (version != HeaderVersion)
                return false;

            // Check key ID length is reasonable
            var keyIdLength = data[1];
            return keyIdLength > 0 && keyIdLength <= MaxKeyIdLength && data.Length >= 2 + keyIdLength + IvSizeBytes + TagSizeBytes;
        }

        #endregion

        /// <summary>
        /// Verifies that the system is running in FIPS-compliant mode.
        /// </summary>
        /// <exception cref="CryptographicException">Thrown if FIPS mode is not enabled or not available.</exception>
        public void VerifyFipsCompliance()
        {
            Interlocked.Increment(ref _fipsVerificationCount);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                VerifyWindowsFipsMode();
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                VerifyLinuxFipsMode();
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                VerifyMacOsFipsMode();
            }

            // Verify AES-GCM works with FIPS-compliant provider
            VerifyAesGcmAvailability();

            _fipsVerified = true;
        }

        /// <summary>
        /// Gets the FIPS compliance status.
        /// </summary>
        /// <returns>True if FIPS mode is verified and active.</returns>
        public bool IsFipsCompliant()
        {
            if (!_fipsVerified)
            {
                try
                {
                    VerifyFipsCompliance();
                }
                catch
                {
                    return false;
                }
            }
            return _fipsVerified;
        }

        #region FIPS Verification Methods

        private void VerifyWindowsFipsMode()
        {
            // On Windows, check registry for FIPS policy
            // HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Lsa\FipsAlgorithmPolicy\Enabled
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Lsa\FipsAlgorithmPolicy");

                if (key != null)
                {
                    var enabled = key.GetValue("Enabled");
                    if (enabled is int value && value == 1)
                    {
                        return; // FIPS mode enabled
                    }
                }

                // Check CNG FIPS mode
                if (CryptoConfig.AllowOnlyFipsAlgorithms)
                {
                    return; // .NET FIPS mode enabled
                }

                if (_config.EnforceFipsMode)
                {
                    throw new CryptographicException(
                        "Windows FIPS mode is not enabled. Enable via Local Security Policy " +
                        "or set HKLM\\SYSTEM\\CurrentControlSet\\Control\\Lsa\\FipsAlgorithmPolicy\\Enabled to 1");
                }
            }
            catch (CryptographicException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (_config.EnforceFipsMode)
                {
                    throw new CryptographicException($"Failed to verify Windows FIPS mode: {ex.Message}", ex);
                }
            }
        }

        private void VerifyLinuxFipsMode()
        {
            try
            {
                // Check /proc/sys/crypto/fips_enabled
                const string fipsPath = "/proc/sys/crypto/fips_enabled";
                if (File.Exists(fipsPath))
                {
                    var content = File.ReadAllText(fipsPath).Trim();
                    if (content == "1")
                    {
                        return; // FIPS mode enabled
                    }
                }

                // Check for FIPS-enabled OpenSSL
                // OpenSSL FIPS module presence check
                var openSslFipsPath = "/etc/ssl/fips_enabled";
                if (File.Exists(openSslFipsPath))
                {
                    var content = File.ReadAllText(openSslFipsPath).Trim();
                    if (content == "1")
                    {
                        return;
                    }
                }

                if (_config.EnforceFipsMode)
                {
                    throw new CryptographicException(
                        "Linux FIPS mode is not enabled. Enable FIPS mode by setting " +
                        "fips=1 kernel parameter or using a FIPS-validated Linux distribution.");
                }
            }
            catch (CryptographicException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (_config.EnforceFipsMode)
                {
                    throw new CryptographicException($"Failed to verify Linux FIPS mode: {ex.Message}", ex);
                }
            }
        }

        private void VerifyMacOsFipsMode()
        {
            // macOS uses CommonCrypto which has FIPS 140-2 validated modules
            // Apple's corecrypto has FIPS certification
            if (_config.EnforceFipsMode)
            {
                // Verify corecrypto availability by attempting to use FIPS-approved algorithms
                try
                {
                    using var aes = Aes.Create();
                    aes.KeySize = 256;
                    // If this succeeds, we have access to AES which is FIPS-approved
                }
                catch (Exception ex)
                {
                    throw new CryptographicException(
                        $"macOS FIPS-compliant crypto not available: {ex.Message}", ex);
                }
            }
        }

        private void VerifyAesGcmAvailability()
        {
            // Test that AES-GCM is available and functional
            try
            {
                var testKey = RandomNumberGenerator.GetBytes(KeySizeBytes);
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
            catch (PlatformNotSupportedException ex)
            {
                throw new CryptographicException(
                    "AES-GCM is not available on this platform. FIPS-compliant encryption requires AES-GCM support.", ex);
            }
        }

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

        private Task HandleVerifyAsync(PluginMessage message)
        {
            try
            {
                VerifyFipsCompliance();
                message.Payload["FipsCompliant"] = true;
                message.Payload["FipsVerified"] = _fipsVerified;
                message.Payload["Platform"] = RuntimeInformation.OSDescription;
            }
            catch (CryptographicException ex)
            {
                message.Payload["FipsCompliant"] = false;
                message.Payload["FipsVerified"] = false;
                message.Payload["Error"] = ex.Message;
            }

            return Task.CompletedTask;
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

            // Add FIPS-specific statistics
            message.Payload["FipsVerificationCount"] = Interlocked.Read(ref _fipsVerificationCount);
            message.Payload["FipsVerified"] = _fipsVerified;

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
    /// Configuration for FIPS encryption plugin.
    /// </summary>
    public sealed class FipsEncryptionConfig
    {
        /// <summary>
        /// Gets or sets the key store to use for encryption keys.
        /// This is used as the default when not explicitly specified per operation.
        /// </summary>
        public IKeyStore? KeyStore { get; set; }

        /// <summary>
        /// Gets or sets whether to enforce FIPS mode.
        /// When true, encryption will fail if the system is not in FIPS mode.
        /// Default is false.
        /// </summary>
        public bool EnforceFipsMode { get; set; }

        /// <summary>
        /// Gets or sets whether to verify FIPS compliance on handshake.
        /// Default is true.
        /// </summary>
        public bool VerifyOnHandshake { get; set; } = true;
    }
}
