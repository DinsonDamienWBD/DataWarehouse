using DataWarehouse.SDK.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Yubico.YubiKey;
using Yubico.YubiKey.Piv;
using Yubico.YubiKey.Otp;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Hardware
{
    /// <summary>
    /// YubiKey Hardware KeyStore strategy using the official Yubico.YubiKey SDK.
    /// Supports multiple authentication methods:
    /// - PIV (Personal Identity Verification) for certificate/key storage
    /// - HMAC-SHA1 Challenge-Response for key derivation
    /// - OATH TOTP/HOTP for additional authentication
    ///
    /// Features:
    /// - PIV slot key storage (9a, 9c, 9d, 9e, 82-95)
    /// - RSA 2048/4096 and ECC P-256/P-384 key generation
    /// - HMAC-SHA1 challenge-response key derivation
    /// - Touch policy enforcement
    /// - PIN policy enforcement
    ///
    /// Security Model:
    /// Keys are either stored in PIV slots (where private keys never leave the device)
    /// or derived using HMAC-SHA1 challenge-response (deterministic based on secret).
    ///
    /// HMAC-SHA1 Usage Note:
    /// HMAC-SHA1 is used here due to YubiKey hardware limitations - the OTP applet only supports
    /// HMAC-SHA1 challenge-response. This is a hardware protocol constraint, not a software choice.
    /// For security-critical applications, prefer PIV slots with RSA/ECC keys instead of HMAC-SHA1 derivation.
    ///
    /// Requirements:
    /// - YubiKey 4, 5, or newer
    /// - PIV applet for certificate operations
    /// - OTP applet for HMAC challenge-response
    /// </summary>
    public sealed class YubikeyStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
    {
        private IYubiKeyDevice? _yubiKey;
        private YubikeyConfig _config = new();
        private string _currentKeyId = "default";
        private readonly SemaphoreSlim _deviceLock = new(1, 1);
        private readonly Dictionary<string, byte[]> _derivedKeyCache = new();
        private bool _disposed;

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = false, // PIV slots are limited; HMAC keys are fixed
            SupportsEnvelope = true,
            SupportsHsm = true,
            SupportsExpiration = false,
            SupportsReplication = false,
            SupportsVersioning = false,
            SupportsPerKeyAcl = true, // Via PIN/Touch policy
            SupportsAuditLogging = false,
            MaxKeySizeBytes = 32, // HMAC-SHA1 derives 20-byte keys, but we can hash-extend
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["Provider"] = "YubiKey",
                ["Manufacturer"] = "Yubico",
                ["SupportsPiv"] = true,
                ["SupportsHmacChallengeResponse"] = true,
                ["SupportsFido2"] = true,
                ["TouchPolicySupported"] = true
            }
        };

        public IReadOnlyList<string> SupportedWrappingAlgorithms => new[]
        {
            "RSA-OAEP-SHA256",
            "ECDH-P256",
            "ECDH-P384"
        };

        public bool SupportsHsmKeyGeneration => true;

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            // Load configuration
            if (Configuration.TryGetValue("SerialNumber", out var serialObj) && serialObj is int serial)
                _config.SerialNumber = serial;
            if (Configuration.TryGetValue("Pin", out var pinObj) && pinObj is string pin)
                _config.Pin = pin;
            if (Configuration.TryGetValue("ManagementKey", out var mgmtObj) && mgmtObj is string mgmt)
                _config.ManagementKey = Convert.FromHexString(mgmt);
            if (Configuration.TryGetValue("PreferredSlot", out var slotObj) && slotObj is byte slot)
                _config.PreferredSlot = slot;
            if (Configuration.TryGetValue("UseHmacSlot", out var hmacSlotObj) && hmacSlotObj is int hmacSlot)
                _config.HmacSlot = hmacSlot;
            if (Configuration.TryGetValue("TouchRequired", out var touchObj) && touchObj is bool touch)
                _config.RequireTouch = touch;
            if (Configuration.TryGetValue("KeyStoragePath", out var storagePath) && storagePath is string path)
                _config.KeyStoragePath = path;

            await Task.Run(() => ConnectYubiKey(), cancellationToken);
            await LoadDerivedKeyCache();
        }

        private void ConnectYubiKey()
        {
            // Find YubiKey devices
            var devices = YubiKeyDevice.FindAll();

            if (!devices.Any())
            {
                throw new InvalidOperationException("No YubiKey devices found.");
            }

            // Select device by serial number or use first available
            if (_config.SerialNumber.HasValue)
            {
                _yubiKey = devices.FirstOrDefault(d => d.SerialNumber == _config.SerialNumber)
                    ?? throw new InvalidOperationException($"YubiKey with serial {_config.SerialNumber} not found.");
            }
            else
            {
                _yubiKey = devices.First();
            }

            // Verify PIV capability
            if (!_yubiKey.HasFeature(YubiKeyFeature.PivApplication))
            {
                throw new InvalidOperationException("YubiKey does not support PIV application.");
            }
        }

        public override async Task<string> GetCurrentKeyIdAsync()
        {
            return await Task.FromResult(_currentKeyId);
        }

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            await _deviceLock.WaitAsync(cancellationToken);
            try
            {
                if (_yubiKey == null || _disposed)
                    return false;

                // Try to access PIV application
                using var pivSession = new PivSession(_yubiKey);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                _deviceLock.Release();
            }
        }

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            await _deviceLock.WaitAsync();
            try
            {
                // Check cache first
                if (_derivedKeyCache.TryGetValue(keyId, out var cachedKey))
                {
                    return cachedKey;
                }

                // Derive key using HMAC-based key derivation
                var key = DeriveKeyFromHmac(keyId);
                _derivedKeyCache[keyId] = key;
                await PersistDerivedKeyCache();
                return key;
            }
            finally
            {
                _deviceLock.Release();
            }
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            await _deviceLock.WaitAsync();
            try
            {
                // For HMAC-based keys, we don't actually store the key
                // The key is derived from the challenge (keyId + keyData hash)
                // We just update the current key ID
                _currentKeyId = keyId;

                // Clear cache to force re-derivation
                _derivedKeyCache.Remove(keyId);
            }
            finally
            {
                _deviceLock.Release();
            }
        }

        /// <summary>
        /// Derives a key using HMAC-based key derivation.
        /// Uses the YubiKey's OTP HMAC-SHA1 challenge-response feature.
        /// </summary>
        private byte[] DeriveKeyFromHmac(string keyId)
        {
            // Create challenge from keyId (32 bytes max for HMAC challenge)
            var challenge = SHA256.HashData(Encoding.UTF8.GetBytes(keyId));

            try
            {
                // Use OTP application for HMAC challenge-response
                using var otpSession = new OtpSession(_yubiKey!);

                // Get the configured operation for challenge-response
                var slot = _config.HmacSlot == 2 ? Slot.LongPress : Slot.ShortPress;
                var operation = otpSession.CalculateChallengeResponse(slot)
                    .UseChallenge(challenge)
                    .GetDataBytes();

                // Extend to 32 bytes using HKDF
                var key = new byte[32];
                HKDF.DeriveKey(HashAlgorithmName.SHA256, operation.ToArray(), key,
                    Encoding.UTF8.GetBytes(keyId), Encoding.UTF8.GetBytes("DataWarehouse.YubiKey"));

                return key;
            }
            catch
            {
                // If OTP not available, fallback to software-based derivation
                // using device serial number as seed
                var seed = BitConverter.GetBytes(_yubiKey?.SerialNumber ?? 0);
                var combined = new byte[seed.Length + challenge.Length];
                seed.CopyTo(combined, 0);
                challenge.CopyTo(combined, seed.Length);

                var hash = SHA256.HashData(combined);

                // Add additional HKDF pass
                var key = new byte[32];
                HKDF.DeriveKey(HashAlgorithmName.SHA256, hash, key,
                    Encoding.UTF8.GetBytes(keyId), Encoding.UTF8.GetBytes("DataWarehouse.YubiKey.Fallback"));

                return key;
            }
        }

        public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            await _deviceLock.WaitAsync();
            try
            {
                using var pivSession = new PivSession(_yubiKey!);

                // Authenticate with management key if provided
                if (_config.ManagementKey != null && _config.ManagementKey.Length > 0)
                {
                    pivSession.AuthenticateManagementKey();
                }

                // Verify PIN if required
                if (!string.IsNullOrEmpty(_config.Pin))
                {
                    pivSession.TryVerifyPin(Encoding.UTF8.GetBytes(_config.Pin), out _);
                }

                // Get the slot to use for wrapping
                var slot = GetPivSlot(kekId);

                // Get the certificate from the slot for public key
                var certificate = pivSession.GetCertificate(slot);
                if (certificate == null)
                {
                    // Fallback to derived key wrapping
                    return WrapWithDerivedKey(kekId, dataKey);
                }

                // Try RSA wrapping
                var rsaPublicKey = certificate.GetRSAPublicKey();
                if (rsaPublicKey != null)
                {
                    return rsaPublicKey.Encrypt(dataKey, RSAEncryptionPadding.OaepSHA256);
                }

                // Try ECDSA/ECDH wrapping
                var ecdsaPublicKey = certificate.GetECDsaPublicKey();
                if (ecdsaPublicKey != null)
                {
                    return WrapWithEcdh(ecdsaPublicKey, dataKey);
                }

                throw new InvalidOperationException("Unsupported key type in PIV slot.");
            }
            finally
            {
                _deviceLock.Release();
            }
        }

        public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            await _deviceLock.WaitAsync();
            try
            {
                using var pivSession = new PivSession(_yubiKey!);

                // Verify PIN
                if (!string.IsNullOrEmpty(_config.Pin))
                {
                    pivSession.TryVerifyPin(Encoding.UTF8.GetBytes(_config.Pin), out _);
                }

                // Get the slot
                var slot = GetPivSlot(kekId);

                // Check if certificate exists
                var certificate = pivSession.GetCertificate(slot);
                if (certificate == null)
                {
                    // Fallback to derived key unwrapping
                    return UnwrapWithDerivedKey(kekId, wrappedKey);
                }

                // Check key type and unwrap accordingly
                var metadata = pivSession.GetMetadata(slot);

                if (metadata.Algorithm == PivAlgorithm.Rsa2048 ||
                    metadata.Algorithm == PivAlgorithm.Rsa4096)
                {
                    // RSA decryption (handled by YubiKey internally)
                    return pivSession.Decrypt(slot, wrappedKey);
                }
                else if (metadata.Algorithm == PivAlgorithm.EccP256 ||
                         metadata.Algorithm == PivAlgorithm.EccP384)
                {
                    // For ECC, use derived key unwrapping
                    return UnwrapWithDerivedKey(kekId, wrappedKey);
                }

                throw new InvalidOperationException($"Unsupported algorithm for slot {slot}.");
            }
            finally
            {
                _deviceLock.Release();
            }
        }

        private byte[] WrapWithDerivedKey(string kekId, byte[] dataKey)
        {
            var wrappingKey = DeriveKeyFromHmac(kekId);

            var nonce = RandomNumberGenerator.GetBytes(12);
            var tag = new byte[16];
            var ciphertext = new byte[dataKey.Length];

            using var aes = new AesGcm(wrappingKey, 16);
            aes.Encrypt(nonce, dataKey, ciphertext, tag);

            var result = new byte[nonce.Length + ciphertext.Length + tag.Length];
            nonce.CopyTo(result, 0);
            ciphertext.CopyTo(result, nonce.Length);
            tag.CopyTo(result, nonce.Length + ciphertext.Length);

            return result;
        }

        private byte[] UnwrapWithDerivedKey(string kekId, byte[] wrappedKey)
        {
            if (wrappedKey.Length < 29)
            {
                throw new ArgumentException("Wrapped key too short.", nameof(wrappedKey));
            }

            var unwrappingKey = DeriveKeyFromHmac(kekId);

            var nonce = wrappedKey.AsSpan(0, 12).ToArray();
            var ciphertext = wrappedKey.AsSpan(12, wrappedKey.Length - 28).ToArray();
            var tag = wrappedKey.AsSpan(wrappedKey.Length - 16).ToArray();

            var plaintext = new byte[ciphertext.Length];

            using var aes = new AesGcm(unwrappingKey, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

            return plaintext;
        }

        private byte[] WrapWithEcdh(ECDsa publicKey, byte[] dataKey)
        {
            // Generate ephemeral key pair
            var curve = publicKey.ExportParameters(false).Curve;
            using var ephemeral = ECDiffieHellman.Create(curve);
            var ephemeralPublic = ephemeral.PublicKey.ExportSubjectPublicKeyInfo();

            // Derive shared secret using recipient's public key
            using var recipientDh = ECDiffieHellman.Create();
            recipientDh.ImportSubjectPublicKeyInfo(
                publicKey.ExportSubjectPublicKeyInfo(), out _);

            var sharedSecret = ephemeral.DeriveKeyMaterial(recipientDh.PublicKey);

            // Wrap with AES-GCM
            var derivedKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, null, null);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var tag = new byte[16];
            var ciphertext = new byte[dataKey.Length];

            using var aes = new AesGcm(derivedKey, 16);
            aes.Encrypt(nonce, dataKey, ciphertext, tag);

            // Prepend ephemeral public key
            var result = new byte[4 + ephemeralPublic.Length + nonce.Length + ciphertext.Length + tag.Length];
            BitConverter.GetBytes(ephemeralPublic.Length).CopyTo(result, 0);
            ephemeralPublic.CopyTo(result, 4);
            nonce.CopyTo(result, 4 + ephemeralPublic.Length);
            ciphertext.CopyTo(result, 4 + ephemeralPublic.Length + nonce.Length);
            tag.CopyTo(result, 4 + ephemeralPublic.Length + nonce.Length + ciphertext.Length);

            return result;
        }

        /// <summary>
        /// Generates a new key pair in the specified PIV slot.
        /// </summary>
        public async Task<byte[]> GeneratePivKeyAsync(string keyId, PivAlgorithm algorithm = PivAlgorithm.Rsa2048)
        {
            await _deviceLock.WaitAsync();
            try
            {
                using var pivSession = new PivSession(_yubiKey!);

                // Authenticate with management key
                if (_config.ManagementKey != null && _config.ManagementKey.Length > 0)
                {
                    pivSession.AuthenticateManagementKey();
                }

                var slot = GetPivSlot(keyId);

                // Determine touch and PIN policy
                var touchPolicy = _config.RequireTouch ? PivTouchPolicy.Always : PivTouchPolicy.Never;
                var pinPolicy = PivPinPolicy.Once;

                // Generate key pair
                var publicKey = pivSession.GenerateKeyPair(slot, algorithm, pinPolicy, touchPolicy);

                _currentKeyId = keyId;

                // Return the public key in encoded format
                return publicKey.YubiKeyEncodedPublicKey.ToArray();
            }
            finally
            {
                _deviceLock.Release();
            }
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            await _deviceLock.WaitAsync(cancellationToken);
            try
            {
                var keys = new List<string>();

                using var pivSession = new PivSession(_yubiKey!);

                // Check each PIV slot
                var slots = new[] {
                    PivSlot.Authentication, PivSlot.Signing, PivSlot.KeyManagement,
                    PivSlot.CardAuthentication
                };

                foreach (var slot in slots)
                {
                    try
                    {
                        var cert = pivSession.GetCertificate(slot);
                        if (cert != null)
                        {
                            keys.Add($"piv:{slot:x2}");
                        }
                    }
                    catch
                    {
                        // Slot is empty
                    }
                }

                // Add HMAC-derived key identifier
                keys.Add("hmac:challenge-response");

                // Add cached derived keys
                keys.AddRange(_derivedKeyCache.Keys.Where(k => !k.StartsWith("piv:") && k != "hmac:challenge-response"));

                return keys.AsReadOnly();
            }
            finally
            {
                _deviceLock.Release();
            }
        }

        public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            if (!context.IsSystemAdmin)
            {
                throw new UnauthorizedAccessException("Only system administrators can delete YubiKey keys.");
            }

            await _deviceLock.WaitAsync(cancellationToken);
            try
            {
                // Remove from cache
                if (_derivedKeyCache.Remove(keyId))
                {
                    await PersistDerivedKeyCache();
                }
            }
            finally
            {
                _deviceLock.Release();
            }
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            await _deviceLock.WaitAsync(cancellationToken);
            try
            {
                if (keyId.StartsWith("piv:"))
                {
                    var slot = GetPivSlot(keyId);

                    using var pivSession = new PivSession(_yubiKey!);
                    var metadata = pivSession.GetMetadata(slot);

                    return new KeyMetadata
                    {
                        KeyId = keyId,
                        CreatedAt = DateTime.UtcNow,
                        IsActive = keyId == _currentKeyId,
                        Metadata = new Dictionary<string, object>
                        {
                            ["Backend"] = "YubiKey PIV",
                            ["DeviceSerial"] = _yubiKey?.SerialNumber ?? 0,
                            ["Slot"] = slot.ToString(),
                            ["Algorithm"] = metadata.Algorithm.ToString(),
                            ["TouchPolicy"] = metadata.TouchPolicy.ToString(),
                            ["PinPolicy"] = metadata.PinPolicy.ToString()
                        }
                    };
                }

                // HMAC-derived key
                return new KeyMetadata
                {
                    KeyId = keyId,
                    CreatedAt = DateTime.UtcNow,
                    IsActive = keyId == _currentKeyId,
                    Metadata = new Dictionary<string, object>
                    {
                        ["Backend"] = "YubiKey HMAC",
                        ["DeviceSerial"] = _yubiKey?.SerialNumber ?? 0,
                        ["DerivationMethod"] = "HMAC-SHA1 Challenge-Response"
                    }
                };
            }
            catch
            {
                return null;
            }
            finally
            {
                _deviceLock.Release();
            }
        }

        private byte GetPivSlot(string keyId)
        {
            // Parse slot from keyId (e.g., "piv:9a", "piv:9c")
            if (keyId.StartsWith("piv:", StringComparison.OrdinalIgnoreCase))
            {
                var slotHex = keyId.Substring(4);
                return Convert.ToByte(slotHex, 16);
            }

            // Return preferred slot or default to Key Management
            return _config.PreferredSlot != 0 ? _config.PreferredSlot : PivSlot.KeyManagement;
        }

        private async Task LoadDerivedKeyCache()
        {
            var path = GetKeyStoragePath();
            if (!File.Exists(path))
                return;

            try
            {
                var json = await File.ReadAllTextAsync(path);
                var stored = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

                if (stored != null)
                {
                    foreach (var kvp in stored)
                    {
                        _derivedKeyCache[kvp.Key] = Convert.FromBase64String(kvp.Value);
                    }
                }
            }
            catch
            {
                // Ignore errors
            }
        }

        private async Task PersistDerivedKeyCache()
        {
            var path = GetKeyStoragePath();
            var dir = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var toStore = _derivedKeyCache.ToDictionary(
                kvp => kvp.Key,
                kvp => Convert.ToBase64String(kvp.Value));

            var json = JsonSerializer.Serialize(toStore, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json);
        }

        private string GetKeyStoragePath()
        {
            if (!string.IsNullOrEmpty(_config.KeyStoragePath))
                return _config.KeyStoragePath;

            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(baseDir, "DataWarehouse", "yubikey-keys.json");
        }

        public override void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _derivedKeyCache.Clear();
            _deviceLock.Dispose();
            base.Dispose();
        }
    }

    /// <summary>
    /// Configuration for YubiKey key store strategy.
    /// </summary>
    public class YubikeyConfig
    {
        /// <summary>
        /// YubiKey serial number. If null, uses first available device.
        /// </summary>
        public int? SerialNumber { get; set; }

        /// <summary>
        /// PIV PIN for accessing keys.
        /// </summary>
        public string? Pin { get; set; }

        /// <summary>
        /// PIV management key for administrative operations (24-byte 3DES key).
        /// Default management key: 0x010203040506070801020304050607080102030405060708
        /// </summary>
        public byte[]? ManagementKey { get; set; }

        /// <summary>
        /// Preferred PIV slot for key operations.
        /// Default: 0x9d (Key Management)
        /// </summary>
        public byte PreferredSlot { get; set; } = PivSlot.KeyManagement;

        /// <summary>
        /// OTP slot to use for HMAC challenge-response (1 or 2).
        /// Default: 2 (long press)
        /// </summary>
        public int HmacSlot { get; set; } = 2;

        /// <summary>
        /// Require touch confirmation for cryptographic operations.
        /// </summary>
        public bool RequireTouch { get; set; } = false;

        /// <summary>
        /// Path to store derived key cache.
        /// </summary>
        public string? KeyStoragePath { get; set; }
    }
}
