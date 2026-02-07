using System.Collections.Concurrent;
using System.Diagnostics;
using DataWarehouse.SDK.Contracts.IntelligenceAware;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.AiEnhanced;

/// <summary>
/// Energy source type.
/// </summary>
public enum EnergySource
{
    /// <summary>
    /// Solar power.
    /// </summary>
    Solar,

    /// <summary>
    /// Wind power.
    /// </summary>
    Wind,

    /// <summary>
    /// Hydroelectric power.
    /// </summary>
    Hydro,

    /// <summary>
    /// Nuclear power.
    /// </summary>
    Nuclear,

    /// <summary>
    /// Natural gas.
    /// </summary>
    NaturalGas,

    /// <summary>
    /// Coal power.
    /// </summary>
    Coal,

    /// <summary>
    /// Mixed/unknown.
    /// </summary>
    Mixed
}

/// <summary>
/// Carbon intensity data for a region.
/// </summary>
public sealed class CarbonIntensity
{
    /// <summary>
    /// Region identifier.
    /// </summary>
    public required string RegionId { get; init; }

    /// <summary>
    /// Region display name.
    /// </summary>
    public required string RegionName { get; init; }

    /// <summary>
    /// Current carbon intensity (gCO2eq/kWh).
    /// </summary>
    public double CurrentIntensity { get; init; }

    /// <summary>
    /// Average carbon intensity.
    /// </summary>
    public double AverageIntensity { get; init; }

    /// <summary>
    /// Percentage from renewable sources.
    /// </summary>
    public double RenewablePercentage { get; init; }

    /// <summary>
    /// Primary energy source.
    /// </summary>
    public EnergySource PrimarySource { get; init; }

    /// <summary>
    /// Predicted intensity for the next hour.
    /// </summary>
    public double? PredictedNextHourIntensity { get; init; }

    /// <summary>
    /// When this data was recorded.
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Best time window for low-carbon operations.
    /// </summary>
    public (DateTime Start, DateTime End)? LowCarbonWindow { get; init; }
}

/// <summary>
/// Carbon footprint for an operation or object.
/// </summary>
public sealed class CarbonFootprint
{
    /// <summary>
    /// Carbon emissions in grams CO2 equivalent.
    /// </summary>
    public double GramsCO2e { get; init; }

    /// <summary>
    /// Energy consumed in kWh.
    /// </summary>
    public double EnergyKwh { get; init; }

    /// <summary>
    /// Region where energy was consumed.
    /// </summary>
    public string? Region { get; init; }

    /// <summary>
    /// Carbon intensity used for calculation.
    /// </summary>
    public double CarbonIntensityUsed { get; init; }

    /// <summary>
    /// Renewable energy offset applied.
    /// </summary>
    public double RenewableOffsetGrams { get; init; }

    /// <summary>
    /// Net carbon footprint after offsets.
    /// </summary>
    public double NetGramsCO2e => Math.Max(0, GramsCO2e - RenewableOffsetGrams);
}

/// <summary>
/// Scheduled operation for carbon-aware execution.
/// </summary>
public sealed class ScheduledOperation
{
    /// <summary>
    /// Operation identifier.
    /// </summary>
    public required string OperationId { get; init; }

    /// <summary>
    /// Operation type.
    /// </summary>
    public required string OperationType { get; init; }

    /// <summary>
    /// Target object IDs.
    /// </summary>
    public string[]? ObjectIds { get; init; }

    /// <summary>
    /// Estimated energy consumption (kWh).
    /// </summary>
    public double EstimatedEnergyKwh { get; init; }

    /// <summary>
    /// Preferred region (null = any).
    /// </summary>
    public string? PreferredRegion { get; init; }

    /// <summary>
    /// Maximum acceptable delay.
    /// </summary>
    public TimeSpan MaxDelay { get; init; }

    /// <summary>
    /// Scheduled execution time (null = execute now).
    /// </summary>
    public DateTime? ScheduledTime { get; set; }

    /// <summary>
    /// Why this time was chosen.
    /// </summary>
    public string? ScheduleReason { get; set; }

