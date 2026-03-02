using System;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Federation;

/// <summary>
/// Deployment mode for the federated VDE.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Federation Options (VFED-14)")]
public enum FederationMode : byte
{
    /// <summary>Single VDE instance, zero-overhead passthrough (no hashing, no routing).</summary>
    SingleVde = 0,

    /// <summary>Multiple VDE instances with path-based routing and cross-shard operations.</summary>
    Federated = 1,

    /// <summary>Hierarchical federation: federations of federations for planet-scale deployments.</summary>
    SuperFederated = 2
}

/// <summary>
/// Configuration for federated VDE: deployment mode, timeouts, parallelism, and pagination.
/// Default values target single-VDE passthrough (zero overhead).
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Federation Options (VFED-14)")]
public sealed record FederationOptions
{
    /// <summary>
    /// Deployment mode. Default <see cref="FederationMode.SingleVde"/> delegates directly with no routing overhead.
    /// </summary>
    public FederationMode Mode { get; init; } = FederationMode.SingleVde;

    /// <summary>
    /// Per-shard operation timeout for Store/Retrieve/Delete/Exists/GetMetadata.
    /// </summary>
    public TimeSpan ShardOperationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum concurrent shard operations during fan-out (List/Search).
    /// Controls the degree of parallelism when querying multiple shards simultaneously.
    /// </summary>
    public int MaxConcurrentShardOperations { get; init; } = 16;

    /// <summary>
    /// Default page size for cross-shard List operations.
    /// The caller can break the <see cref="System.Collections.Generic.IAsyncEnumerable{T}"/> enumeration
    /// at any point to implement custom page boundaries.
    /// </summary>
    public int ListPageSize { get; init; } = 1000;

    /// <summary>
    /// Maximum total results returned across all shards for cross-shard Search.
    /// Prevents unbounded result sets from consuming excessive memory.
    /// </summary>
    public int SearchMaxResults { get; init; } = 10_000;

    /// <summary>
    /// Whether cross-shard List/Search support cursor-based pagination.
    /// When false, results are streamed in a single pass.
    /// </summary>
    public bool EnableCrossShardPagination { get; init; } = true;

    /// <summary>
    /// Total timeout for a complete fan-out + merge operation (List/Search across all shards).
    /// Must be greater than or equal to <see cref="ShardOperationTimeout"/>.
    /// </summary>
    public TimeSpan CrossShardMergeTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Maximum retry attempts per shard for failed operations.
    /// Each retry uses exponential backoff with jitter.
    /// </summary>
    public int MaxRetryPerShard { get; init; } = 2;

    /// <summary>
    /// Validates option combinations and throws <see cref="ArgumentException"/> for invalid configurations.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when any option is invalid.</exception>
    public void Validate()
    {
        if (MaxConcurrentShardOperations < 1)
            throw new ArgumentException(
                $"MaxConcurrentShardOperations must be at least 1, got {MaxConcurrentShardOperations}.",
                nameof(MaxConcurrentShardOperations));

        if (ListPageSize < 1)
            throw new ArgumentException(
                $"ListPageSize must be at least 1, got {ListPageSize}.",
                nameof(ListPageSize));

        if (SearchMaxResults < 1)
            throw new ArgumentException(
                $"SearchMaxResults must be at least 1, got {SearchMaxResults}.",
                nameof(SearchMaxResults));

        if (ShardOperationTimeout <= TimeSpan.Zero)
            throw new ArgumentException(
                "ShardOperationTimeout must be positive.",
                nameof(ShardOperationTimeout));

        if (CrossShardMergeTimeout <= TimeSpan.Zero)
            throw new ArgumentException(
                "CrossShardMergeTimeout must be positive.",
                nameof(CrossShardMergeTimeout));

        if (CrossShardMergeTimeout < ShardOperationTimeout)
            throw new ArgumentException(
                "CrossShardMergeTimeout must be >= ShardOperationTimeout.",
                nameof(CrossShardMergeTimeout));

        if (MaxRetryPerShard < 0)
            throw new ArgumentException(
                $"MaxRetryPerShard must be non-negative, got {MaxRetryPerShard}.",
                nameof(MaxRetryPerShard));
    }
}
