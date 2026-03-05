using DataWarehouse.SDK.Security;
using Org.BouncyCastle.Crypto.Generators;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.PasswordDerived
{
    /// <summary>
    /// Password-derived KeyStore strategy using scrypt - a proven memory-hard KDF.
    /// Designed by Colin Percival in 2009 for Tarsnap backup service.
    ///
    /// scrypt features:
    /// - Memory-hard algorithm (requires significant RAM)
    /// - Sequential memory-hard function (SMHF)
    /// - Configurable N (CPU/memory cost), r (block size), p (parallelization)
    /// - Proven security with years of real-world deployment
    ///
    /// Parameters:
    /// - N: CPU/memory cost factor (must be power of 2). Memory used = 128 * N * r bytes
    /// - r: Block size parameter. Increases memory usage and mixes.
    /// - p: Parallelization parameter. Allows parallel computation.
    ///
    /// Recommended parameters:
    /// - Interactive (login): N=2^14 (16384), r=8, p=1 (~100ms, 16MB)
    /// - Sensitive storage: N=2^20 (1048576), r=8, p=1 (~5s, 1GB)
    /// - Default: N=2^17 (131072), r=8, p=1 (~1s, 128MB)
    ///
    /// Requirements:
    /// - Scrypt.NET NuGet package
    /// </summary>
    public sealed class PasswordDerivedScryptStrategy : KeyStoreStrategyBase
    {
        private ScryptConfig _config = new();
        private string _currentKeyId = "default";
        private readonly Dictionary<string, ScryptEncryptedKeyData> _storedKeys = new();
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
                ["Algorithm"] = "scrypt",
                ["Designer"] = "Colin Percival",
                ["Year"] = 2009,
                ["MemoryHard"] = true,
                ["SequentialMemoryHard"] = true,
                ["UsedBy"] = "Tarsnap, Litecoin, many password managers"
            }
        };

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("passwordderivedscrypt.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }


        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("passwordderivedscrypt.init");
            // Load configuration
            if (Configuration.TryGetValue("Password", out var pwdObj) && pwdObj is string pwd)
                _masterPassword = pwd;
            if (Configuration.TryGetValue("PasswordEnvVar", out var envObj) && envObj is string envVar)
            {
                var envPwd = Environment.GetEnvironmentVariable(envVar);
                if (!string.IsNullOrEmpty(envPwd))
                    _masterPassword = envPwd;
            }
            if (Configuration.TryGetValue("N", out var nObj) && nObj is int n)
                _config.N = n;
            if (Configuration.TryGetValue("CostFactor", out var costObj) && costObj is int cost)
                _config.N = 1 << cost; // 2^cost
            if (Configuration.TryGetValue("r", out var rObj) && rObj is int r)
                _config.R = r;
            if (Configuration.TryGetValue("p", out var pObj) && pObj is int p)
                _config.P = p;
            if (Configuration.TryGetValue("KeySizeBytes", out var keySizeObj) && keySizeObj is int keySize)
                _config.KeySizeBytes = keySize;
            if (Configuration.TryGetValue("StoragePath", out var pathObj) && pathObj is string path)
                _config.StoragePath = path;
            if (Configuration.TryGetValue("SaltSizeBytes", out var saltObj) && saltObj is int saltSize)
                _config.SaltSizeBytes = saltSize;

            // Validate N is power of 2
            if ((_config.N & (_config.N - 1)) != 0 || _config.N < 2)
            {
                throw new ArgumentException("N must be a power of 2 and >= 2", nameof(_config.N));
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
                    throw new KeyNotFoundException($"Key '{keyId}' not found in scrypt key store.");
                }

                // Derive the wrapping key using stored salt and parameters
                var wrappingKey = await DeriveKeyAsync(
                    GetPassword(),
                    encryptedData.Salt,
                    32,
                    CancellationToken.None,
                    encryptedData.N,
                    encryptedData.R,
                    encryptedData.P);

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
                var encryptedData = new ScryptEncryptedKeyData
                {
                    KeyId = keyId,
                    Salt = salt,
                    EncryptedKey = encryptedKey,
                    Nonce = nonce,
                    Tag = tag,
                    N = _config.N,
                    R = _config.R,
                    P = _config.P,
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

            // Calculate approximate memory usage: 128 * N * r bytes
            var memoryUsageBytes = 128L * data.N * data.R;

            return await Task.FromResult(new KeyMetadata
            {
                KeyId = keyId,
                CreatedAt = data.CreatedAt,
                Version = data.Version,
                IsActive = keyId == _currentKeyId,
                KeySizeBytes = _config.KeySizeBytes,
                Metadata = new Dictionary<string, object>
                {
                    ["Algorithm"] = "scrypt",
                    ["N"] = data.N,
                    ["r"] = data.R,
                    ["p"] = data.P,
                    ["MemoryUsageMB"] = memoryUsageBytes / (1024 * 1024),
                    ["SaltSize"] = data.Salt.Length
                }
            });
        }

        /// <summary>
        /// Derives a key using scrypt with the specified parameters.
        /// Uses the ScryptEncoder from Scrypt.NET package.
        /// </summary>
        private async Task<byte[]> DeriveKeyAsync(
            string password,
            byte[] salt,
            int outputLength,
            CancellationToken cancellationToken,
            int? n = null,
            int? r = null,
            int? p = null)
        {
            return await Task.Run(() =>
            {
                var N = n ?? _config.N;
                var R = r ?? _config.R;
                var P = p ?? _config.P;

                // Use BouncyCastle's SCrypt implementation for direct key derivation
                return SCrypt.Generate(
                    Encoding.UTF8.GetBytes(password),
                    salt,
                    N,
                    R,
                    P,
                    outputLength);
            }, cancellationToken);
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
            return Path.Combine(baseDir, "DataWarehouse", "scrypt-keys.json");
        }

        private async Task LoadStoredKeysAsync(CancellationToken cancellationToken)
        {
            var storagePath = GetStoragePath();
            if (!File.Exists(storagePath))
                return;

            try
            {
                var json = await File.ReadAllTextAsync(storagePath, cancellationToken);
                var stored = JsonSerializer.Deserialize<ScryptStoredKeyFile>(json);

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
            var file = new ScryptStoredKeyFile
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
    /// Configuration for scrypt password-derived key store.
    /// </summary>
    public class ScryptConfig
    {
        /// <summary>
        /// CPU/memory cost parameter N. Must be power of 2.
        /// Memory usage = 128 * N * r bytes.
        /// Default: 131072 (2^17) = ~128 MB with r=8
        /// </summary>
        public int N { get; set; } = 131072;

        /// <summary>
        /// Block size parameter r.
        /// Higher values increase memory usage per iteration.
        /// Default: 8
        /// </summary>
        public int R { get; set; } = 8;

        /// <summary>
        /// Parallelization parameter p.
        /// Number of parallel chains to mix.
        /// Default: 1 (single-threaded derivation)
        /// </summary>
        public int P { get; set; } = 1;

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
    /// Encrypted key data stored to disk for scrypt strategy.
    /// </summary>
    internal class ScryptEncryptedKeyData
    {
        public string KeyId { get; set; } = "";
        public byte[] Salt { get; set; } = Array.Empty<byte>();
        public byte[] EncryptedKey { get; set; } = Array.Empty<byte>();
        public byte[] Nonce { get; set; } = Array.Empty<byte>();
        public byte[] Tag { get; set; } = Array.Empty<byte>();
        public int N { get; set; }
        public int R { get; set; }
        public int P { get; set; }
        public DateTime CreatedAt { get; set; }
        public int Version { get; set; }
    }

    /// <summary>
    /// Storage file structure for scrypt strategy.
    /// </summary>
    internal class ScryptStoredKeyFile
    {
        public string? CurrentKeyId { get; set; }
        public List<ScryptEncryptedKeyData> Keys { get; set; } = new();
    }

    #endregion
}
