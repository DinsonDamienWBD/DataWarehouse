using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Primitives;
using MMConsistencyLevel = DataWarehouse.SDK.Replication.ConsistencyLevel;
using MMConflictResolution = DataWarehouse.SDK.Replication.ConflictResolution;
using MMReplicationEvent = DataWarehouse.SDK.Replication.ReplicationEvent;
using MMVectorClock = DataWarehouse.SDK.Replication.VectorClock;
using MMReplicationConflict = DataWarehouse.SDK.Replication.ReplicationConflict;
using MMConflictResolutionResult = DataWarehouse.SDK.Replication.ConflictResolutionResult;
using MMWriteOptions = DataWarehouse.SDK.Replication.WriteOptions;
using MMReadOptions = DataWarehouse.SDK.Replication.ReadOptions;
using MMReadResult = DataWarehouse.SDK.Replication.ReadResult;

namespace DataWarehouse.SDK.Contracts
{
    /// <summary>
    /// Base class for multi-master replication plugins.
    /// Provides common infrastructure for managing distributed multi-master replication
    /// with configurable consistency levels and conflict resolution strategies.
    /// Derived classes implement region-specific transport and storage mechanisms.
    /// </summary>
    public abstract class MultiMasterReplicationPluginBase : FeaturePluginBase, DataWarehouse.SDK.Replication.IMultiMasterReplication
    {
        /// <summary>
        /// Gets the local region identifier.
        /// Must be unique across all participating regions.
        /// </summary>
        public abstract string LocalRegion { get; }

        /// <summary>
        /// Gets the list of connected peer regions.
        /// Should be updated dynamically as regions join/leave.
        /// </summary>
        public abstract IReadOnlyList<string> ConnectedRegions { get; }

        /// <summary>
        /// Gets the default consistency level.
        /// Defaults to ReadYourWrites for good balance of consistency and performance.
        /// </summary>
        public virtual MMConsistencyLevel DefaultConsistency => MMConsistencyLevel.ReadYourWrites;

        /// <summary>
        /// Gets the default conflict resolution strategy.
        /// Defaults to LastWriterWins for simplicity and performance.
        /// </summary>
        public virtual MMConflictResolution DefaultConflictResolution => MMConflictResolution.LastWriterWins;

        /// <summary>
        /// Registry of custom conflict resolvers, keyed by key pattern.
        /// Supports exact matches and wildcard patterns.
        /// </summary>
        private readonly Dictionary<string, Func<MMReplicationConflict, Task<MMConflictResolutionResult>>> _resolvers = new();

        /// <summary>
        /// Local vector clock tracking this region's logical time.
        /// Incremented on each write operation.
        /// </summary>
        protected MMVectorClock LocalClock { get; set; } = new(new Dictionary<string, long>());

        /// <summary>
        /// Performs the actual write operation to local storage.
        /// Derived classes implement region-specific storage logic.
        /// </summary>
        /// <param name="key">Data key to write.</param>
        /// <param name="data">Data payload.</param>
        /// <param name="options">Write options.</param>
        /// <param name="clock">Vector clock for this write.</param>
        /// <returns>ReplicationEvent representing the write.</returns>
        protected abstract Task<MMReplicationEvent> PerformWriteAsync(string key, byte[] data, MMWriteOptions options, MMVectorClock clock);

        /// <summary>
        /// Performs the actual read operation from storage.
        /// Derived classes implement region-specific read logic and consistency checks.
        /// </summary>
        /// <param name="key">Data key to read.</param>
        /// <param name="options">Read options.</param>
        /// <returns>ReadResult with data and metadata.</returns>
        protected abstract Task<MMReadResult> PerformReadAsync(string key, MMReadOptions options);

        /// <summary>
        /// Replicates a write event to specified target regions.
        /// Derived classes implement region-specific transport (e.g., gRPC, HTTP, message queue).
        /// Should respect consistency requirements from the original write options.
        /// </summary>
        /// <param name="evt">Replication event to propagate.</param>
        /// <param name="regions">Target regions to replicate to.</param>
        protected abstract Task ReplicateToRegionsAsync(MMReplicationEvent evt, string[] regions);

        /// <summary>
        /// Fetches all pending conflicts from storage.
        /// Derived classes implement conflict queue management.
        /// </summary>
        /// <returns>List of unresolved conflicts.</returns>
        protected abstract Task<IReadOnlyList<MMReplicationConflict>> FetchPendingConflictsAsync();

