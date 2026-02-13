using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Distributed
{
    /// <summary>
    /// Contract for automatic elastic scaling (DIST-04, DIST-11).
    /// Evaluates cluster metrics and makes scaling decisions to add or remove nodes.
    /// In single-node mode, scaling requests return informational results.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public interface IAutoScaler
    {
        /// <summary>
        /// Raised when a scaling event occurs.
        /// </summary>
        event Action<ScalingEvent>? OnScalingEvent;

        /// <summary>
        /// Evaluates current cluster metrics and returns a scaling decision.
        /// </summary>
        /// <param name="context">The scaling context with current metrics and configuration.</param>
        /// <param name="ct">Cancellation token for the evaluation operation.</param>
        /// <returns>A scaling decision indicating recommended action.</returns>
        Task<ScalingDecision> EvaluateAsync(ScalingContext context, CancellationToken ct = default);

        /// <summary>
        /// Initiates a scale-out operation to add nodes to the cluster.
        /// </summary>
        /// <param name="request">The scale-out request specifying how many nodes to add.</param>
        /// <param name="ct">Cancellation token for the scale-out operation.</param>
        /// <returns>The result of the scale-out operation.</returns>
        Task<ScalingResult> ScaleOutAsync(ScaleOutRequest request, CancellationToken ct = default);

        /// <summary>
        /// Initiates a scale-in operation to remove nodes from the cluster.
        /// </summary>
        /// <param name="request">The scale-in request specifying which nodes to remove.</param>
        /// <param name="ct">Cancellation token for the scale-in operation.</param>
        /// <returns>The result of the scale-in operation.</returns>
        Task<ScalingResult> ScaleInAsync(ScaleInRequest request, CancellationToken ct = default);

        /// <summary>
        /// Gets the current scaling state of the cluster.
        /// </summary>
        /// <returns>The current scaling state.</returns>
        ScalingState GetCurrentState();
    }

    /// <summary>
    /// Contract for scaling policies that evaluate metrics to recommend scaling actions (DIST-04).
    /// Multiple policies can be registered to provide input to the auto-scaler.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public interface IScalingPolicy
    {
        /// <summary>
        /// Gets the name of this scaling policy.
        /// </summary>
        string PolicyName { get; }

        /// <summary>
        /// Evaluates current metrics and recommends a scaling action.
        /// </summary>
        /// <param name="metrics">Current cluster metrics.</param>
        /// <param name="ct">Cancellation token for the evaluation.</param>
        /// <returns>A scaling decision based on the policy's rules.</returns>
        Task<ScalingDecision> ShouldScaleAsync(ScalingMetrics metrics, CancellationToken ct = default);
    }

    /// <summary>
    /// A scaling decision produced by the auto-scaler or a scaling policy.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public record ScalingDecision
    {
        /// <summary>
        /// The recommended scaling action.
        /// </summary>
        public required ScalingAction Action { get; init; }

        /// <summary>
        /// Human-readable reason for the decision.
        /// </summary>
        public required string Reason { get; init; }

        /// <summary>
        /// The target number of nodes after scaling (if applicable).
        /// </summary>
        public int TargetNodeCount { get; init; }
    }

    /// <summary>
    /// Context provided to the auto-scaler for evaluation.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public record ScalingContext
    {
        /// <summary>
        /// Current cluster metrics.
        /// </summary>
        public required ScalingMetrics Metrics { get; init; }

        /// <summary>
        /// Current number of nodes in the cluster.
        /// </summary>
        public required int CurrentNodeCount { get; init; }

        /// <summary>
        /// Minimum number of nodes allowed.
        /// </summary>
        public int MinNodeCount { get; init; } = 1;

        /// <summary>
        /// Maximum number of nodes allowed.
        /// </summary>
        public int MaxNodeCount { get; init; } = 100;
    }

    /// <summary>
    /// Current cluster metrics used by scaling policies.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public record ScalingMetrics
    {
        /// <summary>
        /// Average CPU usage across all nodes (0.0 to 100.0).
        /// </summary>
        public required double CpuUsage { get; init; }

        /// <summary>
        /// Average memory usage across all nodes (0.0 to 100.0).
        /// </summary>
        public required double MemoryUsage { get; init; }

        /// <summary>
        /// Average storage usage across all nodes (0.0 to 100.0).
        /// </summary>
        public required double StorageUsage { get; init; }

        /// <summary>
        /// Total active connections across all nodes.
        /// </summary>
        public required long ActiveConnections { get; init; }

        /// <summary>
        /// Total queue depth across all nodes.
        /// </summary>
        public required long QueueDepth { get; init; }
    }

    /// <summary>
    /// Request to scale out (add nodes to) the cluster.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public record ScaleOutRequest
    {
        /// <summary>
        /// Number of nodes to add.
        /// </summary>
        public required int NodeCount { get; init; }

        /// <summary>
        /// Reason for scaling out.
        /// </summary>
        public required string Reason { get; init; }

        /// <summary>
        /// The role for the new nodes.
        /// </summary>
        public ClusterNodeRole RequestedRole { get; init; } = ClusterNodeRole.Follower;
    }

    /// <summary>
    /// Request to scale in (remove nodes from) the cluster.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public record ScaleInRequest
    {
        /// <summary>
        /// Number of nodes to remove.
        /// </summary>
        public required int NodeCount { get; init; }

        /// <summary>
        /// Reason for scaling in.
        /// </summary>
        public required string Reason { get; init; }

        /// <summary>
        /// Specific node IDs to remove, if known. If empty, the auto-scaler chooses.
        /// </summary>
        public IReadOnlyList<string> PreferredNodeIds { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Result of a scaling operation.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public record ScalingResult
    {
        /// <summary>
        /// Whether the scaling operation succeeded.
        /// </summary>
        public required bool Success { get; init; }

        /// <summary>
        /// Number of nodes actually added or removed.
        /// </summary>
        public required int NodesAffected { get; init; }

        /// <summary>
        /// Human-readable message about the result.
        /// </summary>
        public string? Message { get; init; }

        /// <summary>
        /// Creates a successful scaling result.
        /// </summary>
        /// <param name="nodesAffected">The number of nodes affected.</param>
        /// <param name="message">Optional message.</param>
        /// <returns>A successful scaling result.</returns>
        public static ScalingResult Ok(int nodesAffected, string? message = null) =>
            new() { Success = true, NodesAffected = nodesAffected, Message = message };

        /// <summary>
        /// Creates a failed scaling result.
        /// </summary>
        /// <param name="message">The error message.</param>
        /// <returns>A failed scaling result.</returns>
        public static ScalingResult Error(string message) =>
            new() { Success = false, NodesAffected = 0, Message = message };
    }

    /// <summary>
    /// A scaling event describing what happened during scaling.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public record ScalingEvent
    {
        /// <summary>
        /// The type of scaling event.
        /// </summary>
        public required ScalingEventType EventType { get; init; }

        /// <summary>
        /// The scaling action that was taken.
        /// </summary>
        public required ScalingAction Action { get; init; }

        /// <summary>
        /// Number of nodes affected.
        /// </summary>
        public required int NodesAffected { get; init; }

        /// <summary>
        /// When the event occurred.
        /// </summary>
        public required DateTimeOffset Timestamp { get; init; }

        /// <summary>
        /// Optional detail about the event.
        /// </summary>
        public string? Detail { get; init; }
    }

    /// <summary>
    /// Types of scaling events.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public enum ScalingEventType
    {
        /// <summary>Scaling was evaluated.</summary>
        Evaluated,
        /// <summary>Scale-out was initiated.</summary>
        ScaleOutStarted,
        /// <summary>Scale-out completed.</summary>
        ScaleOutCompleted,
        /// <summary>Scale-out failed.</summary>
        ScaleOutFailed,
        /// <summary>Scale-in was initiated.</summary>
        ScaleInStarted,
        /// <summary>Scale-in completed.</summary>
        ScaleInCompleted,
        /// <summary>Scale-in failed.</summary>
        ScaleInFailed
    }

    /// <summary>
    /// Current state of the cluster from a scaling perspective.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public record ScalingState
    {
        /// <summary>
        /// Current number of nodes in the cluster.
        /// </summary>
        public required int CurrentNodeCount { get; init; }

        /// <summary>
        /// Whether a scaling operation is currently in progress.
        /// </summary>
        public required bool IsScaling { get; init; }

        /// <summary>
        /// The last scaling action that was taken, if any.
        /// </summary>
        public ScalingAction? LastAction { get; init; }

        /// <summary>
        /// When the last scaling action occurred.
        /// </summary>
        public DateTimeOffset? LastScaledAt { get; init; }
    }

    /// <summary>
    /// Possible scaling actions.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public enum ScalingAction
    {
        /// <summary>No scaling action needed.</summary>
        NoAction,
        /// <summary>Scale out -- add nodes.</summary>
        ScaleOut,
        /// <summary>Scale in -- remove nodes.</summary>
        ScaleIn
    }
}
