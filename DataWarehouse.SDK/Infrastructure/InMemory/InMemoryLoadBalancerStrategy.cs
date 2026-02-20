using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Distributed;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Infrastructure.InMemory
{
    /// <summary>
    /// In-memory single-node implementation of <see cref="ILoadBalancerStrategy"/>.
    /// Always selects the first available node (self in single-node mode).
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: In-memory implementation")]
    public sealed class InMemoryLoadBalancerStrategy : ILoadBalancerStrategy
    {
        private readonly BoundedDictionary<string, NodeHealthReport> _healthReports = new BoundedDictionary<string, NodeHealthReport>(1000);

        /// <inheritdoc />
        public string AlgorithmName => "SingleNode";

        /// <inheritdoc />
        public ClusterNode SelectNode(LoadBalancerContext context)
        {
            if (context.AvailableNodes.Count == 0)
            {
                throw new InvalidOperationException("No available nodes for load balancing.");
            }

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
    }
}
