using DataWarehouse.SDK.Contracts;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Federation.Routing;

/// <summary>
/// Defines the contract for routing storage requests to appropriate pipelines.
/// </summary>
/// <remarks>
/// The storage router is the entry point for federated object storage. It classifies
/// incoming requests (Object vs FilePath language) and dispatches them to the appropriate
/// routing pipeline. Future routing layers (permission-aware, location-aware, replication-aware)
/// build on top of this classification.
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Storage router contract")]
public interface IStorageRouter
{
    /// <summary>
    /// Routes a storage request to the appropriate pipeline based on language classification.
    /// </summary>
    /// <param name="request">The storage request to route.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The storage response containing operation result, node ID, data, and latency.</returns>
    /// <remarks>
    /// Routing flow:
    /// <list type="number">
    /// <item><description>Classify request using <see cref="IRequestClassifier"/></description></item>
    /// <item><description>Select pipeline (ObjectPipeline or FilePathPipeline)</description></item>
    /// <item><description>Execute pipeline and return response</description></item>
    /// <item><description>Track metrics (routing counters, latency)</description></item>
    /// </list>
    /// </remarks>
    Task<StorageResponse> RouteRequestAsync(StorageRequest request, CancellationToken ct = default);
}

/// <summary>
/// Represents the response from a routed storage operation.
/// </summary>
/// <remarks>
/// StorageResponse provides uniform result format across Object and FilePath pipelines.
/// Contains success/failure status, executing node ID, optional data payload, error message,
/// and end-to-end latency measurement.
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Storage response model")]
public sealed record StorageResponse
{
    /// <summary>
    /// Gets whether the storage operation succeeded.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Gets the node ID that executed the storage operation.
    /// Used for distributed tracing and observability.
    /// </summary>
    public required string NodeId { get; init; }

    /// <summary>
    /// Gets the data payload returned by the operation (for Read, GetMetadata, List operations).
    /// Null for Write, Delete, SetMetadata operations.
    /// </summary>
    public byte[]? Data { get; init; }

    /// <summary>
    /// Gets the error message if the operation failed.
    /// Null if Success is true.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Gets the end-to-end latency for the routed operation.
    /// Includes classification time, pipeline selection, and execution time.
    /// </summary>
    public TimeSpan Latency { get; init; }
}
