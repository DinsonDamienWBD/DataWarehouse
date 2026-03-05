using System.Collections.Concurrent;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.CarbonAwareness;

/// <summary>
/// Manages carbon offsetting for emissions that cannot be avoided.
/// Tracks carbon footprint and facilitates purchasing of carbon offsets
/// from verified offset providers (Verra, Gold Standard, etc.).
/// </summary>
public sealed class CarbonOffsettingStrategy : SustainabilityStrategyBase
{
    private readonly ConcurrentQueue<CarbonFootprintEntry> _footprintHistory = new();
    private readonly BoundedDictionary<string, OffsetPurchase> _offsetPurchases = new BoundedDictionary<string, OffsetPurchase>(1000);
    private double _totalEmissionsGrams;
    private double _totalOffsetsGrams;
    private readonly object _lock = new();

    /// <inheritdoc/>
    public override string StrategyId => "carbon-offsetting";

    /// <inheritdoc/>
    public override string DisplayName => "Carbon Offsetting";

    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.CarbonAwareness;

    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.CarbonCalculation |
        SustainabilityCapabilities.Reporting |
        SustainabilityCapabilities.ExternalIntegration;

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Manages carbon offsetting for unavoidable emissions. Tracks total carbon footprint, " +
        "integrates with offset providers (Verra, Gold Standard), and maintains offset inventory " +
        "for carbon neutrality reporting.";

    /// <inheritdoc/>
    public override string[] Tags => new[] { "carbon", "offset", "neutrality", "verra", "gold-standard" };

    /// <summary>
    /// Optional user-supplied delegate that fetches live offset project data from a marketplace API.
    /// P2-4416: When set, <see cref="GetRecommendedProjects"/> calls this provider and returns its results
    /// instead of the built-in reference catalog. The delegate receives the preferred project types and
    /// the total offset tons needed; it should return a sorted list of <see cref="OffsetProjectRecommendation"/>.
    /// </summary>
    public Func<IReadOnlyList<OffsetProjectType>, double, IReadOnlyList<OffsetProjectRecommendation>>? ProjectCatalogProvider { get; set; }

    /// <summary>
    /// Gets total emissions tracked (gCO2e).
    /// </summary>
    public double TotalEmissionsGrams
    {
        get { lock (_lock) return _totalEmissionsGrams; }
    }

    /// <summary>
    /// Gets total offsets purchased (gCO2e).
    /// </summary>
    public double TotalOffsetsGrams
    {
        get { lock (_lock) return _totalOffsetsGrams; }
    }

    /// <summary>
    /// Gets net carbon balance (negative = net carbon positive, positive = net carbon negative).
    /// </summary>
    public double NetCarbonBalanceGrams
    {
        get { lock (_lock) return _totalOffsetsGrams - _totalEmissionsGrams; }
    }

    /// <summary>
    /// Whether the system is carbon neutral.
    /// </summary>
    public bool IsCarbonNeutral => NetCarbonBalanceGrams >= 0;

    /// <summary>
    /// Target offset percentage (100 = carbon neutral).
    /// </summary>
    public double TargetOffsetPercent { get; set; } = 100;

    /// <summary>
    /// Preferred offset project types.
    /// </summary>
    public OffsetProjectType[] PreferredProjectTypes { get; set; } = new[]
    {
        OffsetProjectType.Reforestation,
        OffsetProjectType.RenewableEnergy,
        OffsetProjectType.DirectAirCapture
    };

    /// <summary>
    /// Cost per ton of CO2 offset (USD).
    /// </summary>
    public double OffsetCostPerTon { get; set; } = 25.0;

    /// <summary>
    /// Records carbon emissions from an operation.
    /// </summary>
    public void RecordEmission(double emissionsGrams, string source, string? description = null)
    {
        var entry = new CarbonFootprintEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = DateTimeOffset.UtcNow,
            EmissionsGrams = emissionsGrams,
            Source = source,
            Description = description
        };

        _footprintHistory.Enqueue(entry);
        while (_footprintHistory.Count > 10000)
            _footprintHistory.TryDequeue(out _);

        lock (_lock)
        {
            _totalEmissionsGrams += emissionsGrams;
        }

