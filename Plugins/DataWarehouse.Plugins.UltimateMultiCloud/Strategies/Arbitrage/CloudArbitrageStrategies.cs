using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateMultiCloud.Strategies.Arbitrage;

/// <summary>
/// 118.6: Cloud Arbitrage Strategies
/// Exploits price differences across cloud providers.
/// </summary>

/// <summary>
/// Real-time cloud pricing arbitrage strategy.
/// </summary>
public sealed class RealTimePricingArbitrageStrategy : MultiCloudStrategyBase
{
    private readonly BoundedDictionary<string, PricingSnapshot> _currentPricing = new BoundedDictionary<string, PricingSnapshot>(1000);
    private readonly BoundedDictionary<string, ArbitrageOpportunity> _opportunities = new BoundedDictionary<string, ArbitrageOpportunity>(1000);

    public override string StrategyId => "arbitrage-realtime-pricing";
    public override string StrategyName => "Real-Time Pricing Arbitrage";
    public override string Category => "Arbitrage";

    public override MultiCloudCharacteristics Characteristics => new()
    {
        StrategyName = StrategyName,
        Description = "Monitors real-time pricing across clouds to identify arbitrage opportunities",
        Category = Category,
        SupportsCostOptimization = true,
        TypicalLatencyOverheadMs = 5.0,
        MemoryFootprint = "Medium"
    };

    /// <summary>Updates pricing snapshot for a provider.</summary>
    public void UpdatePricing(string providerId, string resourceType, string region, double pricePerUnit)
    {
        var key = $"{providerId}:{resourceType}:{region}";
        _currentPricing[key] = new PricingSnapshot
        {
            ProviderId = providerId,
            ResourceType = resourceType,
            Region = region,
            PricePerUnit = pricePerUnit,
            Timestamp = DateTimeOffset.UtcNow
        };

        DetectArbitrageOpportunities(resourceType);
    }

    private void DetectArbitrageOpportunities(string resourceType)
    {
        var pricesForType = _currentPricing.Values
            .Where(p => p.ResourceType == resourceType)
            .ToList();

        if (pricesForType.Count < 2) return;

        var cheapest = pricesForType.MinBy(p => p.PricePerUnit);
        var expensive = pricesForType.MaxBy(p => p.PricePerUnit);

        if (cheapest != null && expensive != null && cheapest.PricePerUnit < expensive.PricePerUnit * 0.8)
        {
            var opportunityId = $"{resourceType}:{DateTimeOffset.UtcNow.Ticks}";
            _opportunities[opportunityId] = new ArbitrageOpportunity
            {
                OpportunityId = opportunityId,
                ResourceType = resourceType,
                CheapestProvider = cheapest.ProviderId,
                CheapestRegion = cheapest.Region,
                CheapestPrice = cheapest.PricePerUnit,
                ExpensiveProvider = expensive.ProviderId,
                ExpensivePrice = expensive.PricePerUnit,
                SavingsPercent = (1 - cheapest.PricePerUnit / expensive.PricePerUnit) * 100,
                DetectedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15)
            };
            RecordSuccess();
        }
    }

    /// <summary>Gets current arbitrage opportunities.</summary>
    public IReadOnlyList<ArbitrageOpportunity> GetOpportunities(double minSavingsPercent = 10)
    {
        var now = DateTimeOffset.UtcNow;
        return _opportunities.Values
            .Where(o => o.ExpiresAt > now && o.SavingsPercent >= minSavingsPercent)
            .OrderByDescending(o => o.SavingsPercent)
            .ToList();
    }

    /// <summary>Gets best provider for a resource type.</summary>
    public PricingSnapshot? GetBestPrice(string resourceType)
    {
        return _currentPricing.Values
            .Where(p => p.ResourceType == resourceType && DateTimeOffset.UtcNow - p.Timestamp < TimeSpan.FromMinutes(15))
            .MinBy(p => p.PricePerUnit);
    }

    protected override string? GetCurrentState() =>
        // Avoid O(n) Count(predicate) on every status call — report total opportunities instead.
        $"Prices tracked: {_currentPricing.Count}, Opportunities: {_opportunities.Count}";
}

