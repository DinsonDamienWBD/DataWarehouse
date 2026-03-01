namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.CloudOptimization;

/// <summary>
/// Optimizes container density on Kubernetes clusters for energy efficiency.
/// Manages resource requests/limits and bin-packing for optimal node utilization.
/// </summary>
public sealed class ContainerDensityStrategy : SustainabilityStrategyBase
{
    private readonly Dictionary<string, K8sNode> _nodes = new();
    private readonly Dictionary<string, K8sPod> _pods = new();
    private readonly object _lock = new();

    /// <inheritdoc/>
    public override string StrategyId => "container-density";
    /// <inheritdoc/>
    public override string DisplayName => "Container Density Optimization";
    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.CloudOptimization;
    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.RealTimeMonitoring | SustainabilityCapabilities.PredictiveAnalytics;
    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Optimizes Kubernetes container density through resource tuning and bin-packing.";
    /// <inheritdoc/>
    public override string[] Tags => new[] { "kubernetes", "container", "density", "k8s", "bin-packing", "resources" };

    /// <summary>Target node CPU utilization (%).</summary>
    public double TargetNodeCpuPercent { get; set; } = 70;
    /// <summary>Target node memory utilization (%).</summary>
    public double TargetNodeMemoryPercent { get; set; } = 75;
    /// <summary>Node idle power (watts).</summary>
    public double NodeIdlePowerWatts { get; set; } = 100;
    /// <summary>Node max power (watts).</summary>
    public double NodeMaxPowerWatts { get; set; } = 300;

    /// <summary>Registers a Kubernetes node.</summary>
    public void RegisterNode(string nodeId, string name, int cpuMillicores, long memoryBytes)
    {
        // Finding 4453: validate required parameters before mutating state.
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (cpuMillicores <= 0) throw new ArgumentOutOfRangeException(nameof(cpuMillicores), "Must be > 0");
        if (memoryBytes <= 0) throw new ArgumentOutOfRangeException(nameof(memoryBytes), "Must be > 0");
        lock (_lock)
        {
            _nodes[nodeId] = new K8sNode
            {
                NodeId = nodeId,
                Name = name,
                CpuMillicores = cpuMillicores,
                MemoryBytes = memoryBytes
            };
        }
    }

    /// <summary>Updates node utilization.</summary>
    public void UpdateNodeUtilization(string nodeId, int usedCpuMillicores, long usedMemoryBytes, int podCount)
    {
        lock (_lock)
        {
            if (_nodes.TryGetValue(nodeId, out var node))
            {
                node.UsedCpuMillicores = usedCpuMillicores;
                node.UsedMemoryBytes = usedMemoryBytes;
                node.PodCount = podCount;
                node.CpuUtilization = (double)usedCpuMillicores / node.CpuMillicores * 100;
                node.MemoryUtilization = (double)usedMemoryBytes / node.MemoryBytes * 100;

                // Estimate power based on utilization
                var avgUtil = (node.CpuUtilization + node.MemoryUtilization) / 2 / 100;
                node.EstimatedPowerWatts = NodeIdlePowerWatts + (NodeMaxPowerWatts - NodeIdlePowerWatts) * avgUtil;
            }
        }
        RecordSample(usedCpuMillicores, 0);
        EvaluateOptimizations();
    }

    /// <summary>Registers a pod.</summary>
    public void RegisterPod(string podId, string name, string nodeId, int cpuRequest, int cpuLimit, long memoryRequest, long memoryLimit)
    {
        // Finding 4453: validate required parameters.
        ArgumentException.ThrowIfNullOrEmpty(podId);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        lock (_lock)
        {
            _pods[podId] = new K8sPod
            {
                PodId = podId,
                Name = name,
                NodeId = nodeId,
                CpuRequestMillicores = cpuRequest,
                CpuLimitMillicores = cpuLimit,
                MemoryRequestBytes = memoryRequest,
                MemoryLimitBytes = memoryLimit
            };
        }
    }

    /// <summary>Updates pod utilization.</summary>
    public void UpdatePodUtilization(string podId, int usedCpuMillicores, long usedMemoryBytes)
    {
        lock (_lock)
        {
            if (_pods.TryGetValue(podId, out var pod))
            {
                pod.UsedCpuMillicores = usedCpuMillicores;
                pod.UsedMemoryBytes = usedMemoryBytes;
            }
        }
    }

    /// <summary>Gets cluster density metrics.</summary>
    public ClusterDensityMetrics GetClusterMetrics()
    {
        lock (_lock)
        {
            var activeNodes = _nodes.Values.Where(n => n.PodCount > 0).ToList();
            var underutilized = activeNodes.Where(n => n.CpuUtilization < 30 && n.MemoryUtilization < 30).ToList();
            var totalPower = _nodes.Values.Sum(n => n.EstimatedPowerWatts);

            return new ClusterDensityMetrics
            {
                TotalNodes = _nodes.Count,
                ActiveNodes = activeNodes.Count,
                UnderutilizedNodes = underutilized.Count,
                TotalPods = _pods.Count,
                AvgCpuUtilization = activeNodes.Any() ? activeNodes.Average(n => n.CpuUtilization) : 0,
                AvgMemoryUtilization = activeNodes.Any() ? activeNodes.Average(n => n.MemoryUtilization) : 0,
                TotalPowerWatts = totalPower,
                PotentialNodeReduction = underutilized.Count,
                PotentialPowerSavingsWatts = underutilized.Sum(n => n.EstimatedPowerWatts)
            };
        }
    }

