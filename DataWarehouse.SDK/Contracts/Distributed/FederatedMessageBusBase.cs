using DataWarehouse.SDK.Utilities;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Distributed
{
    /// <summary>
    /// Abstract base class for federated message bus implementations (DIST-08).
    /// Wraps a local <see cref="IMessageBus"/> with transparent local/remote routing.
    /// <para>
    /// All standard IMessageBus methods delegate to the local bus by default,
    /// ensuring that existing code works unchanged. Subclasses override
    /// <see cref="SendToRemoteNodeAsync"/> to implement actual remote transport (gRPC, HTTP, etc.).
    /// </para>
    /// <para>
    /// In single-node mode, <see cref="GetRoutingDecision"/> returns Local for all messages,
    /// so behavior is identical to a plain IMessageBus.
    /// </para>
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Federated message bus base class")]
    public abstract class FederatedMessageBusBase : IFederatedMessageBus
    {
        /// <summary>
        /// The local in-process message bus.
        /// </summary>
        protected readonly IMessageBus LocalBus;

        /// <summary>
        /// The cluster membership for node discovery and routing decisions.
        /// </summary>
        protected readonly IClusterMembership ClusterMembershipInstance;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederatedMessageBusBase"/> class.
        /// </summary>
        /// <param name="localBus">The local in-process message bus to delegate to.</param>
        /// <param name="clusterMembership">The cluster membership for topology awareness.</param>
        protected FederatedMessageBusBase(IMessageBus localBus, IClusterMembership clusterMembership)
        {
            LocalBus = localBus ?? throw new ArgumentNullException(nameof(localBus));
            ClusterMembershipInstance = clusterMembership ?? throw new ArgumentNullException(nameof(clusterMembership));
        }

        /// <inheritdoc />
        public IClusterMembership ClusterMembership => ClusterMembershipInstance;

        #region IMessageBus delegation to local bus

        /// <summary>
        /// Publishes a message with routing awareness.
        /// Calls <see cref="GetRoutingDecision"/> to determine whether to route locally or remotely.
        /// </summary>
        /// <param name="topic">The message topic.</param>
        /// <param name="message">The message to publish.</param>
        /// <param name="ct">Cancellation token for the operation.</param>
        /// <returns>A task representing the publish operation.</returns>
        public virtual async Task PublishAsync(string topic, PluginMessage message, CancellationToken ct = default)
        {
            var decision = GetRoutingDecision(topic, message);

            switch (decision.Target)
            {
                case MessageRoutingTarget.Local:
                    await LocalBus.PublishAsync(topic, message, ct);
                    break;

                case MessageRoutingTarget.Remote:
                    if (decision.TargetNodeId != null)
                    {
                        await SendToRemoteNodeAsync(decision.TargetNodeId, topic, message, ct);
                    }
                    break;

                case MessageRoutingTarget.Broadcast:
                    await LocalBus.PublishAsync(topic, message, ct);
                    if (decision.BroadcastNodeIds != null)
                    {
                        foreach (var nodeId in decision.BroadcastNodeIds)
                        {
                            await SendToRemoteNodeAsync(nodeId, topic, message, ct);
                        }
                    }
                    break;

                case MessageRoutingTarget.ConsistentHash:
                    if (decision.TargetNodeId != null)
                    {
                        var selfId = ClusterMembershipInstance.GetSelf().NodeId;
                        if (string.Equals(decision.TargetNodeId, selfId, StringComparison.Ordinal))
                        {
                            await LocalBus.PublishAsync(topic, message, ct);
                        }
                        else
                        {
                            await SendToRemoteNodeAsync(decision.TargetNodeId, topic, message, ct);
                        }
                    }
                    break;

                default:
                    await LocalBus.PublishAsync(topic, message, ct);
                    break;
            }
        }

        /// <inheritdoc />
        public virtual Task PublishAndWaitAsync(string topic, PluginMessage message, CancellationToken ct = default) =>
            LocalBus.PublishAndWaitAsync(topic, message, ct);

        /// <inheritdoc />
        public virtual Task<MessageResponse> SendAsync(string topic, PluginMessage message, CancellationToken ct = default) =>
            LocalBus.SendAsync(topic, message, ct);

        /// <inheritdoc />
        public virtual Task<MessageResponse> SendAsync(string topic, PluginMessage message, TimeSpan timeout, CancellationToken ct = default) =>
            LocalBus.SendAsync(topic, message, timeout, ct);

        /// <inheritdoc />
        public virtual IDisposable Subscribe(string topic, Func<PluginMessage, Task> handler) =>
            LocalBus.Subscribe(topic, handler);

        /// <inheritdoc />
        public virtual IDisposable Subscribe(string topic, Func<PluginMessage, Task<MessageResponse>> handler) =>
            LocalBus.Subscribe(topic, handler);

        /// <inheritdoc />
        public virtual IDisposable SubscribePattern(string pattern, Func<PluginMessage, Task> handler) =>
            LocalBus.SubscribePattern(pattern, handler);

        /// <inheritdoc />
        public virtual void Unsubscribe(string topic) =>
            LocalBus.Unsubscribe(topic);

        /// <inheritdoc />
        public virtual IEnumerable<string> GetActiveTopics() =>
            LocalBus.GetActiveTopics();

        #endregion

        #region IFederatedMessageBus methods

        /// <inheritdoc />
        public virtual async Task PublishToNodeAsync(string nodeId, string topic, PluginMessage message, CancellationToken ct = default)
        {
            var selfId = ClusterMembershipInstance.GetSelf().NodeId;
            if (string.Equals(nodeId, selfId, StringComparison.Ordinal))
            {
                await LocalBus.PublishAsync(topic, message, ct);
            }
            else
            {
                await SendToRemoteNodeAsync(nodeId, topic, message, ct);
            }
        }

        /// <inheritdoc />
        public virtual async Task PublishToAllNodesAsync(string topic, PluginMessage message, CancellationToken ct = default)
        {
            // Publish locally first
            await LocalBus.PublishAsync(topic, message, ct);

            // Then to all remote nodes
            var self = ClusterMembershipInstance.GetSelf();
            var members = ClusterMembershipInstance.GetMembers();
            foreach (var member in members)
            {
                if (!string.Equals(member.NodeId, self.NodeId, StringComparison.Ordinal))
                {
                    await SendToRemoteNodeAsync(member.NodeId, topic, message, ct);
                }
            }
        }

        /// <summary>
        /// Gets the routing decision for a message.
        /// Default implementation returns Local for all messages (single-node safe).
        /// Override to implement custom routing logic.
        /// </summary>
        /// <param name="topic">The message topic.</param>
        /// <param name="message">The message to route.</param>
        /// <returns>A routing decision indicating where to send the message.</returns>
        public virtual MessageRoutingDecision GetRoutingDecision(string topic, PluginMessage message) =>
            new()
            {
                Target = MessageRoutingTarget.Local,
                Reason = "Default: all messages route locally"
            };

        /// <summary>
        /// Checks whether a message targets this node.
        /// Default implementation returns true (single-node safe).
        /// </summary>
        /// <param name="topic">The message topic.</param>
        /// <param name="message">The message to check.</param>
        /// <returns>True if the message targets this node.</returns>
        public virtual bool IsLocalMessage(string topic, PluginMessage message) => true;

        #endregion

        /// <summary>
        /// Sends a message to a remote node.
        /// Subclasses implement the actual transport mechanism (gRPC, HTTP, TCP, etc.).
        /// </summary>
        /// <param name="nodeId">The target node identifier.</param>
        /// <param name="topic">The message topic.</param>
        /// <param name="message">The message to send.</param>
        /// <param name="ct">Cancellation token for the operation.</param>
        /// <returns>A task representing the send operation.</returns>
        protected abstract Task SendToRemoteNodeAsync(string nodeId, string topic, PluginMessage message, CancellationToken ct);
    }
}