        UpdateRecommendations();
    }

    /// <summary>
    /// Records a carbon offset purchase.
    /// </summary>
    public void RecordOffsetPurchase(
        double offsetGrams,
        string provider,
        OffsetProjectType projectType,
        string projectName,
        double costUsd,
        string? certificateId = null)
    {
        var purchase = new OffsetPurchase
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = DateTimeOffset.UtcNow,
            OffsetGrams = offsetGrams,
            Provider = provider,
            ProjectType = projectType,
            ProjectName = projectName,
            CostUsd = costUsd,
            CertificateId = certificateId,
            VerificationStatus = VerificationStatus.Pending
        };

        _offsetPurchases[purchase.Id] = purchase;

        lock (_lock)
        {
            _totalOffsetsGrams += offsetGrams;
        }

        RecordCarbonAvoided(offsetGrams);
        UpdateRecommendations();
    }

    /// <summary>
    /// Calculates offset needed to achieve target offset percentage.
    /// </summary>
    public OffsetRequirement CalculateOffsetRequirement()
    {
        lock (_lock)
        {
            var targetOffset = _totalEmissionsGrams * (TargetOffsetPercent / 100.0);
            var additionalNeeded = Math.Max(0, targetOffset - _totalOffsetsGrams);
            var estimatedCost = (additionalNeeded / 1_000_000) * OffsetCostPerTon;

            return new OffsetRequirement
            {
                TotalEmissionsGrams = _totalEmissionsGrams,
                CurrentOffsetsGrams = _totalOffsetsGrams,
                TargetOffsetsGrams = targetOffset,
                AdditionalOffsetsNeededGrams = additionalNeeded,
                EstimatedCostUsd = estimatedCost,
                CurrentOffsetPercent = _totalEmissionsGrams > 0 ? (_totalOffsetsGrams / _totalEmissionsGrams) * 100 : 100,
                TargetOffsetPercent = TargetOffsetPercent
            };
        }
    }

    /// <summary>
    /// Gets emissions breakdown by source.
    /// </summary>
    public IReadOnlyDictionary<string, double> GetEmissionsBySource(TimeSpan? period = null)
    {
        var cutoff = period.HasValue ? DateTimeOffset.UtcNow - period.Value : DateTimeOffset.MinValue;

        return _footprintHistory
            .Where(e => e.Timestamp >= cutoff)
            .GroupBy(e => e.Source)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.EmissionsGrams));
    }

    /// <summary>
    /// Gets all offset purchases.
    /// </summary>
    public IReadOnlyList<OffsetPurchase> GetOffsetPurchases()
    {
        return _offsetPurchases.Values
            .OrderByDescending(p => p.Timestamp)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Generates a carbon neutrality report.
    /// </summary>
    public CarbonNeutralityReport GenerateReport(TimeSpan period)
    {
        var cutoff = DateTimeOffset.UtcNow - period;

        var periodEmissions = _footprintHistory
            .Where(e => e.Timestamp >= cutoff)
            .Sum(e => e.EmissionsGrams);

        var periodOffsets = _offsetPurchases.Values
            .Where(p => p.Timestamp >= cutoff)
            .Sum(p => p.OffsetGrams);

        var offsetsByType = _offsetPurchases.Values
            .Where(p => p.Timestamp >= cutoff)
            .GroupBy(p => p.ProjectType)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.OffsetGrams));

        var emissionsBySource = GetEmissionsBySource(period);

        return new CarbonNeutralityReport
        {
            ReportId = Guid.NewGuid().ToString("N"),
            GeneratedAt = DateTimeOffset.UtcNow,
            PeriodStart = cutoff,
            PeriodEnd = DateTimeOffset.UtcNow,
            TotalEmissionsGrams = periodEmissions,
            TotalOffsetsGrams = periodOffsets,
            NetCarbonBalanceGrams = periodOffsets - periodEmissions,
            IsCarbonNeutral = periodOffsets >= periodEmissions,
            OffsetPercentage = periodEmissions > 0 ? (periodOffsets / periodEmissions) * 100 : 100,
            EmissionsBySource = emissionsBySource,
            OffsetsByProjectType = offsetsByType,
            TotalOffsetCostUsd = _offsetPurchases.Values.Where(p => p.Timestamp >= cutoff).Sum(p => p.CostUsd)
        };
    }

    /// <summary>
    /// Gets recommended offset projects based on preferences.
    /// P2-4416: Uses <see cref="ProjectCatalogProvider"/> when set for live marketplace data;
    /// falls back to the built-in reference catalog for offline/air-gapped environments.
    /// </summary>
    public IReadOnlyList<OffsetProjectRecommendation> GetRecommendedProjects(double offsetGramsNeeded)
    {
        var offsetTons = offsetGramsNeeded / 1_000_000;

        // Prefer user-supplied live marketplace provider when configured.
        if (ProjectCatalogProvider != null)
        {
            try
            {
                return ProjectCatalogProvider(PreferredProjectTypes, offsetTons);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "[CarbonOffsettingStrategy] ProjectCatalogProvider threw: {0}. Falling back to built-in catalog.", ex.Message);
            }
        }

        // Built-in reference catalog (Verra/Gold Standard reference prices as of 2024).
        var recommendations = new List<OffsetProjectRecommendation>();

        foreach (var projectType in PreferredProjectTypes)
        {
            var project = projectType switch
            {
                OffsetProjectType.Reforestation => new OffsetProjectRecommendation
                {
                    Provider = "Verra",
                    ProjectType = projectType,
                    ProjectName = "Amazon Rainforest Conservation",
                    CostPerTonUsd = 18.0,
                    TotalCostUsd = offsetTons * 18.0,
                    OffsetsAvailableTons = 100000,
                    Certification = "VCS",
                    Rating = 4.5,
                    Description = "Protects 50,000 hectares of primary Amazon rainforest from deforestation."
                },
                OffsetProjectType.RenewableEnergy => new OffsetProjectRecommendation
                {
                    Provider = "Gold Standard",
                    ProjectType = projectType,
                    ProjectName = "India Wind Farm",
                    CostPerTonUsd = 22.0,
                    TotalCostUsd = offsetTons * 22.0,
                    OffsetsAvailableTons = 50000,
                    Certification = "Gold Standard",
                    Rating = 4.8,
                    Description = "100MW wind farm displacing coal power generation in Gujarat."
                },
                OffsetProjectType.DirectAirCapture => new OffsetProjectRecommendation
                {
                    Provider = "Climeworks",
                    ProjectType = projectType,
                    ProjectName = "Orca Direct Air Capture",
                    CostPerTonUsd = 600.0,
                    TotalCostUsd = offsetTons * 600.0,
                    OffsetsAvailableTons = 4000,
                    Certification = "Verified Removal",
                    Rating = 5.0,
                    Description = "Captures CO2 directly from air and stores permanently in basalt rock."
                },
                OffsetProjectType.CookstoveDistribution => new OffsetProjectRecommendation
                {
                    Provider = "Gold Standard",
                    ProjectType = projectType,
                    ProjectName = "Kenya Clean Cookstoves",
                    CostPerTonUsd = 12.0,
                    TotalCostUsd = offsetTons * 12.0,
                    OffsetsAvailableTons = 200000,
                    Certification = "Gold Standard",
                    Rating = 4.2,
                    Description = "Distributes efficient cookstoves reducing wood fuel consumption."
                },
                _ => null
            };

            if (project != null)
            {
                recommendations.Add(project);
            }
        }

        return recommendations.OrderBy(r => r.CostPerTonUsd).ToList().AsReadOnly();
    }

    private void UpdateRecommendations()
    {
        ClearRecommendations();

        var requirement = CalculateOffsetRequirement();

        if (requirement.AdditionalOffsetsNeededGrams > 0)
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-purchase-offsets",
                Type = "PurchaseOffsets",
                Priority = requirement.CurrentOffsetPercent < 50 ? 9 : 6,
                Description = $"Need {requirement.AdditionalOffsetsNeededGrams / 1000:F1} kg CO2e in offsets to reach {TargetOffsetPercent}% target. Estimated cost: ${requirement.EstimatedCostUsd:F2}",
                EstimatedCarbonReductionGrams = requirement.AdditionalOffsetsNeededGrams,
                EstimatedCostSavingsUsd = -requirement.EstimatedCostUsd, // Cost, not savings
                CanAutoApply = false,
                Action = "purchase-offsets",
                ActionParameters = new Dictionary<string, object>
                {
                    ["offsetGramsNeeded"] = requirement.AdditionalOffsetsNeededGrams,
                    ["estimatedCostUsd"] = requirement.EstimatedCostUsd
                }
            });
        }

        if (IsCarbonNeutral)
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-carbon-neutral",
                Type = "CarbonNeutral",
                Priority = 3,
                Description = "System is currently carbon neutral with sufficient offsets.",
                CanAutoApply = false
            });
        }
    }
}

