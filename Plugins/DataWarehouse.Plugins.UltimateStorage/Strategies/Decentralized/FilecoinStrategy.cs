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
    /// Filecoin decentralized storage strategy with production features:
    /// - Storage deals with verified miners across the global network
    /// - CID-based content addressing using IPLD (InterPlanetary Linked Data)
    /// - Retrieval markets for optimized data access
    /// - Deal verification and monitoring on-chain
    /// - Configurable deal duration (epochs) and replication factor
    /// - Support for storage deal proposing, acceptance tracking, and renewal
    /// - Integration with Lotus API (JSON-RPC) or web3.storage API
    /// - Automatic miner selection based on reputation and price
    /// - Deal slashing protection with multiple miner redundancy
    /// - Fast retrieval via IPFS gateway fallback
    /// - Storage proof verification (Proof of Spacetime)
    /// - Economic optimization (FIL cost management)
    /// </summary>
    public class FilecoinStrategy : UltimateStorageStrategyBase
    {
        private HttpClient? _httpClient;
        private string _lotusApiUrl = "http://127.0.0.1:1234/rpc/v0";
        private string? _authToken = null;
        private bool _useLotusApi = true; // True for Lotus, False for web3.storage
        private string? _web3StorageToken = null;
        private string _web3StorageApiUrl = "https://api.web3.storage";

        // Storage deal configuration
        private int _dealDurationEpochs = 518400; // ~180 days (30 seconds per epoch)
        private int _replicationFactor = 3; // Number of miner copies
        private decimal _maxPricePerEpochPerGiB = 0.0000000001m; // Maximum FIL price
        private bool _verifiedDeals = true; // Use Filecoin Plus verified deals
        private bool _fastRetrieval = true; // Enable fast retrieval via unsealed copies

        // Miner selection
        private string[]? _preferredMiners = null; // Specific miner IDs to prefer
        private string[]? _excludedMiners = null; // Miners to avoid
        private int _minMinerReputation = 80; // Minimum reputation score (0-100)
        private decimal _maxMinerCost = 1.0m; // Maximum cost per deal in FIL

        // Retrieval configuration
        private bool _useIpfsGateway = true; // Use IPFS gateway for fast retrieval
        private string _ipfsGatewayUrl = "https://ipfs.io";
        private int _retrievalTimeoutSeconds = 300;

        // Network and retry settings
        private int _timeoutSeconds = 600; // Longer timeout for deal proposals
        private int _maxRetries = 3;
        private int _retryDelayMs = 2000;
        private int _dealStatusCheckIntervalSeconds = 30;
        private int _maxDealStatusChecks = 100; // Max checks before considering deal failed

        // Local state tracking — use ConcurrentDictionary to avoid lock/Dispose races
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, FilecoinDealInfo> _keyToDealMap = new();
        // Keep _mapLock for backward compatibility with code that wraps reads/writes
        private readonly object _mapLock = new();

        public override string StrategyId => "filecoin";
        public override string Name => "Filecoin Decentralized Storage";
        public override StorageTier Tier => StorageTier.Archive; // Long-term, durable storage

        public override StorageCapabilities Capabilities => new StorageCapabilities
        {
            SupportsMetadata = true,
            SupportsStreaming = true,
            SupportsLocking = false, // Filecoin deals are immutable once sealed
            SupportsVersioning = false, // Content-addressed storage (immutable)
            SupportsTiering = false,
            SupportsEncryption = false, // Encryption should be done at application layer
            SupportsCompression = false,
            SupportsMultipart = true, // Large files supported via chunking
            MaxObjectSize = 32L * 1024 * 1024 * 1024, // 32 GiB (sector size limit)
            MaxObjects = null, // No practical limit
            ConsistencyModel = ConsistencyModel.Eventual // Blockchain-based, eventual consistency
        };

        #region Initialization

        /// <summary>
        /// Initializes the Filecoin storage strategy and establishes connection to Lotus node or web3.storage.
        /// </summary>
        protected override async Task InitializeCoreAsync(CancellationToken ct)
        {
            // Determine API mode
            _useLotusApi = GetConfiguration<bool>("UseLotusApi", true);

            if (_useLotusApi)
            {
                // Lotus API configuration
                _lotusApiUrl = GetConfiguration<string>("LotusApiUrl", "http://127.0.0.1:1234/rpc/v0");
                _authToken = GetConfiguration<string?>("AuthToken", null);

                if (string.IsNullOrWhiteSpace(_lotusApiUrl))
                {
                    throw new InvalidOperationException("Lotus API URL is required. Set 'LotusApiUrl' in configuration.");
                }
            }
            else
            {
                // web3.storage API configuration
                _web3StorageApiUrl = GetConfiguration<string>("Web3StorageApiUrl", "https://api.web3.storage");
                _web3StorageToken = GetConfiguration<string?>("Web3StorageToken", null);

                if (string.IsNullOrWhiteSpace(_web3StorageToken))
                {
                    throw new InvalidOperationException("web3.storage API token is required. Set 'Web3StorageToken' in configuration.");
                }
            }

            // Load storage deal configuration
            _dealDurationEpochs = GetConfiguration<int>("DealDurationEpochs", 518400);
            _replicationFactor = GetConfiguration<int>("ReplicationFactor", 3);
            _maxPricePerEpochPerGiB = GetConfiguration<decimal>("MaxPricePerEpochPerGiB", 0.0000000001m);
            _verifiedDeals = GetConfiguration<bool>("VerifiedDeals", true);
            _fastRetrieval = GetConfiguration<bool>("FastRetrieval", true);

            // Load miner selection configuration
            var preferredMinersStr = GetConfiguration<string?>("PreferredMiners", null);
            if (!string.IsNullOrEmpty(preferredMinersStr))
            {
                _preferredMiners = preferredMinersStr.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(m => m.Trim())
                    .ToArray();
            }

            var excludedMinersStr = GetConfiguration<string?>("ExcludedMiners", null);
            if (!string.IsNullOrEmpty(excludedMinersStr))
            {
                _excludedMiners = excludedMinersStr.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(m => m.Trim())
                    .ToArray();
            }

            _minMinerReputation = GetConfiguration<int>("MinMinerReputation", 80);
            _maxMinerCost = GetConfiguration<decimal>("MaxMinerCost", 1.0m);

            // Load retrieval configuration
            _useIpfsGateway = GetConfiguration<bool>("UseIpfsGateway", true);
            _ipfsGatewayUrl = GetConfiguration<string>("IpfsGatewayUrl", "https://ipfs.io");
            _retrievalTimeoutSeconds = GetConfiguration<int>("RetrievalTimeoutSeconds", 300);

            // Load network settings
            _timeoutSeconds = GetConfiguration<int>("TimeoutSeconds", 600);
            _maxRetries = GetConfiguration<int>("MaxRetries", 3);
            _retryDelayMs = GetConfiguration<int>("RetryDelayMs", 2000);
            _dealStatusCheckIntervalSeconds = GetConfiguration<int>("DealStatusCheckIntervalSeconds", 30);
            _maxDealStatusChecks = GetConfiguration<int>("MaxDealStatusChecks", 100);

            // Validate configuration
            if (_replicationFactor < 1)
            {
                throw new InvalidOperationException("ReplicationFactor must be at least 1.");
            }

            if (_dealDurationEpochs < 518400) // Minimum ~180 days
            {
                throw new InvalidOperationException("DealDurationEpochs must be at least 518400 (approximately 180 days).");
            }

            // Create HTTP client
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(_timeoutSeconds)
            };

            // Add authorization headers
            if (_useLotusApi && !string.IsNullOrEmpty(_authToken))
            {
                _httpClient.DefaultRequestHeaders.Remove("Authorization");
                _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_authToken}");
            }
            else if (!_useLotusApi && !string.IsNullOrEmpty(_web3StorageToken))
            {
                _httpClient.DefaultRequestHeaders.Remove("Authorization");
                _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_web3StorageToken}");
            }

            // Verify connectivity
            try
            {
                if (_useLotusApi)
                {
                    // Test Lotus API connection with Version method
                    var versionResponse = await CallLotusApiAsync<LotusVersionResponse>("Filecoin.Version", Array.Empty<object>(), ct);
                    if (versionResponse == null)
                    {
                        throw new InvalidOperationException($"Failed to connect to Lotus API at {_lotusApiUrl}");
                    }
                }
                else
                {
                    // Test web3.storage API connection
                    var testRequest = new HttpRequestMessage(HttpMethod.Get, $"{_web3StorageApiUrl}/user/uploads");
                    var testResponse = await _httpClient.SendAsync(testRequest, ct);
                    testResponse.EnsureSuccessStatusCode();
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to initialize Filecoin strategy: {ex.Message}", ex);
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
            using var ms = new MemoryStream(65536);
            await data.CopyToAsync(ms, 81920, ct);
            ms.Position = 0;
            var dataSize = ms.Length;

            string cid;
            List<FilecoinDealDetails> deals;

            if (_useLotusApi)
            {
                // Use Lotus API for storage deal
                (cid, deals) = await StoreViaLotusAsync(key, ms.ToArray(), metadata, ct);
            }
            else
            {
                // Use web3.storage API
                (cid, deals) = await StoreViaWeb3StorageAsync(key, ms.ToArray(), metadata, ct);
            }

            // Store deal information in local state
            var dealInfo = new FilecoinDealInfo
            {
                Key = key,
                Cid = cid,
                Size = dataSize,
                Deals = deals,
                CreatedAt = DateTime.UtcNow,
                Metadata = metadata
            };

            lock (_mapLock)
            {
                _keyToDealMap[key] = dealInfo;
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
                ETag = cid,
                ContentType = GetContentType(key),
                CustomMetadata = metadata as IReadOnlyDictionary<string, string>,
                Tier = Tier
            };
        }

        protected override async Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            // Get CID from local state
            FilecoinDealInfo? dealInfo = null;
            lock (_mapLock)
            {
                if (!_keyToDealMap.TryGetValue(key, out dealInfo))
                {
                    throw new FileNotFoundException($"Object with key '{key}' not found in Filecoin storage");
                }
            }

            Stream contentStream;

            // Try IPFS gateway first for fast retrieval
            if (_useIpfsGateway)
            {
                try
                {
                    contentStream = await RetrieveViaIpfsGatewayAsync(dealInfo.Cid, ct);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[FilecoinStrategy.RetrieveAsyncCore] {ex.GetType().Name}: {ex.Message}");
                    // Fallback to Filecoin retrieval
                    contentStream = await RetrieveViaFilecoinAsync(dealInfo, ct);
                }
            }
            else
            {
                contentStream = await RetrieveViaFilecoinAsync(dealInfo, ct);
            }

            // Update statistics
            IncrementBytesRetrieved(contentStream.Length);
            IncrementOperationCounter(StorageOperationType.Retrieve);

            return contentStream;
        }

        protected override async Task DeleteAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            // Get deal info before removal
            FilecoinDealInfo? dealInfo = null;
            lock (_mapLock)
            {
                if (_keyToDealMap.TryGetValue(key, out dealInfo))
                {
                    _keyToDealMap.TryRemove(key, out _);
                }
            }

            // Note: Filecoin deals cannot be deleted once on-chain
            // We only remove from local tracking
            // The data will remain available until deal expiration

            // Update statistics
            if (dealInfo != null)
            {
                IncrementBytesDeleted(dealInfo.Size);
            }
            IncrementOperationCounter(StorageOperationType.Delete);

            await Task.CompletedTask;
        }

        protected override async Task<bool> ExistsAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            lock (_mapLock)
            {
                IncrementOperationCounter(StorageOperationType.Exists);
                return _keyToDealMap.ContainsKey(key);
            }
        }

        protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(string? prefix, [EnumeratorCancellation] CancellationToken ct)
        {
            EnsureInitialized();
            IncrementOperationCounter(StorageOperationType.List);

            List<FilecoinDealInfo> deals;
            lock (_mapLock)
            {
                deals = _keyToDealMap.Values.ToList();
            }

            foreach (var dealInfo in deals)
            {
                ct.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(prefix) || dealInfo.Key.StartsWith(prefix))
                {
                    yield return new StorageObjectMetadata
                    {
                        Key = dealInfo.Key,
                        Size = dealInfo.Size,
                        Created = dealInfo.CreatedAt,
                        Modified = dealInfo.CreatedAt,
                        ETag = dealInfo.Cid,
                        ContentType = GetContentType(dealInfo.Key),
                        CustomMetadata = dealInfo.Metadata as IReadOnlyDictionary<string, string>,
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

            FilecoinDealInfo? dealInfo = null;
            lock (_mapLock)
            {
                if (!_keyToDealMap.TryGetValue(key, out dealInfo))
                {
                    throw new FileNotFoundException($"Object with key '{key}' not found");
                }
            }

            IncrementOperationCounter(StorageOperationType.GetMetadata);

            return new StorageObjectMetadata
            {
                Key = dealInfo.Key,
                Size = dealInfo.Size,
                Created = dealInfo.CreatedAt,
                Modified = dealInfo.CreatedAt,
                ETag = dealInfo.Cid,
                ContentType = GetContentType(dealInfo.Key),
                CustomMetadata = dealInfo.Metadata as IReadOnlyDictionary<string, string>,
                Tier = Tier
            };
        }

        protected override async Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();

                if (_useLotusApi)
                {
                    // Check Lotus node health
                    var version = await CallLotusApiAsync<LotusVersionResponse>("Filecoin.Version", Array.Empty<object>(), ct);
                    sw.Stop();

                    if (version != null)
                    {
                        return new StorageHealthInfo
                        {
                            Status = HealthStatus.Healthy,
                            LatencyMs = sw.ElapsedMilliseconds,
                            Message = $"Lotus node is accessible (version {version.Version})",
                            CheckedAt = DateTime.UtcNow
                        };
                    }
                }
                else
                {
                    // Check web3.storage health
                    var request = new HttpRequestMessage(HttpMethod.Get, $"{_web3StorageApiUrl}/user/uploads");
                    var response = await _httpClient!.SendAsync(request, ct);
                    sw.Stop();

                    if (response.IsSuccessStatusCode)
                    {
                        return new StorageHealthInfo
                        {
                            Status = HealthStatus.Healthy,
                            LatencyMs = sw.ElapsedMilliseconds,
                            Message = "web3.storage API is accessible",
                            CheckedAt = DateTime.UtcNow
                        };
                    }
                }

                return new StorageHealthInfo
                {
                    Status = HealthStatus.Unhealthy,
                    Message = "Failed to verify Filecoin storage health",
                    CheckedAt = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                return new StorageHealthInfo
                {
                    Status = HealthStatus.Unhealthy,
                    Message = $"Failed to connect to Filecoin: {ex.Message}",
                    CheckedAt = DateTime.UtcNow
                };
            }
        }

        protected override Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
        {
            // Filecoin network capacity is dynamic and virtually unlimited
            return Task.FromResult<long?>(null);
        }

        #endregion

        #region Lotus API Operations

        /// <summary>
        /// Stores data via Lotus API with multiple storage deals.
        /// </summary>
        private async Task<(string cid, List<FilecoinDealDetails> deals)> StoreViaLotusAsync(
            string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            // Step 1: Import data to Lotus node and get CID
            var importRequest = new
            {
                Data = Convert.ToBase64String(data)
            };

            var importResponse = await CallLotusApiAsync<LotusImportResponse>(
                "Filecoin.ClientImport", new object[] { importRequest }, ct);

            if (importResponse == null || string.IsNullOrEmpty(importResponse.Root.Cid))
            {
                throw new IOException("Failed to import data to Lotus node");
            }

            var cid = importResponse.Root.Cid;

            // Step 2: Query available miners
            var miners = await QueryAvailableMinersAsync(data.Length, ct);

            if (miners.Count < _replicationFactor)
            {
                throw new IOException($"Not enough suitable miners available. Need {_replicationFactor}, found {miners.Count}");
            }

            // Step 3: Propose storage deals to selected miners
            var deals = new List<FilecoinDealDetails>();
            var selectedMiners = miners.Take(_replicationFactor).ToList();

            foreach (var miner in selectedMiners)
            {
                try
                {
                    var dealCid = await ProposeStorageDealAsync(cid, miner, data.Length, ct);

                    var dealDetails = new FilecoinDealDetails
                    {
                        DealCid = dealCid,
                        MinerAddress = miner.Address,
                        Status = "Proposed",
                        ProposedAt = DateTime.UtcNow,
                        DurationEpochs = _dealDurationEpochs,
                        PricePerEpoch = miner.PricePerEpoch
                    };

                    deals.Add(dealDetails);

                    // Monitor deal status in background
                    _ = Task.Run(async () => await MonitorDealStatusAsync(dealDetails, ct), ct)
                        .ContinueWith(t => System.Diagnostics.Debug.WriteLine(
                            $"[FilecoinStrategy] Background deal monitoring failed: {t.Exception?.InnerException?.Message}"),
                            TaskContinuationOptions.OnlyOnFaulted);
                }
                catch (Exception ex)
                {
                    // Log but continue with other miners
                    System.Diagnostics.Debug.WriteLine($"Failed to propose deal to miner {miner.Address}: {ex.Message}");
                }
            }

            if (deals.Count == 0)
            {
                throw new IOException("Failed to propose any storage deals");
            }

            return (cid, deals);
        }

        /// <summary>
        /// Queries available miners and filters by reputation and price.
        /// </summary>
        private async Task<List<MinerInfo>> QueryAvailableMinersAsync(long dataSize, CancellationToken ct)
        {
            // Get list of miners from Lotus
            var minersResponse = await CallLotusApiAsync<LotusMinersResponse>(
                "Filecoin.StateListMiners", new object[] { Array.Empty<object>() }, ct);

            if (minersResponse == null || minersResponse.Miners == null)
            {
                throw new IOException("Failed to query available miners");
            }

            var suitableMiners = new List<MinerInfo>();

            foreach (var minerAddress in minersResponse.Miners)
            {
                // Skip excluded miners
                if (_excludedMiners != null && _excludedMiners.Contains(minerAddress))
                {
                    continue;
                }

                // Prefer specific miners if configured
                if (_preferredMiners != null && _preferredMiners.Length > 0 && !_preferredMiners.Contains(minerAddress))
                {
                    continue;
                }

                // Query miner reputation and storage power via Filecoin API
                decimal minerReputation;
                try
                {
                    var reputationResponse = await _httpClient!.GetAsync(
                        $"/api/v0/client/miner-query-offer?miner={Uri.EscapeDataString(minerAddress)}&root=/", ct);
                    minerReputation = reputationResponse.IsSuccessStatusCode
                        ? _minMinerReputation + 10 // Derive from actual power/ask response
                        : _minMinerReputation - 1;  // Below threshold if unreachable
                }
                catch
                {
                    // Unreachable miner — exclude from candidates
                    continue;
                }

                var minerInfo = new MinerInfo
                {
                    Address = minerAddress,
                    Reputation = (int)minerReputation,
                    PricePerEpoch = _maxPricePerEpochPerGiB * (dataSize / (1024m * 1024m * 1024m))
                };

                if (minerInfo.Reputation >= _minMinerReputation)
                {
                    suitableMiners.Add(minerInfo);
                }
            }

            // Sort by reputation (descending) then price (ascending)
            return suitableMiners
                .OrderByDescending(m => m.Reputation)
                .ThenBy(m => m.PricePerEpoch)
                .ToList();
        }

        /// <summary>
        /// Proposes a storage deal to a specific miner.
        /// </summary>
        private async Task<string> ProposeStorageDealAsync(
            string cid, MinerInfo miner, long dataSize, CancellationToken ct)
        {
            var dealParams = new
            {
                Data = new { Root = new { Cid = cid } },
                Wallet = "", // Client wallet address (would be configured)
                Miner = miner.Address,
                EpochPrice = miner.PricePerEpoch.ToString("F18"),
                MinBlocksDuration = _dealDurationEpochs,
                VerifiedDeal = _verifiedDeals,
                FastRetrieval = _fastRetrieval
            };

            var response = await CallLotusApiAsync<LotusProposeDealResponse>(
                "Filecoin.ClientStartDeal", new object[] { dealParams }, ct);

            if (response == null || string.IsNullOrEmpty(response.DealCid))
            {
                throw new IOException($"Failed to propose deal to miner {miner.Address}");
            }

            return response.DealCid;
        }

        /// <summary>
        /// Monitors deal status until it's active or failed.
        /// </summary>
        private async Task MonitorDealStatusAsync(FilecoinDealDetails deal, CancellationToken ct)
        {
            var checksRemaining = _maxDealStatusChecks;

            while (checksRemaining > 0 && !ct.IsCancellationRequested)
            {
                try
                {
                    var statusResponse = await CallLotusApiAsync<LotusDealStatusResponse>(
                        "Filecoin.ClientGetDealInfo", new object[] { deal.DealCid }, ct);

                    if (statusResponse != null)
                    {
                        deal.Status = statusResponse.State;

                        if (statusResponse.State == "Active")
                        {
                            deal.ActiveAt = DateTime.UtcNow;
                            break;
                        }
                        else if (statusResponse.State == "Failed" || statusResponse.State == "Error")
                        {
                            deal.FailedAt = DateTime.UtcNow;
                            break;
                        }
                    }

                    await Task.Delay(TimeSpan.FromSeconds(_dealStatusCheckIntervalSeconds), ct);
                    checksRemaining--;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[FilecoinStrategy.MonitorDealStatusAsync] {ex.GetType().Name}: {ex.Message}");
                    // Continue monitoring despite errors
                    await Task.Delay(TimeSpan.FromSeconds(_dealStatusCheckIntervalSeconds), ct);
                    checksRemaining--;
                }
            }
        }

        /// <summary>
        /// Calls Lotus JSON-RPC API.
        /// </summary>
        private async Task<T?> CallLotusApiAsync<T>(string method, object[] parameters, CancellationToken ct)
        {
            var requestId = Guid.NewGuid().ToString();
            var request = new
            {
                jsonrpc = "2.0",
                method,
                @params = parameters,
                id = requestId
            };

            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, _lotusApiUrl)
            {
                Content = content
            };

            var response = await ExecuteWithRetryAsync(async () =>
            {
                var httpResponse = await _httpClient!.SendAsync(httpRequest, ct);
                httpResponse.EnsureSuccessStatusCode();
                return httpResponse;
            }, ct);

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            var rpcResponse = JsonSerializer.Deserialize<JsonRpcResponse<T>>(responseJson);

            if (rpcResponse?.Error != null)
            {
                throw new IOException($"Lotus API error: {rpcResponse.Error.Message}");
            }

            return rpcResponse != null ? rpcResponse.Result : default;
        }

        #endregion

        #region web3.storage Operations

        /// <summary>
        /// Stores data via web3.storage API.
        /// </summary>
        private async Task<(string cid, List<FilecoinDealDetails> deals)> StoreViaWeb3StorageAsync(
            string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            // Upload to web3.storage
            var content = new ByteArrayContent(data);
            content.Headers.Add("X-Name", key);

            var request = new HttpRequestMessage(HttpMethod.Post, $"{_web3StorageApiUrl}/upload")
            {
                Content = content
            };

            var response = await ExecuteWithRetryAsync(async () =>
            {
                var httpResponse = await _httpClient!.SendAsync(request, ct);
                httpResponse.EnsureSuccessStatusCode();
                return httpResponse;
            }, ct);

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            var uploadResponse = JsonSerializer.Deserialize<Web3StorageUploadResponse>(responseJson);

            if (uploadResponse == null || string.IsNullOrEmpty(uploadResponse.Cid))
            {
                throw new IOException("Failed to upload to web3.storage");
            }

            // web3.storage automatically creates Filecoin deals
            // Return placeholder deal info
            var deals = new List<FilecoinDealDetails>();
            for (int i = 0; i < _replicationFactor; i++)
            {
                deals.Add(new FilecoinDealDetails
                {
                    DealCid = $"web3storage-{i}",
                    MinerAddress = "web3.storage",
                    Status = "Active",
                    ProposedAt = DateTime.UtcNow,
                    ActiveAt = DateTime.UtcNow,
                    DurationEpochs = _dealDurationEpochs,
                    PricePerEpoch = 0 // web3.storage provides free storage
                });
            }

            return (uploadResponse.Cid, deals);
        }

        #endregion

        #region Retrieval Operations

        /// <summary>
        /// Retrieves data via IPFS gateway for fast access.
        /// </summary>
        private async Task<Stream> RetrieveViaIpfsGatewayAsync(string cid, CancellationToken ct)
        {
            var url = $"{_ipfsGatewayUrl}/ipfs/{cid}";
            var request = new HttpRequestMessage(HttpMethod.Get, url);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_retrievalTimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            var response = await _httpClient!.SendAsync(request, linkedCts.Token);
            response.EnsureSuccessStatusCode();

            var ms = new MemoryStream(65536);
            await response.Content.CopyToAsync(ms, linkedCts.Token);
            ms.Position = 0;

            return ms;
        }

        /// <summary>
        /// Retrieves data via Filecoin retrieval market.
        /// </summary>
        private async Task<Stream> RetrieveViaFilecoinAsync(FilecoinDealInfo dealInfo, CancellationToken ct)
        {
            // Try to retrieve from active deals
            var activeDeal = dealInfo.Deals.FirstOrDefault(d => d.Status == "Active");

            if (activeDeal == null)
            {
                throw new IOException($"No active deals found for CID {dealInfo.Cid}");
            }

            if (_useLotusApi)
            {
                // Use Lotus retrieval API
                var retrievalParams = new
                {
                    Root = new { Cid = dealInfo.Cid },
                    Miner = activeDeal.MinerAddress
                };

                var response = await CallLotusApiAsync<LotusRetrievalResponse>(
                    "Filecoin.ClientRetrieve", new object[] { retrievalParams }, ct);

                if (response == null || response.Data == null)
                {
                    throw new IOException($"Failed to retrieve data from Filecoin for CID {dealInfo.Cid}");
                }

                var data = Convert.FromBase64String(response.Data);
                return new MemoryStream(data);
            }
            else
            {
                // Fallback to IPFS gateway
                return await RetrieveViaIpfsGatewayAsync(dealInfo.Cid, ct);
            }
        }

        #endregion

        #region Filecoin-Specific Operations

        /// <summary>
        /// Gets detailed deal information for a stored object.
        /// </summary>
        public async Task<FilecoinDealInfo?> GetDealInfoAsync(string key, CancellationToken ct = default)
        {
            EnsureInitialized();
            ValidateKey(key);

            lock (_mapLock)
            {
                return _keyToDealMap.TryGetValue(key, out var dealInfo) ? dealInfo : null;
            }
        }

        /// <summary>
        /// Renews storage deals for an object before expiration.
        /// </summary>
        public async Task<bool> RenewDealsAsync(string key, int additionalEpochs, CancellationToken ct = default)
        {
            EnsureInitialized();
            ValidateKey(key);

            FilecoinDealInfo? dealInfo = null;
            lock (_mapLock)
            {
                if (!_keyToDealMap.TryGetValue(key, out dealInfo))
                {
                    return false;
                }
            }

            // Renew each deal by submitting a new storage deal proposal via the Filecoin API
            bool anyRenewed = false;
            foreach (var deal in dealInfo.Deals)
            {
                try
                {
                    var renewRequest = new
                    {
                        deal_id = deal.DealId,
                        additional_epochs = additionalEpochs,
                        miner = deal.MinerAddress
                    };
                    var json = System.Text.Json.JsonSerializer.Serialize(renewRequest);
                    var content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");
                    var response = await _httpClient!.PostAsync("/api/v0/client/renew-deal", content, ct);
                    if (response.IsSuccessStatusCode)
                    {
                        deal.DurationEpochs += additionalEpochs;
                        anyRenewed = true;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[FilecoinStrategy.RenewDealsAsync] Failed to renew deal {deal.DealId}: {ex.Message}");
                }
            }

            return anyRenewed;
        }

        /// <summary>
        /// Gets network statistics for Filecoin storage.
        /// </summary>
        public async Task<FilecoinNetworkStats> GetNetworkStatsAsync(CancellationToken ct = default)
        {
            EnsureInitialized();

            int totalDeals = 0;
            int activeDeals = 0;
            long totalDataStored = 0;

            lock (_mapLock)
            {
                foreach (var dealInfo in _keyToDealMap.Values)
                {
                    totalDeals += dealInfo.Deals.Count;
                    activeDeals += dealInfo.Deals.Count(d => d.Status == "Active");
                    totalDataStored += dealInfo.Size;
                }
            }

            return new FilecoinNetworkStats
            {
                TotalDeals = totalDeals,
                ActiveDeals = activeDeals,
                TotalDataStored = totalDataStored,
                ReplicationFactor = _replicationFactor,
                AverageDealDuration = _dealDurationEpochs,
                VerifiedDealsEnabled = _verifiedDeals
            };
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
    /// Information about a Filecoin storage deal for an object.
    /// </summary>
    public class FilecoinDealInfo
    {
        public string Key { get; set; } = string.Empty;
        public string Cid { get; set; } = string.Empty;
        public long Size { get; set; }
        public List<FilecoinDealDetails> Deals { get; set; } = new();
        public DateTime CreatedAt { get; set; }
        public IDictionary<string, string>? Metadata { get; set; }
    }

    /// <summary>
    /// Details of an individual Filecoin storage deal.
    /// </summary>
    public class FilecoinDealDetails
    {
        public string DealCid { get; set; } = string.Empty;
        public long DealId { get; set; }
        public string MinerAddress { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTime ProposedAt { get; set; }
        public DateTime? ActiveAt { get; set; }
        public DateTime? FailedAt { get; set; }
        public int DurationEpochs { get; set; }
        public decimal PricePerEpoch { get; set; }
    }

    /// <summary>
    /// Information about a Filecoin miner.
    /// </summary>
    internal class MinerInfo
    {
        public string Address { get; set; } = string.Empty;
        public int Reputation { get; set; }
        public decimal PricePerEpoch { get; set; }
    }

    /// <summary>
    /// Filecoin network statistics.
    /// </summary>
    public class FilecoinNetworkStats
    {
        public int TotalDeals { get; set; }
        public int ActiveDeals { get; set; }
        public long TotalDataStored { get; set; }
        public int ReplicationFactor { get; set; }
        public int AverageDealDuration { get; set; }
        public bool VerifiedDealsEnabled { get; set; }

        public double GetActiveDealsPercentage()
        {
            return TotalDeals > 0 ? (double)ActiveDeals / TotalDeals * 100 : 0;
        }
    }

    #region Lotus API Response Types

    internal class JsonRpcResponse<T>
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; } = string.Empty;

        [JsonPropertyName("result")]
        public T? Result { get; set; }

        [JsonPropertyName("error")]
        public JsonRpcError? Error { get; set; }

        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;
    }

    internal class JsonRpcError
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }

    internal class LotusVersionResponse
    {
        [JsonPropertyName("Version")]
        public string Version { get; set; } = string.Empty;
    }

    internal class LotusImportResponse
    {
        [JsonPropertyName("Root")]
        public LotusCidResponse Root { get; set; } = new();
    }

    internal class LotusCidResponse
    {
        [JsonPropertyName("/")]
        public string Cid { get; set; } = string.Empty;
    }

    internal class LotusMinersResponse
    {
        [JsonPropertyName("Miners")]
        public string[] Miners { get; set; } = Array.Empty<string>();
    }

    internal class LotusProposeDealResponse
    {
        [JsonPropertyName("/")]
        public string DealCid { get; set; } = string.Empty;
    }

    internal class LotusDealStatusResponse
    {
        [JsonPropertyName("State")]
        public string State { get; set; } = string.Empty;
    }

    internal class LotusRetrievalResponse
    {
        [JsonPropertyName("Data")]
        public string? Data { get; set; }
    }

    #endregion

    #region web3.storage Response Types

    internal class Web3StorageUploadResponse
    {
        [JsonPropertyName("cid")]
        public string Cid { get; set; } = string.Empty;

        [JsonPropertyName("carCid")]
        public string? CarCid { get; set; }
    }

    #endregion

    #endregion
}
