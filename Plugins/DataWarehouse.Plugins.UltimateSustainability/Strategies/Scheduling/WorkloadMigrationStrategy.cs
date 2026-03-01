namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.Scheduling;

/// <summary>
/// Schedules workload migration between regions/data centers
/// based on carbon intensity and renewable energy availability.
/// </summary>
public sealed class WorkloadMigrationStrategy : SustainabilityStrategyBase
{
    private readonly Dictionary<string, DataCenter> _dataCenters = new();
    private readonly Dictionary<string, Workload> _workloads = new();
    private readonly List<MigrationEvent> _migrations = new();
    private readonly object _lock = new();

    /// <inheritdoc/>
    public override string StrategyId => "workload-migration";
    /// <inheritdoc/>
    public override string DisplayName => "Workload Migration";
    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.Scheduling;
    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.Scheduling | SustainabilityCapabilities.ExternalIntegration | SustainabilityCapabilities.CarbonCalculation;
    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Migrates workloads between regions based on carbon intensity and renewable energy availability.";
    /// <inheritdoc/>
    public override string[] Tags => new[] { "migration", "workload", "carbon", "renewable", "multi-region", "follow-the-sun" };

    /// <summary>Carbon intensity differential to trigger migration (gCO2e/kWh).</summary>
    public double MigrationThreshold { get; set; } = 100;
    /// <summary>Minimum time between migrations per workload (hours).</summary>
    public int MinMigrationIntervalHours { get; set; } = 4;

    /// <summary>Registers a data center.</summary>
    public void RegisterDataCenter(string dcId, string name, string region, double carbonIntensity, bool hasRenewable)
    {
        lock (_lock)
        {
            _dataCenters[dcId] = new DataCenter
            {
                DataCenterId = dcId,
                Name = name,
                Region = region,
                CarbonIntensity = carbonIntensity,
                HasRenewableEnergy = hasRenewable,
                AvailableCapacityPercent = 100
            };
        }
    }

    /// <summary>Updates data center carbon intensity.</summary>
    public void UpdateCarbonIntensity(string dcId, double carbonIntensity, double renewablePercent)
    {
        lock (_lock)
        {
            if (_dataCenters.TryGetValue(dcId, out var dc))
            {
                dc.CarbonIntensity = carbonIntensity;
                dc.RenewablePercent = renewablePercent;
                dc.LastUpdated = DateTimeOffset.UtcNow;
            }
        }
        RecordSample(0, carbonIntensity);
        EvaluateMigrations();
    }

    /// <summary>Registers a workload.</summary>
    /// <param name="workloadId">Unique workload identifier (required).</param>
    /// <param name="name">Workload display name (required).</param>
    /// <param name="currentDcId">Current data center ID (required).</param>
    /// <param name="powerWatts">Power consumption in watts (must be &gt;= 0).</param>
    /// <param name="isMigratable">Whether the workload may be migrated.</param>
    public void RegisterWorkload(string workloadId, string name, string currentDcId, double powerWatts, bool isMigratable)
    {
        if (string.IsNullOrWhiteSpace(workloadId))
            throw new ArgumentException("Workload ID must not be empty.", nameof(workloadId));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Workload name must not be empty.", nameof(name));
        if (string.IsNullOrWhiteSpace(currentDcId))
            throw new ArgumentException("Current data center ID must not be empty.", nameof(currentDcId));
        if (powerWatts < 0)
            throw new ArgumentOutOfRangeException(nameof(powerWatts), powerWatts, "Power consumption must be zero or greater.");

        lock (_lock)
        {
            _workloads[workloadId] = new Workload
            {
                WorkloadId = workloadId,
                Name = name,
                CurrentDataCenterId = currentDcId,
                PowerWatts = powerWatts,
                IsMigratable = isMigratable,
                CreatedAt = DateTimeOffset.UtcNow
            };
        }
    }

