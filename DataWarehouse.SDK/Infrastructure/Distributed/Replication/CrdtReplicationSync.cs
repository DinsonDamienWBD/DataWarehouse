using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Distributed;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using SyncResult = DataWarehouse.SDK.Contracts.Distributed.SyncResult;
using SyncConflict = DataWarehouse.SDK.Contracts.Distributed.SyncConflict;
using ConflictResolutionResult = DataWarehouse.SDK.Contracts.Distributed.ConflictResolutionResult;
using ConflictResolutionStrategy = DataWarehouse.SDK.Contracts.Distributed.ConflictResolutionStrategy;

namespace DataWarehouse.SDK.Infrastructure.Distributed
{
    /// <summary>
    /// Configuration for CRDT-based replication sync.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 29: CRDT-based multi-master replication")]
    public sealed record CrdtReplicationSyncConfiguration
    {
        /// <summary>
        /// Maximum number of items in the data store (bounded collection).
        /// </summary>
        public int MaxStoredItems { get; init; } = 100_000;

        /// <summary>
        /// Default conflict resolution strategy for non-CRDT keys.
        /// </summary>
        public ConflictResolutionStrategy DefaultStrategy { get; init; } = ConflictResolutionStrategy.Merge;

        /// <summary>
        /// Number of items per sync batch.
        /// </summary>
        public int SyncBatchSize { get; init; } = 100;

        /// <summary>
        /// Interval in milliseconds for background gossip propagation.
        /// </summary>
        public int GossipPropagationIntervalMs { get; init; } = 1000;

        /// <summary>
        /// Shared secret for HMAC-SHA256 authentication of CRDT gossip messages.
        /// When set, all outgoing CRDT messages are signed and incoming messages are verified.
        /// Messages with invalid or missing HMAC are rejected. (DIST-04 mitigation)
        /// </summary>
        public byte[]? ClusterSecret { get; init; }
    }

    /// <summary>
    /// Internal data item tracked by the CRDT replication store.
    /// </summary>
    internal sealed class CrdtDataItem
    {
        public string Key { get; set; } = string.Empty;
        public required ICrdtType Value { get; set; }
        public DataWarehouse.SDK.Replication.VectorClock Clock { get; set; } = new(new Dictionary<string, long>());
        public DateTimeOffset LastModified { get; set; }
    }

    /// <summary>
    /// CRDT-based replication sync implementing IReplicationSync.
    /// Resolves conflicts using CRDT merge functions when ConflictResolutionStrategy is Merge.
    /// Data propagates to peers via IGossipProtocol epidemic spread.
    /// Uses VectorClock for causality tracking to detect concurrent writes.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 29: CRDT-based multi-master replication")]
    public sealed class CrdtReplicationSync : IReplicationSync, IDisposable
    {
        private readonly IGossipProtocol _gossip;
        private readonly IClusterMembership _membership;
        private readonly CrdtRegistry _registry;
        private readonly CrdtReplicationSyncConfiguration _config;
        private readonly ConcurrentDictionary<string, CrdtDataItem> _dataStore = new();
        private readonly ConcurrentDictionary<string, SyncStatus> _syncStatuses = new();
        private DataWarehouse.SDK.Replication.VectorClock _localClock = new(new Dictionary<string, long>());
        private readonly SemaphoreSlim _clockLock = new(1, 1);
        private readonly CancellationTokenSource _cts = new();
        private Task? _propagationTask;

        /// <summary>
        /// Creates a new CRDT-based replication sync engine.
        /// </summary>
        /// <param name="gossip">Gossip protocol for epidemic data propagation.</param>
        /// <param name="membership">Cluster membership for node identity.</param>
        /// <param name="registry">CRDT type registry for key-to-CRDT-type mapping.</param>
        /// <param name="config">Optional configuration.</param>
        public CrdtReplicationSync(
            IGossipProtocol gossip,
            IClusterMembership membership,
            CrdtRegistry registry,
            CrdtReplicationSyncConfiguration? config = null)
        {
            _gossip = gossip ?? throw new ArgumentNullException(nameof(gossip));
            _membership = membership ?? throw new ArgumentNullException(nameof(membership));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _config = config ?? new CrdtReplicationSyncConfiguration();

            // Subscribe to gossip for incoming data
            _gossip.OnGossipReceived += HandleGossipReceived;

            // Start background propagation
            _propagationTask = Task.Run(() => RunPropagationLoopAsync(_cts.Token), _cts.Token);
        }

