using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.CloudOptimization;

/// <summary>
/// Analyzes cloud resource utilization and recommends right-sizing to reduce
/// costs and carbon footprint. Identifies over-provisioned instances and suggests
/// optimal instance types.
/// </summary>
public sealed class RightSizingStrategy : SustainabilityStrategyBase
{
    private readonly BoundedDictionary<string, ResourceUtilization> _resources = new BoundedDictionary<string, ResourceUtilization>(1000);

    /// <inheritdoc/>
    public override string StrategyId => "right-sizing";
    /// <inheritdoc/>
    public override string DisplayName => "Right-Sizing";
    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.CloudOptimization;
    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.RealTimeMonitoring | SustainabilityCapabilities.Reporting | SustainabilityCapabilities.PredictiveAnalytics;
    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Analyzes cloud resource utilization and recommends right-sizing for cost and carbon reduction.";
    /// <inheritdoc/>
    public override string[] Tags => new[] { "cloud", "right-sizing", "optimization", "cost", "utilization" };

    /// <summary>CPU utilization threshold for downsizing (%).</summary>
    public double CpuDownsizeThreshold { get; set; } = 30;

    /// <summary>Memory utilization threshold for downsizing (%).</summary>
    public double MemoryDownsizeThreshold { get; set; } = 40;

    /// <summary>CPU utilization threshold for upsizing (%).</summary>
    public double CpuUpsizeThreshold { get; set; } = 85;

    /// <summary>Minimum observation period before recommendations (days).</summary>
    public int MinObservationDays { get; set; } = 7;

    /// <summary>Records resource utilization.</summary>
    public void RecordUtilization(string resourceId, string resourceType, double cpuPercent, double memoryPercent, double costPerHour)
    {
        if (!_resources.TryGetValue(resourceId, out var resource))
        {
            resource = new ResourceUtilization
            {
                ResourceId = resourceId,
                ResourceType = resourceType,
                CostPerHour = costPerHour,
                FirstSeen = DateTimeOffset.UtcNow
            };
            _resources[resourceId] = resource;
        }

        resource.CpuSamples.Add(cpuPercent);
        resource.MemorySamples.Add(memoryPercent);
        resource.LastSeen = DateTimeOffset.UtcNow;

        // Keep last 1000 samples
        while (resource.CpuSamples.Count > 1000) resource.CpuSamples.RemoveAt(0);
        while (resource.MemorySamples.Count > 1000) resource.MemorySamples.RemoveAt(0);

        RecordSample(cpuPercent, 0);
        UpdateRecommendations();
    }

    /// <summary>Gets right-sizing recommendations.</summary>
    public IReadOnlyList<RightSizingRecommendation> GetRecommendations()
    {
        var recommendations = new List<RightSizingRecommendation>();
        var minObservation = TimeSpan.FromDays(MinObservationDays);

        foreach (var resource in _resources.Values)
        {
            if (resource.LastSeen - resource.FirstSeen < minObservation)
                continue;

            // Finding 4455: guard against empty sample lists before calling Average/Max.
            if (resource.CpuSamples.Count == 0 || resource.MemorySamples.Count == 0)
                continue;

            var avgCpu = resource.CpuSamples.Average();
            var avgMemory = resource.MemorySamples.Average();
            var maxCpu = resource.CpuSamples.Max();
            var maxMemory = resource.MemorySamples.Max();

            if (avgCpu < CpuDownsizeThreshold && avgMemory < MemoryDownsizeThreshold)
            {
                var savingsPercent = Math.Min(50, 100 - (avgCpu + avgMemory) / 2);
                recommendations.Add(new RightSizingRecommendation
                {
                    ResourceId = resource.ResourceId,
                    CurrentType = resource.ResourceType,
                    RecommendedAction = RightSizeAction.Downsize,
                    Reason = $"Average CPU {avgCpu:F0}% and memory {avgMemory:F0}% indicate over-provisioning",
                    EstimatedSavingsPercent = savingsPercent,
                    EstimatedSavingsPerHour = resource.CostPerHour * savingsPercent / 100,
                    AvgCpuPercent = avgCpu,
                    AvgMemoryPercent = avgMemory,
                    MaxCpuPercent = maxCpu,
                    MaxMemoryPercent = maxMemory
                });
            }
            else if (maxCpu > CpuUpsizeThreshold)
            {
                recommendations.Add(new RightSizingRecommendation
                {
                    ResourceId = resource.ResourceId,
                    CurrentType = resource.ResourceType,
                    RecommendedAction = RightSizeAction.Upsize,
                    Reason = $"Peak CPU {maxCpu:F0}% indicates potential performance issues",
                    EstimatedSavingsPercent = 0,
                    EstimatedSavingsPerHour = 0,
                    AvgCpuPercent = avgCpu,
                    AvgMemoryPercent = avgMemory,
                    MaxCpuPercent = maxCpu,
                    MaxMemoryPercent = maxMemory
                });
            }
        }

        return recommendations.OrderByDescending(r => r.EstimatedSavingsPerHour).ToList().AsReadOnly();
    }

    private void UpdateRecommendations()
    {
        ClearRecommendations();
        var recs = GetRecommendations();
        var downsizeCount = recs.Count(r => r.RecommendedAction == RightSizeAction.Downsize);
        var totalSavings = recs.Sum(r => r.EstimatedSavingsPerHour) * 24 * 30; // Monthly

        if (downsizeCount > 0)
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-downsize",
                Type = "RightSizing",
                Priority = 7,
                Description = $"{downsizeCount} resources can be downsized for ~${totalSavings:F0}/month savings.",
                EstimatedCostSavingsUsd = totalSavings,
                CanAutoApply = false,
                Action = "apply-rightsizing"
            });
        }
    }
}

/// <summary>Right-sizing action.</summary>
public enum RightSizeAction { NoChange, Downsize, Upsize, Terminate }

/// <summary>Resource utilization tracking.</summary>
public sealed class ResourceUtilization
{
    public required string ResourceId { get; init; }
    public required string ResourceType { get; init; }
    public required double CostPerHour { get; init; }
    public required DateTimeOffset FirstSeen { get; init; }
    public DateTimeOffset LastSeen { get; set; }
    public List<double> CpuSamples { get; } = new();
    public List<double> MemorySamples { get; } = new();
}

/// <summary>Right-sizing recommendation.</summary>
public sealed record RightSizingRecommendation
{
    public required string ResourceId { get; init; }
    public required string CurrentType { get; init; }
    public required RightSizeAction RecommendedAction { get; init; }
    public required string Reason { get; init; }
    public required double EstimatedSavingsPercent { get; init; }
    public required double EstimatedSavingsPerHour { get; init; }
    public required double AvgCpuPercent { get; init; }
    public required double AvgMemoryPercent { get; init; }
    public required double MaxCpuPercent { get; init; }
    public required double MaxMemoryPercent { get; init; }
    public string? RecommendedType { get; init; }
}
