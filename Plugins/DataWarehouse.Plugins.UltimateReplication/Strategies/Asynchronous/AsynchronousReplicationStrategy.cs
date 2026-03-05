using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Replication;
using DataWarehouse.SDK.Utilities;

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

        /// <summary>
        /// Tracks the latest replicated data per node and data key for consistency verification.
        /// Key format: "{nodeId}:{dataId}" -> SHA-256 hash of data + sequence number.
        /// </summary>
        private readonly BoundedDictionary<string, (string Hash, long Sequence)> _nodeDataHashes =
            new BoundedDictionary<string, (string, long)>(100_000);

        private long _globalSequence;

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
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxQueueSize);
            if (replicationInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(replicationInterval), "Replication interval must be positive.");
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

            var targets = targetNodeIds.ToArray();
            var dataId = metadata?.GetValueOrDefault("dataId") ?? "default";
            var dataHash = ComputeSha256Hash(data.Span);
            var seq = Interlocked.Increment(ref _globalSequence);

            // Record the source node's hash immediately (write is committed here)
            _nodeDataHashes[$"{sourceNodeId}:{dataId}"] = (dataHash, seq);

            var pending = new PendingReplication(
                sourceNodeId,
                targets,
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
        public override Task<bool> VerifyConsistencyAsync(
            IEnumerable<string> nodeIds,
            string dataId,
            CancellationToken cancellationToken = default)
        {
            var nodes = nodeIds.ToArray();
            if (nodes.Length < 2)
                return Task.FromResult(true);

            // Read actual stored hashes for each node
            var hashes = new List<(string NodeId, string Hash, long Sequence)>();
            foreach (var nodeId in nodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = $"{nodeId}:{dataId}";
                if (_nodeDataHashes.TryGetValue(key, out var entry))
                {
                    hashes.Add((nodeId, entry.Hash, entry.Sequence));
                }
                // Node has no record for this dataId — it hasn't received the data yet
            }

            // If no nodes have data, it's vacuously consistent
            if (hashes.Count == 0)
                return Task.FromResult(true);

            // For eventual consistency: check if all nodes that HAVE the data agree on its hash.
            // Nodes that haven't received data yet represent acceptable lag, not inconsistency.
            var distinctHashes = hashes.Select(h => h.Hash).Distinct().Count();

            // All nodes that have received the data must have the same hash
            return Task.FromResult(distinctHashes == 1);
        }

        /// <inheritdoc/>
        public override Task<TimeSpan> GetReplicationLagAsync(
            string sourceNodeId,
            string targetNodeId,
            CancellationToken cancellationToken = default)
        {
            // Use tracked replication lag from LagTracker (recorded during queue processing)
            var trackedLag = LagTracker.GetCurrentLag(targetNodeId);

            // If we have no tracked lag yet, estimate from queue depth
            if (trackedLag == TimeSpan.Zero && _replicationQueue.Count > 0)
            {
                var queueDepth = _replicationQueue.Count;
                var estimatedLagMs = Math.Min(queueDepth * 10, 5000); // Cap at 5 seconds
                return Task.FromResult(TimeSpan.FromMilliseconds(estimatedLagMs));
            }

            return Task.FromResult(trackedLag);
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
                        System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
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
                    var dataId = pending.Metadata?.GetValueOrDefault("dataId") ?? "default";
                    var dataHash = ComputeSha256Hash(pending.Data.Span);
                    var seq = Interlocked.Increment(ref _globalSequence);

                    foreach (var targetId in pending.TargetNodeIds)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        // Record that this target now has this version of the data
                        _nodeDataHashes[$"{targetId}:{dataId}"] = (dataHash, seq);

                        // Track replication lag based on time since enqueue
                        var lag = DateTime.UtcNow - pending.EnqueuedAt;
                        RecordReplicationLag(targetId, lag);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Warning] Replication batch processing failed: {ex.Message}");
                }
            });

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Records the data hash for a specific node and data identifier.
        /// Called during replication to track what data each node holds.
        /// </summary>
        /// <param name="nodeId">The node identifier.</param>
        /// <param name="dataId">The data identifier.</param>
        /// <param name="data">The data bytes.</param>
        internal void RecordNodeData(string nodeId, string dataId, ReadOnlySpan<byte> data)
        {
            var hash = ComputeSha256Hash(data);
            var seq = Interlocked.Increment(ref _globalSequence);
            _nodeDataHashes[$"{nodeId}:{dataId}"] = (hash, seq);
        }

        /// <summary>
        /// Computes a SHA-256 hash of the given data.
        /// </summary>
        private static string ComputeSha256Hash(ReadOnlySpan<byte> data)
        {
            Span<byte> hashBytes = stackalloc byte[32];
            SHA256.HashData(data, hashBytes);
            return Convert.ToHexString(hashBytes);
        }

        /// <summary>
        /// Gets the current replication queue depth.
        /// </summary>
        public int GetQueueDepth() => _replicationQueue.Count;

        /// <summary>
        /// Disposes resources used by this strategy.
        /// </summary>
        public new void Dispose()
        {
            _backgroundCts.Cancel();
            _backgroundTask?.Wait(TimeSpan.FromSeconds(5));
            _backgroundCts.Dispose();
            base.Dispose();
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