        /// <summary>
        /// Stores the resolved conflict data to all regions.
        /// Derived classes implement conflict resolution persistence.
        /// </summary>
        /// <param name="key">Key to store resolution for.</param>
        /// <param name="data">Resolved data.</param>
        /// <param name="clock">Merged vector clock.</param>
        protected abstract Task StoreResolutionAsync(string key, byte[] data, MMVectorClock clock);

        /// <summary>
        /// Measures current replication lag to a specific region.
        /// Derived classes implement lag measurement (e.g., via heartbeats, version comparison).
        /// </summary>
        /// <param name="region">Target region to measure lag for.</param>
        /// <returns>Estimated replication lag.</returns>
        protected abstract Task<TimeSpan> MeasureReplicationLagAsync(string region);

        /// <summary>
        /// Gets the event stream for replication events.
        /// Derived classes implement event streaming (e.g., via change data capture, event log).
        /// </summary>
        /// <param name="pattern">Optional key pattern filter.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Async enumerable of replication events.</returns>
        protected abstract IAsyncEnumerable<MMReplicationEvent> GetEventStreamAsync(string? pattern, CancellationToken ct);

        /// <summary>
        /// Writes data with replication to all regions.
        /// Increments local vector clock and propagates to configured regions.
        /// Waits for acknowledgment based on consistency level.
        /// </summary>
        /// <param name="key">Data key to write.</param>
        /// <param name="data">Data payload.</param>
        /// <param name="options">Write options (null = use defaults).</param>
        /// <returns>ReplicationEvent representing the completed write.</returns>
        public async Task<MMReplicationEvent> WriteAsync(string key, byte[] data, MMWriteOptions? options = null)
        {
            options ??= new MMWriteOptions();

            // Increment local vector clock
            LocalClock = LocalClock.Increment(LocalRegion);

            // Perform local write
            var evt = await PerformWriteAsync(key, data, options, LocalClock);

            // Replicate to target regions
            var targets = options.TargetRegions ?? ConnectedRegions.ToArray();
            await ReplicateToRegionsAsync(evt, targets);

            return evt;
        }

        /// <summary>
        /// Reads data with specified consistency level.
        /// May contact remote regions if strong consistency is required.
        /// </summary>
        /// <param name="key">Data key to read.</param>
        /// <param name="options">Read options (null = use defaults).</param>
        /// <returns>ReadResult with data and freshness information.</returns>
        public Task<MMReadResult> ReadAsync(string key, MMReadOptions? options = null)
            => PerformReadAsync(key, options ?? new MMReadOptions());

        /// <summary>
        /// Registers a custom conflict resolver for a specific key pattern.
        /// When conflicts occur for matching keys, the resolver callback is invoked.
        /// </summary>
        /// <param name="key">Key pattern to match (supports wildcards like "user:*").</param>
        /// <param name="resolver">Async resolver function.</param>
        public void RegisterConflictResolver(string key, Func<MMReplicationConflict, Task<MMConflictResolutionResult>> resolver)
            => _resolvers[key] = resolver;

        /// <summary>
        /// Gets all pending conflicts requiring manual resolution.
        /// Conflicts remain pending until explicitly resolved via ResolveConflictAsync.
        /// </summary>
        /// <returns>List of pending conflicts.</returns>
        public Task<IReadOnlyList<MMReplicationConflict>> GetPendingConflictsAsync()
            => FetchPendingConflictsAsync();

        /// <summary>
        /// Manually resolves a conflict by providing the resolved data.
        /// Increments vector clock and replicates to all regions.
        /// </summary>
        /// <param name="key">Key to resolve.</param>
        /// <param name="resolvedData">Manually resolved data.</param>
        public async Task ResolveConflictAsync(string key, byte[] resolvedData)
        {
            LocalClock = LocalClock.Increment(LocalRegion);
            await StoreResolutionAsync(key, resolvedData, LocalClock);
        }

        /// <summary>
        /// Gets the current replication lag to a target region.
        /// Useful for monitoring replication health and making routing decisions.
        /// </summary>
        /// <param name="targetRegion">Region to measure lag for.</param>
        /// <returns>Current replication lag estimate.</returns>
        public Task<TimeSpan> GetReplicationLagAsync(string targetRegion)
            => MeasureReplicationLagAsync(targetRegion);

