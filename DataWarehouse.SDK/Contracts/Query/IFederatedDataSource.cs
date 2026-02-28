using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Contracts.Query;

// ---------------------------------------------------------------
// Federated Data Source Types
// ---------------------------------------------------------------

/// <summary>
/// Classification of federated data sources by access pattern and location.
/// Used by the query planner to estimate network costs and determine
/// which operations can be pushed down to the source.
/// </summary>
public enum FederatedSourceType
{
    /// <summary>Local storage — NVMe, SSD, HDD on this node. Minimal latency.</summary>
    Local,

    /// <summary>Remote object storage — S3, Azure Blob, GCS. High latency, high bandwidth.</summary>
    RemoteStorage,

    /// <summary>Relational or NoSQL database — PostgreSQL, MySQL, Cassandra. Supports pushdown.</summary>
    Database,

    /// <summary>External REST/gRPC API — third-party data. Highest latency, limited pushdown.</summary>
    ExternalApi,

    /// <summary>Another DataWarehouse node in a federated cluster. Full pushdown support.</summary>
    FederatedNode
}

/// <summary>
/// Describes the capabilities and cost model of a federated data source.
/// The query planner uses this information to decide which operations
/// to push to the source versus executing locally.
/// </summary>
public sealed record FederatedDataSourceInfo(
    /// <summary>Unique identifier matching the owning IFederatedDataSource.SourceId.</summary>
    string SourceId,

    /// <summary>Classification of this source for cost estimation heuristics.</summary>
    FederatedSourceType SourceType,

    /// <summary>Estimated round-trip latency in milliseconds to this source.</summary>
    double LatencyMs,

    /// <summary>Estimated available bandwidth in megabits per second.</summary>
    double BandwidthMbps,

    /// <summary>Whether this source can apply WHERE predicates natively.</summary>
    bool SupportsFilterPushdown,

    /// <summary>Whether this source can return only requested columns.</summary>
    bool SupportsProjectionPushdown,

    /// <summary>Whether this source can compute aggregations (SUM, COUNT, etc.) natively.</summary>
    bool SupportsAggregationPushdown,

    /// <summary>Whether this source can execute joins between its own tables.</summary>
    bool SupportsJoinPushdown,

    /// <summary>Tables available on this source.</summary>
    IReadOnlyList<string> AvailableTables,

    /// <summary>
    /// Cost multiplier relative to local storage (1.0). Higher values indicate
    /// more expensive access. E.g., 5.0 for cross-region S3, 10.0 for external APIs.
    /// </summary>
    double CostMultiplier
);

/// <summary>
/// Describes a remote query request that can be sent to a federated data source.
/// Encapsulates the portions of a query that the source can handle natively,
/// allowing maximum pushdown of filters, projections, and limits.
/// </summary>
public sealed record RemoteQueryRequest(
    /// <summary>Table to query on the remote source.</summary>
    string TableName,

    /// <summary>
    /// Columns to project. Empty means all columns.
    /// Only populated when the source supports projection pushdown.
    /// </summary>
    ImmutableArray<string> Columns,

    /// <summary>
    /// Filter predicates to apply at the source.
    /// Only populated when the source supports filter pushdown.
    /// </summary>
    ImmutableArray<Expression> Filters,

    /// <summary>Maximum number of rows to return, or null for unlimited.</summary>
    int? Limit,

    /// <summary>
    /// Order-by specification for result ordering at the source.
    /// Empty if no ordering is required.
    /// </summary>
    ImmutableArray<OrderByItem> OrderBy
);

/// <summary>
/// Represents a result returned by a federated query execution.
/// Contains the data batches, row count, and execution metadata.
/// </summary>
public sealed class FederatedQueryResult
{
    /// <summary>Source that produced this result.</summary>
    public string SourceId { get; }

    /// <summary>Columnar batches containing the result data.</summary>
    public IReadOnlyList<ColumnarBatch> Batches { get; }

    /// <summary>Total number of rows across all batches.</summary>
    public long TotalRows { get; }

    /// <summary>Total bytes transferred from the source.</summary>
    public long BytesTransferred { get; }

    /// <summary>Wall-clock execution time in milliseconds.</summary>
    public double ExecutionTimeMs { get; }

    public FederatedQueryResult(
        string sourceId,
        IReadOnlyList<ColumnarBatch> batches,
        long totalRows,
        long bytesTransferred,
        double executionTimeMs)
    {
        SourceId = sourceId;
        Batches = batches;
        TotalRows = totalRows;
        BytesTransferred = bytesTransferred;
        ExecutionTimeMs = executionTimeMs;
    }
}

/// <summary>
/// Progress callback for federated query execution.
/// Reported per-source as data is fetched.
/// </summary>
public sealed record QueryProgress(
    /// <summary>Source currently being fetched.</summary>
    string SourceId,

    /// <summary>Number of rows fetched so far from this source.</summary>
    long RowsFetched,

    /// <summary>Number of bytes fetched so far from this source.</summary>
    long BytesFetched,

    /// <summary>Whether this source has completed.</summary>
    bool IsComplete
);

// ---------------------------------------------------------------
// IFederatedDataSource
// ---------------------------------------------------------------

