using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.AI;

namespace DataWarehouse.SDK.Contracts.StorageProcessing
{
    #region Storage Processing Interface

    /// <summary>
    /// Defines a storage-side processing strategy for in-place data operations.
    /// Enables compute pushdown to storage layer, reducing data movement and improving performance.
    /// Supports filtering, aggregation, projections, and complex queries executed at the storage tier.
    /// </summary>
    public interface IStorageProcessingStrategy
    {
        /// <summary>
        /// Gets the unique identifier for this storage processing strategy.
        /// </summary>
        string StrategyId { get; }

        /// <summary>
        /// Gets the human-readable name of this processing strategy.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Gets the capabilities supported by this storage processing engine.
        /// Describes what types of operations can be pushed down to storage.
        /// </summary>
        StorageProcessingCapabilities Capabilities { get; }

        /// <summary>
        /// Processes a query directly at the storage layer without transferring all data.
        /// Executes filtering, projection, and transformations close to data.
        /// </summary>
        /// <param name="query">The processing query defining operations to perform.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Results of the processing operation.</returns>
        /// <exception cref="ArgumentNullException">Thrown when query is null.</exception>
        /// <exception cref="NotSupportedException">Thrown when query operation is not supported.</exception>
        Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);

        /// <summary>
        /// Executes a query against stored data with optional filtering and projection.
        /// Similar to SQL SELECT but optimized for the storage backend.
        /// </summary>
        /// <param name="query">The query to execute.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Query results as an async enumerable.</returns>
        /// <exception cref="ArgumentNullException">Thrown when query is null.</exception>
        IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, CancellationToken ct = default);

        /// <summary>
        /// Performs aggregation operations (SUM, COUNT, AVG, MIN, MAX) at the storage layer.
        /// Minimizes data transfer by computing aggregates close to data.
        /// </summary>
        /// <param name="query">The aggregation query.</param>
        /// <param name="aggregationType">Type of aggregation to perform.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Aggregated result.</returns>
        /// <exception cref="ArgumentNullException">Thrown when query is null.</exception>
        /// <exception cref="NotSupportedException">Thrown when aggregation type is not supported.</exception>
        Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);

        /// <summary>
        /// Validates if the given query is supported by this storage processing strategy.
        /// Useful for query planning and optimization.
        /// </summary>
        /// <param name="query">The query to validate.</param>
        /// <returns>True if supported, false otherwise.</returns>
        bool IsQuerySupported(ProcessingQuery query);

        /// <summary>
        /// Estimates the cost/performance of executing a query at the storage layer.
        /// Used for deciding between storage-side vs. client-side processing.
        /// </summary>
        /// <param name="query">The query to estimate.</param>
        /// <returns>Cost estimate including time, I/O, and compute.</returns>
        Task<QueryCostEstimate> EstimateQueryCostAsync(ProcessingQuery query);
    }

    #endregion

    #region Storage Processing Capabilities

    /// <summary>
    /// Describes the processing capabilities available at the storage layer.
    /// Used to determine what operations can be pushed down for execution.
    /// </summary>
    public sealed record StorageProcessingCapabilities
    {
        /// <summary>
        /// Indicates if the storage supports filtering/predicate pushdown (WHERE clauses).
        /// </summary>
        public bool SupportsFiltering { get; init; }

        /// <summary>
        /// Indicates if the storage supports complex predicate expressions (AND, OR, NOT).
        /// </summary>
        public bool SupportsPredication { get; init; }

        /// <summary>
        /// Indicates if the storage supports aggregation operations (SUM, COUNT, AVG, etc).
        /// </summary>
        public bool SupportsAggregation { get; init; }

        /// <summary>
        /// Indicates if the storage supports join operations across datasets.
        /// </summary>
        public bool SupportsJoins { get; init; }

        /// <summary>
        /// Indicates if the storage supports projection (column selection).
        /// </summary>
        public bool SupportsProjection { get; init; }

        /// <summary>
        /// Indicates if the storage supports sorting/ordering results.
        /// </summary>
        public bool SupportsSorting { get; init; }

        /// <summary>
        /// Indicates if the storage supports grouping operations (GROUP BY).
        /// </summary>
        public bool SupportsGrouping { get; init; }

        /// <summary>
        /// Indicates if the storage supports limiting result sets (TOP, LIMIT).
        /// </summary>
        public bool SupportsLimiting { get; init; }

        /// <summary>
        /// Indicates if the storage supports pattern matching (LIKE, regex).
        /// </summary>
        public bool SupportsPatternMatching { get; init; }

        /// <summary>
        /// Indicates if the storage supports full-text search.
        /// </summary>
        public bool SupportsFullTextSearch { get; init; }

        /// <summary>
        /// List of supported operations (filter operators, functions, etc).
        /// Examples: "eq", "ne", "gt", "lt", "in", "contains", "startswith"
        /// </summary>
        public IReadOnlyList<string> SupportedOperations { get; init; } = Array.Empty<string>();

        /// <summary>
        /// List of supported aggregation types.
        /// </summary>
        public IReadOnlyList<AggregationType> SupportedAggregations { get; init; } = Array.Empty<AggregationType>();

        /// <summary>
        /// Maximum complexity level for queries (0-10).
        /// Higher values indicate more complex query capabilities.
        /// </summary>
        public int MaxQueryComplexity { get; init; } = 5;

        /// <summary>
        /// Indicates if the storage supports transactions for processing operations.
        /// </summary>
        public bool SupportsTransactions { get; init; }

        /// <summary>
        /// Default minimal capabilities (basic filtering only).
        /// </summary>
        public static StorageProcessingCapabilities Minimal => new()
        {
            SupportsFiltering = true,
            SupportsProjection = true,
            SupportedOperations = new[] { "eq", "ne", "gt", "lt", "gte", "lte" }
        };

        /// <summary>
        /// Full capabilities (all operations supported).
        /// </summary>
        public static StorageProcessingCapabilities Full => new()
        {
            SupportsFiltering = true,
            SupportsPredication = true,
            SupportsAggregation = true,
            SupportsJoins = true,
            SupportsProjection = true,
            SupportsSorting = true,
            SupportsGrouping = true,
            SupportsLimiting = true,
            SupportsPatternMatching = true,
            SupportsFullTextSearch = true,
            MaxQueryComplexity = 10,
            SupportsTransactions = true,
            SupportedOperations = new[] { "eq", "ne", "gt", "lt", "gte", "lte", "in", "nin", "contains", "startswith", "endswith", "regex" },
            SupportedAggregations = Enum.GetValues<AggregationType>()
        };
    }

    #endregion

    #region Processing Types

    /// <summary>
    /// Represents a processing query to be executed at the storage layer.
    /// Describes the operation, filters, projections, and other query parameters.
    /// </summary>
    public sealed record ProcessingQuery
    {
        /// <summary>
        /// The storage path or dataset to query.
        /// </summary>
        public required string Source { get; init; }

        /// <summary>
        /// Filter expressions to apply (WHERE clause equivalent).
        /// </summary>
        public IReadOnlyList<FilterExpression>? Filters { get; init; }

        /// <summary>
        /// Fields to project/select from the result.
        /// Null or empty means select all fields.
        /// </summary>
        public IReadOnlyList<ProjectionField>? Projection { get; init; }

        /// <summary>
        /// Sort orders to apply.
        /// </summary>
        public IReadOnlyList<SortOrder>? Sort { get; init; }

        /// <summary>
        /// Maximum number of results to return.
        /// Null means no limit.
        /// </summary>
        public int? Limit { get; init; }

        /// <summary>
        /// Number of results to skip before returning.
        /// Used for pagination.
        /// </summary>
        public int? Offset { get; init; }

        /// <summary>
        /// Fields to group by (GROUP BY clause).
        /// </summary>
        public IReadOnlyList<string>? GroupBy { get; init; }

        /// <summary>
        /// Additional query options specific to the storage backend.
        /// </summary>
        public IReadOnlyDictionary<string, object>? Options { get; init; }
    }

    /// <summary>
    /// Represents a filter expression for query predicates.
    /// </summary>
    public sealed record FilterExpression
    {
        /// <summary>
        /// The field name to filter on.
        /// </summary>
        public required string Field { get; init; }

        /// <summary>
        /// The filter operator (eq, ne, gt, lt, in, contains, etc).
        /// </summary>
        public required string Operator { get; init; }

        /// <summary>
        /// The value to compare against.
        /// </summary>
        public required object Value { get; init; }

        /// <summary>
        /// Logical operator to combine with next expression (AND, OR).
        /// </summary>
        public LogicalOperator LogicalOperator { get; init; } = LogicalOperator.And;
    }

    /// <summary>
    /// Logical operators for combining filter expressions.
    /// </summary>
    public enum LogicalOperator
    {
        /// <summary>
        /// Logical AND - both conditions must be true.
        /// </summary>
        And,

        /// <summary>
        /// Logical OR - at least one condition must be true.
        /// </summary>
        Or,

        /// <summary>
        /// Logical NOT - negates the condition.
        /// </summary>
        Not
    }

    /// <summary>
    /// Represents a field to project in query results.
    /// </summary>
    public sealed record ProjectionField
    {
        /// <summary>
        /// The source field name.
        /// </summary>
        public required string SourceField { get; init; }

        /// <summary>
        /// The output field name (alias).
        /// If null, uses the source field name.
        /// </summary>
        public string? OutputName { get; init; }

        /// <summary>
        /// Optional transformation function to apply (UPPER, LOWER, TRIM, etc).
        /// </summary>
        public string? Transformation { get; init; }
    }

    /// <summary>
    /// Represents a sort order specification.
    /// </summary>
    public sealed record SortOrder
    {
        /// <summary>
        /// The field to sort by.
        /// </summary>
        public required string Field { get; init; }

        /// <summary>
        /// The sort direction.
        /// </summary>
        public SortDirection Direction { get; init; } = SortDirection.Ascending;
    }

    /// <summary>
    /// Sort direction for ordering results.
    /// </summary>
    public enum SortDirection
    {
        /// <summary>
        /// Ascending order (A-Z, 0-9).
        /// </summary>
        Ascending,

        /// <summary>
        /// Descending order (Z-A, 9-0).
        /// </summary>
        Descending
    }

    /// <summary>
    /// Result of a storage processing operation.
    /// </summary>
    public sealed record ProcessingResult
    {
        /// <summary>
        /// The result data as a dictionary of field names to values.
        /// </summary>
        public required IReadOnlyDictionary<string, object?> Data { get; init; }

        /// <summary>
        /// Metadata about the processing operation.
        /// </summary>
        public ProcessingMetadata? Metadata { get; init; }
    }

    /// <summary>
    /// Metadata about a processing operation.
    /// </summary>
    public sealed record ProcessingMetadata
    {
        /// <summary>
        /// Number of rows processed at the storage layer.
        /// </summary>
        public long RowsProcessed { get; init; }

        /// <summary>
        /// Number of rows returned after filtering.
        /// </summary>
        public long RowsReturned { get; init; }

        /// <summary>
        /// Amount of data processed in bytes.
        /// </summary>
        public long BytesProcessed { get; init; }

        /// <summary>
        /// Processing time at the storage layer in milliseconds.
        /// </summary>
        public double ProcessingTimeMs { get; init; }

        /// <summary>
        /// Additional backend-specific metadata.
        /// </summary>
        public IReadOnlyDictionary<string, object>? AdditionalInfo { get; init; }
    }

    /// <summary>
    /// Types of aggregation operations.
    /// </summary>
    public enum AggregationType
    {
        /// <summary>
        /// Count the number of rows/values.
        /// </summary>
        Count,

        /// <summary>
        /// Sum numeric values.
        /// </summary>
        Sum,

        /// <summary>
        /// Calculate average of numeric values.
        /// </summary>
        Average,

        /// <summary>
        /// Find minimum value.
        /// </summary>
        Min,

        /// <summary>
        /// Find maximum value.
        /// </summary>
        Max,

        /// <summary>
        /// Count distinct values.
        /// </summary>
        CountDistinct,

        /// <summary>
        /// Calculate standard deviation.
        /// </summary>
        StdDev,

        /// <summary>
        /// Calculate variance.
        /// </summary>
        Variance,

        /// <summary>
        /// Calculate median value.
        /// </summary>
        Median,

        /// <summary>
        /// Calculate percentile.
        /// </summary>
        Percentile
    }

    /// <summary>
    /// Result of an aggregation operation.
    /// </summary>
    public sealed record AggregationResult
    {
        /// <summary>
        /// The aggregation type performed.
        /// </summary>
        public required AggregationType AggregationType { get; init; }

        /// <summary>
        /// The aggregated value.
        /// Type depends on aggregation (long for Count, double for Average, etc).
        /// </summary>
        public required object Value { get; init; }

        /// <summary>
        /// Grouped results if GROUP BY was used.
        /// Key is the group identifier, value is the aggregate for that group.
        /// </summary>
        public IReadOnlyDictionary<string, object>? GroupedResults { get; init; }

        /// <summary>
        /// Metadata about the aggregation operation.
        /// </summary>
        public ProcessingMetadata? Metadata { get; init; }
    }

    /// <summary>
    /// Cost estimate for query execution.
    /// </summary>
    public sealed record QueryCostEstimate
    {
        /// <summary>
        /// Estimated execution time in milliseconds.
        /// </summary>
        public double EstimatedTimeMs { get; init; }

        /// <summary>
        /// Estimated I/O operations required.
        /// </summary>
        public long EstimatedIOOperations { get; init; }

        /// <summary>
        /// Estimated amount of data to scan in bytes.
        /// </summary>
        public long EstimatedDataScanned { get; init; }

        /// <summary>
        /// Estimated CPU cost (arbitrary units, backend-specific).
        /// </summary>
        public double EstimatedCPUCost { get; init; }

        /// <summary>
        /// Estimated memory usage in bytes.
        /// </summary>
        public long EstimatedMemoryUsage { get; init; }

        /// <summary>
        /// Overall cost score (0-100).
        /// Higher values indicate more expensive operations.
        /// </summary>
        public double CostScore { get; init; }

        /// <summary>
        /// Recommendations for query optimization.
        /// </summary>
        public IReadOnlyList<string>? Recommendations { get; init; }
    }

    #endregion

    #region Storage Processing Base Class

    /// <summary>
    /// Abstract base class for storage processing strategy implementations.
    /// Provides common functionality including query validation, capability checking,
    /// and default implementations for optional operations.
    /// </summary>
    public abstract class StorageProcessingStrategyBase : IStorageProcessingStrategy
    {
        /// <summary>
        /// Gets the unique identifier for this storage processing strategy.
        /// </summary>
        public abstract string StrategyId { get; }

        /// <summary>
        /// Gets the human-readable name of this processing strategy.
        /// </summary>
        public abstract string Name { get; }

        /// <summary>
        /// Gets the capabilities supported by this storage processing engine.
        /// </summary>
        public abstract StorageProcessingCapabilities Capabilities { get; }

        #region Intelligence Integration

        /// <summary>
        /// Gets the message bus for Intelligence communication.
        /// </summary>
        protected IMessageBus? MessageBus { get; private set; }

        /// <summary>
        /// Configures Intelligence integration for this storage processing strategy.
        /// </summary>
        /// <param name="messageBus">Optional message bus for Intelligence communication.</param>
        public virtual void ConfigureIntelligence(IMessageBus? messageBus)
        {
            MessageBus = messageBus;
        }

        /// <summary>
        /// Gets a value indicating whether Intelligence integration is available.
        /// </summary>
        protected bool IsIntelligenceAvailable => MessageBus != null;

        /// <summary>
        /// Gets static knowledge about this storage processing strategy for Intelligence registration.
        /// </summary>
        /// <returns>A KnowledgeObject describing this strategy's capabilities.</returns>
        public virtual KnowledgeObject GetStrategyKnowledge()
        {
            return new KnowledgeObject
            {
                Id = $"storageprocessing.{StrategyId}",
                Topic = "storageprocessing.strategy",
                SourcePluginId = "sdk.storageprocessing",
                SourcePluginName = Name,
                KnowledgeType = "capability",
                Description = $"{Name} storage processing strategy for in-place data operations",
                Payload = new Dictionary<string, object>
                {
                    ["strategyId"] = StrategyId,
                    ["supportsFiltering"] = Capabilities.SupportsFiltering,
                    ["supportsAggregation"] = Capabilities.SupportsAggregation,
                    ["supportsJoins"] = Capabilities.SupportsJoins,
                    ["maxQueryComplexity"] = Capabilities.MaxQueryComplexity
                },
                Tags = new[] { "storageprocessing", "compute", "pushdown", "strategy" }
            };
        }

        /// <summary>
        /// Gets the registered capability for this storage processing strategy.
        /// </summary>
        /// <returns>A RegisteredCapability describing this strategy.</returns>
        public virtual RegisteredCapability GetStrategyCapability()
        {
            return new RegisteredCapability
            {
                CapabilityId = $"storageprocessing.{StrategyId}",
                DisplayName = Name,
                Description = $"{Name} storage processing strategy",
                Category = CapabilityCategory.Compute,
                SubCategory = "StorageProcessing",
                PluginId = "sdk.storageprocessing",
                PluginName = Name,
                PluginVersion = "1.0.0",
                Tags = new[] { "storageprocessing", "query", "aggregation" },
                SemanticDescription = $"Use {Name} for storage-side compute and query pushdown"
            };
        }

        /// <summary>
        /// Requests storage processing optimization suggestions from Intelligence.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Optimization suggestions if available, null otherwise.</returns>
        protected async Task<object?> RequestStorageProcessingOptimizationAsync(CancellationToken ct = default)
        {
            if (!IsIntelligenceAvailable) return null;

            // Send request to Intelligence for storage processing optimization
            await Task.CompletedTask;
            return null;
        }

        #endregion

        /// <inheritdoc/>
        public abstract Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);

        /// <inheritdoc/>
        public abstract IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, CancellationToken ct = default);

        /// <inheritdoc/>
        public abstract Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);

        /// <inheritdoc/>
        public virtual bool IsQuerySupported(ProcessingQuery query)
        {
            if (query == null)
                return false;

            // Check if filters use supported operations
            if (query.Filters != null && !Capabilities.SupportsFiltering)
                return false;

            if (query.Filters != null && query.Filters.Count > 0)
            {
                foreach (var filter in query.Filters)
                {
                    if (!Capabilities.SupportedOperations.Contains(filter.Operator))
                        return false;
                }
            }

            // Check if projection is supported
            if (query.Projection != null && query.Projection.Count > 0 && !Capabilities.SupportsProjection)
                return false;

            // Check if sorting is supported
            if (query.Sort != null && query.Sort.Count > 0 && !Capabilities.SupportsSorting)
                return false;

            // Check if grouping is supported
            if (query.GroupBy != null && query.GroupBy.Count > 0 && !Capabilities.SupportsGrouping)
                return false;

            // Check if limiting is supported
            if (query.Limit.HasValue && !Capabilities.SupportsLimiting)
                return false;

            return true;
        }

        /// <inheritdoc/>
        public virtual Task<QueryCostEstimate> EstimateQueryCostAsync(ProcessingQuery query)
        {
            // Default simple cost estimation
            // Derived classes should override with backend-specific logic

            var filterCount = query.Filters?.Count ?? 0;
            var projectionCount = query.Projection?.Count ?? 0;
            var sortCount = query.Sort?.Count ?? 0;
            var hasGroupBy = query.GroupBy != null && query.GroupBy.Count > 0;

            // Simple heuristic cost calculation
            var baseCost = 100.0;
            var filterCost = filterCount * 10.0;
            var projectionCost = projectionCount * 5.0;
            var sortCost = sortCount * 20.0;
            var groupByCost = hasGroupBy ? 50.0 : 0.0;

            var totalCost = baseCost + filterCost + projectionCost + sortCost + groupByCost;

            return Task.FromResult(new QueryCostEstimate
            {
                EstimatedTimeMs = totalCost,
                EstimatedIOOperations = 1000 + (filterCount * 100),
                EstimatedDataScanned = 1024 * 1024, // 1 MB default
                EstimatedCPUCost = totalCost / 10.0,
                EstimatedMemoryUsage = 1024 * 1024, // 1 MB default
                CostScore = Math.Min(100, totalCost / 10.0)
            });
        }

        /// <summary>
        /// Validates a query and throws if not supported.
        /// </summary>
        /// <param name="query">The query to validate.</param>
        /// <exception cref="ArgumentNullException">Thrown when query is null.</exception>
        /// <exception cref="NotSupportedException">Thrown when query is not supported.</exception>
        protected virtual void ValidateQuery(ProcessingQuery query)
        {
            if (query == null)
                throw new ArgumentNullException(nameof(query));

            if (!IsQuerySupported(query))
                throw new NotSupportedException($"Query is not supported by {Name}. Check Capabilities for supported operations.");
        }

        /// <summary>
        /// Validates an aggregation type and throws if not supported.
        /// </summary>
        /// <param name="aggregationType">The aggregation type to validate.</param>
        /// <exception cref="NotSupportedException">Thrown when aggregation type is not supported.</exception>
        protected virtual void ValidateAggregation(AggregationType aggregationType)
        {
            if (!Capabilities.SupportsAggregation)
                throw new NotSupportedException($"{Name} does not support aggregation operations.");

            if (!Capabilities.SupportedAggregations.Contains(aggregationType))
                throw new NotSupportedException($"Aggregation type {aggregationType} is not supported by {Name}.");
        }
    }

    #endregion
}
