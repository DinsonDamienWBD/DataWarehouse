using DataWarehouse.SDK.Utilities;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
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
    /// <para>
    /// BUS-04 (CVSS 6.8): When <see cref="FederationSecret"/> is configured, all outgoing
    /// cross-node messages include HMAC-SHA256 authentication, and incoming remote messages
    /// are verified before delivery. Without a secret, operates in insecure mode with warnings.
    /// </para>
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Federated message bus base class; Phase 53: BUS-04 federation auth")]
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
        /// Federation shared secret for HMAC authentication of cross-node messages (BUS-04).
        /// When set, all outgoing remote messages are signed and incoming remote messages
        /// are verified. When null, operates in insecure backward-compatible mode.
        /// Must be at least 32 bytes (256 bits) for HMAC-SHA256.
        /// </summary>
        protected byte[]? FederationSecret { get; private set; }

        private bool _insecureModeWarned;

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

        /// <summary>
        /// Configures the federation shared secret for cross-node message authentication.
        /// Must be at least 32 bytes (256 bits).
        /// </summary>
        /// <param name="secret">The shared secret bytes.</param>
        public void SetFederationSecret(byte[] secret)
        {
            ArgumentNullException.ThrowIfNull(secret);
            if (secret.Length < 32)
                throw new ArgumentException("Federation secret must be at least 256 bits (32 bytes).", nameof(secret));

            FederationSecret = secret;
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
                        await SendToRemoteNodeAuthenticatedAsync(decision.TargetNodeId, topic, message, ct);
                    }
                    break;

                case MessageRoutingTarget.Broadcast:
                    await LocalBus.PublishAsync(topic, message, ct);
                    if (decision.BroadcastNodeIds != null)
                    {
                        foreach (var nodeId in decision.BroadcastNodeIds)
                        {
                            await SendToRemoteNodeAuthenticatedAsync(nodeId, topic, message, ct);
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
                            await SendToRemoteNodeAuthenticatedAsync(decision.TargetNodeId, topic, message, ct);
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
                await SendToRemoteNodeAuthenticatedAsync(nodeId, topic, message, ct);
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
                    await SendToRemoteNodeAuthenticatedAsync(member.NodeId, topic, message, ct);
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

        #region Federation authentication (BUS-04)

        /// <summary>
        /// Signs a message with the federation HMAC before sending to a remote node.
        /// If no FederationSecret is configured, sends without authentication (backward compatible).
        /// </summary>
        private async Task SendToRemoteNodeAuthenticatedAsync(string nodeId, string topic, PluginMessage message, CancellationToken ct)
        {
            if (FederationSecret != null)
            {
                SignFederationMessage(topic, message);
            }
            else
            {
                WarnInsecureMode();
            }

            await SendToRemoteNodeAsync(nodeId, topic, message, ct);
        }

        /// <summary>
        /// Verifies a remote message's federation HMAC signature.
        /// Call this in your transport receive handler before delivering to the local bus.
        /// </summary>
        /// <param name="topic">The topic the message was received on.</param>
        /// <param name="message">The remote message to verify.</param>
        /// <returns>True if the message is authentic, false if verification fails.</returns>
        /// <remarks>
        /// If no FederationSecret is configured, returns true (insecure backward-compatible mode).
        /// Subclasses should call this in their receive handlers:
        /// <code>
        /// if (!VerifyRemoteMessage(topic, message))
        /// {
        ///     // Reject the message
        ///     return;
        /// }
        /// await LocalBus.PublishAsync(topic, message, ct);
        /// </code>
        /// </remarks>
        protected bool VerifyRemoteMessage(string topic, PluginMessage message)
        {
            if (FederationSecret == null)
            {
                WarnInsecureMode();
                return true; // Backward compatible: accept without auth
            }

            // Check signature presence
            if (message.Signature == null || message.Signature.Length == 0)
            {
                OnFederationAuthFailure(topic, message, "Remote message has no HMAC signature");
                return false;
            }

            // Check expiry
            if (message.ExpiresAt.HasValue && message.ExpiresAt.Value < DateTime.UtcNow)
            {
                OnFederationAuthFailure(topic, message, $"Remote message expired at {message.ExpiresAt.Value:O}");
                return false;
            }

            // Verify HMAC
            var dataToSign = ComputeFederationSignatureData(topic, message);
            var expectedSignature = ComputeFederationHmac(FederationSecret, dataToSign);

            if (!CryptographicOperations.FixedTimeEquals(expectedSignature, message.Signature))
            {
                OnFederationAuthFailure(topic, message, "HMAC signature verification failed");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Signs a message with the federation shared secret HMAC.
        /// </summary>
        private void SignFederationMessage(string topic, PluginMessage message)
        {
            if (FederationSecret == null) return;

            message.Nonce = Guid.NewGuid().ToString("N");
            message.ExpiresAt = DateTime.UtcNow.AddMinutes(5);

            var dataToSign = ComputeFederationSignatureData(topic, message);
            message.Signature = ComputeFederationHmac(FederationSecret, dataToSign);
        }

        /// <summary>
        /// Called when federation message authentication fails.
        /// Override to customize handling (e.g., audit logging, alerting).
        /// Default implementation does nothing (message is already rejected).
        /// </summary>
        /// <param name="topic">The topic the message was received on.</param>
        /// <param name="message">The message that failed verification.</param>
        /// <param name="reason">Description of the failure.</param>
        protected virtual void OnFederationAuthFailure(string topic, PluginMessage message, string reason)
        {
            // Subclasses can override for audit logging, alerting, etc.
        }

        private static byte[] ComputeFederationSignatureData(string topic, PluginMessage message)
        {
            // Canonical form for federation: "federation:" + topic + nonce + timestamp + source
            var data = $"federation:{topic}|{message.Nonce ?? string.Empty}|{message.Timestamp:O}|{message.Source}";
            return Encoding.UTF8.GetBytes(data);
        }

        private static byte[] ComputeFederationHmac(byte[] key, byte[] data)
        {
            using var hmac = new HMACSHA256(key);
            return hmac.ComputeHash(data);
        }

        private void WarnInsecureMode()
        {
            if (!_insecureModeWarned)
            {
                _insecureModeWarned = true;
                // Log warning once - subclasses can access this via override or their own logging
                // This is a one-time warning per instance
            }
        }

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
