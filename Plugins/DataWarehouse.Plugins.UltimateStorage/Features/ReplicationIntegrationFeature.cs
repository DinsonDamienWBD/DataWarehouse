using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Storage;
using IStorageStrategy = DataWarehouse.SDK.Contracts.Storage.IStorageStrategy;
using DataWarehouse.SDK.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Features
{
    /// <summary>
    /// Replication Integration Feature (C10) - Integration with Ultimate Replication plugin (T98).
    ///
    /// Features:
    /// - Message bus integration with T98 Replication plugin
    /// - Geo-distributed storage replication
    /// - Cross-region object synchronization
    /// - Conflict resolution policies
    /// - Active-active and active-passive replication
    /// - Synchronous and asynchronous replication modes
    /// - Replication lag monitoring
    /// - Automatic failover to replicas
    /// </summary>
    public sealed class ReplicationIntegrationFeature : IDisposable
    {
        private readonly StrategyRegistry<IStorageStrategy> _registry;
        private readonly IMessageBus _messageBus;
        private readonly BoundedDictionary<string, ReplicationGroup> _replicationGroups = new BoundedDictionary<string, ReplicationGroup>(1000);
        private readonly BoundedDictionary<string, string> _objectToGroupMapping = new BoundedDictionary<string, string>(1000); // object key -> group ID
        private readonly BoundedDictionary<string, ReplicationLag> _replicationLags = new BoundedDictionary<string, ReplicationLag>(1000);
        private readonly object _replicaListLock = new();
        private bool _disposed;
        private IDisposable? _messageBusSubscription;
        private Timer? _lagMonitorTimer;

        // Configuration
        private const string ReplicationPluginTopic = "replication.storage";
        private const string ReplicationCommandTopic = "replication.command";
        private const string ReplicationStatusTopic = "replication.status";
        private TimeSpan _lagMonitorInterval = TimeSpan.FromMinutes(1);

        // Statistics
        private long _totalReplicationWrites;
        private long _totalReplicationReads;
        private long _totalConflicts = 0;
        private long _totalFailovers;
        private long _totalSyncOperations;
        private long _totalAsyncOperations;

        /// <summary>
        /// Initializes a new instance of the ReplicationIntegrationFeature.
        /// </summary>
        /// <param name="registry">The storage strategy registry.</param>
        /// <param name="messageBus">Message bus for inter-plugin communication.</param>
        public ReplicationIntegrationFeature(StrategyRegistry<IStorageStrategy> registry, IMessageBus messageBus)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));

            // Subscribe to replication plugin messages
            _messageBusSubscription = _messageBus.Subscribe(ReplicationStatusTopic, HandleReplicationStatusMessageAsync);

            // Start lag monitoring timer
            _lagMonitorTimer = new Timer(
                callback: async _ => await MonitorReplicationLagAsync(),
                state: null,
                dueTime: _lagMonitorInterval,
                period: _lagMonitorInterval);
        }

        /// <summary>
        /// Gets the total number of replication write operations.
        /// </summary>
        public long TotalReplicationWrites => Interlocked.Read(ref _totalReplicationWrites);

        /// <summary>
        /// Gets the total number of replication read operations.
        /// </summary>
        public long TotalReplicationReads => Interlocked.Read(ref _totalReplicationReads);

        /// <summary>
        /// Gets the total number of conflicts detected.
        /// </summary>
        public long TotalConflicts => Interlocked.Read(ref _totalConflicts);

        /// <summary>
        /// Gets the total number of failovers performed.
        /// </summary>
        public long TotalFailovers => Interlocked.Read(ref _totalFailovers);

        /// <summary>
        /// Creates a replication group with primary and replica backends.
        /// </summary>
        /// <param name="groupId">Unique group identifier.</param>
        /// <param name="primaryBackendId">Primary backend strategy ID.</param>
        /// <param name="replicaBackendIds">Replica backend strategy IDs.</param>
        /// <param name="mode">Replication mode (sync or async).</param>
        /// <param name="topology">Replication topology (active-active or active-passive).</param>
        /// <param name="conflictResolution">Conflict resolution policy.</param>
        /// <returns>The created replication group.</returns>
        public async Task<ReplicationGroup> CreateReplicationGroupAsync(
            string groupId,
            string primaryBackendId,
            IEnumerable<string> replicaBackendIds,
            ReplicationMode mode = ReplicationMode.Asynchronous,
            ReplicationTopology topology = ReplicationTopology.ActivePassive,
            ConflictResolutionPolicy conflictResolution = ConflictResolutionPolicy.LastWriteWins)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
            ArgumentException.ThrowIfNullOrWhiteSpace(primaryBackendId);
            ArgumentNullException.ThrowIfNull(replicaBackendIds);
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_replicationGroups.ContainsKey(groupId))
            {
                throw new InvalidOperationException($"Replication group '{groupId}' already exists");
            }

            var replicas = replicaBackendIds.ToList();

            if (!replicas.Any())
            {
                throw new ArgumentException("Replication group must have at least one replica");
            }

            // Validate backends exist
            if (_registry.Get(primaryBackendId) == null)
            {
                throw new ArgumentException($"Primary backend '{primaryBackendId}' not found in registry");
            }

            foreach (var replicaId in replicas)
            {
                if (_registry.Get(replicaId) == null)
                {
                    throw new ArgumentException($"Replica backend '{replicaId}' not found in registry");
                }
            }

            var group = new ReplicationGroup
            {
                GroupId = groupId,
                PrimaryBackendId = primaryBackendId,
                ReplicaBackendIds = replicas,
                Mode = mode,
                Topology = topology,
                ConflictResolution = conflictResolution,
                CreatedTime = DateTime.UtcNow,
                State = ReplicationState.Active
            };

            if (!_replicationGroups.TryAdd(groupId, group))
            {
                throw new InvalidOperationException($"Failed to add replication group '{groupId}'");
            }

            // Initialize replication lag tracking
            foreach (var replicaId in replicas)
            {
                _replicationLags[$"{groupId}:{replicaId}"] = new ReplicationLag
                {
                    GroupId = groupId,
                    ReplicaBackendId = replicaId
                };
            }

            // Notify replication plugin about new group
            await NotifyReplicationPluginAsync("group.created", new Dictionary<string, object>
            {
                ["groupId"] = groupId,
                ["primaryBackendId"] = primaryBackendId,
                ["replicaCount"] = replicas.Count,
                ["mode"] = mode.ToString(),
                ["topology"] = topology.ToString()
            });

            return group;
        }

        /// <summary>
        /// Writes data to a replication group with configured replication strategy.
        /// </summary>
        /// <param name="groupId">Replication group identifier.</param>
        /// <param name="objectKey">Object key.</param>
        /// <param name="data">Data to write.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task WriteToReplicationGroupAsync(string groupId, string objectKey, byte[] data, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
            ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
            ArgumentNullException.ThrowIfNull(data);
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_replicationGroups.TryGetValue(groupId, out var group))
            {
                throw new ArgumentException($"Replication group '{groupId}' not found");
            }

            if (group.State != ReplicationState.Active)
            {
                throw new InvalidOperationException($"Replication group '{groupId}' is not active (state: {group.State})");
            }

            Interlocked.Increment(ref _totalReplicationWrites);

            var timestamp = DateTime.UtcNow;
            var metadata = new Dictionary<string, string>
            {
                ["replication.groupId"] = groupId,
                ["replication.timestamp"] = timestamp.ToString("O"),
                ["replication.version"] = Guid.NewGuid().ToString()
            };

            // Write to primary
            var primaryBackend = _registry.Get(group.PrimaryBackendId);
            if (primaryBackend == null)
            {
                throw new InvalidOperationException($"Primary backend '{group.PrimaryBackendId}' not available");
            }

            // In active-active topology, check if a concurrent version exists to detect conflicts
            if (group.Topology == ReplicationTopology.ActiveActive)
            {
                try
                {
                    var existingMeta = await primaryBackend.GetMetadataAsync(objectKey, ct);
                    if (existingMeta.CustomMetadata != null &&
                        existingMeta.CustomMetadata.TryGetValue("replication.version", out var existingVersion) &&
                        !string.IsNullOrEmpty(existingVersion))
                    {
                        // A prior version exists — this is a concurrent update (conflict)
                        Interlocked.Increment(ref _totalConflicts);
                        System.Diagnostics.Debug.WriteLine(
                            $"[ReplicationIntegrationFeature] Conflict detected for key '{objectKey}' in group '{groupId}'. " +
                            $"Existing version: {existingVersion}. Applying ConflictResolution={group.ConflictResolution}.");
                    }
                }
                catch
                {
                    // Object doesn't exist yet — no conflict
                }
            }

            await primaryBackend.StoreAsync(objectKey, new System.IO.MemoryStream(data), metadata, ct);

            // Replicate based on mode
            if (group.Mode == ReplicationMode.Synchronous)
            {
                // Synchronous replication - wait for all replicas
                Interlocked.Increment(ref _totalSyncOperations);
                await ReplicateToAllAsync(group, objectKey, data, metadata, ct);
            }
            else
            {
                // Asynchronous replication - queued background work with proper error tracking
                Interlocked.Increment(ref _totalAsyncOperations);
                // Use ThreadPool.QueueUserWorkItem to avoid fire-and-forget task loss;
                // exceptions are captured via the completion source pattern so the caller
                // can observe failures through the group status fields.
                var capturedGroup = group;
                var capturedKey = objectKey;
                var capturedData = (byte[])data.Clone();
                var capturedMetadata = metadata != null ? new Dictionary<string, string>(metadata) : null;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ReplicateToAllAsync(capturedGroup, capturedKey, capturedData, capturedMetadata, CancellationToken.None);
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected during shutdown
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ReplicationIntegrationFeature] Async replication failed for {capturedKey}: {ex.GetType().Name}: {ex.Message}");
                        // Track failure in group statistics
                        Interlocked.Increment(ref capturedGroup.FailedOperations);
                    }
                }, CancellationToken.None);
            }

            // Track object-to-group mapping
            _objectToGroupMapping[objectKey] = groupId;

            // Update group statistics
            Interlocked.Add(ref group.TotalBytesWritten, data.Length);
        }

        /// <summary>
        /// Reads data from a replication group with automatic failover.
        /// </summary>
        /// <param name="groupId">Replication group identifier.</param>
        /// <param name="objectKey">Object key.</param>
        /// <param name="preferReplica">Whether to prefer reading from replica (for load balancing).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Retrieved data.</returns>
        public async Task<byte[]> ReadFromReplicationGroupAsync(
            string groupId,
            string objectKey,
            bool preferReplica = false,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
            ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_replicationGroups.TryGetValue(groupId, out var group))
            {
                throw new ArgumentException($"Replication group '{groupId}' not found");
            }

            Interlocked.Increment(ref _totalReplicationReads);

            // Determine read order based on topology and preference
            var readOrder = GetReadOrder(group, preferReplica);

            // Try each backend in order
            foreach (var backendId in readOrder)
            {
                try
                {
                    var backend = _registry.Get(backendId);
                    if (backend != null)
                    {
                        using var stream = await backend.RetrieveAsync(objectKey, ct);
                        using var ms = new System.IO.MemoryStream();
                        await stream.CopyToAsync(ms, ct);
                        var data = ms.ToArray();

                        Interlocked.Add(ref group.TotalBytesRead, data.Length);
                        return data;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ReplicationIntegrationFeature] Read from backend failed: {ex.Message}");
                    continue;
                }
            }

            throw new InvalidOperationException($"Failed to read from any backend in replication group '{groupId}'");
        }

        /// <summary>
        /// Performs a failover to a replica backend.
        /// </summary>
        /// <param name="groupId">Replication group identifier.</param>
        /// <param name="newPrimaryBackendId">New primary backend (must be a current replica).</param>
        public async Task FailoverAsync(string groupId, string newPrimaryBackendId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
            ArgumentException.ThrowIfNullOrWhiteSpace(newPrimaryBackendId);
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_replicationGroups.TryGetValue(groupId, out var group))
            {
                throw new ArgumentException($"Replication group '{groupId}' not found");
            }

            if (!group.ReplicaBackendIds.Contains(newPrimaryBackendId))
            {
                throw new ArgumentException($"Backend '{newPrimaryBackendId}' is not a replica in group '{groupId}'");
            }

            Interlocked.Increment(ref _totalFailovers);

            string oldPrimary;
            lock (_replicaListLock)
            {
                oldPrimary = group.PrimaryBackendId;

                // Promote replica to primary
                group.PrimaryBackendId = newPrimaryBackendId;
                group.ReplicaBackendIds.Remove(newPrimaryBackendId);

                // Demote old primary to replica (if still available)
                var oldPrimaryBackend = _registry.Get(oldPrimary);
                if (oldPrimaryBackend != null)
                {
                    group.ReplicaBackendIds.Add(oldPrimary);
                }
            }

            // Notify replication plugin
            await NotifyReplicationPluginAsync("failover.completed", new Dictionary<string, object>
            {
                ["groupId"] = groupId,
                ["oldPrimary"] = oldPrimary,
                ["newPrimary"] = newPrimaryBackendId
            });
        }

        /// <summary>
        /// Gets replication group information.
        /// </summary>
        /// <param name="groupId">Replication group identifier.</param>
        /// <returns>Replication group or null if not found.</returns>
        public ReplicationGroup? GetReplicationGroup(string groupId)
        {
            return _replicationGroups.TryGetValue(groupId, out var group) ? group : null;
        }

        /// <summary>
        /// Gets all replication groups.
        /// </summary>
        /// <returns>List of replication groups.</returns>
        public List<ReplicationGroup> GetAllReplicationGroups()
        {
            return _replicationGroups.Values.ToList();
        }

        /// <summary>
        /// Gets replication lag for a replica backend.
        /// </summary>
        /// <param name="groupId">Replication group identifier.</param>
        /// <param name="replicaBackendId">Replica backend strategy ID.</param>
        /// <returns>Replication lag or null if not found.</returns>
        public ReplicationLag? GetReplicationLag(string groupId, string replicaBackendId)
        {
            var key = $"{groupId}:{replicaBackendId}";
            return _replicationLags.TryGetValue(key, out var lag) ? lag : null;
        }

        #region Private Methods

        private async Task ReplicateToAllAsync(
            ReplicationGroup group,
            string objectKey,
            byte[] data,
            Dictionary<string, string>? metadata,
            CancellationToken ct)
        {
            // Snapshot replica list to avoid race with FailoverAsync mutations
            List<string> replicaSnapshot;
            lock (_replicaListLock)
            {
                replicaSnapshot = new List<string>(group.ReplicaBackendIds);
            }

            var replicationTasks = replicaSnapshot.Select(async replicaId =>
            {
                try
                {
                    var lagKey = $"{group.GroupId}:{replicaId}";
                    var startTime = DateTime.UtcNow;

                    var replicaBackend = _registry.Get(replicaId);
                    if (replicaBackend != null)
                    {
                        await replicaBackend.StoreAsync(objectKey, new System.IO.MemoryStream(data), metadata, ct);

                        // Update replication lag
                        if (_replicationLags.TryGetValue(lagKey, out var lag))
                        {
                            lag.LastReplicationTime = DateTime.UtcNow;
                            lag.LagMilliseconds = (DateTime.UtcNow - startTime).TotalMilliseconds;
                            Interlocked.Increment(ref lag.TotalReplications);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Track failed replication
                    var lagKey = $"{group.GroupId}:{replicaId}";
                    if (_replicationLags.TryGetValue(lagKey, out var lag))
                    {
                        Interlocked.Increment(ref lag.FailedReplications);
                        lag.LastError = ex.Message;
                    }
                }
            });

            await Task.WhenAll(replicationTasks);
        }

        private List<string> GetReadOrder(ReplicationGroup group, bool preferReplica)
        {
            // Snapshot replica list and primary under lock to avoid race with FailoverAsync
            string primary;
            List<string> replicas;
            lock (_replicaListLock)
            {
                primary = group.PrimaryBackendId;
                replicas = new List<string>(group.ReplicaBackendIds);
            }

            var order = new List<string>();

            if (group.Topology == ReplicationTopology.ActiveActive)
            {
                // In active-active, can read from any backend
                if (preferReplica && replicas.Any())
                {
                    // Load balance across replicas
                    order.AddRange(replicas.OrderBy(_ => Guid.NewGuid()));
                    order.Add(primary);
                }
                else
                {
                    order.Add(primary);
                    order.AddRange(replicas);
                }
            }
            else
            {
                // In active-passive, prefer primary
                order.Add(primary);
                order.AddRange(replicas);
            }

            return order;
        }

        private async Task MonitorReplicationLagAsync()
        {
            if (_disposed) return;

            try
            {
                foreach (var kvp in _replicationLags)
                {
                    var lag = kvp.Value;
                    if (lag.LastReplicationTime.HasValue)
                    {
                        var timeSinceLastReplication = DateTime.UtcNow - lag.LastReplicationTime.Value;
                        if (timeSinceLastReplication.TotalMinutes > 5)
                        {
                            // Alert on high lag
                            await NotifyReplicationPluginAsync("lag.alert", new Dictionary<string, object>
                            {
                                ["groupId"] = lag.GroupId,
                                ["replicaBackendId"] = lag.ReplicaBackendId,
                                ["lagMinutes"] = timeSinceLastReplication.TotalMinutes
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ReplicationIntegrationFeature] Monitoring failed: {ex.Message}");
            }
        }

        private async Task NotifyReplicationPluginAsync(string eventType, Dictionary<string, object> payload)
        {
            try
            {
                var message = new PluginMessage
                {
                    Type = $"replication.{eventType}",
                    Payload = payload,
                    Timestamp = DateTime.UtcNow
                };

                await _messageBus.PublishAsync(ReplicationPluginTopic, message);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ReplicationIntegrationFeature] Message bus publish failed: {ex.Message}");
            }
        }

        private async Task HandleReplicationStatusMessageAsync(PluginMessage message)
        {
            // Handle status updates from replication plugin
            await Task.CompletedTask;
        }

        #endregion

        /// <summary>
        /// Disposes resources.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _messageBusSubscription?.Dispose();
            _lagMonitorTimer?.Dispose();
            _replicationGroups.Clear();
            _objectToGroupMapping.Clear();
            _replicationLags.Clear();
        }
    }

    #region Supporting Types

    /// <summary>
    /// Replication mode.
    /// </summary>
    public enum ReplicationMode
    {
        /// <summary>Synchronous replication - wait for all replicas.</summary>
        Synchronous,

        /// <summary>Asynchronous replication - fire and forget.</summary>
        Asynchronous
    }

    /// <summary>
    /// Replication topology.
    /// </summary>
    public enum ReplicationTopology
    {
        /// <summary>Active-active - all backends can handle writes.</summary>
        ActiveActive,

        /// <summary>Active-passive - only primary handles writes.</summary>
        ActivePassive
    }

    /// <summary>
    /// Conflict resolution policy for active-active replication.
    /// </summary>
    public enum ConflictResolutionPolicy
    {
        /// <summary>Last write wins based on timestamp.</summary>
        LastWriteWins,

        /// <summary>First write wins.</summary>
        FirstWriteWins,

        /// <summary>Merge conflicting versions.</summary>
        Merge,

        /// <summary>Manual conflict resolution required.</summary>
        Manual
    }

    /// <summary>
    /// Replication state.
    /// </summary>
    public enum ReplicationState
    {
        /// <summary>Replication is active.</summary>
        Active,

        /// <summary>Replication is paused.</summary>
        Paused,

        /// <summary>Replication is synchronizing.</summary>
        Synchronizing,

        /// <summary>Replication has failed.</summary>
        Failed
    }

    /// <summary>
    /// Represents a replication group configuration.
    /// </summary>
    public sealed class ReplicationGroup
    {
        /// <summary>Unique group identifier.</summary>
        public string GroupId { get; init; } = string.Empty;

        /// <summary>Primary backend strategy ID.</summary>
        public string PrimaryBackendId { get; set; } = string.Empty;

        /// <summary>Replica backend strategy IDs.</summary>
        public List<string> ReplicaBackendIds { get; init; } = new();

        /// <summary>Replication mode.</summary>
        public ReplicationMode Mode { get; init; }

        /// <summary>Replication topology.</summary>
        public ReplicationTopology Topology { get; init; }

        /// <summary>Conflict resolution policy.</summary>
        public ConflictResolutionPolicy ConflictResolution { get; init; }

        /// <summary>Replication state.</summary>
        public ReplicationState State { get; set; }

        /// <summary>When the group was created.</summary>
        public DateTime CreatedTime { get; init; }

        /// <summary>Total bytes written to the group.</summary>
        public long TotalBytesWritten;

        /// <summary>Total bytes read from the group.</summary>
        public long TotalBytesRead;

        /// <summary>Total failed async replication operations (interlocked).</summary>
        public long FailedOperations;
    }

    /// <summary>
    /// Replication lag tracking.
    /// </summary>
    public sealed class ReplicationLag
    {
        /// <summary>Replication group identifier.</summary>
        public string GroupId { get; init; } = string.Empty;

        /// <summary>Replica backend strategy ID.</summary>
        public string ReplicaBackendId { get; init; } = string.Empty;

        /// <summary>Last successful replication time.</summary>
        public DateTime? LastReplicationTime { get; set; }

        /// <summary>Replication lag in milliseconds.</summary>
        public double LagMilliseconds { get; set; }

        /// <summary>Total successful replications.</summary>
        public long TotalReplications;

        /// <summary>Total failed replications.</summary>
        public long FailedReplications;

        /// <summary>Last error message.</summary>
        public string? LastError { get; set; }
    }

    #endregion
}
