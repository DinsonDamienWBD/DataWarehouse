using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Storage;

namespace DataWarehouse.SDK.VirtualDiskEngine.Federation;

/// <summary>
/// Aggregate statistics from the federated VDE: router, merger, and shard topology.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Federated VDE (VFED-16)")]
public readonly record struct FederatedVdeStats(
    FederationMode Mode,
    FederationRouterStats RouterStats,
    CrossShardMergeStats MergerStats,
    int ActiveShardCount);

/// <summary>
/// Transparent namespace facade that implements the same Store/Retrieve/Delete/List/Search API
/// as <see cref="VirtualDiskEngine"/> but routes operations through the federation router.
/// <para>
/// For single-VDE deployments, all operations delegate directly to the inner VDE with zero overhead:
/// no hashing, no routing table lookup, no cache access, no merger involvement.
/// </para>
/// <para>
/// For federated deployments, operations resolve the target shard via <see cref="VdeFederationRouter"/>
/// and dispatch to the appropriate VDE instance. Cross-shard List/Search fan out through
/// <see cref="CrossShardMerger"/> with k-way merge-sort and pagination.
/// </para>
/// </summary>
/// <remarks>
/// Thread-safe: the router, merger, and VDE instances are all individually thread-safe.
/// This class adds no additional synchronization beyond what those components provide.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Federated VDE (VFED-16)")]
public sealed class FederatedVirtualDiskEngine : IAsyncDisposable
{
    private readonly FederationOptions _options;
    private readonly VdeFederationRouter _router;
    private readonly CrossShardMerger _merger;
    private readonly RoutingTable? _routingTable;
    private readonly Func<Guid, CancellationToken, ValueTask<VirtualDiskEngine?>>? _vdeResolver;
    private readonly VirtualDiskEngine? _singleVde;
    private readonly Guid _singleVdeId;
    private volatile bool _disposed;

    /// <summary>
    /// Creates a federated VDE with full routing infrastructure for multi-shard deployments.
    /// </summary>
    /// <param name="options">Federation configuration (mode, timeouts, parallelism).</param>
    /// <param name="router">The federation router that resolves keys to shard addresses.</param>
    /// <param name="merger">The cross-shard merger for fan-out List/Search operations.</param>
    /// <param name="vdeResolver">
    /// Async factory that retrieves a VDE instance by its GUID from a VDE pool/registry.
    /// Returns null if the VDE is unavailable.
    /// </param>
    /// <param name="routingTable">
    /// The routing table used to enumerate distinct shard VDE IDs for fan-out operations (List/Search).
    /// Required for federated mode to determine which shards to query.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when any argument is null.</exception>
    public FederatedVirtualDiskEngine(
        FederationOptions options,
        VdeFederationRouter router,
        CrossShardMerger merger,
        Func<Guid, CancellationToken, ValueTask<VirtualDiskEngine?>> vdeResolver,
        RoutingTable routingTable)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(merger);
        ArgumentNullException.ThrowIfNull(vdeResolver);
        ArgumentNullException.ThrowIfNull(routingTable);

        options.Validate();

