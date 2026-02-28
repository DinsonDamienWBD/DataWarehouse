using System;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Federation.Orchestration;

/// <summary>
/// Defines the contract for the Federation Orchestrator, which manages multi-node
/// storage cluster lifecycle including node registration, health monitoring, and
/// topology management.
/// </summary>
/// <remarks>
/// <para>
/// IFederationOrchestrator is the central coordinator for a federated object storage cluster.
/// It handles:
/// </para>
/// <list type="bullet">
///   <item><description>Node registration and unregistration</description></item>
///   <item><description>Heartbeat processing and health monitoring</description></item>
///   <item><description>Cluster topology state management</description></item>
///   <item><description>Graceful scale-in/scale-out coordination</description></item>
/// </list>
/// <para>
/// <strong>Raft Integration:</strong> Topology changes (node add/remove) are proposed via
/// the Raft consensus engine to ensure all nodes in the cluster have a consistent view
/// of the topology. This prevents split-brain scenarios during network partitions.
/// </para>
/// </remarks>
public interface IFederationOrchestrator : IDisposable
{
    /// <summary>
    /// Starts the orchestrator and begins monitoring cluster health.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// Stops the orchestrator and cleans up resources.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task StopAsync(CancellationToken ct = default);

    /// <summary>
    /// Registers a new node in the cluster.
    /// </summary>
    /// <param name="registration">The node registration metadata.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Registration is proposed via Raft (if available) to ensure all nodes in the cluster
    /// see the new node. After successful registration, the node becomes eligible for routing.
    /// </remarks>
    Task RegisterNodeAsync(NodeRegistration registration, CancellationToken ct = default);

    /// <summary>
    /// Registers a new node in the cluster, optionally bypassing the topology rate-limit guard.
    /// Use with caution: skipping the rate limit should only be done for initial cluster bootstrap
    /// or operator-initiated batch registration.
    /// </summary>
    /// <param name="registration">The node registration metadata.</param>
    /// <param name="skipTopologyRateLimit">
    /// When <c>true</c>, the per-node topology rate-limit cool-down is skipped.
    /// Intended for bootstrap or administrative use only.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task RegisterNodeAsync(NodeRegistration registration, bool skipTopologyRateLimit, CancellationToken ct = default);

    /// <summary>
    /// Unregisters a node from the cluster.
    /// </summary>
    /// <param name="nodeId">The unique identifier of the node to unregister.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Unregistration triggers graceful scale-in: the node is removed from routing tables,
    /// and data rebalancing may be initiated (future work).
    /// </remarks>
    Task UnregisterNodeAsync(string nodeId, CancellationToken ct = default);

    /// <summary>
    /// Processes a heartbeat from a node, updating its health and capacity metrics.
    /// </summary>
    /// <param name="heartbeat">The heartbeat data.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SendHeartbeatAsync(NodeHeartbeat heartbeat, CancellationToken ct = default);

    /// <summary>
    /// Gets the current cluster topology.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The current <see cref="ClusterTopology"/>.</returns>
    Task<ClusterTopology> GetTopologyAsync(CancellationToken ct = default);
}
