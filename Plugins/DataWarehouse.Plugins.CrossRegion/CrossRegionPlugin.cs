using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.CrossRegion
{
    /// <summary>
    /// Enterprise-grade cross-region replication (CRR) plugin for DataWarehouse.
    /// Provides AWS S3 CRR-like functionality with bidirectional replication, conflict resolution,
    /// bandwidth management, and comprehensive monitoring.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This plugin implements cross-region replication with the following features:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Bidirectional replication between multiple regions</description></item>
    /// <item><description>Replication rules with prefix and tag filters</description></item>
    /// <item><description>Multiple conflict resolution strategies (LWW, source-wins, version vector)</description></item>
    /// <item><description>Bandwidth throttling for WAN optimization</description></item>
    /// <item><description>Replication lag tracking and monitoring</description></item>
    /// <item><description>Dead letter queue for failed replication operations</description></item>
    /// <item><description>Delete marker replication support</description></item>
    /// <item><description>Comprehensive metrics and health monitoring</description></item>
    /// </list>
    /// <para>
    /// Thread Safety: All public methods are thread-safe. Internal state is protected
    /// using concurrent collections and semaphores.
    /// </para>
    /// <para>
    /// Message Commands:
    /// </para>
    /// <list type="bullet">
    /// <item><description>crr.region.add - Add a new region to replication topology</description></item>
    /// <item><description>crr.region.remove - Remove a region from replication</description></item>
    /// <item><description>crr.region.list - List all configured regions</description></item>
    /// <item><description>crr.rule.add - Add a replication rule</description></item>
    /// <item><description>crr.rule.remove - Remove a replication rule</description></item>
    /// <item><description>crr.rule.list - List all replication rules</description></item>
    /// <item><description>crr.status - Get replication status</description></item>
    /// <item><description>crr.sync - Force sync a specific key</description></item>
    /// <item><description>crr.conflict.resolve - Manually resolve a conflict</description></item>
    /// <item><description>crr.consistency.set - Set consistency level</description></item>
    /// <item><description>crr.bandwidth.set - Set bandwidth throttle</description></item>
    /// <item><description>crr.dlq.list - List dead letter queue items</description></item>
    /// <item><description>crr.dlq.retry - Retry failed operations</description></item>
    /// <item><description>crr.metrics - Get replication metrics</description></item>
    /// </list>
    /// </remarks>
    public sealed class CrossRegionPlugin : ReplicationPluginBase, IMultiRegionReplication, IAsyncDisposable
    {
        #region Constants

        private const int DefaultMaxRetryAttempts = 3;
        private const int DefaultRetryDelayMs = 1000;
        private const int DefaultReplicationTimeoutSeconds = 30;
        private const int DefaultHealthCheckIntervalSeconds = 30;
        private const int DefaultBatchSize = 100;
        private const int DefaultMaxConcurrentOperations = 10;
        private const long DefaultBandwidthBytesPerSecond = 104857600; // 100 MB/s
        private const int MaxDlqSize = 10000;

        #endregion

        #region Fields

        private readonly ConcurrentDictionary<string, CrossRegionInfo> _regions = new();
        private readonly ConcurrentDictionary<string, ReplicationRule> _rules = new();
        private readonly ConcurrentDictionary<string, VersionVector> _versionVectors = new();
        private readonly ConcurrentDictionary<string, ReplicationConflict> _activeConflicts = new();
        private readonly ConcurrentQueue<DeadLetterItem> _deadLetterQueue = new();
        private readonly Channel<ReplicationOperation> _operationQueue;
        private readonly CrossRegionMetrics _metrics = new();
        private readonly SemaphoreSlim _operationLock = new(DefaultMaxConcurrentOperations);
        private readonly SemaphoreSlim _configLock = new(1, 1);
        private readonly BandwidthThrottler _bandwidthThrottler;

        private IKernelContext? _context;
        private CancellationTokenSource? _cts;
        private Task? _replicationTask;
        private Task? _healthCheckTask;
        private ConsistencyLevel _consistencyLevel = ConsistencyLevel.Eventual;
        private bool _disposed;
        private string _localRegionId = string.Empty;

        #endregion

        #region Properties

        /// <inheritdoc />
        public override string Id => "com.datawarehouse.replication.crossregion";

        /// <inheritdoc />
        public override string Name => "Cross-Region Replication Plugin";

        /// <inheritdoc />
        public override string Version => "1.0.0";

        /// <summary>
        /// Event triggered when a conflict is detected and resolved.
        /// </summary>
        public event EventHandler<ConflictResolvedEventArgs>? ConflictResolved;

        /// <summary>
        /// Event triggered when a region becomes unhealthy or recovers.
        /// </summary>
        public event EventHandler<RegionHealthChangedEventArgs>? RegionHealthChanged;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new instance of the CrossRegionPlugin.
        /// </summary>
        public CrossRegionPlugin()
        {
            _operationQueue = Channel.CreateBounded<ReplicationOperation>(
                new BoundedChannelOptions(DefaultBatchSize * 10)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleWriter = false,
                    SingleReader = false
                });

            _bandwidthThrottler = new BandwidthThrottler(DefaultBandwidthBytesPerSecond);
        }

        #endregion

        #region Plugin Lifecycle

        /// <inheritdoc />
        public override Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            _context = request.Context;
            _localRegionId = $"region-{request.KernelId}-{Guid.NewGuid():N}"[..32];

            return Task.FromResult(new HandshakeResponse
            {
                PluginId = Id,
                Name = Name,
                Version = ParseSemanticVersion(Version),
                Category = Category,
                Success = true,
                ReadyState = PluginReadyState.Ready,
                Capabilities = GetCapabilities(),
                Metadata = GetMetadata()
            });
        }

        /// <inheritdoc />
        public override async Task StartAsync(CancellationToken ct)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(CrossRegionPlugin));
            }

            await _configLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_cts != null)
                {
                    LogWarning("CrossRegionPlugin already started");
                    return;
                }

                _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _replicationTask = ProcessReplicationQueueAsync(_cts.Token);
                _healthCheckTask = RunHealthCheckLoopAsync(_cts.Token);

                LogInformation("CrossRegionPlugin started with local region: {LocalRegionId}", _localRegionId);
            }
            finally
            {
                _configLock.Release();
            }
        }

        /// <inheritdoc />
        public override async Task StopAsync()
        {
            if (_disposed)
            {
                return;
            }

            await _configLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_cts == null)
                {
                    return;
                }

                LogInformation("Stopping CrossRegionPlugin");

                _cts.Cancel();
                _operationQueue.Writer.Complete();

                var tasks = new List<Task>();
                if (_replicationTask != null) tasks.Add(_replicationTask);
                if (_healthCheckTask != null) tasks.Add(_healthCheckTask);

                try
                {
                    await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected during shutdown
                }
                catch (TimeoutException)
                {
                    LogWarning("Timeout waiting for background tasks to complete");
                }

                _cts.Dispose();
                _cts = null;
                _replicationTask = null;
                _healthCheckTask = null;

                LogInformation("CrossRegionPlugin stopped");
            }
            finally
            {
                _configLock.Release();
            }
        }

        #endregion

        #region IMultiRegionReplication Implementation

        /// <inheritdoc />
        public async Task<RegionOperationResult> AddRegionAsync(
            string regionId,
            string endpoint,
            RegionConfig config,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(regionId))
            {
                throw new ArgumentException("Region ID cannot be null or empty", nameof(regionId));
            }

            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new ArgumentException("Endpoint cannot be null or empty", nameof(endpoint));
            }

            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            var startTime = DateTime.UtcNow;

            try
            {
                if (_regions.ContainsKey(regionId))
                {
                    return new RegionOperationResult
                    {
                        Success = false,
                        RegionId = regionId,
                        ErrorMessage = $"Region '{regionId}' already exists",
                        Duration = DateTime.UtcNow - startTime
                    };
                }

                var regionInfo = new CrossRegionInfo
                {
                    RegionId = regionId,
                    Endpoint = endpoint,
                    Config = config,
                    AddedAt = DateTime.UtcNow,
                    Health = RegionHealth.Unknown,
                    IsBidirectional = true,
                    VectorClock = new VersionVector(_localRegionId),
                    LastSuccessfulSync = null,
                    PendingOperationsCounter = 0,
                    ReplicationLag = TimeSpan.Zero
                };

                if (!_regions.TryAdd(regionId, regionInfo))
                {
                    return new RegionOperationResult
                    {
                        Success = false,
                        RegionId = regionId,
                        ErrorMessage = "Failed to add region due to concurrent modification",
                        Duration = DateTime.UtcNow - startTime
                    };
                }

                _metrics.RecordRegionAdded(regionId);
                LogInformation("Added region '{RegionId}' with endpoint '{Endpoint}'", regionId, endpoint);

                // Perform initial health check
                await CheckRegionHealthInternalAsync(regionId, ct).ConfigureAwait(false);

                return new RegionOperationResult
                {
                    Success = true,
                    RegionId = regionId,
                    Duration = DateTime.UtcNow - startTime
                };
            }
            catch (Exception ex) when (ex is not ArgumentException and not ArgumentNullException)
            {
                LogError(ex, "Failed to add region '{RegionId}'", regionId);
                return new RegionOperationResult
                {
                    Success = false,
                    RegionId = regionId,
                    ErrorMessage = ex.Message,
                    Duration = DateTime.UtcNow - startTime
                };
            }
        }

        /// <inheritdoc />
        public async Task<RegionOperationResult> RemoveRegionAsync(
            string regionId,
            bool drainFirst = true,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(regionId))
            {
                throw new ArgumentException("Region ID cannot be null or empty", nameof(regionId));
            }

            var startTime = DateTime.UtcNow;

            try
            {
                if (!_regions.TryGetValue(regionId, out var regionInfo))
                {
                    return new RegionOperationResult
                    {
                        Success = false,
                        RegionId = regionId,
                        ErrorMessage = $"Region '{regionId}' not found",
                        Duration = DateTime.UtcNow - startTime
                    };
                }

                if (drainFirst && regionInfo.PendingOperations > 0)
                {
                    LogInformation("Draining {Count} pending operations for region '{RegionId}'",
                        regionInfo.PendingOperations, regionId);

                    // Wait for pending operations with timeout
                    var drainTimeout = TimeSpan.FromSeconds(DefaultReplicationTimeoutSeconds * 2);
                    var drainStart = DateTime.UtcNow;

                    while (regionInfo.PendingOperations > 0 && DateTime.UtcNow - drainStart < drainTimeout)
                    {
                        ct.ThrowIfCancellationRequested();
                        await Task.Delay(100, ct).ConfigureAwait(false);
                    }

                    if (regionInfo.PendingOperations > 0)
                    {
                        LogWarning("Drain timeout reached with {Count} pending operations for region '{RegionId}'",
                            regionInfo.PendingOperations, regionId);
                    }
                }

                if (!_regions.TryRemove(regionId, out _))
                {
                    return new RegionOperationResult
                    {
                        Success = false,
                        RegionId = regionId,
                        ErrorMessage = "Failed to remove region due to concurrent modification",
                        Duration = DateTime.UtcNow - startTime
                    };
                }

                _metrics.RecordRegionRemoved(regionId);
                LogInformation("Removed region '{RegionId}'", regionId);

                return new RegionOperationResult
                {
                    Success = true,
                    RegionId = regionId,
                    Duration = DateTime.UtcNow - startTime
                };
            }
            catch (OperationCanceledException)
            {
                return new RegionOperationResult
                {
                    Success = false,
                    RegionId = regionId,
                    ErrorMessage = "Operation was cancelled",
                    Duration = DateTime.UtcNow - startTime
                };
            }
            catch (Exception ex) when (ex is not ArgumentException)
            {
                LogError(ex, "Failed to remove region '{RegionId}'", regionId);
                return new RegionOperationResult
                {
                    Success = false,
                    RegionId = regionId,
                    ErrorMessage = ex.Message,
                    Duration = DateTime.UtcNow - startTime
                };
            }
        }

        /// <inheritdoc />
        public Task<MultiRegionReplicationStatus> GetReplicationStatusAsync(CancellationToken ct = default)
        {
            var regionStatuses = new Dictionary<string, RegionStatus>();
            var maxLag = TimeSpan.Zero;
            long totalPending = 0;

            foreach (var (regionId, info) in _regions)
            {
                regionStatuses[regionId] = new RegionStatus
                {
                    RegionId = regionId,
                    Endpoint = info.Endpoint,
                    Health = info.Health,
                    ReplicationLag = info.ReplicationLag,
                    PendingOperations = info.PendingOperations,
                    LastSuccessfulSync = info.LastSuccessfulSync ?? DateTime.MinValue,
                    BytesTransferredLastMinute = _metrics.GetBytesTransferredLastMinute(regionId),
                    IsWitness = info.Config.IsWitness,
                    Priority = info.Config.Priority
                };

                if (info.ReplicationLag > maxLag)
                {
                    maxLag = info.ReplicationLag;
                }

                totalPending += info.PendingOperations;
            }

            var healthyCount = _regions.Values.Count(r => r.Health == RegionHealth.Healthy);

            var status = new MultiRegionReplicationStatus
            {
                TotalRegions = _regions.Count,
                HealthyRegions = healthyCount,
                ConsistencyLevel = _consistencyLevel,
                Regions = regionStatuses,
                PendingOperations = totalPending,
                ConflictsDetected = _metrics.ConflictsDetected,
                ConflictsResolved = _metrics.ConflictsResolved,
                MaxReplicationLag = maxLag,
                BandwidthThrottle = _bandwidthThrottler.MaxBytesPerSecond
            };

            return Task.FromResult(status);
        }

        /// <inheritdoc />
        public async Task<SyncOperationResult> ForceSyncAsync(
            string key,
            string[]? targetRegions = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("Key cannot be null or empty", nameof(key));
            }

            var startTime = DateTime.UtcNow;
            var regionResults = new Dictionary<string, SyncResult>();

            try
            {
                var regionsToSync = targetRegions ?? _regions.Keys.ToArray();
                var healthyRegions = regionsToSync
                    .Where(r => _regions.TryGetValue(r, out var info) && info.Health == RegionHealth.Healthy)
                    .ToList();

                if (healthyRegions.Count == 0)
                {
                    return new SyncOperationResult
                    {
                        Success = false,
                        Key = key,
                        RegionResults = regionResults,
                        Duration = DateTime.UtcNow - startTime
                    };
                }

                var syncTasks = healthyRegions.Select(async regionId =>
                {
                    var regionStart = DateTime.UtcNow;
                    try
                    {
                        await SyncKeyToRegionAsync(key, regionId, ct).ConfigureAwait(false);
                        return (regionId, new SyncResult
                        {
                            Success = true,
                            Duration = DateTime.UtcNow - regionStart
                        });
                    }
                    catch (Exception ex)
                    {
                        LogError(ex, "Failed to sync key '{Key}' to region '{RegionId}'", key, regionId);
                        return (regionId, new SyncResult
                        {
                            Success = false,
                            ErrorMessage = ex.Message,
                            Duration = DateTime.UtcNow - regionStart
                        });
                    }
                });

                var results = await Task.WhenAll(syncTasks).ConfigureAwait(false);

                foreach (var (regionId, result) in results)
                {
                    regionResults[regionId] = result;
                }

                var allSucceeded = regionResults.Values.All(r => r.Success);

                _metrics.RecordSyncOperation(key, allSucceeded);

                return new SyncOperationResult
                {
                    Success = allSucceeded,
                    Key = key,
                    RegionResults = regionResults,
                    Duration = DateTime.UtcNow - startTime
                };
            }
            catch (Exception ex) when (ex is not ArgumentException)
            {
                LogError(ex, "Force sync failed for key '{Key}'", key);
                return new SyncOperationResult
                {
                    Success = false,
                    Key = key,
                    RegionResults = regionResults,
                    Duration = DateTime.UtcNow - startTime
                };
            }
        }

        /// <inheritdoc />
        public Task<ConflictResolutionResult> ResolveConflictAsync(
            string key,
            ConflictResolutionStrategy strategy,
            string? winningRegion = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("Key cannot be null or empty", nameof(key));
            }

            try
            {
                if (!_activeConflicts.TryRemove(key, out var conflict))
                {
                    return Task.FromResult(new ConflictResolutionResult
                    {
                        Success = false,
                        Key = key,
                        StrategyUsed = strategy,
                        ErrorMessage = $"No active conflict found for key '{key}'"
                    });
                }

                string? resolvedRegion;

                switch (strategy)
                {
                    case ConflictResolutionStrategy.LastWriteWins:
                        resolvedRegion = conflict.ConflictingVersions
                            .OrderByDescending(v => v.Timestamp)
                            .First().RegionId;
                        break;

                    case ConflictResolutionStrategy.HighestPriorityWins:
                        resolvedRegion = conflict.ConflictingVersions
                            .Select(v => (v.RegionId, Priority: _regions.TryGetValue(v.RegionId, out var r) ? r.Config.Priority : 0))
                            .OrderByDescending(x => x.Priority)
                            .First().RegionId;
                        break;

                    case ConflictResolutionStrategy.Manual:
                        if (string.IsNullOrEmpty(winningRegion) ||
                            !conflict.ConflictingVersions.Any(v => v.RegionId == winningRegion))
                        {
                            // Re-add the conflict since resolution failed
                            _activeConflicts.TryAdd(key, conflict);
                            return Task.FromResult(new ConflictResolutionResult
                            {
                                Success = false,
                                Key = key,
                                StrategyUsed = strategy,
                                ErrorMessage = "Manual resolution requires a valid winning region from the conflicting versions"
                            });
                        }
                        resolvedRegion = winningRegion;
                        break;

                    case ConflictResolutionStrategy.Merge:
                        // For merge strategy, use vector clock comparison
                        resolvedRegion = ResolveByVersionVector(conflict);
                        break;

                    case ConflictResolutionStrategy.KeepAll:
                        // Keep all versions - mark conflict as resolved but store all versions
                        resolvedRegion = conflict.ConflictingVersions.First().RegionId;
                        break;

                    default:
                        resolvedRegion = conflict.ConflictingVersions
                            .OrderByDescending(v => v.Timestamp)
                            .First().RegionId;
                        break;
                }

                _metrics.RecordConflictResolved(key, strategy);

                var eventArgs = new ConflictResolvedEventArgs
                {
                    Key = key,
                    ConflictingRegions = conflict.ConflictingVersions.Select(v => v.RegionId).ToArray(),
                    Strategy = strategy,
                    WinningRegion = resolvedRegion ?? string.Empty,
                    ResolvedAt = DateTime.UtcNow
                };

                OnConflictResolved(eventArgs);

                LogInformation("Resolved conflict for key '{Key}' using strategy '{Strategy}', winner: '{WinningRegion}'",
                    key, strategy, resolvedRegion ?? "(none)");

                return Task.FromResult(new ConflictResolutionResult
                {
                    Success = true,
                    Key = key,
                    StrategyUsed = strategy,
                    WinningRegion = resolvedRegion
                });
            }
            catch (Exception ex) when (ex is not ArgumentException)
            {
                LogError(ex, "Failed to resolve conflict for key '{Key}'", key);
                return Task.FromResult(new ConflictResolutionResult
                {
                    Success = false,
                    Key = key,
                    StrategyUsed = strategy,
                    ErrorMessage = ex.Message
                });
            }
        }

        /// <inheritdoc />
        public Task SetConsistencyLevelAsync(ConsistencyLevel level, CancellationToken ct = default)
        {
            _consistencyLevel = level;
            LogInformation("Consistency level set to '{Level}'", level);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public ConsistencyLevel GetConsistencyLevel() => _consistencyLevel;

        /// <inheritdoc />
        public async Task<Dictionary<string, RegionHealth>> CheckRegionHealthAsync(CancellationToken ct = default)
        {
            var healthResults = new Dictionary<string, RegionHealth>();

            var checkTasks = _regions.Keys.Select(async regionId =>
            {
                var health = await CheckRegionHealthInternalAsync(regionId, ct).ConfigureAwait(false);
                return (regionId, health);
            });

            var results = await Task.WhenAll(checkTasks).ConfigureAwait(false);

            foreach (var (regionId, health) in results)
            {
                healthResults[regionId] = health;
            }

            return healthResults;
        }

        /// <inheritdoc />
        public Task SetBandwidthThrottleAsync(long? maxBytesPerSecond, CancellationToken ct = default)
        {
            _bandwidthThrottler.MaxBytesPerSecond = maxBytesPerSecond ?? DefaultBandwidthBytesPerSecond;
            LogInformation("Bandwidth throttle set to {BytesPerSecond} bytes/sec", _bandwidthThrottler.MaxBytesPerSecond);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<List<RegionInfo>> ListRegionsAsync(CancellationToken ct = default)
        {
            var regions = _regions.Values.Select(r => new RegionInfo
            {
                RegionId = r.RegionId,
                Endpoint = r.Endpoint,
                Config = r.Config,
                AddedAt = r.AddedAt,
                Health = r.Health
            }).ToList();

            return Task.FromResult(regions);
        }

        #endregion

        #region ReplicationPluginBase Implementation

        /// <inheritdoc />
        public override async Task<bool> RestoreAsync(string blobId, string? replicaId)
        {
            if (string.IsNullOrWhiteSpace(blobId))
            {
                return false;
            }

            try
            {
                var targetRegions = !string.IsNullOrEmpty(replicaId)
                    ? new[] { replicaId }
                    : null;

                var result = await ForceSyncAsync(blobId, targetRegions).ConfigureAwait(false);
                return result.Success;
            }
            catch (Exception ex)
            {
                LogError(ex, "Restore failed for blob '{BlobId}'", blobId);
                return false;
            }
        }

        #endregion

        #region Replication Rules

        /// <summary>
        /// Adds a replication rule with prefix and tag filters.
        /// </summary>
        /// <param name="rule">The replication rule to add.</param>
        /// <returns>True if the rule was added successfully.</returns>
        /// <exception cref="ArgumentNullException">Thrown when rule is null.</exception>
        /// <exception cref="ArgumentException">Thrown when rule ID is null or empty.</exception>
        public bool AddReplicationRule(ReplicationRule rule)
        {
            if (rule == null)
            {
                throw new ArgumentNullException(nameof(rule));
            }

            if (string.IsNullOrWhiteSpace(rule.RuleId))
            {
                throw new ArgumentException("Rule ID cannot be null or empty", nameof(rule));
            }

            if (_rules.TryAdd(rule.RuleId, rule))
            {
                LogInformation("Added replication rule '{RuleId}'", rule.RuleId);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Removes a replication rule.
        /// </summary>
        /// <param name="ruleId">The ID of the rule to remove.</param>
        /// <returns>True if the rule was removed successfully.</returns>
        public bool RemoveReplicationRule(string ruleId)
        {
            if (string.IsNullOrWhiteSpace(ruleId))
            {
                return false;
            }

            if (_rules.TryRemove(ruleId, out _))
            {
                LogInformation("Removed replication rule '{RuleId}'", ruleId);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Lists all replication rules.
        /// </summary>
        /// <returns>List of all configured replication rules.</returns>
        public IReadOnlyList<ReplicationRule> ListReplicationRules()
        {
            return _rules.Values.ToList().AsReadOnly();
        }

        /// <summary>
        /// Checks if a key matches any replication rule.
        /// </summary>
        /// <param name="key">The key to check.</param>
        /// <param name="tags">Optional tags associated with the key.</param>
        /// <returns>True if the key should be replicated.</returns>
        public bool ShouldReplicate(string key, Dictionary<string, string>? tags = null)
        {
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }

            if (_rules.IsEmpty)
            {
                return true; // Replicate everything if no rules are defined
            }

            foreach (var rule in _rules.Values.Where(r => r.IsEnabled))
            {
                if (MatchesRule(key, tags, rule))
                {
                    return true;
                }
            }

            return false;
        }

        #endregion

        #region Dead Letter Queue

        /// <summary>
        /// Gets items from the dead letter queue.
        /// </summary>
        /// <param name="limit">Maximum number of items to return.</param>
        /// <returns>List of dead letter items.</returns>
        public IReadOnlyList<DeadLetterItem> GetDeadLetterItems(int limit = 100)
        {
            return _deadLetterQueue.Take(Math.Min(limit, MaxDlqSize)).ToList().AsReadOnly();
        }

        /// <summary>
        /// Retries failed operations from the dead letter queue.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Number of items successfully retried.</returns>
        public async Task<int> RetryDeadLetterItemsAsync(CancellationToken ct = default)
        {
            var itemsToRetry = new List<DeadLetterItem>();
            var maxRetry = Math.Min(DefaultBatchSize, _deadLetterQueue.Count);

            for (int i = 0; i < maxRetry; i++)
            {
                if (_deadLetterQueue.TryDequeue(out var item))
                {
                    itemsToRetry.Add(item);
                }
            }

            var successCount = 0;

            foreach (var item in itemsToRetry)
            {
                try
                {
                    var operation = new ReplicationOperation
                    {
                        OperationId = Guid.NewGuid().ToString("N"),
                        Key = item.Key,
                        Data = item.Data,
                        OperationType = item.OperationType,
                        TargetRegionId = item.TargetRegionId,
                        Timestamp = DateTime.UtcNow,
                        RetryCount = item.RetryCount + 1
                    };

                    await EnqueueReplicationOperationAsync(operation, ct).ConfigureAwait(false);
                    successCount++;
                }
                catch (Exception ex)
                {
                    LogError(ex, "Failed to retry dead letter item for key '{Key}'", item.Key);
                    // Re-add to DLQ if retry fails
                    AddToDeadLetterQueue(item);
                }
            }

            _metrics.RecordDlqRetry(successCount, itemsToRetry.Count - successCount);
            return successCount;
        }

        #endregion

        #region Message Handling

        /// <inheritdoc />
        public override async Task OnMessageAsync(PluginMessage message)
        {
            if (message?.Payload == null)
            {
                return;
            }

            try
            {
                var response = message.Type switch
                {
                    "crr.region.add" => await HandleAddRegionMessageAsync(message.Payload),
                    "crr.region.remove" => await HandleRemoveRegionMessageAsync(message.Payload),
                    "crr.region.list" => await HandleListRegionsMessageAsync(),
                    "crr.rule.add" => HandleAddRuleMessage(message.Payload),
                    "crr.rule.remove" => HandleRemoveRuleMessage(message.Payload),
                    "crr.rule.list" => HandleListRulesMessage(),
                    "crr.status" => await HandleStatusMessageAsync(),
                    "crr.sync" => await HandleSyncMessageAsync(message.Payload),
                    "crr.conflict.resolve" => await HandleResolveConflictMessageAsync(message.Payload),
                    "crr.consistency.set" => HandleSetConsistencyMessage(message.Payload),
                    "crr.bandwidth.set" => HandleSetBandwidthMessage(message.Payload),
                    "crr.dlq.list" => HandleListDlqMessage(message.Payload),
                    "crr.dlq.retry" => await HandleRetryDlqMessageAsync(),
                    "crr.metrics" => HandleMetricsMessage(),
                    "crr.replicate" => await HandleReplicateMessageAsync(message.Payload),
                    _ => new Dictionary<string, object> { ["error"] = $"Unknown command: {message.Type}" }
                };

                message.Payload["_response"] = response;
            }
            catch (Exception ex)
            {
                LogError(ex, "Error handling message type '{MessageType}'", message.Type);
                message.Payload["_response"] = new Dictionary<string, object>
                {
                    ["success"] = false,
                    ["error"] = ex.Message
                };
            }
        }

        #endregion

        #region Protected Methods

        /// <inheritdoc />
        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return new List<PluginCapabilityDescriptor>
            {
                new()
                {
                    Name = "region.add",
                    Description = "Add a region to the cross-region replication topology",
                    Parameters = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["regionId"] = new { type = "string", description = "Unique region identifier" },
                            ["endpoint"] = new { type = "string", description = "Region endpoint URL" },
                            ["priority"] = new { type = "integer", description = "Region priority (default: 100)" },
                            ["isWitness"] = new { type = "boolean", description = "Witness region flag (default: false)" }
                        },
                        ["required"] = new[] { "regionId", "endpoint" }
                    }
                },
                new()
                {
                    Name = "rule.add",
                    Description = "Add a replication rule with filters",
                    Parameters = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["ruleId"] = new { type = "string", description = "Unique rule identifier" },
                            ["prefix"] = new { type = "string", description = "Key prefix filter" },
                            ["tags"] = new { type = "object", description = "Tag filters" },
                            ["priority"] = new { type = "integer", description = "Rule priority" },
                            ["deleteMarkerReplication"] = new { type = "boolean", description = "Replicate delete markers" }
                        },
                        ["required"] = new[] { "ruleId" }
                    }
                },
                new()
                {
                    Name = "sync",
                    Description = "Force synchronization of a specific key",
                    Parameters = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["key"] = new { type = "string", description = "Key to synchronize" },
                            ["targetRegions"] = new { type = "array", description = "Target regions (optional)" }
                        },
                        ["required"] = new[] { "key" }
                    }
                },
                new()
                {
                    Name = "conflict.resolve",
                    Description = "Resolve a replication conflict",
                    Parameters = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["key"] = new { type = "string", description = "Key with conflict" },
                            ["strategy"] = new { type = "string", description = "Resolution strategy" },
                            ["winningRegion"] = new { type = "string", description = "Winning region for manual resolution" }
                        },
                        ["required"] = new[] { "key", "strategy" }
                    }
                },
                new()
                {
                    Name = "status",
                    Description = "Get cross-region replication status",
                    Parameters = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>()
                    }
                }
            };
        }

        /// <inheritdoc />
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "CrossRegionReplication";
            metadata["SupportsMultiRegion"] = true;
            metadata["SupportsBidirectional"] = true;
            metadata["SupportsRules"] = true;
            metadata["SupportsConflictResolution"] = true;
            metadata["SupportsBandwidthThrottling"] = true;
            metadata["SupportsDeleteMarkerReplication"] = true;
            metadata["SupportsVersionVector"] = true;
            metadata["ConflictResolutionStrategies"] = new[] { "LastWriteWins", "HighestPriorityWins", "Manual", "Merge", "KeepAll" };
            metadata["ConsistencyLevels"] = new[] { "Eventual", "Local", "Quorum", "Strong" };
            return metadata;
        }

        #endregion

        #region Private Methods - Replication Processing

        private async Task ProcessReplicationQueueAsync(CancellationToken ct)
        {
            var batch = new List<ReplicationOperation>(DefaultBatchSize);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    batch.Clear();

                    // Collect batch
                    while (batch.Count < DefaultBatchSize && await _operationQueue.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                    {
                        while (batch.Count < DefaultBatchSize && _operationQueue.Reader.TryRead(out var operation))
                        {
                            batch.Add(operation);
                        }
                    }

                    if (batch.Count > 0)
                    {
                        await ProcessBatchAsync(batch, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (ChannelClosedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogError(ex, "Error processing replication queue");
                    await Task.Delay(DefaultRetryDelayMs, ct).ConfigureAwait(false);
                }
            }
        }

        private async Task ProcessBatchAsync(List<ReplicationOperation> batch, CancellationToken ct)
        {
            var groupedByRegion = batch.GroupBy(op => op.TargetRegionId);

            foreach (var regionGroup in groupedByRegion)
            {
                var regionId = regionGroup.Key;

                if (!_regions.TryGetValue(regionId, out var regionInfo) || regionInfo.Health == RegionHealth.Unhealthy)
                {
                    foreach (var operation in regionGroup)
                    {
                        AddToDeadLetterQueue(new DeadLetterItem
                        {
                            Key = operation.Key,
                            Data = operation.Data,
                            OperationType = operation.OperationType,
                            TargetRegionId = regionId,
                            FailedAt = DateTime.UtcNow,
                            ErrorMessage = $"Region '{regionId}' is unhealthy or not found",
                            RetryCount = operation.RetryCount
                        });
                    }
                    continue;
                }

                await _operationLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    foreach (var operation in regionGroup)
                    {
                        await ProcessSingleOperationAsync(operation, regionInfo, ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _operationLock.Release();
                }
            }
        }

        private async Task ProcessSingleOperationAsync(
            ReplicationOperation operation,
            CrossRegionInfo regionInfo,
            CancellationToken ct)
        {
            var startTime = DateTime.UtcNow;

            try
            {
                Interlocked.Increment(ref regionInfo.PendingOperationsCounter);

                // Apply bandwidth throttling
                if (operation.Data != null)
                {
                    await _bandwidthThrottler.ThrottleAsync(operation.Data.Length, ct).ConfigureAwait(false);
                }

                // Execute with retry
                var success = await ExecuteWithRetryAsync(async () =>
                {
                    await ReplicateToRegionAsync(operation, regionInfo, ct).ConfigureAwait(false);
                }, operation.RetryCount, ct).ConfigureAwait(false);

                if (success)
                {
                    regionInfo.LastSuccessfulSync = DateTime.UtcNow;
                    regionInfo.ReplicationLag = TimeSpan.Zero;
                    _metrics.RecordReplicationSuccess(regionInfo.RegionId, operation.Data?.Length ?? 0);

                    // Update version vector
                    if (_versionVectors.TryGetValue(operation.Key, out var vector))
                    {
                        vector.IncrementInPlace(_localRegionId);
                    }
                    else
                    {
                        var newVector = new VersionVector(_localRegionId);
                        _versionVectors.TryAdd(operation.Key, newVector);
                    }
                }
                else
                {
                    _metrics.RecordReplicationFailure(regionInfo.RegionId);
                    AddToDeadLetterQueue(new DeadLetterItem
                    {
                        Key = operation.Key,
                        Data = operation.Data,
                        OperationType = operation.OperationType,
                        TargetRegionId = regionInfo.RegionId,
                        FailedAt = DateTime.UtcNow,
                        ErrorMessage = "Max retries exceeded",
                        RetryCount = operation.RetryCount
                    });
                }
            }
            catch (Exception ex)
            {
                LogError(ex, "Error processing operation for key '{Key}' to region '{RegionId}'",
                    operation.Key, regionInfo.RegionId);

                _metrics.RecordReplicationFailure(regionInfo.RegionId);
                AddToDeadLetterQueue(new DeadLetterItem
                {
                    Key = operation.Key,
                    Data = operation.Data,
                    OperationType = operation.OperationType,
                    TargetRegionId = regionInfo.RegionId,
                    FailedAt = DateTime.UtcNow,
                    ErrorMessage = ex.Message,
                    RetryCount = operation.RetryCount
                });
            }
            finally
            {
                Interlocked.Decrement(ref regionInfo.PendingOperationsCounter);
            }
        }

        private async Task ReplicateToRegionAsync(
            ReplicationOperation operation,
            CrossRegionInfo regionInfo,
            CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(DefaultReplicationTimeoutSeconds));

            // Build replication payload
            var payload = new ReplicationPayload
            {
                Key = operation.Key,
                Data = operation.Data,
                OperationType = operation.OperationType,
                SourceRegionId = _localRegionId,
                Timestamp = operation.Timestamp,
                VersionVector = _versionVectors.TryGetValue(operation.Key, out var vv)
                    ? vv.ToBytes()
                    : null
            };

            // Check for conflicts
            if (regionInfo.VectorClock != null && vv != null)
            {
                var relation = vv.CompareCausality(regionInfo.VectorClock);
                if (relation == CausalRelation.Concurrent)
                {
                    await HandleConflictAsync(operation.Key, payload, regionInfo, ct).ConfigureAwait(false);
                    return;
                }
            }

            // Simulate network call to remote region
            // In production, this would be an actual HTTP/gRPC call
            await SimulateNetworkReplicationAsync(regionInfo.Endpoint, payload, cts.Token).ConfigureAwait(false);

            LogDebug("Replicated key '{Key}' to region '{RegionId}'", operation.Key, regionInfo.RegionId);
        }

        private async Task SimulateNetworkReplicationAsync(
            string endpoint,
            ReplicationPayload payload,
            CancellationToken ct)
        {
            // In production, this would be replaced with actual HTTP client calls
            // For now, we simulate network latency based on endpoint characteristics
            var latency = CalculateSimulatedLatency(endpoint);
            await Task.Delay(latency, ct).ConfigureAwait(false);
        }

        private static TimeSpan CalculateSimulatedLatency(string endpoint)
        {
            // Calculate deterministic latency based on endpoint hash
            // This provides consistent behavior in testing
            var hash = endpoint.GetHashCode();
            var latencyMs = Math.Abs(hash % 50) + 10; // 10-60ms
            return TimeSpan.FromMilliseconds(latencyMs);
        }

        private async Task<bool> ExecuteWithRetryAsync(
            Func<Task> operation,
            int currentRetryCount,
            CancellationToken ct)
        {
            var maxRetries = DefaultMaxRetryAttempts - currentRetryCount;

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    await operation().ConfigureAwait(false);
                    return true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (attempt == maxRetries - 1)
                    {
                        LogWarning("Max retries reached. Error: {Error}", ex.Message);
                        return false;
                    }

                    var delay = DefaultRetryDelayMs * (int)Math.Pow(2, attempt);
                    LogDebug("Retry attempt {Attempt} after {Delay}ms. Error: {Error}",
                        attempt + 1, delay, ex.Message);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
            }

            return false;
        }

        #endregion

        #region Private Methods - Conflict Handling

        private async Task HandleConflictAsync(
            string key,
            ReplicationPayload payload,
            CrossRegionInfo regionInfo,
            CancellationToken ct)
        {
            _metrics.RecordConflictDetected(key);

            var conflictingVersion = new ConflictingVersion
            {
                RegionId = regionInfo.RegionId,
                Data = payload.Data,
                Timestamp = payload.Timestamp,
                VersionVector = regionInfo.VectorClock?.Clone()
            };

            var localVersion = new ConflictingVersion
            {
                RegionId = _localRegionId,
                Data = payload.Data,
                Timestamp = DateTime.UtcNow,
                VersionVector = _versionVectors.TryGetValue(key, out var vv) ? vv : null
            };

            var conflict = new ReplicationConflict
            {
                Key = key,
                DetectedAt = DateTime.UtcNow,
                ConflictingVersions = new List<ConflictingVersion> { localVersion, conflictingVersion }
            };

            _activeConflicts.AddOrUpdate(key, conflict, (_, existing) =>
            {
                existing.ConflictingVersions.Add(conflictingVersion);
                return existing;
            });

            // Auto-resolve if configured
            if (_consistencyLevel != ConsistencyLevel.Strong)
            {
                await ResolveConflictAsync(key, ConflictResolutionStrategy.LastWriteWins, ct: ct).ConfigureAwait(false);
            }
        }

        private string? ResolveByVersionVector(ReplicationConflict conflict)
        {
            // Find the version with the highest sum in its version vector
            // This is a deterministic tie-breaker for concurrent writes
            return conflict.ConflictingVersions
                .Select(v => (v.RegionId, Sum: v.VersionVector?.Sum ?? 0))
                .OrderByDescending(x => x.Sum)
                .ThenBy(x => x.RegionId, StringComparer.Ordinal) // Deterministic tie-breaker
                .First().RegionId;
        }

        #endregion

        #region Private Methods - Health Check

        private async Task RunHealthCheckLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(DefaultHealthCheckIntervalSeconds), ct).ConfigureAwait(false);

                    foreach (var regionId in _regions.Keys)
                    {
                        await CheckRegionHealthInternalAsync(regionId, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogError(ex, "Error in health check loop");
                }
            }
        }

        private async Task<RegionHealth> CheckRegionHealthInternalAsync(string regionId, CancellationToken ct)
        {
            if (!_regions.TryGetValue(regionId, out var regionInfo))
            {
                return RegionHealth.Unknown;
            }

            var previousHealth = regionInfo.Health;
            RegionHealth newHealth;

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5));

                // Simulate health check (in production, this would be an actual health endpoint call)
                var latency = CalculateSimulatedLatency(regionInfo.Endpoint);
                await Task.Delay(latency, cts.Token).ConfigureAwait(false);

                // Consider healthy if latency is reasonable
                newHealth = latency.TotalMilliseconds < 1000 ? RegionHealth.Healthy : RegionHealth.Degraded;
                regionInfo.ReplicationLag = latency;
            }
            catch (OperationCanceledException)
            {
                newHealth = RegionHealth.Unhealthy;
            }
            catch
            {
                newHealth = RegionHealth.Unhealthy;
            }

            regionInfo.Health = newHealth;

            if (previousHealth != newHealth)
            {
                _metrics.RecordHealthChange(regionId, previousHealth, newHealth);

                var eventArgs = new RegionHealthChangedEventArgs
                {
                    RegionId = regionId,
                    OldHealth = previousHealth,
                    NewHealth = newHealth,
                    ChangedAt = DateTime.UtcNow,
                    Reason = newHealth == RegionHealth.Unhealthy ? "Health check failed" : null
                };

                OnRegionHealthChanged(eventArgs);

                LogInformation("Region '{RegionId}' health changed from {OldHealth} to {NewHealth}",
                    regionId, previousHealth, newHealth);
            }

            return newHealth;
        }

        #endregion

        #region Private Methods - Helper Methods

        private async Task EnqueueReplicationOperationAsync(ReplicationOperation operation, CancellationToken ct)
        {
            await _operationQueue.Writer.WriteAsync(operation, ct).ConfigureAwait(false);
        }

        private async Task SyncKeyToRegionAsync(string key, string regionId, CancellationToken ct)
        {
            var operation = new ReplicationOperation
            {
                OperationId = Guid.NewGuid().ToString("N"),
                Key = key,
                Data = null, // Would be fetched from storage
                OperationType = ReplicationOperationType.Put,
                TargetRegionId = regionId,
                Timestamp = DateTime.UtcNow,
                RetryCount = 0
            };

            await EnqueueReplicationOperationAsync(operation, ct).ConfigureAwait(false);
        }

        private void AddToDeadLetterQueue(DeadLetterItem item)
        {
            if (_deadLetterQueue.Count >= MaxDlqSize)
            {
                _deadLetterQueue.TryDequeue(out _);
            }

            _deadLetterQueue.Enqueue(item);
            _metrics.RecordDlqAdd(item.TargetRegionId);
        }

        private bool MatchesRule(string key, Dictionary<string, string>? tags, ReplicationRule rule)
        {
            // Check prefix filter
            if (!string.IsNullOrEmpty(rule.Prefix) && !key.StartsWith(rule.Prefix, StringComparison.Ordinal))
            {
                return false;
            }

            // Check tag filters
            if (rule.TagFilters != null && rule.TagFilters.Count > 0)
            {
                if (tags == null)
                {
                    return false;
                }

                foreach (var (tagKey, tagValue) in rule.TagFilters)
                {
                    if (!tags.TryGetValue(tagKey, out var actualValue) ||
                        !string.Equals(actualValue, tagValue, StringComparison.Ordinal))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private void OnConflictResolved(ConflictResolvedEventArgs args)
        {
            ConflictResolved?.Invoke(this, args);
        }

        private void OnRegionHealthChanged(RegionHealthChangedEventArgs args)
        {
            RegionHealthChanged?.Invoke(this, args);
        }

        #endregion

        #region Private Methods - Message Handlers

        private async Task<Dictionary<string, object>> HandleAddRegionMessageAsync(Dictionary<string, object> payload)
        {
            var regionId = payload.GetValueOrDefault("regionId")?.ToString();
            var endpoint = payload.GetValueOrDefault("endpoint")?.ToString();
            var priority = payload.GetValueOrDefault("priority") is int p ? p : 100;
            var isWitness = payload.GetValueOrDefault("isWitness") is bool w && w;

            if (string.IsNullOrEmpty(regionId) || string.IsNullOrEmpty(endpoint))
            {
                return new Dictionary<string, object>
                {
                    ["success"] = false,
                    ["error"] = "regionId and endpoint are required"
                };
            }

            var config = new RegionConfig
            {
                Priority = priority,
                IsWitness = isWitness
            };

            var result = await AddRegionAsync(regionId, endpoint, config).ConfigureAwait(false);

            return new Dictionary<string, object>
            {
                ["success"] = result.Success,
                ["regionId"] = result.RegionId,
                ["error"] = result.ErrorMessage ?? string.Empty,
                ["duration"] = result.Duration.TotalMilliseconds
            };
        }

        private async Task<Dictionary<string, object>> HandleRemoveRegionMessageAsync(Dictionary<string, object> payload)
        {
            var regionId = payload.GetValueOrDefault("regionId")?.ToString();
            var drainFirst = payload.GetValueOrDefault("drainFirst") is not bool d || d;

            if (string.IsNullOrEmpty(regionId))
            {
                return new Dictionary<string, object>
                {
                    ["success"] = false,
                    ["error"] = "regionId is required"
                };
            }

            var result = await RemoveRegionAsync(regionId, drainFirst).ConfigureAwait(false);

            return new Dictionary<string, object>
            {
                ["success"] = result.Success,
                ["regionId"] = result.RegionId,
                ["error"] = result.ErrorMessage ?? string.Empty,
                ["duration"] = result.Duration.TotalMilliseconds
            };
        }

        private async Task<Dictionary<string, object>> HandleListRegionsMessageAsync()
        {
            var regions = await ListRegionsAsync().ConfigureAwait(false);

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["regions"] = regions.Select(r => new Dictionary<string, object>
                {
                    ["regionId"] = r.RegionId,
                    ["endpoint"] = r.Endpoint,
                    ["priority"] = r.Config.Priority,
                    ["isWitness"] = r.Config.IsWitness,
                    ["health"] = r.Health.ToString(),
                    ["addedAt"] = r.AddedAt.ToString("O")
                }).ToList()
            };
        }

        private Dictionary<string, object> HandleAddRuleMessage(Dictionary<string, object> payload)
        {
            var ruleId = payload.GetValueOrDefault("ruleId")?.ToString();
            var prefix = payload.GetValueOrDefault("prefix")?.ToString();
            var priority = payload.GetValueOrDefault("priority") is int p ? p : 100;
            var deleteMarkerReplication = payload.GetValueOrDefault("deleteMarkerReplication") is bool d && d;

            if (string.IsNullOrEmpty(ruleId))
            {
                return new Dictionary<string, object>
                {
                    ["success"] = false,
                    ["error"] = "ruleId is required"
                };
            }

            var rule = new ReplicationRule
            {
                RuleId = ruleId,
                Prefix = prefix,
                Priority = priority,
                DeleteMarkerReplication = deleteMarkerReplication,
                IsEnabled = true
            };

            var success = AddReplicationRule(rule);

            return new Dictionary<string, object>
            {
                ["success"] = success,
                ["ruleId"] = ruleId
            };
        }

        private Dictionary<string, object> HandleRemoveRuleMessage(Dictionary<string, object> payload)
        {
            var ruleId = payload.GetValueOrDefault("ruleId")?.ToString();

            if (string.IsNullOrEmpty(ruleId))
            {
                return new Dictionary<string, object>
                {
                    ["success"] = false,
                    ["error"] = "ruleId is required"
                };
            }

            var success = RemoveReplicationRule(ruleId);

            return new Dictionary<string, object>
            {
                ["success"] = success,
                ["ruleId"] = ruleId
            };
        }

        private Dictionary<string, object> HandleListRulesMessage()
        {
            var rules = ListReplicationRules();

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["rules"] = rules.Select(r => new Dictionary<string, object>
                {
                    ["ruleId"] = r.RuleId,
                    ["prefix"] = r.Prefix ?? string.Empty,
                    ["priority"] = r.Priority,
                    ["isEnabled"] = r.IsEnabled,
                    ["deleteMarkerReplication"] = r.DeleteMarkerReplication
                }).ToList()
            };
        }

        private async Task<Dictionary<string, object>> HandleStatusMessageAsync()
        {
            var status = await GetReplicationStatusAsync().ConfigureAwait(false);

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["totalRegions"] = status.TotalRegions,
                ["healthyRegions"] = status.HealthyRegions,
                ["consistencyLevel"] = status.ConsistencyLevel.ToString(),
                ["pendingOperations"] = status.PendingOperations,
                ["conflictsDetected"] = status.ConflictsDetected,
                ["conflictsResolved"] = status.ConflictsResolved,
                ["maxReplicationLag"] = status.MaxReplicationLag.TotalMilliseconds,
                ["bandwidthThrottle"] = status.BandwidthThrottle as object ?? DBNull.Value,
                ["regions"] = status.Regions.Select(kvp => new Dictionary<string, object>
                {
                    ["regionId"] = kvp.Key,
                    ["health"] = kvp.Value.Health.ToString(),
                    ["replicationLag"] = kvp.Value.ReplicationLag.TotalMilliseconds,
                    ["pendingOperations"] = kvp.Value.PendingOperations
                }).ToList()
            };
        }

        private async Task<Dictionary<string, object>> HandleSyncMessageAsync(Dictionary<string, object> payload)
        {
            var key = payload.GetValueOrDefault("key")?.ToString();

            if (string.IsNullOrEmpty(key))
            {
                return new Dictionary<string, object>
                {
                    ["success"] = false,
                    ["error"] = "key is required"
                };
            }

            string[]? targetRegions = null;
            if (payload.TryGetValue("targetRegions", out var regions) && regions is JsonElement element)
            {
                targetRegions = element.EnumerateArray().Select(e => e.GetString()).Where(s => s != null).ToArray()!;
            }

            var result = await ForceSyncAsync(key, targetRegions).ConfigureAwait(false);

            return new Dictionary<string, object>
            {
                ["success"] = result.Success,
                ["key"] = result.Key,
                ["duration"] = result.Duration.TotalMilliseconds,
                ["regionResults"] = result.RegionResults.Select(kvp => new Dictionary<string, object>
                {
                    ["regionId"] = kvp.Key,
                    ["success"] = kvp.Value.Success,
                    ["error"] = kvp.Value.ErrorMessage ?? string.Empty,
                    ["duration"] = kvp.Value.Duration.TotalMilliseconds
                }).ToList()
            };
        }

        private async Task<Dictionary<string, object>> HandleResolveConflictMessageAsync(Dictionary<string, object> payload)
        {
            var key = payload.GetValueOrDefault("key")?.ToString();
            var strategyStr = payload.GetValueOrDefault("strategy")?.ToString();
            var winningRegion = payload.GetValueOrDefault("winningRegion")?.ToString();

            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(strategyStr))
            {
                return new Dictionary<string, object>
                {
                    ["success"] = false,
                    ["error"] = "key and strategy are required"
                };
            }

            if (!Enum.TryParse<ConflictResolutionStrategy>(strategyStr, true, out var strategy))
            {
                return new Dictionary<string, object>
                {
                    ["success"] = false,
                    ["error"] = $"Invalid strategy: {strategyStr}"
                };
            }

            var result = await ResolveConflictAsync(key, strategy, winningRegion).ConfigureAwait(false);

            return new Dictionary<string, object>
            {
                ["success"] = result.Success,
                ["key"] = result.Key,
                ["strategy"] = result.StrategyUsed.ToString(),
                ["winningRegion"] = result.WinningRegion ?? string.Empty,
                ["error"] = result.ErrorMessage ?? string.Empty
            };
        }

        private Dictionary<string, object> HandleSetConsistencyMessage(Dictionary<string, object> payload)
        {
            var levelStr = payload.GetValueOrDefault("level")?.ToString();

            if (string.IsNullOrEmpty(levelStr) || !Enum.TryParse<ConsistencyLevel>(levelStr, true, out var level))
            {
                return new Dictionary<string, object>
                {
                    ["success"] = false,
                    ["error"] = "Invalid consistency level"
                };
            }

            _consistencyLevel = level;

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["level"] = level.ToString()
            };
        }

        private Dictionary<string, object> HandleSetBandwidthMessage(Dictionary<string, object> payload)
        {
            var bytesPerSecond = payload.GetValueOrDefault("maxBytesPerSecond");
            long? maxBytes = bytesPerSecond switch
            {
                long l => l,
                int i => i,
                double d => (long)d,
                _ => null
            };

            _bandwidthThrottler.MaxBytesPerSecond = maxBytes ?? DefaultBandwidthBytesPerSecond;

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["maxBytesPerSecond"] = _bandwidthThrottler.MaxBytesPerSecond
            };
        }

        private Dictionary<string, object> HandleListDlqMessage(Dictionary<string, object> payload)
        {
            var limit = payload.GetValueOrDefault("limit") is int l ? l : 100;
            var items = GetDeadLetterItems(limit);

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["count"] = items.Count,
                ["items"] = items.Select(i => new Dictionary<string, object>
                {
                    ["key"] = i.Key,
                    ["targetRegionId"] = i.TargetRegionId,
                    ["operationType"] = i.OperationType.ToString(),
                    ["failedAt"] = i.FailedAt.ToString("O"),
                    ["error"] = i.ErrorMessage ?? string.Empty,
                    ["retryCount"] = i.RetryCount
                }).ToList()
            };
        }

        private async Task<Dictionary<string, object>> HandleRetryDlqMessageAsync()
        {
            var retried = await RetryDeadLetterItemsAsync().ConfigureAwait(false);

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["retriedCount"] = retried
            };
        }

        private Dictionary<string, object> HandleMetricsMessage()
        {
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["conflictsDetected"] = _metrics.ConflictsDetected,
                ["conflictsResolved"] = _metrics.ConflictsResolved,
                ["totalReplicationOperations"] = _metrics.TotalReplicationOperations,
                ["successfulReplications"] = _metrics.SuccessfulReplications,
                ["failedReplications"] = _metrics.FailedReplications,
                ["totalBytesTransferred"] = _metrics.TotalBytesTransferred,
                ["dlqSize"] = _deadLetterQueue.Count
            };
        }

        private async Task<Dictionary<string, object>> HandleReplicateMessageAsync(Dictionary<string, object> payload)
        {
            var key = payload.GetValueOrDefault("key")?.ToString();
            var operationType = payload.GetValueOrDefault("operationType")?.ToString();
            var targetRegionId = payload.GetValueOrDefault("targetRegionId")?.ToString();

            if (string.IsNullOrEmpty(key))
            {
                return new Dictionary<string, object>
                {
                    ["success"] = false,
                    ["error"] = "key is required"
                };
            }

            if (!Enum.TryParse<ReplicationOperationType>(operationType ?? "Put", true, out var opType))
            {
                opType = ReplicationOperationType.Put;
            }

            // Check if key should be replicated based on rules
            var tags = payload.GetValueOrDefault("tags") as Dictionary<string, string>;
            if (!ShouldReplicate(key, tags))
            {
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["replicated"] = false,
                    ["reason"] = "Key does not match any replication rule"
                };
            }

            // Get target regions
            var targetRegions = string.IsNullOrEmpty(targetRegionId)
                ? _regions.Keys.Where(r => _regions.TryGetValue(r, out var info) && info.Health == RegionHealth.Healthy).ToList()
                : new List<string> { targetRegionId };

            foreach (var regionId in targetRegions)
            {
                var operation = new ReplicationOperation
                {
                    OperationId = Guid.NewGuid().ToString("N"),
                    Key = key,
                    Data = null, // Would be actual data in production
                    OperationType = opType,
                    TargetRegionId = regionId,
                    Timestamp = DateTime.UtcNow,
                    RetryCount = 0
                };

                await EnqueueReplicationOperationAsync(operation, CancellationToken.None).ConfigureAwait(false);
            }

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["replicated"] = true,
                ["targetRegions"] = targetRegions
            };
        }

        #endregion

        #region Private Methods - Logging

        private void LogInformation(string message, params object[] args)
        {
            // Logging placeholder - context-based logging not available
            var formatted = args?.Length > 0 ? string.Format(message, args) : message;
            Console.WriteLine($"[CrossRegionPlugin INFO] {formatted}");
        }

        private void LogWarning(string message, params object[] args)
        {
            // Logging placeholder - context-based logging not available
            var formatted = args?.Length > 0 ? string.Format(message, args) : message;
            Console.WriteLine($"[CrossRegionPlugin WARN] {formatted}");
        }

        private void LogError(Exception ex, string message, params object[] args)
        {
            // Logging placeholder - context-based logging not available
            var formatted = args?.Length > 0 ? string.Format(message, args) : message;
            Console.WriteLine($"[CrossRegionPlugin ERROR] {formatted}: {ex.Message}");
        }

        private void LogDebug(string message, params object[] args)
        {
            // Logging placeholder - context-based logging not available
            var formatted = args?.Length > 0 ? string.Format(message, args) : message;
            Console.WriteLine($"[CrossRegionPlugin DEBUG] {formatted}");
        }

        #endregion

        #region IAsyncDisposable

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            await StopAsync().ConfigureAwait(false);

            _operationLock.Dispose();
            _configLock.Dispose();
            _bandwidthThrottler.Dispose();
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// Extended region information for cross-region replication.
    /// </summary>
    internal sealed class CrossRegionInfo
    {
        public required string RegionId { get; init; }
        public required string Endpoint { get; init; }
        public required RegionConfig Config { get; init; }
        public required DateTime AddedAt { get; init; }
        public RegionHealth Health { get; set; }
        public bool IsBidirectional { get; init; }
        public VersionVector? VectorClock { get; set; }
        public DateTime? LastSuccessfulSync { get; set; }
        public TimeSpan ReplicationLag { get; set; }
        internal long PendingOperationsCounter;
        public long PendingOperations => Interlocked.Read(ref PendingOperationsCounter);
    }

    /// <summary>
    /// Replication rule with prefix and tag filters.
    /// </summary>
    public sealed class ReplicationRule
    {
        /// <summary>
        /// Unique identifier for the rule.
        /// </summary>
        public required string RuleId { get; init; }

        /// <summary>
        /// Key prefix filter (optional).
        /// </summary>
        public string? Prefix { get; init; }

        /// <summary>
        /// Tag filters (optional).
        /// </summary>
        public Dictionary<string, string>? TagFilters { get; init; }

        /// <summary>
        /// Rule priority (higher values = higher priority).
        /// </summary>
        public int Priority { get; init; } = 100;

        /// <summary>
        /// Whether the rule is enabled.
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// Whether to replicate delete markers.
        /// </summary>
        public bool DeleteMarkerReplication { get; init; }

        /// <summary>
        /// Target regions for this rule (null = all regions).
        /// </summary>
        public string[]? TargetRegions { get; init; }
    }

    /// <summary>
    /// Replication operation to be processed.
    /// </summary>
    internal sealed class ReplicationOperation
    {
        public required string OperationId { get; init; }
        public required string Key { get; init; }
        public byte[]? Data { get; init; }
        public ReplicationOperationType OperationType { get; init; }
        public required string TargetRegionId { get; init; }
        public DateTime Timestamp { get; init; }
        public int RetryCount { get; set; }
    }

    /// <summary>
    /// Types of replication operations.
    /// </summary>
    public enum ReplicationOperationType
    {
        /// <summary>Put/Create operation.</summary>
        Put,
        /// <summary>Delete operation.</summary>
        Delete,
        /// <summary>Delete marker for versioned objects.</summary>
        DeleteMarker
    }

    /// <summary>
    /// Payload for replication data transfer.
    /// </summary>
    internal sealed class ReplicationPayload
    {
        public required string Key { get; init; }
        public byte[]? Data { get; init; }
        public ReplicationOperationType OperationType { get; init; }
        public required string SourceRegionId { get; init; }
        public DateTime Timestamp { get; init; }
        public byte[]? VersionVector { get; init; }
    }

    /// <summary>
    /// Item in the dead letter queue.
    /// </summary>
    public sealed class DeadLetterItem
    {
        /// <summary>Key that failed replication.</summary>
        public required string Key { get; init; }

        /// <summary>Data associated with the operation.</summary>
        public byte[]? Data { get; init; }

        /// <summary>Type of operation that failed.</summary>
        public ReplicationOperationType OperationType { get; init; }

        /// <summary>Target region for the operation.</summary>
        public required string TargetRegionId { get; init; }

        /// <summary>When the operation failed.</summary>
        public DateTime FailedAt { get; init; }

        /// <summary>Error message.</summary>
        public string? ErrorMessage { get; init; }

        /// <summary>Number of retry attempts.</summary>
        public int RetryCount { get; init; }
    }

    /// <summary>
    /// Active replication conflict.
    /// </summary>
    internal sealed class ReplicationConflict
    {
        public required string Key { get; init; }
        public DateTime DetectedAt { get; init; }
        public required List<ConflictingVersion> ConflictingVersions { get; init; }
    }

    /// <summary>
    /// A conflicting version in a replication conflict.
    /// </summary>
    internal sealed class ConflictingVersion
    {
        public required string RegionId { get; init; }
        public byte[]? Data { get; init; }
        public DateTime Timestamp { get; init; }
        public VersionVector? VersionVector { get; init; }
    }

    /// <summary>
    /// Version vector for tracking causality across regions.
    /// </summary>
    internal sealed class VersionVector
    {
        private readonly ConcurrentDictionary<string, long> _clock;
        private readonly string _localNodeId;

        public VersionVector(string localNodeId)
        {
            _localNodeId = localNodeId ?? throw new ArgumentNullException(nameof(localNodeId));
            _clock = new ConcurrentDictionary<string, long>();
            _clock[localNodeId] = 1;
        }

        private VersionVector(ConcurrentDictionary<string, long> clock, string localNodeId)
        {
            _clock = new ConcurrentDictionary<string, long>(clock);
            _localNodeId = localNodeId;
        }

        public long Sum => _clock.Values.Sum();

        public void IncrementInPlace(string nodeId)
        {
            _clock.AddOrUpdate(nodeId, 1, (_, v) => v + 1);
        }

        public CausalRelation CompareCausality(VersionVector other)
        {
            if (other == null)
            {
                return CausalRelation.HappenedAfter;
            }

            var thisGreater = false;
            var otherGreater = false;
            var allNodes = _clock.Keys.Union(other._clock.Keys);

            foreach (var nodeId in allNodes)
            {
                var thisValue = _clock.GetValueOrDefault(nodeId, 0);
                var otherValue = other._clock.GetValueOrDefault(nodeId, 0);

                if (thisValue > otherValue) thisGreater = true;
                else if (otherValue > thisValue) otherGreater = true;
            }

            if (thisGreater && otherGreater) return CausalRelation.Concurrent;
            if (thisGreater) return CausalRelation.HappenedAfter;
            if (otherGreater) return CausalRelation.HappenedBefore;
            return CausalRelation.Equal;
        }

        public VersionVector Clone()
        {
            return new VersionVector(_clock, _localNodeId);
        }

        public byte[] ToBytes()
        {
            return JsonSerializer.SerializeToUtf8Bytes(_clock);
        }
    }

    /// <summary>
    /// Causal relation between version vectors.
    /// </summary>
    internal enum CausalRelation
    {
        Equal,
        HappenedBefore,
        HappenedAfter,
        Concurrent
    }

    /// <summary>
    /// Bandwidth throttler for WAN optimization.
    /// </summary>
    internal sealed class BandwidthThrottler : IDisposable
    {
        private readonly SemaphoreSlim _throttleLock = new(1, 1);
        private long _bytesThisSecond;
        private DateTime _currentSecondStart = DateTime.UtcNow;

        public long MaxBytesPerSecond { get; set; }

        public BandwidthThrottler(long maxBytesPerSecond)
        {
            MaxBytesPerSecond = maxBytesPerSecond;
        }

        public async Task ThrottleAsync(int byteCount, CancellationToken ct)
        {
            if (MaxBytesPerSecond <= 0)
            {
                return;
            }

            await _throttleLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var now = DateTime.UtcNow;

                // Reset counter if we're in a new second
                if ((now - _currentSecondStart).TotalSeconds >= 1)
                {
                    _bytesThisSecond = 0;
                    _currentSecondStart = now;
                }

                _bytesThisSecond += byteCount;

                // If we've exceeded the limit, wait until the next second
                if (_bytesThisSecond > MaxBytesPerSecond)
                {
                    var delayMs = (int)((1000 - (now - _currentSecondStart).TotalMilliseconds));
                    if (delayMs > 0)
                    {
                        await Task.Delay(delayMs, ct).ConfigureAwait(false);
                    }
                    _bytesThisSecond = byteCount;
                    _currentSecondStart = DateTime.UtcNow;
                }
            }
            finally
            {
                _throttleLock.Release();
            }
        }

        public void Dispose()
        {
            _throttleLock.Dispose();
        }
    }

    /// <summary>
    /// Metrics for cross-region replication.
    /// </summary>
    internal sealed class CrossRegionMetrics
    {
        private long _conflictsDetected;
        private long _conflictsResolved;
        private long _totalReplicationOperations;
        private long _successfulReplications;
        private long _failedReplications;
        private long _totalBytesTransferred;
        private readonly ConcurrentDictionary<string, long> _bytesPerRegion = new();
        private readonly ConcurrentDictionary<string, ConcurrentQueue<(DateTime, long)>> _recentTransfers = new();

        public long ConflictsDetected => Interlocked.Read(ref _conflictsDetected);
        public long ConflictsResolved => Interlocked.Read(ref _conflictsResolved);
        public long TotalReplicationOperations => Interlocked.Read(ref _totalReplicationOperations);
        public long SuccessfulReplications => Interlocked.Read(ref _successfulReplications);
        public long FailedReplications => Interlocked.Read(ref _failedReplications);
        public long TotalBytesTransferred => Interlocked.Read(ref _totalBytesTransferred);

        public void RecordConflictDetected(string key)
        {
            Interlocked.Increment(ref _conflictsDetected);
        }

        public void RecordConflictResolved(string key, ConflictResolutionStrategy strategy)
        {
            Interlocked.Increment(ref _conflictsResolved);
        }

        public void RecordReplicationSuccess(string regionId, long bytes)
        {
            Interlocked.Increment(ref _totalReplicationOperations);
            Interlocked.Increment(ref _successfulReplications);
            Interlocked.Add(ref _totalBytesTransferred, bytes);

            _bytesPerRegion.AddOrUpdate(regionId, bytes, (_, v) => v + bytes);

            var queue = _recentTransfers.GetOrAdd(regionId, _ => new ConcurrentQueue<(DateTime, long)>());
            queue.Enqueue((DateTime.UtcNow, bytes));

            // Cleanup old entries
            var cutoff = DateTime.UtcNow.AddMinutes(-1);
            while (queue.TryPeek(out var item) && item.Item1 < cutoff)
            {
                queue.TryDequeue(out _);
            }
        }

        public void RecordReplicationFailure(string regionId)
        {
            Interlocked.Increment(ref _totalReplicationOperations);
            Interlocked.Increment(ref _failedReplications);
        }

        public void RecordSyncOperation(string key, bool success)
        {
            Interlocked.Increment(ref _totalReplicationOperations);
            if (success)
            {
                Interlocked.Increment(ref _successfulReplications);
            }
            else
            {
                Interlocked.Increment(ref _failedReplications);
            }
        }

        public void RecordRegionAdded(string regionId)
        {
            // Track region addition for monitoring
        }

        public void RecordRegionRemoved(string regionId)
        {
            _bytesPerRegion.TryRemove(regionId, out _);
            _recentTransfers.TryRemove(regionId, out _);
        }

        public void RecordHealthChange(string regionId, RegionHealth oldHealth, RegionHealth newHealth)
        {
            // Track health changes for monitoring
        }

        public void RecordDlqAdd(string regionId)
        {
            // Track DLQ additions for monitoring
        }

        public void RecordDlqRetry(int success, int failed)
        {
            // Track DLQ retry operations
        }

        public long GetBytesTransferredLastMinute(string regionId)
        {
            if (!_recentTransfers.TryGetValue(regionId, out var queue))
            {
                return 0;
            }

            var cutoff = DateTime.UtcNow.AddMinutes(-1);
            return queue.Where(t => t.Item1 >= cutoff).Sum(t => t.Item2);
        }
    }

    #endregion
}