        /// <inheritdoc />
        public event Action<SyncEvent>? OnSyncEvent;

        /// <inheritdoc />
        public SyncMode CurrentMode => SyncMode.Online;

        /// <inheritdoc />
        public async Task<SyncResult> SyncAsync(SyncRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            var sw = Stopwatch.StartNew();
            long itemsSynced = 0;
            long conflictsDetected = 0;

            try
            {
                FireSyncEvent(SyncEventType.SyncStarted, request.TargetNodeId, "Sync started");

                // Update sync status
                _syncStatuses[request.TargetNodeId] = new SyncStatus
                {
                    TargetNodeId = request.TargetNodeId,
                    State = SyncState.Syncing,
                    ProgressPercent = 0.0,
                    LastSyncAt = DateTimeOffset.UtcNow
                };

                // Get items to sync
                var items = GetItemsToSync(request);
                var batches = items
                    .Select((item, idx) => new { item, idx })
                    .GroupBy(x => x.idx / _config.SyncBatchSize)
                    .Select(g => g.Select(x => x.item).ToList())
                    .ToList();

                int batchIndex = 0;
                foreach (var batch in batches)
                {
                    ct.ThrowIfCancellationRequested();

                    var payload = SerializeBatch(batch);
                    var gossipMessage = new GossipMessage
                    {
                        MessageId = Guid.NewGuid().ToString("N"),
                        OriginNodeId = _membership.GetSelf().NodeId,
                        Payload = payload,
                        Generation = 0,
                        Timestamp = DateTimeOffset.UtcNow
                    };

                    await _gossip.SpreadAsync(gossipMessage, ct).ConfigureAwait(false);
                    itemsSynced += batch.Count;
                    batchIndex++;

                    // Update progress
                    double progress = batches.Count > 0 ? (double)batchIndex / batches.Count * 100.0 : 100.0;
                    _syncStatuses[request.TargetNodeId] = new SyncStatus
                    {
                        TargetNodeId = request.TargetNodeId,
                        State = SyncState.Syncing,
                        ProgressPercent = progress,
                        LastSyncAt = DateTimeOffset.UtcNow
                    };
                }

                sw.Stop();

                _syncStatuses[request.TargetNodeId] = new SyncStatus
                {
                    TargetNodeId = request.TargetNodeId,
                    State = SyncState.Completed,
                    ProgressPercent = 100.0,
                    LastSyncAt = DateTimeOffset.UtcNow
                };

                FireSyncEvent(SyncEventType.SyncCompleted, request.TargetNodeId,
                    $"Synced {itemsSynced} items, {conflictsDetected} conflicts");

                return SyncResult.Ok(itemsSynced, conflictsDetected, sw.Elapsed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sw.Stop();

                _syncStatuses[request.TargetNodeId] = new SyncStatus
                {
                    TargetNodeId = request.TargetNodeId,
                    State = SyncState.Error,
                    ProgressPercent = 0.0,
                    LastSyncAt = DateTimeOffset.UtcNow
                };

                FireSyncEvent(SyncEventType.SyncFailed, request.TargetNodeId, ex.Message);
                return SyncResult.Error(ex.Message, sw.Elapsed);
            }
        }

        /// <inheritdoc />
        public Task<SyncStatus> GetSyncStatusAsync(string targetNodeId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            if (_syncStatuses.TryGetValue(targetNodeId, out var status))
            {
                return Task.FromResult(status);
            }

            return Task.FromResult(new SyncStatus
            {
                TargetNodeId = targetNodeId,
                State = SyncState.Idle,
                ProgressPercent = 0.0,
                LastSyncAt = DateTimeOffset.MinValue
            });
        }

        /// <inheritdoc />
        public Task<ConflictResolutionResult> ResolveConflictAsync(SyncConflict conflict, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            var crdtType = _registry.GetCrdtType(conflict.Key);

            // If key has a CRDT type registration, use CRDT merge
            if (crdtType != _registry.DefaultType || _config.DefaultStrategy == ConflictResolutionStrategy.Merge)
            {
                try
                {
                    var localCrdt = _registry.Deserialize(conflict.Key, conflict.LocalValue);
                    var remoteCrdt = _registry.Deserialize(conflict.Key, conflict.RemoteValue);
                    var merged = localCrdt.Merge(remoteCrdt);

                    return Task.FromResult(new ConflictResolutionResult
                    {
                        Key = conflict.Key,
                        Strategy = ConflictResolutionStrategy.Merge,
                        ResolvedValue = merged.Serialize()
                    });
                }
                catch
                {
                    // Fall through to non-CRDT strategies if deserialization fails
                }
            }

            // Non-CRDT fallback strategies
            return _config.DefaultStrategy switch
            {
                ConflictResolutionStrategy.LocalWins => Task.FromResult(new ConflictResolutionResult
                {
                    Key = conflict.Key,
                    Strategy = ConflictResolutionStrategy.LocalWins,
                    ResolvedValue = conflict.LocalValue
                }),
                ConflictResolutionStrategy.RemoteWins => Task.FromResult(new ConflictResolutionResult
                {
                    Key = conflict.Key,
                    Strategy = ConflictResolutionStrategy.RemoteWins,
                    ResolvedValue = conflict.RemoteValue
                }),
                ConflictResolutionStrategy.LatestWins => Task.FromResult(new ConflictResolutionResult
                {
                    Key = conflict.Key,
                    Strategy = ConflictResolutionStrategy.LatestWins,
                    ResolvedValue = conflict.LocalTimestamp >= conflict.RemoteTimestamp
                        ? conflict.LocalValue
                        : conflict.RemoteValue
                }),
                _ => Task.FromResult(new ConflictResolutionResult
                {
                    Key = conflict.Key,
                    Strategy = ConflictResolutionStrategy.LocalWins,
                    ResolvedValue = conflict.LocalValue
                })
            };
        }

        /// <summary>
        /// Writes a local data item, incrementing the vector clock and storing the CRDT value.
        /// </summary>
        internal async Task WriteLocalAsync(string key, ICrdtType value, CancellationToken ct = default)
        {
            string selfNodeId = _membership.GetSelf().NodeId;

            await _clockLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _localClock = _localClock.Increment(selfNodeId);
            }
            finally
            {
                _clockLock.Release();
            }

            var item = new CrdtDataItem
            {
                Key = key,
                Value = value,
                Clock = _localClock,
                LastModified = DateTimeOffset.UtcNow
            };

            _dataStore[key] = item;
            EnforceBounds();
        }

