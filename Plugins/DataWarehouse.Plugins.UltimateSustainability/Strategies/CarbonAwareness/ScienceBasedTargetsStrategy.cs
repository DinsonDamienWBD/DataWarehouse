namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.CarbonAwareness;

/// <summary>
/// Tracks progress against Science Based Targets (SBTi) for carbon reduction.
/// Monitors emissions against 1.5C and well-below 2C pathways.
/// </summary>
public sealed class ScienceBasedTargetsStrategy : SustainabilityStrategyBase
{
    private readonly List<EmissionRecord> _records = new();
    private readonly object _lock = new();
    private double _baselineEmissionsKg;
    private int _baselineYear;
    private int _targetYear;
    private double _targetReductionPercent;

    /// <inheritdoc/>
    public override string StrategyId => "science-based-targets";
    /// <inheritdoc/>
    public override string DisplayName => "Science Based Targets";
    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.CarbonAwareness;
    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.CarbonCalculation | SustainabilityCapabilities.Reporting | SustainabilityCapabilities.PredictiveAnalytics;
    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Tracks carbon reduction progress against Science Based Targets (SBTi) for 1.5C pathway alignment.";
    /// <inheritdoc/>
    public override string[] Tags => new[] { "carbon", "sbti", "targets", "1.5c", "net-zero", "compliance" };

    /// <summary>Target pathway (1.5C or 2C).</summary>
    public string Pathway { get; set; } = "1.5C";

    /// <summary>Configures the baseline and target.</summary>
    public void SetTarget(int baselineYear, double baselineEmissionsKg, int targetYear, double targetReductionPercent)
    {
        _baselineYear = baselineYear;
        _baselineEmissionsKg = baselineEmissionsKg;
        _targetYear = targetYear;
        _targetReductionPercent = targetReductionPercent;
        RecordOptimizationAction();
    }

    /// <summary>Records emissions for a year.</summary>
    public void RecordYearlyEmissions(int year, double emissionsKg)
    {
        lock (_lock)
        {
            _records.Add(new EmissionRecord { Year = year, EmissionsKg = emissionsKg });
        }
        UpdateProgress();
    }

    /// <summary>Gets required reduction for a given year.</summary>
    public double GetRequiredEmissionsKg(int year)
    {
        if (year <= _baselineYear) return _baselineEmissionsKg;
        if (year >= _targetYear) return _baselineEmissionsKg * (1 - _targetReductionPercent / 100);

        var totalYears = _targetYear - _baselineYear;
        var yearsElapsed = year - _baselineYear;
        var linearReduction = (_baselineEmissionsKg * _targetReductionPercent / 100) * yearsElapsed / totalYears;
        return _baselineEmissionsKg - linearReduction;
    }

    /// <summary>Gets current progress against target.</summary>
    public SbtProgress GetProgress()
    {
        lock (_lock)
        {
            var currentYear = DateTime.UtcNow.Year;
            var latestRecord = _records.OrderByDescending(r => r.Year).FirstOrDefault();
            var latestEmissions = latestRecord?.EmissionsKg ?? _baselineEmissionsKg;
            var requiredEmissions = GetRequiredEmissionsKg(currentYear);
            var onTrack = latestEmissions <= requiredEmissions;

            var reductionAchieved = _baselineEmissionsKg > 0
                ? (_baselineEmissionsKg - latestEmissions) / _baselineEmissionsKg * 100
                : 0;

            return new SbtProgress
            {
                BaselineYear = _baselineYear,
                BaselineEmissionsKg = _baselineEmissionsKg,
                TargetYear = _targetYear,
                TargetReductionPercent = _targetReductionPercent,
                CurrentYear = currentYear,
                CurrentEmissionsKg = latestEmissions,
                RequiredEmissionsKg = requiredEmissions,
                ReductionAchievedPercent = reductionAchieved,
                IsOnTrack = onTrack,
                GapKg = latestEmissions - requiredEmissions
            };
        }
    }

    private void UpdateProgress()
    {
        ClearRecommendations();
        var progress = GetProgress();

        if (!progress.IsOnTrack)
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-offtrack",
                Type = "SBTOffTrack",
                Priority = 9,
                Description = $"Off track by {progress.GapKg:F0} kg CO2e. Need {progress.GapKg:F0} kg additional reductions to meet SBT.",
                EstimatedCarbonReductionGrams = progress.GapKg * 1000,
                CanAutoApply = false
            });
        }
    }
}

/// <summary>Emission record for a year.</summary>
public sealed record EmissionRecord
{
    public required int Year { get; init; }
    public required double EmissionsKg { get; init; }
}

/// <summary>Science Based Target progress.</summary>
public sealed record SbtProgress
{
    public int BaselineYear { get; init; }
    public double BaselineEmissionsKg { get; init; }
    public int TargetYear { get; init; }
    public double TargetReductionPercent { get; init; }
    public int CurrentYear { get; init; }
    public double CurrentEmissionsKg { get; init; }
    public double RequiredEmissionsKg { get; init; }
    public double ReductionAchievedPercent { get; init; }
    public bool IsOnTrack { get; init; }
    public double GapKg { get; init; }
}
