namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.ResourceEfficiency;

/// <summary>
/// Detects idle resources that can be shutdown or scaled down
/// to reduce energy consumption.
/// </summary>
public sealed class IdleResourceDetectionStrategy : SustainabilityStrategyBase
{
    private readonly Dictionary<string, ResourceTracker> _resources = new();
    private readonly object _lock = new();

    /// <inheritdoc/>
    public override string StrategyId => "idle-resource-detection";
    /// <inheritdoc/>
    public override string DisplayName => "Idle Resource Detection";
    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.ResourceEfficiency;
    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.RealTimeMonitoring | SustainabilityCapabilities.PredictiveAnalytics;
    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Detects idle compute, storage, and network resources for potential shutdown or scale-down.";
    /// <inheritdoc/>
    public override string[] Tags => new[] { "idle", "detection", "shutdown", "scaling", "resources", "zombie" };

    /// <summary>Minutes of idle time before flagging.</summary>
    public int IdleThresholdMinutes { get; set; } = 30;
    /// <summary>CPU utilization threshold for idle (%).</summary>
    public double IdleCpuThresholdPercent { get; set; } = 5;
    /// <summary>Network I/O threshold for idle (bytes/sec).</summary>
    public long IdleNetworkThresholdBytesPerSec { get; set; } = 1000;

    /// <summary>Registers a resource to track.</summary>
    public void RegisterResource(string resourceId, string name, ResourceType type, double powerWatts)
    {
        lock (_lock)
        {
            _resources[resourceId] = new ResourceTracker
            {
                ResourceId = resourceId,
                Name = name,
                Type = type,
                PowerWatts = powerWatts,
                FirstSeen = DateTimeOffset.UtcNow
            };
        }
    }

    /// <summary>Records resource activity.</summary>
    public void RecordActivity(string resourceId, double cpuPercent, long networkBytesPerSec, int activeConnections)
    {
        lock (_lock)
        {
            if (_resources.TryGetValue(resourceId, out var resource))
            {
                resource.LastCpuPercent = cpuPercent;
                resource.LastNetworkBytesPerSec = networkBytesPerSec;
                resource.LastActiveConnections = activeConnections;
                resource.LastActivityCheck = DateTimeOffset.UtcNow;

                var isIdle = cpuPercent < IdleCpuThresholdPercent &&
                             networkBytesPerSec < IdleNetworkThresholdBytesPerSec &&
                             activeConnections == 0;

                if (isIdle)
                {
                    if (!resource.IdleSince.HasValue)
                        resource.IdleSince = DateTimeOffset.UtcNow;
                }
                else
                {
                    resource.IdleSince = null;
                    resource.LastActiveAt = DateTimeOffset.UtcNow;
                }
            }
        }
        RecordSample(cpuPercent, 0);
        EvaluateIdleResources();
    }

    /// <summary>Gets idle resources.</summary>
    public IReadOnlyList<IdleResource> GetIdleResources()
    {
        var now = DateTimeOffset.UtcNow;
        var threshold = TimeSpan.FromMinutes(IdleThresholdMinutes);

        lock (_lock)
        {
            return _resources.Values
                .Where(r => r.IdleSince.HasValue && (now - r.IdleSince.Value) >= threshold)
                .Select(r => new IdleResource
                {
                    ResourceId = r.ResourceId,
                    Name = r.Name,
                    Type = r.Type,
                    IdleDuration = now - r.IdleSince!.Value,
                    PowerWatts = r.PowerWatts,
                    LastActiveAt = r.LastActiveAt,
                    CanShutdown = r.Type != ResourceType.Database
                })
                .OrderByDescending(r => r.IdleDuration)
                .ToList();
        }
    }

