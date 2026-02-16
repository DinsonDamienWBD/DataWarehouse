using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Federation.Addressing;
using DataWarehouse.SDK.Federation.Routing;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Federation.Replication;

/// <summary>
/// Router decorator with replica selection and automatic fallback.
/// </summary>
/// <remarks>
/// <para>
/// ReplicationAwareRouter decorates an <see cref="IStorageRouter"/> to add replication-aware
/// read routing. For read operations on UUID-addressed objects, it selects the best replica
/// based on consistency level and topology, attempts the read, and automatically falls back
/// to other replicas on failure.
/// </para>
/// <para>
/// <strong>Routing Flow:</strong>
/// </para>
/// <list type="number">
///   <item><description>Check if the request is a read operation (Read or GetMetadata)</description></item>
///   <item><description>Check if the address is a UUID-based object address</description></item>
///   <item><description>If not, delegate to inner router without modification</description></item>
///   <item><description>Extract consistency level from request metadata (default: Eventual)</description></item>
///   <item><description>Select initial replica via <see cref="IReplicaSelector.SelectReplicaAsync"/></description></item>
///   <item><description>Attempt read from selected replica with timeout</description></item>
///   <item><description>On failure, get fallback chain and retry up to MaxFallbackAttempts</description></item>
///   <item><description>Return response or error after all attempts exhausted</description></item>
/// </list>
/// <para>
/// <strong>Fallback Behavior:</strong> When a replica read fails (timeout, error, network issue),
/// the router queries the replica selector for a fallback chain, selects the next-best replica,
/// and retries. This process repeats up to MaxFallbackAttempts times.
/// </para>
/// <para>
/// <strong>Non-Read Operations:</strong> Write, Delete, SetMetadata, and other non-read operations
/// bypass replica selection and are routed directly to the inner router.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Replication-aware router with fallback (FOS-07)")]
public sealed class ReplicationAwareRouter : IStorageRouter
{
    private readonly IStorageRouter _innerRouter;
    private readonly IReplicaSelector _replicaSelector;
    private readonly ConsistencyConfiguration _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReplicationAwareRouter"/> class.
    /// </summary>
    /// <param name="innerRouter">The inner router to delegate to after replica selection.</param>
    /// <param name="replicaSelector">The replica selector for choosing read targets.</param>
    /// <param name="config">
    /// Optional consistency configuration. If null, uses default configuration (Eventual
    /// consistency, 5s staleness bound, 3 fallback attempts, 2s timeout).
    /// </param>
    public ReplicationAwareRouter(
        IStorageRouter innerRouter,
        IReplicaSelector replicaSelector,
        ConsistencyConfiguration? config = null)
    {
        _innerRouter = innerRouter;
        _replicaSelector = replicaSelector;
        _config = config ?? new ConsistencyConfiguration();
    }

    /// <inheritdoc/>
    public async Task<StorageResponse> RouteRequestAsync(StorageRequest request, CancellationToken ct = default)
    {
        // Only apply replica selection for read operations
        if (request.Operation != StorageOperation.Read && request.Operation != StorageOperation.GetMetadata)
        {
            return await _innerRouter.RouteRequestAsync(request, ct).ConfigureAwait(false);
        }

        // Extract object ID from address
        if (!UuidObjectAddress.TryGetUuid(request.Address, out var objectId))
        {
            // Not a UUID address -- delegate to inner router
            return await _innerRouter.RouteRequestAsync(request, ct).ConfigureAwait(false);
        }

        // Determine consistency level from request metadata
        var consistency = GetConsistencyLevel(request);

        // Select initial replica
        ReplicaSelectionResult selection;
        try
        {
            selection = await _replicaSelector.SelectReplicaAsync(objectId, consistency, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new StorageResponse
            {
                Success = false,
                NodeId = "router",
                ErrorMessage = $"Replica selection failed: {ex.Message}"
            };
        }

        // Attempt read with fallback
        var attempt = 0;
        var currentNodeId = selection.NodeId;

        while (attempt < _config.MaxFallbackAttempts)
        {
            var routedRequest = new StorageRequest
            {
                RequestId = request.RequestId,
                Address = request.Address,
                Operation = request.Operation,
                Metadata = new Dictionary<string, string>(request.Metadata ?? new Dictionary<string, string>())
                {
                    ["target-node-id"] = currentNodeId,
                    ["replica-selection-reason"] = selection.Reason,
                    ["fallback-attempt"] = attempt.ToString()
                },
                LanguageHint = request.LanguageHint,
                UserId = request.UserId,
                TimestampUtc = request.TimestampUtc
            };

            var response = await TryReadFromReplicaAsync(routedRequest, ct).ConfigureAwait(false);

            if (response.Success)
                return response;

            // Failure -- try fallback
            attempt++;
            var fallbackChain = await _replicaSelector.GetFallbackChainAsync(objectId, currentNodeId, ct).ConfigureAwait(false);

            if (fallbackChain.Count == 0)
            {
                return new StorageResponse
                {
                    Success = false,
                    NodeId = currentNodeId,
                    ErrorMessage = $"Read failed, no fallback replicas available after {attempt} attempts"
                };
            }

            currentNodeId = fallbackChain[0];
        }

        return new StorageResponse
        {
            Success = false,
            NodeId = currentNodeId,
            ErrorMessage = $"Read failed after {_config.MaxFallbackAttempts} fallback attempts"
        };
    }

    /// <summary>
    /// Attempts to read from a replica with timeout.
    /// </summary>
    /// <param name="request">The storage request with target-node-id set.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The storage response from the replica. Returns a failure response if the read times out
    /// or throws an exception.
    /// </returns>
    /// <remarks>
    /// This method enforces the FallbackTimeout configured in <see cref="ConsistencyConfiguration"/>.
    /// If the replica does not respond within the timeout, the read is considered failed and a
    /// failure response is returned (allowing fallback to the next replica).
    /// </remarks>
    private async Task<StorageResponse> TryReadFromReplicaAsync(StorageRequest request, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(_config.FallbackTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            return await _innerRouter.RouteRequestAsync(request, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
        {
            return new StorageResponse
            {
                Success = false,
                NodeId = request.Metadata?["target-node-id"] ?? "unknown",
                ErrorMessage = $"Read timeout after {_config.FallbackTimeout.TotalSeconds}s"
            };
        }
        catch (Exception ex)
        {
            return new StorageResponse
            {
                Success = false,
                NodeId = request.Metadata?["target-node-id"] ?? "unknown",
                ErrorMessage = $"Read failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Extracts the consistency level from request metadata.
    /// </summary>
    /// <param name="request">The storage request.</param>
    /// <returns>
    /// The consistency level specified in request metadata, or the default consistency level
    /// if not specified or invalid.
    /// </returns>
    /// <remarks>
    /// Consistency level can be specified via the "consistency-level" metadata key with values:
    /// "Eventual", "BoundedStaleness", or "Strong" (case-insensitive).
    /// </remarks>
    private ConsistencyLevel GetConsistencyLevel(StorageRequest request)
    {
        if (request.Metadata?.TryGetValue("consistency-level", out var level) == true)
        {
            if (Enum.TryParse<ConsistencyLevel>(level, true, out var parsed))
                return parsed;
        }

        return _config.DefaultLevel;
    }
}
