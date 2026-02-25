using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Distributed;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Infrastructure.Distributed
{
    /// <summary>
    /// Load balancer strategy using consistent hashing for cache-friendly request routing.
    /// The same key always routes to the same node, with minimal disruption when nodes join/leave.
    /// Wraps a ConsistentHashRing and syncs ring membership with the available nodes in each context.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 29: Consistent hash load balancer")]
    public sealed class ConsistentHashLoadBalancer : ILoadBalancerStrategy
    {
        private readonly ConsistentHashRing _ring;
        private readonly BoundedDictionary<string, NodeHealthReport> _healthReports = new BoundedDictionary<string, NodeHealthReport>(1000);
        private readonly HashSet<string> _currentRingNodes = new(StringComparer.Ordinal);
        private readonly object _syncLock = new();

        /// <summary>
        /// Creates a new consistent hash load balancer.
        /// </summary>
        /// <param name="ring">The consistent hash ring to use for key-to-node mapping.</param>
        public ConsistentHashLoadBalancer(ConsistentHashRing ring)
        {
            _ring = ring ?? throw new ArgumentNullException(nameof(ring));
        }

        /// <inheritdoc />
        public string AlgorithmName => "ConsistentHash";

        /// <inheritdoc />
        public ClusterNode SelectNode(LoadBalancerContext context)
        {
            if (context.AvailableNodes.Count == 0)
            {
                throw new InvalidOperationException("No available nodes for load balancing.");
            }

            // Sync ring with current available nodes
            SyncRingWithAvailableNodes(context.AvailableNodes);

            try
            {
                string nodeId = _ring.GetNode(context.RequestKey);

                // Find matching ClusterNode in the available nodes
                var matchingNode = context.AvailableNodes
                    .FirstOrDefault(n => string.Equals(n.NodeId, nodeId, StringComparison.Ordinal));

                if (matchingNode != null)
                {
                    return matchingNode;
                }
            }
            catch (InvalidOperationException)
            {
                // Ring might be empty if sync race condition -- fall through
            }

            // Fallback: return first available node
            return context.AvailableNodes[0];
        }

        /// <inheritdoc />
        public Task<ClusterNode> SelectNodeAsync(LoadBalancerContext context, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(SelectNode(context));
        }

        /// <inheritdoc />
        public void ReportNodeHealth(string nodeId, NodeHealthReport report)
        {
            _healthReports[nodeId] = report;
        }

        /// <summary>
        /// Synchronizes the hash ring membership with the currently available nodes.
        /// Adds nodes present in the list but missing from the ring,
        /// and removes nodes in the ring but absent from the list.
        /// </summary>
        private void SyncRingWithAvailableNodes(IReadOnlyList<ClusterNode> nodes)
        {
            lock (_syncLock)
            {
                var currentNodes = new HashSet<string>(nodes.Select(n => n.NodeId), StringComparer.Ordinal);

                // Add nodes that are in the available list but not in the ring
                foreach (var nodeId in currentNodes)
                {
                    if (_currentRingNodes.Add(nodeId))
                    {
                        _ring.AddNode(nodeId);
                    }
                }

                // Remove nodes that are in the ring but not in the available list
                var toRemove = _currentRingNodes.Where(n => !currentNodes.Contains(n)).ToList();
                foreach (var nodeId in toRemove)
                {
                    _currentRingNodes.Remove(nodeId);
                    _ring.RemoveNode(nodeId);
                }
            }
        }
    }
}