    /// <summary>Gets potential savings from idle resources.</summary>
    public IdleSavingsSummary GetPotentialSavings()
    {
        var idleResources = GetIdleResources();
        return new IdleSavingsSummary
        {
            IdleResourceCount = idleResources.Count,
            TotalPowerWatts = idleResources.Sum(r => r.PowerWatts),
            EstimatedDailyKwh = idleResources.Sum(r => r.PowerWatts) * 24 / 1000,
            EstimatedMonthlyCostUsd = idleResources.Sum(r => r.PowerWatts) * 24 * 30 / 1000 * 0.12, // $0.12/kWh
            ByType = idleResources
                .GroupBy(r => r.Type)
                .ToDictionary(g => g.Key, g => g.Sum(r => r.PowerWatts))
        };
    }

    /// <summary>Marks a resource as terminated.</summary>
    public bool TerminateResource(string resourceId)
    {
        lock (_lock)
        {
            if (_resources.TryGetValue(resourceId, out var resource))
            {
                var savings = resource.PowerWatts;
                _resources.Remove(resourceId);
                RecordEnergySaved(savings);
                RecordOptimizationAction();
                return true;
            }
            return false;
        }
    }

    private void EvaluateIdleResources()
    {
        ClearRecommendations();
        var idleResources = GetIdleResources();
        var savings = GetPotentialSavings();

        if (idleResources.Any())
        {
            var shutdownable = idleResources.Where(r => r.CanShutdown).ToList();
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-idle",
                Type = "IdleResources",
                Priority = 7,
                Description = $"{idleResources.Count} idle resources consuming {savings.TotalPowerWatts:F0}W. {shutdownable.Count} can be shutdown.",
                EstimatedEnergySavingsWh = savings.TotalPowerWatts * 24,
                EstimatedCostSavingsUsd = savings.EstimatedMonthlyCostUsd / 30,
                CanAutoApply = true,
                Action = "shutdown-idle-resources"
            });

            // Flag long-idle resources specifically
            var longIdle = idleResources.Where(r => r.IdleDuration > TimeSpan.FromHours(24)).ToList();
            if (longIdle.Any())
            {
                AddRecommendation(new SustainabilityRecommendation
                {
                    RecommendationId = $"{StrategyId}-zombie",
                    Type = "ZombieResources",
                    Priority = 8,
                    Description = $"{longIdle.Count} resources idle for 24+ hours. Consider permanent removal.",
                    EstimatedEnergySavingsWh = longIdle.Sum(r => r.PowerWatts) * 24,
                    CanAutoApply = false
                });
            }
        }
    }
}

/// <summary>Resource type.</summary>
public enum ResourceType { VM, Container, Database, LoadBalancer, Storage, Network, Service }

/// <summary>Resource tracker.</summary>
public sealed class ResourceTracker
{
    public required string ResourceId { get; init; }
    public required string Name { get; init; }
    public required ResourceType Type { get; init; }
    public required double PowerWatts { get; init; }
    public required DateTimeOffset FirstSeen { get; init; }
    public DateTimeOffset LastActivityCheck { get; set; }
    public DateTimeOffset? IdleSince { get; set; }
    public DateTimeOffset? LastActiveAt { get; set; }
    public double LastCpuPercent { get; set; }
    public long LastNetworkBytesPerSec { get; set; }
    public int LastActiveConnections { get; set; }
}

/// <summary>Idle resource information.</summary>
public sealed record IdleResource
{
    public required string ResourceId { get; init; }
    public required string Name { get; init; }
    public required ResourceType Type { get; init; }
    public required TimeSpan IdleDuration { get; init; }
    public required double PowerWatts { get; init; }
    public DateTimeOffset? LastActiveAt { get; init; }
    public bool CanShutdown { get; init; }
}

/// <summary>Idle resources savings summary.</summary>
public sealed record IdleSavingsSummary
{
    public int IdleResourceCount { get; init; }
    public double TotalPowerWatts { get; init; }
    public double EstimatedDailyKwh { get; init; }
    public double EstimatedMonthlyCostUsd { get; init; }
    public Dictionary<ResourceType, double> ByType { get; init; } = new();
}
