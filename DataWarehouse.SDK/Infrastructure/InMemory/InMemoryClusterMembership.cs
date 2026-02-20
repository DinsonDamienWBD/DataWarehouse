using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Distributed;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Infrastructure.InMemory
{
    /// <summary>
    /// In-memory single-node implementation of <see cref="IClusterMembership"/>.
    /// Always reports self as the only cluster member with Leader role.
    /// Suitable for single-node deployments with zero configuration.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: In-memory implementation")]
    public sealed class InMemoryClusterMembership : IClusterMembership
    {
        private readonly ClusterNode _self;

        /// <summary>
        /// Initializes a new single-node cluster membership.
        /// </summary>
        /// <param name="nodeId">The node identifier.</param>
        /// <param name="address">The node address. Default: "localhost".</param>
        /// <param name="port">The node port. Default: 5000.</param>
        public InMemoryClusterMembership(string nodeId, string address = "localhost", int port = 5000)
        {
            _self = new ClusterNode
            {
                NodeId = nodeId,
                Address = address,
                Port = port,
                Role = ClusterNodeRole.Leader,
                Status = ClusterNodeStatus.Active,
                JoinedAt = DateTimeOffset.UtcNow
            };
        }

        /// <inheritdoc />
        public event Action<ClusterMembershipEvent>? OnMembershipChanged;

        /// <inheritdoc />
        public IReadOnlyList<ClusterNode> GetMembers() => new[] { _self };

        /// <inheritdoc />
        public ClusterNode? GetLeader() => _self;

        /// <inheritdoc />
        public ClusterNode GetSelf() => _self;

        /// <inheritdoc />
        public Task JoinAsync(ClusterJoinRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            OnMembershipChanged?.Invoke(new ClusterMembershipEvent
            {
                EventType = ClusterMembershipEventType.NodeJoined,
                Node = _self,
                Timestamp = DateTimeOffset.UtcNow
            });
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task LeaveAsync(string reason, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            OnMembershipChanged?.Invoke(new ClusterMembershipEvent
            {
                EventType = ClusterMembershipEventType.NodeLeft,
                Node = _self,
                Timestamp = DateTimeOffset.UtcNow,
                Reason = reason
            });
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<bool> IsHealthyAsync(string nodeId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(string.Equals(nodeId, _self.NodeId, StringComparison.Ordinal));
        }
    }
}
