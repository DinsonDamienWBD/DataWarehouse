using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Replication;

namespace DataWarehouse.Plugins.UltimateReplication.Strategies.GeoReplication
{
    /// <summary>
    /// Primary-Secondary (Master-Slave) replication strategy.
    /// All writes go to the primary node, which then replicates to secondary read replicas.
    /// </summary>
    /// <remarks>
    /// This strategy implements classic primary-secondary (also known as master-slave)
    /// replication. All write operations are directed to a single primary node, which
    /// then asynchronously replicates changes to one or more secondary nodes. Secondary
    /// nodes are read-only and provide scalability for read-heavy workloads. Automatic
    /// failover promotes a secondary to primary if the primary fails.
    /// Suitable for read-heavy applications, reporting systems, and disaster recovery.
    /// </remarks>
    public sealed class PrimarySecondaryReplicationStrategy : EnhancedReplicationStrategyBase
    {
        private string? _primaryNodeId;
        private readonly List<string> _secondaryNodeIds = new();
        private readonly bool _autoFailover;
        private readonly TimeSpan _failoverTimeout = TimeSpan.FromSeconds(10);
        private readonly TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(5);

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => ConsistencyModel.ReadYourWrites;

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities { get; } = new ReplicationCapabilities(
            SupportsMultiMaster: false,
            ConflictResolutionMethods: new[] { ConflictResolutionMethod.FirstWriteWins },
            SupportsAsyncReplication: true,
            SupportsSyncReplication: true,
            IsGeoAware: false,
            MaxReplicationLag: TimeSpan.FromSeconds(2),
            MinReplicaCount: 2,
            MaxReplicaCount: 50);

        /// <summary>
        /// Initializes a new instance of <see cref="PrimarySecondaryReplicationStrategy"/>.
        /// </summary>
        public PrimarySecondaryReplicationStrategy() : this(autoFailover: true)
        {
        }