/// <summary>
/// Workload placement optimization based on pricing.
/// </summary>
public sealed class WorkloadPlacementArbitrageStrategy : MultiCloudStrategyBase
{
    private readonly BoundedDictionary<string, WorkloadProfile> _workloads = new BoundedDictionary<string, WorkloadProfile>(1000);

    public override string StrategyId => "arbitrage-workload-placement";
    public override string StrategyName => "Workload Placement Arbitrage";
    public override string Category => "Arbitrage";

    public override MultiCloudCharacteristics Characteristics => new()
    {
        StrategyName = StrategyName,
        Description = "Places workloads on cheapest provider that meets requirements",
        Category = Category,
        SupportsCostOptimization = true,
        SupportsAutomaticFailover = true,
        TypicalLatencyOverheadMs = 10.0,
        MemoryFootprint = "Low"
    };

    /// <summary>Registers a workload profile.</summary>
    public void RegisterWorkload(string workloadId, WorkloadRequirements requirements, double currentMonthlyCost)
    {
        _workloads[workloadId] = new WorkloadProfile
        {
            WorkloadId = workloadId,
            Requirements = requirements,
            CurrentMonthlyCost = currentMonthlyCost,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>Gets placement recommendation.</summary>
    public PlacementRecommendation GetPlacementRecommendation(
        string workloadId,
        IEnumerable<ProviderOffer> offers)
    {
        if (!_workloads.TryGetValue(workloadId, out var workload))
        {
            return new PlacementRecommendation { Success = false, Reason = "Workload not found" };
        }

        var eligibleOffers = offers
            .Where(o => MeetsRequirements(o, workload.Requirements))
            .OrderBy(o => o.MonthlyCost)
            .ToList();

        if (!eligibleOffers.Any())
        {
            RecordFailure();
            return new PlacementRecommendation { Success = false, Reason = "No eligible providers" };
        }

        var best = eligibleOffers.First();
        var savings = workload.CurrentMonthlyCost - best.MonthlyCost;

        RecordSuccess();
        return new PlacementRecommendation
        {
            Success = true,
            WorkloadId = workloadId,
            RecommendedProvider = best.ProviderId,
            RecommendedRegion = best.Region,
            EstimatedMonthlyCost = best.MonthlyCost,
            MonthlySavings = savings > 0 ? savings : 0,
            Reason = savings > 0 ? $"Save ${savings:F2}/month by moving to {best.ProviderId}" : "Already optimal"
        };
    }

    private static bool MeetsRequirements(ProviderOffer offer, WorkloadRequirements requirements)
    {
        if (requirements.MinCpuCores > 0 && offer.CpuCores < requirements.MinCpuCores)
            return false;
        if (requirements.MinMemoryGb > 0 && offer.MemoryGb < requirements.MinMemoryGb)
            return false;
        if (requirements.RequiredRegions.Any() && !requirements.RequiredRegions.Contains(offer.Region))
            return false;
        return true;
    }

    protected override string? GetCurrentState() => $"Workloads: {_workloads.Count}";
}

/// <summary>
/// Spot instance arbitrage across clouds.
/// </summary>
public sealed class SpotInstanceArbitrageStrategy : MultiCloudStrategyBase
{
    private readonly BoundedDictionary<string, SpotInstanceOffer> _spotOffers = new BoundedDictionary<string, SpotInstanceOffer>(1000);

    public override string StrategyId => "arbitrage-spot-instance";
    public override string StrategyName => "Spot Instance Arbitrage";
    public override string Category => "Arbitrage";

    public override MultiCloudCharacteristics Characteristics => new()
    {
        StrategyName = StrategyName,
        Description = "Exploits spot/preemptible instance pricing differences across clouds",
        Category = Category,
        SupportsCostOptimization = true,
        SupportsAutomaticFailover = true,
        TypicalLatencyOverheadMs = 3.0,
        MemoryFootprint = "Medium"
    };

    /// <summary>Updates spot offer.</summary>
    public void UpdateSpotOffer(string providerId, string instanceType, string region,
        double spotPrice, double onDemandPrice, double interruptionProbability)
    {
        var key = $"{providerId}:{instanceType}:{region}";
        _spotOffers[key] = new SpotInstanceOffer
        {
            ProviderId = providerId,
            InstanceType = instanceType,
            Region = region,
            SpotPrice = spotPrice,
            OnDemandPrice = onDemandPrice,
            Savings = ((onDemandPrice - spotPrice) / onDemandPrice) * 100,
            InterruptionProbability = interruptionProbability,
            Timestamp = DateTimeOffset.UtcNow
        };
    }

    /// <summary>Gets best spot option considering interruption risk.</summary>
    public SpotInstanceOffer? GetBestSpotOffer(string instanceType, double maxInterruptionProbability = 0.20)
    {
        var offer = _spotOffers.Values
            .Where(o => o.InstanceType == instanceType &&
                       o.InterruptionProbability <= maxInterruptionProbability &&
                       DateTimeOffset.UtcNow - o.Timestamp < TimeSpan.FromMinutes(10))
            .OrderBy(o => o.SpotPrice)
            .FirstOrDefault();

        if (offer != null) RecordSuccess();
        return offer;
    }

    /// <summary>Gets arbitrage opportunities for spot instances.</summary>
    public IReadOnlyList<SpotArbitrageOpportunity> GetSpotArbitrageOpportunities()
    {
        var opportunities = new List<SpotArbitrageOpportunity>();
        var byInstanceType = _spotOffers.Values.GroupBy(o => o.InstanceType);

        foreach (var group in byInstanceType)
        {
            var offers = group.OrderBy(o => o.SpotPrice).ToList();
            if (offers.Count < 2) continue;

            var cheapest = offers.First();
            var mostExpensive = offers.Last();

            if (cheapest.SpotPrice < mostExpensive.SpotPrice * 0.7)
            {
                opportunities.Add(new SpotArbitrageOpportunity
                {
                    InstanceType = group.Key,
                    CheapestProvider = cheapest.ProviderId,
                    CheapestRegion = cheapest.Region,
                    CheapestPrice = cheapest.SpotPrice,
                    ExpensiveProvider = mostExpensive.ProviderId,
                    ExpensivePrice = mostExpensive.SpotPrice,
                    SavingsPercent = (1 - cheapest.SpotPrice / mostExpensive.SpotPrice) * 100,
                    InterruptionRisk = cheapest.InterruptionProbability
                });
            }
        }

        return opportunities.OrderByDescending(o => o.SavingsPercent).ToList();
    }

    protected override string? GetCurrentState() => $"Spot offers: {_spotOffers.Count}";
}

/// <summary>
/// Market-based scheduling for cost optimization.
/// </summary>
public sealed class MarketBasedSchedulingStrategy : MultiCloudStrategyBase
{
    private readonly BoundedDictionary<string, ScheduledJob> _jobs = new BoundedDictionary<string, ScheduledJob>(1000);
    private readonly BoundedDictionary<string, List<PriceWindow>> _priceHistory = new BoundedDictionary<string, List<PriceWindow>>(1000);

    public override string StrategyId => "arbitrage-market-scheduling";
    public override string StrategyName => "Market-Based Scheduling";
    public override string Category => "Arbitrage";

    public override MultiCloudCharacteristics Characteristics => new()
    {
        StrategyName = StrategyName,
        Description = "Schedules workloads based on predicted pricing windows across clouds",
        Category = Category,
        SupportsCostOptimization = true,
        TypicalLatencyOverheadMs = 5.0,
        MemoryFootprint = "Medium"
    };

    /// <summary>Records price window.</summary>
    public void RecordPriceWindow(string providerId, double price, DateTimeOffset start, DateTimeOffset end)
    {
        var history = _priceHistory.GetOrAdd(providerId, _ => new List<PriceWindow>());
        history.Add(new PriceWindow
        {
            ProviderId = providerId,
            Price = price,
            StartTime = start,
            EndTime = end
        });
    }

    /// <summary>Finds optimal execution window.</summary>
    public OptimalWindow FindOptimalWindow(JobRequirements requirements, int hoursAhead = 24)
    {
        var now = DateTimeOffset.UtcNow;
        var searchEnd = now.AddHours(hoursAhead);

        // Analyze historical patterns to predict cheap windows
        var predictions = new List<(string providerId, DateTimeOffset window, double predictedPrice)>();

        foreach (var (providerId, history) in _priceHistory)
        {
            // Simple pattern: find historically cheap hours
            var hourlyPrices = history
                .Where(w => w.EndTime > now.AddDays(-7))
                .GroupBy(w => w.StartTime.Hour)
                .ToDictionary(g => g.Key, g => g.Average(w => w.Price));

            if (hourlyPrices.Any())
            {
                var cheapestHour = hourlyPrices.MinBy(kv => kv.Value);
                var nextWindow = now.Date.AddHours(cheapestHour.Key);
                if (nextWindow < now) nextWindow = nextWindow.AddDays(1);

                predictions.Add((providerId, nextWindow, cheapestHour.Value));
            }
        }

        if (!predictions.Any())
        {
            return new OptimalWindow { Found = false };
        }

        var best = predictions.MinBy(p => p.predictedPrice);
        RecordSuccess();

        return new OptimalWindow
        {
            Found = true,
            ProviderId = best.providerId,
            StartTime = best.window,
            EstimatedPrice = best.predictedPrice,
            Confidence = 0.75
        };
    }

    /// <summary>Schedules a job for optimal window.</summary>
    public string ScheduleJob(string jobId, JobRequirements requirements)
    {
        var window = FindOptimalWindow(requirements);

        var job = new ScheduledJob
        {
            JobId = jobId,
            Requirements = requirements,
            ScheduledProvider = window.ProviderId ?? "default",
            ScheduledTime = window.Found ? window.StartTime : DateTimeOffset.UtcNow,
            EstimatedCost = window.EstimatedPrice,
            Status = "Scheduled"
        };

        _jobs[jobId] = job;
        return jobId;
    }

    protected override string? GetCurrentState() =>
        $"Jobs: {_jobs.Count}, Price history: {_priceHistory.Sum(h => h.Value.Count)}";
}

/// <summary>
/// Bandwidth arbitrage across cloud providers.
/// </summary>
public sealed class BandwidthArbitrageStrategy : MultiCloudStrategyBase
{
    private readonly BoundedDictionary<string, BandwidthPricing> _pricing = new BoundedDictionary<string, BandwidthPricing>(1000);

    public override string StrategyId => "arbitrage-bandwidth";
    public override string StrategyName => "Bandwidth Arbitrage";
    public override string Category => "Arbitrage";

    public override MultiCloudCharacteristics Characteristics => new()
    {
        StrategyName = StrategyName,
        Description = "Optimizes data transfer costs by routing through cheapest paths",
        Category = Category,
        SupportsCostOptimization = true,
        TypicalLatencyOverheadMs = 2.0,
        MemoryFootprint = "Low"
    };

    /// <summary>Sets bandwidth pricing.</summary>
    public void SetPricing(string sourceProvider, string destProvider, double pricePerGb, int latencyMs)
    {
        var key = $"{sourceProvider}:{destProvider}";
        _pricing[key] = new BandwidthPricing
        {
            SourceProvider = sourceProvider,
            DestProvider = destProvider,
            PricePerGb = pricePerGb,
            TypicalLatencyMs = latencyMs
        };
    }

    /// <summary>Finds cheapest route between providers.</summary>
    public TransferRoute FindCheapestRoute(string source, string dest, double dataSizeGb)
    {
        // Direct route
        var directKey = $"{source}:{dest}";
        var directCost = _pricing.TryGetValue(directKey, out var directPricing)
            ? directPricing.PricePerGb * dataSizeGb
            : double.MaxValue;

        // Find via intermediate (hub-and-spoke)
        var viaRoutes = _pricing.Values
            .Where(p => p.SourceProvider == source)
            .SelectMany(first => _pricing.Values
                .Where(second => second.SourceProvider == first.DestProvider && second.DestProvider == dest)
                .Select(second => new
                {
                    Via = first.DestProvider,
                    Cost = (first.PricePerGb + second.PricePerGb) * dataSizeGb,
                    Latency = first.TypicalLatencyMs + second.TypicalLatencyMs
                }))
            .ToList();

        var cheapestVia = viaRoutes.MinBy(r => r.Cost);

        if (cheapestVia != null && cheapestVia.Cost < directCost)
        {
            RecordSuccess();
            return new TransferRoute
            {
                Source = source,
                Destination = dest,
                ViaProvider = cheapestVia.Via,
                TotalCost = cheapestVia.Cost,
                EstimatedLatencyMs = cheapestVia.Latency,
                SavingsVsDirect = directCost - cheapestVia.Cost
            };
        }

        RecordSuccess();
        return new TransferRoute
        {
            Source = source,
            Destination = dest,
            ViaProvider = null,
            TotalCost = directCost != double.MaxValue ? directCost : 0,
            EstimatedLatencyMs = directPricing?.TypicalLatencyMs ?? 0,
            SavingsVsDirect = 0
        };
    }

    protected override string? GetCurrentState() => $"Routes: {_pricing.Count}";
}

#region Supporting Types

public sealed class PricingSnapshot
{
    public required string ProviderId { get; init; }
    public required string ResourceType { get; init; }
    public required string Region { get; init; }
    public double PricePerUnit { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}

public sealed class ArbitrageOpportunity
{
    public required string OpportunityId { get; init; }
    public required string ResourceType { get; init; }
    public required string CheapestProvider { get; init; }
    public required string CheapestRegion { get; init; }
    public double CheapestPrice { get; init; }
    public required string ExpensiveProvider { get; init; }
    public double ExpensivePrice { get; init; }
    public double SavingsPercent { get; init; }
    public DateTimeOffset DetectedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
}

public sealed class WorkloadProfile
{
    public required string WorkloadId { get; init; }
    public required WorkloadRequirements Requirements { get; init; }
    public double CurrentMonthlyCost { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class WorkloadRequirements
{
    public int MinCpuCores { get; init; }
    public int MinMemoryGb { get; init; }
    public int MinStorageGb { get; init; }
    public List<string> RequiredRegions { get; init; } = new();
    public bool RequiresGpu { get; init; }
}

public sealed class ProviderOffer
{
    public required string ProviderId { get; init; }
    public required string Region { get; init; }
    public int CpuCores { get; init; }
    public int MemoryGb { get; init; }
    public int StorageGb { get; init; }
    public double MonthlyCost { get; init; }
    public bool HasGpu { get; init; }
}

public sealed class PlacementRecommendation
{
    public bool Success { get; init; }
    public string? WorkloadId { get; init; }
    public string? RecommendedProvider { get; init; }
    public string? RecommendedRegion { get; init; }
    public double EstimatedMonthlyCost { get; init; }
    public double MonthlySavings { get; init; }
    public string? Reason { get; init; }
}

public sealed class SpotInstanceOffer
{
    public required string ProviderId { get; init; }
    public required string InstanceType { get; init; }
    public required string Region { get; init; }
    public double SpotPrice { get; init; }
    public double OnDemandPrice { get; init; }
    public double Savings { get; init; }
    public double InterruptionProbability { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}

public sealed class SpotArbitrageOpportunity
{
    public required string InstanceType { get; init; }
    public required string CheapestProvider { get; init; }
    public required string CheapestRegion { get; init; }
    public double CheapestPrice { get; init; }
    public required string ExpensiveProvider { get; init; }
    public double ExpensivePrice { get; init; }
    public double SavingsPercent { get; init; }
    public double InterruptionRisk { get; init; }
}

public sealed class PriceWindow
{
    public required string ProviderId { get; init; }
    public double Price { get; init; }
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset EndTime { get; init; }
}

public sealed class JobRequirements
{
    public required string ResourceType { get; init; }
    public int EstimatedDurationMinutes { get; init; }
    public bool CanBeInterrupted { get; init; }
    public DateTimeOffset? Deadline { get; init; }
}

public sealed class ScheduledJob
{
    public required string JobId { get; init; }
    public required JobRequirements Requirements { get; init; }
    public required string ScheduledProvider { get; init; }
    public DateTimeOffset ScheduledTime { get; init; }
    public double EstimatedCost { get; init; }
    public required string Status { get; init; }
}

public sealed class OptimalWindow
{
    public bool Found { get; init; }
    public string? ProviderId { get; init; }
    public DateTimeOffset StartTime { get; init; }
    public double EstimatedPrice { get; init; }
    public double Confidence { get; init; }
}

public sealed class BandwidthPricing
{
    public required string SourceProvider { get; init; }
    public required string DestProvider { get; init; }
    public double PricePerGb { get; init; }
    public int TypicalLatencyMs { get; init; }
}

public sealed class TransferRoute
{
    public required string Source { get; init; }
    public required string Destination { get; init; }
    public string? ViaProvider { get; init; }
    public double TotalCost { get; init; }
    public int EstimatedLatencyMs { get; init; }
    public double SavingsVsDirect { get; init; }
}

#endregion