/// <summary>
/// Type of offset project.
/// </summary>
public enum OffsetProjectType
{
    /// <summary>Tree planting and forest conservation.</summary>
    Reforestation,
    /// <summary>Wind, solar, or hydro energy projects.</summary>
    RenewableEnergy,
    /// <summary>Technology that captures CO2 directly from air.</summary>
    DirectAirCapture,
    /// <summary>Methane capture from landfills or agriculture.</summary>
    MethaneCapture,
    /// <summary>Projects improving soil carbon storage.</summary>
    SoilCarbon,
    /// <summary>Blue carbon projects (mangroves, seagrass).</summary>
    BlueCarbon,
    /// <summary>Clean cookstove distribution in developing countries.</summary>
    CookstoveDistribution,
    /// <summary>Biochar production and application.</summary>
    Biochar
}

/// <summary>
/// Verification status of an offset purchase.
/// </summary>
public enum VerificationStatus
{
    /// <summary>Verification pending.</summary>
    Pending,
    /// <summary>Verified and certified.</summary>
    Verified,
    /// <summary>Verification failed.</summary>
    Failed,
    /// <summary>Certificate retired/used.</summary>
    Retired
}

/// <summary>
/// A carbon footprint entry.
/// </summary>
public sealed record CarbonFootprintEntry
{
    public required string Id { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required double EmissionsGrams { get; init; }
    public required string Source { get; init; }
    public string? Description { get; init; }
}

/// <summary>
/// An offset purchase record.
/// </summary>
public sealed record OffsetPurchase
{
    public required string Id { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required double OffsetGrams { get; init; }
    public required string Provider { get; init; }
    public required OffsetProjectType ProjectType { get; init; }
    public required string ProjectName { get; init; }
    public required double CostUsd { get; init; }
    public string? CertificateId { get; init; }
    public VerificationStatus VerificationStatus { get; init; }
}

/// <summary>
/// Offset requirement calculation result.
/// </summary>
public sealed record OffsetRequirement
{
    public required double TotalEmissionsGrams { get; init; }
    public required double CurrentOffsetsGrams { get; init; }
    public required double TargetOffsetsGrams { get; init; }
    public required double AdditionalOffsetsNeededGrams { get; init; }
    public required double EstimatedCostUsd { get; init; }
    public required double CurrentOffsetPercent { get; init; }
    public required double TargetOffsetPercent { get; init; }
}

/// <summary>
/// Carbon neutrality report.
/// </summary>
public sealed record CarbonNeutralityReport
{
    public required string ReportId { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
    public required DateTimeOffset PeriodStart { get; init; }
    public required DateTimeOffset PeriodEnd { get; init; }
    public required double TotalEmissionsGrams { get; init; }
    public required double TotalOffsetsGrams { get; init; }
    public required double NetCarbonBalanceGrams { get; init; }
    public required bool IsCarbonNeutral { get; init; }
    public required double OffsetPercentage { get; init; }
    public required IReadOnlyDictionary<string, double> EmissionsBySource { get; init; }
    public required IReadOnlyDictionary<OffsetProjectType, double> OffsetsByProjectType { get; init; }
    public required double TotalOffsetCostUsd { get; init; }
}

/// <summary>
/// A recommended offset project.
/// </summary>
public sealed record OffsetProjectRecommendation
{
    public required string Provider { get; init; }
    public required OffsetProjectType ProjectType { get; init; }
    public required string ProjectName { get; init; }
    public required double CostPerTonUsd { get; init; }
    public required double TotalCostUsd { get; init; }
    public required double OffsetsAvailableTons { get; init; }
    public required string Certification { get; init; }
    public required double Rating { get; init; }
    public required string Description { get; init; }
}
