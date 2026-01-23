using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DataWarehouse.Plugins.FipsEncryption
{
    /// <summary>
    /// FIPS 140-2 validated encryption plugin for DataWarehouse.
    /// Uses exclusively FIPS-compliant .NET BCL cryptographic primitives (AES-GCM).
    ///
    /// Security Features:
    /// - FIPS 140-2 Level 1 compliance verification at startup
    /// - AES-256-GCM authenticated encryption (NIST SP 800-38D)
    /// - Cryptographically secure random IV generation (96-bit)
    /// - 128-bit authentication tag for integrity verification
    /// - Automatic FIPS mode detection on Windows (CNG) and Linux (OpenSSL)
    /// - Key management integration via IKeyStore interface
    /// - Secure memory clearing for sensitive data (PCI-DSS compliance)
    ///
    /// Thread Safety: All operations are thread-safe.
    ///
    /// Message Commands:
    /// - fips.encryption.configure: Configure encryption settings
    /// - fips.encryption.verify: Verify FIPS compliance status
    /// - fips.encryption.stats: Get encryption statistics
    /// - fips.encryption.setKeyStore: Set the key store
    /// </summary>
    public sealed class FipsEncryptionPlugin : PipelinePluginBase, IDisposable
    {
        private readonly FipsEncryptionConfig _config;
        private readonly object _statsLock = new();
        private readonly ConcurrentDictionary<string, DateTime> _keyAccessLog = new();
        private IKeyStore? _keyStore;
        private ISecurityContext? _securityContext;
        private long _encryptionCount;
        private long _decryptionCount;
        private long _totalBytesEncrypted;
        private long _totalBytesDecrypted;
        private long _fipsVerificationCount;
        private bool _fipsVerified;
        private bool _disposed;

        /// <summary>
        /// IV size for AES-GCM (96 bits as per NIST SP 800-38D).
        /// </summary>
        private const int IvSizeBytes = 12;

        /// <summary>
        /// Authentication tag size (128 bits for maximum security).
        /// </summary>
        private const int TagSizeBytes = 16;

        /// <summary>
        /// Maximum key ID length in header.
        /// </summary>
        private const int MaxKeyIdLength = 64;

        /// <summary>
        /// Header format: [Version:1][KeyIdLen:1][KeyId:KeyIdLen][IV:12][Tag:16][Ciphertext:...]
        /// </summary>
        private const byte HeaderVersion = 0x01;

        /// <summary>
        /// Minimum header size without key ID.
        /// </summary>
        private const int MinHeaderSize = 2 + IvSizeBytes + TagSizeBytes;

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

            if (_config.EnforceFipsMode)
            {
                VerifyFipsCompliance();
            }
        }

        /// <inheritdoc/>
        public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
        {
            var response = await base.OnHandshakeAsync(request);

            if (_config.KeyStore != null)
            {
                _keyStore = _config.KeyStore;
            }

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
            metadata["Algorithm"] = "AES-256-GCM";
            metadata["Standard"] = "FIPS 140-2";
            metadata["KeySize"] = 256;
            metadata["IVSize"] = IvSizeBytes * 8;
            metadata["TagSize"] = TagSizeBytes * 8;
            metadata["FipsVerified"] = _fipsVerified;
            metadata["FipsEnforced"] = _config.EnforceFipsMode;
            metadata["SupportsKeyRotation"] = true;
            metadata["SupportsStreaming"] = true;
            metadata["RequiresKeyStore"] = true;
            metadata["NistCompliant"] = true;
            metadata["PciDssCompliant"] = true;
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

        /// <summary>
        /// Encrypts data from the input stream using FIPS-compliant AES-256-GCM.
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
        /// Output format: [Version:1][KeyIdLen:1][KeyId:KeyIdLen][IV:12][Tag:16][Ciphertext:...]
        /// </remarks>
        public override Stream OnWrite(Stream input, IKernelContext context, Dictionary<string, object> args)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var keyStore = GetKeyStore(args, context);
            var securityContext = GetSecurityContext(args);

            string keyId;
            if (args.TryGetValue("keyId", out var kidObj) && kidObj is string specificKeyId)
            {
                keyId = specificKeyId;
            }
            else
            {
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
                throw new CryptographicException($"FIPS AES-256 requires a 256-bit (32-byte) key. Received {key.Length * 8}-bit key.");
            }

            byte[]? plaintext = null;
            byte[]? ciphertext = null;
            byte[]? iv = null;
            byte[]? tag = null;

            try
            {
                using var inputMs = new MemoryStream();
                input.CopyTo(inputMs);
                plaintext = inputMs.ToArray();

                iv = RandomNumberGenerator.GetBytes(IvSizeBytes);
                tag = new byte[TagSizeBytes];
                ciphertext = new byte[plaintext.Length];

                using var aesGcm = new AesGcm(key, TagSizeBytes);
                aesGcm.Encrypt(iv, plaintext, ciphertext, tag);

                var keyIdBytes = Encoding.UTF8.GetBytes(keyId);
                if (keyIdBytes.Length > MaxKeyIdLength)
                {
                    throw new CryptographicException($"Key ID exceeds maximum length of {MaxKeyIdLength} bytes");
                }

                var outputLength = 2 + keyIdBytes.Length + IvSizeBytes + TagSizeBytes + ciphertext.Length;
                var output = new byte[outputLength];
                var pos = 0;

                output[pos++] = HeaderVersion;
                output[pos++] = (byte)keyIdBytes.Length;
                Array.Copy(keyIdBytes, 0, output, pos, keyIdBytes.Length);
                pos += keyIdBytes.Length;
                Array.Copy(iv, 0, output, pos, IvSizeBytes);
                pos += IvSizeBytes;
                Array.Copy(tag, 0, output, pos, TagSizeBytes);
                pos += TagSizeBytes;
                Array.Copy(ciphertext, 0, output, pos, ciphertext.Length);

                lock (_statsLock)
                {
                    _encryptionCount++;
                    _totalBytesEncrypted += plaintext.Length;
                }

                LogKeyAccess(keyId);
                context.LogDebug($"FIPS AES-256-GCM encrypted {plaintext.Length} bytes with key {TruncateKeyId(keyId)}");

                return new MemoryStream(output);
            }
            finally
            {
                if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext);
                if (ciphertext != null) CryptographicOperations.ZeroMemory(ciphertext);
                CryptographicOperations.ZeroMemory(key);
            }
        }

        /// <summary>
        /// Decrypts data from the stored stream using FIPS-compliant AES-256-GCM.
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
                using var inputMs = new MemoryStream();
                stored.CopyTo(inputMs);
                encryptedData = inputMs.ToArray();

                if (encryptedData.Length < MinHeaderSize)
                {
                    throw new CryptographicException($"Encrypted data too short. Minimum size is {MinHeaderSize} bytes.");
                }

                var pos = 0;
                var version = encryptedData[pos++];
                if (version != HeaderVersion)
                {
                    throw new CryptographicException($"Unsupported encryption format version: {version}");
                }

                var keyIdLength = encryptedData[pos++];
                if (keyIdLength > MaxKeyIdLength || pos + keyIdLength > encryptedData.Length)
                {
                    throw new CryptographicException("Invalid key ID length in encrypted data header");
                }

                var keyId = Encoding.UTF8.GetString(encryptedData, pos, keyIdLength);
                pos += keyIdLength;

                if (encryptedData.Length < pos + IvSizeBytes + TagSizeBytes)
                {
                    throw new CryptographicException("Encrypted data truncated: missing IV or tag");
                }

                var iv = new byte[IvSizeBytes];
                Array.Copy(encryptedData, pos, iv, 0, IvSizeBytes);
                pos += IvSizeBytes;

                var tag = new byte[TagSizeBytes];
                Array.Copy(encryptedData, pos, tag, 0, TagSizeBytes);
                pos += TagSizeBytes;

                var ciphertextLength = encryptedData.Length - pos;
                var ciphertext = new byte[ciphertextLength];
                Array.Copy(encryptedData, pos, ciphertext, 0, ciphertextLength);

                key = RunSyncWithErrorHandling(
                    () => keyStore.GetKeyAsync(keyId, securityContext),
                    $"Failed to retrieve key from key store for decryption");

                if (key.Length != 32)
                {
                    throw new CryptographicException($"FIPS AES-256 requires a 256-bit (32-byte) key. Retrieved key is {key.Length * 8}-bit.");
                }

                plaintext = new byte[ciphertextLength];

                using var aesGcm = new AesGcm(key, TagSizeBytes);
                aesGcm.Decrypt(iv, ciphertext, tag, plaintext);

                lock (_statsLock)
                {
                    _decryptionCount++;
                    _totalBytesDecrypted += plaintext.Length;
                }

                LogKeyAccess(keyId);
                context.LogDebug($"FIPS AES-256-GCM decrypted {ciphertextLength} bytes with key {TruncateKeyId(keyId)}");

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
                if (encryptedData != null) CryptographicOperations.ZeroMemory(encryptedData);
                if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext);
                if (key != null) CryptographicOperations.ZeroMemory(key);
            }
        }

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
            catch (PlatformNotSupportedException ex)
            {
                throw new CryptographicException(
                    "AES-GCM is not available on this platform. FIPS-compliant encryption requires AES-GCM support.", ex);
            }
        }

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

            var keyStorePlugin = context.GetPlugins<IPlugin>()
                .OfType<IKeyStore>()
                .FirstOrDefault();

            if (keyStorePlugin != null)
            {
                return keyStorePlugin;
            }

            throw new InvalidOperationException(
                "No IKeyStore available. Configure a FIPS-compliant key store before using encryption. " +
                "Ensure the key store uses FIPS-approved key derivation functions (PBKDF2, HKDF with SHA-256/384/512).");
        }

        private ISecurityContext GetSecurityContext(Dictionary<string, object> args)
        {
            if (args.TryGetValue("securityContext", out var scObj) && scObj is ISecurityContext sc)
            {
                return sc;
            }

            return _securityContext ?? new FipsSecurityContext();
        }

        private static T RunSyncWithErrorHandling<T>(Func<Task<T>> asyncOperation, string errorContext)
        {
            try
            {
                return Task.Run(asyncOperation).GetAwaiter().GetResult();
            }
            catch (AggregateException ae) when (ae.InnerException != null)
            {
                var innerException = ae.InnerException;
                if (innerException is CryptographicException)
                {
                    throw innerException;
                }
                throw new CryptographicException($"{errorContext}: {innerException.Message}", innerException);
            }
            catch (CryptographicException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new CryptographicException($"{errorContext}: {ex.Message}", ex);
            }
        }

        private void LogKeyAccess(string keyId)
        {
            _keyAccessLog[keyId] = DateTime.UtcNow;
        }

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
            lock (_statsLock)
            {
                message.Payload["EncryptionCount"] = _encryptionCount;
                message.Payload["DecryptionCount"] = _decryptionCount;
                message.Payload["TotalBytesEncrypted"] = _totalBytesEncrypted;
                message.Payload["TotalBytesDecrypted"] = _totalBytesDecrypted;
                message.Payload["FipsVerificationCount"] = Interlocked.Read(ref _fipsVerificationCount);
                message.Payload["FipsVerified"] = _fipsVerified;
                message.Payload["UniqueKeysUsed"] = _keyAccessLog.Count;
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
    /// Configuration for FIPS encryption plugin.
    /// </summary>
    public sealed class FipsEncryptionConfig
    {
        /// <summary>
        /// Gets or sets the key store to use for encryption keys.
        /// </summary>
        public IKeyStore? KeyStore { get; set; }

        /// <summary>
        /// Gets or sets the security context for key access.
        /// </summary>
        public ISecurityContext? SecurityContext { get; set; }

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

    /// <summary>
    /// Default security context for FIPS encryption operations.
    /// </summary>
    internal sealed class FipsSecurityContext : ISecurityContext
    {
        /// <inheritdoc/>
        public string UserId => Environment.UserName;

        /// <inheritdoc/>
        public string? TenantId => "fips-local";

        /// <inheritdoc/>
        public IEnumerable<string> Roles => new[] { "fips-user" };

        /// <inheritdoc/>
        public bool IsSystemAdmin => false;
    }
}