    /// <summary>
    /// Estimated carbon footprint at scheduled time.
    /// </summary>
    public CarbonFootprint? EstimatedFootprint { get; set; }

    /// <summary>
    /// When the operation was queued.
    /// </summary>
    public DateTime QueuedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Carbon-aware data management strategy for green data practices.
/// </summary>
/// <remarks>
/// Features:
/// - Carbon footprint tracking per operation
/// - Schedule operations for low-carbon periods
/// - Renewable energy preference for region selection
/// - Carbon intensity forecasting
/// - Sustainability reporting
/// </remarks>
public sealed class CarbonAwareDataManagementStrategy : AiEnhancedStrategyBase
{
    private readonly ConcurrentDictionary<string, CarbonIntensity> _regionIntensities = new();
    private readonly ConcurrentDictionary<string, ScheduledOperation> _scheduledOperations = new();
    private readonly ConcurrentDictionary<string, CarbonFootprint> _operationFootprints = new();
    private readonly object _statsLock = new();
    private double _totalCO2eGrams;
    private double _totalEnergyKwh;
    private long _operationCount;

    // Energy estimates per operation type (kWh per GB)
    private static readonly Dictionary<string, double> EnergyPerGBByOperation = new()
    {
        ["read"] = 0.0001,
        ["write"] = 0.0002,
        ["copy"] = 0.0003,
        ["delete"] = 0.00005,
        ["transform"] = 0.0005,
        ["compress"] = 0.0003,
        ["encrypt"] = 0.0002
    };

    /// <summary>
    /// Initializes a new CarbonAwareDataManagementStrategy.
    /// </summary>
    public CarbonAwareDataManagementStrategy()
    {
        InitializeDefaultRegions();
    }

    /// <inheritdoc/>
    public override string StrategyId => "ai.carbon-aware";

    /// <inheritdoc/>
    public override string DisplayName => "Carbon-Aware Data Management";

    /// <inheritdoc/>
    public override AiEnhancedCategory AiCategory => AiEnhancedCategory.Sustainability;

    /// <inheritdoc/>
    public override IntelligenceCapabilities RequiredCapabilities =>
        IntelligenceCapabilities.TimeSeriesForecasting | IntelligenceCapabilities.Prediction;

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 1_000,
        TypicalLatencyMs = 5.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Carbon-aware data management strategy for sustainable operations. " +
        "Tracks carbon footprint, schedules operations for low-carbon periods, and prefers renewable energy regions.";

    /// <inheritdoc/>
    public override string[] Tags => ["ai", "carbon", "sustainability", "green", "renewable", "scheduling"];

    /// <summary>
    /// Updates carbon intensity for a region.
    /// </summary>
    public void UpdateRegionIntensity(CarbonIntensity intensity)
    {
        ArgumentNullException.ThrowIfNull(intensity);
        ArgumentException.ThrowIfNullOrWhiteSpace(intensity.RegionId);

        _regionIntensities[intensity.RegionId] = intensity;
    }

    /// <summary>
    /// Gets carbon intensity for a region.
    /// </summary>
    public CarbonIntensity? GetRegionIntensity(string regionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(regionId);
        return _regionIntensities.TryGetValue(regionId, out var intensity) ? intensity : null;
    }

    /// <summary>
    /// Gets all regions ordered by current carbon intensity.
    /// </summary>
    public IReadOnlyList<CarbonIntensity> GetRegionsByIntensity()
    {
        return _regionIntensities.Values
            .OrderBy(r => r.CurrentIntensity)
            .ToList();
    }

    /// <summary>
    /// Gets the greenest region for operations.
    /// </summary>
    public CarbonIntensity? GetGreenestRegion()
    {
        return _regionIntensities.Values
            .OrderBy(r => r.CurrentIntensity)
            .ThenByDescending(r => r.RenewablePercentage)
            .FirstOrDefault();
    }