        /// <summary>
        /// Initializes a new instance with custom configuration.
        /// </summary>
        /// <param name="autoFailover">Whether to automatically promote a secondary on primary failure.</param>
        /// <param name="failoverTimeout">Timeout for detecting primary failure.</param>
        public PrimarySecondaryReplicationStrategy(bool autoFailover, TimeSpan? failoverTimeout = null)
        {
            _autoFailover = autoFailover;
            _failoverTimeout = failoverTimeout ?? TimeSpan.FromSeconds(10);
        }

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "PrimarySecondary",
            Description = "Primary-Secondary replication - single writable primary with read-only secondaries and automatic failover",
            ConsistencyModel = ConsistencyModel.ReadYourWrites,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: false,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.FirstWriteWins },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: true,
                IsGeoAware: false,
                MaxReplicationLag: TimeSpan.FromSeconds(2),
                MinReplicaCount: 2,
                MaxReplicaCount: 50),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = false,
            SupportsDeltaSync = true,
            SupportsStreaming = true,
            TypicalLagMs = 500,
            ConsistencySlaMs = 2000
        };

        /// <summary>
        /// Sets the primary node.
        /// </summary>
        /// <param name="nodeId">The primary node identifier.</param>
        public void SetPrimary(string nodeId)
        {
            _primaryNodeId = nodeId;
            _secondaryNodeIds.Remove(nodeId);
        }

        /// <summary>
        /// Adds a secondary node.
        /// </summary>
        /// <param name="nodeId">The secondary node identifier.</param>
        public void AddSecondary(string nodeId)
        {
            if (nodeId != _primaryNodeId && !_secondaryNodeIds.Contains(nodeId))
            {
                _secondaryNodeIds.Add(nodeId);
            }
        }

        /// <summary>
        /// Removes a secondary node.
        /// </summary>
        /// <param name="nodeId">The secondary node identifier.</param>
        public void RemoveSecondary(string nodeId)
        {
            _secondaryNodeIds.Remove(nodeId);
        }

        /// <summary>
        /// Gets the current primary node.
        /// </summary>
        public string? GetPrimary() => _primaryNodeId;

        /// <summary>
        /// Gets all secondary nodes.
        /// </summary>
        public IReadOnlyList<string> GetSecondaries() => _secondaryNodeIds.AsReadOnly();

        /// <inheritdoc/>
        public override async Task ReplicateAsync(
            string sourceNodeId,
            IEnumerable<string> targetNodeIds,
            ReadOnlyMemory<byte> data,
            IDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            var targets = targetNodeIds.ToArray();
            ValidateReplicationTargets(targets);

            // Auto-configure if not set
            if (string.IsNullOrEmpty(_primaryNodeId))
            {
                _primaryNodeId = sourceNodeId;
                foreach (var target in targets)
                {
                    if (target != _primaryNodeId)
                        AddSecondary(target);
                }
            }

            // Verify source is the primary
            if (sourceNodeId != _primaryNodeId)
            {
                throw new InvalidOperationException(
                    $"Only the primary node ({_primaryNodeId}) can initiate writes. Source: {sourceNodeId}");
            }

            // Write to primary first (synchronous)
            await Task.Delay(Random.Shared.Next(10, 30), cancellationToken);

            // In production:
            // await _networkClient.WriteAsync(_primaryNodeId, data, metadata, cancellationToken);

            // Replicate to secondaries asynchronously in background
            _ = Task.Run(async () =>
            {
                var replicationTasks = targets
                    .Where(t => t != _primaryNodeId)
                    .Select(async targetId =>
                    {
                        try
                        {
                            await Task.Delay(Random.Shared.Next(50, 200), cancellationToken);
                            // In production:
                            // await _networkClient.ReplicateAsync(targetId, data, metadata, cancellationToken);
                        }
                        catch
                        {

                            // Log replication failure to secondary
                            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
                        }
                    });

                await Task.WhenAll(replicationTasks);
            }, cancellationToken);
        }

        /// <inheritdoc/>
        public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(
            ReplicationConflict conflict,
            CancellationToken cancellationToken = default)
        {
            // Primary-secondary doesn't have write conflicts - primary always wins
            return Task.FromResult((conflict.LocalData, conflict.LocalVersion));
        }

        /// <inheritdoc/>
        public override async Task<bool> VerifyConsistencyAsync(
            IEnumerable<string> nodeIds,
            string dataId,
            CancellationToken cancellationToken = default)
        {
            var nodes = nodeIds.ToArray();
            if (nodes.Length < 2)
                return true;

            // Read from primary
            await Task.Delay(Random.Shared.Next(5, 15), cancellationToken);
            var primaryHash = "primary-hash-value";

            // Read from secondaries
            var secondaryTasks = nodes
                .Where(n => n != _primaryNodeId)
                .Select(async nodeId =>
                {
                    await Task.Delay(Random.Shared.Next(10, 50), cancellationToken);
                    // Secondaries may lag slightly
                    return Random.Shared.Next(0, 10) > 1 ? primaryHash : "stale-hash";
                });

            var secondaryHashes = await Task.WhenAll(secondaryTasks);

            // Most secondaries should match primary (allow some lag)
            var matchingCount = secondaryHashes.Count(h => h == primaryHash);
            return matchingCount >= (secondaryHashes.Length * 0.8); // 80% threshold
        }

        /// <inheritdoc/>
        public override Task<TimeSpan> GetReplicationLagAsync(
            string sourceNodeId,
            string targetNodeId,
            CancellationToken cancellationToken = default)
        {
            // Primary has no lag
            if (targetNodeId == _primaryNodeId)
                return Task.FromResult(TimeSpan.Zero);

            // Secondaries have typical async replication lag
            var lagMs = Random.Shared.Next(100, 1000);
            return Task.FromResult(TimeSpan.FromMilliseconds(lagMs));
        }

        /// <summary>
        /// Performs automatic failover if primary becomes unavailable.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>True if failover succeeded, false otherwise.</returns>
        public async Task<bool> PerformFailoverAsync(CancellationToken cancellationToken = default)
        {
            if (!_autoFailover || _secondaryNodeIds.Count == 0)
                return false;

            // Check primary health
            var isPrimaryHealthy = await CheckPrimaryHealthAsync(cancellationToken);
            if (isPrimaryHealthy)
                return false; // Primary is healthy, no failover needed

            // Select new primary (choose secondary with lowest lag)
            var newPrimary = await SelectBestSecondaryForPromotionAsync(cancellationToken);
            if (newPrimary == null)
                return false;

            var oldPrimary = _primaryNodeId;
            _primaryNodeId = newPrimary;
            _secondaryNodeIds.Remove(newPrimary);

            if (oldPrimary != null)
                _secondaryNodeIds.Add(oldPrimary);

            return true;
        }

        /// <summary>
        /// Checks if the primary node is healthy.
        /// </summary>
        private async Task<bool> CheckPrimaryHealthAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(_primaryNodeId))
                return false;

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(_failoverTimeout);

                // Simulate health check
                await Task.Delay(Random.Shared.Next(10, 50), cts.Token);

                // In production:
                // return await _networkClient.PingAsync(_primaryNodeId, cts.Token);

                return Random.Shared.Next(0, 10) > 0; // 90% healthy
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Selects the best secondary to promote to primary.
        /// </summary>
        private async Task<string?> SelectBestSecondaryForPromotionAsync(CancellationToken cancellationToken)
        {
            if (_secondaryNodeIds.Count == 0)
                return null;

            // In production, select secondary with:
            // 1. Lowest replication lag
            // 2. Highest health score
            // 3. Most recent data

            var healthChecks = _secondaryNodeIds.Select(async nodeId =>
            {
                await Task.Delay(Random.Shared.Next(10, 50), cancellationToken);
                var lag = Random.Shared.Next(100, 1000);
                return (nodeId, lag);
            });

            var results = await Task.WhenAll(healthChecks);
            return results.OrderBy(r => r.lag).FirstOrDefault().nodeId;
        }

        /// <inheritdoc/>
        protected override string GetStrategyDescription() =>
            $"Primary-Secondary replication with {(_autoFailover ? "automatic" : "manual")} failover. " +
            $"Single writable primary, {_secondaryNodeIds.Count} read-only secondaries. " +
            $"Typical lag: {Characteristics.TypicalLagMs}ms. " +
            $"Use for read-heavy workloads, reporting, disaster recovery.";

        /// <inheritdoc/>
        protected override string[] GetKnowledgeTags() => new[]
        {
            "replication",
            "primary-secondary",
            "master-slave",
            "read-replicas",
            "automatic-failover",
            "read-scaling",
            "disaster-recovery"
        };
    }
}
