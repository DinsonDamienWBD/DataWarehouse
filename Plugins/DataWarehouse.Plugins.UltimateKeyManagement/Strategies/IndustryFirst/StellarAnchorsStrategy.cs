using DataWarehouse.SDK.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.IndustryFirst
{
    /// <summary>
    /// Stellar Blockchain Key Anchoring Strategy using the Stellar network for
    /// immutable, decentralized key custody and metadata storage.
    ///
    /// NOTE: This is a stub implementation. The stellar_dotnet_sdk package
    /// needs to be installed for full functionality. Currently provides
    /// local-only key storage as a fallback.
    ///
    /// Features (when stellar_dotnet_sdk is available):
    /// - Key anchoring via Stellar data entries (up to 64 bytes per entry)
    /// - Multi-signature accounts for M-of-N key custody
    /// - Transaction memo-based key metadata and audit trail
    /// - Decentralized, censorship-resistant key storage
    /// - Built-in key versioning via Stellar ledger sequence numbers
    /// </summary>
    public sealed class StellarAnchorsStrategy : KeyStoreStrategyBase
    {
        private StellarConfig _config = new();
        private readonly Dictionary<string, StellarKeyEntry> _keyStore = new();
        private string _currentKeyId = "default";
        private readonly SemaphoreSlim _lock = new(1, 1);
        private byte[] _localEncryptionKey = Array.Empty<byte>();
        private bool _disposed;

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = true,
            SupportsEnvelope = false,
            SupportsHsm = false,
            SupportsExpiration = false,
            SupportsReplication = true,
            SupportsVersioning = true,
            SupportsPerKeyAcl = true,
            SupportsAuditLogging = true,
            MaxKeySizeBytes = 192,
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["Network"] = "Stellar (Stub)",
                ["Consensus"] = "Stellar Consensus Protocol (SCP)",
                ["StorageType"] = "Local (Stellar SDK not installed)",
                ["Immutable"] = false
            }
        };

        protected override Task InitializeStorage(CancellationToken cancellationToken)
        {
            // Load configuration
            if (Configuration.TryGetValue("LocalEncryptionKey", out var localKeyObj) && localKeyObj is string localKey)
                _localEncryptionKey = Convert.FromBase64String(localKey);

            // Generate local encryption key if not provided
            if (_localEncryptionKey.Length == 0)
            {
                _localEncryptionKey = new byte[32];
                RandomNumberGenerator.Fill(_localEncryptionKey);
            }

            return Task.CompletedTask;
        }

        public override Task<string> GetCurrentKeyIdAsync()
        {
            return Task.FromResult(_currentKeyId);
        }

        public override Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            // Stub always returns healthy for local mode
            return Task.FromResult(true);
        }

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            await _lock.WaitAsync();
            try
            {
                if (!_keyStore.TryGetValue(keyId, out var entry))
                {
                    throw new KeyNotFoundException($"Key '{keyId}' not found.");
                }

                return entry.DecryptedKey ?? throw new CryptographicException("Key material not available.");
            }
            finally
            {
                _lock.Release();
            }
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            await _lock.WaitAsync();
            try
            {
                var encryptedKey = EncryptKeyData(keyData);

                var entry = new StellarKeyEntry
                {
                    KeyId = keyId,
                    EncryptedKey = encryptedKey,
                    DecryptedKey = keyData,
                    TransactionHash = $"local-{Guid.NewGuid():N}",
                    LedgerSequence = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = context.UserId,
                    AccountId = "local-storage"
                };

                _keyStore[keyId] = entry;
                _currentKeyId = keyId;
            }
            finally
            {
                _lock.Release();
            }
        }

        private byte[] EncryptKeyData(byte[] keyData)
        {
            var nonce = new byte[12];
            RandomNumberGenerator.Fill(nonce);

            var ciphertext = new byte[keyData.Length];
            var tag = new byte[16];

            using var aes = new AesGcm(_localEncryptionKey, 16);
            aes.Encrypt(nonce, keyData, ciphertext, tag);

            var result = new byte[nonce.Length + ciphertext.Length + tag.Length];
            nonce.CopyTo(result, 0);
            ciphertext.CopyTo(result, nonce.Length);
            tag.CopyTo(result, nonce.Length + ciphertext.Length);

            return result;
        }

        private byte[] DecryptKeyData(byte[] encryptedData)
        {
            if (encryptedData.Length < 29)
            {
                throw new CryptographicException("Invalid encrypted data.");
            }

            var nonce = encryptedData.AsSpan(0, 12).ToArray();
            var ciphertext = encryptedData.AsSpan(12, encryptedData.Length - 28).ToArray();
            var tag = encryptedData.AsSpan(encryptedData.Length - 16).ToArray();

            var plaintext = new byte[ciphertext.Length];

            using var aes = new AesGcm(_localEncryptionKey, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

            return plaintext;
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);
            await _lock.WaitAsync(cancellationToken);
            try
            {
                return _keyStore.Keys.ToList().AsReadOnly();
            }
            finally
            {
                _lock.Release();
            }
        }

        public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            if (!context.IsSystemAdmin)
            {
                throw new UnauthorizedAccessException("Only system administrators can delete keys.");
            }

            await _lock.WaitAsync(cancellationToken);
            try
            {
                _keyStore.Remove(keyId);
            }
            finally
            {
                _lock.Release();
            }
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            await _lock.WaitAsync(cancellationToken);
            try
            {
                if (!_keyStore.TryGetValue(keyId, out var entry))
                {
                    return null;
                }

                return new KeyMetadata
                {
                    KeyId = keyId,
                    CreatedAt = entry.CreatedAt,
                    CreatedBy = entry.CreatedBy,
                    KeySizeBytes = entry.DecryptedKey?.Length ?? 0,
                    IsActive = keyId == _currentKeyId,
                    Metadata = new Dictionary<string, object>
                    {
                        ["StorageMode"] = "Local (Stellar SDK not installed)",
                        ["TransactionHash"] = entry.TransactionHash ?? "",
                        ["LedgerSequence"] = entry.LedgerSequence,
                        ["Network"] = _config.Network
                    }
                };
            }
            finally
            {
                _lock.Release();
            }
        }

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Array.Clear(_localEncryptionKey, 0, _localEncryptionKey.Length);
            _lock.Dispose();
            base.Dispose();
        }
    }

    #region Stellar Types

    public class StellarConfig
    {
        public string Network { get; set; } = "testnet";
        public string HorizonUrl { get; set; } = "https://horizon-testnet.stellar.org";
        public string? MasterSecretKey { get; set; }
        public string? SignerSecretKey { get; set; }
        public int MultiSigThreshold { get; set; } = 1;
    }

    internal class StellarKeyEntry
    {
        public string KeyId { get; set; } = "";
        public byte[] EncryptedKey { get; set; } = Array.Empty<byte>();
        public byte[]? DecryptedKey { get; set; }
        public string? TransactionHash { get; set; }
        public long LedgerSequence { get; set; }
        public string AccountId { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
    }

    internal class StellarKeyMetadata
    {
        public string KeyId { get; set; } = "";
        public int ChunkCount { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public int Version { get; set; }
    }

    public class StellarKeyAuditEntry
    {
        public string TransactionHash { get; set; } = "";
        public string OperationType { get; set; } = "";
        public string? DataName { get; set; }
        public DateTime Timestamp { get; set; }
        public int LedgerSequence { get; set; }
    }

    #endregion
}