    /// <summary>
    /// Calculates carbon footprint for an operation.
    /// </summary>
    /// <param name="operationType">Type of operation.</param>
    /// <param name="dataSizeBytes">Data size in bytes.</param>
    /// <param name="regionId">Region where operation occurs.</param>
    /// <returns>Carbon footprint estimate.</returns>
    public CarbonFootprint CalculateFootprint(string operationType, long dataSizeBytes, string regionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationType);
        ArgumentException.ThrowIfNullOrWhiteSpace(regionId);

        var dataSizeGB = dataSizeBytes / (1024.0 * 1024 * 1024);
        var energyPerGB = EnergyPerGBByOperation.TryGetValue(operationType.ToLowerInvariant(), out var e) ? e : 0.0002;
        var energyKwh = dataSizeGB * energyPerGB;

        var intensity = GetRegionIntensity(regionId);
        var carbonIntensity = intensity?.CurrentIntensity ?? 400; // Default to moderate intensity

        var gramsCO2e = energyKwh * carbonIntensity;
        var renewableOffset = intensity != null ? gramsCO2e * (intensity.RenewablePercentage / 100) : 0;

        return new CarbonFootprint
        {
            GramsCO2e = gramsCO2e,
            EnergyKwh = energyKwh,
            Region = regionId,
            CarbonIntensityUsed = carbonIntensity,
            RenewableOffsetGrams = renewableOffset
        };
    }

    /// <summary>
    /// Records an operation's carbon footprint.
    /// </summary>
    public void RecordOperationFootprint(string operationId, CarbonFootprint footprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(footprint);

        _operationFootprints[operationId] = footprint;

        lock (_statsLock)
        {
            _totalCO2eGrams += footprint.NetGramsCO2e;
            _totalEnergyKwh += footprint.EnergyKwh;
            _operationCount++;
        }
    }

    /// <summary>
    /// Schedules an operation for a low-carbon time window.
    /// </summary>
    /// <param name="operation">Operation to schedule.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Updated operation with schedule.</returns>
    public async Task<ScheduledOperation> ScheduleOperationAsync(
        ScheduledOperation operation,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentNullException.ThrowIfNull(operation);

        var sw = Stopwatch.StartNew();

        if (IsAiAvailable)
        {
            operation = await ScheduleWithAiAsync(operation, ct) ?? ScheduleWithFallback(operation);
            RecordAiOperation(operation.ScheduledTime.HasValue, false, 0.8, sw.Elapsed.TotalMilliseconds);
        }
        else
        {
            operation = ScheduleWithFallback(operation);
            RecordAiOperation(false, false, 0, sw.Elapsed.TotalMilliseconds);
        }

        _scheduledOperations[operation.OperationId] = operation;
        return operation;
    }

    /// <summary>
    /// Gets pending scheduled operations.
    /// </summary>
    public IReadOnlyList<ScheduledOperation> GetPendingOperations()
    {
        var now = DateTime.UtcNow;
        return _scheduledOperations.Values
            .Where(o => o.ScheduledTime == null || o.ScheduledTime > now)
            .OrderBy(o => o.ScheduledTime ?? DateTime.MaxValue)
            .ToList();
    }

    /// <summary>
    /// Gets operations ready for execution.
    /// </summary>
    public IReadOnlyList<ScheduledOperation> GetReadyOperations()
    {
        var now = DateTime.UtcNow;
        return _scheduledOperations.Values
            .Where(o => o.ScheduledTime.HasValue && o.ScheduledTime.Value <= now)
            .OrderBy(o => o.ScheduledTime)
            .ToList();
    }

    /// <summary>
    /// Removes a scheduled operation.
    /// </summary>
    public bool RemoveOperation(string operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        return _scheduledOperations.TryRemove(operationId, out _);
    }