    /// <summary>Gets resource right-sizing recommendations.</summary>
    public IReadOnlyList<ResourceRecommendation> GetResourceRecommendations()
    {
        var recommendations = new List<ResourceRecommendation>();

        lock (_lock)
        {
            foreach (var pod in _pods.Values)
            {
                if (pod.CpuRequestMillicores > 0)
                {
                    var cpuUtilization = (double)pod.UsedCpuMillicores / pod.CpuRequestMillicores;
                    if (cpuUtilization < 0.3 && pod.CpuRequestMillicores > 100)
                    {
                        var recommended = Math.Max(50, (int)(pod.UsedCpuMillicores * 1.5));
                        recommendations.Add(new ResourceRecommendation
                        {
                            PodId = pod.PodId,
                            PodName = pod.Name,
                            ResourceType = "cpu",
                            CurrentRequest = pod.CpuRequestMillicores,
                            RecommendedRequest = recommended,
                            UtilizationPercent = cpuUtilization * 100,
                            Reason = $"CPU at {cpuUtilization * 100:F0}% of request"
                        });
                    }
                }

                if (pod.MemoryRequestBytes > 0)
                {
                    var memUtilization = (double)pod.UsedMemoryBytes / pod.MemoryRequestBytes;
                    if (memUtilization < 0.4 && pod.MemoryRequestBytes > 128 * 1024 * 1024)
                    {
                        var recommended = Math.Max(64 * 1024 * 1024, (long)(pod.UsedMemoryBytes * 1.3));
                        recommendations.Add(new ResourceRecommendation
                        {
                            PodId = pod.PodId,
                            PodName = pod.Name,
                            ResourceType = "memory",
                            CurrentRequest = pod.MemoryRequestBytes,
                            RecommendedRequest = recommended,
                            UtilizationPercent = memUtilization * 100,
                            Reason = $"Memory at {memUtilization * 100:F0}% of request"
                        });
                    }
                }
            }
        }

        return recommendations;
    }

    private void EvaluateOptimizations()
    {
        ClearRecommendations();
        var metrics = GetClusterMetrics();

        if (metrics.UnderutilizedNodes > 0)
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-consolidate",
                Type = "NodeConsolidation",
                Priority = 7,
                Description = $"{metrics.UnderutilizedNodes} underutilized nodes. Consolidate to save {metrics.PotentialPowerSavingsWatts:F0}W.",
                EstimatedEnergySavingsWh = metrics.PotentialPowerSavingsWatts * 24,
                CanAutoApply = false,
                Action = "consolidate-pods"
            });
        }

        var resourceRecs = GetResourceRecommendations();
        if (resourceRecs.Count > 5)
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-rightsizing",
                Type = "ResourceRightsizing",
                Priority = 6,
                Description = $"{resourceRecs.Count} pods have oversized resource requests.",
                CanAutoApply = true,
                Action = "apply-resource-recommendations"
            });
        }
    }
}

/// <summary>Kubernetes node information.</summary>
public sealed class K8sNode
{
    public required string NodeId { get; init; }
    public required string Name { get; init; }
    public required int CpuMillicores { get; init; }
    public required long MemoryBytes { get; init; }
    public int UsedCpuMillicores { get; set; }
    public long UsedMemoryBytes { get; set; }
    public int PodCount { get; set; }
    public double CpuUtilization { get; set; }
    public double MemoryUtilization { get; set; }
    public double EstimatedPowerWatts { get; set; }
}

/// <summary>Kubernetes pod information.</summary>
public sealed class K8sPod
{
    public required string PodId { get; init; }
    public required string Name { get; init; }
    public required string NodeId { get; init; }
    public required int CpuRequestMillicores { get; init; }
    public required int CpuLimitMillicores { get; init; }
    public required long MemoryRequestBytes { get; init; }
    public required long MemoryLimitBytes { get; init; }
    public int UsedCpuMillicores { get; set; }
    public long UsedMemoryBytes { get; set; }
}

/// <summary>Cluster density metrics.</summary>
public sealed record ClusterDensityMetrics
{
    public int TotalNodes { get; init; }
    public int ActiveNodes { get; init; }
    public int UnderutilizedNodes { get; init; }
    public int TotalPods { get; init; }
    public double AvgCpuUtilization { get; init; }
    public double AvgMemoryUtilization { get; init; }
    public double TotalPowerWatts { get; init; }
    public int PotentialNodeReduction { get; init; }
    public double PotentialPowerSavingsWatts { get; init; }
}

/// <summary>Resource right-sizing recommendation.</summary>
public sealed record ResourceRecommendation
{
    public required string PodId { get; init; }
    public required string PodName { get; init; }
    public required string ResourceType { get; init; }
    public required long CurrentRequest { get; init; }
    public required long RecommendedRequest { get; init; }
    public required double UtilizationPercent { get; init; }
    public required string Reason { get; init; }
}