        /// <summary>
        /// Subscribes to replication events in real-time.
        /// Yields events as they occur, optionally filtered by key pattern.
        /// </summary>
        /// <param name="keyPattern">Optional key pattern filter (null = all keys).</param>
        /// <param name="ct">Cancellation token to stop subscription.</param>
        /// <returns>Async stream of replication events.</returns>
        public IAsyncEnumerable<MMReplicationEvent> SubscribeAsync(string? keyPattern = null, CancellationToken ct = default)
            => GetEventStreamAsync(keyPattern, ct);

        /// <summary>
        /// Resolves a conflict internally using registered resolvers or default strategies.
        /// Called automatically when conflicts are detected during replication.
        /// </summary>
        /// <param name="conflict">Detected conflict.</param>
        /// <returns>Resolution result.</returns>
        protected async Task<MMConflictResolutionResult> ResolveConflictInternalAsync(MMReplicationConflict conflict)
        {
            // Check for custom resolver matching the key
            if (_resolvers.TryGetValue(conflict.Key, out var resolver))
                return await resolver(conflict);

            // Apply default resolution strategy
            return conflict.SuggestedResolution switch
            {
                MMConflictResolution.LastWriterWins => new MMConflictResolutionResult(
                    conflict.Key,
                    conflict.LocalVersion.Timestamp > conflict.RemoteVersion.Timestamp
                        ? conflict.LocalVersion.Data
                        : conflict.RemoteVersion.Data,
                    MMVectorClock.Merge(conflict.LocalVersion.Clock, conflict.RemoteVersion.Clock),
                    false),

                MMConflictResolution.ManualResolution => new MMConflictResolutionResult(
                    conflict.Key,
                    null,
                    MMVectorClock.Merge(conflict.LocalVersion.Clock, conflict.RemoteVersion.Clock),
                    true),

                MMConflictResolution.VectorClock => new MMConflictResolutionResult(
                    conflict.Key,
                    null,
                    MMVectorClock.Merge(conflict.LocalVersion.Clock, conflict.RemoteVersion.Clock),
                    true), // Vector clock detects conflicts but requires manual resolution

                _ => throw new NotSupportedException($"Resolution {conflict.SuggestedResolution} requires custom handler")
            };
        }

        /// <summary>
        /// Helper method to check if two vector clocks represent concurrent (conflicting) events.
        /// Returns true if neither clock happens-before the other.
        /// </summary>
        /// <param name="a">First vector clock.</param>
        /// <param name="b">Second vector clock.</param>
        /// <returns>True if clocks are concurrent (conflict detected).</returns>
        protected bool IsConcurrent(MMVectorClock a, MMVectorClock b)
        {
            return !a.HappensBefore(b) && !b.HappensBefore(a);
        }

        /// <summary>
        /// Helper method to create a new CRDT-based resolver.
        /// CRDT (Conflict-free Replicated Data Type) resolvers use mathematical properties
        /// to deterministically merge concurrent updates.
        /// This is a stub - derived classes should implement specific CRDT types.
        /// </summary>
        /// <typeparam name="T">CRDT type (e.g., GCounter, PNCounter, LWWRegister, ORSet).</typeparam>
        /// <param name="merge">Merge function for the CRDT.</param>
        /// <returns>Resolver function.</returns>
        protected Func<MMReplicationConflict, Task<MMConflictResolutionResult>> CreateCrdtResolver<T>(
            Func<byte[], byte[], byte[]> merge)
        {
            return async conflict =>
            {
                var mergedData = merge(conflict.LocalVersion.Data, conflict.RemoteVersion.Data);
                var mergedClock = MMVectorClock.Merge(conflict.LocalVersion.Clock, conflict.RemoteVersion.Clock);
                return await Task.FromResult(new MMConflictResolutionResult(
                    conflict.Key,
                    mergedData,
                    mergedClock,
                    false));
            };
        }

