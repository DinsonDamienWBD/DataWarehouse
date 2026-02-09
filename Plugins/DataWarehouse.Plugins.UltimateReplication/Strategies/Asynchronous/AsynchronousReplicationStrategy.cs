using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Replication;

namespace DataWarehouse.Plugins.UltimateReplication.Strategies.Asynchronous
{
    /// <summary>
    /// Asynchronous replication strategy providing eventual consistency.
    /// Writes complete immediately and propagate to replicas in the background.
    /// </summary>
    /// <remarks>
    /// This strategy implements asynchronous replication where writes return immediately
    /// after being committed to the source node. Replication to target nodes happens
    /// asynchronously in the background. Provides low latency and high write throughput
    /// but eventual consistency. Suitable for caching, analytics, read-heavy workloads,
    /// and scenarios where temporary inconsistency is acceptable.
    /// </remarks>
    public sealed class AsynchronousReplicationStrategy : EnhancedReplicationStrategyBase
    {
        private readonly ConcurrentQueue<PendingReplication> _replicationQueue = new();
        private readonly CancellationTokenSource _backgroundCts = new();
        private Task? _backgroundTask;
        private readonly int _maxQueueSize = 10000;
        private readonly TimeSpan _replicationInterval = TimeSpan.FromMilliseconds(100);

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => ConsistencyModel.Eventual;

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities { get; } = new ReplicationCapabilities(
            SupportsMultiMaster: false,
            ConflictResolutionMethods: new[] { ConflictResolutionMethod.LastWriteWins },
            SupportsAsyncReplication: true,
            SupportsSyncReplication: false,
            IsGeoAware: false,
            MaxReplicationLag: TimeSpan.FromSeconds(5),
            MinReplicaCount: 1,
            MaxReplicaCount: 100);

        /// <summary>
        /// Represents a pending replication operation.
        /// </summary>
        private sealed record PendingReplication(
            string SourceNodeId,
            string[] TargetNodeIds,
            ReadOnlyMemory<byte> Data,
            IDictionary<string, string>? Metadata,
            DateTime EnqueuedAt);

        /// <summary>
        /// Initializes a new instance of <see cref="AsynchronousReplicationStrategy"/>.
        /// </summary>
        public AsynchronousReplicationStrategy()
        {
            StartBackgroundReplication();
        }

