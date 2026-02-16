using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Distributed;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Infrastructure.Distributed
{
    /// <summary>
    /// Configuration for resource-aware load balancing.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 29: Resource-aware load balancer")]
    public sealed record ResourceAwareConfiguration
    {
        /// <summary>
        /// CPU usage threshold (percentage) above which a node is excluded.
        /// </summary>
        public double CpuThresholdPercent { get; init; } = 90.0;

        /// <summary>
        /// Memory usage threshold (percentage) above which a node is excluded.
        /// </summary>
        public double MemoryThresholdPercent { get; init; } = 90.0;

        /// <summary>
        /// Maximum active connections above which a node is excluded.
        /// </summary>
        public long MaxActiveConnections { get; init; } = 10_000;

        /// <summary>
        /// Weight for CPU usage in composite health score.
        /// </summary>
        public double CpuWeight { get; init; } = 0.3;

        /// <summary>
        /// Weight for memory usage in composite health score.
        /// </summary>
        public double MemoryWeight { get; init; } = 0.3;

        /// <summary>
        /// Weight for connection count in composite health score.
        /// </summary>
        public double ConnectionsWeight { get; init; } = 0.2;

        /// <summary>
        /// Weight for latency in composite health score.
        /// </summary>
        public double LatencyWeight { get; init; } = 0.2;

        /// <summary>
        /// Health reports older than this duration are considered stale and the node is treated as healthy.
        /// </summary>
        public TimeSpan HealthReportStaleness { get; init; } = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Load balancer that routes requests to nodes based on real-time health metrics.
    /// Excludes overloaded nodes (above CPU/memory/connection thresholds) and selects from
    /// remaining nodes using weighted random selection based on composite health score.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 29: Resource-aware load balancer")]
    public sealed class ResourceAwareLoadBalancer : ILoadBalancerStrategy
    {
        private readonly ResourceAwareConfiguration _config;
        private readonly ConcurrentDictionary<string, NodeHealthReport> _healthReports = new();

        /// <summary>
        /// Creates a new resource-aware load balancer.
        /// </summary>
        /// <param name="config">Optional configuration for thresholds and weights.</param>
        public ResourceAwareLoadBalancer(ResourceAwareConfiguration? config = null)
        {
            _config = config ?? new ResourceAwareConfiguration();
        }

        /// <inheritdoc />
        public string AlgorithmName => "ResourceAware";

        /// <inheritdoc />
        public void ReportNodeHealth(string nodeId, NodeHealthReport report)
        {
            _healthReports[nodeId] = report;
        }

        /// <inheritdoc />
        public ClusterNode SelectNode(LoadBalancerContext context)
        {
            if (context.AvailableNodes.Count == 0)
            {
                throw new InvalidOperationException("No available nodes for load balancing.");
            }

            // Build list of eligible nodes with their composite scores
            var now = DateTimeOffset.UtcNow;
            var eligible = new List<(ClusterNode Node, double Score)>();

            foreach (var node in context.AvailableNodes)
            {
                if (_healthReports.TryGetValue(node.NodeId, out var report)
                    && (now - report.ReportedAt) < _config.HealthReportStaleness)
                {
                    // Fresh health report -- check thresholds
                    if (report.CpuUsage > _config.CpuThresholdPercent) continue;
                    if (report.MemoryUsage > _config.MemoryThresholdPercent) continue;
                    if (report.ActiveConnections > _config.MaxActiveConnections) continue;

                    // Compute composite health score (higher = healthier)
                    double cpuScore = (100.0 - report.CpuUsage) / 100.0;
                    double memScore = (100.0 - report.MemoryUsage) / 100.0;
                    double connScore = Math.Max(0, 1.0 - (report.ActiveConnections / (double)_config.MaxActiveConnections));
                    double latScore = Math.Max(0, 1.0 - (report.AverageLatency.TotalMilliseconds / 1000.0));

                    double compositeScore =
                        cpuScore * _config.CpuWeight +
                        memScore * _config.MemoryWeight +
                        connScore * _config.ConnectionsWeight +
                        latScore * _config.LatencyWeight;

                    eligible.Add((node, Math.Max(compositeScore, 0.001))); // Ensure minimum positive score
                }
                else
                {
                    // No report or stale report -- assume healthy with neutral score
                    eligible.Add((node, 0.5));
                }
            }

            // Safety fallback: if all nodes were excluded, include all with equal weight
            if (eligible.Count == 0)
            {
                foreach (var node in context.AvailableNodes)
                {
                    eligible.Add((node, 1.0));
                }
            }

            // Weighted random selection using RandomNumberGenerator
            return WeightedRandomSelect(eligible);
        }

        /// <inheritdoc />
        public Task<ClusterNode> SelectNodeAsync(LoadBalancerContext context, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(SelectNode(context));
        }

        /// <summary>
        /// Selects a node using weighted random selection.
        /// Converts composite scores to integer weights and uses CSPRNG for uniform random selection.
        /// </summary>
        private static ClusterNode WeightedRandomSelect(List<(ClusterNode Node, double Score)> candidates)
        {
            if (candidates.Count == 1)
            {
                return candidates[0].Node;
            }

            // Convert scores to integer weights (multiply by 10000 for precision)
            const int ScaleMultiplier = 10_000;
            var weights = new int[candidates.Count];
            int totalWeight = 0;

            for (int i = 0; i < candidates.Count; i++)
            {
                weights[i] = Math.Max(1, (int)(candidates[i].Score * ScaleMultiplier));
                totalWeight += weights[i];
            }

            // Edge case: all scores are effectively zero
            if (totalWeight <= 0)
            {
                int index = RandomNumberGenerator.GetInt32(0, candidates.Count);
                return candidates[index].Node;
            }

            // Generate random value and walk cumulative weights
            int randomValue = RandomNumberGenerator.GetInt32(0, totalWeight);
            int cumulative = 0;

            for (int i = 0; i < candidates.Count; i++)
            {
                cumulative += weights[i];
                if (randomValue < cumulative)
                {
                    return candidates[i].Node;
                }
            }

            // Fallback (should not reach here)
            return candidates[candidates.Count - 1].Node;
        }
    }
}
