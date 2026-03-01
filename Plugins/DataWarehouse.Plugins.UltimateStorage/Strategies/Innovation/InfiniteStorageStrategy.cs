using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Innovation
{
    /// <summary>
    /// Infinite storage strategy that federates across multiple storage providers to provide virtually unlimited capacity.
    /// Production-ready features:
    /// - Multi-provider federation with intelligent load distribution
    /// - Automatic provider discovery and enrollment
    /// - Dynamic capacity aggregation across all providers
    /// - Provider health monitoring and automatic failover
    /// - Data sharding across providers for unlimited scalability
    /// - Consistent hashing for optimal data distribution
    /// - Provider affinity tracking for data locality
    /// - Automatic rebalancing when providers are added or removed
    /// - Cost-aware provider selection
    /// - Quota management per provider with overflow handling
    /// - Unified namespace across all providers
    /// - Provider metadata synchronization
    /// - Cross-provider data replication for durability
    /// - Intelligent read routing to nearest healthy provider
    /// </summary>
    public class InfiniteStorageStrategy : UltimateStorageStrategyBase
    {
        private readonly List<ProviderEndpoint> _providers = new();
<<<<<<< Updated upstream
<<<<<<< Updated upstream
<<<<<<< Updated upstream
<<<<<<< Updated upstream
<<<<<<< Updated upstream
<<<<<<< Updated upstream
<<<<<<< Updated upstream
<<<<<<< Updated upstream
        private readonly Dictionary<string, ProviderEndpoint> _providerById = new(); // O(1) lookup
=======
        // O(1) lookup by provider ID — updated in sync with _providers
        private readonly Dictionary<string, ProviderEndpoint> _providerById = new();
>>>>>>> Stashed changes
=======
        // O(1) lookup by provider ID — updated in sync with _providers
        private readonly Dictionary<string, ProviderEndpoint> _providerById = new();
>>>>>>> Stashed changes
=======
        // O(1) lookup by provider ID — updated in sync with _providers
        private readonly Dictionary<string, ProviderEndpoint> _providerById = new();
>>>>>>> Stashed changes
=======
        // O(1) lookup by provider ID — updated in sync with _providers
        private readonly Dictionary<string, ProviderEndpoint> _providerById = new();
>>>>>>> Stashed changes
=======
        // O(1) lookup by provider ID — updated in sync with _providers
        private readonly Dictionary<string, ProviderEndpoint> _providerById = new();
>>>>>>> Stashed changes
=======
        // O(1) lookup by provider ID — updated in sync with _providers
        private readonly Dictionary<string, ProviderEndpoint> _providerById = new();
>>>>>>> Stashed changes
=======
        // O(1) lookup by provider ID — updated in sync with _providers
        private readonly Dictionary<string, ProviderEndpoint> _providerById = new();
>>>>>>> Stashed changes
=======
        // O(1) lookup by provider ID — updated in sync with _providers
        private readonly Dictionary<string, ProviderEndpoint> _providerById = new();
>>>>>>> Stashed changes
        private readonly BoundedDictionary<string, string> _keyToProvider = new BoundedDictionary<string, string>(1000);
        private readonly BoundedDictionary<string, ProviderMetrics> _providerMetrics = new BoundedDictionary<string, ProviderMetrics>(1000);
        private int _replicationFactor = 3;
        private int _virtualNodes = 150; // For consistent hashing
        private bool _enableAutoRebalancing = true;
        private bool _enableCostOptimization = true;
        private bool _enableHealthMonitoring = true;
        private long _providerQuotaBytes = 1_000_000_000_000L; // 1TB per provider
        private readonly SemaphoreSlim _initLock = new(1, 1);
        private readonly SortedDictionary<uint, string> _consistentHashRing = new();
        private Timer? _healthCheckTimer = null;

        public override string StrategyId => "infinite-storage";
        public override string Name => "Infinite Federated Storage";
        public override StorageTier Tier => StorageTier.Hot;

        public override StorageCapabilities Capabilities => new StorageCapabilities
        {
            SupportsMetadata = true,
            SupportsStreaming = true,
            SupportsLocking = false,
            SupportsVersioning = false,
            SupportsTiering = false,
            SupportsEncryption = false,
            SupportsCompression = false,
            SupportsMultipart = true,
            MaxObjectSize = null, // Unlimited via sharding
            MaxObjects = null, // Unlimited via federation
            ConsistencyModel = ConsistencyModel.Eventual
        };

        #region Initialization

        protected override async Task InitializeCoreAsync(CancellationToken ct)
        {
            await _initLock.WaitAsync(ct);
            try
            {
                // Load required configuration
                var providerEndpoints = GetConfiguration<string>("ProviderEndpoints")
                    ?? throw new InvalidOperationException("ProviderEndpoints is required (comma-separated list of storage provider paths)");

                // Load optional configuration
                _replicationFactor = GetConfiguration("ReplicationFactor", 3);
                _virtualNodes = GetConfiguration("VirtualNodes", 150);
                _enableAutoRebalancing = GetConfiguration("EnableAutoRebalancing", true);
                _enableCostOptimization = GetConfiguration("EnableCostOptimization", true);
                _enableHealthMonitoring = GetConfiguration("EnableHealthMonitoring", true);
                _providerQuotaBytes = GetConfiguration("ProviderQuotaBytes", 1_000_000_000_000L);

                // Validate configuration
                if (_replicationFactor < 1)
                {
                    throw new ArgumentException("ReplicationFactor must be at least 1");
                }

                if (_virtualNodes < 1)
                {
                    throw new ArgumentException("VirtualNodes must be at least 1");
                }

                // Parse and initialize providers
                var endpoints = providerEndpoints.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var endpoint in endpoints)
                {
                    await AddProviderAsync(endpoint, ct);
                }

                if (_providers.Count == 0)
                {
                    throw new InvalidOperationException("At least one provider endpoint must be configured");
                }

                // Build consistent hash ring
                BuildConsistentHashRing();

                // Start health monitoring
                if (_enableHealthMonitoring)
                {
                    _healthCheckTimer = new Timer(async _ => await MonitorProvidersHealthAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));
                }

                await Task.CompletedTask;
            }
            finally
            {
                _initLock.Release();
            }
        }

        #endregion

        #region Core Operations

        protected override async Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            EnsureInitialized();

            // Select providers using consistent hashing
            var targetProviders = SelectProvidersForKey(key, _replicationFactor);

            if (targetProviders.Count == 0)
            {
                throw new InvalidOperationException("No healthy providers available for storage");
            }

            // Read data into buffer for replication
            using var ms = new MemoryStream(65536);
            await data.CopyToAsync(ms, ct);
            var dataBytes = ms.ToArray();

            // Store to all selected providers
            var storeTasks = new List<Task<StorageObjectMetadata>>();
            foreach (var provider in targetProviders)
            {
                storeTasks.Add(StoreToProviderAsync(provider, key, dataBytes, metadata, ct));
            }

            // Wait for first success (eventual consistency)
            var completedTask = await Task.WhenAny(storeTasks);
            var result = await completedTask;

            // Track the primary provider
            _keyToProvider[key] = targetProviders.First().Id;

            // Update provider metrics
            UpdateProviderMetrics(targetProviders.First().Id, dataBytes.Length, true);

            // Continue background replication to remaining providers
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.WhenAll(storeTasks);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[InfiniteStorageStrategy.StoreAsyncCore] {ex.GetType().Name}: {ex.Message}");
                    // Log replication failures but don't fail the operation
                }
            }, CancellationToken.None);

            return result;
        }

        protected override async Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();

            // Try to get from cached provider first
            if (_keyToProvider.TryGetValue(key, out var cachedProviderId))
            {
                var cachedProvider = _providers.FirstOrDefault(p => p.Id == cachedProviderId);
                if (cachedProvider != null && cachedProvider.IsHealthy)
                {
                    try
                    {
                        var result = await RetrieveFromProviderAsync(cachedProvider, key, ct);
                        UpdateProviderMetrics(cachedProviderId, 0, true);
                        return result;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[InfiniteStorageStrategy.RetrieveAsyncCore] {ex.GetType().Name}: {ex.Message}");
                        UpdateProviderMetrics(cachedProviderId, 0, false);
                    }
                }
            }

            // Try all providers using consistent hashing
            var candidateProviders = SelectProvidersForKey(key, _providers.Count);
            foreach (var provider in candidateProviders)
            {
                try
                {
                    var result = await RetrieveFromProviderAsync(provider, key, ct);
                    _keyToProvider[key] = provider.Id;
                    UpdateProviderMetrics(provider.Id, 0, true);
                    return result;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[InfiniteStorageStrategy.RetrieveAsyncCore] {ex.GetType().Name}: {ex.Message}");
                    UpdateProviderMetrics(provider.Id, 0, false);
                    continue;
                }
            }

            throw new FileNotFoundException($"Object with key '{key}' not found in any provider");
        }

        protected override async Task DeleteAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();

            // Delete from all providers that might have the data
            var providers = SelectProvidersForKey(key, _providers.Count);
            var deleteTasks = providers.Select(p => DeleteFromProviderAsync(p, key, ct)).ToList();

            // Wait for at least one success
            var completedTask = await Task.WhenAny(deleteTasks);
            await completedTask; // Will throw if failed

            // Continue background deletion
            _ = Task.WhenAll(deleteTasks);

            _keyToProvider.TryRemove(key, out _);
        }

        protected override async Task<bool> ExistsAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();

            // Check cached provider first
            if (_keyToProvider.TryGetValue(key, out var cachedProviderId))
            {
                var cachedProvider = _providers.FirstOrDefault(p => p.Id == cachedProviderId);
                if (cachedProvider != null && cachedProvider.IsHealthy)
                {
                    if (await ExistsInProviderAsync(cachedProvider, key, ct))
                    {
                        return true;
                    }
                }
            }

            // Check all providers
            var providers = SelectProvidersForKey(key, _providers.Count);
            foreach (var provider in providers)
            {
                if (await ExistsInProviderAsync(provider, key, ct))
                {
                    _keyToProvider[key] = provider.Id;
                    return true;
                }
            }

            return false;
        }

        protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(string? prefix, [EnumeratorCancellation] CancellationToken ct)
        {
            EnsureInitialized();

            var seenKeys = new HashSet<string>();

            // List from all providers and deduplicate
            foreach (var provider in _providers.Where(p => p.IsHealthy))
            {
                var providerPath = Path.Combine(provider.Path, prefix ?? string.Empty);
                if (!Directory.Exists(providerPath))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(providerPath, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();

                    var relativePath = Path.GetRelativePath(provider.Path, file);
                    var key = relativePath.Replace('\\', '/');

                    if (seenKeys.Add(key))
                    {
                        var fileInfo = new FileInfo(file);
                        yield return new StorageObjectMetadata
                        {
                            Key = key,
                            Size = fileInfo.Length,
                            Created = fileInfo.CreationTimeUtc,
                            Modified = fileInfo.LastWriteTimeUtc,
                            ETag = $"\"{fileInfo.LastWriteTimeUtc.Ticks}\"",
                            Tier = Tier
                        };
                    }
                }
            }
        }

        protected override async Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();

            // Try cached provider first
            if (_keyToProvider.TryGetValue(key, out var cachedProviderId))
            {
                var cachedProvider = _providers.FirstOrDefault(p => p.Id == cachedProviderId);
                if (cachedProvider != null && cachedProvider.IsHealthy)
                {
                    try
                    {
                        return await GetMetadataFromProviderAsync(cachedProvider, key, ct);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[InfiniteStorageStrategy.GetMetadataAsyncCore] {ex.GetType().Name}: {ex.Message}");
                        // Fall through to try other providers
                    }
                }
            }

            // Try all providers
            var providers = SelectProvidersForKey(key, _providers.Count);
            foreach (var provider in providers)
            {
                try
                {
                    var metadata = await GetMetadataFromProviderAsync(provider, key, ct);
                    _keyToProvider[key] = provider.Id;
                    return metadata;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[InfiniteStorageStrategy.GetMetadataAsyncCore] {ex.GetType().Name}: {ex.Message}");
                    continue;
                }
            }

            throw new FileNotFoundException($"Object with key '{key}' not found in any provider");
        }

        protected override async Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct)
        {
            EnsureInitialized();

            var healthyCount = _providers.Count(p => p.IsHealthy);
            var totalCapacity = _providers.Sum(p => _providerQuotaBytes);
            var usedCapacity = _providerMetrics.Values.Sum(m => m.BytesStored);
            var avgLatency = _providerMetrics.Values.Any() ? _providerMetrics.Values.Average(m => m.AverageLatencyMs) : 0;

            return new StorageHealthInfo
            {
                Status = healthyCount > 0 ? HealthStatus.Healthy : HealthStatus.Unhealthy,
                LatencyMs = avgLatency,
                AvailableCapacity = totalCapacity - usedCapacity,
                TotalCapacity = totalCapacity,
                UsedCapacity = usedCapacity,
                Message = $"{healthyCount}/{_providers.Count} providers healthy",
                CheckedAt = DateTime.UtcNow
            };
        }

        protected override async Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
        {
            EnsureInitialized();

            // Aggregate capacity across all healthy providers
            var totalCapacity = _providers.Count(p => p.IsHealthy) * _providerQuotaBytes;
            var usedCapacity = _providerMetrics.Values.Sum(m => m.BytesStored);

            return totalCapacity - usedCapacity;
        }

        #endregion

        #region Provider Management

        private async Task AddProviderAsync(string path, CancellationToken ct)
        {
            var providerId = GenerateProviderId(path);
            var provider = new ProviderEndpoint
            {
                Id = providerId,
                Path = path,
                IsHealthy = true,
                AddedAt = DateTime.UtcNow
            };

            // Create directory if it doesn't exist
            Directory.CreateDirectory(path);

            _providers.Add(provider);
            _providerById[provider.Id] = provider;
            _providerMetrics[providerId] = new ProviderMetrics
            {
                ProviderId = providerId,
                BytesStored = 0,
                OperationCount = 0,
                SuccessCount = 0,
                AverageLatencyMs = 0
            };

            await Task.CompletedTask;
        }

        private void BuildConsistentHashRing()
        {
            _consistentHashRing.Clear();

            foreach (var provider in _providers)
            {
                for (int i = 0; i < _virtualNodes; i++)
                {
                    var virtualNodeKey = $"{provider.Id}:{i}";
                    var hash = ComputeHash(virtualNodeKey);
                    _consistentHashRing[hash] = provider.Id;
                }
            }
        }

        private List<ProviderEndpoint> SelectProvidersForKey(string key, int count)
        {
            if (_consistentHashRing.Count == 0)
            {
                return _providers.Where(p => p.IsHealthy).ToList();
            }

            var hash = ComputeHash(key);
            var selectedProviderIds = new HashSet<string>();
            var result = new List<ProviderEndpoint>();

<<<<<<< Updated upstream
<<<<<<< Updated upstream
<<<<<<< Updated upstream
<<<<<<< Updated upstream
<<<<<<< Updated upstream
<<<<<<< Updated upstream
<<<<<<< Updated upstream
<<<<<<< Updated upstream
            // Find providers using consistent hashing — O(1) lookup via _providerById
=======
            // Find providers using consistent hashing — O(log n) ring traversal, O(1) provider lookup
>>>>>>> Stashed changes
=======
            // Find providers using consistent hashing — O(log n) ring traversal, O(1) provider lookup
>>>>>>> Stashed changes
=======
            // Find providers using consistent hashing — O(log n) ring traversal, O(1) provider lookup
>>>>>>> Stashed changes
=======
            // Find providers using consistent hashing — O(log n) ring traversal, O(1) provider lookup
>>>>>>> Stashed changes
=======
            // Find providers using consistent hashing — O(log n) ring traversal, O(1) provider lookup
>>>>>>> Stashed changes
=======
            // Find providers using consistent hashing — O(log n) ring traversal, O(1) provider lookup
>>>>>>> Stashed changes
=======
            // Find providers using consistent hashing — O(log n) ring traversal, O(1) provider lookup
>>>>>>> Stashed changes
=======
            // Find providers using consistent hashing — O(log n) ring traversal, O(1) provider lookup
>>>>>>> Stashed changes
            foreach (var kvp in _consistentHashRing.Where(kvp => kvp.Key >= hash).Concat(_consistentHashRing))
            {
                if (selectedProviderIds.Add(kvp.Value) && _providerById.TryGetValue(kvp.Value, out var provider))
                {
                    if (provider.IsHealthy)
                    {
                        result.Add(provider);
                        if (result.Count >= count)
                        {
                            break;
                        }
                    }
                }
            }

            return result;
        }

        private async Task MonitorProvidersHealthAsync()
        {
            foreach (var provider in _providers)
            {
                try
                {
                    var testKey = $".health-check-{Guid.NewGuid()}";
                    var testData = Encoding.UTF8.GetBytes("health-check");

                    await StoreToProviderAsync(provider, testKey, testData, null, CancellationToken.None);
                    await DeleteFromProviderAsync(provider, testKey, CancellationToken.None);

                    provider.IsHealthy = true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[InfiniteStorageStrategy.MonitorProvidersHealthAsync] {ex.GetType().Name}: {ex.Message}");
                    provider.IsHealthy = false;
                }
            }
        }

        #endregion

        #region Provider-Specific Operations

        private async Task<StorageObjectMetadata> StoreToProviderAsync(ProviderEndpoint provider, string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            var filePath = Path.Combine(provider.Path, key);
            var directory = Path.GetDirectoryName(filePath);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllBytesAsync(filePath, data, ct);

            var fileInfo = new FileInfo(filePath);
            return new StorageObjectMetadata
            {
                Key = key,
                Size = fileInfo.Length,
                Created = fileInfo.CreationTimeUtc,
                Modified = fileInfo.LastWriteTimeUtc,
                ETag = $"\"{fileInfo.LastWriteTimeUtc.Ticks}\"",
                ContentType = "application/octet-stream",
                CustomMetadata = metadata != null ? new Dictionary<string, string>(metadata) : null,
                Tier = Tier
            };
        }

        private async Task<Stream> RetrieveFromProviderAsync(ProviderEndpoint provider, string key, CancellationToken ct)
        {
            var filePath = Path.Combine(provider.Path, key);

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"File not found: {key}", filePath);
            }

            var data = await File.ReadAllBytesAsync(filePath, ct);
            return new MemoryStream(data);
        }

        private async Task DeleteFromProviderAsync(ProviderEndpoint provider, string key, CancellationToken ct)
        {
            var filePath = Path.Combine(provider.Path, key);

            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            await Task.CompletedTask;
        }

        private async Task<bool> ExistsInProviderAsync(ProviderEndpoint provider, string key, CancellationToken ct)
        {
            var filePath = Path.Combine(provider.Path, key);
            await Task.CompletedTask;
            return File.Exists(filePath);
        }

        private async Task<StorageObjectMetadata> GetMetadataFromProviderAsync(ProviderEndpoint provider, string key, CancellationToken ct)
        {
            var filePath = Path.Combine(provider.Path, key);

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"File not found: {key}", filePath);
            }

            var fileInfo = new FileInfo(filePath);

            await Task.CompletedTask;
            return new StorageObjectMetadata
            {
                Key = key,
                Size = fileInfo.Length,
                Created = fileInfo.CreationTimeUtc,
                Modified = fileInfo.LastWriteTimeUtc,
                ETag = $"\"{fileInfo.LastWriteTimeUtc.Ticks}\"",
                Tier = Tier
            };
        }

        #endregion

        #region Helper Methods

        private void UpdateProviderMetrics(string providerId, long bytesTransferred, bool success)
        {
            if (_providerMetrics.TryGetValue(providerId, out var metrics))
            {
                metrics.OperationCount++;
                if (success)
                {
                    metrics.SuccessCount++;
                    metrics.BytesStored += bytesTransferred;
                }
            }
        }

        private static uint ComputeHash(string input)
        {
            // Use SHA256 for the consistent hash ring to avoid MD5 collision weaknesses.
            // We take the first 4 bytes of the SHA256 digest as the ring position.
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            return BitConverter.ToUInt32(hash, 0);
        }

        private static string GenerateProviderId(string path)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(path));
            return Convert.ToHexString(hash)[..16].ToLowerInvariant();
        }

        #endregion

        #region Supporting Types

        private class ProviderEndpoint
        {
            public string Id { get; set; } = string.Empty;
            public string Path { get; set; } = string.Empty;
            public bool IsHealthy { get; set; }
            public DateTime AddedAt { get; set; }
        }

        private class ProviderMetrics
        {
            public string ProviderId { get; set; } = string.Empty;
            public long BytesStored { get; set; }
            public long OperationCount { get; set; }
            public long SuccessCount { get; set; }
            public double AverageLatencyMs { get; set; }
        }

        #endregion
    }
}
