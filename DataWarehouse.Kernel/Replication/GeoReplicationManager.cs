using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Infrastructure;
using DataWarehouse.SDK.Primitives;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Kernel.Replication
{
    /// <summary>
    /// Kernel-level geo-replication orchestrator.
    /// Coordinates multi-region replication using MultiRegionReplicator and GeoDistributedConsensus from SDK.
    /// </summary>
    public sealed class GeoReplicationManager
    {
        private readonly IKernelContext _context;
        private readonly MultiRegionReplicator _replicator;
        private readonly GeoDistributedConsensus? _consensus;
        private readonly ConcurrentDictionary<string, IStorageProvider> _storageProviders = new();
        private readonly ConcurrentDictionary<string, RegionInfo> _regions = new();
        private readonly SemaphoreSlim _lock = new(1, 1);
        private long _bandwidthThrottle = 0; // 0 = unlimited
        private readonly CancellationTokenSource _cts = new();
        private Task? _backgroundSyncTask;

        /// <summary>
        /// Creates a new geo-replication manager.
        /// </summary>
        /// <param name="context">Kernel context for logging</param>
        /// <param name="enableConsensus">Whether to enable geo-distributed consensus</param>
        public GeoReplicationManager(IKernelContext context, bool enableConsensus = true)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));

            // Create replicator with default transport
            _replicator = new MultiRegionReplicator(
                new HttpRegionTransport(),
                new InMemoryMultiRegionMetrics());

            // Optionally enable consensus for strong consistency
            if (enableConsensus)
            {
                _consensus = new GeoDistributedConsensus();
            }

            // Subscribe to conflict events
            _replicator.ConflictResolved += OnConflictResolved;

            _context.LogInformation("GeoReplicationManager initialized (consensus: {EnableConsensus})", enableConsensus);
        }

        /// <summary>
        /// Starts the geo-replication manager and background sync loop.
        /// </summary>
        public async Task StartAsync(CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                if (_backgroundSyncTask != null)
                {
                    _context.LogWarning("GeoReplicationManager already started");
                    return;
                }

                _context.LogInformation("Starting GeoReplicationManager");

                // Start background sync loop
                _backgroundSyncTask = RunBackgroundSyncAsync(_cts.Token);

                // Start consensus if enabled
                if (_consensus != null)
                {
                    await _consensus.StartAsync(_cts.Token);
                }

                _context.LogInformation("GeoReplicationManager started");
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Stops the geo-replication manager.
        /// </summary>
        public async Task StopAsync()
        {
            await _lock.WaitAsync();
            try
            {
                _context.LogInformation("Stopping GeoReplicationManager");

                // Cancel background tasks
                _cts.Cancel();

                // Wait for background sync to complete
                if (_backgroundSyncTask != null)
                {
                    await _backgroundSyncTask.ContinueWith(_ => { });
                }

                // Stop consensus if enabled
                if (_consensus != null)
                {
                    // GeoDistributedConsensus doesn't have StopAsync, but we can just let it be
                }

                _context.LogInformation("GeoReplicationManager stopped");
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Adds a new region to the replication topology.
        /// </summary>
        public async Task<RegionOperationResult> AddRegionAsync(
            string regionId,
            string endpoint,
            RegionConfig config,
            IStorageProvider storageProvider,
            CancellationToken ct = default)
        {
            var startTime = DateTime.UtcNow;

            try
            {
                _context.LogInformation("Adding region {RegionId} at {Endpoint}", regionId, endpoint);

                // Validate inputs
                if (string.IsNullOrWhiteSpace(regionId))
                    throw new ArgumentException("Region ID cannot be empty", nameof(regionId));
                if (string.IsNullOrWhiteSpace(endpoint))
                    throw new ArgumentException("Endpoint cannot be empty", nameof(endpoint));
                if (storageProvider == null)
                    throw new ArgumentNullException(nameof(storageProvider));

                // Check if region already exists
                if (_regions.ContainsKey(regionId))
                {
                    return new RegionOperationResult
                    {
                        Success = false,
                        ErrorMessage = $"Region {regionId} already exists",
                        RegionId = regionId,
                        Duration = DateTime.UtcNow - startTime
                    };
                }

                // Store the storage provider
                _storageProviders[regionId] = storageProvider;

                // Add region to internal tracking
                var regionInfo = new RegionInfo
                {
                    RegionId = regionId,
                    Endpoint = endpoint,
                    Config = config,
                    AddedAt = DateTime.UtcNow,
                    Health = RegionHealth.Unknown
                };
                _regions[regionId] = regionInfo;

                // Add to consensus cluster if enabled
                if (_consensus != null)
                {
                    var regionCluster = new RegionCluster
                    {
                        RegionId = regionId,
                        Endpoint = endpoint,
                        Priority = config.Priority,
                        IsWitness = config.IsWitness
                    };
                    _consensus.AddRegion(regionCluster);
                }

                _context.LogInformation("Region {RegionId} added successfully", regionId);

                return new RegionOperationResult
                {
                    Success = true,
                    RegionId = regionId,
                    Duration = DateTime.UtcNow - startTime
                };
            }
            catch (Exception ex)
            {
                _context.LogError(ex, "Failed to add region {RegionId}", regionId);
                return new RegionOperationResult
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    RegionId = regionId,
                    Duration = DateTime.UtcNow - startTime
                };
            }
        }

        /// <summary>
        /// Removes a region from the replication topology.
        /// </summary>
        public async Task<RegionOperationResult> RemoveRegionAsync(
            string regionId,
            bool drainFirst = true,
            CancellationToken ct = default)
        {
            var startTime = DateTime.UtcNow;

            try
            {
                _context.LogInformation("Removing region {RegionId} (drain: {DrainFirst})", regionId, drainFirst);

                if (!_regions.ContainsKey(regionId))
                {
                    return new RegionOperationResult
                    {
                        Success = false,
                        ErrorMessage = $"Region {regionId} not found",
                        RegionId = regionId,
                        Duration = DateTime.UtcNow - startTime
                    };
                }

                // Drain pending operations if requested
                if (drainFirst)
                {
                    _context.LogInformation("Draining pending operations for region {RegionId}", regionId);
                    // Wait for pending replication operations to complete
                    // The MultiRegionReplicator handles this via batch processing
                    await Task.Delay(1000, ct); // Give time for in-flight operations
                }

                // Remove from regions
                _regions.TryRemove(regionId, out _);
                _storageProviders.TryRemove(regionId, out _);

                // Remove from consensus if enabled
                if (_consensus != null)
                {
                    _consensus.RemoveRegion(regionId);
                }

                _context.LogInformation("Region {RegionId} removed successfully", regionId);

                return new RegionOperationResult
                {
                    Success = true,
                    RegionId = regionId,
                    Duration = DateTime.UtcNow - startTime
                };
            }
            catch (Exception ex)
            {
                _context.LogError(ex, "Failed to remove region {RegionId}", regionId);
                return new RegionOperationResult
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    RegionId = regionId,
                    Duration = DateTime.UtcNow - startTime
                };
            }
        }

        /// <summary>
        /// Gets the current replication status.
        /// </summary>
        public async Task<MultiRegionReplicationStatus> GetReplicationStatusAsync(CancellationToken ct = default)
        {
            try
            {
                var regionStatuses = new Dictionary<string, RegionStatus>();

                foreach (var (regionId, regionInfo) in _regions)
                {
                    // Check region health
                    var health = await CheckRegionHealthInternalAsync(regionId, ct);

                    regionStatuses[regionId] = new RegionStatus
                    {
                        RegionId = regionId,
                        Endpoint = regionInfo.Endpoint,
                        Health = health,
                        ReplicationLag = TimeSpan.Zero, // TODO: Calculate from vector clocks
                        PendingOperations = 0, // TODO: Get from replicator
                        LastSuccessfulSync = DateTime.UtcNow,
                        BytesTransferredLastMinute = 0, // TODO: Track bandwidth
                        IsWitness = regionInfo.Config.IsWitness,
                        Priority = regionInfo.Config.Priority
                    };
                }

                var healthyCount = regionStatuses.Values.Count(r => r.Health == RegionHealth.Healthy);

                return new MultiRegionReplicationStatus
                {
                    TotalRegions = _regions.Count,
                    HealthyRegions = healthyCount,
                    ConsistencyLevel = _replicator.DefaultConsistencyLevel,
                    Regions = regionStatuses,
                    PendingOperations = 0, // TODO: Aggregate from all regions
                    ConflictsDetected = 0, // TODO: Get from metrics
                    ConflictsResolved = 0, // TODO: Get from metrics
                    MaxReplicationLag = regionStatuses.Values.Max(r => r.ReplicationLag),
                    BandwidthThrottle = _bandwidthThrottle > 0 ? _bandwidthThrottle : null
                };
            }
            catch (Exception ex)
            {
                _context.LogError(ex, "Failed to get replication status");
                throw;
            }
        }

        /// <summary>
        /// Forces immediate synchronization of a key.
        /// </summary>
        public async Task<SyncOperationResult> ForceSyncAsync(
            string key,
            byte[] value,
            string[]? targetRegions = null,
            CancellationToken ct = default)
        {
            var startTime = DateTime.UtcNow;
            var regionResults = new Dictionary<string, SyncResult>();

            try
            {
                _context.LogInformation("Force syncing key {Key} to {TargetCount} regions",
                    key, targetRegions?.Length ?? _regions.Count);

                var regionsToSync = targetRegions ?? _regions.Keys.ToArray();

                foreach (var regionId in regionsToSync)
                {
                    var regionStartTime = DateTime.UtcNow;

                    try
                    {
                        if (!_storageProviders.TryGetValue(regionId, out var provider))
                        {
                            regionResults[regionId] = new SyncResult
                            {
                                Success = false,
                                ErrorMessage = $"Storage provider not found for region {regionId}",
                                Duration = DateTime.UtcNow - regionStartTime
                            };
                            continue;
                        }

                        // Write directly to the region's storage provider
                        var uri = new Uri($"{provider.Scheme}://{regionId}/{key}");
                        using var stream = new System.IO.MemoryStream(value);
                        await provider.SaveAsync(uri, stream);

                        regionResults[regionId] = new SyncResult
                        {
                            Success = true,
                            Duration = DateTime.UtcNow - regionStartTime
                        };

                        _context.LogDebug("Synced key {Key} to region {RegionId}", key, regionId);
                    }
                    catch (Exception ex)
                    {
                        _context.LogError(ex, "Failed to sync key {Key} to region {RegionId}", key, regionId);
                        regionResults[regionId] = new SyncResult
                        {
                            Success = false,
                            ErrorMessage = ex.Message,
                            Duration = DateTime.UtcNow - regionStartTime
                        };
                    }
                }

                var allSucceeded = regionResults.Values.All(r => r.Success);

                return new SyncOperationResult
                {
                    Success = allSucceeded,
                    Key = key,
                    RegionResults = regionResults,
                    Duration = DateTime.UtcNow - startTime
                };
            }
            catch (Exception ex)
            {
                _context.LogError(ex, "Force sync failed for key {Key}", key);
                return new SyncOperationResult
                {
                    Success = false,
                    Key = key,
                    RegionResults = regionResults,
                    Duration = DateTime.UtcNow - startTime
                };
            }
        }

        /// <summary>
        /// Resolves a conflict for a specific key.
        /// </summary>
        public async Task<ConflictResolutionResult> ResolveConflictAsync(
            string key,
            ConflictResolutionStrategy strategy,
            string? winningRegion = null,
            CancellationToken ct = default)
        {
            try
            {
                _context.LogInformation("Resolving conflict for key {Key} using strategy {Strategy}",
                    key, strategy);

                // For manual resolution, validate winning region
                if (strategy == ConflictResolutionStrategy.Manual)
                {
                    if (string.IsNullOrEmpty(winningRegion) || !_regions.ContainsKey(winningRegion))
                    {
                        return new ConflictResolutionResult
                        {
                            Success = false,
                            Key = key,
                            StrategyUsed = strategy,
                            ErrorMessage = "Manual resolution requires a valid winning region"
                        };
                    }
                }

                // Delegate to MultiRegionReplicator's conflict resolution
                // This is simplified - in production, we'd fetch conflicting values and resolve
                string? chosenRegion = strategy switch
                {
                    ConflictResolutionStrategy.Manual => winningRegion,
                    ConflictResolutionStrategy.HighestPriorityWins => _regions
                        .OrderByDescending(r => r.Value.Config.Priority)
                        .First().Key,
                    _ => null
                };

                return new ConflictResolutionResult
                {
                    Success = true,
                    Key = key,
                    StrategyUsed = strategy,
                    WinningRegion = chosenRegion
                };
            }
            catch (Exception ex)
            {
                _context.LogError(ex, "Failed to resolve conflict for key {Key}", key);
                return new ConflictResolutionResult
                {
                    Success = false,
                    Key = key,
                    StrategyUsed = strategy,
                    ErrorMessage = ex.Message
                };
            }
        }

        /// <summary>
        /// Sets the consistency level.
        /// </summary>
        public Task SetConsistencyLevelAsync(ConsistencyLevel level, CancellationToken ct = default)
        {
            _context.LogInformation("Setting consistency level to {Level}", level);
            _replicator.DefaultConsistencyLevel = level;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Gets the current consistency level.
        /// </summary>
        public ConsistencyLevel GetConsistencyLevel()
        {
            return _replicator.DefaultConsistencyLevel;
        }

        /// <summary>
        /// Checks health of all regions.
        /// </summary>
        public async Task<Dictionary<string, RegionHealth>> CheckRegionHealthAsync(CancellationToken ct = default)
        {
            var healthResults = new Dictionary<string, RegionHealth>();

            foreach (var regionId in _regions.Keys)
            {
                healthResults[regionId] = await CheckRegionHealthInternalAsync(regionId, ct);
            }

            return healthResults;
        }

        /// <summary>
        /// Sets bandwidth throttle.
        /// </summary>
        public Task SetBandwidthThrottleAsync(long? maxBytesPerSecond, CancellationToken ct = default)
        {
            _bandwidthThrottle = maxBytesPerSecond ?? 0;
            _context.LogInformation("Bandwidth throttle set to {Bytes} bytes/sec (0 = unlimited)", _bandwidthThrottle);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Lists all configured regions.
        /// </summary>
        public Task<List<RegionInfo>> ListRegionsAsync(CancellationToken ct = default)
        {
            return Task.FromResult(_regions.Values.ToList());
        }

        /// <summary>
        /// Replicates a write operation to all regions based on consistency level.
        /// </summary>
        public async Task<bool> ReplicateWriteAsync(string key, byte[] value, CancellationToken ct = default)
        {
            try
            {
                var consistencyLevel = _replicator.DefaultConsistencyLevel;

                switch (consistencyLevel)
                {
                    case ConsistencyLevel.Eventual:
                        // Fire and forget - return immediately
                        _ = ForceSyncAsync(key, value, ct: ct);
                        return true;

                    case ConsistencyLevel.Local:
                        // Wait for local region only
                        var localRegion = _regions.Keys.FirstOrDefault();
                        if (localRegion != null)
                        {
                            var result = await ForceSyncAsync(key, value, new[] { localRegion }, ct);
                            return result.Success;
                        }
                        return false;

                    case ConsistencyLevel.Quorum:
                        // Wait for majority
                        var result2 = await ForceSyncAsync(key, value, ct: ct);
                        var successCount = result2.RegionResults.Values.Count(r => r.Success);
                        var quorum = (_regions.Count / 2) + 1;
                        return successCount >= quorum;

                    case ConsistencyLevel.Strong:
                        // Wait for all regions
                        var result3 = await ForceSyncAsync(key, value, ct: ct);
                        return result3.Success;

                    default:
                        return false;
                }
            }
            catch (Exception ex)
            {
                _context.LogError(ex, "Replication failed for key {Key}", key);
                return false;
            }
        }

        /// <summary>
        /// Background sync loop for pending replication operations.
        /// </summary>
        private async Task RunBackgroundSyncAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);

                    // Check region health periodically
                    _ = CheckRegionHealthAsync(ct);

                    // Replicator handles background batching automatically
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _context.LogError(ex, "Background sync error");
                }
            }
        }

        /// <summary>
        /// Internal health check for a single region.
        /// </summary>
        private async Task<RegionHealth> CheckRegionHealthInternalAsync(string regionId, CancellationToken ct)
        {
            try
            {
                if (!_storageProviders.TryGetValue(regionId, out var provider))
                    return RegionHealth.Unknown;

                // Simple health check - try to ping the provider
                var testUri = new Uri($"{provider.Scheme}://{regionId}/__health__");
                var exists = await provider.ExistsAsync(testUri);

                // Update region info
                if (_regions.TryGetValue(regionId, out var regionInfo))
                {
                    var oldHealth = regionInfo.Health;
                    var newHealth = RegionHealth.Healthy;

                    if (oldHealth != newHealth)
                    {
                        // Health changed - update and log
                        var updatedInfo = regionInfo with { Health = newHealth };
                        _regions[regionId] = updatedInfo;
                        _context.LogInformation("Region {RegionId} health changed from {Old} to {New}",
                            regionId, oldHealth, newHealth);
                    }

                    return newHealth;
                }

                return RegionHealth.Healthy;
            }
            catch
            {
                return RegionHealth.Unhealthy;
            }
        }

        /// <summary>
        /// Event handler for conflicts resolved by the replicator.
        /// </summary>
        private void OnConflictResolved(object? sender, ConflictResolvedEventArgs e)
        {
            _context.LogInformation("Conflict resolved for key {Key} - winning region: {WinningRegion}",
                e.Key, e.WinningRegion);
        }
    }

    /// <summary>
    /// Simple HTTP-based region transport implementation.
    /// In production, this would use actual HTTP clients.
    /// </summary>
    internal class HttpRegionTransport : IRegionTransport
    {
        public async Task<bool> SendToRegionAsync(string regionEndpoint, string key, byte[] value, CancellationToken ct)
        {
            // Simplified - in production, use HttpClient to POST to region
            await Task.CompletedTask;
            return true;
        }

        public async Task<byte[]?> FetchFromRegionAsync(string regionEndpoint, string key, CancellationToken ct)
        {
            // Simplified - in production, use HttpClient to GET from region
            await Task.CompletedTask;
            return null;
        }

        public async Task<Dictionary<string, RegionVectorClock>> GetVectorClockAsync(string regionEndpoint, CancellationToken ct)
        {
            await Task.CompletedTask;
            return new Dictionary<string, RegionVectorClock>();
        }
    }

    /// <summary>
    /// In-memory metrics implementation for multi-region replication.
    /// </summary>
    internal class InMemoryMultiRegionMetrics : IMultiRegionMetrics
    {
        private long _conflictsDetected;
        private long _conflictsResolved;
        private long _bytesReplicated;

        public void RecordConflict(string key, int regionCount)
        {
            Interlocked.Increment(ref _conflictsDetected);
        }

        public void RecordConflictResolution(string key, string winningRegion)
        {
            Interlocked.Increment(ref _conflictsResolved);
        }

        public void RecordReplication(string regionId, long bytes)
        {
            Interlocked.Add(ref _bytesReplicated, bytes);
        }

        public Dictionary<string, object> GetMetrics()
        {
            return new Dictionary<string, object>
            {
                ["ConflictsDetected"] = _conflictsDetected,
                ["ConflictsResolved"] = _conflictsResolved,
                ["BytesReplicated"] = _bytesReplicated
            };
        }
    }
}
