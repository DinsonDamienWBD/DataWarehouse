using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Distributed
{
    /// <summary>
    /// Contract for pluggable load balancing strategies (DIST-02).
    /// Implementations select target nodes for request distribution
    /// based on node health, load, and algorithm-specific criteria.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public interface ILoadBalancerStrategy
    {
        /// <summary>
        /// Gets the name of the load balancing algorithm (e.g., "RoundRobin", "LeastConnections").
        /// </summary>
        string AlgorithmName { get; }

        /// <summary>
        /// Selects a target node synchronously based on the given context.
        /// </summary>
        /// <param name="context">The load balancer context containing available nodes and request metadata.</param>
        /// <returns>The selected cluster node.</returns>
        ClusterNode SelectNode(LoadBalancerContext context);

        /// <summary>
        /// Selects a target node asynchronously, allowing for I/O-based selection (e.g., latency probing).
        /// </summary>
        /// <param name="context">The load balancer context.</param>
        /// <param name="ct">Cancellation token for the selection operation.</param>
        /// <returns>The selected cluster node.</returns>
        Task<ClusterNode> SelectNodeAsync(LoadBalancerContext context, CancellationToken ct = default);

        /// <summary>
        /// Reports a node's health metrics so the load balancer can make informed decisions.
        /// </summary>
        /// <param name="nodeId">The node identifier.</param>
        /// <param name="report">The health report for the node.</param>
        void ReportNodeHealth(string nodeId, NodeHealthReport report);
    }

    /// <summary>
    /// Context provided to the load balancer for node selection.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public record LoadBalancerContext
    {
        /// <summary>
        /// A key identifying the request, used for consistent hashing.
        /// </summary>
        public required string RequestKey { get; init; }

        /// <summary>
        /// The list of available nodes to choose from.
        /// </summary>
        public required IReadOnlyList<ClusterNode> AvailableNodes { get; init; }

        /// <summary>
        /// Additional metadata for the load balancer to consider.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Health report for a node, used by load balancers to adjust routing decisions.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public record NodeHealthReport
    {
        /// <summary>
        /// CPU usage as a percentage (0.0 to 100.0).
        /// </summary>
        public required double CpuUsage { get; init; }

        /// <summary>
        /// Memory usage as a percentage (0.0 to 100.0).
        /// </summary>
        public required double MemoryUsage { get; init; }

        /// <summary>
        /// Number of active connections to this node.
        /// </summary>
        public required long ActiveConnections { get; init; }

        /// <summary>
        /// Average latency for requests to this node.
        /// </summary>
        public required TimeSpan AverageLatency { get; init; }

        /// <summary>
        /// When this report was generated.
        /// </summary>
        public required DateTimeOffset ReportedAt { get; init; }
    }
}
