using System.Collections.Concurrent;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.CloudOptimization;

/// <summary>
/// Optimizes cloud costs and sustainability by utilizing spot/preemptible instances
/// for fault-tolerant workloads. Handles instance interruption and checkpointing.
/// </summary>
public sealed class SpotInstanceStrategy : SustainabilityStrategyBase
{
    private readonly BoundedDictionary<string, SpotInstance> _instances = new BoundedDictionary<string, SpotInstance>(1000);
    private readonly ConcurrentQueue<InterruptionEvent> _interruptions = new();

    /// <inheritdoc/>
    public override string StrategyId => "spot-instance";
    /// <inheritdoc/>
    public override string DisplayName => "Spot/Preemptible Instances";
    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.CloudOptimization;
    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.Scheduling | SustainabilityCapabilities.ExternalIntegration;
    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Utilizes cloud spot/preemptible instances for cost and carbon savings on fault-tolerant workloads.";
    /// <inheritdoc/>
    public override string[] Tags => new[] { "cloud", "spot", "preemptible", "cost", "aws", "gcp", "azure" };

    /// <summary>Maximum spot price as percentage of on-demand.</summary>
    public int MaxSpotPricePercent { get; set; } = 70;

    /// <summary>Enable automatic checkpointing.</summary>
    public bool EnableCheckpointing { get; set; } = true;

    /// <summary>Active spot instances.</summary>
    public IReadOnlyDictionary<string, SpotInstance> Instances => new Dictionary<string, SpotInstance>(_instances);

    /// <summary>Estimated cost savings from spot usage.</summary>
    public double EstimatedCostSavingsUsd => _instances.Values.Sum(i => i.OnDemandCostUsd - i.SpotCostUsd);

    /// <summary>Requests a spot instance.</summary>
    public SpotInstance RequestSpotInstance(
        string instanceType,
        double maxBidUsd,
        string region,
        string provider = "AWS")
    {
        // Finding 4454: validate inputs before constructing the instance.
        ArgumentException.ThrowIfNullOrEmpty(instanceType);
        ArgumentException.ThrowIfNullOrEmpty(region);
        if (maxBidUsd <= 0) throw new ArgumentOutOfRangeException(nameof(maxBidUsd), "Max bid must be > 0");
        var instance = new SpotInstance
        {
            InstanceId = Guid.NewGuid().ToString("N"),
            InstanceType = instanceType,
            Region = region,
            Provider = provider,
            MaxBidUsd = maxBidUsd,
            SpotCostUsd = maxBidUsd * 0.7,
            OnDemandCostUsd = maxBidUsd / (MaxSpotPricePercent / 100.0),
            RequestedAt = DateTimeOffset.UtcNow,
            Status = SpotInstanceStatus.Pending
        };

        _instances[instance.InstanceId] = instance;
        RecordWorkloadScheduled();
        return instance;
    }

    /// <summary>Simulates instance allocation.</summary>
    public void AllocateInstance(string instanceId)
    {
        if (_instances.TryGetValue(instanceId, out var instance))
        {
            instance.Status = SpotInstanceStatus.Running;
            instance.AllocatedAt = DateTimeOffset.UtcNow;
            RecordOptimizationAction();
            var savings = instance.OnDemandCostUsd - instance.SpotCostUsd;
            RecordEnergySaved(savings * 10); // Rough conversion
        }
    }

    /// <summary>Handles instance interruption.</summary>
    public void HandleInterruption(string instanceId, string reason)
    {
        if (_instances.TryRemove(instanceId, out var instance))
        {
            instance.Status = SpotInstanceStatus.Interrupted;
            _interruptions.Enqueue(new InterruptionEvent
            {
                InstanceId = instanceId,
                Reason = reason,
                Timestamp = DateTimeOffset.UtcNow
            });
            while (_interruptions.Count > 100) _interruptions.TryDequeue(out _);
        }
        UpdateRecommendations();
    }

    /// <summary>Terminates a spot instance.</summary>
    public void TerminateInstance(string instanceId)
    {
        if (_instances.TryRemove(instanceId, out var instance))
        {
            instance.Status = SpotInstanceStatus.Terminated;
        }
    }

    private void UpdateRecommendations()
    {
        ClearRecommendations();
        var recentInterruptions = _interruptions.Count(i => i.Timestamp > DateTimeOffset.UtcNow.AddHours(-1));
        if (recentInterruptions > 3)
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-high-interruptions",
                Type = "HighInterruptions",
                Priority = 7,
                Description = $"{recentInterruptions} spot interruptions in last hour. Consider different instance types or regions.",
                CanAutoApply = false
            });
        }
    }
}

/// <summary>Spot instance status.</summary>
public enum SpotInstanceStatus { Pending, Running, Interrupted, Terminated }

/// <summary>Spot instance information.</summary>
public sealed class SpotInstance
{
    public required string InstanceId { get; init; }
    public required string InstanceType { get; init; }
    public required string Region { get; init; }
    public required string Provider { get; init; }
    public required double MaxBidUsd { get; init; }
    public required double SpotCostUsd { get; init; }
    public required double OnDemandCostUsd { get; init; }
    public required DateTimeOffset RequestedAt { get; init; }
    public SpotInstanceStatus Status { get; set; }
    public DateTimeOffset? AllocatedAt { get; set; }
}

/// <summary>Instance interruption event.</summary>
public sealed record InterruptionEvent
{
    public required string InstanceId { get; init; }
    public required string Reason { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}
