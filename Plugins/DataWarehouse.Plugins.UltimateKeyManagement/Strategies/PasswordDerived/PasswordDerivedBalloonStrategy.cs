using System.Buffers.Binary;
using DataWarehouse.SDK.Security;
using Org.BouncyCastle.Crypto.Digests;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.PasswordDerived
{
    /// <summary>
    /// Password-derived KeyStore strategy using Balloon Hashing.
    /// Published by Dan Boneh, Henry Corrigan-Gibbs, and Stuart Schechter in 2016.
    ///
    /// Balloon hashing features:
    /// - Memory-hard with proven security guarantees
    /// - Provably memory-hard in the random oracle model
    /// - Simple, analyzable design
    /// - Space-time tradeoff resistance
    /// - Uses only standard cryptographic primitives (hash functions)
    ///
    /// Algorithm outline:
    /// 1. Initialize buffer with hash(password || salt || counter)
    /// 2. Mix buffer contents iteratively (sCost rounds)
    /// 3. Use delta (number of dependencies) to ensure memory-hardness
    /// 4. Final hash produces output
    ///
    /// Parameters:
    /// - sCost: Space cost - number of blocks in buffer (memory = sCost * hash_output_size)
    /// - tCost: Time cost - number of mixing rounds
    /// - delta: Number of dependencies per block (recommended: 3)
    ///
    /// Implementation uses BouncyCastle for SHA-256 (or configurable hash).
    /// </summary>
    public sealed class PasswordDerivedBalloonStrategy : KeyStoreStrategyBase
    {
        private BalloonConfig _config = new();
        private string _currentKeyId = "default";
        private readonly Dictionary<string, BalloonEncryptedKeyData> _storedKeys = new();
        // #3563: Store password as byte[] instead of string to enable zeroing after use.
        private byte[]? _masterPasswordBytes;
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
                ["Algorithm"] = "Balloon",
                ["Designers"] = "Boneh, Corrigan-Gibbs, Schechter",
                ["Year"] = 2016,
                ["MemoryHard"] = true,
                ["ProvablySecure"] = true,
                ["SpaceTimeTradeoffResistant"] = true,
                ["UsesStandardPrimitives"] = true
            }
        };

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("passwordderivedballoon.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }


        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("passwordderivedballoon.init");
            // Load configuration
            // #3563: Convert password to byte[] immediately; do not retain the string form.
            if (Configuration.TryGetValue("Password", out var pwdObj) && pwdObj is string pwd)
            {
                _masterPasswordBytes = Encoding.UTF8.GetBytes(pwd);
                // Note: the string 'pwd' cannot be zeroed in managed code, but we avoid storing it as a field
            }
            if (Configuration.TryGetValue("PasswordEnvVar", out var envObj) && envObj is string envVar)
            {
                var envPwd = Environment.GetEnvironmentVariable(envVar);
                if (!string.IsNullOrEmpty(envPwd))
                    _masterPasswordBytes = Encoding.UTF8.GetBytes(envPwd);
            }
            if (Configuration.TryGetValue("SpaceCost", out var sObj) && sObj is int s)
                _config.SpaceCost = s;
            if (Configuration.TryGetValue("TimeCost", out var tObj) && tObj is int t)
                _config.TimeCost = t;
            if (Configuration.TryGetValue("Delta", out var dObj) && dObj is int d)
                _config.Delta = d;
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
            if (_masterPasswordBytes == null || _masterPasswordBytes.Length == 0)
                return false;

            try
            {
                var testSalt = RandomNumberGenerator.GetBytes(16);
                var _ = await DeriveKeyAsync(GetPasswordBytes(), testSalt, 32, cancellationToken);
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
                    throw new KeyNotFoundException($"Key '{keyId}' not found in Balloon key store.");
                }

                // Derive the wrapping key using stored salt and parameters
                var wrappingKey = await DeriveKeyAsync(
                    GetPasswordBytes(),
                    encryptedData.Salt,
                    32,
                    CancellationToken.None,
                    encryptedData.SpaceCost,
                    encryptedData.TimeCost,
                    encryptedData.Delta);

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
                var wrappingKey = await DeriveKeyAsync(GetPasswordBytes(), salt, 32, CancellationToken.None);

                // Encrypt the key material
                var (encryptedKey, nonce, tag) = EncryptKeyMaterial(keyData, wrappingKey);

                // Store the encrypted data with parameters
                var encryptedData = new BalloonEncryptedKeyData
                {
                    KeyId = keyId,
                    Salt = salt,
                    EncryptedKey = encryptedKey,
                    Nonce = nonce,
                    Tag = tag,
                    SpaceCost = _config.SpaceCost,
                    TimeCost = _config.TimeCost,
                    Delta = _config.Delta,
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
            // #3571: Take a snapshot under the storage lock to prevent torn reads.
            await _storageLock.WaitAsync(cancellationToken);
            try
            {
                return _storedKeys.Keys.ToList().AsReadOnly();
            }
            finally
            {
                _storageLock.Release();
            }
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

            // Memory usage = SpaceCost * HashOutputSize (32 bytes for SHA-256)
            var memoryUsageBytes = data.SpaceCost * 32L;

            return await Task.FromResult(new KeyMetadata
            {
                KeyId = keyId,
                CreatedAt = data.CreatedAt,
                Version = data.Version,
                IsActive = keyId == _currentKeyId,
                KeySizeBytes = _config.KeySizeBytes,
                Metadata = new Dictionary<string, object>
                {
                    ["Algorithm"] = "Balloon",
                    ["SpaceCost"] = data.SpaceCost,
                    ["TimeCost"] = data.TimeCost,
                    ["Delta"] = data.Delta,
                    ["MemoryUsageKB"] = memoryUsageBytes / 1024,
                    ["SaltSize"] = data.Salt.Length
                }
            });
        }

        /// <summary>
        /// Derives a key using Balloon hashing algorithm.
        /// Implementation based on the original paper specification.
        /// </summary>
        private async Task<byte[]> DeriveKeyAsync(
            byte[] passwordBytes,
            byte[] salt,
            int outputLength,
            CancellationToken cancellationToken,
            int? spaceCost = null,
            int? timeCost = null,
            int? delta = null)
        {
            return await Task.Run(() =>
            {
                var s = spaceCost ?? _config.SpaceCost;
                var t = timeCost ?? _config.TimeCost;
                var d = delta ?? _config.Delta;

                return BalloonHash(
                    passwordBytes,
                    salt,
                    s,
                    t,
                    d,
                    outputLength);
            }, cancellationToken);
        }

        /// <summary>
        /// Core Balloon hashing implementation.
        /// </summary>
        private static byte[] BalloonHash(byte[] password, byte[] salt, int sCost, int tCost, int delta, int outputLength)
        {
            const int HASH_SIZE = 32; // SHA-256 output size

            // Initialize buffer with sCost blocks
            var buffer = new byte[sCost][];
            for (int i = 0; i < sCost; i++)
            {
                buffer[i] = new byte[HASH_SIZE];
            }

            var counter = 0L;

            // Step 1: Expand input into buffer
            // buffer[0] = H(counter++ || password || salt)
            buffer[0] = HashWithCounter(ref counter, password, salt);

            // buffer[i] = H(counter++ || buffer[i-1]) for i > 0
            for (int i = 1; i < sCost; i++)
            {
                buffer[i] = HashWithCounter(ref counter, buffer[i - 1]);
            }

            // Step 2: Mix buffer tCost times
            for (int t = 0; t < tCost; t++)
            {
                for (int m = 0; m < sCost; m++)
                {
                    // Step 2a: Hash previous block
                    var prev = buffer[(m - 1 + sCost) % sCost];
                    buffer[m] = HashWithCounter(ref counter, prev, buffer[m]);

                    // Step 2b: Hash delta pseudo-random blocks
                    // Use a pre-allocated 12-byte buffer for INTS_TO_BLOCK to avoid ~3 allocations per iteration.
                    var idxInput = new byte[12];
                    for (int i = 0; i < delta; i++)
                    {
                        // idx_block = INTS_TO_BLOCK(t, m, i) — written in-place to avoid per-call allocations.
                        BinaryPrimitives.WriteInt32LittleEndian(idxInput.AsSpan(0, 4), t);
                        BinaryPrimitives.WriteInt32LittleEndian(idxInput.AsSpan(4, 4), m);
                        BinaryPrimitives.WriteInt32LittleEndian(idxInput.AsSpan(8, 4), i);

                        // other = INT(H(counter++ || salt || idx_block)) mod sCost
                        var otherHash = HashWithCounter(ref counter, salt, idxInput);
                        var other = (int)(BitConverter.ToUInt32(otherHash, 0) % (uint)sCost);

                        buffer[m] = HashWithCounter(ref counter, buffer[m], buffer[other]);
                    }
                }
            }

            // Step 3: Extract output
            // Output = H(buffer[sCost-1])
            var finalHash = Hash(buffer[sCost - 1]);

            // If output needs to be longer than hash size, use HKDF-like expansion
            if (outputLength <= HASH_SIZE)
            {
                var result = new byte[outputLength];
                Array.Copy(finalHash, result, outputLength);
                return result;
            }
            else
            {
                return ExpandOutput(finalHash, outputLength);
            }
        }

        /// <summary>
        /// Hash function with counter prefix.
        /// </summary>
        private static byte[] HashWithCounter(ref long counter, params byte[][] inputs)
        {
            var digest = new Sha256Digest();

            // Add counter (8 bytes, little-endian)
            var counterBytes = BitConverter.GetBytes(counter++);
            digest.BlockUpdate(counterBytes, 0, counterBytes.Length);

            // Add all inputs
            foreach (var input in inputs)
            {
                digest.BlockUpdate(input, 0, input.Length);
            }

            var result = new byte[digest.GetDigestSize()];
            digest.DoFinal(result, 0);
            return result;
        }

        /// <summary>
        /// Simple hash without counter.
        /// </summary>
        private static byte[] Hash(byte[] input)
        {
            var digest = new Sha256Digest();
            digest.BlockUpdate(input, 0, input.Length);
            var result = new byte[digest.GetDigestSize()];
            digest.DoFinal(result, 0);
            return result;
        }

        /// <summary>
        /// Expands output to desired length using counter mode.
        /// </summary>
        private static byte[] ExpandOutput(byte[] seed, int outputLength)
        {
            const int HASH_SIZE = 32;
            var result = new byte[outputLength];
            var offset = 0;
            var counter = 0;

            while (offset < outputLength)
            {
                var digest = new Sha256Digest();
                digest.BlockUpdate(seed, 0, seed.Length);
                var counterBytes = BitConverter.GetBytes(counter++);
                digest.BlockUpdate(counterBytes, 0, counterBytes.Length);

                var block = new byte[HASH_SIZE];
                digest.DoFinal(block, 0);

                var toCopy = Math.Min(HASH_SIZE, outputLength - offset);
                Array.Copy(block, 0, result, offset, toCopy);
                offset += toCopy;
            }

            return result;
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

        private byte[] GetPasswordBytes()
        {
            if (_masterPasswordBytes == null || _masterPasswordBytes.Length == 0)
            {
                throw new InvalidOperationException(
                    "Master password not configured. Set 'Password' or 'PasswordEnvVar' in configuration.");
            }
            return _masterPasswordBytes;
        }

        private string GetStoragePath()
        {
            if (!string.IsNullOrEmpty(_config.StoragePath))
                return _config.StoragePath;

            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(baseDir, "DataWarehouse", "balloon-keys.json");
        }

        private async Task LoadStoredKeysAsync(CancellationToken cancellationToken)
        {
            var storagePath = GetStoragePath();
            if (!File.Exists(storagePath))
                return;

            try
            {
                var json = await File.ReadAllTextAsync(storagePath, cancellationToken);
                var stored = JsonSerializer.Deserialize<BalloonStoredKeyFile>(json);

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
            var file = new BalloonStoredKeyFile
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
            // #3563: Zero out password bytes on disposal
            if (_masterPasswordBytes != null)
            {
                CryptographicOperations.ZeroMemory(_masterPasswordBytes);
                _masterPasswordBytes = null;
            }
            _storageLock.Dispose();
            base.Dispose();
        }
    }

    #region Configuration and Data Classes

    /// <summary>
    /// Configuration for Balloon password-derived key store.
    /// </summary>
    public class BalloonConfig
    {
        /// <summary>
        /// Space cost (number of blocks in buffer).
        /// Memory usage = SpaceCost * 32 bytes (SHA-256 output).
        /// Default: 16384 (512 KB with SHA-256)
        /// Higher values use more memory and increase security.
        /// </summary>
        public int SpaceCost { get; set; } = 16384;

        /// <summary>
        /// Time cost (number of mixing rounds).
        /// Higher values increase computation time.
        /// Default: 3
        /// </summary>
        public int TimeCost { get; set; } = 3;

        /// <summary>
        /// Delta - number of dependencies per block.
        /// Recommended value from paper: 3
        /// Higher values increase mixing but also computation.
        /// Default: 3
        /// </summary>
        public int Delta { get; set; } = 3;

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
    /// Encrypted key data stored to disk for Balloon strategy.
    /// </summary>
    internal class BalloonEncryptedKeyData
    {
        public string KeyId { get; set; } = "";
        public byte[] Salt { get; set; } = Array.Empty<byte>();
        public byte[] EncryptedKey { get; set; } = Array.Empty<byte>();
        public byte[] Nonce { get; set; } = Array.Empty<byte>();
        public byte[] Tag { get; set; } = Array.Empty<byte>();
        public int SpaceCost { get; set; }
        public int TimeCost { get; set; }
        public int Delta { get; set; }
        public DateTime CreatedAt { get; set; }
        public int Version { get; set; }
    }

    /// <summary>
    /// Storage file structure for Balloon strategy.
    /// </summary>
    internal class BalloonStoredKeyFile
    {
        public string? CurrentKeyId { get; set; }
        public List<BalloonEncryptedKeyData> Keys { get; set; } = new();
    }

    #endregion
}
