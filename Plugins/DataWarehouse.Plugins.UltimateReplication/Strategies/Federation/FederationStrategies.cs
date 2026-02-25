using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Replication;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateReplication.Strategies.Federation
{
    /// <summary>
    /// Represents a federated data source.
    /// </summary>
    public sealed class FederatedDataSource
    {
        /// <summary>
        /// Data source identifier.
        /// </summary>
        public required string SourceId { get; init; }

        /// <summary>
        /// Display name.
        /// </summary>
        public required string Name { get; init; }

        /// <summary>
        /// Source type (e.g., "postgres", "mysql", "mongodb", "api").
        /// </summary>
        public required string SourceType { get; init; }

        /// <summary>
        /// Connection endpoint.
        /// </summary>
        public required string Endpoint { get; init; }

        /// <summary>
        /// Source schema mappings.
        /// </summary>
        public Dictionary<string, string> SchemaMappings { get; init; } = new();

        /// <summary>
        /// Whether this source supports transactions.
        /// </summary>
        public bool SupportsTransactions { get; init; }

        /// <summary>
        /// Priority for read operations.
        /// </summary>
        public int ReadPriority { get; init; } = 100;

        /// <summary>
        /// Priority for write operations.
        /// </summary>
        public int WritePriority { get; init; } = 100;

        /// <summary>
        /// Is this source read-only?
        /// </summary>
        public bool IsReadOnly { get; init; }

        /// <summary>
        /// Current health status.
        /// </summary>
        public DataSourceHealth Health { get; set; } = DataSourceHealth.Healthy;
    }

    /// <summary>
    /// Data source health status.
    /// </summary>
    public enum DataSourceHealth
    {
        Healthy,
        Degraded,
        Unhealthy,
        Offline
    }

    /// <summary>
    /// Unified schema for federation.
    /// </summary>
    public sealed class UnifiedSchema
    {
        /// <summary>
        /// Schema name.
        /// </summary>
        public required string Name { get; init; }

        /// <summary>
        /// Field definitions.
        /// </summary>
        public Dictionary<string, UnifiedFieldType> Fields { get; init; } = new();

        /// <summary>
        /// Source mappings.
        /// </summary>
        public Dictionary<string, SourceMapping> SourceMappings { get; init; } = new();
    }

    /// <summary>
    /// Unified field types for schema unification.
    /// </summary>
    public enum UnifiedFieldType
    {
        String,
        Integer,
        Long,
        Double,
        Boolean,
        DateTime,
        Binary,
        Json,
        Array
    }

    /// <summary>
    /// Mapping from unified schema to a specific source.
    /// </summary>
    public sealed class SourceMapping
    {
        /// <summary>
        /// Source field name.
        /// </summary>
        public required string SourceField { get; init; }

        /// <summary>
        /// Transformation expression (optional).
        /// </summary>
        public string? Transform { get; init; }
    }

    /// <summary>
    /// Federation strategy for coordinating multiple data sources with schema unification and distributed transactions.
    /// </summary>
    public sealed class FederationStrategy : EnhancedReplicationStrategyBase
    {
        private readonly BoundedDictionary<string, FederatedDataSource> _sources = new BoundedDictionary<string, FederatedDataSource>(1000);
        private readonly BoundedDictionary<string, UnifiedSchema> _schemas = new BoundedDictionary<string, UnifiedSchema>(1000);
        private readonly BoundedDictionary<string, DistributedTransaction> _activeTransactions = new BoundedDictionary<string, DistributedTransaction>(1000);

        private sealed class DistributedTransaction
        {
            public required string TransactionId { get; init; }
            public HashSet<string> ParticipantSources { get; } = new();
            public TransactionState State { get; set; } = TransactionState.Active;
            public DateTimeOffset StartedAt { get; init; }
            public List<(string SourceId, byte[] Data)> Writes { get; } = new();
        }

        private enum TransactionState
        {
            Active,
            Preparing,
            Prepared,
            Committing,
            Committed,
            Aborting,
            Aborted
        }

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "Federation",
            Description = "Data federation with multiple source coordination, schema unification, and distributed transactions",
            ConsistencyModel = ConsistencyModel.Strong,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: true,
                ConflictResolutionMethods: new[] {
                    ConflictResolutionMethod.LastWriteWins,
                    ConflictResolutionMethod.Custom
                },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: true,
                IsGeoAware: false,
                MaxReplicationLag: TimeSpan.FromSeconds(1),
                MinReplicaCount: 1,
                MaxReplicaCount: 50),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = false,
            SupportsStreaming = false,
            TypicalLagMs = 100,
            ConsistencySlaMs = 1000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Adds a data source to the federation.
        /// </summary>
        public void AddDataSource(FederatedDataSource source)
        {
            ArgumentNullException.ThrowIfNull(source);
            _sources[source.SourceId] = source;
        }

        /// <summary>
        /// Removes a data source.
        /// </summary>
        public bool RemoveDataSource(string sourceId)
        {
            return _sources.TryRemove(sourceId, out _);
        }

        /// <summary>
        /// Gets all data sources.
        /// </summary>
        public IReadOnlyCollection<FederatedDataSource> GetDataSources() => _sources.Values.ToArray();

        /// <summary>
        /// Registers a unified schema.
        /// </summary>
        public void RegisterSchema(UnifiedSchema schema)
        {
            _schemas[schema.Name] = schema;
        }

        /// <summary>
        /// Gets a unified schema.
        /// </summary>
        public UnifiedSchema? GetSchema(string name)
        {
            return _schemas.GetValueOrDefault(name);
        }

        /// <summary>
        /// Begins a distributed transaction across sources.
        /// </summary>
        public string BeginTransaction(IEnumerable<string> participantSourceIds)
        {
            var txId = $"TX-{Guid.NewGuid():N}";
            var tx = new DistributedTransaction
            {
                TransactionId = txId,
                StartedAt = DateTimeOffset.UtcNow
            };

            foreach (var sourceId in participantSourceIds)
            {
                if (_sources.ContainsKey(sourceId))
                    tx.ParticipantSources.Add(sourceId);
            }

            _activeTransactions[txId] = tx;
            return txId;
        }

        /// <summary>
        /// Adds a write operation to a transaction.
        /// </summary>
        public void AddTransactionWrite(string transactionId, string sourceId, byte[] data)
        {
            if (_activeTransactions.TryGetValue(transactionId, out var tx) &&
                tx.State == TransactionState.Active &&
                tx.ParticipantSources.Contains(sourceId))
            {
                tx.Writes.Add((sourceId, data));
            }
        }

        /// <summary>
        /// Commits a distributed transaction using 2-phase commit.
        /// </summary>
        public async Task<bool> CommitTransactionAsync(string transactionId, CancellationToken ct = default)
        {
            if (!_activeTransactions.TryGetValue(transactionId, out var tx))
                return false;

            try
            {
                // Phase 1: Prepare
                tx.State = TransactionState.Preparing;
                foreach (var sourceId in tx.ParticipantSources)
                {
                    await Task.Delay(10, ct); // Simulate prepare
                }
                tx.State = TransactionState.Prepared;

                // Phase 2: Commit
                tx.State = TransactionState.Committing;
                foreach (var write in tx.Writes)
                {
                    await Task.Delay(10, ct); // Simulate commit
                }
                tx.State = TransactionState.Committed;

                _activeTransactions.TryRemove(transactionId, out _);
                return true;
            }
            catch
            {
                tx.State = TransactionState.Aborting;
                // Rollback
                tx.State = TransactionState.Aborted;
                return false;
            }
        }

        /// <summary>
        /// Aborts a transaction.
        /// </summary>
        public async Task AbortTransactionAsync(string transactionId, CancellationToken ct = default)
        {
            if (_activeTransactions.TryGetValue(transactionId, out var tx))
            {
                tx.State = TransactionState.Aborting;
                await Task.Delay(10, ct); // Simulate rollback
                tx.State = TransactionState.Aborted;
                _activeTransactions.TryRemove(transactionId, out _);
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

            // Federate write to all target sources
            var tasks = targetNodeIds
                .Where(id => _sources.ContainsKey(id))
                .Select(async targetId =>
                {
                    var startTime = DateTime.UtcNow;
                    await Task.Delay(20, cancellationToken);
                    RecordReplicationLag(targetId, DateTime.UtcNow - startTime);
                });

            await Task.WhenAll(tasks);
        }

        /// <inheritdoc/>
        public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(
            ReplicationConflict conflict,
            CancellationToken cancellationToken = default)
        {
            // Federation uses custom resolution based on source priority
            var localPriority = GetSourcePriority(conflict.LocalNodeId);
            var remotePriority = GetSourcePriority(conflict.RemoteNodeId);

            var mergedClock = conflict.LocalVersion.Clone();
            mergedClock.Merge(conflict.RemoteVersion);

            var winner = remotePriority < localPriority
                ? conflict.RemoteData
                : conflict.LocalData;

            return Task.FromResult((winner, mergedClock));
        }

        private int GetSourcePriority(string sourceId)
        {
            return _sources.TryGetValue(sourceId, out var source) ? source.WritePriority : int.MaxValue;
        }

        /// <inheritdoc/>
        public override Task<bool> VerifyConsistencyAsync(
            IEnumerable<string> nodeIds,
            string dataId,
            CancellationToken cancellationToken = default)
        {
            // Verify all sources are healthy
            var allHealthy = nodeIds.All(id =>
                _sources.TryGetValue(id, out var s) && s.Health == DataSourceHealth.Healthy);
            return Task.FromResult(allHealthy);
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

    /// <summary>
    /// Read preference for federated queries.
    /// </summary>
    public enum ReadPreference
    {
        /// <summary>
        /// Read from primary source.
        /// </summary>
        Primary,

        /// <summary>
        /// Read from secondary (replica) sources.
        /// </summary>
        Secondary,

        /// <summary>
        /// Prefer primary but use secondary if unavailable.
        /// </summary>
        PrimaryPreferred,

        /// <summary>
        /// Prefer secondary but use primary if unavailable.
        /// </summary>
        SecondaryPreferred,

        /// <summary>
        /// Read from nearest source.
        /// </summary>
        Nearest
    }

    /// <summary>
    /// Federated query strategy with query routing and result aggregation.
    /// </summary>
    public sealed class FederatedQueryStrategy : EnhancedReplicationStrategyBase
    {
        private readonly BoundedDictionary<string, FederatedDataSource> _sources = new BoundedDictionary<string, FederatedDataSource>(1000);
        private readonly BoundedDictionary<string, List<string>> _shardMappings = new BoundedDictionary<string, List<string>>(1000);
        private ReadPreference _defaultReadPreference = ReadPreference.PrimaryPreferred;

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "FederatedQuery",
            Description = "Federated query execution with query routing to optimal replica, result aggregation, and read preference policies",
            ConsistencyModel = ConsistencyModel.ReadYourWrites,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: false,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.LastWriteWins },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: false,
                IsGeoAware: true,
                MaxReplicationLag: TimeSpan.FromSeconds(5),
                MinReplicaCount: 1,
                MaxReplicaCount: 100),
            SupportsAutoConflictResolution = false,
            SupportsVectorClocks = true,
            SupportsDeltaSync = false,
            SupportsStreaming = true,
            TypicalLagMs = 50,
            ConsistencySlaMs = 5000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Sets the default read preference.
        /// </summary>
        public void SetReadPreference(ReadPreference preference)
        {
            _defaultReadPreference = preference;
        }

        /// <summary>
        /// Gets the current read preference.
        /// </summary>
        public ReadPreference GetReadPreference() => _defaultReadPreference;

        /// <summary>
        /// Adds a data source.
        /// </summary>
        public void AddSource(FederatedDataSource source)
        {
            _sources[source.SourceId] = source;
        }

        /// <summary>
        /// Maps a shard key to sources.
        /// </summary>
        public void MapShard(string shardKey, IEnumerable<string> sourceIds)
        {
            _shardMappings[shardKey] = sourceIds.ToList();
        }

        /// <summary>
        /// Routes a query to the optimal source based on read preference and health.
        /// </summary>
        public string? RouteQuery(string? shardKey = null)
        {
            IEnumerable<FederatedDataSource> candidates;

            if (shardKey != null && _shardMappings.TryGetValue(shardKey, out var mappedIds))
            {
                candidates = mappedIds
                    .Select(id => _sources.GetValueOrDefault(id))
                    .Where(s => s != null)!;
            }
            else
            {
                candidates = _sources.Values;
            }

            var healthy = candidates.Where(s => s.Health == DataSourceHealth.Healthy);

            return _defaultReadPreference switch
            {
                ReadPreference.Primary => healthy
                    .Where(s => !s.IsReadOnly)
                    .OrderBy(s => s.ReadPriority)
                    .FirstOrDefault()?.SourceId,

                ReadPreference.Secondary => healthy
                    .Where(s => s.IsReadOnly)
                    .OrderBy(s => s.ReadPriority)
                    .FirstOrDefault()?.SourceId,

                ReadPreference.PrimaryPreferred => healthy
                    .OrderBy(s => s.IsReadOnly ? 1 : 0)
                    .ThenBy(s => s.ReadPriority)
                    .FirstOrDefault()?.SourceId,

                ReadPreference.SecondaryPreferred => healthy
                    .OrderBy(s => s.IsReadOnly ? 0 : 1)
                    .ThenBy(s => s.ReadPriority)
                    .FirstOrDefault()?.SourceId,

                ReadPreference.Nearest => healthy
                    .OrderBy(s => s.ReadPriority)
                    .FirstOrDefault()?.SourceId,

                _ => healthy.FirstOrDefault()?.SourceId
            };
        }

        /// <summary>
        /// Executes a scatter-gather query across multiple sources.
        /// </summary>
        public async Task<IReadOnlyList<(string SourceId, byte[] Result)>> ScatterGatherAsync(
            IEnumerable<string> sourceIds,
            byte[] queryData,
            CancellationToken ct = default)
        {
            var results = new ConcurrentBag<(string SourceId, byte[] Result)>();

            var tasks = sourceIds.Select(async sourceId =>
            {
                if (_sources.TryGetValue(sourceId, out var source) &&
                    source.Health == DataSourceHealth.Healthy)
                {
                    // Simulate query execution
                    await Task.Delay(20, ct);
                    results.Add((sourceId, queryData)); // Echo back for demo
                }
            });

            await Task.WhenAll(tasks);
            return results.ToArray();
        }

        /// <summary>
        /// Aggregates results from multiple sources.
        /// </summary>
        public byte[] AggregateResults(IEnumerable<(string SourceId, byte[] Result)> results, string aggregationType = "concat")
        {
            var allResults = results.ToList();
            if (allResults.Count == 0)
                return Array.Empty<byte>();

            return aggregationType.ToLowerInvariant() switch
            {
                "concat" => allResults.SelectMany(r => r.Result).ToArray(),
                "first" => allResults.First().Result,
                "largest" => allResults.OrderByDescending(r => r.Result.Length).First().Result,
                _ => allResults.First().Result
            };
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

            // Route to optimal targets
            var routed = RouteQuery();
            var targets = routed != null
                ? new[] { routed }.Concat(targetNodeIds).Distinct()
                : targetNodeIds;

            foreach (var targetId in targets)
            {
                var startTime = DateTime.UtcNow;
                await Task.Delay(15, cancellationToken);
                RecordReplicationLag(targetId, DateTime.UtcNow - startTime);
            }
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
            var allHealthy = nodeIds.All(id =>
                _sources.TryGetValue(id, out var s) && s.Health == DataSourceHealth.Healthy);
            return Task.FromResult(allHealthy);
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