    /// <summary>
    /// Gets carbon statistics.
    /// </summary>
    public Dictionary<string, object> GetCarbonStatistics()
    {
        lock (_statsLock)
        {
            var avgIntensity = _operationCount > 0 && _totalEnergyKwh > 0
                ? _totalCO2eGrams / _totalEnergyKwh
                : 0;

            return new Dictionary<string, object>
            {
                ["TotalCO2eGrams"] = _totalCO2eGrams,
                ["TotalCO2eKg"] = _totalCO2eGrams / 1000,
                ["TotalEnergyKwh"] = _totalEnergyKwh,
                ["OperationCount"] = _operationCount,
                ["AverageIntensityGramsPerKwh"] = avgIntensity,
                ["TrackedRegions"] = _regionIntensities.Count,
                ["PendingScheduledOperations"] = _scheduledOperations.Count,
                ["GreenestRegion"] = GetGreenestRegion()?.RegionName ?? "Unknown"
            };
        }
    }

    /// <summary>
    /// Gets a sustainability report.
    /// </summary>
    public Dictionary<string, object> GenerateSustainabilityReport()
    {
        var stats = GetCarbonStatistics();

        var byRegion = _operationFootprints.Values
            .Where(f => f.Region != null)
            .GroupBy(f => f.Region!)
            .ToDictionary(
                g => g.Key,
                g => new
                {
                    TotalCO2eGrams = g.Sum(f => f.GramsCO2e),
                    TotalEnergyKwh = g.Sum(f => f.EnergyKwh),
                    OperationCount = g.Count()
                });

        var regions = _regionIntensities.Values.ToList();
        var avgRenewable = regions.Count > 0 ? regions.Average(r => r.RenewablePercentage) : 0;

        return new Dictionary<string, object>
        {
            ["GeneratedAt"] = DateTime.UtcNow,
            ["TotalCarbon"] = stats,
            ["ByRegion"] = byRegion,
            ["AverageRenewablePercentage"] = avgRenewable,
            ["RegionCount"] = regions.Count,
            ["GreenestRegion"] = GetGreenestRegion()?.RegionName ?? "Unknown",
            ["HighestEmissionRegion"] = regions.OrderByDescending(r => r.CurrentIntensity).FirstOrDefault()?.RegionName ?? "Unknown",
            ["CarbonSavingsFromScheduling"] = CalculateSchedulingSavings()
        };
    }

    private async Task<ScheduledOperation?> ScheduleWithAiAsync(ScheduledOperation operation, CancellationToken ct)
    {
        var inputData = new Dictionary<string, object>
        {
            ["operationType"] = operation.OperationType,
            ["estimatedEnergyKwh"] = operation.EstimatedEnergyKwh,
            ["maxDelayHours"] = operation.MaxDelay.TotalHours,
            ["preferredRegion"] = operation.PreferredRegion ?? "",
            ["currentIntensities"] = _regionIntensities.Values.Select(r => new
            {
                Region = r.RegionId,
                Intensity = r.CurrentIntensity,
                Renewable = r.RenewablePercentage,
                PredictedNextHour = r.PredictedNextHourIntensity
            }).ToList()
        };

        var prediction = await RequestPredictionAsync("carbon_scheduling", inputData, DefaultContext, ct);

        if (prediction.HasValue)
        {
            var metadata = prediction.Value.Metadata;

            var scheduledTime = DateTime.UtcNow;
            if (metadata.TryGetValue("delayHours", out var delayObj) && delayObj is double delayHours)
            {
                scheduledTime = DateTime.UtcNow.AddHours(delayHours);
            }

            var region = operation.PreferredRegion;
            if (metadata.TryGetValue("recommendedRegion", out var regionObj))
            {
                region = regionObj?.ToString();
            }

            operation.ScheduledTime = scheduledTime;
            operation.ScheduleReason = metadata.TryGetValue("reason", out var reason) ? reason?.ToString() : "AI-optimized schedule";

            if (region != null)
            {
                operation.EstimatedFootprint = CalculateFootprint(
                    operation.OperationType,
                    (long)(operation.EstimatedEnergyKwh * 1024 * 1024 * 1024 / 0.0002), // Estimate size
                    region);
            }

            return operation;
        }

        return null;
    }

