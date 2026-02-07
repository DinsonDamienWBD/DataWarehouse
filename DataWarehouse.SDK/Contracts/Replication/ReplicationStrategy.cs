using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Replication
{
    /// <summary>
    /// Defines a strategy for data replication modes including multi-master, conflict resolution,
    /// and consistency models. This interface supports various replication topologies and conflict
    /// handling strategies for distributed data warehouses.
    /// </summary>
    public interface IReplicationStrategy
    {
        /// <summary>
        /// Gets the capabilities supported by this replication strategy.
        /// </summary>
        ReplicationCapabilities Capabilities { get; }

        /// <summary>
        /// Gets the consistency model provided by this strategy.
        /// </summary>
        ConsistencyModel ConsistencyModel { get; }

        /// <summary>
        /// Replicates data from a source node to one or more target nodes.
        /// </summary>
        /// <param name="sourceNodeId">The identifier of the source node.</param>
        /// <param name="targetNodeIds">The identifiers of the target nodes.</param>
        /// <param name="data">The data to replicate.</param>
        /// <param name="metadata">Optional metadata associated with the data.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous replication operation.</returns>
        Task ReplicateAsync(
            string sourceNodeId,
            IEnumerable<string> targetNodeIds,
            ReadOnlyMemory<byte> data,
            IDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Detects conflicts between concurrent updates using vector clocks or similar mechanisms.
        /// </summary>
        /// <param name="localVersion">The local version information.</param>
        /// <param name="remoteVersion">The remote version information.</param>
        /// <param name="localData">The local data.</param>
        /// <param name="remoteData">The remote data.</param>
        /// <returns>A replication conflict if detected, null otherwise.</returns>
        ReplicationConflict? DetectConflict(
            VectorClock localVersion,
            VectorClock remoteVersion,
            ReadOnlyMemory<byte> localData,
            ReadOnlyMemory<byte> remoteData);

        /// <summary>
        /// Resolves a replication conflict using the configured resolution strategy.
        /// </summary>
        /// <param name="conflict">The conflict to resolve.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The resolved data and version information.</returns>
        Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(
            ReplicationConflict conflict,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Verifies the integrity and consistency of replicated data across nodes.
        /// </summary>
        /// <param name="nodeIds">The nodes to verify.</param>
        /// <param name="dataId">The identifier of the data to verify.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>True if all replicas are consistent, false otherwise.</returns>
        Task<bool> VerifyConsistencyAsync(
            IEnumerable<string> nodeIds,
            string dataId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the current replication lag for a specific target node.
        /// </summary>
        /// <param name="sourceNodeId">The source node identifier.</param>
        /// <param name="targetNodeId">The target node identifier.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The replication lag duration.</returns>
        Task<TimeSpan> GetReplicationLagAsync(
            string sourceNodeId,
            string targetNodeId,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Describes the capabilities of a replication strategy including multi-master support,
    /// conflict resolution mechanisms, synchronous/asynchronous modes, and geo-aware features.
    /// </summary>
    /// <param name="SupportsMultiMaster">Indicates if the strategy supports multi-master replication.</param>
    /// <param name="ConflictResolutionMethods">The conflict resolution methods supported.</param>
    /// <param name="SupportsAsyncReplication">Indicates if asynchronous replication is supported.</param>
    /// <param name="SupportsSyncReplication">Indicates if synchronous replication is supported.</param>
    /// <param name="IsGeoAware">Indicates if the strategy considers geographic topology.</param>
    /// <param name="MaxReplicationLag">The maximum acceptable replication lag.</param>
    /// <param name="MinReplicaCount">The minimum number of replicas required.</param>
    /// <param name="MaxReplicaCount">The maximum number of replicas supported.</param>
    public record ReplicationCapabilities(
        bool SupportsMultiMaster,
        ConflictResolutionMethod[] ConflictResolutionMethods,
        bool SupportsAsyncReplication,
        bool SupportsSyncReplication,
        bool IsGeoAware,
        TimeSpan? MaxReplicationLag,
        int MinReplicaCount,
        int MaxReplicaCount);

    /// <summary>
    /// Defines the consistency model for replicated data, ranging from strong consistency
    /// to eventual consistency with various intermediate guarantees.
    /// </summary>
    public enum ConsistencyModel
    {
        /// <summary>
        /// All nodes see the same data at the same time. Strongest consistency guarantee.
        /// </summary>
        Strong = 0,

        /// <summary>
        /// All replicas will eventually converge to the same state given no new updates.
        /// </summary>
        Eventual = 1,

        /// <summary>
        /// Operations are ordered based on causal relationships.
        /// </summary>
        Causal = 2,

        /// <summary>
        /// A client always sees its own writes in subsequent reads.
        /// </summary>
        SessionConsistent = 3,

        /// <summary>
        /// Reads lag behind writes by at most a bounded time period.
        /// </summary>
        BoundedStaleness = 4,

        /// <summary>
        /// Guarantees that a read operation always returns the result of the most recent write
        /// within the same session.
        /// </summary>
        ReadYourWrites = 5,

        /// <summary>
        /// Subsequent reads will never return older values than previous reads.
        /// </summary>
        MonotonicReads = 6,

        /// <summary>
        /// Writes from the same client are applied in order.
        /// </summary>
        MonotonicWrites = 7
    }

    /// <summary>
    /// Conflict resolution methods for handling concurrent updates in multi-master replication.
    /// </summary>
    public enum ConflictResolutionMethod
    {
        /// <summary>
        /// Last write wins based on timestamp.
        /// </summary>
        LastWriteWins = 0,

        /// <summary>
        /// First write wins, subsequent updates are rejected.
        /// </summary>
        FirstWriteWins = 1,

        /// <summary>
        /// Custom application-defined conflict resolution logic.
        /// </summary>
        Custom = 2,

        /// <summary>
        /// Merge conflicting updates using a merge function.
        /// </summary>
        Merge = 3,

        /// <summary>
        /// Higher priority node wins.
        /// </summary>
        PriorityBased = 4,

        /// <summary>
        /// Manual intervention required to resolve conflict.
        /// </summary>
        Manual = 5,

        /// <summary>
        /// Use Conflict-free Replicated Data Type (CRDT) semantics.
        /// </summary>
        Crdt = 6
    }

    /// <summary>
    /// Represents a vector clock for tracking causal ordering of events across distributed nodes.
    /// Used for conflict detection in multi-master replication scenarios.
    /// </summary>
    public class VectorClock
    {
        private readonly Dictionary<string, long> _clock;

        /// <summary>
        /// Initializes a new instance of the <see cref="VectorClock"/> class.
        /// </summary>
        public VectorClock()
        {
            _clock = new Dictionary<string, long>();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="VectorClock"/> class from an existing clock.
        /// </summary>
        /// <param name="clock">The clock values to copy.</param>
        public VectorClock(IReadOnlyDictionary<string, long> clock)
        {
            _clock = new Dictionary<string, long>(clock);
        }

        /// <summary>
        /// Gets the clock value for a specific node.
        /// </summary>
        /// <param name="nodeId">The node identifier.</param>
        /// <returns>The clock value, or 0 if the node is not in the clock.</returns>
        public long this[string nodeId] => _clock.TryGetValue(nodeId, out var value) ? value : 0;

        /// <summary>
        /// Gets all node identifiers in the vector clock.
        /// </summary>
        public IEnumerable<string> Nodes => _clock.Keys;

        /// <summary>
        /// Increments the clock value for a specific node.
        /// </summary>
        /// <param name="nodeId">The node identifier.</param>
        public void Increment(string nodeId)
        {
            if (!_clock.ContainsKey(nodeId))
                _clock[nodeId] = 1;
            else
                _clock[nodeId]++;
        }

        /// <summary>
        /// Merges this vector clock with another, taking the maximum value for each node.
        /// </summary>
        /// <param name="other">The other vector clock.</param>
        public void Merge(VectorClock other)
        {
            foreach (var node in other.Nodes)
            {
                var otherValue = other[node];
                if (!_clock.ContainsKey(node))
                    _clock[node] = otherValue;
                else
                    _clock[node] = Math.Max(_clock[node], otherValue);
            }
        }

        /// <summary>
        /// Determines if this vector clock happens before another (is causally earlier).
        /// </summary>
        /// <param name="other">The other vector clock.</param>
        /// <returns>True if this clock happens before the other.</returns>
        public bool HappensBefore(VectorClock other)
        {
            bool anyLess = false;
            var allNodes = Nodes.Union(other.Nodes);

            foreach (var node in allNodes)
            {
                var thisValue = this[node];
                var otherValue = other[node];

                if (thisValue > otherValue)
                    return false;
                if (thisValue < otherValue)
                    anyLess = true;
            }

            return anyLess;
        }

        /// <summary>
        /// Determines if this vector clock is concurrent with another (neither happens before the other).
        /// </summary>
        /// <param name="other">The other vector clock.</param>
        /// <returns>True if the clocks are concurrent.</returns>
        public bool IsConcurrentWith(VectorClock other)
        {
            return !HappensBefore(other) && !other.HappensBefore(this);
        }

        /// <summary>
        /// Creates a deep copy of this vector clock.
        /// </summary>
        /// <returns>A new vector clock with the same values.</returns>
        public VectorClock Clone()
        {
            return new VectorClock(_clock);
        }

        /// <summary>
        /// Returns a string representation of the vector clock.
        /// </summary>
        public override string ToString()
        {
            return "{" + string.Join(", ", _clock.Select(kvp => $"{kvp.Key}:{kvp.Value}")) + "}";
        }
    }

    /// <summary>
    /// Describes a replication conflict with both versions of the data and metadata
    /// needed to resolve the conflict.
    /// </summary>
    /// <param name="DataId">The identifier of the conflicting data.</param>
    /// <param name="LocalVersion">The local vector clock version.</param>
    /// <param name="RemoteVersion">The remote vector clock version.</param>
    /// <param name="LocalData">The local data version.</param>
    /// <param name="RemoteData">The remote data version.</param>
    /// <param name="LocalNodeId">The identifier of the local node.</param>
    /// <param name="RemoteNodeId">The identifier of the remote node.</param>
    /// <param name="DetectedAt">The timestamp when the conflict was detected.</param>
    /// <param name="LocalMetadata">Optional local metadata.</param>
    /// <param name="RemoteMetadata">Optional remote metadata.</param>
    public record ReplicationConflict(
        string DataId,
        VectorClock LocalVersion,
        VectorClock RemoteVersion,
        ReadOnlyMemory<byte> LocalData,
        ReadOnlyMemory<byte> RemoteData,
        string LocalNodeId,
        string RemoteNodeId,
        DateTimeOffset DetectedAt,
        IReadOnlyDictionary<string, string>? LocalMetadata = null,
        IReadOnlyDictionary<string, string>? RemoteMetadata = null);

    /// <summary>
    /// Abstract base class for replication strategies providing common conflict detection
    /// and resolution infrastructure.
    /// </summary>
    public abstract class ReplicationStrategyBase : IReplicationStrategy
    {
        /// <inheritdoc/>
        public abstract ReplicationCapabilities Capabilities { get; }

        /// <inheritdoc/>
        public abstract ConsistencyModel ConsistencyModel { get; }

        /// <inheritdoc/>
        public abstract Task ReplicateAsync(
            string sourceNodeId,
            IEnumerable<string> targetNodeIds,
            ReadOnlyMemory<byte> data,
            IDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default);

        /// <inheritdoc/>
        public virtual ReplicationConflict? DetectConflict(
            VectorClock localVersion,
            VectorClock remoteVersion,
            ReadOnlyMemory<byte> localData,
            ReadOnlyMemory<byte> remoteData)
        {
            // No conflict if versions are equal
            if (!localVersion.IsConcurrentWith(remoteVersion))
                return null;

            // Check if data is actually different
            if (localData.Span.SequenceEqual(remoteData.Span))
                return null;

            // Conflict detected
            return new ReplicationConflict(
                DataId: Guid.NewGuid().ToString(),
                LocalVersion: localVersion,
                RemoteVersion: remoteVersion,
                LocalData: localData,
                RemoteData: remoteData,
                LocalNodeId: string.Empty,
                RemoteNodeId: string.Empty,
                DetectedAt: DateTimeOffset.UtcNow);
        }

        /// <inheritdoc/>
        public abstract Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(
            ReplicationConflict conflict,
            CancellationToken cancellationToken = default);

        /// <inheritdoc/>
        public abstract Task<bool> VerifyConsistencyAsync(
            IEnumerable<string> nodeIds,
            string dataId,
            CancellationToken cancellationToken = default);

        /// <inheritdoc/>
        public abstract Task<TimeSpan> GetReplicationLagAsync(
            string sourceNodeId,
            string targetNodeId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Resolves a conflict using the Last Write Wins strategy based on timestamps.
        /// </summary>
        /// <param name="conflict">The conflict to resolve.</param>
        /// <returns>The winning data and version.</returns>
        protected virtual (ReadOnlyMemory<byte> Data, VectorClock Version) ResolveLastWriteWins(
            ReplicationConflict conflict)
        {
            // Compare timestamps to determine winner
            var localTimestamp = conflict.LocalMetadata?.TryGetValue("timestamp", out var localTs) == true
                ? DateTimeOffset.Parse(localTs)
                : DateTimeOffset.MinValue;

            var remoteTimestamp = conflict.RemoteMetadata?.TryGetValue("timestamp", out var remoteTs) == true
                ? DateTimeOffset.Parse(remoteTs)
                : DateTimeOffset.MinValue;

            if (remoteTimestamp > localTimestamp)
            {
                var mergedVersion = conflict.LocalVersion.Clone();
                mergedVersion.Merge(conflict.RemoteVersion);
                return (conflict.RemoteData, mergedVersion);
            }
            else
            {
                var mergedVersion = conflict.RemoteVersion.Clone();
                mergedVersion.Merge(conflict.LocalVersion);
                return (conflict.LocalData, mergedVersion);
            }
        }

        /// <summary>
        /// Validates that the target nodes are within acceptable replication parameters.
        /// </summary>
        /// <param name="targetNodeIds">The target node identifiers.</param>
        /// <exception cref="ArgumentException">Thrown when the node count is invalid.</exception>
        protected virtual void ValidateReplicationTargets(IEnumerable<string> targetNodeIds)
        {
            var nodeCount = targetNodeIds.Count();

            if (nodeCount < Capabilities.MinReplicaCount)
                throw new ArgumentException(
                    $"Minimum replica count is {Capabilities.MinReplicaCount}, got {nodeCount}");

            if (nodeCount > Capabilities.MaxReplicaCount)
                throw new ArgumentException(
                    $"Maximum replica count is {Capabilities.MaxReplicaCount}, got {nodeCount}");
        }

        #region Intelligence Integration

        /// <summary>
        /// Unique identifier for this replication strategy.
        /// </summary>
        public virtual string StrategyId => $"replication-{ConsistencyModel.ToString().ToLowerInvariant()}";

        /// <summary>
        /// Human-readable name of this replication strategy.
        /// </summary>
        public virtual string StrategyName => $"{ConsistencyModel} Replication";

        /// <summary>
        /// Message bus reference for Intelligence communication.
        /// </summary>
        protected IMessageBus? MessageBus { get; private set; }

        /// <summary>
        /// Configures Intelligence integration for this strategy.
        /// Called by the plugin to enable AI-enhanced features.
        /// </summary>
        public virtual void ConfigureIntelligence(IMessageBus? messageBus)
        {
            MessageBus = messageBus;
        }

        /// <summary>
        /// Whether Intelligence is available for this strategy.
        /// </summary>
        protected bool IsIntelligenceAvailable => MessageBus != null;

        /// <summary>
        /// Gets static knowledge about this strategy for AI discovery.
        /// Override to provide strategy-specific knowledge.
        /// </summary>
        public virtual KnowledgeObject GetStrategyKnowledge()
        {
            return new KnowledgeObject
            {
                Id = $"strategy.{StrategyId}",
                Topic = $"{GetKnowledgeTopic()}",
                SourcePluginId = "sdk.strategy",
                SourcePluginName = StrategyName,
                KnowledgeType = "capability",
                Description = GetStrategyDescription(),
                Payload = GetKnowledgePayload(),
                Tags = GetKnowledgeTags()
            };
        }

        /// <summary>
        /// Gets the registered capability for this strategy.
        /// </summary>
        public virtual RegisteredCapability GetStrategyCapability()
        {
            return new RegisteredCapability
            {
                CapabilityId = $"strategy.{StrategyId}",
                DisplayName = StrategyName,
                Description = GetStrategyDescription(),
                Category = GetCapabilityCategory(),
                PluginId = "sdk.strategy",
                PluginName = StrategyName,
                PluginVersion = "1.0.0",
                Tags = GetKnowledgeTags(),
                Metadata = GetCapabilityMetadata(),
                SemanticDescription = GetSemanticDescription()
            };
        }

        /// <summary>
        /// Gets the knowledge topic for this strategy type.
        /// </summary>
        protected virtual string GetKnowledgeTopic() => "replication";

        /// <summary>
        /// Gets the capability category for this strategy type.
        /// </summary>
        protected virtual CapabilityCategory GetCapabilityCategory() => CapabilityCategory.Replication;

        /// <summary>
        /// Gets a description for this strategy.
        /// </summary>
        protected virtual string GetStrategyDescription() =>
            $"{StrategyName} strategy with {ConsistencyModel} consistency and {Capabilities.MinReplicaCount}-{Capabilities.MaxReplicaCount} replicas";

        /// <summary>
        /// Gets the knowledge payload for this strategy.
        /// </summary>
        protected virtual Dictionary<string, object> GetKnowledgePayload() => new()
        {
            ["consistencyModel"] = ConsistencyModel.ToString(),
            ["supportsMultiMaster"] = Capabilities.SupportsMultiMaster,
            ["supportsAsyncReplication"] = Capabilities.SupportsAsyncReplication,
            ["supportsSyncReplication"] = Capabilities.SupportsSyncReplication,
            ["isGeoAware"] = Capabilities.IsGeoAware,
            ["minReplicaCount"] = Capabilities.MinReplicaCount,
            ["maxReplicaCount"] = Capabilities.MaxReplicaCount,
            ["conflictResolutionMethods"] = Capabilities.ConflictResolutionMethods.Select(m => m.ToString()).ToArray()
        };

        /// <summary>
        /// Gets tags for this strategy.
        /// </summary>
        protected virtual string[] GetKnowledgeTags() => new[]
        {
            "strategy",
            "replication",
            ConsistencyModel.ToString().ToLowerInvariant(),
            Capabilities.SupportsMultiMaster ? "multi-master" : "single-master"
        };

        /// <summary>
        /// Gets capability metadata for this strategy.
        /// </summary>
        protected virtual Dictionary<string, object> GetCapabilityMetadata() => new()
        {
            ["consistencyModel"] = ConsistencyModel.ToString(),
            ["supportsMultiMaster"] = Capabilities.SupportsMultiMaster
        };

        /// <summary>
        /// Gets the semantic description for AI-driven discovery.
        /// </summary>
        protected virtual string GetSemanticDescription() =>
            $"Use {StrategyName} for {ConsistencyModel} consistency with {(Capabilities.SupportsMultiMaster ? "multi-master" : "single-master")} topology";

        /// <summary>
        /// Requests an AI recommendation for optimal replication configuration.
        /// </summary>
        /// <param name="latencyRequirement">Latency requirement in milliseconds.</param>
        /// <param name="consistencyPreference">Preferred consistency level.</param>
        /// <param name="geoDistributed">Whether nodes are geographically distributed.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Recommended replication configuration, or null if Intelligence unavailable.</returns>
        protected async Task<Dictionary<string, object>?> RequestReplicationRecommendationAsync(
            int latencyRequirement,
            string consistencyPreference,
            bool geoDistributed = false,
            CancellationToken cancellationToken = default)
        {
            if (!IsIntelligenceAvailable || MessageBus == null)
                return null;

            var request = new Dictionary<string, object>
            {
                ["requestType"] = "replication_recommendation",
                ["strategyId"] = StrategyId,
                ["latencyRequirement"] = latencyRequirement,
                ["consistencyPreference"] = consistencyPreference,
                ["geoDistributed"] = geoDistributed,
                ["currentConsistencyModel"] = ConsistencyModel.ToString()
            };

            var message = new PluginMessage
            {
                MessageId = Guid.NewGuid().ToString(),
                SourcePluginId = StrategyId,
                MessageType = "intelligence.request",
                Payload = request
            };

            var response = await MessageBus.SendAsync(MessageTopics.AIQuery, message, cancellationToken);
            return response.Success ? response.Payload as Dictionary<string, object> : null;
        }

        #endregion
    }
}
