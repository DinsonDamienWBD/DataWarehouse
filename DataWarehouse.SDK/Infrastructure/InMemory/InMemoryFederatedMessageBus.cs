using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Distributed;
using DataWarehouse.SDK.Utilities;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Infrastructure.InMemory
{
    /// <summary>
    /// In-memory single-node implementation of <see cref="IFederatedMessageBus"/>.
    /// Extends <see cref="FederatedMessageBusBase"/> and routes all messages locally.
    /// Remote messaging throws because there are no remote nodes in single-node mode.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: In-memory implementation")]
    public sealed class InMemoryFederatedMessageBus : FederatedMessageBusBase
    {
        /// <summary>
        /// Initializes a new single-node federated message bus.
        /// </summary>
        /// <param name="localBus">The local message bus to delegate to.</param>
        /// <param name="clusterMembership">The cluster membership (should be InMemoryClusterMembership).</param>
        public InMemoryFederatedMessageBus(IMessageBus localBus, IClusterMembership clusterMembership)
            : base(localBus, clusterMembership)
        {
        }

        /// <inheritdoc />
        protected override Task SendToRemoteNodeAsync(string nodeId, string topic, PluginMessage message, CancellationToken ct)
        {
            throw new InvalidOperationException("Remote messaging not available in single-node mode.");
        }

        /// <inheritdoc />
        public override MessageRoutingDecision GetRoutingDecision(string topic, PluginMessage message) =>
            new()
            {
                Target = MessageRoutingTarget.Local,
                Reason = "Single-node mode: all messages route locally"
            };

        /// <inheritdoc />
        public override bool IsLocalMessage(string topic, PluginMessage message) => true;
    }
}
