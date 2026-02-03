using DataWarehouse.SDK.Security;
using stellar_dotnet_sdk;
using stellar_dotnet_sdk.responses;
using stellar_dotnet_sdk.responses.operations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.IndustryFirst
{
    /// <summary>
    /// Stellar Blockchain Key Anchoring Strategy using the Stellar network for
    /// immutable, decentralized key custody and metadata storage.
    ///
    /// Features:
    /// - Key anchoring via Stellar data entries (up to 64 bytes per entry)
    /// - Multi-signature accounts for M-of-N key custody
    /// - Transaction memo-based key metadata and audit trail
    /// - Decentralized, censorship-resistant key storage
    /// - Built-in key versioning via Stellar ledger sequence numbers
    ///
    /// Architecture:
    /// ┌─────────────────────────────────────────────────────────────────┐
    /// │                    Stellar Network                              │
    /// │  ┌────────────────┐  ┌────────────────┐  ┌────────────────┐   │
    /// │  │   Validator    │  │   Validator    │  │   Validator    │   │
    /// │  └───────┬────────┘  └───────┬────────┘  └───────┬────────┘   │
    /// │          │                   │                   │            │
    /// │          └───────────────────┼───────────────────┘            │
    /// │                              │                                 │
    /// │                    ┌─────────┴─────────┐                       │
    /// │                    │   Key Custody     │                       │
    /// │                    │   Account         │                       │
    /// │                    │   (Multi-Sig)     │                       │
    /// │                    └───────────────────┘                       │
    /// └─────────────────────────────────────────────────────────────────┘
    ///                              │
    ///                    ┌─────────┴─────────┐
    ///                    │   This Strategy   │
    ///                    └───────────────────┘
    ///
    /// Security Model:
    /// - Keys are encrypted locally before being anchored on-chain
    /// - Multi-sig accounts require multiple parties to access keys
    /// - Immutable audit trail via blockchain transactions
    /// - Decentralized - no single point of failure
    /// </summary>
    public sealed class StellarAnchorsStrategy : KeyStoreStrategyBase
    {
        private StellarConfig _config = new();
        private Server _stellarServer = null!;
        private KeyPair _masterKeyPair = null!;
        private KeyPair? _signerKeyPair;
        private readonly Dictionary<string, StellarKeyEntry> _keyStore = new();
        private string _currentKeyId = "default";
        private readonly SemaphoreSlim _lock = new(1, 1);
        private byte[] _localEncryptionKey = Array.Empty<byte>();
        private bool _disposed;

        // Stellar data entry name prefix for key storage
        private const string KeyDataPrefix = "dwkey:";
        private const string KeyMetaPrefix = "dwmeta:";
        private const int MaxDataEntrySize = 64; // Stellar limit

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = true,
            SupportsEnvelope = false,
            SupportsHsm = false,
            SupportsExpiration = false, // Blockchain is permanent
            SupportsReplication = true, // Inherent in blockchain
            SupportsVersioning = true, // Via ledger sequence
            SupportsPerKeyAcl = true, // Via multi-sig
            SupportsAuditLogging = true, // Immutable ledger
            MaxKeySizeBytes = 192, // 3 data entries * 64 bytes
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["Network"] = "Stellar",
                ["Consensus"] = "Stellar Consensus Protocol (SCP)",
                ["StorageType"] = "Decentralized Ledger",
                ["Immutable"] = true
            }
        };

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            // Load configuration
            if (Configuration.TryGetValue("Network", out var networkObj) && networkObj is string network)
                _config.Network = network;
            if (Configuration.TryGetValue("HorizonUrl", out var horizonObj) && horizonObj is string horizon)
                _config.HorizonUrl = horizon;
            if (Configuration.TryGetValue("MasterSecretKey", out var masterObj) && masterObj is string master)
                _config.MasterSecretKey = master;
            if (Configuration.TryGetValue("SignerSecretKey", out var signerObj) && signerObj is string signer)
                _config.SignerSecretKey = signer;
            if (Configuration.TryGetValue("MultiSigThreshold", out var thresholdObj) && thresholdObj is int threshold)
                _config.MultiSigThreshold = threshold;
            if (Configuration.TryGetValue("LocalEncryptionKey", out var localKeyObj) && localKeyObj is string localKey)
                _localEncryptionKey = Convert.FromBase64String(localKey);

            // Set network
            Network.Use(_config.Network == "mainnet"
                ? new Network("Public Global Stellar Network ; September 2015")
                : new Network("Test SDF Network ; September 2015"));

            // Initialize Stellar server connection
            _stellarServer = new Server(_config.HorizonUrl);

            // Initialize key pairs
            if (!string.IsNullOrEmpty(_config.MasterSecretKey))
            {
                _masterKeyPair = KeyPair.FromSecretSeed(_config.MasterSecretKey);
            }
            else
            {
                // Generate new master key pair
                _masterKeyPair = KeyPair.Random();
            }

            if (!string.IsNullOrEmpty(_config.SignerSecretKey))
            {
                _signerKeyPair = KeyPair.FromSecretSeed(_config.SignerSecretKey);
            }

            // Generate local encryption key if not provided
            if (_localEncryptionKey.Length == 0)
            {
                _localEncryptionKey = new byte[32];
                RandomNumberGenerator.Fill(_localEncryptionKey);
            }

            // Load existing keys from Stellar account
            await LoadKeysFromStellar(cancellationToken);
        }

        public override async Task<string> GetCurrentKeyIdAsync()
        {
            return await Task.FromResult(_currentKeyId);
        }

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // Check Horizon server connectivity
                var root = _stellarServer.Root();
                return await Task.FromResult(root != null);
            }
            catch
            {
                return false;
            }
        }

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            await _lock.WaitAsync();
            try
            {
                if (!_keyStore.TryGetValue(keyId, out var entry))
                {
                    // Try to load from Stellar
                    var key = await LoadKeyFromStellar(keyId);
                    if (key == null)
                    {
                        throw new KeyNotFoundException($"Key '{keyId}' not found on Stellar network.");
                    }
                    return key;
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
                // Encrypt key data before anchoring
                var encryptedKey = EncryptKeyData(keyData);

                // Anchor key to Stellar
                var txHash = await AnchorKeyToStellar(keyId, encryptedKey, context);

                // Store locally
                var entry = new StellarKeyEntry
                {
                    KeyId = keyId,
                    EncryptedKey = encryptedKey,
                    DecryptedKey = keyData,
                    TransactionHash = txHash,
                    LedgerSequence = await GetLatestLedgerSequence(),
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = context.UserId,
                    AccountId = _masterKeyPair.AccountId
                };

                _keyStore[keyId] = entry;
                _currentKeyId = keyId;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Anchors an encrypted key to the Stellar blockchain via data entries.
        /// </summary>
        private async Task<string> AnchorKeyToStellar(string keyId, byte[] encryptedKey, ISecurityContext context)
        {
            // Get account information
            var account = await _stellarServer.Accounts.Account(_masterKeyPair.AccountId);

            // Build transaction
            var builder = new TransactionBuilder(account);

            // Split encrypted key into chunks (max 64 bytes per data entry)
            var chunks = SplitIntoChunks(encryptedKey, MaxDataEntrySize);
            var keyIdHash = ComputeKeyIdHash(keyId);

            for (int i = 0; i < chunks.Count; i++)
            {
                var dataName = $"{KeyDataPrefix}{keyIdHash}:{i}";
                var dataValue = Convert.ToBase64String(chunks[i]);

                // Truncate to fit Stellar's 64-byte limit
                if (Encoding.UTF8.GetByteCount(dataName) > 64)
                {
                    dataName = dataName.Substring(0, 64);
                }

                builder.AddOperation(new ManageDataOperation.Builder(dataName, Encoding.UTF8.GetBytes(dataValue)).Build());
            }

            // Add metadata entry
            var metadata = new StellarKeyMetadata
            {
                KeyId = keyId,
                ChunkCount = chunks.Count,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = context.UserId,
                Version = 1
            };

            var metadataJson = JsonSerializer.Serialize(metadata);
            var metadataName = $"{KeyMetaPrefix}{keyIdHash}";

            if (metadataJson.Length <= MaxDataEntrySize)
            {
                builder.AddOperation(new ManageDataOperation.Builder(metadataName, Encoding.UTF8.GetBytes(metadataJson)).Build());
            }

            // Add memo for audit trail
            builder.AddMemo(Memo.Text($"KeyStore:{keyId}"));

            // Build and sign transaction
            var tx = builder.Build();
            tx.Sign(_masterKeyPair);

            // If multi-sig is configured, add additional signature
            if (_signerKeyPair != null && _config.MultiSigThreshold > 1)
            {
                tx.Sign(_signerKeyPair);
            }

            // Submit transaction
            var response = await _stellarServer.SubmitTransaction(tx);

            if (!response.IsSuccess())
            {
                var errorMessage = response.ResultXdr ?? "Unknown error";
                throw new InvalidOperationException($"Failed to anchor key to Stellar: {errorMessage}");
            }

            return response.Hash;
        }

        /// <summary>
        /// Loads a key from Stellar data entries.
        /// </summary>
        private async Task<byte[]?> LoadKeyFromStellar(string keyId)
        {
            try
            {
                var account = await _stellarServer.Accounts.Account(_masterKeyPair.AccountId);
                var keyIdHash = ComputeKeyIdHash(keyId);

                // Find metadata entry
                var metadataName = $"{KeyMetaPrefix}{keyIdHash}";
                if (!account.Data.TryGetValue(metadataName, out var metadataBase64))
                {
                    return null;
                }

                var metadataJson = Encoding.UTF8.GetString(Convert.FromBase64String(metadataBase64));
                var metadata = JsonSerializer.Deserialize<StellarKeyMetadata>(metadataJson);

                if (metadata == null)
                {
                    return null;
                }

                // Reassemble key from chunks
                var chunks = new List<byte[]>();
                for (int i = 0; i < metadata.ChunkCount; i++)
                {
                    var dataName = $"{KeyDataPrefix}{keyIdHash}:{i}";
                    if (account.Data.TryGetValue(dataName, out var chunkBase64))
                    {
                        var chunkData = Convert.FromBase64String(chunkBase64);
                        var actualChunk = Convert.FromBase64String(Encoding.UTF8.GetString(chunkData));
                        chunks.Add(actualChunk);
                    }
                }

                if (chunks.Count != metadata.ChunkCount)
                {
                    throw new CryptographicException("Incomplete key data on Stellar.");
                }

                // Combine and decrypt
                var encryptedKey = CombineChunks(chunks);
                var decryptedKey = DecryptKeyData(encryptedKey);

                // Cache locally
                var entry = new StellarKeyEntry
                {
                    KeyId = keyId,
                    EncryptedKey = encryptedKey,
                    DecryptedKey = decryptedKey,
                    AccountId = _masterKeyPair.AccountId,
                    CreatedAt = metadata.CreatedAt,
                    CreatedBy = metadata.CreatedBy
                };
                _keyStore[keyId] = entry;

                return decryptedKey;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Loads all keys from the Stellar account.
        /// </summary>
        private async Task LoadKeysFromStellar(CancellationToken cancellationToken)
        {
            try
            {
                var account = await _stellarServer.Accounts.Account(_masterKeyPair.AccountId);

                // Find all key metadata entries
                foreach (var entry in account.Data)
                {
                    if (entry.Key.StartsWith(KeyMetaPrefix))
                    {
                        try
                        {
                            var metadataJson = Encoding.UTF8.GetString(Convert.FromBase64String(entry.Value));
                            var metadata = JsonSerializer.Deserialize<StellarKeyMetadata>(metadataJson);

                            if (metadata != null)
                            {
                                // Load the key
                                await LoadKeyFromStellar(metadata.KeyId);
                            }
                        }
                        catch
                        {
                            // Skip invalid entries
                        }
                    }
                }

                if (_keyStore.Count > 0)
                {
                    _currentKeyId = _keyStore.Keys.First();
                }
            }
            catch (Exception ex) when (ex.Message.Contains("404") || ex.Message.Contains("Not Found"))
            {
                // Account doesn't exist yet - will be created on first key save
            }
        }

        /// <summary>
        /// Creates a multi-signature custody account for enhanced security.
        /// </summary>
        public async Task<string> CreateMultiSigCustodyAccountAsync(
            ISecurityContext context,
            params string[] signerPublicKeys)
        {
            ValidateSecurityContext(context);

            // Generate new custody account
            var custodyKeyPair = KeyPair.Random();

            // Fund the account (requires existing funded account)
            var sourceAccount = await _stellarServer.Accounts.Account(_masterKeyPair.AccountId);

            var createBuilder = new TransactionBuilder(sourceAccount)
                .AddOperation(new CreateAccountOperation.Builder(custodyKeyPair, "10").Build()) // 10 XLM minimum
                .AddMemo(Memo.Text("KeyCustody"));

            var createTx = createBuilder.Build();
            createTx.Sign(_masterKeyPair);

            var createResponse = await _stellarServer.SubmitTransaction(createTx);
            if (!createResponse.IsSuccess())
            {
                throw new InvalidOperationException("Failed to create custody account.");
            }

            // Configure multi-sig on custody account
            var custodyAccount = await _stellarServer.Accounts.Account(custodyKeyPair.AccountId);

            var multiSigBuilder = new TransactionBuilder(custodyAccount);

            // Add signers
            foreach (var signerPubKey in signerPublicKeys)
            {
                var signerKey = KeyPair.FromAccountId(signerPubKey);
                multiSigBuilder.AddOperation(
                    new SetOptionsOperation.Builder()
                        .SetSigner(stellar_dotnet_sdk.Signer.Ed25519PublicKey(signerKey), 1)
                        .Build());
            }

            // Set thresholds
            multiSigBuilder.AddOperation(
                new SetOptionsOperation.Builder()
                    .SetLowThreshold(_config.MultiSigThreshold)
                    .SetMediumThreshold(_config.MultiSigThreshold)
                    .SetHighThreshold(_config.MultiSigThreshold)
                    .Build());

            var multiSigTx = multiSigBuilder.Build();
            multiSigTx.Sign(custodyKeyPair);

            var multiSigResponse = await _stellarServer.SubmitTransaction(multiSigTx);
            if (!multiSigResponse.IsSuccess())
            {
                throw new InvalidOperationException("Failed to configure multi-sig.");
            }

            return custodyKeyPair.AccountId;
        }

        /// <summary>
        /// Gets the transaction history for key audit trail.
        /// </summary>
        public async Task<IReadOnlyList<StellarKeyAuditEntry>> GetKeyAuditTrailAsync(
            string keyId,
            ISecurityContext context)
        {
            ValidateSecurityContext(context);

            var auditEntries = new List<StellarKeyAuditEntry>();
            var keyIdHash = ComputeKeyIdHash(keyId);

            // Get operations from the account
            var operationsRequest = _stellarServer.Operations
                .ForAccount(_masterKeyPair.AccountId)
                .Limit(100);

            var operations = await operationsRequest.Execute();

            foreach (var op in operations.Records)
            {
                if (op is ManageDataOperationResponse dataOp)
                {
                    if (dataOp.Name.StartsWith($"{KeyDataPrefix}{keyIdHash}") ||
                        dataOp.Name == $"{KeyMetaPrefix}{keyIdHash}")
                    {
                        auditEntries.Add(new StellarKeyAuditEntry
                        {
                            TransactionHash = dataOp.TransactionHash,
                            OperationType = "ManageData",
                            DataName = dataOp.Name,
                            Timestamp = DateTime.TryParse(dataOp.CreatedAt, out var dt) ? dt : DateTime.UtcNow,
                            LedgerSequence = int.TryParse(dataOp.PagingToken, out var seq) ? seq : 0
                        });
                    }
                }
            }

            return auditEntries.AsReadOnly();
        }

        private byte[] EncryptKeyData(byte[] keyData)
        {
            var nonce = new byte[12];
            RandomNumberGenerator.Fill(nonce);

            var ciphertext = new byte[keyData.Length];
            var tag = new byte[16];

            using var aes = new AesGcm(_localEncryptionKey, 16);
            aes.Encrypt(nonce, keyData, ciphertext, tag);

            // Combine: nonce + ciphertext + tag
            var result = new byte[nonce.Length + ciphertext.Length + tag.Length];
            nonce.CopyTo(result, 0);
            ciphertext.CopyTo(result, nonce.Length);
            tag.CopyTo(result, nonce.Length + ciphertext.Length);

            return result;
        }

        private byte[] DecryptKeyData(byte[] encryptedData)
        {
            if (encryptedData.Length < 29) // 12 nonce + 1 min + 16 tag
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

        private string ComputeKeyIdHash(string keyId)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(keyId));
            return Convert.ToHexString(hash).Substring(0, 16).ToLower();
        }

        private List<byte[]> SplitIntoChunks(byte[] data, int chunkSize)
        {
            var chunks = new List<byte[]>();

            for (int i = 0; i < data.Length; i += chunkSize)
            {
                var remaining = Math.Min(chunkSize, data.Length - i);
                var chunk = new byte[remaining];
                Array.Copy(data, i, chunk, 0, remaining);
                chunks.Add(chunk);
            }

            return chunks;
        }

        private byte[] CombineChunks(List<byte[]> chunks)
        {
            var totalLength = chunks.Sum(c => c.Length);
            var result = new byte[totalLength];
            var offset = 0;

            foreach (var chunk in chunks)
            {
                chunk.CopyTo(result, offset);
                offset += chunk.Length;
            }

            return result;
        }

        private Task<long> GetLatestLedgerSequence()
        {
            try
            {
                var root = _stellarServer.Root();
                return Task.FromResult((long)root.HistoryLatestLedger);
            }
            catch
            {
                return Task.FromResult(0L);
            }
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
                throw new UnauthorizedAccessException("Only system administrators can delete Stellar-anchored keys.");
            }

            await _lock.WaitAsync(cancellationToken);
            try
            {
                // Delete data entries from Stellar (set to null)
                var keyIdHash = ComputeKeyIdHash(keyId);
                var account = await _stellarServer.Accounts.Account(_masterKeyPair.AccountId);

                var builder = new TransactionBuilder(account);

                // Find and delete all related data entries
                foreach (var entry in account.Data)
                {
                    if (entry.Key.StartsWith($"{KeyDataPrefix}{keyIdHash}") ||
                        entry.Key == $"{KeyMetaPrefix}{keyIdHash}")
                    {
                        builder.AddOperation(new ManageDataOperation.Builder(entry.Key, null).Build());
                    }
                }

                builder.AddMemo(Memo.Text($"Delete:{keyId}"));

                var tx = builder.Build();
                tx.Sign(_masterKeyPair);

                if (_signerKeyPair != null && _config.MultiSigThreshold > 1)
                {
                    tx.Sign(_signerKeyPair);
                }

                await _stellarServer.SubmitTransaction(tx);

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
                        ["StellarAccountId"] = entry.AccountId,
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

            // Clear sensitive data
            Array.Clear(_localEncryptionKey, 0, _localEncryptionKey.Length);
            _lock.Dispose();
            base.Dispose();
        }
    }

    #region Stellar Types

    public class StellarConfig
    {
        public string Network { get; set; } = "testnet"; // testnet or mainnet
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
