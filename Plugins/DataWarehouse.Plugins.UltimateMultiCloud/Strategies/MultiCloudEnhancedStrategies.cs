using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateMultiCloud.Strategies;

/// <summary>
/// Cross-cloud replication conflict resolution using Last-Write-Wins with vector clocks.
/// </summary>
public sealed class CrossCloudConflictResolutionStrategy : MultiCloudStrategyBase
{
    private readonly BoundedDictionary<string, VectorClock> _vectorClocks = new BoundedDictionary<string, VectorClock>(1000);
    private readonly BoundedDictionary<string, List<ConflictRecord>> _conflictLog = new BoundedDictionary<string, List<ConflictRecord>>(1000);

    public override string StrategyId => "conflict-resolution-lww";
    public override string StrategyName => "Cross-Cloud Conflict Resolution (LWW)";
    public override string Category => "Replication";

    public override MultiCloudCharacteristics Characteristics => new()
    {
        StrategyName = StrategyName,
        Description = "Last-Write-Wins conflict resolution with vector clocks for cross-cloud replication",
        Category = Category,
        SupportsCrossCloudReplication = true,
        SupportsAutomaticFailover = false,
        TypicalLatencyOverheadMs = 2.0,
        MemoryFootprint = "Medium"
    };

    /// <summary>
    /// Records a write operation and updates the vector clock.
    /// </summary>
    public WriteResult RecordWrite(string objectId, string cloudId, byte[] data, DateTimeOffset timestamp)
    {
        var clock = _vectorClocks.GetOrAdd(objectId, _ => new VectorClock());

        lock (clock)
        {
            clock.Increment(cloudId);

            var existingTimestamp = clock.GetTimestamp(cloudId);
            if (existingTimestamp.HasValue && existingTimestamp.Value > timestamp)
            {
                // Conflict detected - existing write is newer
                RecordConflict(objectId, cloudId, "Concurrent write detected, LWW applied");
                return new WriteResult
                {
                    ObjectId = objectId,
                    Accepted = false,
                    ConflictDetected = true,
                    WinningCloud = clock.GetLatestWriter() ?? cloudId,
                    VectorClock = clock.GetState()
                };
            }

            clock.SetTimestamp(cloudId, timestamp);

            return new WriteResult
            {
                ObjectId = objectId,
                Accepted = true,
                ConflictDetected = false,
                WinningCloud = cloudId,
                VectorClock = clock.GetState()
            };
        }
    }

    /// <summary>
    /// Resolves a conflict using Last-Write-Wins.
    /// </summary>
    public ConflictResolution ResolveConflict(string objectId, WriteCandidate[] candidates)
    {
        if (candidates.Length == 0)
            return new ConflictResolution { ObjectId = objectId, Resolved = false };

        // LWW: pick the candidate with the latest timestamp
        var winner = candidates.OrderByDescending(c => c.Timestamp).First();

        return new ConflictResolution
        {
            ObjectId = objectId,
            Resolved = true,
            WinnerCloudId = winner.CloudId,
            WinnerTimestamp = winner.Timestamp,
            LosersCount = candidates.Length - 1,
            Strategy = "LastWriteWins"
        };
    }

    /// <summary>
    /// Gets conflict history for an object.
    /// </summary>
    public IReadOnlyList<ConflictRecord> GetConflictHistory(string objectId) =>
        _conflictLog.TryGetValue(objectId, out var log) ? log.AsReadOnly() : Array.Empty<ConflictRecord>();

    private void RecordConflict(string objectId, string cloudId, string description)
    {
        var record = new ConflictRecord
        {
            ObjectId = objectId,
            CloudId = cloudId,
            Description = description,
            DetectedAt = DateTimeOffset.UtcNow
        };

        _conflictLog.AddOrUpdate(
            objectId,
            _ => new List<ConflictRecord> { record },
            (_, list) => { lock (list) { list.Add(record); } return list; });
    }
}

/// <summary>
/// Cloud arbitrage strategy for workload placement based on real-time pricing.
/// </summary>
public sealed class CloudArbitrageStrategy : MultiCloudStrategyBase
{
    private readonly BoundedDictionary<string, CloudPricing> _pricing = new BoundedDictionary<string, CloudPricing>(1000);
    private readonly BoundedDictionary<string, List<ArbitrageDecision>> _decisionHistory = new BoundedDictionary<string, List<ArbitrageDecision>>(1000);

    public override string StrategyId => "cloud-arbitrage";
    public override string StrategyName => "Cloud Arbitrage";
    public override string Category => "CostOptimization";

    public override MultiCloudCharacteristics Characteristics => new()
    {
        StrategyName = StrategyName,
        Description = "Workload placement based on real-time cloud pricing for cost optimization",
        Category = Category,
        SupportsCrossCloudReplication = false,
        SupportsAutomaticFailover = false,
        TypicalLatencyOverheadMs = 10.0,
        MemoryFootprint = "Low"
    };

