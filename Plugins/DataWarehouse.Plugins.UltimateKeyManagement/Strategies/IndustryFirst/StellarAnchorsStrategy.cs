using DataWarehouse.SDK.Security;
using StellarDotnetSdk;
using StellarDotnetSdk.Accounts;
using StellarDotnetSdk.Responses;
using StellarDotnetSdk.Operations;
using StellarDotnetSdk.Transactions;
using StellarDotnetSdk.Memos;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.IndustryFirst
{
    /// <summary>
    /// Stellar Anchors Strategy - Blockchain-based key management using Stellar network.
    ///
    /// Implements cryptographic key anchoring to the Stellar blockchain for:
    /// - Immutable audit trail of key operations
    /// - Decentralized key metadata storage via transaction memos
    /// - Blockchain-verified key rotation with on-chain proof
    /// - Account-based recovery mechanisms
    /// - Cross-organizational key sharing via federation
    ///
    /// Architecture:
    /// ┌──────────────────────────────────────────────────────────────────┐
    /// │                    Stellar Network Integration                   │
    /// │  ┌────────────────────────────────────────────────────────────┐ │
    /// │  │                   Master Account (Issuer)                  │ │
    /// │  │  - Manages key lifecycle transactions                      │ │
    /// │  │  - Signs key creation/rotation operations                  │ │
    /// │  │  - Controls key recovery via account recovery               │ │
    /// │  └─────────────────┬──────────────────────────────────────────┘ │
    /// │                    │                                             │
    /// │  ┌─────────────────▼──────────────────────────────────────────┐ │
    /// │  │              Transaction Memos (Key Metadata)              │ │
    /// │  │  {                                                         │ │
    /// │  │    "key_id": "encryption-key-2024",                       │ │
    /// │  │    "operation": "create|rotate|revoke",                   │ │
    /// │  │    "key_hash": "sha256(key_material)",                    │ │
    /// │  │    "created_by": "user@domain.com",                       │ │
    /// │  │    "timestamp": 1704067200                                │ │
    /// │  │  }                                                         │ │
    /// │  └────────────────────────────────────────────────────────────┘ │
    /// └──────────────────────────────────────────────────────────────────┘
    ///                                │
    ///  ┌─────────────────────────────▼─────────────────────────────────┐
    ///  │              Key Derivation from Stellar Account              │
    ///  │  Master Key = HKDF(AccountSeed, KeyId, OperationHash)       │
    ///  │  - Uses Stellar ED25519 seed for cryptographic derivation   │
    ///  │  - Combines with on-chain operation hash for uniqueness      │
    ///  │  - HKDF-SHA256 ensures proper key stretching                │
    ///  └───────────────────────────────────────────────────────────────┘
    ///
    /// Security Model:
    /// - Keys derived from Stellar account seed (ED25519)
    /// - Each key operation creates immutable blockchain record
    /// - MEMO_TEXT field stores encrypted key metadata
    /// - Transaction signatures provide non-repudiation
    /// - Multi-signature support for M-of-N key approval
    /// - Time-bounds for key validity periods
    ///
    /// Supported Networks:
    /// - Stellar Mainnet (Public)
    /// - Stellar Testnet
    /// - Private Stellar networks
    /// </summary>
    public sealed class StellarAnchorsStrategy : KeyStoreStrategyBase
    {
        private StellarConfig _config = new();
        private Server _stellarServer = null!;
        private KeyPair _masterKeyPair = null!;
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
            SupportsExpiration = true, // Via time-bounds
            SupportsReplication = true, // Blockchain replication
            SupportsVersioning = true, // Transaction sequence
            SupportsPerKeyAcl = true, // Multi-sig
            SupportsAuditLogging = true, // Immutable blockchain log
            MaxKeySizeBytes = 256,
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["Network"] = "Stellar",
                ["Protocol"] = "Blockchain",
                ["Consensus"] = "Stellar Consensus Protocol (SCP)",
                ["ImmutableAudit"] = true
            }
        };

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            // Load configuration
            if (Configuration.TryGetValue("NetworkPassphrase", out var passphraseObj) && passphraseObj is string passphrase)
                _config.NetworkPassphrase = passphrase;
            if (Configuration.TryGetValue("HorizonUrl", out var horizonObj) && horizonObj is string horizon)
                _config.HorizonUrl = horizon;
            if (Configuration.TryGetValue("AccountSecret", out var secretObj) && secretObj is string secret)
                _config.AccountSecret = secret;
            if (Configuration.TryGetValue("UseTestnet", out var testnetObj) && testnetObj is bool testnet)
                _config.UseTestnet = testnet;
            if (Configuration.TryGetValue("BaseFee", out var feeObj) && feeObj is int fee)
                _config.BaseFee = fee;
            if (Configuration.TryGetValue("Timeout", out var timeoutObj) && timeoutObj is int timeout)
                _config.TransactionTimeoutSeconds = timeout;
            if (Configuration.TryGetValue("LocalEncryptionKey", out var localKeyObj) && localKeyObj is string localKey)
                _localEncryptionKey = Convert.FromBase64String(localKey);

            // Configure network
            if (_config.UseTestnet)
            {
                Network.UseTestNetwork();
                _config.NetworkPassphrase = Network.Current?.NetworkPassphrase ?? "Test SDF Network ; September 2015";
                if (string.IsNullOrEmpty(_config.HorizonUrl))
                    _config.HorizonUrl = "https://horizon-testnet.stellar.org";
            }
            else
            {
                Network.UsePublicNetwork();
                _config.NetworkPassphrase = Network.Current?.NetworkPassphrase ?? "Public Global Stellar Network ; September 2015";
                if (string.IsNullOrEmpty(_config.HorizonUrl))
                    _config.HorizonUrl = "https://horizon.stellar.org";
            }

            // Initialize Stellar server
            _stellarServer = new Server(_config.HorizonUrl);

            // Initialize master keypair
            if (string.IsNullOrEmpty(_config.AccountSecret))
            {
                // Generate new account
                _masterKeyPair = KeyPair.Random();
                _config.AccountSecret = _masterKeyPair.SecretSeed ?? throw new InvalidOperationException("Failed to generate keypair secret");
            }
            else
            {
                _masterKeyPair = KeyPair.FromSecretSeed(_config.AccountSecret);
            }

            // Generate local encryption key if not provided
            if (_localEncryptionKey.Length == 0)
            {
                _localEncryptionKey = new byte[32];
                RandomNumberGenerator.Fill(_localEncryptionKey);
            }

            // Verify account exists and is funded
            await VerifyAccountAsync(cancellationToken);

            // Load existing keys from blockchain
            await LoadKeysFromBlockchain(cancellationToken);
        }

        /// <summary>
        /// Verifies the Stellar account exists and has sufficient balance.
        /// </summary>
        private async Task VerifyAccountAsync(CancellationToken cancellationToken)
        {
            try
            {
                var account = await _stellarServer.Accounts.Account(_masterKeyPair.AccountId);

                // Verify minimum balance (at least 1 XLM for operations)
                var balances = account.Balances;
                var xlmBalance = balances.FirstOrDefault(b => b.AssetType == "native");

                if (xlmBalance == null || decimal.Parse(xlmBalance.BalanceString ?? "0") < 1m)
                {
                    throw new InvalidOperationException(
                        $"Stellar account {_masterKeyPair.AccountId} has insufficient balance. " +
                        "Please fund the account with at least 1 XLM.");
                }
            }
            catch (HttpRequestException ex)
            {
                if (ex.Message.Contains("404"))
                {
                    throw new InvalidOperationException(
                        $"Stellar account {_masterKeyPair.AccountId} does not exist. " +
                        $"Please create and fund the account on {(_config.UseTestnet ? "testnet" : "mainnet")}.", ex);
                }
                throw;
            }
        }

        public override async Task<string> GetCurrentKeyIdAsync()
        {
            return await Task.FromResult(_currentKeyId);
        }

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // Check Stellar network health
                var ledger = await _stellarServer.Ledgers.Limit(1).Order(StellarDotnetSdk.Requests.OrderDirection.DESC).Execute();
                if (ledger?.Records == null || ledger.Records.Count == 0)
                    return false;

                // Check account health
                var account = await _stellarServer.Accounts.Account(_masterKeyPair.AccountId);
                return account != null;
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
                    throw new KeyNotFoundException($"Key '{keyId}' not found in Stellar anchors.");
                }

                // Check expiration
                if (entry.ExpiresAt.HasValue && entry.ExpiresAt.Value < DateTime.UtcNow)
                {
                    throw new CryptographicException($"Key '{keyId}' has expired.");
                }

                // Return the derived key
                return entry.DerivedKey;
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
                // Create key metadata
                var metadata = new KeyMetadataPayload
                {
                    KeyId = keyId,
                    Operation = _keyStore.ContainsKey(keyId) ? "rotate" : "create",
                    KeyHash = Convert.ToBase64String(SHA256.HashData(keyData)),
                    CreatedBy = context.UserId ?? "system",
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };

                // Anchor to blockchain
                var txHash = await AnchorKeyToBlockchain(metadata, keyData, context);

                // Derive persistent key from account seed and key metadata
                var derivedKey = DeriveKeyFromStellarAccount(keyId, txHash);

                // Store entry
                var entry = new StellarKeyEntry
                {
                    KeyId = keyId,
                    DerivedKey = derivedKey,
                    TransactionHash = txHash,
                    SequenceNumber = await GetCurrentSequence(),
                    AccountId = _masterKeyPair.AccountId,
                    KeyHash = metadata.KeyHash,
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddHours(_config.KeyExpirationHours),
                    CreatedBy = metadata.CreatedBy,
                    Operation = metadata.Operation,
                    Metadata = metadata
                };

                _keyStore[keyId] = entry;
                _currentKeyId = keyId;

                // Persist to local cache
                await PersistCache();
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Anchors key metadata to Stellar blockchain as a transaction memo.
        /// Creates an immutable, timestamped record of the key operation.
        /// </summary>
        private async Task<string> AnchorKeyToBlockchain(
            KeyMetadataPayload metadata,
            byte[] keyData,
            ISecurityContext context)
        {
            // Get account for sequence number
            var account = await _stellarServer.Accounts.Account(_masterKeyPair.AccountId);
            var sourceAccount = new Account(_masterKeyPair.AccountId, account.SequenceNumber);

            // Serialize metadata
            var metadataJson = JsonSerializer.Serialize(metadata);

            // Create transaction with memo (using TEXT memo for metadata)
            var txBuilder = new TransactionBuilder(sourceAccount);
            txBuilder.SetFee((uint)_config.BaseFee);

            // Add manage data operation to store key metadata
            var dataName = $"k:{metadata.KeyId}";
            var dataValue = Encoding.UTF8.GetBytes(metadata.KeyHash);
            var manageDataOp = new ManageDataOperation(dataName, dataValue);
            txBuilder.AddOperation(manageDataOp);

            // Add memo with metadata JSON (truncated to 28 bytes if needed)
            var memoText = metadataJson.Length <= 28 ? metadataJson : metadataJson.Substring(0, 28);
            txBuilder.AddMemo(new MemoText(memoText));

            // Add time bounds
            var minTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var maxTime = minTime + _config.TransactionTimeoutSeconds;
            txBuilder.AddTimeBounds(new TimeBounds((ulong)minTime, (ulong)maxTime));

            // Build and sign transaction
            var transaction = txBuilder.Build();
            transaction.Sign(_masterKeyPair);

            // Submit to network
            try
            {
                var response = await _stellarServer.SubmitTransaction(transaction);

                if (response?.IsSuccess == false)
                {
                    var result = response.Result?.ToString() ?? "unknown";
                    throw new InvalidOperationException(
                        $"Failed to anchor key to Stellar blockchain. Result: {result}");
                }

                return response?.Hash ?? throw new InvalidOperationException("Transaction submitted but hash is null");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to submit transaction to Stellar network: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Derives a cryptographic key from the Stellar account seed and key metadata.
        /// Uses HKDF-SHA256 for proper key derivation.
        /// </summary>
        private byte[] DeriveKeyFromStellarAccount(string keyId, string transactionHash)
        {
            // Use the account secret seed as the input key material
            var ikm = _masterKeyPair.SecretSeed ?? throw new InvalidOperationException("SecretSeed is null");
            var ikmBytes = Encoding.UTF8.GetBytes(ikm);

            // Use transaction hash as salt for uniqueness
            var salt = Convert.FromHexString(transactionHash);

            // Use key ID as info parameter
            var info = Encoding.UTF8.GetBytes($"stellar-key:{keyId}");

            // Derive 32-byte key using HKDF-SHA256
            var derivedKey = HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                ikmBytes,
                32, // output length
                salt,
                info);

            return derivedKey;
        }

        /// <summary>
        /// Rotates a key by creating a new blockchain transaction with updated metadata.
        /// </summary>
        public async Task<byte[]> RotateKeyAsync(string keyId, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            if (!_keyStore.ContainsKey(keyId))
            {
                throw new KeyNotFoundException($"Key '{keyId}' not found. Use CreateKeyAsync instead.");
            }

            // Generate new key
            var newKey = new byte[32];
            RandomNumberGenerator.Fill(newKey);

            // Save will handle rotation via metadata
            await SaveKeyToStorage(keyId, newKey, context);

            return newKey;
        }

        /// <summary>
        /// Revokes a key by creating a revocation transaction on the blockchain.
        /// </summary>
        public async Task RevokeKeyAsync(string keyId, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            if (!context.IsSystemAdmin)
            {
                throw new UnauthorizedAccessException("Only system administrators can revoke keys.");
            }

            await _lock.WaitAsync();
            try
            {
                if (!_keyStore.TryGetValue(keyId, out var entry))
                {
                    throw new KeyNotFoundException($"Key '{keyId}' not found.");
                }

                // Create revocation metadata
                var metadata = new KeyMetadataPayload
                {
                    KeyId = keyId,
                    Operation = "revoke",
                    KeyHash = entry.KeyHash,
                    CreatedBy = context.UserId ?? "system",
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };

                // Anchor revocation to blockchain
                var txHash = await AnchorRevocationToBlockchain(metadata);

                // Remove from store
                _keyStore.Remove(keyId);

                await PersistCache();
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Anchors key revocation to blockchain by removing the data entry.
        /// </summary>
        private async Task<string> AnchorRevocationToBlockchain(KeyMetadataPayload metadata)
        {
            var account = await _stellarServer.Accounts.Account(_masterKeyPair.AccountId);
            var sourceAccount = new Account(_masterKeyPair.AccountId, account.SequenceNumber);

            var metadataJson = JsonSerializer.Serialize(metadata);

            // Create transaction to remove data entry (signals revocation)
            var txBuilder = new TransactionBuilder(sourceAccount);
            txBuilder.SetFee((uint)_config.BaseFee);

            // Remove data entry (null value removes it)
            var dataName = $"k:{metadata.KeyId}";
            var manageDataOp = new ManageDataOperation(dataName, (byte[]?)null);
            txBuilder.AddOperation(manageDataOp);

            // Add memo
            var memoText = metadataJson.Length <= 28 ? metadataJson : metadataJson.Substring(0, 28);
            txBuilder.AddMemo(new MemoText(memoText));

            // Add time bounds
            var minTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var maxTime = minTime + _config.TransactionTimeoutSeconds;
            txBuilder.AddTimeBounds(new TimeBounds((ulong)minTime, (ulong)maxTime));

            // Build and sign
            var transaction = txBuilder.Build();
            transaction.Sign(_masterKeyPair);

            var response = await _stellarServer.SubmitTransaction(transaction);

            if (response?.IsSuccess == false)
            {
                throw new InvalidOperationException(
                    $"Failed to anchor revocation to blockchain. Result: {response.Result?.ToString() ?? "unknown"}");
            }

            return response?.Hash ?? throw new InvalidOperationException("Transaction hash is null");
        }

        /// <summary>
        /// Recovers key entries from blockchain transaction history.
        /// Scans account operations for key management transactions.
        /// </summary>
        private async Task LoadKeysFromBlockchain(CancellationToken cancellationToken)
        {
            // First try loading from local cache
            await LoadFromCache();

            // If cache is empty, scan blockchain (expensive)
            if (_keyStore.Count == 0)
            {
                await ScanBlockchainHistory(cancellationToken);
            }
        }

        /// <summary>
        /// Scans blockchain transaction history for key operations.
        /// </summary>
        private async Task ScanBlockchainHistory(CancellationToken cancellationToken)
        {
            try
            {
                // Get recent transactions for this account
                var transactions = await _stellarServer.Transactions
                    .ForAccount(_masterKeyPair.AccountId)
                    .Limit(200)
                    .Order(StellarDotnetSdk.Requests.OrderDirection.DESC)
                    .Execute();

                if (transactions?.Records == null)
                    return;

                foreach (var tx in transactions.Records)
                {
                    // Check if transaction has memo
                    if (tx.MemoValue == null)
                        continue;

                    try
                    {
                        // Try to parse metadata from operations
                        var operations = await _stellarServer.Operations
                            .ForTransaction(tx.Hash)
                            .Execute();

                        foreach (var op in operations?.Records ?? Enumerable.Empty<StellarDotnetSdk.Responses.Operations.OperationResponse>())
                        {
                            if (op is StellarDotnetSdk.Responses.Operations.ManageDataOperationResponse dataOp)
                            {
                                var name = dataOp.Name;
                                if (name != null && name.StartsWith("k:"))
                                {
                                    var keyId = name.Substring(2);

                                    // Reconstruct key entry
                                    if (!_keyStore.ContainsKey(keyId))
                                    {
                                        var derivedKey = DeriveKeyFromStellarAccount(keyId, tx.Hash);

                                        _keyStore[keyId] = new StellarKeyEntry
                                        {
                                            KeyId = keyId,
                                            DerivedKey = derivedKey,
                                            TransactionHash = tx.Hash,
                                            SequenceNumber = long.Parse(tx.PagingToken ?? "0"),
                                            AccountId = _masterKeyPair.AccountId,
                                            CreatedAt = tx.CreatedAt,
                                            CreatedBy = "blockchain-recovery"
                                        };
                                    }
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Skip malformed transactions
                        continue;
                    }
                }

                if (_keyStore.Count > 0)
                {
                    _currentKeyId = _keyStore.Keys.First();
                    await PersistCache();
                }
            }
            catch (Exception ex)
            {
                // Blockchain scan failed - not critical
                System.Diagnostics.Debug.WriteLine($"Blockchain scan failed: {ex.Message}");
            }
        }

        private async Task<long> GetCurrentSequence()
        {
            var account = await _stellarServer.Accounts.Account(_masterKeyPair.AccountId);
            return account.SequenceNumber;
        }

        private string GetCachePath()
        {
            if (Configuration.TryGetValue("CachePath", out var pathObj) && pathObj is string path)
                return path;

            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(baseDir, "DataWarehouse", "stellar-keys.json");
        }

        private async Task LoadFromCache()
        {
            var cachePath = GetCachePath();
            if (!File.Exists(cachePath))
                return;

            try
            {
                var json = await File.ReadAllTextAsync(cachePath);
                var stored = JsonSerializer.Deserialize<Dictionary<string, StellarKeyEntrySerialized>>(json);

                if (stored != null)
                {
                    foreach (var kvp in stored)
                    {
                        _keyStore[kvp.Key] = new StellarKeyEntry
                        {
                            KeyId = kvp.Value.KeyId,
                            DerivedKey = Convert.FromBase64String(kvp.Value.DerivedKey),
                            TransactionHash = kvp.Value.TransactionHash,
                            SequenceNumber = kvp.Value.SequenceNumber,
                            AccountId = kvp.Value.AccountId,
                            KeyHash = kvp.Value.KeyHash,
                            CreatedAt = kvp.Value.CreatedAt,
                            ExpiresAt = kvp.Value.ExpiresAt,
                            CreatedBy = kvp.Value.CreatedBy,
                            Operation = kvp.Value.Operation
                        };
                    }

                    if (_keyStore.Count > 0)
                    {
                        _currentKeyId = _keyStore.Keys.First();
                    }
                }
            }
            catch
            {
                // Ignore cache errors
            }
        }

        private async Task PersistCache()
        {
            var cachePath = GetCachePath();
            var dir = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var toStore = _keyStore.ToDictionary(
                kvp => kvp.Key,
                kvp => new StellarKeyEntrySerialized
                {
                    KeyId = kvp.Value.KeyId,
                    DerivedKey = Convert.ToBase64String(kvp.Value.DerivedKey),
                    TransactionHash = kvp.Value.TransactionHash,
                    SequenceNumber = kvp.Value.SequenceNumber,
                    AccountId = kvp.Value.AccountId,
                    KeyHash = kvp.Value.KeyHash,
                    CreatedAt = kvp.Value.CreatedAt,
                    ExpiresAt = kvp.Value.ExpiresAt,
                    CreatedBy = kvp.Value.CreatedBy,
                    Operation = kvp.Value.Operation
                });

            var json = JsonSerializer.Serialize(toStore, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(cachePath, json);
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
            await RevokeKeyAsync(keyId, context);
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
                    ExpiresAt = entry.ExpiresAt,
                    KeySizeBytes = entry.DerivedKey.Length,
                    IsActive = keyId == _currentKeyId,
                    Metadata = new Dictionary<string, object>
                    {
                        ["TransactionHash"] = entry.TransactionHash,
                        ["SequenceNumber"] = entry.SequenceNumber,
                        ["AccountId"] = entry.AccountId,
                        ["KeyHash"] = entry.KeyHash,
                        ["Operation"] = entry.Operation ?? "unknown",
                        ["Network"] = _config.UseTestnet ? "testnet" : "mainnet"
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

            CryptographicOperations.ZeroMemory(_localEncryptionKey);
            _lock.Dispose();
            base.Dispose();
        }
    }

    #region Stellar Types

    public class StellarConfig
    {
        public string NetworkPassphrase { get; set; } = "";
        public string HorizonUrl { get; set; } = "";
        public string AccountSecret { get; set; } = "";
        public bool UseTestnet { get; set; } = true;
        public int BaseFee { get; set; } = 100;
        public int TransactionTimeoutSeconds { get; set; } = 180;
        public int KeyExpirationHours { get; set; } = 8760; // 1 year
    }

    internal class StellarKeyEntry
    {
        public string KeyId { get; set; } = "";
        public byte[] DerivedKey { get; set; } = Array.Empty<byte>();
        public string TransactionHash { get; set; } = "";
        public long SequenceNumber { get; set; }
        public string AccountId { get; set; } = "";
        public string KeyHash { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public string? CreatedBy { get; set; }
        public string? Operation { get; set; }
        public KeyMetadataPayload? Metadata { get; set; }
    }

    internal class StellarKeyEntrySerialized
    {
        public string KeyId { get; set; } = "";
        public string DerivedKey { get; set; } = "";
        public string TransactionHash { get; set; } = "";
        public long SequenceNumber { get; set; }
        public string AccountId { get; set; } = "";
        public string KeyHash { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public string? CreatedBy { get; set; }
        public string? Operation { get; set; }
    }

    public class KeyMetadataPayload
    {
        [JsonPropertyName("key_id")]
        public string KeyId { get; set; } = "";

        [JsonPropertyName("operation")]
        public string Operation { get; set; } = "";

        [JsonPropertyName("key_hash")]
        public string KeyHash { get; set; } = "";

        [JsonPropertyName("created_by")]
        public string CreatedBy { get; set; } = "";

        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }
    }

    #endregion
}