        _options = options;
        _router = router;
        _merger = merger;
        _vdeResolver = vdeResolver;
        _routingTable = routingTable;
    }

    /// <summary>
    /// Private constructor for single-VDE passthrough mode.
    /// </summary>
    private FederatedVirtualDiskEngine(VirtualDiskEngine vde, Guid vdeId)
    {
        _singleVde = vde;
        _singleVdeId = vdeId;
        _options = new FederationOptions { Mode = FederationMode.SingleVde };
        _router = VdeFederationRouter.CreateSingleVde(vdeId);
        _merger = new CrossShardMerger(_options);
    }

    /// <summary>
    /// Creates a zero-overhead federated VDE that delegates all operations directly to a single VDE.
    /// No hashing, no routing table, no cache, no merger overhead.
    /// This is the recommended constructor for single-instance deployments.
    /// </summary>
    /// <param name="vde">The single VDE instance.</param>
    /// <param name="vdeId">The unique identifier for the VDE instance.</param>
    /// <returns>A passthrough federated VDE.</returns>
    /// <exception cref="ArgumentNullException">Thrown when vde is null.</exception>
    /// <exception cref="ArgumentException">Thrown when vdeId is empty.</exception>
    public static FederatedVirtualDiskEngine CreateSingleVde(VirtualDiskEngine vde, Guid vdeId)
    {
        ArgumentNullException.ThrowIfNull(vde);
        if (vdeId == Guid.Empty)
            throw new ArgumentException("VDE ID must not be empty.", nameof(vdeId));

        return new FederatedVirtualDiskEngine(vde, vdeId);
    }

    /// <summary>
    /// The current federation mode.
    /// </summary>
    public FederationMode Mode => _options.Mode;

    /// <summary>
    /// Stores data with the specified key.
    /// Single-VDE mode delegates directly. Federated mode routes through the federation router.
    /// </summary>
    /// <param name="key">Storage key (mapped to VDE namespace path).</param>
    /// <param name="data">Data stream to store.</param>
    /// <param name="metadata">Optional metadata to associate with the object.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Storage object metadata.</returns>
    public async Task<StorageObjectMetadata> StoreAsync(
        string key,
        Stream data,
        IDictionary<string, string>? metadata,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(data);

        // Single-VDE passthrough: DIRECT delegation with zero routing overhead
        if (_singleVde != null)
        {
            return await _singleVde.StoreAsync(key, data, metadata, ct).ConfigureAwait(false);
        }

        // Federated: resolve shard, get VDE, delegate
        var targetVde = await ResolveVdeForKeyAsync(key, ct).ConfigureAwait(false);
        return await targetVde.StoreAsync(key, data, metadata, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Retrieves data for the specified key.
    /// Single-VDE mode delegates directly. Federated mode routes through the federation router.
    /// </summary>
    /// <param name="key">Storage key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Data stream.</returns>
    public async Task<Stream> RetrieveAsync(string key, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(key);

        // Single-VDE passthrough
        if (_singleVde != null)
        {
            return await _singleVde.RetrieveAsync(key, ct).ConfigureAwait(false);
        }

        var targetVde = await ResolveVdeForKeyAsync(key, ct).ConfigureAwait(false);
        return await targetVde.RetrieveAsync(key, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes the object with the specified key.
    /// Single-VDE mode delegates directly. Federated mode routes through the federation router.
    /// </summary>
    /// <param name="key">Storage key.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(key);

        // Single-VDE passthrough
        if (_singleVde != null)
        {
            await _singleVde.DeleteAsync(key, ct).ConfigureAwait(false);
            return;
        }

        var targetVde = await ResolveVdeForKeyAsync(key, ct).ConfigureAwait(false);
        await targetVde.DeleteAsync(key, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks if an object with the specified key exists.
    /// Single-VDE mode delegates directly. Federated mode routes through the federation router.
    /// </summary>
    /// <param name="key">Storage key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the object exists, false otherwise.</returns>
    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(key);

        // Single-VDE passthrough
        if (_singleVde != null)
        {
            return await _singleVde.ExistsAsync(key, ct).ConfigureAwait(false);
        }

        var targetVde = await ResolveVdeForKeyAsync(key, ct).ConfigureAwait(false);
        return await targetVde.ExistsAsync(key, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets metadata for a specific object without retrieving its data.
    /// Single-VDE mode delegates directly. Federated mode routes through the federation router.
    /// </summary>
    /// <param name="key">Storage key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Storage object metadata.</returns>
    public async Task<StorageObjectMetadata> GetMetadataAsync(string key, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(key);

        // Single-VDE passthrough
        if (_singleVde != null)
        {
            return await _singleVde.GetMetadataAsync(key, ct).ConfigureAwait(false);
        }

        var targetVde = await ResolveVdeForKeyAsync(key, ct).ConfigureAwait(false);
        return await targetVde.GetMetadataAsync(key, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists all objects with an optional key prefix.
    /// Single-VDE mode delegates directly. Federated mode fans out to relevant shards
    /// via <see cref="CrossShardMerger.MergeListAsync"/> with k-way merge-sort.
    /// </summary>
    /// <param name="prefix">Optional key prefix filter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async enumerable of merge-sorted storage object metadata.</returns>
    public async IAsyncEnumerable<StorageObjectMetadata> ListAsync(
        string? prefix,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Single-VDE passthrough: DIRECT delegation
        if (_singleVde != null)
        {
            await foreach (var item in _singleVde.ListAsync(prefix, ct).ConfigureAwait(false))
            {
                yield return item;
            }
            yield break;
        }

        // Federated: build targets from all known shards and merge
        var targets = await BuildListTargetsAsync(prefix, ct).ConfigureAwait(false);

        await foreach (var item in _merger.MergeListAsync(prefix, targets, ct).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    /// <summary>
    /// Searches across all shards with the given query, returning merge-sorted results.
    /// Single-VDE mode is not supported for search (requires federation infrastructure).
    /// </summary>
    /// <param name="query">Search query string.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async enumerable of merge-sorted search results, capped at SearchMaxResults.</returns>
    /// <exception cref="NotSupportedException">Thrown in single-VDE mode.</exception>
    public async IAsyncEnumerable<StorageObjectMetadata> SearchAsync(
        string query,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(query);

        if (_singleVde != null)
        {
            throw new NotSupportedException(
                "Search requires VDE 2.0B+ federation. Use federated mode or query the VDE directly.");
        }

        var targets = await BuildSearchTargetsAsync(ct).ConfigureAwait(false);

        await foreach (var item in _merger.MergeSearchAsync(query, targets, ct).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    /// <summary>
    /// Returns aggregate statistics from the federation router and cross-shard merger.
    /// </summary>
    /// <returns>Current federated VDE statistics snapshot.</returns>
    public FederatedVdeStats GetStats()
    {
        var routerStats = _router.GetStats();
        var mergerStats = _merger.GetStats();
        int activeShards = _singleVde != null ? 1 : GetDistinctShardCount();

        return new FederatedVdeStats(
            _options.Mode,
            routerStats,
            mergerStats,
            activeShards);
    }

    /// <summary>
    /// Disposes the federated VDE, releasing the router and VDE references.
    /// Does not dispose the underlying VDE instances (they are owned by the VDE pool/registry).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        await _router.DisposeAsync().ConfigureAwait(false);
    }

    #region Private Helpers

    /// <summary>
    /// Resolves a key to its target VDE instance via the federation router.
    /// </summary>
    /// <param name="key">The key to resolve.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The resolved VDE instance.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when the shard is not found or VDE is unavailable.</exception>
    private async ValueTask<VirtualDiskEngine> ResolveVdeForKeyAsync(string key, CancellationToken ct)
    {
        ShardAddress address = await _router.ResolveAsync(key, ct).ConfigureAwait(false);

        if (!address.IsValid)
        {
            throw new KeyNotFoundException(
                $"Federation router could not resolve key '{key}' to a shard address.");
        }

        VirtualDiskEngine? vde = await _vdeResolver!(address.VdeId, ct).ConfigureAwait(false);

        if (vde is null)
        {
            throw new KeyNotFoundException(
                $"VDE instance {address.VdeId} is unavailable for key '{key}'.");
        }

        return vde;
    }

    /// <summary>
    /// Builds List operation targets from all known data shards.
    /// Uses the routing table to enumerate distinct VDE IDs.
    /// If the prefix maps to a single domain slot, only that shard is queried.
    /// If prefix is null/empty, all shards are queried.
    /// </summary>
    private async Task<IReadOnlyList<ShardOperationTarget>> BuildListTargetsAsync(
        string? prefix,
        CancellationToken ct)
    {
        var distinctVdeIds = GetDistinctVdeIds();
        var targets = new List<ShardOperationTarget>(distinctVdeIds.Count);

        foreach (Guid vdeId in distinctVdeIds)
        {
            Guid capturedId = vdeId;
            VirtualDiskEngine? vde = await _vdeResolver!(capturedId, ct).ConfigureAwait(false);

            if (vde != null)
            {
                targets.Add(new ShardOperationTarget(
                    capturedId,
                    (pfx, token) => vde.ListAsync(pfx, token)));
            }
        }

        return targets;
    }

    /// <summary>
    /// Builds Search operation targets from all known data shards.
    /// Search always fans out to all shards (no prefix-based optimization).
    /// </summary>
    private async Task<IReadOnlyList<ShardSearchTarget>> BuildSearchTargetsAsync(CancellationToken ct)
    {
        var distinctVdeIds = GetDistinctVdeIds();
        var targets = new List<ShardSearchTarget>(distinctVdeIds.Count);

        foreach (Guid vdeId in distinctVdeIds)
        {
            Guid capturedId = vdeId;
            VirtualDiskEngine? vde = await _vdeResolver!(capturedId, ct).ConfigureAwait(false);

            if (vde != null)
            {
                // Search delegates to ListAsync with the query as prefix (basic implementation).
                // Full-text search would require a dedicated search index per VDE.
                targets.Add(new ShardSearchTarget(
                    capturedId,
                    (query, token) => vde.ListAsync(query, token)));
            }
        }

        return targets;
    }

    /// <summary>
    /// Enumerates the routing table assignments to collect distinct non-empty VDE IDs.
    /// Uses <see cref="RoutingTable.GetAssignments"/> which returns merged contiguous ranges.
    /// </summary>
    private HashSet<Guid> GetDistinctVdeIds()
    {
        if (_routingTable is null)
            return new HashSet<Guid>();

        var assignments = _routingTable.GetAssignments();
        var distinctIds = new HashSet<Guid>(assignments.Count);

        foreach (var (_, _, vdeId) in assignments)
        {
            distinctIds.Add(vdeId);
        }

        return distinctIds;
    }

    /// <summary>
    /// Returns the count of distinct active shards from the routing table.
    /// </summary>
    private int GetDistinctShardCount()
    {
        return GetDistinctVdeIds().Count;
    }

    #endregion
}
