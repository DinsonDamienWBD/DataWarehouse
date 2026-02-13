using DataWarehouse.SDK.Utilities;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Distributed
{
    /// <summary>
    /// Federated message bus that extends <see cref="IMessageBus"/> with transparent
    /// local/remote routing for distributed deployments (DIST-08).
    /// <para>
    /// When used as a drop-in replacement for IMessageBus, existing code works unchanged.
    /// In single-node mode, all messages route locally. In a cluster, messages are
    /// automatically routed to the correct node based on topic, content, or consistent hashing.
    /// </para>
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Federated message bus")]
    public interface IFederatedMessageBus : IMessageBus
    {
        /// <summary>
        /// Gets the cluster membership for topology awareness.
        /// </summary>
        IClusterMembership ClusterMembership { get; }

        /// <summary>
        /// Publishes a message to a specific node by ID.
        /// If the target node is this node, the message is delivered locally.
        /// </summary>
        /// <param name="nodeId">The target node identifier.</param>
        /// <param name="topic">The message topic.</param>
        /// <param name="message">The message to publish.</param>
        /// <param name="ct">Cancellation token for the operation.</param>
        /// <returns>A task representing the publish operation.</returns>
        Task PublishToNodeAsync(string nodeId, string topic, PluginMessage message, CancellationToken ct = default);

        /// <summary>
        /// Publishes a message to all nodes in the cluster (local + remote).
        /// </summary>
        /// <param name="topic">The message topic.</param>
        /// <param name="message">The message to publish.</param>
        /// <param name="ct">Cancellation token for the operation.</param>
        /// <returns>A task representing the broadcast operation.</returns>
        Task PublishToAllNodesAsync(string topic, PluginMessage message, CancellationToken ct = default);

        /// <summary>
        /// Inspects the routing decision for a message without sending it.
        /// Useful for diagnostics and testing.
        /// </summary>
        /// <param name="topic">The message topic.</param>
        /// <param name="message">The message to inspect routing for.</param>
        /// <returns>The routing decision that would be made.</returns>
        MessageRoutingDecision GetRoutingDecision(string topic, PluginMessage message);

        /// <summary>
        /// Checks whether a message targets this node.
        /// </summary>
        /// <param name="topic">The message topic.</param>
        /// <param name="message">The message to check.</param>
        /// <returns>True if the message targets this node; otherwise false.</returns>
        bool IsLocalMessage(string topic, PluginMessage message);
    }

    /// <summary>
    /// Contract for consistent hash ring used to determine message routing (DIST-08).
    /// Maps keys to nodes using a virtual-node-based consistent hash ring.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Federated message bus")]
    public interface IConsistentHashRing
    {
        /// <summary>
        /// Gets the node responsible for the given key.
        /// </summary>
        /// <param name="key">The key to hash.</param>
        /// <returns>The node identifier responsible for this key.</returns>
        string GetNode(string key);

        /// <summary>
        /// Gets N nodes responsible for the given key (for replication).
        /// </summary>
        /// <param name="key">The key to hash.</param>
        /// <param name="count">The number of nodes to return.</param>
        /// <returns>A read-only list of node identifiers.</returns>
        IReadOnlyList<string> GetNodes(string key, int count);

        /// <summary>
        /// Adds a node to the hash ring.
        /// </summary>
        /// <param name="nodeId">The node identifier to add.</param>
        void AddNode(string nodeId);

        /// <summary>
        /// Removes a node from the hash ring.
        /// </summary>
        /// <param name="nodeId">The node identifier to remove.</param>
        void RemoveNode(string nodeId);

        /// <summary>
        /// Gets the number of virtual nodes per physical node.
        /// </summary>
        int VirtualNodeCount { get; }
    }

    /// <summary>
    /// Routing decision made by the federated message bus.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Federated message bus")]
    public record MessageRoutingDecision
    {
        /// <summary>
        /// The routing target.
        /// </summary>
        public required MessageRoutingTarget Target { get; init; }

        /// <summary>
        /// The specific target node ID when routing to a remote node.
        /// </summary>
        public string? TargetNodeId { get; init; }

        /// <summary>
        /// The list of node IDs when broadcasting.
        /// </summary>
        public IReadOnlyList<string>? BroadcastNodeIds { get; init; }

        /// <summary>
        /// Reason for the routing decision.
        /// </summary>
        public required string Reason { get; init; }
    }

    /// <summary>
    /// Possible routing targets for a message.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Federated message bus")]
    public enum MessageRoutingTarget
    {
        /// <summary>Message is delivered locally on this node.</summary>
        Local,
        /// <summary>Message is sent to a specific remote node.</summary>
        Remote,
        /// <summary>Message is sent to all nodes in the cluster.</summary>
        Broadcast,
        /// <summary>Message is routed via consistent hashing.</summary>
        ConsistentHash
    }
}