        private void HandleGossipReceived(GossipMessage gossipMessage)
        {
            try
            {
                // DIST-04: Validate that the sender is a known cluster member before merging state
                if (!string.IsNullOrEmpty(gossipMessage.OriginNodeId))
                {
                    var knownMembers = _membership.GetMembers();
                    bool isMember = false;
                    foreach (var m in knownMembers)
                    {
                        if (string.Equals(m.NodeId, gossipMessage.OriginNodeId, StringComparison.Ordinal))
                        {
                            isMember = true;
                            break;
                        }
                    }
                    if (!isMember)
                    {
                        // Reject CRDT updates from unknown nodes
                        FireSyncEvent(SyncEventType.SyncFailed, gossipMessage.OriginNodeId,
                            $"Rejected CRDT update from unknown node: {gossipMessage.OriginNodeId}");
                        return;
                    }
                }

                // DIST-04: Verify HMAC authentication if cluster secret is configured
                if (_config.ClusterSecret != null && _config.ClusterSecret.Length > 0)
                {
                    if (!VerifyCrdtGossipHmac(gossipMessage))
                    {
                        FireSyncEvent(SyncEventType.SyncFailed, gossipMessage.OriginNodeId ?? "unknown",
                            "Rejected CRDT gossip: HMAC verification failed");
                        return;
                    }
                }

                var items = DeserializeBatch(gossipMessage.Payload);
                if (items == null) return;

                foreach (var remoteItem in items)
                {
                    ProcessRemoteItem(remoteItem);
                }
            }
            catch
            {
                // Gossip may carry non-CRDT messages; ignore parse errors
            }
        }

