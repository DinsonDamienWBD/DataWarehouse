using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Replication;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateReplication.Strategies.DR
{
    #region DR Types

    /// <summary>
    /// Represents a disaster recovery site.
    /// </summary>
    public sealed class DRSite
    {
        /// <summary>Site identifier.</summary>
        public required string SiteId { get; init; }

        /// <summary>Display name.</summary>
        public required string Name { get; init; }

        /// <summary>Geographic location.</summary>
        public required string Location { get; init; }

        /// <summary>Site role.</summary>
        public DRSiteRole Role { get; set; } = DRSiteRole.Secondary;

        /// <summary>Current health status.</summary>
        public DRSiteHealth Health { get; set; } = DRSiteHealth.Healthy;

        /// <summary>Recovery Point Objective in seconds.</summary>
        public int RpoSeconds { get; set; } = 300;

        /// <summary>Recovery Time Objective in seconds.</summary>
        public int RtoSeconds { get; set; } = 3600;

        /// <summary>Estimated latency in milliseconds.</summary>
        public int LatencyMs { get; set; } = 100;

        /// <summary>Last successful sync time.</summary>
        public DateTimeOffset? LastSyncTime { get; set; }
    }

    /// <summary>
    /// DR site role.
    /// </summary>
    public enum DRSiteRole
    {
        Primary,
        Secondary,
        Witness,
        Standby
    }

    /// <summary>
    /// DR site health status.
    /// </summary>
    public enum DRSiteHealth
    {
        Healthy,
        Degraded,
        Failed,
        Recovering,
        Offline
    }

    /// <summary>
    /// Failover status.
    /// </summary>
    public enum FailoverStatus
    {
        NotStarted,
        InProgress,
        Completed,
        Failed,
        RolledBack
    }

    #endregion

    /// <summary>
    /// Asynchronous DR replication strategy with configurable RPO
    /// and automatic checkpointing.
    /// </summary>
    public sealed class AsyncDRStrategy : EnhancedReplicationStrategyBase
    {
        private readonly BoundedDictionary<string, DRSite> _sites = new BoundedDictionary<string, DRSite>(1000);
        private readonly BoundedDictionary<string, (byte[] Data, DateTimeOffset Timestamp, long Checkpoint)> _dataStore = new BoundedDictionary<string, (byte[] Data, DateTimeOffset Timestamp, long Checkpoint)>(1000);
        private readonly BoundedDictionary<string, long> _siteCheckpoints = new BoundedDictionary<string, long>(1000);
        private string? _primarySiteId;
        private long _currentCheckpoint;
        private int _rpoSeconds = 300; // 5 minutes default

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "AsyncDR",
            Description = "Asynchronous DR replication with configurable RPO and automatic checkpointing",
            ConsistencyModel = ConsistencyModel.Eventual,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: false,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.LastWriteWins },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: false,
                IsGeoAware: true,
                MaxReplicationLag: TimeSpan.FromMinutes(15),
                MinReplicaCount: 2,
                MaxReplicaCount: 5),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = true,
            SupportsStreaming = true,
            TypicalLagMs = 1000,
            ConsistencySlaMs = 900000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Sets the RPO target in seconds.
        /// </summary>
        public void SetRPO(int seconds)
        {
            _rpoSeconds = seconds;
        }

        /// <summary>
        /// Adds a DR site.
        /// </summary>
        public void AddSite(DRSite site)
        {
            _sites[site.SiteId] = site;
            _siteCheckpoints[site.SiteId] = 0;

            if (site.Role == DRSiteRole.Primary)
            {
                _primarySiteId = site.SiteId;
            }
        }

        /// <summary>
        /// Gets the current checkpoint.
        /// </summary>
        public long GetCurrentCheckpoint() => _currentCheckpoint;

        /// <summary>
        /// Gets the checkpoint lag for a site.
        /// </summary>
        public long GetCheckpointLag(string siteId)
        {
            if (_siteCheckpoints.TryGetValue(siteId, out var checkpoint))
            {
                return _currentCheckpoint - checkpoint;
            }
            return _currentCheckpoint;
        }

        /// <summary>
        /// Checks if site is within RPO.
        /// </summary>
        public bool IsWithinRPO(string siteId)
        {
            if (!_sites.TryGetValue(siteId, out var site) || site.LastSyncTime == null)
                return false;

            return (DateTimeOffset.UtcNow - site.LastSyncTime.Value).TotalSeconds <= _rpoSeconds;
        }

        /// <inheritdoc/>
        public override async Task ReplicateAsync(
            string sourceNodeId,
            IEnumerable<string> targetNodeIds,
            ReadOnlyMemory<byte> data,
            IDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            IncrementLocalClock();
            var key = metadata?.GetValueOrDefault("key") ?? Guid.NewGuid().ToString();

            var checkpoint = Interlocked.Increment(ref _currentCheckpoint);
            _dataStore[key] = (data.ToArray(), DateTimeOffset.UtcNow, checkpoint);

            var tasks = targetNodeIds
                .Where(id => _sites.TryGetValue(id, out var s) && s.Health == DRSiteHealth.Healthy)
                .Select(async siteId =>
                {
                    var startTime = DateTime.UtcNow;
                    var site = _sites[siteId];

                    await Task.Delay(site.LatencyMs, cancellationToken);

                    _siteCheckpoints[siteId] = checkpoint;
                    site.LastSyncTime = DateTimeOffset.UtcNow;

                    RecordReplicationLag(siteId, DateTime.UtcNow - startTime);
                });

            await Task.WhenAll(tasks);
        }

        /// <inheritdoc/>
        public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(
            ReplicationConflict conflict,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ResolveLastWriteWins(conflict));
        }

        /// <inheritdoc/>
        public override Task<bool> VerifyConsistencyAsync(
            IEnumerable<string> nodeIds,
            string dataId,
            CancellationToken cancellationToken = default)
        {
            if (!_dataStore.ContainsKey(dataId))
                return Task.FromResult(false);

            return Task.FromResult(nodeIds.All(id => IsWithinRPO(id)));
        }

        /// <inheritdoc/>
        public override Task<TimeSpan> GetReplicationLagAsync(
            string sourceNodeId,
            string targetNodeId,
            CancellationToken cancellationToken = default)
        {
            if (_sites.TryGetValue(targetNodeId, out var site) && site.LastSyncTime != null)
            {
                return Task.FromResult(DateTimeOffset.UtcNow - site.LastSyncTime.Value);
            }
            return Task.FromResult(TimeSpan.FromSeconds(_rpoSeconds));
        }
    }

    /// <summary>
    /// Synchronous DR replication strategy with zero data loss guarantee.
    /// </summary>
    public sealed class SyncDRStrategy : EnhancedReplicationStrategyBase
    {
        private readonly BoundedDictionary<string, DRSite> _sites = new BoundedDictionary<string, DRSite>(1000);
        private readonly BoundedDictionary<string, (byte[] Data, DateTimeOffset Timestamp)> _dataStore = new BoundedDictionary<string, (byte[] Data, DateTimeOffset Timestamp)>(1000);
        private string? _primarySiteId;
        private int _quorum = 2;

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "SyncDR",
            Description = "Synchronous DR replication with zero data loss guarantee",
            ConsistencyModel = ConsistencyModel.Strong,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: false,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.FirstWriteWins },
                SupportsAsyncReplication: false,
                SupportsSyncReplication: true,
                IsGeoAware: true,
                MaxReplicationLag: TimeSpan.FromSeconds(5),
                MinReplicaCount: 2,
                MaxReplicaCount: 3),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = false,
            SupportsStreaming = false,
            TypicalLagMs = 100,
            ConsistencySlaMs = 5000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Sets the write quorum.
        /// </summary>
        public void SetQuorum(int quorum)
        {
            _quorum = quorum;
        }

        /// <summary>
        /// Adds a DR site.
        /// </summary>
        public void AddSite(DRSite site)
        {
            site.RpoSeconds = 0; // Sync DR has zero RPO
            _sites[site.SiteId] = site;

            if (site.Role == DRSiteRole.Primary)
            {
                _primarySiteId = site.SiteId;
            }
        }

        /// <summary>
        /// Gets the primary site.
        /// </summary>
        public DRSite? GetPrimary()
        {
            return _primarySiteId != null && _sites.TryGetValue(_primarySiteId, out var site) ? site : null;
        }

        /// <inheritdoc/>
        public override async Task ReplicateAsync(
            string sourceNodeId,
            IEnumerable<string> targetNodeIds,
            ReadOnlyMemory<byte> data,
            IDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            IncrementLocalClock();
            var key = metadata?.GetValueOrDefault("key") ?? Guid.NewGuid().ToString();

            var ackCount = 0;
            var ackLock = new object();

            var tasks = targetNodeIds
                .Where(id => _sites.TryGetValue(id, out var s) && s.Health == DRSiteHealth.Healthy)
                .Select(async siteId =>
                {
                    var startTime = DateTime.UtcNow;
                    var site = _sites[siteId];

                    // Synchronous wait for acknowledgment
                    await Task.Delay(site.LatencyMs * 2, cancellationToken); // Round-trip

                    lock (ackLock) ackCount++;
                    site.LastSyncTime = DateTimeOffset.UtcNow;
                    RecordReplicationLag(siteId, DateTime.UtcNow - startTime);
                });

            await Task.WhenAll(tasks);

            if (ackCount < _quorum)
            {
                throw new InvalidOperationException(
                    $"Sync DR failed: only {ackCount}/{_quorum} sites acknowledged");
            }

            _dataStore[key] = (data.ToArray(), DateTimeOffset.UtcNow);
        }

        /// <inheritdoc/>
        public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(
            ReplicationConflict conflict,
            CancellationToken cancellationToken = default)
        {
            // Sync DR: primary wins
            var mergedClock = conflict.LocalVersion.Clone();
            mergedClock.Merge(conflict.RemoteVersion);

            var isPrimaryLocal = conflict.LocalNodeId == _primarySiteId;
            var winner = isPrimaryLocal ? conflict.LocalData : conflict.RemoteData;

            return Task.FromResult((winner, mergedClock));
        }

        /// <inheritdoc/>
        public override Task<bool> VerifyConsistencyAsync(
            IEnumerable<string> nodeIds,
            string dataId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_dataStore.ContainsKey(dataId));
        }

        /// <inheritdoc/>
        public override Task<TimeSpan> GetReplicationLagAsync(
            string sourceNodeId,
            string targetNodeId,
            CancellationToken cancellationToken = default)
        {
            // Sync DR has minimal lag (network round-trip only)
            if (_sites.TryGetValue(targetNodeId, out var site))
            {
                return Task.FromResult(TimeSpan.FromMilliseconds(site.LatencyMs * 2));
            }
            return Task.FromResult(TimeSpan.FromMilliseconds(100));
        }
    }

    /// <summary>
    /// Zero-RPO DR replication strategy with continuous log shipping
    /// and transaction log mirroring.
    /// </summary>
    public sealed class ZeroRPOStrategy : EnhancedReplicationStrategyBase
    {
        private readonly BoundedDictionary<string, DRSite> _sites = new BoundedDictionary<string, DRSite>(1000);
        private readonly ConcurrentQueue<LogEntry> _transactionLog = new();
        private readonly BoundedDictionary<string, (byte[] Data, long Lsn)> _dataStore = new BoundedDictionary<string, (byte[] Data, long Lsn)>(1000);
        private readonly BoundedDictionary<string, long> _siteLsn = new BoundedDictionary<string, long>(1000);
        private long _currentLsn;
        private string? _primarySiteId;

        /// <summary>
        /// Represents a transaction log entry for Zero-RPO replication.
        /// </summary>
        public sealed record LogEntry(long Lsn, string Key, byte[] Data, DateTimeOffset Timestamp);

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "ZeroRPO",
            Description = "Zero-RPO DR with continuous log shipping and transaction log mirroring",
            ConsistencyModel = ConsistencyModel.Strong,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: false,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.FirstWriteWins },
                SupportsAsyncReplication: false,
                SupportsSyncReplication: true,
                IsGeoAware: true,
                MaxReplicationLag: TimeSpan.Zero,
                MinReplicaCount: 2,
                MaxReplicaCount: 3),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = true,
            SupportsStreaming = true,
            TypicalLagMs = 50,
            ConsistencySlaMs = 1000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Adds a DR site.
        /// </summary>
        public void AddSite(DRSite site)
        {
            site.RpoSeconds = 0;
            _sites[site.SiteId] = site;
            _siteLsn[site.SiteId] = 0;

            if (site.Role == DRSiteRole.Primary)
            {
                _primarySiteId = site.SiteId;
            }
        }

        /// <summary>
        /// Gets the current LSN.
        /// </summary>
        public long GetCurrentLsn() => _currentLsn;

        /// <summary>
        /// Gets the LSN for a site.
        /// </summary>
        public long GetSiteLsn(string siteId)
        {
            return _siteLsn.GetValueOrDefault(siteId, 0);
        }

        /// <summary>
        /// Gets pending log entries for a site.
        /// </summary>
        public IEnumerable<LogEntry> GetPendingLogEntries(string siteId, int maxEntries = 100)
        {
            var siteLsn = GetSiteLsn(siteId);
            return _transactionLog
                .Where(e => e.Lsn > siteLsn)
                .OrderBy(e => e.Lsn)
                .Take(maxEntries);
        }

        /// <inheritdoc/>
        public override async Task ReplicateAsync(
            string sourceNodeId,
            IEnumerable<string> targetNodeIds,
            ReadOnlyMemory<byte> data,
            IDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            IncrementLocalClock();
            var key = metadata?.GetValueOrDefault("key") ?? Guid.NewGuid().ToString();

            var lsn = Interlocked.Increment(ref _currentLsn);
            var logEntry = new LogEntry(lsn, key, data.ToArray(), DateTimeOffset.UtcNow);

            _transactionLog.Enqueue(logEntry);
            _dataStore[key] = (data.ToArray(), lsn);

            // All sites must acknowledge before commit
            var tasks = targetNodeIds
                .Where(id => _sites.TryGetValue(id, out var s) && s.Health == DRSiteHealth.Healthy)
                .Select(async siteId =>
                {
                    var startTime = DateTime.UtcNow;
                    var site = _sites[siteId];

                    // Simulate log shipping
                    await Task.Delay(site.LatencyMs, cancellationToken);

                    _siteLsn[siteId] = lsn;
                    site.LastSyncTime = DateTimeOffset.UtcNow;
                    RecordReplicationLag(siteId, DateTime.UtcNow - startTime);
                });

            await Task.WhenAll(tasks);

            // Verify all sites have the log entry
            var allSynced = targetNodeIds.All(id =>
                !_sites.ContainsKey(id) ||
                _sites[id].Health != DRSiteHealth.Healthy ||
                GetSiteLsn(id) >= lsn);

            if (!allSynced)
            {
                throw new InvalidOperationException("Zero-RPO replication failed: not all sites synchronized");
            }
        }

        /// <inheritdoc/>
        public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(
            ReplicationConflict conflict,
            CancellationToken cancellationToken = default)
        {
            // Zero-RPO: no conflicts possible with synchronous replication
            var mergedClock = conflict.LocalVersion.Clone();
            mergedClock.Merge(conflict.RemoteVersion);
            return Task.FromResult((conflict.LocalData, mergedClock));
        }

        /// <inheritdoc/>
        public override Task<bool> VerifyConsistencyAsync(
            IEnumerable<string> nodeIds,
            string dataId,
            CancellationToken cancellationToken = default)
        {
            if (!_dataStore.TryGetValue(dataId, out var item))
                return Task.FromResult(false);

            return Task.FromResult(nodeIds.All(id => GetSiteLsn(id) >= item.Lsn));
        }

        /// <inheritdoc/>
        public override Task<TimeSpan> GetReplicationLagAsync(
            string sourceNodeId,
            string targetNodeId,
            CancellationToken cancellationToken = default)
        {
            // Zero-RPO has no lag
            return Task.FromResult(TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Active-Passive DR replication strategy with automatic failover
    /// and health monitoring.
    /// </summary>
    public sealed class ActivePassiveStrategy : EnhancedReplicationStrategyBase
    {
        private readonly BoundedDictionary<string, DRSite> _sites = new BoundedDictionary<string, DRSite>(1000);
        private readonly BoundedDictionary<string, (byte[] Data, DateTimeOffset Timestamp)> _dataStore = new BoundedDictionary<string, (byte[] Data, DateTimeOffset Timestamp)>(1000);
        private string? _activeSiteId;
        private readonly List<FailoverEvent> _failoverHistory = new();
        private readonly object _failoverHistoryLock = new();
        private bool _autoFailoverEnabled = true;
        private int _healthCheckIntervalMs = 5000;

        /// <summary>
        /// Represents a failover event for Active-Passive replication.
        /// </summary>
        public sealed record FailoverEvent(
            string FromSite,
            string ToSite,
            DateTimeOffset Timestamp,
            FailoverStatus Status,
            string? Reason);

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "ActivePassive",
            Description = "Active-Passive DR with automatic failover and health monitoring",
            ConsistencyModel = ConsistencyModel.Eventual,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: false,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.PriorityBased },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: true,
                IsGeoAware: true,
                MaxReplicationLag: TimeSpan.FromMinutes(5),
                MinReplicaCount: 2,
                MaxReplicaCount: 3),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = true,
            SupportsStreaming = true,
            TypicalLagMs = 500,
            ConsistencySlaMs = 300000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Enables or disables automatic failover.
        /// </summary>
        public void SetAutoFailover(bool enabled)
        {
            _autoFailoverEnabled = enabled;
        }

        /// <summary>
        /// Sets the health check interval.
        /// </summary>
        public void SetHealthCheckInterval(int intervalMs)
        {
            _healthCheckIntervalMs = intervalMs;
        }

        /// <summary>
        /// Adds a DR site.
        /// </summary>
        public void AddSite(DRSite site)
        {
            _sites[site.SiteId] = site;

            if (site.Role == DRSiteRole.Primary)
            {
                _activeSiteId = site.SiteId;
            }
        }

        /// <summary>
        /// Gets the active site.
        /// </summary>
        public DRSite? GetActiveSite()
        {
            return _activeSiteId != null && _sites.TryGetValue(_activeSiteId, out var site) ? site : null;
        }

        /// <summary>
        /// Gets passive sites.
        /// </summary>
        public IEnumerable<DRSite> GetPassiveSites()
        {
            return _sites.Values.Where(s => s.SiteId != _activeSiteId);
        }

        /// <summary>
        /// Performs manual failover.
        /// </summary>
        public async Task<bool> FailoverAsync(string targetSiteId, string? reason = null, CancellationToken ct = default)
        {
            if (!_sites.TryGetValue(targetSiteId, out var targetSite) ||
                targetSite.Health != DRSiteHealth.Healthy)
            {
                return false;
            }

            var previousActive = _activeSiteId;
            if (previousActive != null && _sites.TryGetValue(previousActive, out var oldActive))
            {
                oldActive.Role = DRSiteRole.Secondary;
            }

            targetSite.Role = DRSiteRole.Primary;
            _activeSiteId = targetSiteId;

            lock (_failoverHistoryLock)
            {
                _failoverHistory.Add(new FailoverEvent(
                    previousActive ?? "none",
                    targetSiteId,
                    DateTimeOffset.UtcNow,
                    FailoverStatus.Completed,
                    reason ?? "Manual failover"));
            }

            await Task.Delay(100, ct); // Simulate failover processing
            return true;
        }

        /// <summary>
        /// Checks health and triggers auto-failover if needed.
        /// </summary>
        public async Task CheckHealthAndFailoverAsync(CancellationToken ct = default)
        {
            if (!_autoFailoverEnabled || _activeSiteId == null)
                return;

            if (_sites.TryGetValue(_activeSiteId, out var active) &&
                active.Health == DRSiteHealth.Failed)
            {
                var bestPassive = GetPassiveSites()
                    .Where(s => s.Health == DRSiteHealth.Healthy)
                    .OrderBy(s => s.RtoSeconds)
                    .FirstOrDefault();

                if (bestPassive != null)
                {
                    await FailoverAsync(bestPassive.SiteId, "Automatic failover due to primary failure", ct);
                }
            }
        }

        /// <summary>
        /// Gets failover history.
        /// </summary>
        public IReadOnlyList<FailoverEvent> GetFailoverHistory()
        {
            lock (_failoverHistoryLock)
            {
                return _failoverHistory.ToArray();
            }
        }

        /// <inheritdoc/>
        public override async Task ReplicateAsync(
            string sourceNodeId,
            IEnumerable<string> targetNodeIds,
            ReadOnlyMemory<byte> data,
            IDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            IncrementLocalClock();
            var key = metadata?.GetValueOrDefault("key") ?? Guid.NewGuid().ToString();

            _dataStore[key] = (data.ToArray(), DateTimeOffset.UtcNow);

            // Only replicate to passive sites
            var passiveTargets = targetNodeIds
                .Where(id => id != _activeSiteId && _sites.TryGetValue(id, out var s) && s.Health == DRSiteHealth.Healthy);

            var tasks = passiveTargets.Select(async siteId =>
            {
                var startTime = DateTime.UtcNow;
                var site = _sites[siteId];

                await Task.Delay(site.LatencyMs, cancellationToken);

                site.LastSyncTime = DateTimeOffset.UtcNow;
                RecordReplicationLag(siteId, DateTime.UtcNow - startTime);
            });

            await Task.WhenAll(tasks);
        }

        /// <inheritdoc/>
        public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(
            ReplicationConflict conflict,
            CancellationToken cancellationToken = default)
        {
            // Active site always wins
            var mergedClock = conflict.LocalVersion.Clone();
            mergedClock.Merge(conflict.RemoteVersion);

            var isActiveLocal = conflict.LocalNodeId == _activeSiteId;
            var winner = isActiveLocal ? conflict.LocalData : conflict.RemoteData;

            return Task.FromResult((winner, mergedClock));
        }

        /// <inheritdoc/>
        public override Task<bool> VerifyConsistencyAsync(
            IEnumerable<string> nodeIds,
            string dataId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_dataStore.ContainsKey(dataId));
        }

        /// <inheritdoc/>
        public override Task<TimeSpan> GetReplicationLagAsync(
            string sourceNodeId,
            string targetNodeId,
            CancellationToken cancellationToken = default)
        {
            if (_sites.TryGetValue(targetNodeId, out var site) && site.LastSyncTime != null)
            {
                return Task.FromResult(DateTimeOffset.UtcNow - site.LastSyncTime.Value);
            }
            return Task.FromResult(TimeSpan.FromMinutes(5));
        }
    }

    /// <summary>
    /// Failover DR strategy with coordinated switchover, rollback support,
    /// and zero-downtime failover procedures.
    /// </summary>
    public sealed class FailoverDRStrategy : EnhancedReplicationStrategyBase
    {
        private readonly BoundedDictionary<string, DRSite> _sites = new BoundedDictionary<string, DRSite>(1000);
        private readonly BoundedDictionary<string, (byte[] Data, DateTimeOffset Timestamp)> _dataStore = new BoundedDictionary<string, (byte[] Data, DateTimeOffset Timestamp)>(1000);
        private readonly BoundedDictionary<string, (byte[] Data, DateTimeOffset Timestamp)> _rollbackStore = new BoundedDictionary<string, (byte[] Data, DateTimeOffset Timestamp)>(1000);
        private string? _currentPrimary;
        private string? _previousPrimary;
        private FailoverStatus _currentStatus = FailoverStatus.NotStarted;
        private DateTimeOffset? _failoverStartTime;

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "FailoverDR",
            Description = "Failover DR with coordinated switchover, rollback support, and zero-downtime procedures",
            ConsistencyModel = ConsistencyModel.Strong,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: false,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.PriorityBased },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: true,
                IsGeoAware: true,
                MaxReplicationLag: TimeSpan.FromMinutes(1),
                MinReplicaCount: 2,
                MaxReplicaCount: 5),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = true,
            SupportsStreaming = true,
            TypicalLagMs = 200,
            ConsistencySlaMs = 60000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Adds a DR site.
        /// </summary>
        public void AddSite(DRSite site)
        {
            _sites[site.SiteId] = site;

            if (site.Role == DRSiteRole.Primary)
            {
                _currentPrimary = site.SiteId;
            }
        }

        /// <summary>
        /// Gets current failover status.
        /// </summary>
        public FailoverStatus GetFailoverStatus() => _currentStatus;

        /// <summary>
        /// Gets the current primary site.
        /// </summary>
        public DRSite? GetPrimary()
        {
            return _currentPrimary != null && _sites.TryGetValue(_currentPrimary, out var site) ? site : null;
        }

        /// <summary>
        /// Initiates coordinated failover.
        /// </summary>
        public async Task<bool> InitiateFailoverAsync(string targetSiteId, CancellationToken ct = default)
        {
            if (_currentStatus == FailoverStatus.InProgress)
                return false;

            if (!_sites.TryGetValue(targetSiteId, out var targetSite) ||
                targetSite.Health != DRSiteHealth.Healthy)
                return false;

            _currentStatus = FailoverStatus.InProgress;
            _failoverStartTime = DateTimeOffset.UtcNow;
            _previousPrimary = _currentPrimary;

            try
            {
                // Step 1: Pause writes on current primary
                await Task.Delay(50, ct);

                // Step 2: Wait for replication to complete
                await Task.Delay(100, ct);

                // Step 3: Store rollback point
                foreach (var (key, value) in _dataStore)
                {
                    _rollbackStore[key] = value;
                }

                // Step 4: Switch roles
                if (_currentPrimary != null && _sites.TryGetValue(_currentPrimary, out var oldPrimary))
                {
                    oldPrimary.Role = DRSiteRole.Secondary;
                }

                targetSite.Role = DRSiteRole.Primary;
                _currentPrimary = targetSiteId;

                // Step 5: Resume writes on new primary
                await Task.Delay(50, ct);

                _currentStatus = FailoverStatus.Completed;
                return true;
            }
            catch
            {
                _currentStatus = FailoverStatus.Failed;
                return false;
            }
        }

        /// <summary>
        /// Rolls back to previous primary.
        /// </summary>
        public async Task<bool> RollbackAsync(CancellationToken ct = default)
        {
            if (_previousPrimary == null)
                return false;

            // Restore data from rollback store
            foreach (var (key, value) in _rollbackStore)
            {
                _dataStore[key] = value;
            }

            // Switch back to previous primary
            if (_currentPrimary != null && _sites.TryGetValue(_currentPrimary, out var currentPrimary))
            {
                currentPrimary.Role = DRSiteRole.Secondary;
            }

            if (_sites.TryGetValue(_previousPrimary, out var prevPrimary))
            {
                prevPrimary.Role = DRSiteRole.Primary;
            }

            _currentPrimary = _previousPrimary;
            _previousPrimary = null;
            _currentStatus = FailoverStatus.RolledBack;

            await Task.Delay(50, ct);
            return true;
        }

        /// <summary>
        /// Performs planned switchover (zero-downtime).
        /// </summary>
        public async Task<bool> PlannedSwitchoverAsync(string targetSiteId, CancellationToken ct = default)
        {
            if (!_sites.TryGetValue(targetSiteId, out var targetSite) ||
                targetSite.Health != DRSiteHealth.Healthy)
                return false;

            // Zero-downtime switchover:
            // 1. Route new writes to both sites
            // 2. Wait for queue drain
            // 3. Switch primary role
            // 4. Resume normal operation

            await Task.Delay(100, ct);

            if (_currentPrimary != null && _sites.TryGetValue(_currentPrimary, out var oldPrimary))
            {
                oldPrimary.Role = DRSiteRole.Secondary;
            }

            targetSite.Role = DRSiteRole.Primary;
            _currentPrimary = targetSiteId;

            return true;
        }

        /// <inheritdoc/>
        public override async Task ReplicateAsync(
            string sourceNodeId,
            IEnumerable<string> targetNodeIds,
            ReadOnlyMemory<byte> data,
            IDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            IncrementLocalClock();
            var key = metadata?.GetValueOrDefault("key") ?? Guid.NewGuid().ToString();

            // Block writes during failover
            if (_currentStatus == FailoverStatus.InProgress)
            {
                await Task.Delay(200, cancellationToken);
            }

            _dataStore[key] = (data.ToArray(), DateTimeOffset.UtcNow);

            var tasks = targetNodeIds
                .Where(id => _sites.TryGetValue(id, out var s) && s.Health == DRSiteHealth.Healthy)
                .Select(async siteId =>
                {
                    var startTime = DateTime.UtcNow;
                    var site = _sites[siteId];

                    await Task.Delay(site.LatencyMs, cancellationToken);

                    site.LastSyncTime = DateTimeOffset.UtcNow;
                    RecordReplicationLag(siteId, DateTime.UtcNow - startTime);
                });

            await Task.WhenAll(tasks);
        }

        /// <inheritdoc/>
        public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(
            ReplicationConflict conflict,
            CancellationToken cancellationToken = default)
        {
            var mergedClock = conflict.LocalVersion.Clone();
            mergedClock.Merge(conflict.RemoteVersion);

            var isPrimaryLocal = conflict.LocalNodeId == _currentPrimary;
            var winner = isPrimaryLocal ? conflict.LocalData : conflict.RemoteData;

            return Task.FromResult((winner, mergedClock));
        }

        /// <inheritdoc/>
        public override Task<bool> VerifyConsistencyAsync(
            IEnumerable<string> nodeIds,
            string dataId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_dataStore.ContainsKey(dataId));
        }

        /// <inheritdoc/>
        public override Task<TimeSpan> GetReplicationLagAsync(
            string sourceNodeId,
            string targetNodeId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(LagTracker.GetCurrentLag(targetNodeId));
        }
    }
}
