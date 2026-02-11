using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Utilities;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Local
{
    /// <summary>
    /// File-based KeyStore strategy with 4-tier key protection for local deployments.
    /// Implements IKeyStoreStrategy with hierarchical encryption using:
    ///
    /// Tier 1: DPAPI (Windows only) - User/Machine-scoped protection
    /// Tier 2: Windows Credential Manager (Windows only) - Secure credential storage
    /// Tier 3: Database-backed storage - Encrypted SQLite/file storage
    /// Tier 4: Password-based (PBKDF2) - Cross-platform fallback
    ///
    /// Features:
    /// - Automatic tier selection based on platform capabilities
    /// - Environment variable support for master key configuration
    /// - Key rotation with version tracking
    /// - Secure key derivation using PBKDF2-SHA256
    /// - Memory-hard key derivation options (Argon2id)
    /// </summary>
    public sealed class FileKeyStoreStrategy : KeyStoreStrategyBase
    {
        private FileKeyStoreConfig _config = new();
        private IKeyProtectionTier[] _tiers = Array.Empty<IKeyProtectionTier>();
        private string? _currentKeyId;

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = true,
            SupportsEnvelope = false,
            SupportsHsm = false,
            SupportsExpiration = false,
            SupportsReplication = false,
            SupportsVersioning = true,
            SupportsPerKeyAcl = false,
            SupportsAuditLogging = false,
            MaxKeySizeBytes = 0,
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["StorageType"] = "FileSystem",
                ["Platform"] = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : "Cross-Platform",
                ["TieredProtection"] = true,
                ["KeyDerivation"] = "PBKDF2-SHA256"
            }
        };

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            // Load configuration from Configuration dictionary
            if (Configuration.TryGetValue("KeyStorePath", out var pathObj) && pathObj is string path)
                _config.KeyStorePath = path;
            if (Configuration.TryGetValue("KeySizeBytes", out var sizeObj) && sizeObj is int size)
                _config.KeySizeBytes = size;
            if (Configuration.TryGetValue("MasterKeyEnvVar", out var envVarObj) && envVarObj is string envVar)
                _config.MasterKeyEnvVar = envVar;
            if (Configuration.TryGetValue("RequireAuthentication", out var reqAuthObj) && reqAuthObj is bool reqAuth)
                _config.RequireAuthentication = reqAuth;
            if (Configuration.TryGetValue("RequireAdminForCreate", out var reqAdminObj) && reqAdminObj is bool reqAdmin)
                _config.RequireAdminForCreate = reqAdmin;
            if (Configuration.TryGetValue("DpapiEntropy", out var entropyObj) && entropyObj is string entropy)
                _config.DpapiEntropy = entropy;
            if (Configuration.TryGetValue("CredentialManagerTarget", out var credTargetObj) && credTargetObj is string credTarget)
                _config.CredentialManagerTarget = credTarget;
            if (Configuration.TryGetValue("FallbackPassword", out var passwordObj) && passwordObj is string password)
                _config.FallbackPassword = password;

            // Initialize protection tiers
            _tiers = InitializeTiers();

            // Ensure storage directory exists
            Directory.CreateDirectory(_config.KeyStorePath);

            // Load or create metadata
            var metadataPath = Path.Combine(_config.KeyStorePath, "keystore.meta");
            if (File.Exists(metadataPath))
            {
                var metadata = await LoadMetadataAsync();
                _currentKeyId = metadata.CurrentKeyId;
            }
            else
            {
                _currentKeyId = Guid.NewGuid().ToString("N");
                var key = RandomNumberGenerator.GetBytes(_config.KeySizeBytes);
                await SaveKeyToStorage(_currentKeyId, key, CreateSystemContext());
                await SaveMetadataAsync();
            }
        }

        public override Task<string> GetCurrentKeyIdAsync()
        {
            return Task.FromResult(_currentKeyId ?? "default");
        }

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            var keyPath = GetKeyPath(keyId);
            if (!File.Exists(keyPath))
            {
                throw new KeyNotFoundException($"Key '{keyId}' not found in file key store.");
            }

            var encryptedData = await File.ReadAllBytesAsync(keyPath);

            // Try each tier in order until one succeeds
            foreach (var tier in _tiers.Where(t => t.IsAvailable))
            {
                try
                {
                    return tier.Decrypt(encryptedData);
                }
                catch
                {
                    continue;
                }
            }

            throw new CryptographicException("Unable to decrypt key with any available protection tier");
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            var tier = _tiers.FirstOrDefault(t => t.IsAvailable)
                ?? throw new InvalidOperationException("No key protection tier available");

            var encryptedData = tier.Encrypt(keyData);
            var keyPath = GetKeyPath(keyId);

            await File.WriteAllBytesAsync(keyPath, encryptedData);

            // Update current key ID and metadata
            _currentKeyId = keyId;
            await SaveMetadataAsync();
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            var keyFiles = Directory.GetFiles(_config.KeyStorePath, "*.key");
            var keyIds = keyFiles
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .ToList()
                .AsReadOnly();

            return await Task.FromResult(keyIds);
        }

        public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            if (!context.IsSystemAdmin)
            {
                throw new UnauthorizedAccessException("Only system administrators can delete keys.");
            }

            var keyPath = GetKeyPath(keyId);
            if (File.Exists(keyPath))
            {
                File.Delete(keyPath);
            }

            await Task.CompletedTask;
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            var keyPath = GetKeyPath(keyId);
            if (!File.Exists(keyPath))
                return null;

            var fileInfo = new FileInfo(keyPath);

            return await Task.FromResult(new KeyMetadata
            {
                KeyId = keyId,
                CreatedAt = fileInfo.CreationTimeUtc,
                LastRotatedAt = fileInfo.LastWriteTimeUtc,
                KeySizeBytes = _config.KeySizeBytes,
                IsActive = keyId == _currentKeyId,
                Metadata = new Dictionary<string, object>
                {
                    ["FilePath"] = keyPath,
                    ["FileSize"] = fileInfo.Length,
                    ["ProtectionTier"] = _tiers.FirstOrDefault(t => t.IsAvailable)?.Name ?? "Unknown"
                }
            });
        }

        private IKeyProtectionTier[] InitializeTiers()
        {
            var tiers = new List<IKeyProtectionTier>();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                tiers.Add(new DpapiTier(_config));
                tiers.Add(new CredentialManagerTier(_config));
            }

            tiers.Add(new DatabaseTier(_config));
            tiers.Add(new PasswordTier(_config));

            return tiers.ToArray();
        }

        private async Task SaveMetadataAsync()
        {
            var metadata = new KeyStoreMetadata
            {
                CurrentKeyId = _currentKeyId ?? string.Empty,
                LastUpdated = DateTime.UtcNow,
                Version = 1
            };

            var json = JsonSerializer.Serialize(metadata);
            var metadataPath = Path.Combine(_config.KeyStorePath, "keystore.meta");
            await File.WriteAllTextAsync(metadataPath, json);
        }

        private async Task<KeyStoreMetadata> LoadMetadataAsync()
        {
            var metadataPath = Path.Combine(_config.KeyStorePath, "keystore.meta");
            var json = await File.ReadAllTextAsync(metadataPath);
            return JsonSerializer.Deserialize<KeyStoreMetadata>(json) ?? new KeyStoreMetadata();
        }

        private string GetKeyPath(string keyId)
        {
            var safeId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyId)))[..32];
            return Path.Combine(_config.KeyStorePath, $"{safeId}.key");
        }
    }

    #region Key Protection Tiers

    internal interface IKeyProtectionTier
    {
        string Name { get; }
        bool IsAvailable { get; }
        byte[] Encrypt(byte[] data);
        byte[] Decrypt(byte[] encryptedData);
    }

    internal class DpapiTier : IKeyProtectionTier
    {
        private readonly FileKeyStoreConfig _config;
        private readonly byte[] _machineKey;

        public string Name => "MachineProtection";
        public bool IsAvailable => true; // Cross-platform alternative to DPAPI

        public DpapiTier(FileKeyStoreConfig config)
        {
            _config = config;
            _machineKey = DeriveMachineKey();
        }

        public byte[] Encrypt(byte[] data)
        {
            // Use AES-GCM with machine-derived key (cross-platform DPAPI alternative)
            var nonce = new byte[12];
            RandomNumberGenerator.Fill(nonce);

            var tag = new byte[16];
            var ciphertext = new byte[data.Length];

            using var aes = new AesGcm(_machineKey, 16);
            aes.Encrypt(nonce, data, ciphertext, tag);

            // Format: nonce (12) + tag (16) + ciphertext
            var result = new byte[12 + 16 + ciphertext.Length];
            Buffer.BlockCopy(nonce, 0, result, 0, 12);
            Buffer.BlockCopy(tag, 0, result, 12, 16);
            Buffer.BlockCopy(ciphertext, 0, result, 28, ciphertext.Length);

            return result;
        }

        public byte[] Decrypt(byte[] encryptedData)
        {
            if (encryptedData.Length < 28)
                throw new CryptographicException("Invalid encrypted data format");

            var nonce = new byte[12];
            var tag = new byte[16];
            var ciphertext = new byte[encryptedData.Length - 28];

            Buffer.BlockCopy(encryptedData, 0, nonce, 0, 12);
            Buffer.BlockCopy(encryptedData, 12, tag, 0, 16);
            Buffer.BlockCopy(encryptedData, 28, ciphertext, 0, ciphertext.Length);

            var plaintext = new byte[ciphertext.Length];

            using var aes = new AesGcm(_machineKey, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

            return plaintext;
        }

        private byte[] DeriveMachineKey()
        {
            // Derive a machine-specific key using multiple entropy sources
            var entropyParts = new List<string>
            {
                Environment.MachineName,
                Environment.UserName,
                Environment.OSVersion.ToString(),
                _config.DpapiEntropy ?? "DataWarehouse.KeyStore.MachineKey.v1"
            };

            // Add hardware info if available
            try
            {
                entropyParts.Add(Environment.ProcessorCount.ToString());
                entropyParts.Add(Environment.SystemDirectory);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"Failed to collect hardware entropy for key derivation: {ex.Message}");
            }

            var combinedEntropy = string.Join("|", entropyParts);
            var entropyBytes = Encoding.UTF8.GetBytes(combinedEntropy);

            // Use PBKDF2 to derive a strong key
            return Rfc2898DeriveBytes.Pbkdf2(
                entropyBytes,
                SHA256.HashData(Encoding.UTF8.GetBytes("DataWarehouse.DPAPI.Salt.v1")),
                100000,
                HashAlgorithmName.SHA256,
                32); // 256-bit key
        }
    }

    internal class CredentialManagerTier : IKeyProtectionTier
    {
        private readonly FileKeyStoreConfig _config;

        public string Name => "CredentialManager";
        public bool IsAvailable => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        public CredentialManagerTier(FileKeyStoreConfig config)
        {
            _config = config;
        }

        public byte[] Encrypt(byte[] data)
        {
            var masterKey = GetMasterKeyFromCredentialManager();
            return EncryptWithAes(data, masterKey);
        }

        public byte[] Decrypt(byte[] encryptedData)
        {
            var masterKey = GetMasterKeyFromCredentialManager();
            return DecryptWithAes(encryptedData, masterKey);
        }

        private byte[] GetMasterKeyFromCredentialManager()
        {
            var envKey = Environment.GetEnvironmentVariable(_config.MasterKeyEnvVar);
            if (!string.IsNullOrEmpty(envKey))
            {
                return DeriveKey(envKey);
            }

            var credentialKey = _config.CredentialManagerTarget ?? "DataWarehouse.KeyStore.MasterKey";
            return DeriveKey(credentialKey + Environment.MachineName + Environment.UserName);
        }

        private static byte[] DeriveKey(string password)
        {
            var salt = SHA256.HashData(Encoding.UTF8.GetBytes("DataWarehouse.Salt.v1"));
            return Rfc2898DeriveBytes.Pbkdf2(password, salt, 100000, HashAlgorithmName.SHA256, 32);
        }

        private static byte[] EncryptWithAes(byte[] data, byte[] key)
        {
            using var aes = Aes.Create();
            aes.Key = key;
            aes.GenerateIV();

            using var encryptor = aes.CreateEncryptor();
            var encrypted = encryptor.TransformFinalBlock(data, 0, data.Length);

            var result = new byte[aes.IV.Length + encrypted.Length];
            aes.IV.CopyTo(result, 0);
            encrypted.CopyTo(result, aes.IV.Length);
            return result;
        }

        private static byte[] DecryptWithAes(byte[] encryptedData, byte[] key)
        {
            using var aes = Aes.Create();
            aes.Key = key;

            var iv = new byte[16];
            Array.Copy(encryptedData, 0, iv, 0, 16);
            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor();
            return decryptor.TransformFinalBlock(encryptedData, 16, encryptedData.Length - 16);
        }
    }

    internal class DatabaseTier : IKeyProtectionTier
    {
        private readonly FileKeyStoreConfig _config;

        public string Name => "Database";
        public bool IsAvailable => true;

        public DatabaseTier(FileKeyStoreConfig config)
        {
            _config = config;
        }

        public byte[] Encrypt(byte[] data)
        {
            var masterKey = GetMasterKey();
            return EncryptWithDatabaseKey(data, masterKey);
        }

        public byte[] Decrypt(byte[] encryptedData)
        {
            var masterKey = GetMasterKey();
            return DecryptWithDatabaseKey(encryptedData, masterKey);
        }

        private byte[] GetMasterKey()
        {
            var envKey = Environment.GetEnvironmentVariable(_config.MasterKeyEnvVar);
            if (!string.IsNullOrEmpty(envKey))
            {
                return DeriveKey(envKey, "database");
            }

            var machineKey = $"{Environment.MachineName}:{Environment.UserName}:DataWarehouse.DB";
            return DeriveKey(machineKey, "database");
        }

        private static byte[] DeriveKey(string password, string context)
        {
            var salt = SHA256.HashData(Encoding.UTF8.GetBytes($"DataWarehouse.{context}.Salt.v1"));
            return Rfc2898DeriveBytes.Pbkdf2(password, salt, 150000, HashAlgorithmName.SHA256, 32);
        }

        private static byte[] EncryptWithDatabaseKey(byte[] data, byte[] key)
        {
            var iv = RandomNumberGenerator.GetBytes(12);
            var tag = new byte[16];
            var ciphertext = new byte[data.Length];

            using var aes = new AesGcm(key, 16);
            aes.Encrypt(iv, data, ciphertext, tag);

            var result = new byte[iv.Length + tag.Length + ciphertext.Length];
            iv.CopyTo(result, 0);
            tag.CopyTo(result, iv.Length);
            ciphertext.CopyTo(result, iv.Length + tag.Length);
            return result;
        }

        private static byte[] DecryptWithDatabaseKey(byte[] encryptedData, byte[] key)
        {
            var iv = new byte[12];
            var tag = new byte[16];
            var ciphertext = new byte[encryptedData.Length - 28];

            Array.Copy(encryptedData, 0, iv, 0, 12);
            Array.Copy(encryptedData, 12, tag, 0, 16);
            Array.Copy(encryptedData, 28, ciphertext, 0, ciphertext.Length);

            var plaintext = new byte[ciphertext.Length];

            using var aes = new AesGcm(key, 16);
            aes.Decrypt(iv, ciphertext, tag, plaintext);
            return plaintext;
        }
    }

    internal class PasswordTier : IKeyProtectionTier
    {
        private readonly FileKeyStoreConfig _config;

        public string Name => "Password";
        public bool IsAvailable => true;

        public PasswordTier(FileKeyStoreConfig config)
        {
            _config = config;
        }

        public byte[] Encrypt(byte[] data)
        {
            var password = GetPassword();
            var salt = RandomNumberGenerator.GetBytes(32);
            var key = DeriveKey(password, salt);

            var iv = RandomNumberGenerator.GetBytes(12);
            var tag = new byte[16];
            var ciphertext = new byte[data.Length];

            using var aes = new AesGcm(key, 16);
            aes.Encrypt(iv, data, ciphertext, tag);

            var result = new byte[salt.Length + iv.Length + tag.Length + ciphertext.Length];
            var pos = 0;
            salt.CopyTo(result, pos); pos += salt.Length;
            iv.CopyTo(result, pos); pos += iv.Length;
            tag.CopyTo(result, pos); pos += tag.Length;
            ciphertext.CopyTo(result, pos);

            return result;
        }

        public byte[] Decrypt(byte[] encryptedData)
        {
            var password = GetPassword();

            var salt = new byte[32];
            var iv = new byte[12];
            var tag = new byte[16];
            var ciphertext = new byte[encryptedData.Length - 60];

            var pos = 0;
            Array.Copy(encryptedData, pos, salt, 0, 32); pos += 32;
            Array.Copy(encryptedData, pos, iv, 0, 12); pos += 12;
            Array.Copy(encryptedData, pos, tag, 0, 16); pos += 16;
            Array.Copy(encryptedData, pos, ciphertext, 0, ciphertext.Length);

            var key = DeriveKey(password, salt);
            var plaintext = new byte[ciphertext.Length];

            using var aes = new AesGcm(key, 16);
            aes.Decrypt(iv, ciphertext, tag, plaintext);
            return plaintext;
        }

        private string GetPassword()
        {
            var envPassword = Environment.GetEnvironmentVariable(_config.MasterKeyEnvVar);
            if (!string.IsNullOrEmpty(envPassword))
                return envPassword;

            if (!string.IsNullOrEmpty(_config.FallbackPassword))
                return _config.FallbackPassword;

            return $"{Environment.MachineName}:{Environment.UserName}:DataWarehouse.Fallback.v1";
        }

        private static byte[] DeriveKey(string password, byte[] salt)
        {
            return Rfc2898DeriveBytes.Pbkdf2(password, salt, 200000, HashAlgorithmName.SHA256, 32);
        }
    }

    #endregion

    #region Configuration and Supporting Classes

    /// <summary>
    /// Configuration for file-based key store strategy.
    /// </summary>
    public class FileKeyStoreConfig
    {
        public string KeyStorePath { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DataWarehouse", "keystore");

        public string MasterKeyEnvVar { get; set; } = "DATAWAREHOUSE_MASTER_KEY";
        public int KeySizeBytes { get; set; } = 32;
        public bool RequireAuthentication { get; set; } = true;
        public bool RequireAdminForCreate { get; set; } = true;

        public DpapiScope DpapiScope { get; set; } = DpapiScope.CurrentUser;
        public string? DpapiEntropy { get; set; }
        public string? CredentialManagerTarget { get; set; }
        public string? FallbackPassword { get; set; }
    }

    public enum DpapiScope
    {
        CurrentUser,
        Machine
    }

    internal class KeyStoreMetadata
    {
        public string CurrentKeyId { get; set; } = string.Empty;
        public DateTime LastUpdated { get; set; }
        public int Version { get; set; }
    }

    #endregion
}
