using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Replication;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateReplication.Strategies.Conflict
{
    #region Conflict Resolution Types

    /// <summary>
    /// Represents a resolved conflict with audit trail.
    /// </summary>
    public sealed class ResolvedConflict
    {
        /// <summary>Original conflict.</summary>
        public required EnhancedReplicationConflict OriginalConflict { get; init; }

        /// <summary>Resolution method used.</summary>
        public required ConflictResolutionMethod Method { get; init; }

        /// <summary>Winning data.</summary>
        public required byte[] ResolvedData { get; init; }

        /// <summary>Winning source.</summary>
        public required string WinningSource { get; init; }

        /// <summary>Resolution timestamp.</summary>
        public DateTimeOffset ResolvedAt { get; init; } = DateTimeOffset.UtcNow;

        /// <summary>Resolution reasoning.</summary>
        public string? Reason { get; init; }
    }

    /// <summary>
    /// CRDT wrapper for conflict-free data.
    /// </summary>
    public interface ICrdtValue
    {
        /// <summary>Merges with another CRDT value.</summary>
        void Merge(ICrdtValue other);

        /// <summary>Serializes to bytes.</summary>
        byte[] ToBytes();
    }

    #endregion

    /// <summary>
    /// Last-Write-Wins conflict resolution strategy with timestamp-based ordering
    /// and tie-breaking via node priority.
    /// </summary>
    public sealed class LastWriteWinsStrategy : EnhancedReplicationStrategyBase
    {
        private readonly BoundedDictionary<string, (byte[] Data, DateTimeOffset Timestamp, string NodeId)> _dataStore = new BoundedDictionary<string, (byte[] Data, DateTimeOffset Timestamp, string NodeId)>(1000);
        private readonly BoundedDictionary<string, int> _nodePriorities = new BoundedDictionary<string, int>(1000);
        private readonly List<ResolvedConflict> _conflictLog = new();
        private readonly object _logLock = new();

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "LastWriteWins",
            Description = "Last-Write-Wins conflict resolution with timestamp ordering and node priority tie-breaking",
            ConsistencyModel = ConsistencyModel.Eventual,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: true,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.LastWriteWins },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: true,
                IsGeoAware: false,
                MaxReplicationLag: TimeSpan.FromSeconds(30),
                MinReplicaCount: 2,
                MaxReplicaCount: 100),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = false,
            SupportsStreaming = true,
            TypicalLagMs = 30,
            ConsistencySlaMs = 30000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Sets node priority (lower = higher priority for tie-breaking).
        /// </summary>
        public void SetNodePriority(string nodeId, int priority)
        {
            _nodePriorities[nodeId] = priority;
        }

        /// <summary>
        /// Gets the conflict resolution log.
        /// </summary>
        public IReadOnlyList<ResolvedConflict> GetConflictLog()
        {
            lock (_logLock)
            {
                return _conflictLog.ToArray();
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
            var timestamp = metadata?.TryGetValue("timestamp", out var ts) == true
                ? DateTimeOffset.Parse(ts)
                : DateTimeOffset.UtcNow;

            // Check for conflict
            if (_dataStore.TryGetValue(key, out var existing))
            {
                if (existing.Timestamp > timestamp)
                {
                    // Existing wins, ignore this write
                    return;
                }
                else if (existing.Timestamp == timestamp)
                {
                    // Tie-break by node priority
                    var existingPriority = _nodePriorities.GetValueOrDefault(existing.NodeId, 100);
                    var newPriority = _nodePriorities.GetValueOrDefault(sourceNodeId, 100);

                    if (existingPriority < newPriority)
                    {
                        return; // Existing wins
                    }
                }
            }

            _dataStore[key] = (data.ToArray(), timestamp, sourceNodeId);

            var tasks = targetNodeIds.Select(async targetId =>
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
            var localTs = conflict.LocalMetadata?.TryGetValue("timestamp", out var lts) == true
                ? DateTimeOffset.Parse(lts)
                : DateTimeOffset.MinValue;
            var remoteTs = conflict.RemoteMetadata?.TryGetValue("timestamp", out var rts) == true
                ? DateTimeOffset.Parse(rts)
                : DateTimeOffset.MinValue;

            var mergedClock = conflict.LocalVersion.Clone();
            mergedClock.Merge(conflict.RemoteVersion);

            ReadOnlyMemory<byte> winner;
            string winnerId;

            if (remoteTs > localTs)
            {
                winner = conflict.RemoteData;
                winnerId = conflict.RemoteNodeId;
            }
            else if (localTs > remoteTs)
            {
                winner = conflict.LocalData;
                winnerId = conflict.LocalNodeId;
            }
            else
            {
                // Tie-break
                var localPriority = _nodePriorities.GetValueOrDefault(conflict.LocalNodeId, 100);
                var remotePriority = _nodePriorities.GetValueOrDefault(conflict.RemoteNodeId, 100);

                if (remotePriority < localPriority)
                {
                    winner = conflict.RemoteData;
                    winnerId = conflict.RemoteNodeId;
                }
                else
                {
                    winner = conflict.LocalData;
                    winnerId = conflict.LocalNodeId;
                }
            }

            // Log resolution
            LogConflictResolution(conflict, winner.ToArray(), winnerId);

            return Task.FromResult((winner, mergedClock));
        }

        private void LogConflictResolution(ReplicationConflict conflict, byte[] resolvedData, string winnerId)
        {
            var enhanced = new EnhancedReplicationConflict
            {
                DataId = conflict.DataId,
                LocalVersion = new EnhancedVectorClock(),
                RemoteVersion = new EnhancedVectorClock(),
                LocalData = conflict.LocalData,
                RemoteData = conflict.RemoteData,
                LocalNodeId = conflict.LocalNodeId,
                RemoteNodeId = conflict.RemoteNodeId,
                DetectedAt = conflict.DetectedAt
            };

            lock (_logLock)
            {
                _conflictLog.Add(new ResolvedConflict
                {
                    OriginalConflict = enhanced,
                    Method = ConflictResolutionMethod.LastWriteWins,
                    ResolvedData = resolvedData,
                    WinningSource = winnerId,
                    Reason = "Timestamp-based resolution with node priority tie-breaking"
                });

                // Keep last 1000 resolutions
                if (_conflictLog.Count > 1000)
                    _conflictLog.RemoveAt(0);
            }
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

    /// <summary>
    /// Vector Clock conflict resolution strategy using Lamport clocks
    /// for causal ordering and concurrent write detection.
    /// </summary>
    public sealed class VectorClockStrategy : EnhancedReplicationStrategyBase
    {
        private readonly BoundedDictionary<string, (byte[] Data, EnhancedVectorClock Clock)> _dataStore = new BoundedDictionary<string, (byte[] Data, EnhancedVectorClock Clock)>(1000);
        private readonly BoundedDictionary<string, List<EnhancedReplicationConflict>> _pendingConflicts = new BoundedDictionary<string, List<EnhancedReplicationConflict>>(1000);

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "VectorClock",
            Description = "Vector Clock conflict resolution with causal ordering and concurrent write detection",
            ConsistencyModel = ConsistencyModel.Causal,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: true,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.LastWriteWins, ConflictResolutionMethod.Manual },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: true,
                IsGeoAware: false,
                MaxReplicationLag: TimeSpan.FromSeconds(30),
                MinReplicaCount: 2,
                MaxReplicaCount: 50),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = false,
            SupportsStreaming = true,
            TypicalLagMs = 40,
            ConsistencySlaMs = 30000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Gets pending conflicts requiring manual resolution.
        /// </summary>
        public IEnumerable<EnhancedReplicationConflict> GetPendingConflicts(string key)
        {
            if (_pendingConflicts.TryGetValue(key, out var conflicts))
            {
                lock (conflicts)
                {
                    return conflicts.ToArray();
                }
            }
            return Array.Empty<EnhancedReplicationConflict>();
        }

        /// <summary>
        /// Manually resolves a conflict.
        /// </summary>
        public void ResolveManually(string key, byte[] resolvedData)
        {
            var clock = VectorClock.Clone();
            clock.Increment(LocalNodeId);
            _dataStore[key] = (resolvedData, clock);

            if (_pendingConflicts.TryGetValue(key, out var conflicts))
            {
                lock (conflicts)
                {
                    conflicts.Clear();
                }
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

            // Check for concurrent writes
            if (_dataStore.TryGetValue(key, out var existing))
            {
                if (existing.Clock.IsConcurrentWith(VectorClock))
                {
                    // Concurrent write detected - queue for resolution
                    var conflict = new EnhancedReplicationConflict
                    {
                        DataId = key,
                        LocalVersion = existing.Clock,
                        RemoteVersion = VectorClock.Clone(),
                        LocalData = existing.Data,
                        RemoteData = data,
                        LocalNodeId = LocalNodeId,
                        RemoteNodeId = sourceNodeId
                    };

                    var conflicts = _pendingConflicts.GetOrAdd(key, _ => new List<EnhancedReplicationConflict>());
                    lock (conflicts)
                    {
                        conflicts.Add(conflict);
                    }

                    // Auto-resolve using LWW
                    var (resolved, mergedClock) = await ResolveConflictEnhancedAsync(conflict, cancellationToken);
                    _dataStore[key] = (resolved.ToArray(), mergedClock);
                }
                else if (VectorClock.HappensBefore(existing.Clock))
                {
                    // Our write is older, ignore
                    return;
                }
            }

            _dataStore[key] = (data.ToArray(), VectorClock.Clone());

            var tasks = targetNodeIds.Select(async targetId =>
            {
                var startTime = DateTime.UtcNow;
                await Task.Delay(25, cancellationToken);
                RecordReplicationLag(targetId, DateTime.UtcNow - startTime);
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

            // Use vector clock comparison for resolution
            if (conflict.RemoteVersion.HappensBefore(conflict.LocalVersion))
            {
                return Task.FromResult((conflict.LocalData, mergedClock));
            }
            else if (conflict.LocalVersion.HappensBefore(conflict.RemoteVersion))
            {
                return Task.FromResult((conflict.RemoteData, mergedClock));
            }
            else
            {
                // Concurrent - fall back to larger data (more recent operations)
                var winner = conflict.LocalData.Length >= conflict.RemoteData.Length
                    ? conflict.LocalData
                    : conflict.RemoteData;
                return Task.FromResult((winner, mergedClock));
            }
        }

        /// <inheritdoc/>
        public override Task<bool> VerifyConsistencyAsync(
            IEnumerable<string> nodeIds,
            string dataId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_dataStore.ContainsKey(dataId) &&
                (!_pendingConflicts.TryGetValue(dataId, out var c) || c.Count == 0));
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
    /// Merge-based conflict resolution strategy with field-level merging
    /// and semantic understanding of data structure.
    /// </summary>
    public sealed class MergeConflictStrategy : EnhancedReplicationStrategyBase
    {
        private readonly BoundedDictionary<string, (byte[] Data, EnhancedVectorClock Clock)> _dataStore = new BoundedDictionary<string, (byte[] Data, EnhancedVectorClock Clock)>(1000);
        private readonly BoundedDictionary<string, Func<byte[], byte[], byte[]>> _customMergers = new BoundedDictionary<string, Func<byte[], byte[], byte[]>>(1000);

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "MergeConflict",
            Description = "Merge-based conflict resolution with field-level merging and custom merge functions",
            ConsistencyModel = ConsistencyModel.Eventual,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: true,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.Merge, ConflictResolutionMethod.Custom },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: false,
                IsGeoAware: false,
                MaxReplicationLag: TimeSpan.FromSeconds(30),
                MinReplicaCount: 2,
                MaxReplicaCount: 50),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = true,
            SupportsStreaming = false,
            TypicalLagMs = 60,
            ConsistencySlaMs = 30000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Registers a custom merge function for a data type.
        /// </summary>
        public void RegisterMerger(string dataType, Func<byte[], byte[], byte[]> merger)
        {
            _customMergers[dataType] = merger;
        }

        /// <summary>
        /// Performs JSON field-level merge.
        /// </summary>
        public byte[] MergeJson(byte[] local, byte[] remote)
        {
            try
            {
                using var localDoc = JsonDocument.Parse(local);
                using var remoteDoc = JsonDocument.Parse(remote);

                var merged = MergeJsonElements(localDoc.RootElement, remoteDoc.RootElement);
                return JsonSerializer.SerializeToUtf8Bytes(merged);
            }
            catch
            {
                // Fallback to concatenation if not valid JSON
                return ConcatenateBytes(local, remote);
            }
        }

        private static Dictionary<string, JsonElement> MergeJsonElements(JsonElement local, JsonElement remote)
        {
            // Build a merged JsonObject by serialising back through a writer so all values
            // are proper JsonElement instances (avoids unsafe casts between JsonElement and
            // Dictionary<string,object?> in the recursive case).
            var bufferWriter = new System.Buffers.ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(bufferWriter))
            {
                writer.WriteStartObject();

                // Start with all local properties
                var localProps = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                if (local.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in local.EnumerateObject())
                    {
                        localProps[prop.Name] = prop.Value.Clone();
                    }
                }

                // Merge remote properties
                if (remote.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in remote.EnumerateObject())
                    {
                        if (localProps.TryGetValue(prop.Name, out var localValue) &&
                            prop.Value.ValueKind == JsonValueKind.Object &&
                            localValue.ValueKind == JsonValueKind.Object)
                        {
                            // Recursively merge nested objects by serialising the sub-result
                            var nested = MergeJsonElements(localValue, prop.Value);
                            writer.WritePropertyName(prop.Name);
                            writer.WriteStartObject();
                            foreach (var kv in nested)
                            {
                                writer.WritePropertyName(kv.Key);
                                kv.Value.WriteTo(writer);
                            }
                            writer.WriteEndObject();
                            localProps.Remove(prop.Name); // already written
                        }
                        else
                        {
                            // Remote wins for primitives / arrays / type mismatches
                            localProps[prop.Name] = prop.Value.Clone();
                        }
                    }
                }

                // Write remaining local props (not yet written by the loop above)
                foreach (var kv in localProps)
                {
                    writer.WritePropertyName(kv.Key);
                    kv.Value.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            using var doc = JsonDocument.Parse(bufferWriter.WrittenMemory);
            return doc.RootElement.EnumerateObject()
                      .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        }

        private static byte[] ConcatenateBytes(byte[] a, byte[] b)
        {
            var result = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, result, 0, a.Length);
            Buffer.BlockCopy(b, 0, result, a.Length, b.Length);
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
            var dataType = metadata?.GetValueOrDefault("dataType") ?? "json";

            // Check for conflict and merge
            if (_dataStore.TryGetValue(key, out var existing) && existing.Clock.IsConcurrentWith(VectorClock))
            {
                byte[] merged;
                if (_customMergers.TryGetValue(dataType, out var customMerger))
                {
                    merged = customMerger(existing.Data, data.ToArray());
                }
                else if (dataType == "json")
                {
                    merged = MergeJson(existing.Data, data.ToArray());
                }
                else
                {
                    merged = ConcatenateBytes(existing.Data, data.ToArray());
                }

                var mergedClock = existing.Clock.Clone();
                mergedClock.Merge(VectorClock);
                mergedClock.Increment(LocalNodeId);

                _dataStore[key] = (merged, mergedClock);
            }
            else
            {
                _dataStore[key] = (data.ToArray(), VectorClock.Clone());
            }

            var tasks = targetNodeIds.Select(async targetId =>
            {
                var startTime = DateTime.UtcNow;
                await Task.Delay(35, cancellationToken);
                RecordReplicationLag(targetId, DateTime.UtcNow - startTime);
            });

            await Task.WhenAll(tasks);
        }

        /// <inheritdoc/>
        public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(
            ReplicationConflict conflict,
            CancellationToken cancellationToken = default)
        {
            var merged = MergeJson(conflict.LocalData.ToArray(), conflict.RemoteData.ToArray());

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
            return Task.FromResult(LagTracker.GetCurrentLag(targetNodeId));
        }
    }

    /// <summary>
    /// Custom conflict resolution strategy with pluggable resolution functions
    /// and business rule-based conflict handling.
    /// </summary>
    public sealed class CustomConflictStrategy : EnhancedReplicationStrategyBase
    {
        private readonly BoundedDictionary<string, (byte[] Data, EnhancedVectorClock Clock)> _dataStore = new BoundedDictionary<string, (byte[] Data, EnhancedVectorClock Clock)>(1000);
        private Func<byte[], byte[], (byte[] Winner, string Reason)>? _globalResolver;
        private readonly BoundedDictionary<string, Func<byte[], byte[], (byte[] Winner, string Reason)>> _typeResolvers = new BoundedDictionary<string, Func<byte[], byte[], (byte[] Winner, string Reason)>>(1000);

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "CustomConflict",
            Description = "Custom conflict resolution with pluggable resolution functions and business rules",
            ConsistencyModel = ConsistencyModel.Eventual,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: true,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.Custom },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: true,
                IsGeoAware: false,
                MaxReplicationLag: TimeSpan.FromSeconds(30),
                MinReplicaCount: 2,
                MaxReplicaCount: 50),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = false,
            SupportsStreaming = true,
            TypicalLagMs = 50,
            ConsistencySlaMs = 30000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Sets the global conflict resolver.
        /// </summary>
        public void SetGlobalResolver(Func<byte[], byte[], (byte[] Winner, string Reason)> resolver)
        {
            _globalResolver = resolver;
        }

        /// <summary>
        /// Sets a resolver for a specific data type.
        /// </summary>
        public void SetTypeResolver(string dataType, Func<byte[], byte[], (byte[] Winner, string Reason)> resolver)
        {
            _typeResolvers[dataType] = resolver;
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
            var dataType = metadata?.GetValueOrDefault("dataType") ?? "default";

            if (_dataStore.TryGetValue(key, out var existing) && existing.Clock.IsConcurrentWith(VectorClock))
            {
                // Apply custom resolution
                Func<byte[], byte[], (byte[] Winner, string Reason)>? resolver = null;

                if (_typeResolvers.TryGetValue(dataType, out var typeResolver))
                    resolver = typeResolver;
                else if (_globalResolver != null)
                    resolver = _globalResolver;

                byte[] resolved;
                if (resolver != null)
                {
                    var (winner, _) = resolver(existing.Data, data.ToArray());
                    resolved = winner;
                }
                else
                {
                    // Default: take newer data by size (assume more operations)
                    resolved = data.Length >= existing.Data.Length ? data.ToArray() : existing.Data;
                }

                var mergedClock = existing.Clock.Clone();
                mergedClock.Merge(VectorClock);

                _dataStore[key] = (resolved, mergedClock);
            }
            else
            {
                _dataStore[key] = (data.ToArray(), VectorClock.Clone());
            }

            var tasks = targetNodeIds.Select(async targetId =>
            {
                var startTime = DateTime.UtcNow;
                await Task.Delay(30, cancellationToken);
                RecordReplicationLag(targetId, DateTime.UtcNow - startTime);
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

            if (_globalResolver != null)
            {
                var (winner, _) = _globalResolver(conflict.LocalData.ToArray(), conflict.RemoteData.ToArray());
                return Task.FromResult<(ReadOnlyMemory<byte>, VectorClock)>((winner, mergedClock));
            }

            return Task.FromResult((conflict.LocalData, mergedClock));
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

    /// <summary>
    /// CRDT-based conflict resolution strategy providing conflict-free semantics
    /// for counters, sets, and registers.
    /// </summary>
    public sealed class CrdtConflictStrategy : EnhancedReplicationStrategyBase
    {
        private readonly BoundedDictionary<string, (byte[] Data, string CrdtType, EnhancedVectorClock Clock)> _dataStore = new BoundedDictionary<string, (byte[] Data, string CrdtType, EnhancedVectorClock Clock)>(1000);

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "CrdtConflict",
            Description = "CRDT-based conflict-free resolution for counters, sets, and registers",
            ConsistencyModel = ConsistencyModel.Causal,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: true,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.Crdt },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: false,
                IsGeoAware: false,
                MaxReplicationLag: TimeSpan.FromSeconds(30),
                MinReplicaCount: 2,
                MaxReplicaCount: 100),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = true,
            SupportsStreaming = true,
            TypicalLagMs = 25,
            ConsistencySlaMs = 30000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

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
            var crdtType = metadata?.GetValueOrDefault("crdtType") ?? "lww-register";

            if (_dataStore.TryGetValue(key, out var existing))
            {
                // CRDT merge - always succeeds
                var merged = MergeCrdt(existing.Data, data.ToArray(), crdtType);
                var mergedClock = existing.Clock.Clone();
                mergedClock.Merge(VectorClock);
                _dataStore[key] = (merged, crdtType, mergedClock);
            }
            else
            {
                _dataStore[key] = (data.ToArray(), crdtType, VectorClock.Clone());
            }

            var tasks = targetNodeIds.Select(async targetId =>
            {
                var startTime = DateTime.UtcNow;
                await Task.Delay(15, cancellationToken);
                RecordReplicationLag(targetId, DateTime.UtcNow - startTime);
            });

            await Task.WhenAll(tasks);
        }

        private byte[] MergeCrdt(byte[] local, byte[] remote, string crdtType)
        {
            return crdtType.ToLowerInvariant() switch
            {
                "g-counter" => MergeGCounter(local, remote),
                "pn-counter" => MergePNCounter(local, remote),
                "lww-register" => MergeLWWRegister(local, remote),
                "or-set" => MergeORSet(local, remote),
                _ => local.Length >= remote.Length ? local : remote
            };
        }

        private byte[] MergeGCounter(byte[] local, byte[] remote)
        {
            try
            {
                var localCounts = JsonSerializer.Deserialize<Dictionary<string, long>>(local) ?? new();
                var remoteCounts = JsonSerializer.Deserialize<Dictionary<string, long>>(remote) ?? new();

                foreach (var (k, v) in remoteCounts)
                {
                    if (!localCounts.TryGetValue(k, out var existing) || v > existing)
                        localCounts[k] = v;
                }

                return JsonSerializer.SerializeToUtf8Bytes(localCounts);
            }
            catch
            {
                return local;
            }
        }

        private byte[] MergePNCounter(byte[] local, byte[] remote)
        {
            try
            {
                var localDoc = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, long>>>(local);
                var remoteDoc = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, long>>>(remote);

                if (localDoc == null) return remote;
                if (remoteDoc == null) return local;

                foreach (var counter in new[] { "P", "N" })
                {
                    if (remoteDoc.TryGetValue(counter, out var remoteCounter))
                    {
                        if (!localDoc.ContainsKey(counter))
                            localDoc[counter] = new Dictionary<string, long>();

                        foreach (var (k, v) in remoteCounter)
                        {
                            if (!localDoc[counter].TryGetValue(k, out var existing) || v > existing)
                                localDoc[counter][k] = v;
                        }
                    }
                }

                return JsonSerializer.SerializeToUtf8Bytes(localDoc);
            }
            catch
            {
                return local;
            }
        }

        private byte[] MergeLWWRegister(byte[] local, byte[] remote)
        {
            try
            {
                var localReg = JsonSerializer.Deserialize<Dictionary<string, object>>(local);
                var remoteReg = JsonSerializer.Deserialize<Dictionary<string, object>>(remote);

                if (localReg == null) return remote;
                if (remoteReg == null) return local;

                var localTs = localReg.TryGetValue("ts", out var lts) ? Convert.ToInt64(lts) : 0;
                var remoteTs = remoteReg.TryGetValue("ts", out var rts) ? Convert.ToInt64(rts) : 0;

                return remoteTs > localTs ? remote : local;
            }
            catch
            {
                return local;
            }
        }

        private byte[] MergeORSet(byte[] local, byte[] remote)
        {
            try
            {
                var localSet = JsonSerializer.Deserialize<Dictionary<string, HashSet<string>>>(local) ?? new();
                var remoteSet = JsonSerializer.Deserialize<Dictionary<string, HashSet<string>>>(remote) ?? new();

                foreach (var (element, tags) in remoteSet)
                {
                    if (!localSet.ContainsKey(element))
                        localSet[element] = new HashSet<string>();
                    localSet[element].UnionWith(tags);
                }

                return JsonSerializer.SerializeToUtf8Bytes(localSet);
            }
            catch
            {
                return local;
            }
        }

        /// <inheritdoc/>
        public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(
            ReplicationConflict conflict,
            CancellationToken cancellationToken = default)
        {
            // CRDT merge always succeeds
            var merged = MergeCrdt(conflict.LocalData.ToArray(), conflict.RemoteData.ToArray(), "lww-register");

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
            return Task.FromResult(LagTracker.GetCurrentLag(targetNodeId));
        }
    }

    /// <summary>
    /// Version-based conflict resolution strategy using monotonic version numbers
    /// and optimistic concurrency control.
    /// </summary>
    public sealed class VersionConflictStrategy : EnhancedReplicationStrategyBase
    {
        private readonly BoundedDictionary<string, (byte[] Data, long Version, string NodeId)> _dataStore = new BoundedDictionary<string, (byte[] Data, long Version, string NodeId)>(1000);
        private readonly BoundedDictionary<string, long> _versionCounters = new BoundedDictionary<string, long>(1000);

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "VersionConflict",
            Description = "Version-based conflict resolution with monotonic version numbers and optimistic concurrency",
            ConsistencyModel = ConsistencyModel.Strong,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: false,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.FirstWriteWins },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: true,
                IsGeoAware: false,
                MaxReplicationLag: TimeSpan.FromSeconds(10),
                MinReplicaCount: 2,
                MaxReplicaCount: 50),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = false,
            SupportsDeltaSync = false,
            SupportsStreaming = true,
            TypicalLagMs = 20,
            ConsistencySlaMs = 10000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Gets the current version for a key.
        /// </summary>
        public long GetVersion(string key)
        {
            return _dataStore.TryGetValue(key, out var item) ? item.Version : 0;
        }

        /// <summary>
        /// Attempts a conditional write (only succeeds if version matches).
        /// </summary>
        public bool TryConditionalWrite(string key, byte[] data, long expectedVersion, string nodeId, out long newVersion)
        {
            newVersion = 0;

            if (_dataStore.TryGetValue(key, out var existing))
            {
                if (existing.Version != expectedVersion)
                {
                    return false; // Version conflict
                }
            }
            else if (expectedVersion != 0)
            {
                return false; // Expected existing data
            }

            var version = _versionCounters.AddOrUpdate(key, 1, (_, v) => v + 1);
            _dataStore[key] = (data, version, nodeId);
            newVersion = version;
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
            var expectedVersion = long.TryParse(metadata?.GetValueOrDefault("expectedVersion"), out var ev) ? ev : 0;

            if (!TryConditionalWrite(key, data.ToArray(), expectedVersion, sourceNodeId, out var newVersion))
            {
                throw new InvalidOperationException($"Version conflict for key '{key}': expected version {expectedVersion}");
            }

            var tasks = targetNodeIds.Select(async targetId =>
            {
                var startTime = DateTime.UtcNow;
                await Task.Delay(15, cancellationToken);
                RecordReplicationLag(targetId, DateTime.UtcNow - startTime);
            });

            await Task.WhenAll(tasks);
        }

        /// <inheritdoc/>
        public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(
            ReplicationConflict conflict,
            CancellationToken cancellationToken = default)
        {
            // Version-based: first write wins (reject later writes)
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

    /// <summary>
    /// Three-way merge conflict resolution strategy for text-like data
    /// with common ancestor tracking.
    /// </summary>
    public sealed class ThreeWayMergeStrategy : EnhancedReplicationStrategyBase
    {
        private readonly BoundedDictionary<string, VersionedData> _dataStore = new BoundedDictionary<string, VersionedData>(1000);
        private readonly BoundedDictionary<string, List<byte[]>> _ancestors = new BoundedDictionary<string, List<byte[]>>(1000);

        private sealed class VersionedData
        {
            public required byte[] Data { get; init; }
            public required EnhancedVectorClock Clock { get; init; }
            public string? AncestorId { get; init; }
        }

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "ThreeWayMerge",
            Description = "Three-way merge conflict resolution with common ancestor tracking for text-like data",
            ConsistencyModel = ConsistencyModel.Eventual,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: true,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.Merge },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: false,
                IsGeoAware: false,
                MaxReplicationLag: TimeSpan.FromSeconds(60),
                MinReplicaCount: 2,
                MaxReplicaCount: 20),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = true,
            SupportsStreaming = false,
            TypicalLagMs = 100,
            ConsistencySlaMs = 60000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Performs three-way merge of byte arrays (line-based for text).
        /// </summary>
        public byte[] ThreeWayMerge(byte[] ancestor, byte[] local, byte[] remote)
        {
            var ancestorLines = SplitLines(ancestor);
            var localLines = SplitLines(local);
            var remoteLines = SplitLines(remote);

            var result = new List<string>();

            var maxLines = Math.Max(Math.Max(ancestorLines.Length, localLines.Length), remoteLines.Length);

            for (int i = 0; i < maxLines; i++)
            {
                var ancestorLine = i < ancestorLines.Length ? ancestorLines[i] : "";
                var localLine = i < localLines.Length ? localLines[i] : "";
                var remoteLine = i < remoteLines.Length ? remoteLines[i] : "";

                if (localLine == remoteLine)
                {
                    result.Add(localLine);
                }
                else if (localLine == ancestorLine)
                {
                    result.Add(remoteLine); // Remote changed
                }
                else if (remoteLine == ancestorLine)
                {
                    result.Add(localLine); // Local changed
                }
                else
                {
                    // Both changed - include both with markers
                    result.Add($"<<<<<<< LOCAL");
                    result.Add(localLine);
                    result.Add($"=======");
                    result.Add(remoteLine);
                    result.Add($">>>>>>> REMOTE");
                }
            }

            return System.Text.Encoding.UTF8.GetBytes(string.Join("\n", result));
        }

        private static string[] SplitLines(byte[] data)
        {
            var text = System.Text.Encoding.UTF8.GetString(data);
            return text.Split('\n');
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
            var ancestorId = metadata?.GetValueOrDefault("ancestorId");

            if (_dataStore.TryGetValue(key, out var existing) && existing.Clock.IsConcurrentWith(VectorClock))
            {
                // Get common ancestor
                byte[] ancestor = Array.Empty<byte>();
                if (!string.IsNullOrEmpty(ancestorId) && _ancestors.TryGetValue(key, out var ancestorList) && ancestorList.Count > 0)
                {
                    ancestor = ancestorList[0];
                }

                var merged = ThreeWayMerge(ancestor, existing.Data, data.ToArray());
                var mergedClock = existing.Clock.Clone();
                mergedClock.Merge(VectorClock);

                _dataStore[key] = new VersionedData { Data = merged, Clock = mergedClock, AncestorId = ancestorId };
            }
            else
            {
                // Store as ancestor for future merges
                var ancestorList = _ancestors.GetOrAdd(key, _ => new List<byte[]>());
                if (_dataStore.TryGetValue(key, out var oldData))
                {
                    lock (ancestorList)
                    {
                        ancestorList.Insert(0, oldData.Data);
                        if (ancestorList.Count > 10) ancestorList.RemoveAt(ancestorList.Count - 1);
                    }
                }

                _dataStore[key] = new VersionedData { Data = data.ToArray(), Clock = VectorClock.Clone(), AncestorId = ancestorId };
            }

            var tasks = targetNodeIds.Select(async targetId =>
            {
                var startTime = DateTime.UtcNow;
                await Task.Delay(50, cancellationToken);
                RecordReplicationLag(targetId, DateTime.UtcNow - startTime);
            });

            await Task.WhenAll(tasks);
        }

        /// <inheritdoc/>
        public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(
            ReplicationConflict conflict,
            CancellationToken cancellationToken = default)
        {
            var merged = ThreeWayMerge(Array.Empty<byte>(), conflict.LocalData.ToArray(), conflict.RemoteData.ToArray());

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
            return Task.FromResult(LagTracker.GetCurrentLag(targetNodeId));
        }
    }
}
