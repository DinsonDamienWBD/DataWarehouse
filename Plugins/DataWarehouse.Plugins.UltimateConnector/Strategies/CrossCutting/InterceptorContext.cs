using System;
using System.Collections.Generic;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.CrossCutting
{
    /// <summary>
    /// Context provided to the <see cref="IConnectionInterceptor.OnBeforeRequestAsync"/> hook
    /// before a connection operation is executed. Interceptors may modify the request payload
    /// or cancel the operation by setting <see cref="IsCancelled"/>.
    /// </summary>
    /// <param name="StrategyId">Strategy identifier processing the request.</param>
    /// <param name="ConnectionId">Connection identifier the request is targeting.</param>
    /// <param name="Timestamp">When the request was initiated.</param>
    /// <param name="OperationType">Type of operation being performed (connect, query, write).</param>
    /// <param name="RequestPayload">Mutable request payload that interceptors may modify.</param>
    public sealed record BeforeRequestContext(
        string StrategyId,
        string ConnectionId,
        DateTimeOffset Timestamp,
        string OperationType,
        Dictionary<string, object> RequestPayload)
    {
        /// <summary>
        /// Whether a preceding interceptor has cancelled this request.
        /// </summary>
        public bool IsCancelled { get; set; }

        /// <summary>
        /// Reason for cancellation, if <see cref="IsCancelled"/> is true.
        /// </summary>
        public string? CancellationReason { get; set; }

        /// <summary>
        /// Mutable metadata bag for passing data between interceptors in the pipeline.
        /// </summary>
        public Dictionary<string, object> InterceptorMetadata { get; init; } = new();
    }

    /// <summary>
    /// Context provided to the <see cref="IConnectionInterceptor.OnAfterResponseAsync"/> hook
    /// after a connection operation completes. Contains the response data and timing information.
    /// </summary>
    /// <param name="StrategyId">Strategy identifier that processed the request.</param>
    /// <param name="ConnectionId">Connection identifier that was used.</param>
    /// <param name="Timestamp">When the response was received.</param>
    /// <param name="OperationType">Type of operation that was performed.</param>
    /// <param name="Duration">Duration of the operation.</param>
    /// <param name="Success">Whether the operation succeeded.</param>
    /// <param name="ResponsePayload">Response data from the operation.</param>
    public sealed record AfterResponseContext(
        string StrategyId,
        string ConnectionId,
        DateTimeOffset Timestamp,
        string OperationType,
        TimeSpan Duration,
        bool Success,
        Dictionary<string, object> ResponsePayload)
    {
        /// <summary>
        /// Error message if the operation failed.
        /// </summary>
        public string? ErrorMessage { get; init; }

        /// <summary>
        /// Mutable metadata bag for passing data between interceptors in the pipeline.
        /// </summary>
        public Dictionary<string, object> InterceptorMetadata { get; init; } = new();
    }

    /// <summary>
    /// Context provided to the <see cref="IConnectionInterceptor.OnSchemaDiscoveredAsync"/> hook
    /// when a schema is discovered during connection or query operations.
    /// </summary>
    /// <param name="StrategyId">Strategy identifier that discovered the schema.</param>
    /// <param name="ConnectionId">Connection identifier used for discovery.</param>
    /// <param name="Timestamp">When the schema was discovered.</param>
    /// <param name="SchemaName">Name of the discovered schema (table, collection, topic).</param>
    /// <param name="Fields">Discovered field definitions as name-type pairs.</param>
    public sealed record SchemaDiscoveryContext(
        string StrategyId,
        string ConnectionId,
        DateTimeOffset Timestamp,
        string SchemaName,
        IReadOnlyList<SchemaFieldInfo> Fields)
    {
        /// <summary>
        /// Source of the schema (e.g., "introspection", "metadata-catalog", "sample-based").
        /// </summary>
        public string DiscoveryMethod { get; init; } = "introspection";

        /// <summary>
        /// Mutable metadata bag for passing data between interceptors in the pipeline.
        /// </summary>
        public Dictionary<string, object> InterceptorMetadata { get; init; } = new();
    }

    /// <summary>
    /// Describes a single field discovered during schema introspection.
    /// </summary>
    /// <param name="Name">Field name.</param>
    /// <param name="DataType">Data type of the field.</param>
    /// <param name="IsNullable">Whether the field is nullable.</param>
    /// <param name="MaxLength">Maximum length for string/binary fields.</param>
    public sealed record SchemaFieldInfo(
        string Name,
        string DataType,
        bool IsNullable = true,
        int? MaxLength = null);

    /// <summary>
    /// Context provided to the <see cref="IConnectionInterceptor.OnErrorAsync"/> hook
    /// when an error occurs during a connection operation.
    /// </summary>
    /// <param name="StrategyId">Strategy identifier where the error occurred.</param>
    /// <param name="ConnectionId">Connection identifier involved in the error.</param>
    /// <param name="Timestamp">When the error occurred.</param>
    /// <param name="Exception">The exception that was thrown.</param>
    /// <param name="OperationType">Operation being performed when the error occurred.</param>
    public sealed record ErrorContext(
        string StrategyId,
        string ConnectionId,
        DateTimeOffset Timestamp,
        Exception Exception,
        string OperationType)
    {
        /// <summary>
        /// Whether the error has been handled by an interceptor and should not propagate.
        /// </summary>
        public bool IsHandled { get; set; }

        /// <summary>
        /// Optional replacement result if an interceptor handled the error and produced a fallback.
        /// </summary>
        public object? FallbackResult { get; set; }

        /// <summary>
        /// Mutable metadata bag for passing data between interceptors in the pipeline.
        /// </summary>
        public Dictionary<string, object> InterceptorMetadata { get; init; } = new();
    }

    /// <summary>
    /// Context provided to the <see cref="IConnectionInterceptor.OnConnectionEstablishedAsync"/> hook
    /// when a new connection is successfully established.
    /// </summary>
    /// <param name="StrategyId">Strategy that established the connection.</param>
    /// <param name="ConnectionId">Newly assigned connection identifier.</param>
    /// <param name="Timestamp">When the connection was established.</param>
    /// <param name="ConnectionInfo">Metadata about the established connection.</param>
    /// <param name="Latency">Time taken to establish the connection.</param>
    public sealed record ConnectionEstablishedContext(
        string StrategyId,
        string ConnectionId,
        DateTimeOffset Timestamp,
        IReadOnlyDictionary<string, object> ConnectionInfo,
        TimeSpan Latency)
    {
        /// <summary>
        /// Mutable metadata bag for passing data between interceptors in the pipeline.
        /// </summary>
        public Dictionary<string, object> InterceptorMetadata { get; init; } = new();
    }
}
