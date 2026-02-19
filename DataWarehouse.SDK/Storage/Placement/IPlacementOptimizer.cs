using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Storage.Placement;

/// <summary>
/// Gravity-aware placement optimizer that scores objects based on multi-dimensional factors
/// (access frequency, egress cost, latency, compliance, co-location) and generates
/// optimized rebalance plans.
/// </summary>
/// <remarks>
/// Unlike <see cref="IPlacementAlgorithm"/> which is a pure deterministic function,
/// the optimizer requires I/O to gather metrics, pricing data, and access patterns,
/// so all methods are asynchronous.
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Zero-Gravity Storage")]
public interface IPlacementOptimizer
{
    /// <summary>
    /// Computes the data gravity score for a single object, quantifying how strongly
    /// it is bound to its current location.
    /// </summary>
    /// <param name="objectKey">The key of the object to score.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A gravity score where higher values indicate stronger binding to the current node.</returns>
    Task<DataGravityScore> ComputeGravityAsync(string objectKey, CancellationToken ct = default);

    /// <summary>
    /// Computes data gravity scores for multiple objects in a single batch operation.
    /// </summary>
    /// <param name="objectKeys">The keys of the objects to score.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Gravity scores for each requested object.</returns>
    Task<IReadOnlyList<DataGravityScore>> ComputeGravityBatchAsync(
        IReadOnlyList<string> objectKeys,
        CancellationToken ct = default);

    /// <summary>
    /// Produces an optimized placement decision that factors in data gravity alongside
    /// the base placement algorithm's topology-aware mapping.
    /// </summary>
    /// <param name="target">The object and its placement requirements.</param>
    /// <param name="clusterMap">The current set of available nodes.</param>
    /// <param name="gravity">The pre-computed gravity score for the object.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A placement decision optimized for the object's gravity profile.</returns>
    Task<PlacementDecision> OptimizePlacementAsync(
        PlacementTarget target,
        IReadOnlyList<NodeDescriptor> clusterMap,
        DataGravityScore gravity,
        CancellationToken ct = default);

    /// <summary>
    /// Generates a rebalance plan for the entire cluster based on gravity scores,
    /// cost optimization, and the provided options.
    /// </summary>
    /// <param name="clusterMap">The current set of available nodes.</param>
    /// <param name="options">Options controlling the rebalance plan generation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A plan describing the recommended object moves.</returns>
    Task<RebalancePlan> GenerateRebalancePlanAsync(
        IReadOnlyList<NodeDescriptor> clusterMap,
        RebalanceOptions options,
        CancellationToken ct = default);
}