/// <summary>
/// Abstraction for a remote or heterogeneous data source that can participate
/// in federated queries. Implementations wrap specific backends (S3, PostgreSQL,
/// another DataWarehouse node, etc.) and expose their capabilities for
/// the query planner to optimize data access.
/// </summary>
public interface IFederatedDataSource
{
    /// <summary>
    /// Unique identifier for this source (e.g., "s3-us-east", "local-nvme", "postgres-analytics").
    /// Used in dotted table references: "{sourceId}.{tableName}".
    /// </summary>
    string SourceId { get; }

    /// <summary>
    /// Returns capability and cost information for this source.
    /// The planner uses this to decide pushdown strategy and cost estimation.
    /// </summary>
    FederatedDataSourceInfo GetInfo();

    /// <summary>
    /// Executes a query against a specific table on this source.
    /// The request includes pushed-down filters, projections, and limits
    /// based on the source's declared capabilities.
    /// </summary>
    /// <param name="tableName">The table to query on this source.</param>
    /// <param name="request">Query specification with pushed-down operations.</param>
    /// <param name="ct">Cancellation token for timeout and cancellation.</param>
    /// <returns>Async stream of columnar batches from this source.</returns>
    Task<IAsyncEnumerable<ColumnarBatch>> ExecuteRemoteQueryAsync(
        string tableName,
        RemoteQueryRequest request,
        CancellationToken ct);

    /// <summary>
    /// Returns table-level statistics from this source for cost estimation.
    /// Returns null if statistics are unavailable for the specified table.
    /// </summary>
    /// <param name="tableName">The table to get statistics for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Table statistics or null if unavailable.</returns>
    Task<TableStatistics?> GetRemoteStatisticsAsync(
        string tableName,
        CancellationToken ct);
}

// ---------------------------------------------------------------
// IFederatedDataSourceRegistry
// ---------------------------------------------------------------

/// <summary>
/// Registry for managing federated data sources. Provides source lookup,
/// table resolution, and lifecycle management. Table names can be dotted
/// (e.g., "s3_archive.events") where the prefix before the first dot
/// is the source identifier and the remainder is the table name on that source.
/// </summary>
public interface IFederatedDataSourceRegistry
{
    /// <summary>Register a federated data source. Overwrites existing with same SourceId.</summary>
    void RegisterSource(IFederatedDataSource source);

    /// <summary>Remove a federated data source by its identifier.</summary>
    /// <returns>True if the source was found and removed.</returns>
    bool UnregisterSource(string sourceId);

    /// <summary>Look up a source by its identifier.</summary>
    /// <returns>The source, or null if not found.</returns>
    IFederatedDataSource? GetSource(string sourceId);

    /// <summary>Returns all registered federated data sources.</summary>
    IReadOnlyList<IFederatedDataSource> GetAllSources();

    /// <summary>
    /// Resolves a potentially-dotted table name to its owning source and remote table name.
    /// For "s3_archive.events", returns (source for "s3_archive", "events").
    /// For unqualified names, searches all sources for a matching table.
    /// </summary>
    /// <returns>The resolved source and remote table name, or null if no source owns this table.</returns>
    (IFederatedDataSource Source, string RemoteTable)? ResolveTable(string tableName);
}

// ---------------------------------------------------------------
// InMemoryFederatedDataSourceRegistry
// ---------------------------------------------------------------

/// <summary>
/// Thread-safe in-memory implementation of IFederatedDataSourceRegistry.
/// Uses ConcurrentDictionary for lock-free reads and safe concurrent modifications.
/// </summary>
public sealed class InMemoryFederatedDataSourceRegistry : IFederatedDataSourceRegistry
{
    private readonly BoundedDictionary<string, IFederatedDataSource> _sources = new BoundedDictionary<string, IFederatedDataSource>(1000);

    /// <inheritdoc />
    public void RegisterSource(IFederatedDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _sources[source.SourceId] = source;
    }

    /// <inheritdoc />
    public bool UnregisterSource(string sourceId)
    {
        ArgumentNullException.ThrowIfNull(sourceId);
        return _sources.TryRemove(sourceId, out _);
    }

    /// <inheritdoc />
    public IFederatedDataSource? GetSource(string sourceId)
    {
        ArgumentNullException.ThrowIfNull(sourceId);
        return _sources.TryGetValue(sourceId, out var source) ? source : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<IFederatedDataSource> GetAllSources()
    {
        return _sources.Values.ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public (IFederatedDataSource Source, string RemoteTable)? ResolveTable(string tableName)
    {
        ArgumentNullException.ThrowIfNull(tableName);

        // Try dotted notation first: "sourceId.tableName"
        var dotIndex = tableName.IndexOf('.');
        if (dotIndex > 0 && dotIndex < tableName.Length - 1)
        {
            var sourceId = tableName[..dotIndex];
            var remoteTable = tableName[(dotIndex + 1)..];

            if (_sources.TryGetValue(sourceId, out var source))
            {
                return (source, remoteTable);
            }
        }

        // Search all sources for a matching table (unqualified name)
        // Snapshot Values to avoid enumeration-during-mutation (finding P2-177)
        foreach (var source in _sources.Values.ToList())
        {
            var info = source.GetInfo();
            if (info.AvailableTables.Any(t =>
                string.Equals(t, tableName, StringComparison.OrdinalIgnoreCase)))
            {
                return (source, tableName);
            }
        }

        return null;
    }
}
