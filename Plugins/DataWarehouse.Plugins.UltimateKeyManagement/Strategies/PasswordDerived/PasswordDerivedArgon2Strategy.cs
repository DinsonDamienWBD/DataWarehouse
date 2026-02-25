using DataWarehouse.SDK.Security;
using Konscious.Security.Cryptography;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.PasswordDerived
{
    /// <summary>
    /// Password-derived KeyStore strategy using Argon2id - the recommended algorithm.
    /// Winner of the Password Hashing Competition (PHC) in 2015.
    ///
    /// Argon2id is a hybrid variant that combines:
    /// - Argon2i (data-independent memory access - side-channel resistant)
    /// - Argon2d (data-dependent memory access - GPU/ASIC resistant)
    ///
    /// Features:
    /// - Memory-hard key derivation (configurable memory cost)
    /// - Parallelism support (configurable lanes)
    /// - Time cost (iteration count)
    /// - Unique salt per key
    /// - AES-GCM encryption of derived key material for storage
    ///
    /// Recommended parameters (OWASP 2024):
    /// - Memory: 64 MiB minimum (65536 KiB)
    /// - Iterations: 3 minimum
    /// - Parallelism: 4 (match available cores)
    ///
    /// Requirements:
    /// - Konscious.Security.Cryptography.Argon2 NuGet package
    /// </summary>
    public sealed class PasswordDerivedArgon2Strategy : KeyStoreStrategyBase
    {
        private Argon2Config _config = new();
        private string _currentKeyId = "default";
        private readonly Dictionary<string, EncryptedKeyData> _storedKeys = new();
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
                ["Algorithm"] = "Argon2id",
                ["Standard"] = "RFC 9106",
                ["PHCWinner"] = true,
                ["MemoryHard"] = true,
                ["SideChannelResistant"] = true,
                ["GpuResistant"] = true
            }
        };

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("passwordderivedargon2.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }


        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("passwordderivedargon2.init");
            // Load configuration
            if (Configuration.TryGetValue("Password", out var pwdObj) && pwdObj is string pwd)
                _masterPassword = pwd;
            if (Configuration.TryGetValue("PasswordEnvVar", out var envObj) && envObj is string envVar)
            {
                var envPwd = Environment.GetEnvironmentVariable(envVar);
                if (!string.IsNullOrEmpty(envPwd))
                    _masterPassword = envPwd;
            }
            if (Configuration.TryGetValue("MemoryKiB", out var memObj) && memObj is int mem)
                _config.MemoryKiB = mem;
            if (Configuration.TryGetValue("Iterations", out var iterObj) && iterObj is int iter)
                _config.Iterations = iter;
            if (Configuration.TryGetValue("Parallelism", out var paraObj) && paraObj is int para)
                _config.Parallelism = para;
            if (Configuration.TryGetValue("KeySizeBytes", out var keySizeObj) && keySizeObj is int keySize)
                _config.KeySizeBytes = keySize;
            if (Configuration.TryGetValue("StoragePath", out var pathObj) && pathObj is string path)
                _config.StoragePath = path;
            if (Configuration.TryGetValue("SaltSizeBytes", out var saltObj) && saltObj is int saltSize)
                _config.SaltSizeBytes = saltSize;

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

            // Verify we can derive a key
            try
            {
                var testSalt = RandomNumberGenerator.GetBytes(16);
                var _ = await DeriveKeyAsync("test", testSalt, 32, cancellationToken);
                return true;
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
                    throw new KeyNotFoundException($"Key '{keyId}' not found in Argon2 key store.");
                }

                // Derive the wrapping key using stored salt and parameters
                var wrappingKey = await DeriveKeyAsync(
                    GetPassword(),
                    encryptedData.Salt,
                    32, // 256-bit wrapping key
                    CancellationToken.None,
                    encryptedData.MemoryKiB,
                    encryptedData.Iterations,
                    encryptedData.Parallelism);

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
                var wrappingKey = await DeriveKeyAsync(GetPassword(), salt, 32, CancellationToken.None);

                // Encrypt the key material
                var (encryptedKey, nonce, tag) = EncryptKeyMaterial(keyData, wrappingKey);

                // Store the encrypted data with parameters
                var encryptedData = new EncryptedKeyData
                {
                    KeyId = keyId,
                    Salt = salt,
                    EncryptedKey = encryptedKey,
                    Nonce = nonce,
                    Tag = tag,
                    MemoryKiB = _config.MemoryKiB,
                    Iterations = _config.Iterations,
                    Parallelism = _config.Parallelism,
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
                    ["Algorithm"] = "Argon2id",
                    ["MemoryKiB"] = data.MemoryKiB,
                    ["Iterations"] = data.Iterations,
                    ["Parallelism"] = data.Parallelism,
                    ["SaltSize"] = data.Salt.Length
                }
            });
        }

        /// <summary>
        /// Derives a key using Argon2id with the specified parameters.
        /// </summary>
        private async Task<byte[]> DeriveKeyAsync(
            string password,
            byte[] salt,
            int outputLength,
            CancellationToken cancellationToken,
            int? memoryKiB = null,
            int? iterations = null,
            int? parallelism = null)
        {
            return await Task.Run(() =>
            {
                using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
                {
                    Salt = salt,
                    MemorySize = memoryKiB ?? _config.MemoryKiB,
                    Iterations = iterations ?? _config.Iterations,
                    DegreeOfParallelism = parallelism ?? _config.Parallelism
                };

                return argon2.GetBytes(outputLength);
            }, cancellationToken);
        }

        /// <summary>
        /// Encrypts key material using AES-GCM with the derived wrapping key.
        /// </summary>
        private static (byte[] ciphertext, byte[] nonce, byte[] tag) EncryptKeyMaterial(byte[] plaintext, byte[] key)
        {
            var nonce = RandomNumberGenerator.GetBytes(12); // 96-bit nonce for AES-GCM
            var tag = new byte[16]; // 128-bit authentication tag
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
            return Path.Combine(baseDir, "DataWarehouse", "argon2-keys.json");
        }

        private async Task LoadStoredKeysAsync(CancellationToken cancellationToken)
        {
            var storagePath = GetStoragePath();
            if (!File.Exists(storagePath))
                return;

            try
            {
                var json = await File.ReadAllTextAsync(storagePath, cancellationToken);
                var stored = JsonSerializer.Deserialize<StoredKeyFile>(json);

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
                System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
            }
        }

        private async Task PersistStoredKeysAsync()
        {
            var storagePath = GetStoragePath();
            var file = new StoredKeyFile
            {
                CurrentKeyId = _currentKeyId,
                Keys = _storedKeys.Values.ToList()
            };

            var json = JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(storagePath, json);
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
    /// Configuration for Argon2id password-derived key store.
    /// </summary>
    public class Argon2Config
    {
        /// <summary>
        /// Memory cost in KiB. OWASP recommends minimum 64 MiB (65536 KiB).
        /// Higher values increase resistance to GPU/ASIC attacks.
        /// Default: 65536 (64 MiB)
        /// </summary>
        public int MemoryKiB { get; set; } = 65536;

        /// <summary>
        /// Number of iterations (time cost). Minimum recommended: 3.
        /// Higher values increase computation time linearly.
        /// Default: 3
        /// </summary>
        public int Iterations { get; set; } = 3;

        /// <summary>
        /// Degree of parallelism (number of threads/lanes).
        /// Should match available CPU cores for optimal performance.
        /// Default: 4
        /// </summary>
        public int Parallelism { get; set; } = 4;

        /// <summary>
        /// Size of generated encryption keys in bytes.
        /// Default: 32 (256 bits)
        /// </summary>
        public int KeySizeBytes { get; set; } = 32;

        /// <summary>
        /// Size of salt in bytes. Minimum 16 bytes recommended.
        /// Default: 32 (256 bits)
        /// </summary>
        public int SaltSizeBytes { get; set; } = 32;

        /// <summary>
        /// Path to store encrypted key material.
        /// Defaults to LocalApplicationData/DataWarehouse/argon2-keys.json
        /// </summary>
        public string? StoragePath { get; set; }
    }

    /// <summary>
    /// Encrypted key data stored to disk.
    /// </summary>
    internal class EncryptedKeyData
    {
        public string KeyId { get; set; } = "";
        public byte[] Salt { get; set; } = Array.Empty<byte>();
        public byte[] EncryptedKey { get; set; } = Array.Empty<byte>();
        public byte[] Nonce { get; set; } = Array.Empty<byte>();
        public byte[] Tag { get; set; } = Array.Empty<byte>();
        public int MemoryKiB { get; set; }
        public int Iterations { get; set; }
        public int Parallelism { get; set; }
        public DateTime CreatedAt { get; set; }
        public int Version { get; set; }
    }

    /// <summary>
    /// Storage file structure.
    /// </summary>
    internal class StoredKeyFile
    {
        public string? CurrentKeyId { get; set; }
        public List<EncryptedKeyData> Keys { get; set; } = new();
    }

    #endregion
}
