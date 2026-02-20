using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Replication;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateReplication.Strategies.Geo
{
    /// <summary>
    /// Represents a geographic region for replication.
    /// </summary>
    public sealed class GeoRegion
    {
        /// <summary>
        /// Region identifier.
        /// </summary>
        public required string RegionId { get; init; }

        /// <summary>
        /// Region display name.
        /// </summary>
        public required string Name { get; init; }

        /// <summary>
        /// Region endpoint URL.
        /// </summary>
        public required string Endpoint { get; init; }

        /// <summary>
        /// Geographic latitude.
        /// </summary>
        public double Latitude { get; init; }

        /// <summary>
        /// Geographic longitude.
        /// </summary>
        public double Longitude { get; init; }

        /// <summary>
        /// Region priority (lower = higher priority).
        /// </summary>
        public int Priority { get; init; } = 100;

        /// <summary>
        /// Whether this is a witness-only region.
        /// </summary>
        public bool IsWitness { get; init; }

        /// <summary>
        /// Estimated latency to this region in milliseconds.
        /// </summary>
        public int EstimatedLatencyMs { get; set; } = 50;

        /// <summary>
        /// Current health status.
        /// </summary>
        public RegionHealth Health { get; set; } = RegionHealth.Healthy;
    }

    /// <summary>
    /// Region health status.
    /// </summary>
    public enum RegionHealth
    {
        Healthy,
        Degraded,
        Unhealthy,
        Offline
    }

    /// <summary>
    /// Geographic replication strategy with region-aware routing and WAN optimization.
    /// </summary>
    public sealed class GeoReplicationStrategy : EnhancedReplicationStrategyBase
    {
        private readonly BoundedDictionary<string, GeoRegion> _regions = new BoundedDictionary<string, GeoRegion>(1000);
        private readonly BoundedDictionary<string, DateTimeOffset> _lastHealthCheck = new BoundedDictionary<string, DateTimeOffset>(1000);
        private string? _primaryRegionId;

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "GeoReplication",
            Description = "Geographic replication with region-aware routing, WAN optimization, and latency-based primary selection",
            ConsistencyModel = ConsistencyModel.BoundedStaleness,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: true,
                ConflictResolutionMethods: new[] {
                    ConflictResolutionMethod.LastWriteWins,
                    ConflictResolutionMethod.PriorityBased
                },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: true,
                IsGeoAware: true,
                MaxReplicationLag: TimeSpan.FromSeconds(60),
                MinReplicaCount: 2,
                MaxReplicaCount: 20),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = true,
            SupportsStreaming = false,
            TypicalLagMs = 500,
            ConsistencySlaMs = 60000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Adds a region to the replication topology.
        /// </summary>
        public void AddRegion(GeoRegion region)
        {
            ArgumentNullException.ThrowIfNull(region);
            _regions[region.RegionId] = region;

            // Auto-select primary if none set
            if (_primaryRegionId == null)
                _primaryRegionId = region.RegionId;
        }

        /// <summary>
        /// Removes a region from the replication topology.
        /// </summary>
        public bool RemoveRegion(string regionId)
        {
            var removed = _regions.TryRemove(regionId, out _);
            if (removed && _primaryRegionId == regionId)
                _primaryRegionId = _regions.Keys.FirstOrDefault();
            return removed;
        }

        /// <summary>
        /// Gets all registered regions.
        /// </summary>
        public IReadOnlyCollection<GeoRegion> GetRegions() => _regions.Values.ToArray();

        /// <summary>
        /// Sets the primary region for writes.
        /// </summary>
        public void SetPrimaryRegion(string regionId)
        {
            if (!_regions.ContainsKey(regionId))
                throw new ArgumentException($"Region {regionId} not found");
            _primaryRegionId = regionId;
        }

        /// <summary>
        /// Gets the primary region.
        /// </summary>
        public GeoRegion? GetPrimaryRegion()
        {
            return _primaryRegionId != null && _regions.TryGetValue(_primaryRegionId, out var region)
                ? region
                : null;
        }

        /// <summary>
        /// Selects primary region based on latency.
        /// </summary>
        public void SelectPrimaryByLatency()
        {
            var healthyRegions = _regions.Values
                .Where(r => r.Health == RegionHealth.Healthy && !r.IsWitness)
                .OrderBy(r => r.EstimatedLatencyMs)
                .ThenBy(r => r.Priority);

            _primaryRegionId = healthyRegions.FirstOrDefault()?.RegionId;
        }

        /// <summary>
        /// Calculates great-circle distance between two regions.
        /// </summary>
        public double CalculateDistance(string regionId1, string regionId2)
        {
            if (!_regions.TryGetValue(regionId1, out var r1) || !_regions.TryGetValue(regionId2, out var r2))
                return double.MaxValue;

            // Haversine formula
            const double R = 6371; // Earth radius in km
            var lat1 = r1.Latitude * Math.PI / 180;
            var lat2 = r2.Latitude * Math.PI / 180;
            var dLat = (r2.Latitude - r1.Latitude) * Math.PI / 180;
            var dLon = (r2.Longitude - r1.Longitude) * Math.PI / 180;

            var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                    Math.Cos(lat1) * Math.Cos(lat2) *
                    Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

            return R * c;
        }

        /// <summary>
        /// Routes to the nearest healthy region.
        /// </summary>
        public GeoRegion? RouteToNearest(string fromRegionId)
        {
            return _regions.Values
                .Where(r => r.RegionId != fromRegionId && r.Health == RegionHealth.Healthy && !r.IsWitness)
                .OrderBy(r => CalculateDistance(fromRegionId, r.RegionId))
                .FirstOrDefault();
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

            // Geo-aware replication: route through nearest regions
            var sourceRegion = _regions.Values.FirstOrDefault(r => r.RegionId == sourceNodeId);
            var tasks = new List<Task>();

            foreach (var targetId in targetNodeIds)
            {
                if (_regions.TryGetValue(targetId, out var targetRegion))
                {
                    var startTime = DateTime.UtcNow;

                    // Simulate WAN transfer with latency based on distance
                    var latency = sourceRegion != null
                        ? Math.Max(20, (int)(CalculateDistance(sourceNodeId, targetId) / 100))
                        : 50;

                    tasks.Add(Task.Run(async () =>
                    {
                        await Task.Delay(latency, cancellationToken);
                        RecordReplicationLag(targetId, DateTime.UtcNow - startTime);
                    }, cancellationToken));
                }
            }

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Checks health of all regions.
        /// </summary>
        public async Task CheckRegionHealthAsync(CancellationToken ct = default)
        {
            foreach (var region in _regions.Values)
            {
                try
                {
                    // Simulate health check
                    await Task.Delay(10, ct);
                    region.Health = RegionHealth.Healthy;
                    _lastHealthCheck[region.RegionId] = DateTimeOffset.UtcNow;
                }
                catch
                {
                    region.Health = RegionHealth.Unhealthy;
                }
            }
        }

        /// <inheritdoc/>
        public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(
            ReplicationConflict conflict,
            CancellationToken cancellationToken = default)
        {
            // Use priority-based resolution for geo conflicts
            // Prefer data from higher-priority (lower number) regions
            var localPriority = GetRegionPriority(conflict.LocalNodeId);
            var remotePriority = GetRegionPriority(conflict.RemoteNodeId);

            var mergedClock = conflict.LocalVersion.Clone();
            mergedClock.Merge(conflict.RemoteVersion);

            var winner = remotePriority < localPriority
                ? conflict.RemoteData
                : conflict.LocalData;

            return Task.FromResult((winner, mergedClock));
        }

        private int GetRegionPriority(string nodeId)
        {
            return _regions.TryGetValue(nodeId, out var region) ? region.Priority : int.MaxValue;
        }

        /// <inheritdoc/>
        public override Task<bool> VerifyConsistencyAsync(
            IEnumerable<string> nodeIds,
            string dataId,
            CancellationToken cancellationToken = default)
        {
            // Verify all specified regions are healthy
            var allHealthy = nodeIds.All(id =>
                _regions.TryGetValue(id, out var r) && r.Health == RegionHealth.Healthy);
            return Task.FromResult(allHealthy);
        }

        /// <inheritdoc/>
        public override Task<TimeSpan> GetReplicationLagAsync(
            string sourceNodeId,
            string targetNodeId,
            CancellationToken cancellationToken = default)
        {
            // Factor in geographic distance
            var baseLag = LagTracker.GetCurrentLag(targetNodeId);
            var distance = CalculateDistance(sourceNodeId, targetNodeId);
            var additionalLag = TimeSpan.FromMilliseconds(distance / 100); // ~10ms per 1000km

            return Task.FromResult(baseLag + additionalLag);
        }
    }

    /// <summary>
    /// Cross-region replication strategy with async replication, bounded staleness, and automatic failover.
    /// </summary>
    public sealed class CrossRegionStrategy : EnhancedReplicationStrategyBase
    {
        private readonly BoundedDictionary<string, GeoRegion> _regions = new BoundedDictionary<string, GeoRegion>(1000);
        private readonly BoundedDictionary<string, DateTimeOffset> _lastReplicatedAt = new BoundedDictionary<string, DateTimeOffset>(1000);
        private readonly TimeSpan _boundedStaleness;
        private string? _activeRegionId;
        private bool _autoFailoverEnabled = true;

        /// <summary>
        /// Creates a new cross-region strategy with specified staleness bound.
        /// </summary>
        public CrossRegionStrategy(TimeSpan? boundedStaleness = null) : base()
        {
            _boundedStaleness = boundedStaleness ?? TimeSpan.FromMinutes(5);
        }

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "CrossRegion",
            Description = "Cross-region replication with async replication, bounded staleness, automatic failover, and region health monitoring",
            ConsistencyModel = ConsistencyModel.BoundedStaleness,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: false,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.LastWriteWins },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: false,
                IsGeoAware: true,
                MaxReplicationLag: TimeSpan.FromMinutes(5),
                MinReplicaCount: 2,
                MaxReplicaCount: 10),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = true,
            SupportsStreaming = false,
            TypicalLagMs = 1000,
            ConsistencySlaMs = 300000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Gets the bounded staleness configuration.
        /// </summary>
        public TimeSpan BoundedStaleness => _boundedStaleness;

        /// <summary>
        /// Enables or disables automatic failover.
        /// </summary>
        public void SetAutoFailover(bool enabled)
        {
            _autoFailoverEnabled = enabled;
        }

        /// <summary>
        /// Adds a region.
        /// </summary>
        public void AddRegion(GeoRegion region)
        {
            _regions[region.RegionId] = region;
            _activeRegionId ??= region.RegionId;
        }

        /// <summary>
        /// Gets the active (primary) region.
        /// </summary>
        public GeoRegion? GetActiveRegion()
        {
            return _activeRegionId != null && _regions.TryGetValue(_activeRegionId, out var r) ? r : null;
        }

        /// <summary>
        /// Triggers failover to a target region.
        /// </summary>
        public async Task<bool> FailoverAsync(string targetRegionId, CancellationToken ct = default)
        {
            if (!_regions.TryGetValue(targetRegionId, out var targetRegion))
                return false;

            if (targetRegion.Health != RegionHealth.Healthy)
                return false;

            var oldActive = _activeRegionId;
            _activeRegionId = targetRegionId;

            // Simulate failover process
            await Task.Delay(100, ct);

            return true;
        }

        /// <summary>
        /// Monitors region health and triggers auto-failover if needed.
        /// </summary>
        public async Task MonitorAndFailoverAsync(CancellationToken ct = default)
        {
            if (!_autoFailoverEnabled || _activeRegionId == null)
                return;

            if (_regions.TryGetValue(_activeRegionId, out var activeRegion) &&
                activeRegion.Health == RegionHealth.Unhealthy)
            {
                // Find best failover target
                var target = _regions.Values
                    .Where(r => r.RegionId != _activeRegionId && r.Health == RegionHealth.Healthy && !r.IsWitness)
                    .OrderBy(r => r.Priority)
                    .FirstOrDefault();

                if (target != null)
                {
                    await FailoverAsync(target.RegionId, ct);
                }
            }
        }

        /// <summary>
        /// Checks if data from a region is within bounded staleness.
        /// </summary>
        public bool IsWithinStaleness(string regionId)
        {
            if (_lastReplicatedAt.TryGetValue(regionId, out var lastRep))
            {
                return DateTimeOffset.UtcNow - lastRep <= _boundedStaleness;
            }
            return false;
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

            // Async replication to cross-region targets
            var tasks = targetNodeIds.Select(async targetId =>
            {
                var startTime = DateTime.UtcNow;

                // Simulate cross-region transfer
                await Task.Delay(100, cancellationToken);

                _lastReplicatedAt[targetId] = DateTimeOffset.UtcNow;
                RecordReplicationLag(targetId, DateTime.UtcNow - startTime);
            });

            await Task.WhenAll(tasks);
        }

        /// <inheritdoc/>
        public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(
            ReplicationConflict conflict,
            CancellationToken cancellationToken = default)
        {
            // Cross-region uses LWW
            return Task.FromResult(ResolveLastWriteWins(conflict));
        }

        /// <inheritdoc/>
        public override Task<bool> VerifyConsistencyAsync(
            IEnumerable<string> nodeIds,
            string dataId,
            CancellationToken cancellationToken = default)
        {
            // Check all regions are within staleness bound
            var allWithin = nodeIds.All(id => IsWithinStaleness(id) || id == _activeRegionId);
            return Task.FromResult(allWithin);
        }

        /// <inheritdoc/>
        public override Task<TimeSpan> GetReplicationLagAsync(
            string sourceNodeId,
            string targetNodeId,
            CancellationToken cancellationToken = default)
        {
            // Calculate lag based on last replication time
            if (_lastReplicatedAt.TryGetValue(targetNodeId, out var lastRep))
            {
                return Task.FromResult(DateTimeOffset.UtcNow - lastRep);
            }
            return Task.FromResult(_boundedStaleness); // Max if never replicated
        }
    }
}