    /// <summary>
    /// Updates pricing for a cloud provider.
    /// </summary>
    public void UpdatePricing(string cloudId, double computeCostPerHour, double storageCostPerGbMonth,
        double networkEgressCostPerGb, double? spotDiscount = null)
    {
        _pricing[cloudId] = new CloudPricing
        {
            CloudId = cloudId,
            ComputeCostPerHour = computeCostPerHour,
            StorageCostPerGbMonth = storageCostPerGbMonth,
            NetworkEgressCostPerGb = networkEgressCostPerGb,
            SpotDiscount = spotDiscount ?? 0,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Recommends optimal cloud placement for a workload.
    /// </summary>
    public ArbitrageRecommendation Recommend(WorkloadProfile workload)
    {
        if (_pricing.Count == 0)
            return new ArbitrageRecommendation { WorkloadId = workload.WorkloadId, HasRecommendation = false };

        var costs = _pricing.Values.Select(p =>
        {
            var computeCost = p.ComputeCostPerHour * workload.EstimatedComputeHours * (1.0 - (workload.AllowSpot ? p.SpotDiscount : 0));
            var storageCost = p.StorageCostPerGbMonth * workload.StorageGb;
            var networkCost = p.NetworkEgressCostPerGb * workload.EstimatedEgressGb;
            var totalCost = computeCost + storageCost + networkCost;

            return new CloudCostEstimate
            {
                CloudId = p.CloudId,
                ComputeCost = computeCost,
                StorageCost = storageCost,
                NetworkCost = networkCost,
                TotalCost = totalCost,
                SpotSavings = workload.AllowSpot ? p.SpotDiscount * computeCost : 0
            };
        }).OrderBy(c => c.TotalCost).ToList();

        var best = costs.First();
        var savings = costs.Count > 1 ? costs[1].TotalCost - best.TotalCost : 0;

        var decision = new ArbitrageDecision
        {
            WorkloadId = workload.WorkloadId,
            RecommendedCloud = best.CloudId,
            EstimatedCost = best.TotalCost,
            EstimatedSavings = savings,
            DecidedAt = DateTimeOffset.UtcNow
        };

        _decisionHistory.AddOrUpdate(
            workload.WorkloadId,
            _ => new List<ArbitrageDecision> { decision },
            (_, list) => { lock (list) { list.Add(decision); } return list; });

        return new ArbitrageRecommendation
        {
            WorkloadId = workload.WorkloadId,
            HasRecommendation = true,
            RecommendedCloudId = best.CloudId,
            CostEstimates = costs,
            EstimatedMonthlySavings = savings,
            Confidence = costs.Count > 1 ? Math.Min(1.0, savings / costs[1].TotalCost) : 0.5
        };
    }

    /// <summary>
    /// Gets current pricing for all clouds.
    /// </summary>
    public IReadOnlyList<CloudPricing> GetCurrentPricing() =>
        _pricing.Values.OrderBy(p => p.ComputeCostPerHour).ToList().AsReadOnly();
}

#region Models

public sealed class VectorClock
{
    private readonly Dictionary<string, long> _counters = new();
    private readonly Dictionary<string, DateTimeOffset> _timestamps = new();

    public void Increment(string nodeId)
    {
        _counters.TryGetValue(nodeId, out var current);
        _counters[nodeId] = current + 1;
    }

    public void SetTimestamp(string nodeId, DateTimeOffset timestamp) => _timestamps[nodeId] = timestamp;
    public DateTimeOffset? GetTimestamp(string nodeId) => _timestamps.TryGetValue(nodeId, out var ts) ? ts : null;

    public string? GetLatestWriter() =>
        _timestamps.Count > 0
            ? _timestamps.OrderByDescending(kvp => kvp.Value).First().Key
            : null;

    public Dictionary<string, long> GetState() => new(_counters);
}

public sealed record WriteResult
{
    public required string ObjectId { get; init; }
    public bool Accepted { get; init; }
    public bool ConflictDetected { get; init; }
    public required string WinningCloud { get; init; }
    public Dictionary<string, long> VectorClock { get; init; } = new();
}

public sealed record WriteCandidate
{
    public required string CloudId { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public byte[]? Data { get; init; }
}

public sealed record ConflictResolution
{
    public required string ObjectId { get; init; }
    public bool Resolved { get; init; }
    public string? WinnerCloudId { get; init; }
    public DateTimeOffset? WinnerTimestamp { get; init; }
    public int LosersCount { get; init; }
    public string? Strategy { get; init; }
}

public sealed record ConflictRecord
{
    public required string ObjectId { get; init; }
    public required string CloudId { get; init; }
    public required string Description { get; init; }
    public DateTimeOffset DetectedAt { get; init; }
}

public sealed record CloudPricing
{
    public required string CloudId { get; init; }
    public double ComputeCostPerHour { get; init; }
    public double StorageCostPerGbMonth { get; init; }
    public double NetworkEgressCostPerGb { get; init; }
    public double SpotDiscount { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record WorkloadProfile
{
    public required string WorkloadId { get; init; }
    public double EstimatedComputeHours { get; init; }
    public double StorageGb { get; init; }
    public double EstimatedEgressGb { get; init; }
    public bool AllowSpot { get; init; }
    public string[]? PreferredRegions { get; init; }
}

public sealed record CloudCostEstimate
{
    public required string CloudId { get; init; }
    public double ComputeCost { get; init; }
    public double StorageCost { get; init; }
    public double NetworkCost { get; init; }
    public double TotalCost { get; init; }
    public double SpotSavings { get; init; }
}

public sealed record ArbitrageRecommendation
{
    public required string WorkloadId { get; init; }
    public bool HasRecommendation { get; init; }
    public string? RecommendedCloudId { get; init; }
    public List<CloudCostEstimate> CostEstimates { get; init; } = new();
    public double EstimatedMonthlySavings { get; init; }
    public double Confidence { get; init; }
}

public sealed record ArbitrageDecision
{
    public required string WorkloadId { get; init; }
    public required string RecommendedCloud { get; init; }
    public double EstimatedCost { get; init; }
    public double EstimatedSavings { get; init; }
    public DateTimeOffset DecidedAt { get; init; }
}

#endregion
