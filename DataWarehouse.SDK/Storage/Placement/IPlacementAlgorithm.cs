using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Storage.Placement;

/// <summary>
/// Deterministic placement algorithm (CRUSH-equivalent) for computing object-to-node mappings.
/// </summary>
/// <remarks>
/// <para>
/// All methods are synchronous because CRUSH-style placement is a pure mathematical function
/// with no I/O. Implementations MUST be deterministic: identical inputs always produce identical outputs.
/// </para>
/// <para>
/// The algorithm maps objects to nodes based on cluster topology, weights, and constraints
/// while minimizing data movement when the cluster topology changes.
/// </para>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Zero-Gravity Storage")]
public interface IPlacementAlgorithm
{
    /// <summary>
    /// Computes the placement decision for a target object given the current cluster map and constraints.
    /// </summary>
    /// <param name="target">The object and its placement requirements.</param>
    /// <param name="clusterMap">The current set of available nodes.</param>
    /// <param name="constraints">Optional placement constraints (zone separation, tag matching, etc.).</param>
    /// <returns>A deterministic placement decision mapping the object to specific nodes.</returns>
    PlacementDecision ComputePlacement(
        PlacementTarget target,
        IReadOnlyList<NodeDescriptor> clusterMap,
        IReadOnlyList<PlacementConstraint>? constraints = null);

    /// <summary>
    /// Recomputes placement when the cluster topology changes, using the previous decision
    /// to minimize unnecessary data movement.
    /// </summary>
    /// <param name="target">The object and its placement requirements.</param>
    /// <param name="newClusterMap">The updated set of available nodes.</param>
    /// <param name="previousDecision">The previous placement decision to optimize against.</param>
    /// <returns>An updated placement decision with minimal movement from the previous placement.</returns>
    PlacementDecision RecomputeOnNodeChange(
        PlacementTarget target,
        IReadOnlyList<NodeDescriptor> newClusterMap,
        PlacementDecision previousDecision);

    /// <summary>
    /// Estimates the fraction of data that would need to move if the cluster is resized
    /// from the current map to the new map.
    /// </summary>
    /// <param name="currentMap">The current cluster topology.</param>
    /// <param name="newMap">The proposed new cluster topology.</param>
    /// <returns>A value between 0.0 and 1.0 representing the estimated fraction of data movement.</returns>
    double EstimateMovementOnResize(
        IReadOnlyList<NodeDescriptor> currentMap,
        IReadOnlyList<NodeDescriptor> newMap);
}