    /// <summary>Gets migration recommendations.</summary>
    public IReadOnlyList<MigrationRecommendation> GetMigrationRecommendations()
    {
        var recommendations = new List<MigrationRecommendation>();
        var now = DateTimeOffset.UtcNow;

        lock (_lock)
        {
            var greenestDc = _dataCenters.Values
                .Where(dc => dc.AvailableCapacityPercent > 20)
                .OrderBy(dc => dc.CarbonIntensity)
                .FirstOrDefault();

            if (greenestDc == null) return recommendations;

            foreach (var workload in _workloads.Values.Where(w => w.IsMigratable))
            {
                if (!_dataCenters.TryGetValue(workload.CurrentDataCenterId, out var currentDc))
                    continue;

                // Check if migration interval has passed
                if (workload.LastMigratedAt.HasValue &&
                    (now - workload.LastMigratedAt.Value).TotalHours < MinMigrationIntervalHours)
                    continue;

                var carbonDiff = currentDc.CarbonIntensity - greenestDc.CarbonIntensity;
                if (carbonDiff >= MigrationThreshold && greenestDc.DataCenterId != currentDc.DataCenterId)
                {
                    var hourlyKwh = workload.PowerWatts / 1000.0;
                    var carbonSaved = hourlyKwh * carbonDiff;

                    recommendations.Add(new MigrationRecommendation
                    {
                        WorkloadId = workload.WorkloadId,
                        WorkloadName = workload.Name,
                        CurrentDataCenterId = currentDc.DataCenterId,
                        TargetDataCenterId = greenestDc.DataCenterId,
                        CurrentCarbonIntensity = currentDc.CarbonIntensity,
                        TargetCarbonIntensity = greenestDc.CarbonIntensity,
                        CarbonSavedGramsPerHour = carbonSaved,
                        Priority = carbonDiff > 200 ? 8 : carbonDiff > 150 ? 6 : 4
                    });
                }
            }
        }

        return recommendations.OrderByDescending(r => r.CarbonSavedGramsPerHour).ToList();
    }

    /// <summary>Executes a migration.</summary>
    public bool MigrateWorkload(string workloadId, string targetDcId)
    {
        lock (_lock)
        {
            if (!_workloads.TryGetValue(workloadId, out var workload) || !workload.IsMigratable)
                return false;

            if (!_dataCenters.TryGetValue(targetDcId, out var targetDc))
                return false;

            var sourceDcId = workload.CurrentDataCenterId;
            workload.CurrentDataCenterId = targetDcId;
            workload.LastMigratedAt = DateTimeOffset.UtcNow;

            _migrations.Add(new MigrationEvent
            {
                WorkloadId = workloadId,
                SourceDataCenterId = sourceDcId,
                TargetDataCenterId = targetDcId,
                Timestamp = DateTimeOffset.UtcNow
            });

            if (_migrations.Count > 1000) _migrations.RemoveAt(0);
        }

        RecordOptimizationAction();
        return true;
    }

    private void EvaluateMigrations()
    {
        ClearRecommendations();
        var recs = GetMigrationRecommendations();

        if (recs.Any())
        {
            var totalSavings = recs.Sum(r => r.CarbonSavedGramsPerHour);
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-migrate",
                Type = "WorkloadMigration",
                Priority = recs.Max(r => r.Priority),
                Description = $"{recs.Count} workloads can migrate to save {totalSavings:F0}g CO2e/hour",
                EstimatedCarbonReductionGrams = totalSavings * 24,
                CanAutoApply = true,
                Action = "migrate-workloads"
            });
        }
    }
}

/// <summary>Data center information.</summary>
public sealed class DataCenter
{
    public required string DataCenterId { get; init; }
    public required string Name { get; init; }
    public required string Region { get; init; }
    public double CarbonIntensity { get; set; }
    public bool HasRenewableEnergy { get; set; }
    public double RenewablePercent { get; set; }
    public double AvailableCapacityPercent { get; set; }
    public DateTimeOffset LastUpdated { get; set; }
}

/// <summary>Workload information.</summary>
public sealed class Workload
{
    public required string WorkloadId { get; init; }
    public required string Name { get; init; }
    public string CurrentDataCenterId { get; set; } = "";
    public required double PowerWatts { get; init; }
    public required bool IsMigratable { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastMigratedAt { get; set; }
}

/// <summary>Migration recommendation.</summary>
public sealed record MigrationRecommendation
{
    public required string WorkloadId { get; init; }
    public required string WorkloadName { get; init; }
    public required string CurrentDataCenterId { get; init; }
    public required string TargetDataCenterId { get; init; }
    public required double CurrentCarbonIntensity { get; init; }
    public required double TargetCarbonIntensity { get; init; }
    public required double CarbonSavedGramsPerHour { get; init; }
    public required int Priority { get; init; }
}

/// <summary>Migration event record.</summary>
public sealed record MigrationEvent
{
    public required string WorkloadId { get; init; }
    public required string SourceDataCenterId { get; init; }
    public required string TargetDataCenterId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}