    private ScheduledOperation ScheduleWithFallback(ScheduledOperation operation)
    {
        var region = operation.PreferredRegion ?? GetGreenestRegion()?.RegionId;

        if (region != null)
        {
            var intensity = GetRegionIntensity(region);

            if (intensity?.LowCarbonWindow != null)
            {
                var window = intensity.LowCarbonWindow.Value;
                if (window.Start > DateTime.UtcNow && window.Start < DateTime.UtcNow.Add(operation.MaxDelay))
                {
                    operation.ScheduledTime = window.Start;
                    operation.ScheduleReason = $"Scheduled for low-carbon window in {intensity.RegionName}";
                }
            }
            else if (intensity != null && intensity.CurrentIntensity > intensity.AverageIntensity * 1.2)
            {
                // Current intensity is high, delay if possible
                var delay = TimeSpan.FromHours(Math.Min(2, operation.MaxDelay.TotalHours));
                operation.ScheduledTime = DateTime.UtcNow.Add(delay);
                operation.ScheduleReason = "Delayed due to high current carbon intensity";
            }

            if (region != null)
            {
                operation.EstimatedFootprint = CalculateFootprint(
                    operation.OperationType,
                    (long)(operation.EstimatedEnergyKwh * 1024 * 1024 * 1024 / 0.0002),
                    region);
            }
        }

        if (operation.ScheduledTime == null)
        {
            operation.ScheduledTime = DateTime.UtcNow;
            operation.ScheduleReason = "Immediate execution - no optimization opportunity found";
        }

        return operation;
    }

    private double CalculateSchedulingSavings()
    {
        var totalSavings = 0.0;

        foreach (var operation in _scheduledOperations.Values)
        {
            if (operation.EstimatedFootprint != null)
            {
                var immediateIntensity = GetRegionIntensity(operation.PreferredRegion ?? "default")?.CurrentIntensity ?? 400;
                var scheduledIntensity = operation.EstimatedFootprint.CarbonIntensityUsed;

                if (scheduledIntensity < immediateIntensity)
                {
                    var savings = (immediateIntensity - scheduledIntensity) * operation.EstimatedEnergyKwh;
                    totalSavings += savings;
                }
            }
        }

        return totalSavings;
    }

    private void InitializeDefaultRegions()
    {
        UpdateRegionIntensity(new CarbonIntensity
        {
            RegionId = "us-west-2",
            RegionName = "US West (Oregon)",
            CurrentIntensity = 150,
            AverageIntensity = 180,
            RenewablePercentage = 85,
            PrimarySource = EnergySource.Hydro,
            LowCarbonWindow = (DateTime.UtcNow.Date.AddHours(14), DateTime.UtcNow.Date.AddHours(18))
        });

        UpdateRegionIntensity(new CarbonIntensity
        {
            RegionId = "eu-north-1",
            RegionName = "EU North (Stockholm)",
            CurrentIntensity = 50,
            AverageIntensity = 60,
            RenewablePercentage = 95,
            PrimarySource = EnergySource.Hydro
        });

        UpdateRegionIntensity(new CarbonIntensity
        {
            RegionId = "us-east-1",
            RegionName = "US East (Virginia)",
            CurrentIntensity = 380,
            AverageIntensity = 400,
            RenewablePercentage = 20,
            PrimarySource = EnergySource.NaturalGas
        });

        UpdateRegionIntensity(new CarbonIntensity
        {
            RegionId = "ap-southeast-2",
            RegionName = "Asia Pacific (Sydney)",
            CurrentIntensity = 650,
            AverageIntensity = 700,
            RenewablePercentage = 15,
            PrimarySource = EnergySource.Coal
        });

        UpdateRegionIntensity(new CarbonIntensity
        {
            RegionId = "eu-west-1",
            RegionName = "EU West (Ireland)",
            CurrentIntensity = 280,
            AverageIntensity = 320,
            RenewablePercentage = 45,
            PrimarySource = EnergySource.Wind,
            LowCarbonWindow = (DateTime.UtcNow.Date.AddHours(10), DateTime.UtcNow.Date.AddHours(16))
        });
    }
}
