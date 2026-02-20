using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Features
{
    /// <summary>
    /// D5: Key derivation hierarchy implementing HKDF-based key derivation (RFC 5869).
    /// Supports master key to derived keys hierarchy with purpose labeling and derivation path tracking.
    /// Similar to BIP32 but generalized for all key types, not just cryptocurrency.
    /// </summary>
    public sealed class KeyDerivationHierarchy : IDisposable
    {
        private readonly BoundedDictionary<string, DerivedKeyInfo> _derivedKeys = new BoundedDictionary<string, DerivedKeyInfo>(1000);
        private readonly BoundedDictionary<string, MasterKeyInfo> _masterKeys = new BoundedDictionary<string, MasterKeyInfo>(1000);
        private readonly IKeyStore _keyStore;
        private readonly SemaphoreSlim _derivationLock = new(1, 1);
        private bool _disposed;

        // Derivation path separator
        private const char PathSeparator = '/';
        private const string MasterPrefix = "m";

        /// <summary>
        /// Key purposes for derived keys.
        /// </summary>
        public static class KeyPurpose
        {
            public const string Encryption = "encryption";
            public const string Signing = "signing";
            public const string Authentication = "authentication";
            public const string KeyWrapping = "key-wrapping";
            public const string DataEncryption = "data-encryption";
            public const string KeyEncryption = "key-encryption";
            public const string MacGeneration = "mac-generation";
            public const string TokenGeneration = "token-generation";
            public const string SessionKey = "session-key";
            public const string BackupKey = "backup-key";
        }

        /// <summary>
        /// Creates a new key derivation hierarchy service.
        /// </summary>
        /// <param name="keyStore">The underlying key store for master keys.</param>
        public KeyDerivationHierarchy(IKeyStore keyStore)
        {
            _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        }

        /// <summary>
        /// Registers a master key for derivation.
        /// </summary>
        /// <param name="masterId">Unique identifier for this master key.</param>
        /// <param name="keyId">Key ID in the underlying key store.</param>
        /// <param name="context">Security context.</param>
        public async Task RegisterMasterKeyAsync(
            string masterId,
            string keyId,
            ISecurityContext context,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(masterId);
            ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
            ArgumentNullException.ThrowIfNull(context);

            // Verify key exists in store
            var keyData = await _keyStore.GetKeyAsync(keyId, context);
            if (keyData == null || keyData.Length == 0)
            {
                throw new KeyNotFoundException($"Key '{keyId}' not found in key store.");
            }

            var masterInfo = new MasterKeyInfo
            {
                MasterId = masterId,
                KeyStoreKeyId = keyId,
                RegisteredAt = DateTime.UtcNow,
                RegisteredBy = context.UserId,
                KeySizeBytes = keyData.Length,
                DerivationPath = MasterPrefix
            };

            _masterKeys[masterId] = masterInfo;
        }

        /// <summary>
        /// Derives a key from a master key using HKDF (RFC 5869).
        /// </summary>
        /// <param name="masterId">The master key identifier.</param>
        /// <param name="purpose">The purpose of the derived key.</param>
        /// <param name="context">Security context.</param>
        /// <param name="salt">Optional salt for HKDF (uses purpose if not provided).</param>
        /// <param name="keySizeBytes">Size of derived key in bytes (default: 32 for 256-bit).</param>
        /// <returns>Derived key information including the key material.</returns>
        public async Task<DerivedKeyResult> DeriveKeyAsync(
            string masterId,
            string purpose,
            ISecurityContext context,
            byte[]? salt = null,
            int keySizeBytes = 32,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(masterId);
            ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
            ArgumentNullException.ThrowIfNull(context);

            if (keySizeBytes < 16 || keySizeBytes > 64)
            {
                throw new ArgumentOutOfRangeException(nameof(keySizeBytes),
                    "Key size must be between 16 and 64 bytes.");
            }

            await _derivationLock.WaitAsync(ct);
            try
            {
                if (!_masterKeys.TryGetValue(masterId, out var masterInfo))
                {
                    throw new KeyNotFoundException($"Master key '{masterId}' not registered.");
                }

                // Get master key material
                var masterKey = await _keyStore.GetKeyAsync(masterInfo.KeyStoreKeyId, context);

                // Build derivation path
                var derivationPath = BuildDerivationPath(masterId, purpose);

                // Build info for HKDF (includes purpose and path for domain separation)
                var info = BuildHkdfInfo(purpose, derivationPath, context);

                // Use purpose as salt if not provided
                var hkdfSalt = salt ?? Encoding.UTF8.GetBytes($"dw-kdf-{purpose}");

                // Derive key using HKDF
                var derivedKey = HkdfDerive(masterKey, hkdfSalt, info, keySizeBytes);

                // Generate derived key ID
                var derivedKeyId = GenerateDerivedKeyId(masterId, purpose);

                // Store derived key info
                var derivedInfo = new DerivedKeyInfo
                {
                    DerivedKeyId = derivedKeyId,
                    MasterId = masterId,
                    Purpose = purpose,
                    DerivationPath = derivationPath,
                    DerivedAt = DateTime.UtcNow,
                    DerivedBy = context.UserId,
                    KeySizeBytes = keySizeBytes,
                    SaltUsed = Convert.ToBase64String(hkdfSalt),
                    DerivationLevel = GetDerivationLevel(derivationPath)
                };

                _derivedKeys[derivedKeyId] = derivedInfo;
                masterInfo.DerivedKeyCount++;

                return new DerivedKeyResult
                {
                    DerivedKeyId = derivedKeyId,
                    KeyMaterial = derivedKey,
                    Purpose = purpose,
                    DerivationPath = derivationPath,
                    MasterId = masterId,
                    DerivedAt = derivedInfo.DerivedAt,
                    KeySizeBytes = keySizeBytes
                };
            }
            finally
            {
                _derivationLock.Release();
            }
        }

        /// <summary>
        /// Derives a child key from an already derived key (hierarchical derivation).
        /// </summary>
        public async Task<DerivedKeyResult> DeriveChildKeyAsync(
            string parentDerivedKeyId,
            string childPurpose,
            ISecurityContext context,
            byte[]? salt = null,
            int keySizeBytes = 32,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(parentDerivedKeyId);
            ArgumentException.ThrowIfNullOrWhiteSpace(childPurpose);

            await _derivationLock.WaitAsync(ct);
            try
            {
                if (!_derivedKeys.TryGetValue(parentDerivedKeyId, out var parentInfo))
                {
                    throw new KeyNotFoundException($"Parent derived key '{parentDerivedKeyId}' not found.");
                }

                if (!_masterKeys.TryGetValue(parentInfo.MasterId, out var masterInfo))
                {
                    throw new KeyNotFoundException($"Master key '{parentInfo.MasterId}' not found.");
                }

                // Get master key material
                var masterKey = await _keyStore.GetKeyAsync(masterInfo.KeyStoreKeyId, context);

                // Build full derivation path from master through parent to child
                var derivationPath = $"{parentInfo.DerivationPath}{PathSeparator}{childPurpose}";

                // Build info with full path for HKDF
                var info = BuildHkdfInfo(childPurpose, derivationPath, context);

                // Use child purpose as salt if not provided
                var hkdfSalt = salt ?? Encoding.UTF8.GetBytes($"dw-kdf-{childPurpose}-child");

                // Derive key using HKDF from master (ensures reproducibility)
                // In practice, we derive from the full path from master
                var pathComponents = derivationPath.Split(PathSeparator);
                var currentKey = masterKey;

                // Derive through each level
                foreach (var component in pathComponents.Skip(1)) // Skip 'm'
                {
                    var levelInfo = Encoding.UTF8.GetBytes($"dw-kdf-level-{component}");
                    var levelSalt = Encoding.UTF8.GetBytes($"dw-salt-{component}");
                    currentKey = HkdfDerive(currentKey, levelSalt, levelInfo, keySizeBytes);
                }

                // Generate child key ID
                var childKeyId = GenerateDerivedKeyId(parentInfo.MasterId, $"{parentInfo.Purpose}-{childPurpose}");

                // Store derived key info
                var childInfo = new DerivedKeyInfo
                {
                    DerivedKeyId = childKeyId,
                    MasterId = parentInfo.MasterId,
                    ParentDerivedKeyId = parentDerivedKeyId,
                    Purpose = childPurpose,
                    DerivationPath = derivationPath,
                    DerivedAt = DateTime.UtcNow,
                    DerivedBy = context.UserId,
                    KeySizeBytes = keySizeBytes,
                    SaltUsed = Convert.ToBase64String(hkdfSalt),
                    DerivationLevel = GetDerivationLevel(derivationPath)
                };

                _derivedKeys[childKeyId] = childInfo;
                parentInfo.ChildKeyCount++;

                return new DerivedKeyResult
                {
                    DerivedKeyId = childKeyId,
                    KeyMaterial = currentKey,
                    Purpose = childPurpose,
                    DerivationPath = derivationPath,
                    MasterId = parentInfo.MasterId,
                    ParentKeyId = parentDerivedKeyId,
                    DerivedAt = childInfo.DerivedAt,
                    KeySizeBytes = keySizeBytes
                };
            }
            finally
            {
                _derivationLock.Release();
            }
        }

        /// <summary>
        /// Re-derives a key using stored derivation parameters.
        /// Useful for key recovery without storing derived key material.
        /// </summary>
        public async Task<byte[]> RederiveKeyAsync(
            string derivedKeyId,
            ISecurityContext context,
            CancellationToken ct = default)
        {
            if (!_derivedKeys.TryGetValue(derivedKeyId, out var derivedInfo))
            {
                throw new KeyNotFoundException($"Derived key '{derivedKeyId}' not found.");
            }

            if (!_masterKeys.TryGetValue(derivedInfo.MasterId, out var masterInfo))
            {
                throw new KeyNotFoundException($"Master key '{derivedInfo.MasterId}' not found.");
            }

            // Get master key material
            var masterKey = await _keyStore.GetKeyAsync(masterInfo.KeyStoreKeyId, context);

            // Re-derive using stored path
            var pathComponents = derivedInfo.DerivationPath.Split(PathSeparator);
            var currentKey = masterKey;

            foreach (var component in pathComponents.Skip(1)) // Skip 'm'
            {
                var levelInfo = Encoding.UTF8.GetBytes($"dw-kdf-level-{component}");
                var levelSalt = Encoding.UTF8.GetBytes($"dw-salt-{component}");
                currentKey = HkdfDerive(currentKey, levelSalt, levelInfo, derivedInfo.KeySizeBytes);
            }

            return currentKey;
        }

        /// <summary>
        /// Gets the derivation path for a derived key.
        /// </summary>
        public DerivationPathInfo? GetDerivationPath(string derivedKeyId)
        {
            if (!_derivedKeys.TryGetValue(derivedKeyId, out var info))
            {
                return null;
            }

            return new DerivationPathInfo
            {
                DerivedKeyId = derivedKeyId,
                FullPath = info.DerivationPath,
                Components = info.DerivationPath.Split(PathSeparator).ToList(),
                Level = info.DerivationLevel,
                MasterId = info.MasterId,
                Purpose = info.Purpose,
                ParentKeyId = info.ParentDerivedKeyId
            };
        }

        /// <summary>
        /// Lists all derived keys from a master key.
        /// </summary>
        public IReadOnlyList<DerivedKeyInfo> GetDerivedKeys(string masterId)
        {
            return _derivedKeys.Values
                .Where(d => d.MasterId == masterId)
                .OrderBy(d => d.DerivationLevel)
                .ThenBy(d => d.DerivedAt)
                .ToList()
                .AsReadOnly();
        }

        /// <summary>
        /// Gets all keys derived for a specific purpose.
        /// </summary>
        public IReadOnlyList<DerivedKeyInfo> GetKeysByPurpose(string purpose)
        {
            return _derivedKeys.Values
                .Where(d => d.Purpose.Equals(purpose, StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => d.DerivedAt)
                .ToList()
                .AsReadOnly();
        }

        /// <summary>
        /// Gets the hierarchy tree for a master key.
        /// </summary>
        public KeyHierarchyTree GetHierarchyTree(string masterId)
        {
            if (!_masterKeys.TryGetValue(masterId, out var masterInfo))
            {
                throw new KeyNotFoundException($"Master key '{masterId}' not found.");
            }

            var tree = new KeyHierarchyTree
            {
                MasterId = masterId,
                KeyStoreKeyId = masterInfo.KeyStoreKeyId,
                RegisteredAt = masterInfo.RegisteredAt
            };

            // Build tree from derived keys
            var derivedKeys = _derivedKeys.Values
                .Where(d => d.MasterId == masterId)
                .OrderBy(d => d.DerivationLevel)
                .ToList();

            foreach (var derived in derivedKeys)
            {
                var node = new KeyHierarchyNode
                {
                    KeyId = derived.DerivedKeyId,
                    Purpose = derived.Purpose,
                    DerivationPath = derived.DerivationPath,
                    Level = derived.DerivationLevel,
                    DerivedAt = derived.DerivedAt,
                    ParentKeyId = derived.ParentDerivedKeyId
                };

                if (string.IsNullOrEmpty(derived.ParentDerivedKeyId))
                {
                    tree.DirectChildren.Add(node);
                }
                else
                {
                    // Find parent and add as child
                    var parent = FindNode(tree.DirectChildren, derived.ParentDerivedKeyId);
                    parent?.Children.Add(node);
                }
            }

            return tree;
        }

        /// <summary>
        /// Validates a derivation path.
        /// </summary>
        public bool ValidateDerivationPath(string derivationPath)
        {
            if (string.IsNullOrWhiteSpace(derivationPath))
                return false;

            var components = derivationPath.Split(PathSeparator);

            // Must start with master prefix
            if (components.Length == 0 || components[0] != MasterPrefix)
                return false;

            // All components must be non-empty
            return components.All(c => !string.IsNullOrWhiteSpace(c));
        }

        /// <summary>
        /// Removes a derived key from tracking.
        /// </summary>
        public bool RemoveDerivedKey(string derivedKeyId)
        {
            if (_derivedKeys.TryRemove(derivedKeyId, out var info))
            {
                if (_masterKeys.TryGetValue(info.MasterId, out var masterInfo))
                {
                    masterInfo.DerivedKeyCount--;
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// Removes a master key and all its derived keys from tracking.
        /// </summary>
        public int RemoveMasterKey(string masterId)
        {
            var removedCount = 0;

            // Remove all derived keys
            var derivedKeyIds = _derivedKeys.Values
                .Where(d => d.MasterId == masterId)
                .Select(d => d.DerivedKeyId)
                .ToList();

            foreach (var keyId in derivedKeyIds)
            {
                if (_derivedKeys.TryRemove(keyId, out _))
                {
                    removedCount++;
                }
            }

            // Remove master
            _masterKeys.TryRemove(masterId, out _);

            return removedCount;
        }

        /// <summary>
        /// Gets statistics about the key hierarchy.
        /// </summary>
        public KeyHierarchyStatistics GetStatistics()
        {
            return new KeyHierarchyStatistics
            {
                TotalMasterKeys = _masterKeys.Count,
                TotalDerivedKeys = _derivedKeys.Count,
                MaxDerivationDepth = _derivedKeys.Values.Any()
                    ? _derivedKeys.Values.Max(d => d.DerivationLevel)
                    : 0,
                KeysByPurpose = _derivedKeys.Values
                    .GroupBy(d => d.Purpose)
                    .ToDictionary(g => g.Key, g => g.Count()),
                KeysByMaster = _masterKeys.ToDictionary(
                    m => m.Key,
                    m => _derivedKeys.Values.Count(d => d.MasterId == m.Key))
            };
        }

        #region HKDF Implementation (RFC 5869)

        /// <summary>
        /// HKDF-Expand using HMAC-SHA256.
        /// </summary>
        private byte[] HkdfDerive(byte[] ikm, byte[] salt, byte[] info, int outputLength)
        {
            // HKDF-Extract
            var prk = HkdfExtract(salt, ikm);

            // HKDF-Expand
            return HkdfExpand(prk, info, outputLength);
        }

        /// <summary>
        /// HKDF-Extract: PRK = HMAC-Hash(salt, IKM)
        /// </summary>
        private byte[] HkdfExtract(byte[] salt, byte[] ikm)
        {
            using var hmac = new HMACSHA256(salt);
            return hmac.ComputeHash(ikm);
        }

        /// <summary>
        /// HKDF-Expand: OKM = T(1) | T(2) | ... | T(N)
        /// where T(i) = HMAC-Hash(PRK, T(i-1) | info | i)
        /// </summary>
        private byte[] HkdfExpand(byte[] prk, byte[] info, int outputLength)
        {
            var hashLength = 32; // SHA-256 output length
            var iterations = (int)Math.Ceiling((double)outputLength / hashLength);

            if (iterations > 255)
            {
                throw new ArgumentException("Output length too large for HKDF-Expand.");
            }

            using var hmac = new HMACSHA256(prk);
            var output = new byte[iterations * hashLength];
            var tPrevious = Array.Empty<byte>();

            for (int i = 1; i <= iterations; i++)
            {
                // T(i) = HMAC-Hash(PRK, T(i-1) | info | i)
                var input = new byte[tPrevious.Length + info.Length + 1];
                Buffer.BlockCopy(tPrevious, 0, input, 0, tPrevious.Length);
                Buffer.BlockCopy(info, 0, input, tPrevious.Length, info.Length);
                input[^1] = (byte)i;

                tPrevious = hmac.ComputeHash(input);
                Buffer.BlockCopy(tPrevious, 0, output, (i - 1) * hashLength, hashLength);
            }

            // Truncate to desired length
            var result = new byte[outputLength];
            Buffer.BlockCopy(output, 0, result, 0, outputLength);
            return result;
        }

        #endregion

        #region Helper Methods

        private string BuildDerivationPath(string masterId, string purpose)
        {
            return $"{MasterPrefix}{PathSeparator}{purpose}";
        }

        private byte[] BuildHkdfInfo(string purpose, string derivationPath, ISecurityContext context)
        {
            // Include context information for domain separation
            var infoString = $"DataWarehouse|{purpose}|{derivationPath}|{context.TenantId ?? "default"}";
            return Encoding.UTF8.GetBytes(infoString);
        }

        private string GenerateDerivedKeyId(string masterId, string purpose)
        {
            var timestamp = DateTime.UtcNow.Ticks;
            var randomPart = Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
            return $"dk-{masterId}-{purpose}-{timestamp:X}-{randomPart}".ToLowerInvariant();
        }

        private int GetDerivationLevel(string derivationPath)
        {
            // Level is the number of path components minus 1 (for 'm')
            return derivationPath.Split(PathSeparator).Length - 1;
        }

        private KeyHierarchyNode? FindNode(List<KeyHierarchyNode> nodes, string keyId)
        {
            foreach (var node in nodes)
            {
                if (node.KeyId == keyId)
                    return node;

                var found = FindNode(node.Children, keyId);
                if (found != null)
                    return found;
            }
            return null;
        }

        #endregion

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _derivedKeys.Clear();
            _masterKeys.Clear();
            _derivationLock.Dispose();

            GC.SuppressFinalize(this);
        }
    }

    #region Supporting Types

    /// <summary>
    /// Information about a registered master key.
    /// </summary>
    public class MasterKeyInfo
    {
        public string MasterId { get; set; } = "";
        public string KeyStoreKeyId { get; set; } = "";
        public DateTime RegisteredAt { get; set; }
        public string? RegisteredBy { get; set; }
        public int KeySizeBytes { get; set; }
        public string DerivationPath { get; set; } = "m";
        public int DerivedKeyCount { get; set; }
    }

    /// <summary>
    /// Information about a derived key.
    /// </summary>
    public class DerivedKeyInfo
    {
        public string DerivedKeyId { get; set; } = "";
        public string MasterId { get; set; } = "";
        public string? ParentDerivedKeyId { get; set; }
        public string Purpose { get; set; } = "";
        public string DerivationPath { get; set; } = "";
        public DateTime DerivedAt { get; set; }
        public string? DerivedBy { get; set; }
        public int KeySizeBytes { get; set; }
        public string? SaltUsed { get; set; }
        public int DerivationLevel { get; set; }
        public int ChildKeyCount { get; set; }
    }

    /// <summary>
    /// Result of a key derivation operation.
    /// </summary>
    public class DerivedKeyResult
    {
        public string DerivedKeyId { get; set; } = "";
        public byte[] KeyMaterial { get; set; } = Array.Empty<byte>();
        public string Purpose { get; set; } = "";
        public string DerivationPath { get; set; } = "";
        public string MasterId { get; set; } = "";
        public string? ParentKeyId { get; set; }
        public DateTime DerivedAt { get; set; }
        public int KeySizeBytes { get; set; }
    }

    /// <summary>
    /// Derivation path information.
    /// </summary>
    public class DerivationPathInfo
    {
        public string DerivedKeyId { get; set; } = "";
        public string FullPath { get; set; } = "";
        public List<string> Components { get; set; } = new();
        public int Level { get; set; }
        public string MasterId { get; set; } = "";
        public string Purpose { get; set; } = "";
        public string? ParentKeyId { get; set; }
    }

    /// <summary>
    /// Tree representation of key hierarchy.
    /// </summary>
    public class KeyHierarchyTree
    {
        public string MasterId { get; set; } = "";
        public string KeyStoreKeyId { get; set; } = "";
        public DateTime RegisteredAt { get; set; }
        public List<KeyHierarchyNode> DirectChildren { get; set; } = new();
    }

    /// <summary>
    /// Node in the key hierarchy tree.
    /// </summary>
    public class KeyHierarchyNode
    {
        public string KeyId { get; set; } = "";
        public string Purpose { get; set; } = "";
        public string DerivationPath { get; set; } = "";
        public int Level { get; set; }
        public DateTime DerivedAt { get; set; }
        public string? ParentKeyId { get; set; }
        public List<KeyHierarchyNode> Children { get; set; } = new();
    }

    /// <summary>
    /// Statistics about the key hierarchy.
    /// </summary>
    public class KeyHierarchyStatistics
    {
        public int TotalMasterKeys { get; set; }
        public int TotalDerivedKeys { get; set; }
        public int MaxDerivationDepth { get; set; }
        public Dictionary<string, int> KeysByPurpose { get; set; } = new();
        public Dictionary<string, int> KeysByMaster { get; set; } = new();
    }

    #endregion
}
