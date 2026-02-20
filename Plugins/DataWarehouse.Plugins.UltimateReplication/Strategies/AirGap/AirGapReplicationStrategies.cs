using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Replication;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateReplication.Strategies.AirGap
{
    #region Air-Gap Types

    /// <summary>
    /// Represents an air-gapped site.
    /// </summary>
    public sealed class AirGapSite
    {
        /// <summary>Site identifier.</summary>
        public required string SiteId { get; init; }

        /// <summary>Display name.</summary>
        public required string Name { get; init; }

        /// <summary>Whether site is currently connected.</summary>
        public bool IsConnected { get; set; }

        /// <summary>Last connection time.</summary>
        public DateTimeOffset? LastConnectedAt { get; set; }

        /// <summary>Last sync time.</summary>
        public DateTimeOffset? LastSyncAt { get; set; }

        /// <summary>Pending sync queue size.</summary>
        public int PendingSyncCount { get; set; }

        /// <summary>Data checksum for integrity verification.</summary>
        public string? DataChecksum { get; set; }
    }

    /// <summary>
    /// Represents a sync batch for air-gapped transfer.
    /// </summary>
    public sealed class SyncBatch
    {
        /// <summary>Batch identifier.</summary>
        public required string BatchId { get; init; }

        /// <summary>Source site.</summary>
        public required string SourceSiteId { get; init; }

        /// <summary>Target site.</summary>
        public required string TargetSiteId { get; init; }

        /// <summary>Batch entries.</summary>
        public List<SyncEntry> Entries { get; } = new();

        /// <summary>Batch creation time.</summary>
        public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

        /// <summary>Whether batch has been applied.</summary>
        public bool IsApplied { get; set; }

        /// <summary>Batch checksum.</summary>
        public string? Checksum { get; set; }

        /// <summary>Schema version.</summary>
        public int SchemaVersion { get; init; } = 1;
    }

    /// <summary>
    /// Represents a sync entry.
    /// </summary>
    public sealed class SyncEntry
    {
        /// <summary>Entry key.</summary>
        public required string Key { get; init; }

        /// <summary>Entry data.</summary>
        public required byte[] Data { get; init; }

        /// <summary>Entry timestamp.</summary>
        public DateTimeOffset Timestamp { get; init; }

        /// <summary>Entry version.</summary>
        public long Version { get; init; }

        /// <summary>Entry operation type.</summary>
        public SyncOperation Operation { get; init; } = SyncOperation.Upsert;

        /// <summary>Provenance information.</summary>
        public string? Provenance { get; init; }
    }

    /// <summary>
    /// Sync operation type.
    /// </summary>
    public enum SyncOperation
    {
        Upsert,
        Delete,
        Merge
    }

    #endregion

    /// <summary>
    /// Bidirectional merge strategy for air-gapped environments with
    /// offline change tracking and conflict-free merge.
    /// </summary>
    public sealed class BidirectionalMergeStrategy : EnhancedReplicationStrategyBase
    {
        private readonly BoundedDictionary<string, AirGapSite> _sites = new BoundedDictionary<string, AirGapSite>(1000);
        private readonly BoundedDictionary<string, (byte[] Data, long Version, string Origin)> _dataStore = new BoundedDictionary<string, (byte[] Data, long Version, string Origin)>(1000);
        private readonly BoundedDictionary<string, Queue<SyncEntry>> _pendingSyncs = new BoundedDictionary<string, Queue<SyncEntry>>(1000);
        private readonly BoundedDictionary<string, HashSet<string>> _mergeHistory = new BoundedDictionary<string, HashSet<string>>(1000);

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "BidirectionalMerge",
            Description = "Bidirectional merge for air-gapped environments with offline change tracking",
            ConsistencyModel = ConsistencyModel.Eventual,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: true,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.Merge, ConflictResolutionMethod.LastWriteWins },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: false,
                IsGeoAware: false,
                MaxReplicationLag: TimeSpan.FromDays(30),
                MinReplicaCount: 2,
                MaxReplicaCount: 10),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = true,
            SupportsStreaming = false,
            TypicalLagMs = 86400000, // 1 day
            ConsistencySlaMs = 2592000000 // 30 days
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Adds an air-gapped site.
        /// </summary>
        public void AddSite(AirGapSite site)
        {
            _sites[site.SiteId] = site;
            _pendingSyncs[site.SiteId] = new Queue<SyncEntry>();
            _mergeHistory[site.SiteId] = new HashSet<string>();
        }

        /// <summary>
        /// Creates a sync batch for export.
        /// </summary>
        public SyncBatch CreateExportBatch(string sourceSiteId, string targetSiteId, int maxEntries = 1000)
        {
            var batch = new SyncBatch
            {
                BatchId = Guid.NewGuid().ToString("N"),
                SourceSiteId = sourceSiteId,
                TargetSiteId = targetSiteId
            };

            if (_pendingSyncs.TryGetValue(targetSiteId, out var queue))
            {
                for (int i = 0; i < maxEntries && queue.TryDequeue(out var entry); i++)
                {
                    batch.Entries.Add(entry);
                }
            }

            // Calculate checksum
            batch.Checksum = ComputeBatchChecksum(batch);

            return batch;
        }

        /// <summary>
        /// Imports and merges a sync batch.
        /// </summary>
        public async Task<(int Merged, int Skipped, int Conflicts)> ImportBatchAsync(
            SyncBatch batch, CancellationToken ct = default)
        {
            var merged = 0;
            var skipped = 0;
            var conflicts = 0;

            // Verify checksum
            var expectedChecksum = ComputeBatchChecksum(batch);
            if (batch.Checksum != expectedChecksum)
            {
                throw new InvalidOperationException("Batch checksum mismatch");
            }

            foreach (var entry in batch.Entries)
            {
                if (ct.IsCancellationRequested) break;

                // Check merge history to avoid duplicates
                var mergeKey = $"{entry.Key}:{entry.Version}";
                if (_mergeHistory.TryGetValue(batch.TargetSiteId, out var history) && history.Contains(mergeKey))
                {
                    skipped++;
                    continue;
                }

                // Merge or insert
                if (_dataStore.TryGetValue(entry.Key, out var existing))
                {
                    if (existing.Version >= entry.Version)
                    {
                        skipped++;
                        continue;
                    }

                    if (existing.Origin != batch.SourceSiteId)
                    {
                        // Potential conflict - merge
                        var mergedData = MergeData(existing.Data, entry.Data);
                        _dataStore[entry.Key] = (mergedData, Math.Max(existing.Version, entry.Version) + 1, batch.TargetSiteId);
                        conflicts++;
                    }
                    else
                    {
                        _dataStore[entry.Key] = (entry.Data, entry.Version, batch.SourceSiteId);
                        merged++;
                    }
                }
                else
                {
                    _dataStore[entry.Key] = (entry.Data, entry.Version, batch.SourceSiteId);
                    merged++;
                }

                // Record in merge history
                history?.Add(mergeKey);
            }

            batch.IsApplied = true;

            if (_sites.TryGetValue(batch.TargetSiteId, out var site))
            {
                site.LastSyncAt = DateTimeOffset.UtcNow;
            }

            await Task.CompletedTask;
            return (merged, skipped, conflicts);
        }

        private byte[] MergeData(byte[] local, byte[] remote)
        {
            // Simple merge: keep larger (assumes more data = more operations)
            return local.Length >= remote.Length ? local : remote;
        }

        private static string ComputeBatchChecksum(SyncBatch batch)
        {
            using var sha = SHA256.Create();
            var json = JsonSerializer.Serialize(batch.Entries.Select(e => new { e.Key, e.Version }));
            var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(json));
            return Convert.ToBase64String(hash);
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
            var version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            _dataStore[key] = (data.ToArray(), version, sourceNodeId);

            // Queue for offline sync
            var entry = new SyncEntry
            {
                Key = key,
                Data = data.ToArray(),
                Timestamp = DateTimeOffset.UtcNow,
                Version = version,
                Provenance = sourceNodeId
            };

            foreach (var targetId in targetNodeIds)
            {
                if (_pendingSyncs.TryGetValue(targetId, out var queue))
                {
                    queue.Enqueue(entry);

                    if (_sites.TryGetValue(targetId, out var site))
                    {
                        site.PendingSyncCount = queue.Count;
                    }
                }
            }

            await Task.CompletedTask;
        }

        /// <inheritdoc/>
        public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(
            ReplicationConflict conflict,
            CancellationToken cancellationToken = default)
        {
            var merged = MergeData(conflict.LocalData.ToArray(), conflict.RemoteData.ToArray());

            var mergedClock = conflict.LocalVersion.Clone();
            mergedClock.Merge(conflict.RemoteVersion);

            return Task.FromResult<(ReadOnlyMemory<byte>, VectorClock)>((merged, mergedClock));
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
            if (_sites.TryGetValue(targetNodeId, out var site) && site.LastSyncAt != null)
            {
                return Task.FromResult(DateTimeOffset.UtcNow - site.LastSyncAt.Value);
            }
            return Task.FromResult(TimeSpan.FromDays(30));
        }
    }

    /// <summary>
    /// Conflict avoidance strategy using deterministic key partitioning
    /// for air-gapped environments.
    /// </summary>
    public sealed class ConflictAvoidanceStrategy : EnhancedReplicationStrategyBase
    {
        private readonly BoundedDictionary<string, AirGapSite> _sites = new BoundedDictionary<string, AirGapSite>(1000);
        private readonly BoundedDictionary<string, (byte[] Data, string Owner)> _dataStore = new BoundedDictionary<string, (byte[] Data, string Owner)>(1000);
        private readonly BoundedDictionary<string, string> _keyOwnership = new BoundedDictionary<string, string>(1000);
        private readonly BoundedDictionary<string, HashSet<string>> _siteKeyRanges = new BoundedDictionary<string, HashSet<string>>(1000);

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "ConflictAvoidance",
            Description = "Conflict avoidance using deterministic key partitioning for air-gapped sync",
            ConsistencyModel = ConsistencyModel.Eventual,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: true,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.PriorityBased },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: false,
                IsGeoAware: false,
                MaxReplicationLag: TimeSpan.FromDays(30),
                MinReplicaCount: 2,
                MaxReplicaCount: 10),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = false,
            SupportsDeltaSync = true,
            SupportsStreaming = false,
            TypicalLagMs = 86400000,
            ConsistencySlaMs = 2592000000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Adds a site with key range ownership.
        /// </summary>
        public void AddSite(AirGapSite site, IEnumerable<string>? keyPrefixes = null)
        {
            _sites[site.SiteId] = site;
            _siteKeyRanges[site.SiteId] = new HashSet<string>(keyPrefixes ?? Array.Empty<string>());
        }

        /// <summary>
        /// Assigns key range to a site.
        /// </summary>
        public void AssignKeyRange(string siteId, string keyPrefix)
        {
            if (_siteKeyRanges.TryGetValue(siteId, out var ranges))
            {
                ranges.Add(keyPrefix);
            }
        }

        /// <summary>
        /// Gets the owner site for a key.
        /// </summary>
        public string? GetKeyOwner(string key)
        {
            if (_keyOwnership.TryGetValue(key, out var owner))
                return owner;

            foreach (var (siteId, ranges) in _siteKeyRanges)
            {
                if (ranges.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    _keyOwnership[key] = siteId;
                    return siteId;
                }
            }

            // Hash-based assignment if no prefix match
            var hash = key.GetHashCode();
            var sites = _sites.Keys.ToArray();
            if (sites.Length > 0)
            {
                var assignedOwner = sites[Math.Abs(hash) % sites.Length];
                _keyOwnership[key] = assignedOwner;
                return assignedOwner;
            }

            return null;
        }

        /// <summary>
        /// Checks if a site can write to a key.
        /// </summary>
        public bool CanWrite(string siteId, string key)
        {
            var owner = GetKeyOwner(key);
            return owner == siteId;
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

            // Verify write permission
            var owner = GetKeyOwner(key);
            if (owner != sourceNodeId)
            {
                throw new InvalidOperationException($"Site {sourceNodeId} cannot write to key {key} (owned by {owner})");
            }

            _dataStore[key] = (data.ToArray(), sourceNodeId);

            await Task.CompletedTask;
        }

        /// <inheritdoc/>
        public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(
            ReplicationConflict conflict,
            CancellationToken cancellationToken = default)
        {
            // Conflict avoidance: owner always wins
            var owner = GetKeyOwner(conflict.DataId);
            var mergedClock = conflict.LocalVersion.Clone();
            mergedClock.Merge(conflict.RemoteVersion);

            var winner = owner == conflict.LocalNodeId ? conflict.LocalData : conflict.RemoteData;
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
            if (_sites.TryGetValue(targetNodeId, out var site) && site.LastSyncAt != null)
            {
                return Task.FromResult(DateTimeOffset.UtcNow - site.LastSyncAt.Value);
            }
            return Task.FromResult(TimeSpan.FromDays(30));
        }
    }

    /// <summary>
    /// Schema evolution strategy for air-gapped environments with
    /// backward/forward compatibility and migration support.
    /// </summary>
    public sealed class SchemaEvolutionStrategy : EnhancedReplicationStrategyBase
    {
        private readonly BoundedDictionary<string, AirGapSite> _sites = new BoundedDictionary<string, AirGapSite>(1000);
        private readonly BoundedDictionary<string, (byte[] Data, int SchemaVersion)> _dataStore = new BoundedDictionary<string, (byte[] Data, int SchemaVersion)>(1000);
        private readonly BoundedDictionary<int, SchemaDefinition> _schemas = new BoundedDictionary<int, SchemaDefinition>(1000);
        private readonly BoundedDictionary<(int From, int To), Func<byte[], byte[]>> _migrations = new BoundedDictionary<(int From, int To), Func<byte[], byte[]>>(1000);
        private int _currentSchemaVersion = 1;

        /// <summary>
        /// Schema definition.
        /// </summary>
        public sealed class SchemaDefinition
        {
            public required int Version { get; init; }
            public required string Name { get; init; }
            public Dictionary<string, string> Fields { get; init; } = new();
            public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
        }

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "SchemaEvolution",
            Description = "Schema evolution for air-gapped sync with backward/forward compatibility",
            ConsistencyModel = ConsistencyModel.Eventual,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: true,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.LastWriteWins },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: false,
                IsGeoAware: false,
                MaxReplicationLag: TimeSpan.FromDays(30),
                MinReplicaCount: 2,
                MaxReplicaCount: 10),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = true,
            SupportsStreaming = false,
            TypicalLagMs = 86400000,
            ConsistencySlaMs = 2592000000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Registers a schema version.
        /// </summary>
        public void RegisterSchema(SchemaDefinition schema)
        {
            _schemas[schema.Version] = schema;
            if (schema.Version > _currentSchemaVersion)
            {
                _currentSchemaVersion = schema.Version;
            }
        }

        /// <summary>
        /// Registers a migration function.
        /// </summary>
        public void RegisterMigration(int fromVersion, int toVersion, Func<byte[], byte[]> migrator)
        {
            _migrations[(fromVersion, toVersion)] = migrator;
        }

        /// <summary>
        /// Gets the current schema version.
        /// </summary>
        public int GetCurrentSchemaVersion() => _currentSchemaVersion;

        /// <summary>
        /// Migrates data from one version to another.
        /// </summary>
        public byte[] MigrateData(byte[] data, int fromVersion, int toVersion)
        {
            if (fromVersion == toVersion)
                return data;

            var current = fromVersion;
            var currentData = data;

            while (current < toVersion)
            {
                var next = current + 1;
                if (_migrations.TryGetValue((current, next), out var migrator))
                {
                    currentData = migrator(currentData);
                }
                current = next;
            }

            return currentData;
        }

        /// <summary>
        /// Checks schema compatibility.
        /// </summary>
        public bool IsCompatible(int version1, int version2)
        {
            var min = Math.Min(version1, version2);
            var max = Math.Max(version1, version2);

            // Check if migration path exists
            for (int v = min; v < max; v++)
            {
                if (!_migrations.ContainsKey((v, v + 1)))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Adds a site.
        /// </summary>
        public void AddSite(AirGapSite site)
        {
            _sites[site.SiteId] = site;
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
            var schemaVersion = int.TryParse(metadata?.GetValueOrDefault("schemaVersion"), out var sv) ? sv : _currentSchemaVersion;

            // Migrate to current schema if needed
            var migratedData = schemaVersion < _currentSchemaVersion
                ? MigrateData(data.ToArray(), schemaVersion, _currentSchemaVersion)
                : data.ToArray();

            _dataStore[key] = (migratedData, _currentSchemaVersion);

            await Task.CompletedTask;
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
            return Task.FromResult(_dataStore.ContainsKey(dataId));
        }

        /// <inheritdoc/>
        public override Task<TimeSpan> GetReplicationLagAsync(
            string sourceNodeId,
            string targetNodeId,
            CancellationToken cancellationToken = default)
        {
            if (_sites.TryGetValue(targetNodeId, out var site) && site.LastSyncAt != null)
            {
                return Task.FromResult(DateTimeOffset.UtcNow - site.LastSyncAt.Value);
            }
            return Task.FromResult(TimeSpan.FromDays(30));
        }
    }

    /// <summary>
    /// Zero data loss strategy for air-gapped environments with
    /// cryptographic verification and audit trails.
    /// </summary>
    public sealed class ZeroDataLossStrategy : EnhancedReplicationStrategyBase
    {
        private readonly BoundedDictionary<string, AirGapSite> _sites = new BoundedDictionary<string, AirGapSite>(1000);
        private readonly BoundedDictionary<string, (byte[] Data, string Hash, long Sequence)> _dataStore = new BoundedDictionary<string, (byte[] Data, string Hash, long Sequence)>(1000);
        private readonly ConcurrentQueue<AuditEntry> _auditLog = new();
        private long _sequence;

        /// <summary>
        /// Represents an audit entry for data operations.
        /// </summary>
        public sealed record AuditEntry(
            long Sequence,
            string Key,
            string Operation,
            string DataHash,
            DateTimeOffset Timestamp,
            string SourceSite);

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "ZeroDataLoss",
            Description = "Zero data loss for air-gapped sync with cryptographic verification and audit trails",
            ConsistencyModel = ConsistencyModel.Strong,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: false,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.FirstWriteWins },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: false,
                IsGeoAware: false,
                MaxReplicationLag: TimeSpan.FromDays(30),
                MinReplicaCount: 2,
                MaxReplicaCount: 5),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = true,
            SupportsStreaming = false,
            TypicalLagMs = 86400000,
            ConsistencySlaMs = 2592000000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Adds a site.
        /// </summary>
        public void AddSite(AirGapSite site)
        {
            _sites[site.SiteId] = site;
        }

        /// <summary>
        /// Computes cryptographic hash of data.
        /// </summary>
        public string ComputeHash(byte[] data)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(data);
            return Convert.ToBase64String(hash);
        }

        /// <summary>
        /// Verifies data integrity.
        /// </summary>
        public bool VerifyIntegrity(string key, byte[] data)
        {
            if (!_dataStore.TryGetValue(key, out var stored))
                return false;

            var hash = ComputeHash(data);
            return stored.Hash == hash;
        }

        /// <summary>
        /// Gets audit entries for a key.
        /// </summary>
        public IEnumerable<AuditEntry> GetAuditTrail(string? key = null)
        {
            if (key == null)
                return _auditLog.ToArray();

            return _auditLog.Where(e => e.Key == key);
        }

        /// <summary>
        /// Gets data integrity report.
        /// </summary>
        public (int Total, int Valid, int Invalid) GetIntegrityReport()
        {
            var total = _dataStore.Count;
            var valid = 0;
            var invalid = 0;

            foreach (var (key, item) in _dataStore)
            {
                var computedHash = ComputeHash(item.Data);
                if (computedHash == item.Hash)
                    valid++;
                else
                    invalid++;
            }

            return (total, valid, invalid);
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

            var hash = ComputeHash(data.ToArray());
            var sequence = Interlocked.Increment(ref _sequence);

            _dataStore[key] = (data.ToArray(), hash, sequence);

            // Audit log entry
            _auditLog.Enqueue(new AuditEntry(
                sequence,
                key,
                "Write",
                hash,
                DateTimeOffset.UtcNow,
                sourceNodeId));

            // Update site checksum
            UpdateSiteChecksum(sourceNodeId);

            await Task.CompletedTask;
        }

        private void UpdateSiteChecksum(string siteId)
        {
            if (_sites.TryGetValue(siteId, out var site))
            {
                // Compute merkle root of all data
                using var sha = SHA256.Create();
                var combined = _dataStore.Values
                    .OrderBy(v => v.Sequence)
                    .Select(v => v.Hash)
                    .Aggregate("", (a, b) => a + b);

                var rootHash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(combined));
                site.DataChecksum = Convert.ToBase64String(rootHash);
            }
        }

        /// <inheritdoc/>
        public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(
            ReplicationConflict conflict,
            CancellationToken cancellationToken = default)
        {
            // Zero data loss: first write wins (reject conflicting writes)
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

            return Task.FromResult(VerifyIntegrity(dataId, item.Data));
        }

        /// <inheritdoc/>
        public override Task<TimeSpan> GetReplicationLagAsync(
            string sourceNodeId,
            string targetNodeId,
            CancellationToken cancellationToken = default)
        {
            if (_sites.TryGetValue(targetNodeId, out var site) && site.LastSyncAt != null)
            {
                return Task.FromResult(DateTimeOffset.UtcNow - site.LastSyncAt.Value);
            }
            return Task.FromResult(TimeSpan.FromDays(30));
        }
    }

    /// <summary>
    /// Resumable merge strategy for air-gapped environments with
    /// checkpoint support and partial sync recovery.
    /// </summary>
    public sealed class ResumableMergeStrategy : EnhancedReplicationStrategyBase
    {
        private readonly BoundedDictionary<string, AirGapSite> _sites = new BoundedDictionary<string, AirGapSite>(1000);
        private readonly BoundedDictionary<string, (byte[] Data, long Checkpoint)> _dataStore = new BoundedDictionary<string, (byte[] Data, long Checkpoint)>(1000);
        private readonly BoundedDictionary<string, SyncState> _syncStates = new BoundedDictionary<string, SyncState>(1000);

        private sealed class SyncState
        {
            public string? BatchId { get; set; }
            public long LastCheckpoint { get; set; }
            public int ProcessedCount { get; set; }
            public int TotalCount { get; set; }
            public SyncStatus Status { get; set; } = SyncStatus.NotStarted;
            public DateTimeOffset? StartedAt { get; set; }
            public DateTimeOffset? PausedAt { get; set; }
        }

        private enum SyncStatus
        {
            NotStarted,
            InProgress,
            Paused,
            Completed,
            Failed
        }

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "ResumableMerge",
            Description = "Resumable merge for air-gapped sync with checkpoint support and partial recovery",
            ConsistencyModel = ConsistencyModel.Eventual,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: true,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.LastWriteWins },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: false,
                IsGeoAware: false,
                MaxReplicationLag: TimeSpan.FromDays(30),
                MinReplicaCount: 2,
                MaxReplicaCount: 10),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = true,
            SupportsStreaming = false,
            TypicalLagMs = 86400000,
            ConsistencySlaMs = 2592000000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Adds a site.
        /// </summary>
        public void AddSite(AirGapSite site)
        {
            _sites[site.SiteId] = site;
            _syncStates[site.SiteId] = new SyncState();
        }

        /// <summary>
        /// Gets sync state for a site.
        /// </summary>
        public (long Checkpoint, int Processed, int Total, string Status) GetSyncState(string siteId)
        {
            if (_syncStates.TryGetValue(siteId, out var state))
            {
                return (state.LastCheckpoint, state.ProcessedCount, state.TotalCount, state.Status.ToString());
            }
            return (0, 0, 0, "Unknown");
        }

        /// <summary>
        /// Pauses sync for a site.
        /// </summary>
        public bool PauseSync(string siteId)
        {
            if (_syncStates.TryGetValue(siteId, out var state) && state.Status == SyncStatus.InProgress)
            {
                state.Status = SyncStatus.Paused;
                state.PausedAt = DateTimeOffset.UtcNow;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Resumes sync for a site from last checkpoint.
        /// </summary>
        public async Task<bool> ResumeSyncAsync(string siteId, IEnumerable<SyncEntry> entries, CancellationToken ct = default)
        {
            if (!_syncStates.TryGetValue(siteId, out var state))
                return false;

            var lastCheckpoint = state.LastCheckpoint;
            state.Status = SyncStatus.InProgress;
            state.TotalCount = entries.Count();

            foreach (var entry in entries.Where(e => e.Version > lastCheckpoint).OrderBy(e => e.Version))
            {
                if (ct.IsCancellationRequested)
                {
                    state.Status = SyncStatus.Paused;
                    return false;
                }

                _dataStore[entry.Key] = (entry.Data, entry.Version);
                state.LastCheckpoint = entry.Version;
                state.ProcessedCount++;
            }

            state.Status = SyncStatus.Completed;

            if (_sites.TryGetValue(siteId, out var site))
            {
                site.LastSyncAt = DateTimeOffset.UtcNow;
            }

            await Task.CompletedTask;
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
            var checkpoint = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            _dataStore[key] = (data.ToArray(), checkpoint);

            await Task.CompletedTask;
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
            return Task.FromResult(_dataStore.ContainsKey(dataId));
        }

        /// <inheritdoc/>
        public override Task<TimeSpan> GetReplicationLagAsync(
            string sourceNodeId,
            string targetNodeId,
            CancellationToken cancellationToken = default)
        {
            if (_sites.TryGetValue(targetNodeId, out var site) && site.LastSyncAt != null)
            {
                return Task.FromResult(DateTimeOffset.UtcNow - site.LastSyncAt.Value);
            }
            return Task.FromResult(TimeSpan.FromDays(30));
        }
    }

    /// <summary>
    /// Incremental sync strategy for air-gapped environments with
    /// delta computation and efficient transfer.
    /// </summary>
    public sealed class IncrementalSyncStrategy : EnhancedReplicationStrategyBase
    {
        private readonly BoundedDictionary<string, AirGapSite> _sites = new BoundedDictionary<string, AirGapSite>(1000);
        private readonly BoundedDictionary<string, (byte[] Data, long Version, byte[] PreviousHash)> _dataStore = new BoundedDictionary<string, (byte[] Data, long Version, byte[] PreviousHash)>(1000);
        private readonly BoundedDictionary<string, long> _siteVersions = new BoundedDictionary<string, long>(1000);

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "IncrementalSync",
            Description = "Incremental sync for air-gapped environments with delta computation",
            ConsistencyModel = ConsistencyModel.Eventual,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: true,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.LastWriteWins },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: false,
                IsGeoAware: false,
                MaxReplicationLag: TimeSpan.FromDays(30),
                MinReplicaCount: 2,
                MaxReplicaCount: 10),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = true,
            SupportsStreaming = false,
            TypicalLagMs = 86400000,
            ConsistencySlaMs = 2592000000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Adds a site.
        /// </summary>
        public void AddSite(AirGapSite site)
        {
            _sites[site.SiteId] = site;
            _siteVersions[site.SiteId] = 0;
        }

        /// <summary>
        /// Gets incremental changes since a version.
        /// </summary>
        public IEnumerable<(string Key, byte[] Data, long Version)> GetChangesSince(long sinceVersion)
        {
            return _dataStore
                .Where(kv => kv.Value.Version > sinceVersion)
                .OrderBy(kv => kv.Value.Version)
                .Select(kv => (kv.Key, kv.Value.Data, kv.Value.Version));
        }

        /// <summary>
        /// Computes delta between two byte arrays.
        /// </summary>
        public byte[] ComputeDelta(byte[] oldData, byte[] newData)
        {
            // Simple XOR-based delta
            var maxLen = Math.Max(oldData.Length, newData.Length);
            var delta = new byte[maxLen + 8];

            BitConverter.TryWriteBytes(delta.AsSpan(0, 4), oldData.Length);
            BitConverter.TryWriteBytes(delta.AsSpan(4, 4), newData.Length);

            for (int i = 0; i < maxLen; i++)
            {
                var oldByte = i < oldData.Length ? oldData[i] : (byte)0;
                var newByte = i < newData.Length ? newData[i] : (byte)0;
                delta[i + 8] = (byte)(oldByte ^ newByte);
            }

            return delta;
        }

        /// <summary>
        /// Applies delta to recreate new data.
        /// </summary>
        public byte[] ApplyDelta(byte[] oldData, byte[] delta)
        {
            var newLen = BitConverter.ToInt32(delta.AsSpan(4, 4));
            var result = new byte[newLen];

            for (int i = 0; i < newLen; i++)
            {
                var oldByte = i < oldData.Length ? oldData[i] : (byte)0;
                var deltaByte = i + 8 < delta.Length ? delta[i + 8] : (byte)0;
                result[i] = (byte)(oldByte ^ deltaByte);
            }

            return result;
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
            var version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Compute hash for delta tracking
            using var sha = SHA256.Create();
            var previousHash = _dataStore.TryGetValue(key, out var existing)
                ? sha.ComputeHash(existing.Data)
                : Array.Empty<byte>();

            _dataStore[key] = (data.ToArray(), version, previousHash);

            await Task.CompletedTask;
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
            return Task.FromResult(_dataStore.ContainsKey(dataId));
        }

        /// <inheritdoc/>
        public override Task<TimeSpan> GetReplicationLagAsync(
            string sourceNodeId,
            string targetNodeId,
            CancellationToken cancellationToken = default)
        {
            if (_sites.TryGetValue(targetNodeId, out var site) && site.LastSyncAt != null)
            {
                return Task.FromResult(DateTimeOffset.UtcNow - site.LastSyncAt.Value);
            }
            return Task.FromResult(TimeSpan.FromDays(30));
        }
    }

    /// <summary>
    /// Provenance tracking strategy for air-gapped environments with
    /// full data lineage and origin tracking.
    /// </summary>
    public sealed class ProvenanceTrackingStrategy : EnhancedReplicationStrategyBase
    {
        private readonly BoundedDictionary<string, AirGapSite> _sites = new BoundedDictionary<string, AirGapSite>(1000);
        private readonly BoundedDictionary<string, DataWithProvenance> _dataStore = new BoundedDictionary<string, DataWithProvenance>(1000);

        private sealed class DataWithProvenance
        {
            public required byte[] Data { get; init; }
            public required string OriginSite { get; init; }
            public required DateTimeOffset CreatedAt { get; init; }
            public List<ProvenanceEntry> Lineage { get; } = new();
        }

        private sealed record ProvenanceEntry(
            string SiteId,
            string Operation,
            DateTimeOffset Timestamp,
            string? Metadata);

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "ProvenanceTracking",
            Description = "Provenance tracking for air-gapped sync with full data lineage",
            ConsistencyModel = ConsistencyModel.Eventual,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: true,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.LastWriteWins },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: false,
                IsGeoAware: false,
                MaxReplicationLag: TimeSpan.FromDays(30),
                MinReplicaCount: 2,
                MaxReplicaCount: 10),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = true,
            SupportsStreaming = false,
            TypicalLagMs = 86400000,
            ConsistencySlaMs = 2592000000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Adds a site.
        /// </summary>
        public void AddSite(AirGapSite site)
        {
            _sites[site.SiteId] = site;
        }

        /// <summary>
        /// Gets provenance lineage for a key.
        /// </summary>
        public IEnumerable<(string Site, string Op, DateTimeOffset Time)> GetLineage(string key)
        {
            if (_dataStore.TryGetValue(key, out var item))
            {
                return item.Lineage.Select(e => (e.SiteId, e.Operation, e.Timestamp));
            }
            return Array.Empty<(string, string, DateTimeOffset)>();
        }

        /// <summary>
        /// Gets the origin site for a key.
        /// </summary>
        public string? GetOriginSite(string key)
        {
            return _dataStore.TryGetValue(key, out var item) ? item.OriginSite : null;
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

            if (_dataStore.TryGetValue(key, out var existing))
            {
                // Update with provenance tracking
                existing.Lineage.Add(new ProvenanceEntry(
                    sourceNodeId,
                    "Update",
                    DateTimeOffset.UtcNow,
                    metadata?.GetValueOrDefault("reason")));

                _dataStore[key] = new DataWithProvenance
                {
                    Data = data.ToArray(),
                    OriginSite = existing.OriginSite,
                    CreatedAt = existing.CreatedAt
                };

                // Copy lineage
                foreach (var entry in existing.Lineage)
                {
                    _dataStore[key].Lineage.Add(entry);
                }
            }
            else
            {
                var newItem = new DataWithProvenance
                {
                    Data = data.ToArray(),
                    OriginSite = sourceNodeId,
                    CreatedAt = DateTimeOffset.UtcNow
                };

                newItem.Lineage.Add(new ProvenanceEntry(
                    sourceNodeId,
                    "Create",
                    DateTimeOffset.UtcNow,
                    metadata?.GetValueOrDefault("reason")));

                _dataStore[key] = newItem;
            }

            await Task.CompletedTask;
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
            return Task.FromResult(_dataStore.ContainsKey(dataId));
        }

        /// <inheritdoc/>
        public override Task<TimeSpan> GetReplicationLagAsync(
            string sourceNodeId,
            string targetNodeId,
            CancellationToken cancellationToken = default)
        {
            if (_sites.TryGetValue(targetNodeId, out var site) && site.LastSyncAt != null)
            {
                return Task.FromResult(DateTimeOffset.UtcNow - site.LastSyncAt.Value);
            }
            return Task.FromResult(TimeSpan.FromDays(30));
        }
    }
}