        /// <summary>
        /// Verifies the HMAC-SHA256 signature of a CRDT gossip message (DIST-04 mitigation).
        /// The HMAC is computed over the payload bytes using the cluster secret.
        /// </summary>
        private bool VerifyCrdtGossipHmac(GossipMessage message)
        {
            if (_config.ClusterSecret == null || _config.ClusterSecret.Length == 0)
                return true; // No secret configured, skip verification

            if (message.Payload == null || message.Payload.Length < 32)
                return false; // Too short to contain HMAC

            // HMAC is the last 32 bytes of the payload
            int dataLength = message.Payload.Length - 32;
            var data = message.Payload.AsSpan(0, dataLength);
            var receivedHmac = message.Payload.AsSpan(dataLength, 32);

            using var hmac = new HMACSHA256(_config.ClusterSecret);
            var expectedHmac = hmac.ComputeHash(data.ToArray());

            return CryptographicOperations.FixedTimeEquals(receivedHmac, expectedHmac);
        }

        /// <summary>
        /// Maximum allowed clock skew for incoming CRDT timestamps.
        /// Timestamps more than this amount in the future are rejected (DIST-08 mitigation).
        /// </summary>
        private static readonly TimeSpan MaxTimestampSkew = TimeSpan.FromHours(1);

        private void ProcessRemoteItem(CrdtSyncItem remoteItem)
        {
            try
            {
                // DIST-08: Reject items with timestamps too far in the future
                if (remoteItem.LastModified > DateTimeOffset.UtcNow + MaxTimestampSkew)
                {
                    FireSyncEvent(SyncEventType.SyncFailed, remoteItem.Key,
                        $"Rejected CRDT item with suspicious future timestamp: {remoteItem.LastModified}");
                    return;
                }

                var remoteCrdt = _registry.Deserialize(remoteItem.Key, remoteItem.Value);
                var remoteClock = new DataWarehouse.SDK.Replication.VectorClock(
                    remoteItem.ClockEntries ?? new Dictionary<string, long>());

                if (_dataStore.TryGetValue(remoteItem.Key, out var localItem))
                {
                    // Compare vector clocks for causality
                    bool remoteHappensBefore = remoteClock.HappensBefore(localItem.Clock);
                    bool localHappensBefore = localItem.Clock.HappensBefore(remoteClock);

                    if (remoteHappensBefore)
                    {
                        // Remote is older -- skip
                        return;
                    }

                    if (localHappensBefore)
                    {
                        // Remote is newer -- replace
                        localItem.Value = remoteCrdt;
                        localItem.Clock = remoteClock;
                        localItem.LastModified = DateTimeOffset.UtcNow;
                        return;
                    }

                    // Concurrent writes -- CRDT merge
                    FireSyncEvent(SyncEventType.ConflictDetected, remoteItem.Key,
                        $"Concurrent write detected for key: {remoteItem.Key}");

                    var merged = localItem.Value.Merge(remoteCrdt);
                    var mergedClock = DataWarehouse.SDK.Replication.VectorClock.Merge(localItem.Clock, remoteClock);

                    localItem.Value = merged;
                    localItem.Clock = mergedClock;
                    localItem.LastModified = DateTimeOffset.UtcNow;

                    FireSyncEvent(SyncEventType.ConflictResolved, remoteItem.Key,
                        $"CRDT merge resolved conflict for key: {remoteItem.Key}");
                }
                else
                {
                    // No local version -- store directly
                    _dataStore[remoteItem.Key] = new CrdtDataItem
                    {
                        Key = remoteItem.Key,
                        Value = remoteCrdt,
                        Clock = remoteClock,
                        LastModified = DateTimeOffset.UtcNow
                    };
                }

                EnforceBounds();
            }
            catch
            {
                // Processing failure should not crash the handler
            }
        }