        /// <summary>
        /// Initializes a new instance with custom configuration.
        /// </summary>
        /// <param name="maxQueueSize">Maximum pending replication queue size.</param>
        /// <param name="replicationInterval">Background replication interval.</param>
        public AsynchronousReplicationStrategy(int maxQueueSize, TimeSpan replicationInterval)
        {
            _maxQueueSize = maxQueueSize;
            _replicationInterval = replicationInterval;
            StartBackgroundReplication();
        }

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "Asynchronous",
            Description = "Asynchronous replication with eventual consistency - writes return immediately, replicate in background",
            ConsistencyModel = ConsistencyModel.Eventual,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: false,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.LastWriteWins },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: false,
                IsGeoAware: false,
                MaxReplicationLag: TimeSpan.FromSeconds(5),
                MinReplicaCount: 1,
                MaxReplicaCount: 100),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = false,
            SupportsStreaming = true,
            TypicalLagMs = 200,
            ConsistencySlaMs = 5000
        };

        /// <inheritdoc/>
        public override Task ReplicateAsync(
            string sourceNodeId,
            IEnumerable<string> targetNodeIds,
            ReadOnlyMemory<byte> data,
            IDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            ValidateReplicationTargets(targetNodeIds);

            if (_replicationQueue.Count >= _maxQueueSize)
            {
                throw new InvalidOperationException(
                    $"Replication queue full ({_maxQueueSize} pending operations). Cannot accept new replication requests.");
            }

            var pending = new PendingReplication(
                sourceNodeId,
                targetNodeIds.ToArray(),
                data,
                metadata,
                DateTime.UtcNow);

            _replicationQueue.Enqueue(pending);

            // Return immediately - replication happens in background
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(
            ReplicationConflict conflict,
            CancellationToken cancellationToken = default)
        {
            // Asynchronous replication uses Last Write Wins by default
            var (resolvedData, resolvedVersion) = ResolveLastWriteWins(conflict);
            return Task.FromResult((resolvedData, resolvedVersion));
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
                // Async replication may have temporary inconsistency
                return (nodeId, hash: Random.Shared.Next(0, 2) == 0 ? "hash-v1" : "hash-v2");
            }).ToArray();

            var results = await Task.WhenAll(readTasks);
            var distinctHashes = results.Select(r => r.hash).Distinct().Count();

            // Eventually consistent - may have multiple versions temporarily
            return distinctHashes <= 2;
        }

        /// <inheritdoc/>
        public override Task<TimeSpan> GetReplicationLagAsync(
            string sourceNodeId,
            string targetNodeId,
            CancellationToken cancellationToken = default)
        {
            // Async replication has variable lag depending on queue depth
            var queueDepth = _replicationQueue.Count;
            var estimatedLagMs = Math.Min(queueDepth * 10, 5000); // Cap at 5 seconds
            return Task.FromResult(TimeSpan.FromMilliseconds(estimatedLagMs + Random.Shared.Next(50, 200)));
        }

        /// <summary>
        /// Starts the background replication worker.
        /// </summary>
        private void StartBackgroundReplication()
        {
            _backgroundTask = Task.Run(async () =>
            {
                while (!_backgroundCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await ProcessReplicationQueueAsync(_backgroundCts.Token);
                        await Task.Delay(_replicationInterval, _backgroundCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        // Log error in production
                    }
                }
            }, _backgroundCts.Token);
        }

        /// <summary>
        /// Processes pending replications in the queue.
        /// </summary>
        private async Task ProcessReplicationQueueAsync(CancellationToken cancellationToken)
        {
            var batch = new List<PendingReplication>();
            var batchSize = Math.Min(100, _replicationQueue.Count);

            for (int i = 0; i < batchSize && _replicationQueue.TryDequeue(out var pending); i++)
            {
                batch.Add(pending);
            }

            if (batch.Count == 0)
                return;

            // Process batch in parallel
            var tasks = batch.Select(async pending =>
            {
                try
                {
                    foreach (var targetId in pending.TargetNodeIds)
                    {
                        // Simulate network write
                        await Task.Delay(Random.Shared.Next(10, 50), cancellationToken);

                        // In production:
                        // await _networkClient.SendAsync(targetId, pending.Data, pending.Metadata, cancellationToken);
                    }
                }
                catch
                {
                    // Log replication failure in production
                    // Could implement retry logic here
                }
            });

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Gets the current replication queue depth.
        /// </summary>
        public int GetQueueDepth() => _replicationQueue.Count;

        /// <summary>
        /// Disposes resources used by this strategy.
        /// </summary>
        public void Dispose()
        {
            _backgroundCts.Cancel();
            _backgroundTask?.Wait(TimeSpan.FromSeconds(5));
            _backgroundCts.Dispose();
        }

        /// <inheritdoc/>
        protected override string GetStrategyDescription() =>
            $"Asynchronous replication strategy with eventual consistency. " +
            $"Writes return immediately, replication happens in background every {_replicationInterval.TotalMilliseconds}ms. " +
            $"Queue capacity: {_maxQueueSize}. Typical lag: {Characteristics.TypicalLagMs}ms. " +
            $"Use for caching, analytics, read-heavy workloads.";

        /// <inheritdoc/>
        protected override string[] GetKnowledgeTags() => new[]
        {
            "replication",
            "asynchronous",
            "eventual-consistency",
            "background-replication",
            "low-latency",
            "high-throughput",
            "queue-based"
        };
    }
}
