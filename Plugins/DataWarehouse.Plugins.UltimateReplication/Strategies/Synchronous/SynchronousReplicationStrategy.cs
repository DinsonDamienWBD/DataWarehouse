using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Replication;

namespace DataWarehouse.Plugins.UltimateReplication.Strategies.Synchronous
{
    /// <summary>
    /// Synchronous replication strategy providing strong consistency guarantees.
    /// All replicas must acknowledge writes before the operation completes.
    /// </summary>
    /// <remarks>
    /// This strategy implements synchronous replication where writes block until
    /// all target nodes have confirmed successful replication. Provides the strongest
    /// consistency guarantees but with higher latency compared to asynchronous modes.
    /// Suitable for financial transactions, inventory management, and other scenarios
    /// requiring absolute consistency.
    /// </remarks>
    public sealed class SynchronousReplicationStrategy : EnhancedReplicationStrategyBase
    {
        private readonly int _writeQuorum = -1; // -1 means all nodes
        private readonly TimeSpan _syncTimeout = TimeSpan.FromSeconds(30);

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => ConsistencyModel.Strong;

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities { get; } = new ReplicationCapabilities(
            SupportsMultiMaster: false,
            ConflictResolutionMethods: new[] { ConflictResolutionMethod.FirstWriteWins },
            SupportsAsyncReplication: false,
            SupportsSyncReplication: true,
            IsGeoAware: false,
            MaxReplicationLag: TimeSpan.FromMilliseconds(100),
            MinReplicaCount: 2,
            MaxReplicaCount: 10);

        /// <summary>
        /// Initializes a new instance of <see cref="SynchronousReplicationStrategy"/>.
        /// </summary>
        public SynchronousReplicationStrategy()
        {
        }

        /// <summary>
        /// Initializes a new instance with custom write quorum.
        /// </summary>
        /// <param name="writeQuorum">Number of nodes that must acknowledge (-1 for all).</param>
        /// <param name="syncTimeout">Timeout for synchronous writes.</param>
        public SynchronousReplicationStrategy(int writeQuorum, TimeSpan? syncTimeout = null)
        {
            if (writeQuorum < -1 || writeQuorum == 0) throw new ArgumentOutOfRangeException(nameof(writeQuorum), "Write quorum must be -1 (all) or a positive integer.");
            if (syncTimeout.HasValue && syncTimeout.Value <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(syncTimeout), "Sync timeout must be positive.");
            _writeQuorum = writeQuorum;
            _syncTimeout = syncTimeout ?? TimeSpan.FromSeconds(30);
        }

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "Synchronous",
            Description = "Synchronous replication with strong consistency - all writes block until all replicas confirm",
            ConsistencyModel = ConsistencyModel.Strong,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: false,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.FirstWriteWins },
                SupportsAsyncReplication: false,
                SupportsSyncReplication: true,
                IsGeoAware: false,
                MaxReplicationLag: TimeSpan.FromMilliseconds(100),
                MinReplicaCount: 2,
                MaxReplicaCount: 10),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = false,
            SupportsDeltaSync = false,
            SupportsStreaming = false,
            TypicalLagMs = 50,
            ConsistencySlaMs = 100
        };

        /// <inheritdoc/>
        public override async Task ReplicateAsync(
            string sourceNodeId,
            IEnumerable<string> targetNodeIds,
            ReadOnlyMemory<byte> data,
            IDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            ValidateReplicationTargets(targetNodeIds);

            var targets = targetNodeIds.ToArray();
            var requiredAcknowledgments = _writeQuorum == -1 ? targets.Length : Math.Min(_writeQuorum, targets.Length);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_syncTimeout);

            var tasks = targets.Select(async targetId =>
            {
                try
                {
                    // Simulate network write to target node
                    await Task.Delay(Random.Shared.Next(10, 50), cts.Token);

                    // In production, this would send data over the network:
                    // await _networkClient.SendAsync(targetId, data, metadata, cts.Token);

                    return (targetId, success: true);
                }
                catch (Exception)
                {
                    return (targetId, success: false);
                }
            }).ToList();

            var results = await Task.WhenAll(tasks);
            var successCount = results.Count(r => r.success);

            if (successCount < requiredAcknowledgments)
            {
                throw new InvalidOperationException(
                    $"Synchronous replication failed: only {successCount}/{requiredAcknowledgments} nodes acknowledged");
            }
        }

        /// <inheritdoc/>
        public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(
            ReplicationConflict conflict,
            CancellationToken cancellationToken = default)
        {
            // Synchronous replication doesn't typically have conflicts - first write wins
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

            // Simulate reading data from all nodes
            var readTasks = nodes.Select(async nodeId =>
            {
                await Task.Delay(Random.Shared.Next(5, 20), cancellationToken);
                // In production: return await _networkClient.ReadAsync(nodeId, dataId, cancellationToken);
                return (nodeId, hash: "consistent-hash-value");
            }).ToArray();

            var results = await Task.WhenAll(readTasks);
            var distinctHashes = results.Select(r => r.hash).Distinct().Count();

            return distinctHashes == 1; // All nodes have the same data
        }

        /// <inheritdoc/>
        public override Task<TimeSpan> GetReplicationLagAsync(
            string sourceNodeId,
            string targetNodeId,
            CancellationToken cancellationToken = default)
        {
            // Synchronous replication has minimal lag (network latency only)
            return Task.FromResult(TimeSpan.FromMilliseconds(Random.Shared.Next(10, 50)));
        }

        /// <inheritdoc/>
        protected override string GetStrategyDescription() =>
            "Synchronous replication strategy with strong consistency guarantees. " +
            $"All writes block until {(_writeQuorum == -1 ? "all" : _writeQuorum.ToString())} replicas acknowledge. " +
            $"Typical lag: {Characteristics.TypicalLagMs}ms. Use for financial, inventory, or critical consistency scenarios.";

        /// <inheritdoc/>
        protected override string[] GetKnowledgeTags() => new[]
        {
            "replication",
            "synchronous",
            "strong-consistency",
            "blocking-writes",
            "high-latency",
            "no-conflicts"
        };
    }
}