        private async Task RunPropagationLoopAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_config.GossipPropagationIntervalMs));

            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    // Propagate recently modified items
                    var recentItems = _dataStore.Values
                        .OrderByDescending(i => i.LastModified)
                        .Take(_config.SyncBatchSize)
                        .ToList();

                    if (recentItems.Count == 0) continue;

                    var payload = SerializeBatch(recentItems);
                    var gossipMessage = new GossipMessage
                    {
                        MessageId = Guid.NewGuid().ToString("N"),
                        OriginNodeId = _membership.GetSelf().NodeId,
                        Payload = payload,
                        Generation = 0,
                        Timestamp = DateTimeOffset.UtcNow
                    };

                    await _gossip.SpreadAsync(gossipMessage, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    // Propagation failure should not crash the loop
                }
            }
        }

        private List<CrdtDataItem> GetItemsToSync(SyncRequest request)
        {
            var query = _dataStore.Values.AsEnumerable();

            if (request.SinceTimestamp.HasValue)
            {
                query = query.Where(i => i.LastModified >= request.SinceTimestamp.Value);
            }

            if (request.DataSetFilter != null && request.DataSetFilter.Count > 0)
            {
                var filterSet = new HashSet<string>(request.DataSetFilter, StringComparer.Ordinal);
                query = query.Where(i => filterSet.Contains(i.Key));
            }

            return query.ToList();
        }

        private byte[] SerializeBatch(List<CrdtDataItem> items)
        {
            var syncItems = items.Select(item => new CrdtSyncItem
            {
                Key = item.Key,
                Value = item.Value.Serialize(),
                ClockEntries = item.Clock.Clocks,
                LastModified = item.LastModified
            }).ToList();

            var data = JsonSerializer.SerializeToUtf8Bytes(syncItems);

            // DIST-04: Append HMAC-SHA256 when cluster secret is configured
            if (_config.ClusterSecret != null && _config.ClusterSecret.Length > 0)
            {
                using var hmac = new HMACSHA256(_config.ClusterSecret);
                var mac = hmac.ComputeHash(data);
                var authenticated = new byte[data.Length + mac.Length];
                Buffer.BlockCopy(data, 0, authenticated, 0, data.Length);
                Buffer.BlockCopy(mac, 0, authenticated, data.Length, mac.Length);
                return authenticated;
            }

            return data;
        }

        private List<CrdtSyncItem>? DeserializeBatch(byte[] data)
        {
            try
            {
                // If cluster secret is configured, strip HMAC (last 32 bytes) before deserializing.
                // HMAC verification was already done in HandleGossipReceived.
                byte[] jsonData = data;
                if (_config.ClusterSecret != null && _config.ClusterSecret.Length > 0 && data.Length > 32)
                {
                    jsonData = new byte[data.Length - 32];
                    Buffer.BlockCopy(data, 0, jsonData, 0, jsonData.Length);
                }

                return JsonSerializer.Deserialize<List<CrdtSyncItem>>(jsonData);
            }
            catch
            {
                return null;
            }
        }

        private void EnforceBounds()
        {
            if (_dataStore.Count > _config.MaxStoredItems)
            {
                var toEvict = _dataStore
                    .OrderBy(kv => kv.Value.LastModified)
                    .Take(_dataStore.Count - _config.MaxStoredItems)
                    .Select(kv => kv.Key)
                    .ToList();

                foreach (var key in toEvict)
                {
                    _dataStore.TryRemove(key, out _);
                }
            }
        }

        private void FireSyncEvent(SyncEventType eventType, string targetNodeId, string? detail)
        {
            OnSyncEvent?.Invoke(new SyncEvent
            {
                EventType = eventType,
                TargetNodeId = targetNodeId,
                Timestamp = DateTimeOffset.UtcNow,
                Detail = detail
            });
        }

        /// <summary>
        /// Disposes the CRDT replication sync, stopping background tasks and releasing resources.
        /// </summary>
        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
            _clockLock.Dispose();
            _gossip.OnGossipReceived -= HandleGossipReceived;
        }
    }

    /// <summary>
    /// Serializable sync item for gossip transport.
    /// </summary>
    internal sealed class CrdtSyncItem
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        [JsonPropertyName("value")]
        public byte[] Value { get; set; } = Array.Empty<byte>();

        [JsonPropertyName("clock")]
        public Dictionary<string, long>? ClockEntries { get; set; }

        [JsonPropertyName("lastModified")]
        public DateTimeOffset LastModified { get; set; }
    }
}
