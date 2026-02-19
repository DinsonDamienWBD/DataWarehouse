using DataWarehouse.SDK.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.PasswordDerived
{
    /// <summary>
    /// Password-derived KeyStore strategy using PBKDF2-HMAC-SHA256.
    /// NIST SP 800-132 recommended password-based key derivation function.
    ///
    /// PBKDF2 features:
    /// - Widely supported and FIPS 140-2 compliant
    /// - Built into .NET (Rfc2898DeriveBytes)
    /// - Configurable iteration count
    /// - Compatible with hardware acceleration
    ///
    /// Security notes:
    /// - PBKDF2 is NOT memory-hard (vulnerable to GPU/ASIC attacks)
    /// - Requires high iteration counts for security (600,000+ recommended for SHA-256)
    /// - Best for environments requiring FIPS compliance or hardware acceleration
    /// - Consider Argon2id or scrypt for new deployments where FIPS isn't required
    ///
    /// OWASP 2024 recommendations:
    /// - PBKDF2-HMAC-SHA256: 600,000 iterations minimum
    /// - PBKDF2-HMAC-SHA512: 210,000 iterations minimum
    ///
    /// Uses built-in System.Security.Cryptography.Rfc2898DeriveBytes.
    /// </summary>
    public sealed class PasswordDerivedPbkdf2Strategy : KeyStoreStrategyBase
    {
        private Pbkdf2Config _config = new();
        private string _currentKeyId = "default";
        private readonly Dictionary<string, Pbkdf2EncryptedKeyData> _storedKeys = new();
        private string? _masterPassword;
        private readonly SemaphoreSlim _storageLock = new(1, 1);
        private bool _disposed;

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = true,
            SupportsEnvelope = false,
            SupportsHsm = false,
            SupportsExpiration = false,
            SupportsReplication = false,
            SupportsVersioning = true,
            SupportsPerKeyAcl = false,
            SupportsAuditLogging = true,
            MaxKeySizeBytes = 0, // Unlimited
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["Algorithm"] = "PBKDF2-HMAC-SHA256",
                ["Standard"] = "NIST SP 800-132, RFC 8018",
                ["FipsCompliant"] = true,
                ["MemoryHard"] = false,
                ["HardwareAccelerated"] = true,
                ["BuiltIn"] = true
            }
        };

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("passwordderivedpbkdf2.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }


        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("passwordderivedpbkdf2.init");
            // Load configuration
            if (Configuration.TryGetValue("Password", out var pwdObj) && pwdObj is string pwd)
                _masterPassword = pwd;
            if (Configuration.TryGetValue("PasswordEnvVar", out var envObj) && envObj is string envVar)
            {
                var envPwd = Environment.GetEnvironmentVariable(envVar);
                if (!string.IsNullOrEmpty(envPwd))
                    _masterPassword = envPwd;
            }
            if (Configuration.TryGetValue("Iterations", out var iterObj) && iterObj is int iter)
                _config.Iterations = iter;
            if (Configuration.TryGetValue("HashAlgorithm", out var hashObj) && hashObj is string hash)
            {
                _config.HashAlgorithmName = hash.ToUpperInvariant() switch
                {
                    "SHA256" or "SHA-256" => HashAlgorithmName.SHA256,
                    "SHA384" or "SHA-384" => HashAlgorithmName.SHA384,
                    "SHA512" or "SHA-512" => HashAlgorithmName.SHA512,
                    "SHA1" or "SHA-1" => HandleDeprecatedSHA1(),
                    _ => HashAlgorithmName.SHA256
                };
            }
            if (Configuration.TryGetValue("KeySizeBytes", out var keySizeObj) && keySizeObj is int keySize)
                _config.KeySizeBytes = keySize;
            if (Configuration.TryGetValue("StoragePath", out var pathObj) && pathObj is string path)
                _config.StoragePath = path;
            if (Configuration.TryGetValue("SaltSizeBytes", out var saltObj) && saltObj is int saltSize)
                _config.SaltSizeBytes = saltSize;

            // Warn if iterations too low
            if (_config.Iterations < 600000 && _config.HashAlgorithmName == HashAlgorithmName.SHA256)
            {
                System.Diagnostics.Trace.TraceWarning(
                    $"PBKDF2-SHA256 iteration count ({_config.Iterations}) is below OWASP recommendation (600000). " +
                    "Consider increasing iterations or using Argon2id.");
            }

            // Ensure storage directory exists
            var dir = Path.GetDirectoryName(GetStoragePath());
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            // Load existing keys
            await LoadStoredKeysAsync(cancellationToken);
        }

        public override Task<string> GetCurrentKeyIdAsync()
        {
            return Task.FromResult(_currentKeyId);
        }

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(_masterPassword))
                return false;

            try
            {
                var testSalt = RandomNumberGenerator.GetBytes(16);
                var _ = DeriveKey("test", testSalt, 32);
                return await Task.FromResult(true);
            }
            catch
            {
                return false;
            }
        }

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            await _storageLock.WaitAsync();
            try
            {
                if (!_storedKeys.TryGetValue(keyId, out var encryptedData))
                {
                    throw new KeyNotFoundException($"Key '{keyId}' not found in PBKDF2 key store.");
                }

                // Parse the hash algorithm
                var hashAlg = encryptedData.HashAlgorithm switch
                {
                    "SHA256" => HashAlgorithmName.SHA256,
                    "SHA384" => HashAlgorithmName.SHA384,
                    "SHA512" => HashAlgorithmName.SHA512,
                    "SHA1" => HashAlgorithmName.SHA1,
                    _ => HashAlgorithmName.SHA256
                };

                // Derive the wrapping key using stored salt and parameters
                var wrappingKey = DeriveKey(
                    GetPassword(),
                    encryptedData.Salt,
                    32,
                    encryptedData.Iterations,
                    hashAlg);

                // Decrypt the stored key material
                return DecryptKeyMaterial(encryptedData.EncryptedKey, wrappingKey, encryptedData.Nonce, encryptedData.Tag);
            }
            finally
            {
                _storageLock.Release();
            }
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            await _storageLock.WaitAsync();
            try
            {
                // Generate unique salt for this key
                var salt = RandomNumberGenerator.GetBytes(_config.SaltSizeBytes);

                // Derive wrapping key
                var wrappingKey = DeriveKey(GetPassword(), salt, 32);

                // Encrypt the key material
                var (encryptedKey, nonce, tag) = EncryptKeyMaterial(keyData, wrappingKey);

                // Store the encrypted data with parameters
                var encryptedData = new Pbkdf2EncryptedKeyData
                {
                    KeyId = keyId,
                    Salt = salt,
                    EncryptedKey = encryptedKey,
                    Nonce = nonce,
                    Tag = tag,
                    Iterations = _config.Iterations,
                    HashAlgorithm = _config.HashAlgorithmName.Name ?? "SHA256",
                    CreatedAt = DateTime.UtcNow,
                    Version = (_storedKeys.TryGetValue(keyId, out var existing) ? existing.Version : 0) + 1
                };

                _storedKeys[keyId] = encryptedData;
                _currentKeyId = keyId;

                // Persist to storage
                await PersistStoredKeysAsync();
            }
            finally
            {
                _storageLock.Release();
            }
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);
            await Task.CompletedTask;
            return _storedKeys.Keys.ToList().AsReadOnly();
        }

        public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            if (!context.IsSystemAdmin)
            {
                throw new UnauthorizedAccessException("Only system administrators can delete keys.");
            }

            await _storageLock.WaitAsync(cancellationToken);
            try
            {
                if (_storedKeys.Remove(keyId))
                {
                    if (_currentKeyId == keyId && _storedKeys.Count > 0)
                    {
                        _currentKeyId = _storedKeys.Keys.First();
                    }
                    await PersistStoredKeysAsync();
                }
            }
            finally
            {
                _storageLock.Release();
            }
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            if (!_storedKeys.TryGetValue(keyId, out var data))
                return null;

            return await Task.FromResult(new KeyMetadata
            {
                KeyId = keyId,
                CreatedAt = data.CreatedAt,
                Version = data.Version,
                IsActive = keyId == _currentKeyId,
                KeySizeBytes = _config.KeySizeBytes,
                Metadata = new Dictionary<string, object>
                {
                    ["Algorithm"] = $"PBKDF2-HMAC-{data.HashAlgorithm}",
                    ["Iterations"] = data.Iterations,
                    ["SaltSize"] = data.Salt.Length,
                    ["FipsCompliant"] = true
                }
            });
        }

        /// <summary>
        /// Derives a key using PBKDF2 with the specified parameters.
        /// Uses the built-in Rfc2898DeriveBytes implementation.
        /// </summary>
        private byte[] DeriveKey(
            string password,
            byte[] salt,
            int outputLength,
            int? iterations = null,
            HashAlgorithmName? hashAlgorithm = null)
        {
            // Use the static Rfc2898DeriveBytes.Pbkdf2 method (recommended in .NET 6+)
            return Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                iterations ?? _config.Iterations,
                hashAlgorithm ?? _config.HashAlgorithmName,
                outputLength);
        }

        /// <summary>
        /// Encrypts key material using AES-GCM with the derived wrapping key.
        /// </summary>
        private static (byte[] ciphertext, byte[] nonce, byte[] tag) EncryptKeyMaterial(byte[] plaintext, byte[] key)
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var tag = new byte[16];
            var ciphertext = new byte[plaintext.Length];

            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plaintext, ciphertext, tag);

            return (ciphertext, nonce, tag);
        }

        /// <summary>
        /// Decrypts key material using AES-GCM with the derived wrapping key.
        /// </summary>
        private static byte[] DecryptKeyMaterial(byte[] ciphertext, byte[] key, byte[] nonce, byte[] tag)
        {
            var plaintext = new byte[ciphertext.Length];

            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

            return plaintext;
        }

        private string GetPassword()
        {
            if (string.IsNullOrEmpty(_masterPassword))
            {
                throw new InvalidOperationException(
                    "Master password not configured. Set 'Password' or 'PasswordEnvVar' in configuration.");
            }
            return _masterPassword;
        }

        private string GetStoragePath()
        {
            if (!string.IsNullOrEmpty(_config.StoragePath))
                return _config.StoragePath;

            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(baseDir, "DataWarehouse", "pbkdf2-keys.json");
        }

        private async Task LoadStoredKeysAsync(CancellationToken cancellationToken)
        {
            var storagePath = GetStoragePath();
            if (!File.Exists(storagePath))
                return;

            try
            {
                var json = await File.ReadAllTextAsync(storagePath, cancellationToken);
                var stored = JsonSerializer.Deserialize<Pbkdf2StoredKeyFile>(json);

                if (stored?.Keys != null)
                {
                    foreach (var key in stored.Keys)
                    {
                        _storedKeys[key.KeyId] = key;
                    }
                    _currentKeyId = stored.CurrentKeyId ?? _currentKeyId;
                }
            }
            catch
            {
                // Ignore load errors - start fresh
            }
        }

        private async Task PersistStoredKeysAsync()
        {
            var storagePath = GetStoragePath();
            var file = new Pbkdf2StoredKeyFile
            {
                CurrentKeyId = _currentKeyId,
                Keys = _storedKeys.Values.ToList()
            };

            var json = JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(storagePath, json);
        }

        /// <summary>
        /// Handles deprecated SHA1 configuration with warning.
        /// </summary>
        [Obsolete("SHA1 is deprecated for PBKDF2 due to collision vulnerabilities. Use SHA256 or SHA512 instead.", false)]
        private static HashAlgorithmName HandleDeprecatedSHA1()
        {
            System.Diagnostics.Trace.TraceWarning(
                "WARNING: PBKDF2-SHA1 is deprecated due to collision vulnerabilities. " +
                "Please migrate to SHA256 or SHA512 for better security.");
            return HashAlgorithmName.SHA1;
        }

        public override void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _storageLock.Dispose();
            base.Dispose();
        }
    }

    #region Configuration and Data Classes

    /// <summary>
    /// Configuration for PBKDF2 password-derived key store.
    /// </summary>
    public class Pbkdf2Config
    {
        /// <summary>
        /// Number of iterations. OWASP 2024 recommends:
        /// - SHA-256: 600,000 minimum
        /// - SHA-512: 210,000 minimum
        /// Default: 600000
        /// </summary>
        public int Iterations { get; set; } = 600000;

        /// <summary>
        /// Hash algorithm to use with HMAC.
        /// Options: SHA256, SHA384, SHA512, SHA1 (not recommended)
        /// Default: SHA256
        /// </summary>
        public HashAlgorithmName HashAlgorithmName { get; set; } = HashAlgorithmName.SHA256;

        /// <summary>
        /// Size of generated encryption keys in bytes.
        /// Default: 32 (256 bits)
        /// </summary>
        public int KeySizeBytes { get; set; } = 32;

        /// <summary>
        /// Size of salt in bytes.
        /// Default: 32 (256 bits)
        /// </summary>
        public int SaltSizeBytes { get; set; } = 32;

        /// <summary>
        /// Path to store encrypted key material.
        /// </summary>
        public string? StoragePath { get; set; }
    }

    /// <summary>
    /// Encrypted key data stored to disk for PBKDF2 strategy.
    /// </summary>
    internal class Pbkdf2EncryptedKeyData
    {
        public string KeyId { get; set; } = "";
        public byte[] Salt { get; set; } = Array.Empty<byte>();
        public byte[] EncryptedKey { get; set; } = Array.Empty<byte>();
        public byte[] Nonce { get; set; } = Array.Empty<byte>();
        public byte[] Tag { get; set; } = Array.Empty<byte>();
        public int Iterations { get; set; }
        public string HashAlgorithm { get; set; } = "SHA256";
        public DateTime CreatedAt { get; set; }
        public int Version { get; set; }
    }

    /// <summary>
    /// Storage file structure for PBKDF2 strategy.
    /// </summary>
    internal class Pbkdf2StoredKeyFile
    {
        public string? CurrentKeyId { get; set; }
        public List<Pbkdf2EncryptedKeyData> Keys { get; set; } = new();
    }

    #endregion
}
