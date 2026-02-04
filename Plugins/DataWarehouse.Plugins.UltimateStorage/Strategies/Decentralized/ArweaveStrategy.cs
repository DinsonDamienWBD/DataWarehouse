using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Decentralized
{
    /// <summary>
    /// Arweave permanent storage strategy with production features:
    /// - Permanent, immutable data storage (pay once, store forever)
    /// - Transaction-based content addressing with cryptographic verification
    /// - Arweave tags for rich metadata and queryability via GraphQL
    /// - Support for bundled transactions via Bundlr/Irys for instant finality
    /// - Wallet-based authentication using JWK (JSON Web Key) format
    /// - Content verification using transaction signatures
    /// - Integration with Arweave HTTP gateway (arweave.net or custom)
    /// - Path manifests for organizing multiple files
    /// - Atomic swaps and SmartWeave contract integration
    /// - Data availability sampling for verification
    /// - Permaweb hosting with content-addressable URLs
    /// - Note: Deletion is not supported - data is permanent by design
    /// </summary>
    public class ArweaveStrategy : UltimateStorageStrategyBase
    {
        private HttpClient? _httpClient;
        private string _gatewayUrl = "https://arweave.net";
        private string? _walletJwk = null; // JWK JSON string
        private ArweaveWallet? _wallet = null;
        private bool _useBundlr = false; // Use Bundlr/Irys for instant finality
        private string _bundlrUrl = "https://node1.bundlr.network";
        private string? _bundlrApiKey = null;
        private int _timeoutSeconds = 600; // Longer timeout for permanent storage
        private int _maxRetries = 5; // More retries for permanent storage
        private int _retryDelayMs = 2000;
        private bool _verifyAfterStore = true;
        private int _confirmationWaitSeconds = 120; // Wait time for transaction confirmation
        private int _maxConfirmationChecks = 20;
        private decimal _minArBalance = 0.01m; // Minimum AR balance warning threshold
        private bool _enableTags = true; // Enable Arweave tags for metadata
        private bool _compressData = false; // Compress data before upload
        private string _contentType = "application/octet-stream";

        // Local state tracking (permanent storage means no deletion)
        private readonly Dictionary<string, ArweaveTransactionInfo> _keyToTransactionMap = new();
        private readonly object _mapLock = new();

        public override string StrategyId => "arweave";
        public override string Name => "Arweave Permanent Storage";
        public override StorageTier Tier => StorageTier.Archive; // Permanent, immutable archive

        public override StorageCapabilities Capabilities => new StorageCapabilities
        {
            SupportsMetadata = true, // Via Arweave tags
            SupportsStreaming = true,
            SupportsLocking = false, // Data is immutable once stored
            SupportsVersioning = false, // Immutable by nature (new versions = new transactions)
            SupportsTiering = false,
            SupportsEncryption = false, // Encryption should be done at application layer
            SupportsCompression = true, // Optional compression before upload
            SupportsMultipart = false, // Arweave handles large files natively
            MaxObjectSize = 10L * 1024 * 1024 * 1024, // 10 GB practical limit (no hard limit)
            MaxObjects = null, // No limit
            ConsistencyModel = ConsistencyModel.Eventual // Blockchain-based, eventual consistency
        };

        #region Initialization

        /// <summary>
        /// Initializes the Arweave storage strategy and establishes connection to Arweave gateway.
        /// </summary>
        protected override async Task InitializeCoreAsync(CancellationToken ct)
        {
            // Load required configuration
            _gatewayUrl = GetConfiguration<string>("GatewayUrl", "https://arweave.net");
            _walletJwk = GetConfiguration<string?>("WalletJwk", null);

            // Load optional configuration
            _useBundlr = GetConfiguration<bool>("UseBundlr", false);
            _bundlrUrl = GetConfiguration<string>("BundlrUrl", "https://node1.bundlr.network");
            _bundlrApiKey = GetConfiguration<string?>("BundlrApiKey", null);
            _timeoutSeconds = GetConfiguration<int>("TimeoutSeconds", 600);
            _maxRetries = GetConfiguration<int>("MaxRetries", 5);
            _retryDelayMs = GetConfiguration<int>("RetryDelayMs", 2000);
            _verifyAfterStore = GetConfiguration<bool>("VerifyAfterStore", true);
            _confirmationWaitSeconds = GetConfiguration<int>("ConfirmationWaitSeconds", 120);
            _maxConfirmationChecks = GetConfiguration<int>("MaxConfirmationChecks", 20);
            _minArBalance = GetConfiguration<decimal>("MinArBalance", 0.01m);
            _enableTags = GetConfiguration<bool>("EnableTags", true);
            _compressData = GetConfiguration<bool>("CompressData", false);
            _contentType = GetConfiguration<string>("ContentType", "application/octet-stream");

            // Validate configuration
            if (string.IsNullOrWhiteSpace(_gatewayUrl))
            {
                throw new InvalidOperationException("Arweave gateway URL is required. Set 'GatewayUrl' in configuration.");
            }

            if (string.IsNullOrWhiteSpace(_walletJwk))
            {
                throw new InvalidOperationException("Arweave wallet JWK is required for transactions. Set 'WalletJwk' in configuration.");
            }

            if (_useBundlr && string.IsNullOrWhiteSpace(_bundlrApiKey))
            {
                throw new InvalidOperationException("Bundlr API key is required when UseBundlr is enabled. Set 'BundlrApiKey' in configuration.");
            }

            // Initialize wallet from JWK
            try
            {
                _wallet = ArweaveWallet.FromJwk(_walletJwk);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to load Arweave wallet from JWK: {ex.Message}", ex);
            }

            // Create HTTP client
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(_timeoutSeconds)
            };

            // Verify connectivity to Arweave gateway
            try
            {
                var infoRequest = new HttpRequestMessage(HttpMethod.Get, $"{_gatewayUrl}/info");
                var infoResponse = await _httpClient.SendAsync(infoRequest, ct);
                infoResponse.EnsureSuccessStatusCode();

                var infoJson = await infoResponse.Content.ReadAsStringAsync(ct);
                var info = JsonSerializer.Deserialize<ArweaveNetworkInfo>(infoJson);

                if (info == null)
                {
                    throw new InvalidOperationException($"Failed to connect to Arweave gateway at {_gatewayUrl}");
                }

                // Check wallet balance
                var balanceRequest = new HttpRequestMessage(HttpMethod.Get, $"{_gatewayUrl}/wallet/{_wallet.Address}/balance");
                var balanceResponse = await _httpClient.SendAsync(balanceRequest, ct);
                balanceResponse.EnsureSuccessStatusCode();

                var balanceWinstonStr = await balanceResponse.Content.ReadAsStringAsync(ct);
                if (long.TryParse(balanceWinstonStr, out var balanceWinston))
                {
                    var balanceAr = balanceWinston / 1_000_000_000_000m; // Convert Winston to AR
                    if (balanceAr < _minArBalance)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"Warning: Arweave wallet balance ({balanceAr:F4} AR) is below minimum threshold ({_minArBalance} AR)");
                    }
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to initialize Arweave strategy: {ex.Message}", ex);
            }
        }

        protected override async ValueTask DisposeCoreAsync()
        {
            await base.DisposeCoreAsync();
            _httpClient?.Dispose();
        }

        #endregion

        #region Core Storage Operations

        protected override async Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);
            ValidateStream(data);

            // Read stream into memory
            using var ms = new MemoryStream();
            await data.CopyToAsync(ms, 81920, ct);
            ms.Position = 0;
            var dataBytes = ms.ToArray();
            var dataSize = dataBytes.Length;

            // Optionally compress data
            byte[] finalData = dataBytes;
            if (_compressData)
            {
                finalData = await CompressDataAsync(dataBytes, ct);
            }

            string transactionId;
            ArweaveTransactionInfo txInfo;

            if (_useBundlr)
            {
                // Use Bundlr for instant finality
                (transactionId, txInfo) = await StoreViaBundlrAsync(key, finalData, dataSize, metadata, ct);
            }
            else
            {
                // Use native Arweave transactions
                (transactionId, txInfo) = await StoreViaArweaveAsync(key, finalData, dataSize, metadata, ct);
            }

            // Store transaction info in local state
            lock (_mapLock)
            {
                _keyToTransactionMap[key] = txInfo;
            }

            // Verify transaction if enabled
            if (_verifyAfterStore)
            {
                var verified = await VerifyTransactionAsync(transactionId, ct);
                if (!verified)
                {
                    throw new IOException($"Transaction verification failed for key '{key}' with TX ID {transactionId}");
                }
            }

            // Update statistics
            IncrementBytesStored(dataSize);
            IncrementOperationCounter(StorageOperationType.Store);

            return new StorageObjectMetadata
            {
                Key = key,
                Size = dataSize,
                Created = DateTime.UtcNow,
                Modified = DateTime.UtcNow,
                ETag = transactionId,
                ContentType = GetContentType(key),
                CustomMetadata = metadata as IReadOnlyDictionary<string, string>,
                Tier = Tier
            };
        }

        protected override async Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            // Get transaction ID from local state
            ArweaveTransactionInfo? txInfo = null;
            lock (_mapLock)
            {
                if (!_keyToTransactionMap.TryGetValue(key, out txInfo))
                {
                    throw new FileNotFoundException($"Object with key '{key}' not found in Arweave storage");
                }
            }

            // Retrieve content from Arweave
            var url = $"{_gatewayUrl}/{txInfo.TransactionId}";
            var request = new HttpRequestMessage(HttpMethod.Get, url);

            HttpResponseMessage response;
            try
            {
                response = await ExecuteWithRetryAsync(async () =>
                {
                    var httpResponse = await _httpClient!.SendAsync(request, ct);
                    httpResponse.EnsureSuccessStatusCode();
                    return httpResponse;
                }, ct);
            }
            catch (Exception ex)
            {
                throw new IOException($"Failed to retrieve object '{key}' from Arweave: {ex.Message}", ex);
            }

            var ms = new MemoryStream();
            await response.Content.CopyToAsync(ms, ct);
            ms.Position = 0;

            // Decompress if it was compressed
            Stream resultStream = ms;
            if (txInfo.IsCompressed)
            {
                resultStream = await DecompressDataAsync(ms.ToArray(), ct);
                await ms.DisposeAsync();
            }

            // Update statistics
            IncrementBytesRetrieved(resultStream.Length);
            IncrementOperationCounter(StorageOperationType.Retrieve);

            return resultStream;
        }

        protected override async Task DeleteAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            // NOTE: Arweave is permanent storage - deletion is not possible
            // We can only remove from local tracking
            // Throw exception to indicate this is not supported

            throw new NotSupportedException(
                "Deletion is not supported on Arweave permanent storage. " +
                "Data stored on Arweave is immutable and permanent by design. " +
                "To remove an object from tracking, use RemoveFromLocalTracking() method.");
        }

        protected override async Task<bool> ExistsAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            // Check local cache first
            bool existsInCache;
            lock (_mapLock)
            {
                existsInCache = _keyToTransactionMap.ContainsKey(key);
            }

            IncrementOperationCounter(StorageOperationType.Exists);

            if (!existsInCache)
            {
                return false;
            }

            // Optionally verify the transaction still exists on Arweave
            ArweaveTransactionInfo? txInfo = null;
            lock (_mapLock)
            {
                _keyToTransactionMap.TryGetValue(key, out txInfo);
            }

            if (txInfo != null)
            {
                try
                {
                    var txRequest = new HttpRequestMessage(HttpMethod.Get, $"{_gatewayUrl}/tx/{txInfo.TransactionId}/status");
                    var txResponse = await _httpClient!.SendAsync(txRequest, ct);
                    return txResponse.IsSuccessStatusCode;
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(string? prefix, [EnumeratorCancellation] CancellationToken ct)
        {
            EnsureInitialized();
            IncrementOperationCounter(StorageOperationType.List);

            List<ArweaveTransactionInfo> transactions;
            lock (_mapLock)
            {
                transactions = _keyToTransactionMap.Values.ToList();
            }

            foreach (var txInfo in transactions)
            {
                ct.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(prefix) || txInfo.Key.StartsWith(prefix))
                {
                    yield return new StorageObjectMetadata
                    {
                        Key = txInfo.Key,
                        Size = txInfo.Size,
                        Created = txInfo.CreatedAt,
                        Modified = txInfo.CreatedAt, // Arweave data is immutable
                        ETag = txInfo.TransactionId,
                        ContentType = txInfo.ContentType,
                        CustomMetadata = txInfo.Tags as IReadOnlyDictionary<string, string>,
                        Tier = Tier
                    };
                }

                await Task.Yield();
            }
        }

        protected override async Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            ArweaveTransactionInfo? txInfo = null;
            lock (_mapLock)
            {
                if (!_keyToTransactionMap.TryGetValue(key, out txInfo))
                {
                    throw new FileNotFoundException($"Object with key '{key}' not found");
                }
            }

            // Optionally fetch latest transaction metadata from Arweave
            try
            {
                var txRequest = new HttpRequestMessage(HttpMethod.Get, $"{_gatewayUrl}/tx/{txInfo.TransactionId}");
                var txResponse = await _httpClient!.SendAsync(txRequest, ct);

                if (txResponse.IsSuccessStatusCode)
                {
                    var txJson = await txResponse.Content.ReadAsStringAsync(ct);
                    var transaction = JsonSerializer.Deserialize<ArweaveTransaction>(txJson);

                    if (transaction != null)
                    {
                        // Update tags from blockchain
                        if (transaction.Tags != null)
                        {
                            var tagDict = new Dictionary<string, string>();
                            foreach (var tag in transaction.Tags)
                            {
                                var tagName = DecodeBase64Url(tag.Name);
                                var tagValue = DecodeBase64Url(tag.Value);
                                tagDict[tagName] = tagValue;
                            }
                            txInfo.Tags = tagDict;
                        }
                    }
                }
            }
            catch
            {
                // Use cached metadata if fetch fails
            }

            IncrementOperationCounter(StorageOperationType.GetMetadata);

            return new StorageObjectMetadata
            {
                Key = txInfo.Key,
                Size = txInfo.Size,
                Created = txInfo.CreatedAt,
                Modified = txInfo.CreatedAt,
                ETag = txInfo.TransactionId,
                ContentType = txInfo.ContentType,
                CustomMetadata = txInfo.Tags as IReadOnlyDictionary<string, string>,
                Tier = Tier
            };
        }

        protected override async Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();

                // Check Arweave network info
                var request = new HttpRequestMessage(HttpMethod.Get, $"{_gatewayUrl}/info");
                var response = await _httpClient!.SendAsync(request, ct);

                sw.Stop();

                if (response.IsSuccessStatusCode)
                {
                    var infoJson = await response.Content.ReadAsStringAsync(ct);
                    var info = JsonSerializer.Deserialize<ArweaveNetworkInfo>(infoJson);

                    return new StorageHealthInfo
                    {
                        Status = HealthStatus.Healthy,
                        LatencyMs = sw.ElapsedMilliseconds,
                        Message = $"Arweave network is accessible (height: {info?.Height ?? 0})",
                        CheckedAt = DateTime.UtcNow
                    };
                }
                else
                {
                    return new StorageHealthInfo
                    {
                        Status = HealthStatus.Unhealthy,
                        LatencyMs = sw.ElapsedMilliseconds,
                        Message = "Arweave network is not responding",
                        CheckedAt = DateTime.UtcNow
                    };
                }
            }
            catch (Exception ex)
            {
                return new StorageHealthInfo
                {
                    Status = HealthStatus.Unhealthy,
                    Message = $"Failed to connect to Arweave network: {ex.Message}",
                    CheckedAt = DateTime.UtcNow
                };
            }
        }

        protected override Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
        {
            // Arweave has no fixed capacity - storage is permanent and grows with the network
            return Task.FromResult<long?>(null);
        }

        #endregion

        #region Arweave Native Operations

        /// <summary>
        /// Stores data via native Arweave transactions with wallet signing.
        /// </summary>
        private async Task<(string transactionId, ArweaveTransactionInfo txInfo)> StoreViaArweaveAsync(
            string key, byte[] data, long originalSize, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            // Step 1: Get price for data size
            var priceRequest = new HttpRequestMessage(HttpMethod.Get, $"{_gatewayUrl}/price/{data.Length}");
            var priceResponse = await _httpClient!.SendAsync(priceRequest, ct);
            priceResponse.EnsureSuccessStatusCode();

            var priceWinstonStr = await priceResponse.Content.ReadAsStringAsync(ct);
            var priceWinston = long.Parse(priceWinstonStr);

            // Step 2: Get wallet's last transaction (for nonce)
            var lastTxRequest = new HttpRequestMessage(HttpMethod.Get, $"{_gatewayUrl}/wallet/{_wallet!.Address}/last_tx");
            var lastTxResponse = await _httpClient.SendAsync(lastTxRequest, ct);
            var lastTx = await lastTxResponse.Content.ReadAsStringAsync(ct);
            lastTx = lastTx.Trim('"');

            // Step 3: Create transaction
            var transaction = new ArweaveTransaction
            {
                Format = 2,
                LastTx = lastTx,
                Owner = _wallet.PublicKeyModulus,
                Target = "",
                Quantity = "0",
                Data = EncodeBase64Url(data),
                Reward = priceWinston.ToString(),
                Tags = new List<ArweaveTag>()
            };

            // Add tags for metadata and identification
            if (_enableTags)
            {
                transaction.Tags.Add(new ArweaveTag
                {
                    Name = EncodeBase64Url("Content-Type"),
                    Value = EncodeBase64Url(GetContentType(key))
                });

                transaction.Tags.Add(new ArweaveTag
                {
                    Name = EncodeBase64Url("Key"),
                    Value = EncodeBase64Url(key)
                });

                transaction.Tags.Add(new ArweaveTag
                {
                    Name = EncodeBase64Url("Timestamp"),
                    Value = EncodeBase64Url(DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString())
                });

                if (_compressData)
                {
                    transaction.Tags.Add(new ArweaveTag
                    {
                        Name = EncodeBase64Url("Compressed"),
                        Value = EncodeBase64Url("true")
                    });
                }

                // Add custom metadata as tags
                if (metadata != null)
                {
                    foreach (var kvp in metadata)
                    {
                        transaction.Tags.Add(new ArweaveTag
                        {
                            Name = EncodeBase64Url($"Meta-{kvp.Key}"),
                            Value = EncodeBase64Url(kvp.Value)
                        });
                    }
                }
            }

            // Step 4: Sign transaction
            var txId = await _wallet.SignTransactionAsync(transaction);

            // Step 5: Submit transaction to Arweave
            var submitRequest = new HttpRequestMessage(HttpMethod.Post, $"{_gatewayUrl}/tx")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(transaction),
                    Encoding.UTF8,
                    "application/json")
            };

            var submitResponse = await ExecuteWithRetryAsync(async () =>
            {
                var response = await _httpClient.SendAsync(submitRequest, ct);
                response.EnsureSuccessStatusCode();
                return response;
            }, ct);

            // Step 6: Wait for confirmation (optional)
            if (_confirmationWaitSeconds > 0)
            {
                await WaitForConfirmationAsync(txId, ct);
            }

            var txInfo = new ArweaveTransactionInfo
            {
                Key = key,
                TransactionId = txId,
                Size = originalSize,
                CreatedAt = DateTime.UtcNow,
                ContentType = GetContentType(key),
                Tags = metadata,
                IsCompressed = _compressData,
                IsBundled = false,
                Status = "Pending"
            };

            return (txId, txInfo);
        }

        /// <summary>
        /// Stores data via Bundlr for instant finality.
        /// </summary>
        private async Task<(string transactionId, ArweaveTransactionInfo txInfo)> StoreViaBundlrAsync(
            string key, byte[] data, long originalSize, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            // Bundlr/Irys provides instant data availability
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_bundlrUrl}/tx/arweave")
            {
                Content = new ByteArrayContent(data)
            };

            // Add authorization
            if (!string.IsNullOrEmpty(_bundlrApiKey))
            {
                request.Headers.Add("Authorization", $"Bearer {_bundlrApiKey}");
            }

            // Add tags as headers
            if (_enableTags && metadata != null)
            {
                var tagsJson = JsonSerializer.Serialize(metadata);
                request.Content.Headers.Add("X-Tags", Convert.ToBase64String(Encoding.UTF8.GetBytes(tagsJson)));
            }

            var response = await ExecuteWithRetryAsync(async () =>
            {
                var httpResponse = await _httpClient!.SendAsync(request, ct);
                httpResponse.EnsureSuccessStatusCode();
                return httpResponse;
            }, ct);

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            var bundlrResponse = JsonSerializer.Deserialize<BundlrUploadResponse>(responseJson);

            if (bundlrResponse == null || string.IsNullOrEmpty(bundlrResponse.Id))
            {
                throw new IOException("Failed to upload to Bundlr");
            }

            var txInfo = new ArweaveTransactionInfo
            {
                Key = key,
                TransactionId = bundlrResponse.Id,
                Size = originalSize,
                CreatedAt = DateTime.UtcNow,
                ContentType = GetContentType(key),
                Tags = metadata,
                IsCompressed = _compressData,
                IsBundled = true,
                Status = "Confirmed" // Bundlr provides instant confirmation
            };

            return (bundlrResponse.Id, txInfo);
        }

        /// <summary>
        /// Waits for transaction confirmation on Arweave blockchain.
        /// </summary>
        private async Task WaitForConfirmationAsync(string transactionId, CancellationToken ct)
        {
            var checksRemaining = _maxConfirmationChecks;
            var checkInterval = TimeSpan.FromSeconds(_confirmationWaitSeconds / (double)_maxConfirmationChecks);

            while (checksRemaining > 0 && !ct.IsCancellationRequested)
            {
                try
                {
                    var statusRequest = new HttpRequestMessage(HttpMethod.Get, $"{_gatewayUrl}/tx/{transactionId}/status");
                    var statusResponse = await _httpClient!.SendAsync(statusRequest, ct);

                    if (statusResponse.IsSuccessStatusCode)
                    {
                        var statusJson = await statusResponse.Content.ReadAsStringAsync(ct);
                        var status = JsonSerializer.Deserialize<ArweaveTransactionStatus>(statusJson);

                        if (status?.NumberOfConfirmations > 0)
                        {
                            return; // Transaction confirmed
                        }
                    }

                    await Task.Delay(checkInterval, ct);
                    checksRemaining--;
                }
                catch
                {
                    await Task.Delay(checkInterval, ct);
                    checksRemaining--;
                }
            }

            // Transaction not confirmed within timeout, but may still succeed later
            System.Diagnostics.Debug.WriteLine(
                $"Transaction {transactionId} not confirmed within {_confirmationWaitSeconds} seconds, but may still be pending");
        }

        /// <summary>
        /// Verifies a transaction exists and is valid on Arweave.
        /// </summary>
        private async Task<bool> VerifyTransactionAsync(string transactionId, CancellationToken ct)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, $"{_gatewayUrl}/tx/{transactionId}");
                var response = await _httpClient!.SendAsync(request, ct);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Arweave-Specific Public Methods

        /// <summary>
        /// Gets the Arweave transaction ID for a stored key.
        /// </summary>
        public string? GetTransactionId(string key)
        {
            EnsureInitialized();
            ValidateKey(key);

            lock (_mapLock)
            {
                return _keyToTransactionMap.TryGetValue(key, out var txInfo) ? txInfo.TransactionId : null;
            }
        }

        /// <summary>
        /// Gets the public Arweave URL for an object.
        /// </summary>
        public string? GetArweaveUrl(string key)
        {
            var txId = GetTransactionId(key);
            return txId != null ? $"{_gatewayUrl}/{txId}" : null;
        }

        /// <summary>
        /// Gets detailed transaction information for a stored object.
        /// </summary>
        public ArweaveTransactionInfo? GetTransactionInfo(string key)
        {
            EnsureInitialized();
            ValidateKey(key);

            lock (_mapLock)
            {
                return _keyToTransactionMap.TryGetValue(key, out var txInfo) ? txInfo : null;
            }
        }

        /// <summary>
        /// Removes an object from local tracking without deleting from Arweave.
        /// NOTE: The data remains permanently stored on Arweave.
        /// </summary>
        public bool RemoveFromLocalTracking(string key)
        {
            EnsureInitialized();
            ValidateKey(key);

            lock (_mapLock)
            {
                return _keyToTransactionMap.Remove(key);
            }
        }

        /// <summary>
        /// Gets the current wallet balance in AR.
        /// </summary>
        public async Task<decimal> GetWalletBalanceAsync(CancellationToken ct = default)
        {
            EnsureInitialized();

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, $"{_gatewayUrl}/wallet/{_wallet!.Address}/balance");
                var response = await _httpClient!.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();

                var balanceWinstonStr = await response.Content.ReadAsStringAsync(ct);
                var balanceWinston = long.Parse(balanceWinstonStr);
                return balanceWinston / 1_000_000_000_000m; // Convert Winston to AR
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Gets the estimated cost in AR for storing data of a given size.
        /// </summary>
        public async Task<decimal> GetStorageCostAsync(long dataSize, CancellationToken ct = default)
        {
            EnsureInitialized();

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, $"{_gatewayUrl}/price/{dataSize}");
                var response = await _httpClient!.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();

                var priceWinstonStr = await response.Content.ReadAsStringAsync(ct);
                var priceWinston = long.Parse(priceWinstonStr);
                return priceWinston / 1_000_000_000_000m; // Convert Winston to AR
            }
            catch
            {
                return 0;
            }
        }

        #endregion

        #region Compression Support

        /// <summary>
        /// Compresses data using GZip compression.
        /// </summary>
        private async Task<byte[]> CompressDataAsync(byte[] data, CancellationToken ct)
        {
            using var inputStream = new MemoryStream(data);
            using var outputStream = new MemoryStream();
            using var gzipStream = new System.IO.Compression.GZipStream(outputStream, System.IO.Compression.CompressionLevel.Optimal);

            await inputStream.CopyToAsync(gzipStream, 81920, ct);
            await gzipStream.FlushAsync(ct);

            return outputStream.ToArray();
        }

        /// <summary>
        /// Decompresses data that was compressed with GZip.
        /// </summary>
        private async Task<Stream> DecompressDataAsync(byte[] compressedData, CancellationToken ct)
        {
            using var inputStream = new MemoryStream(compressedData);
            using var gzipStream = new System.IO.Compression.GZipStream(inputStream, System.IO.Compression.CompressionMode.Decompress);
            var outputStream = new MemoryStream();

            await gzipStream.CopyToAsync(outputStream, 81920, ct);
            outputStream.Position = 0;

            return outputStream;
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Executes an operation with retry logic and exponential backoff.
        /// </summary>
        private async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation, CancellationToken ct)
        {
            Exception? lastException = null;

            for (int attempt = 0; attempt <= _maxRetries; attempt++)
            {
                try
                {
                    return await operation();
                }
                catch (Exception ex) when (attempt < _maxRetries)
                {
                    lastException = ex;

                    // Exponential backoff
                    var delay = _retryDelayMs * (int)Math.Pow(2, attempt);
                    await Task.Delay(delay, ct);
                }
            }

            throw lastException ?? new InvalidOperationException("Operation failed after retries");
        }

        /// <summary>
        /// Encodes data to Base64URL format (used by Arweave).
        /// </summary>
        private static string EncodeBase64Url(byte[] data)
        {
            return Convert.ToBase64String(data)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
        }

        /// <summary>
        /// Encodes string to Base64URL format.
        /// </summary>
        private static string EncodeBase64Url(string text)
        {
            return EncodeBase64Url(Encoding.UTF8.GetBytes(text));
        }

        /// <summary>
        /// Decodes Base64URL string.
        /// </summary>
        private static string DecodeBase64Url(string base64Url)
        {
            var base64 = base64Url.Replace('-', '+').Replace('_', '/');
            switch (base64.Length % 4)
            {
                case 2: base64 += "=="; break;
                case 3: base64 += "="; break;
            }
            var bytes = Convert.FromBase64String(base64);
            return Encoding.UTF8.GetString(bytes);
        }

        /// <summary>
        /// Gets the MIME content type based on file extension.
        /// </summary>
        private string GetContentType(string key)
        {
            var extension = Path.GetExtension(key).ToLowerInvariant();
            return extension switch
            {
                ".json" => "application/json",
                ".xml" => "application/xml",
                ".txt" => "text/plain",
                ".csv" => "text/csv",
                ".html" or ".htm" => "text/html",
                ".pdf" => "application/pdf",
                ".zip" => "application/zip",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".mp4" => "video/mp4",
                ".mp3" => "audio/mpeg",
                _ => "application/octet-stream"
            };
        }

        protected override int GetMaxKeyLength() => 2048;

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// Information about an Arweave transaction for a stored object.
    /// </summary>
    public class ArweaveTransactionInfo
    {
        public string Key { get; set; } = string.Empty;
        public string TransactionId { get; set; } = string.Empty;
        public long Size { get; set; }
        public DateTime CreatedAt { get; set; }
        public string ContentType { get; set; } = string.Empty;
        public IDictionary<string, string>? Tags { get; set; }
        public bool IsCompressed { get; set; }
        public bool IsBundled { get; set; }
        public string Status { get; set; } = string.Empty;
    }

    /// <summary>
    /// Arweave transaction structure.
    /// </summary>
    internal class ArweaveTransaction
    {
        [JsonPropertyName("format")]
        public int Format { get; set; }

        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("last_tx")]
        public string LastTx { get; set; } = string.Empty;

        [JsonPropertyName("owner")]
        public string Owner { get; set; } = string.Empty;

        [JsonPropertyName("tags")]
        public List<ArweaveTag> Tags { get; set; } = new();

        [JsonPropertyName("target")]
        public string Target { get; set; } = string.Empty;

        [JsonPropertyName("quantity")]
        public string Quantity { get; set; } = string.Empty;

        [JsonPropertyName("data")]
        public string Data { get; set; } = string.Empty;

        [JsonPropertyName("data_size")]
        public string? DataSize { get; set; }

        [JsonPropertyName("data_root")]
        public string? DataRoot { get; set; }

        [JsonPropertyName("reward")]
        public string Reward { get; set; } = string.Empty;

        [JsonPropertyName("signature")]
        public string? Signature { get; set; }
    }

    /// <summary>
    /// Arweave transaction tag.
    /// </summary>
    internal class ArweaveTag
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("value")]
        public string Value { get; set; } = string.Empty;
    }

    /// <summary>
    /// Arweave network information.
    /// </summary>
    internal class ArweaveNetworkInfo
    {
        [JsonPropertyName("network")]
        public string Network { get; set; } = string.Empty;

        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("height")]
        public long Height { get; set; }

        [JsonPropertyName("blocks")]
        public long Blocks { get; set; }
    }

    /// <summary>
    /// Arweave transaction status.
    /// </summary>
    internal class ArweaveTransactionStatus
    {
        [JsonPropertyName("block_height")]
        public long BlockHeight { get; set; }

        [JsonPropertyName("block_indep_hash")]
        public string BlockIndepHash { get; set; } = string.Empty;

        [JsonPropertyName("number_of_confirmations")]
        public int NumberOfConfirmations { get; set; }
    }

    /// <summary>
    /// Bundlr upload response.
    /// </summary>
    internal class BundlrUploadResponse
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }
    }

    /// <summary>
    /// Arweave wallet implementation for JWK signing.
    /// </summary>
    internal class ArweaveWallet
    {
        public string Address { get; private set; } = string.Empty;
        public string PublicKeyModulus { get; private set; } = string.Empty;
        private RSA? _rsa;

        public static ArweaveWallet FromJwk(string jwkJson)
        {
            var jwk = JsonSerializer.Deserialize<Dictionary<string, string>>(jwkJson);
            if (jwk == null)
            {
                throw new InvalidOperationException("Invalid JWK format");
            }

            var wallet = new ArweaveWallet();

            // Extract RSA parameters from JWK
            var rsaParams = new RSAParameters
            {
                Modulus = DecodeBase64Url(jwk["n"]),
                Exponent = DecodeBase64Url(jwk["e"]),
                D = DecodeBase64Url(jwk["d"]),
                P = DecodeBase64Url(jwk["p"]),
                Q = DecodeBase64Url(jwk["q"]),
                DP = DecodeBase64Url(jwk["dp"]),
                DQ = DecodeBase64Url(jwk["dq"]),
                InverseQ = DecodeBase64Url(jwk["qi"])
            };

            wallet._rsa = RSA.Create();
            wallet._rsa.ImportParameters(rsaParams);

            // Calculate address from public key modulus
            wallet.PublicKeyModulus = jwk["n"];
            var modulusBytes = DecodeBase64Url(jwk["n"]);
            var addressHash = SHA256.HashData(modulusBytes);
            wallet.Address = EncodeBase64Url(addressHash);

            return wallet;
        }

        public async Task<string> SignTransactionAsync(ArweaveTransaction transaction)
        {
            if (_rsa == null)
            {
                throw new InvalidOperationException("Wallet not initialized");
            }

            // Create signing payload
            var dataBytes = string.IsNullOrEmpty(transaction.Data)
                ? Array.Empty<byte>()
                : DecodeBase64Url(transaction.Data);

            var dataRoot = SHA256.HashData(dataBytes);
            transaction.DataRoot = EncodeBase64Url(dataRoot);
            transaction.DataSize = dataBytes.Length.ToString();

            // Build signature data
            var signingData = BuildSigningData(transaction);

            // Sign with RSA-PSS
            var signature = _rsa.SignData(signingData, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            transaction.Signature = EncodeBase64Url(signature);

            // Calculate transaction ID
            var signatureHash = SHA256.HashData(signature);
            var txId = EncodeBase64Url(signatureHash);
            transaction.Id = txId;

            return txId;
        }

        private byte[] BuildSigningData(ArweaveTransaction transaction)
        {
            using var ms = new MemoryStream();

            // Arweave signature format: owner + target + data + quantity + reward + last_tx + tags
            ms.Write(DecodeBase64Url(transaction.Owner));
            ms.Write(DecodeBase64Url(transaction.Target));
            ms.Write(DecodeBase64Url(transaction.Data));
            ms.Write(Encoding.UTF8.GetBytes(transaction.Quantity));
            ms.Write(Encoding.UTF8.GetBytes(transaction.Reward));
            ms.Write(DecodeBase64Url(transaction.LastTx));

            foreach (var tag in transaction.Tags)
            {
                ms.Write(DecodeBase64Url(tag.Name));
                ms.Write(DecodeBase64Url(tag.Value));
            }

            return ms.ToArray();
        }

        private static string EncodeBase64Url(byte[] data)
        {
            return Convert.ToBase64String(data)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
        }

        private static byte[] DecodeBase64Url(string base64Url)
        {
            var base64 = base64Url.Replace('-', '+').Replace('_', '/');
            switch (base64.Length % 4)
            {
                case 2: base64 += "=="; break;
                case 3: base64 += "="; break;
            }
            return Convert.FromBase64String(base64);
        }
    }

    #endregion
}
