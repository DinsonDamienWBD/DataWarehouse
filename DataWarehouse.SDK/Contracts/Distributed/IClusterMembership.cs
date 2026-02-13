using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Distributed
{
    /// <summary>
    /// Contract for cluster membership management (DIST-01).
    /// Provides node join/leave/discovery and health monitoring capabilities
    /// for distributed deployments.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public interface IClusterMembership
    {
        /// <summary>
        /// Raised when the cluster membership changes (node join, leave, failure, leader change).
        /// </summary>
        event Action<ClusterMembershipEvent>? OnMembershipChanged;

        /// <summary>
        /// Gets all current cluster members.
        /// </summary>
        /// <returns>A read-only list of all cluster nodes.</returns>
        IReadOnlyList<ClusterNode> GetMembers();

        /// <summary>
        /// Gets the current cluster leader, or null if no leader is elected.
        /// </summary>
        /// <returns>The leader node, or null.</returns>
        ClusterNode? GetLeader();

        /// <summary>
        /// Gets the local node representing this instance.
        /// </summary>
        /// <returns>The local cluster node.</returns>
        ClusterNode GetSelf();

        /// <summary>
        /// Joins the cluster with the specified join request.
        /// </summary>
        /// <param name="request">The cluster join request containing node identity and role.</param>
        /// <param name="ct">Cancellation token for the join operation.</param>
        /// <returns>A task representing the join operation.</returns>
        Task JoinAsync(ClusterJoinRequest request, CancellationToken ct = default);

        /// <summary>
        /// Gracefully leaves the cluster.
        /// </summary>
        /// <param name="reason">The reason for leaving.</param>
        /// <param name="ct">Cancellation token for the leave operation.</param>
        /// <returns>A task representing the leave operation.</returns>
        Task LeaveAsync(string reason, CancellationToken ct = default);

        /// <summary>
        /// Checks whether a specific node is healthy.
        /// </summary>
        /// <param name="nodeId">The node identifier to check.</param>
        /// <param name="ct">Cancellation token for the health check.</param>
        /// <returns>True if the node is healthy; otherwise false.</returns>
        Task<bool> IsHealthyAsync(string nodeId, CancellationToken ct = default);
    }

    /// <summary>
    /// Represents a node in the cluster.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public record ClusterNode
    {
        /// <summary>
        /// Unique identifier for this node.
        /// </summary>
        public required string NodeId { get; init; }

        /// <summary>
        /// Network address of the node (hostname or IP).
        /// </summary>
        public required string Address { get; init; }

        /// <summary>
        /// Port the node listens on.
        /// </summary>
        public required int Port { get; init; }

        /// <summary>
        /// The role this node plays in the cluster.
        /// </summary>
        public required ClusterNodeRole Role { get; init; }

        /// <summary>
        /// Current status of the node.
        /// </summary>
        public required ClusterNodeStatus Status { get; init; }

        /// <summary>
        /// When this node joined the cluster.
        /// </summary>
        public required DateTimeOffset JoinedAt { get; init; }

        /// <summary>
        /// Additional metadata associated with this node.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Request to join a cluster.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public record ClusterJoinRequest
    {
        /// <summary>
        /// Unique identifier for the joining node.
        /// </summary>
        public required string NodeId { get; init; }

        /// <summary>
        /// Network address of the joining node.
        /// </summary>
        public required string Address { get; init; }

        /// <summary>
        /// Port the joining node listens on.
        /// </summary>
        public required int Port { get; init; }

        /// <summary>
        /// The role the joining node requests.
        /// </summary>
        public required ClusterNodeRole RequestedRole { get; init; }

        /// <summary>
        /// Additional metadata for the joining node.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Event describing a cluster membership change.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public record ClusterMembershipEvent
    {
        /// <summary>
        /// The type of membership event.
        /// </summary>
        public required ClusterMembershipEventType EventType { get; init; }

        /// <summary>
        /// The node involved in the event.
        /// </summary>
        public required ClusterNode Node { get; init; }

        /// <summary>
        /// When the event occurred.
        /// </summary>
        public required DateTimeOffset Timestamp { get; init; }

        /// <summary>
        /// Optional reason for the event.
        /// </summary>
        public string? Reason { get; init; }
    }

    /// <summary>
    /// Roles a node can play in the cluster.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public enum ClusterNodeRole
    {
        /// <summary>The node is the cluster leader responsible for coordination.</summary>
        Leader,
        /// <summary>The node follows the leader and replicates state.</summary>
        Follower,
        /// <summary>The node observes the cluster but does not participate in consensus.</summary>
        Observer,
        /// <summary>The node is a candidate for leader election.</summary>
        Candidate
    }

    /// <summary>
    /// Status of a cluster node.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public enum ClusterNodeStatus
    {
        /// <summary>Node is active and healthy.</summary>
        Active,
        /// <summary>Node is in the process of joining the cluster.</summary>
        Joining,
        /// <summary>Node is in the process of leaving the cluster.</summary>
        Leaving,
        /// <summary>Node is suspected of being unhealthy.</summary>
        Suspected,
        /// <summary>Node has been confirmed dead.</summary>
        Dead
    }

    /// <summary>
    /// Types of cluster membership events.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public enum ClusterMembershipEventType
    {
        /// <summary>A node has joined the cluster.</summary>
        NodeJoined,
        /// <summary>A node has left the cluster.</summary>
        NodeLeft,
        /// <summary>A node is suspected of being unhealthy.</summary>
        NodeSuspected,
        /// <summary>A node has been confirmed dead.</summary>
        NodeDead,
        /// <summary>The cluster leader has changed.</summary>
        LeaderChanged
    }
}