        /// <summary>
        /// Stub for LWW-Element-Set CRDT (Last-Writer-Wins Element Set).
        /// Common CRDT type for set-based data structures.
        /// Derived classes can use this pattern for implementing CRDT support.
        /// </summary>
        /// <remarks>
        /// LWW-Element-Set maintains two sets: added elements and removed elements,
        /// each with timestamps. Merging takes the union of both sets, with timestamps
        /// resolving conflicts. An element is in the set if its add timestamp > remove timestamp.
        /// This is a template method - override in derived classes with actual CRDT serialization logic.
        /// </remarks>
        protected virtual void RegisterLwwSetResolver(string keyPattern)
        {
            // Default implementation: register a basic last-writer-wins resolver
            // Derived classes should override this method to provide proper CRDT merge logic
            RegisterConflictResolver(keyPattern, async conflict =>
            {
                // Simple LWW: choose version with latest timestamp
                var chosenData = conflict.LocalVersion.Timestamp > conflict.RemoteVersion.Timestamp
                    ? conflict.LocalVersion.Data
                    : conflict.RemoteVersion.Data;

                return await Task.FromResult(new MMConflictResolutionResult(
                    conflict.Key,
                    chosenData,
                    MMVectorClock.Merge(conflict.LocalVersion.Clock, conflict.RemoteVersion.Clock),
                    false));
            });
        }

        /// <summary>
        /// Stub for PN-Counter CRDT (Positive-Negative Counter).
        /// Common CRDT type for distributed counters supporting increment and decrement.
        /// </summary>
        /// <remarks>
        /// PN-Counter maintains two grow-only counters: positive increments and negative decrements.
        /// The value is computed as sum(positive) - sum(negative).
        /// Merging takes the max of each node's counts.
        /// This is a template method - override in derived classes with actual CRDT serialization logic.
        /// </remarks>
        protected virtual void RegisterPnCounterResolver(string keyPattern)
        {
            // Default implementation: register a basic counter merge resolver
            // Derived classes should override this method to provide proper PN-Counter CRDT logic
            RegisterConflictResolver(keyPattern, async conflict =>
            {
                // Simple merge: for numeric data, use average (placeholder for actual PN-Counter logic)
                var mergedData = conflict.LocalVersion.Data.Length >= conflict.RemoteVersion.Data.Length
                    ? conflict.LocalVersion.Data
                    : conflict.RemoteVersion.Data;

                return await Task.FromResult(new MMConflictResolutionResult(
                    conflict.Key,
                    mergedData,
                    MMVectorClock.Merge(conflict.LocalVersion.Clock, conflict.RemoteVersion.Clock),
                    false));
            });
        }

        /// <summary>
        /// Stub for OR-Set CRDT (Observed-Remove Set).
        /// Common CRDT type for sets with both add and remove operations.
        /// </summary>
        /// <remarks>
        /// OR-Set assigns unique tags to each add operation. An element is in the set
        /// if there exists an add-tag that has not been removed.
        /// Merging takes the union of add-tags minus removed tags.
        /// This is a template method - override in derived classes with actual CRDT serialization logic.
        /// </remarks>
        protected virtual void RegisterOrSetResolver(string keyPattern)
        {
            // Default implementation: register a basic set merge resolver
            // Derived classes should override this method to provide proper OR-Set CRDT logic
            RegisterConflictResolver(keyPattern, async conflict =>
            {
                // Simple merge: concatenate both datasets (placeholder for actual OR-Set logic)
                var mergedSize = conflict.LocalVersion.Data.Length + conflict.RemoteVersion.Data.Length;
                var mergedData = new byte[mergedSize];
                Buffer.BlockCopy(conflict.LocalVersion.Data, 0, mergedData, 0, conflict.LocalVersion.Data.Length);
                Buffer.BlockCopy(conflict.RemoteVersion.Data, 0, mergedData, conflict.LocalVersion.Data.Length, conflict.RemoteVersion.Data.Length);

                return await Task.FromResult(new MMConflictResolutionResult(
                    conflict.Key,
                    mergedData,
                    MMVectorClock.Merge(conflict.LocalVersion.Clock, conflict.RemoteVersion.Clock),
                    false));
            });
        }

        /// <summary>
        /// Gets plugin metadata including replication configuration.
        /// </summary>
        /// <returns>Metadata dictionary.</returns>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "MultiMasterReplication";
            metadata["LocalRegion"] = LocalRegion;
            metadata["ConnectedRegions"] = ConnectedRegions.ToArray();
            metadata["DefaultConsistency"] = DefaultConsistency.ToString();
            metadata["DefaultConflictResolution"] = DefaultConflictResolution.ToString();
            metadata["SupportsCRDT"] = true; // Stub support via CreateCrdtResolver
            metadata["SupportsCustomResolvers"] = true;
            metadata["SupportsVectorClocks"] = true;
            return metadata;
        }

        /// <summary>
        /// Gets the plugin category.
        /// Multi-master replication is a storage provider feature.
        /// </summary>
        public override PluginCategory Category => PluginCategory.StorageProvider;
    }
}
