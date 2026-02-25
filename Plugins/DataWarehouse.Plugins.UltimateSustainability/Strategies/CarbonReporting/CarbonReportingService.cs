using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Carbon;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.CarbonReporting;

/// <summary>
/// Composite carbon reporting service implementing <see cref="ICarbonReportingService"/>
/// from the SDK. Orchestrates GHG Protocol reporting via <see cref="GhgProtocolReportingStrategy"/>
/// and dashboard data aggregation via <see cref="CarbonDashboardDataStrategy"/>
/// into a unified API surface.
///
/// Extends <see cref="SustainabilityStrategyBase"/> for framework integration and
/// provides the full <see cref="ICarbonReportingService"/> contract including:
/// - GHG report generation (Scope 2 + 3)
/// - Total emissions by scope
/// - Regional emission breakdowns
/// - Aggregated carbon summary with renewable %, intensity, and top emitting region
/// </summary>
public sealed class CarbonReportingService : SustainabilityStrategyBase, ICarbonReportingService
{
    private readonly GhgProtocolReportingStrategy _ghgStrategy;
    private readonly CarbonDashboardDataStrategy _dashboardStrategy;

    // Cached green score averages for summary generation
    private readonly BoundedDictionary<string, double> _regionCarbonIntensity = new BoundedDictionary<string, double>(1000);
    private readonly BoundedDictionary<string, double> _regionRenewablePct = new BoundedDictionary<string, double>(1000);
    private IDisposable? _intensitySubscription;
    private IDisposable? _scoresSubscription;

    /// <summary>
    /// Default lookback period for summaries when no explicit period is provided.
    /// </summary>
    private static readonly TimeSpan DefaultSummaryPeriod = TimeSpan.FromDays(30);

    private const string PluginId = "com.datawarehouse.sustainability.ultimate";

    /// <inheritdoc/>
    public override string StrategyId => "carbon-reporting-service";

    /// <inheritdoc/>
    public override string DisplayName => "Carbon Reporting Service";

    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.Metrics;

    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.Reporting |
        SustainabilityCapabilities.CarbonCalculation;

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Composite carbon reporting service providing GHG Protocol-compliant emission reports, " +
        "per-scope emission totals, regional breakdowns, and aggregated carbon summaries. " +
        "Implements ICarbonReportingService SDK contract for system-wide carbon reporting integration.";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "reporting", "carbon", "ghg", "summary", "emissions",
        "scope2", "scope3", "composite", "service"
    };

    /// <summary>
    /// Creates a new CarbonReportingService with the specified GHG and dashboard strategies.
    /// </summary>
    /// <param name="ghgStrategy">GHG Protocol reporting strategy for Scope 2/3 reports.</param>
    /// <param name="dashboardStrategy">Dashboard data aggregation strategy.</param>
    public CarbonReportingService(
        GhgProtocolReportingStrategy ghgStrategy,
        CarbonDashboardDataStrategy dashboardStrategy)
    {
        ArgumentNullException.ThrowIfNull(ghgStrategy);
        ArgumentNullException.ThrowIfNull(dashboardStrategy);

        _ghgStrategy = ghgStrategy;
        _dashboardStrategy = dashboardStrategy;
    }

    /// <summary>
    /// Creates a new CarbonReportingService with default dependencies.
    /// Provided for auto-discovery scenarios.
    /// </summary>
    public CarbonReportingService()
        : this(new GhgProtocolReportingStrategy(), new CarbonDashboardDataStrategy())
    {
    }

    /// <summary>
    /// Gets the underlying GHG Protocol reporting strategy.
    /// </summary>
    public GhgProtocolReportingStrategy GhgStrategy => _ghgStrategy;

    /// <summary>
    /// Gets the underlying dashboard data strategy.
    /// </summary>
    public CarbonDashboardDataStrategy DashboardStrategy => _dashboardStrategy;

    /// <inheritdoc/>
    public override void ConfigureIntelligence(IMessageBus? messageBus)
    {
        base.ConfigureIntelligence(messageBus);

        // Propagate message bus to child strategies
        _ghgStrategy.ConfigureIntelligence(messageBus);
        _dashboardStrategy.ConfigureIntelligence(messageBus);
    }

    /// <inheritdoc/>
    protected override async Task InitializeCoreAsync(CancellationToken ct)
    {
        await _ghgStrategy.InitializeAsync(ct);
        await _dashboardStrategy.InitializeAsync(ct);
        SubscribeToUpdates();
    }

    /// <inheritdoc/>
    protected override async Task DisposeCoreAsync()
    {
        _intensitySubscription?.Dispose();
        _intensitySubscription = null;
        _scoresSubscription?.Dispose();
        _scoresSubscription = null;

        await _ghgStrategy.DisposeAsync();
        await _dashboardStrategy.DisposeAsync();
    }

    #region ICarbonReportingService Implementation

    /// <inheritdoc/>
    public async Task<IReadOnlyList<GhgReportEntry>> GenerateGhgReportAsync(
        DateTimeOffset from, DateTimeOffset to, string? tenantId = null, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        var scope2 = await _ghgStrategy.GenerateScope2ReportAsync(from, to, tenantId, ct);
        var scope3 = await _ghgStrategy.GenerateScope3ReportAsync(from, to, tenantId, ct);

        var combined = scope2.Concat(scope3).ToList();

        RecordOptimizationAction();
        return combined.AsReadOnly();
    }

    /// <inheritdoc/>
    public async Task<double> GetTotalEmissionsAsync(
        GhgScopeCategory scope, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        IReadOnlyList<GhgReportEntry> entries;

        switch (scope)
        {
            case GhgScopeCategory.Scope2_PurchasedElectricity:
                entries = await _ghgStrategy.GenerateScope2ReportAsync(from, to, tenantId: null, ct);
                break;

            case GhgScopeCategory.Scope3_ValueChain:
                entries = await _ghgStrategy.GenerateScope3ReportAsync(from, to, tenantId: null, ct);
                break;

            default:
                // Scope 1 not applicable for data warehouse operations
                return 0;
        }

        return entries.Sum(e => e.EmissionsGramsCO2e);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, double>> GetEmissionsByRegionAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        var allEntries = await GenerateGhgReportAsync(from, to, tenantId: null, ct);

        var byRegion = allEntries
            .GroupBy(e => e.Region)
            .ToDictionary(
                g => g.Key,
                g => Math.Round(g.Sum(e => e.EmissionsGramsCO2e), 4));

        return byRegion;
    }

    /// <inheritdoc/>
    public async Task<CarbonSummary> GetCarbonSummaryAsync(
        string? tenantId = null, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        var now = DateTimeOffset.UtcNow;
        var periodStart = now - DefaultSummaryPeriod;

        // Get total emissions from GHG report
        var entries = await GenerateGhgReportAsync(periodStart, now, tenantId, ct);
        var totalEmissions = entries.Sum(e => e.EmissionsGramsCO2e);
        var totalEnergy = entries.Sum(e => e.EnergyConsumedWh);

        // Calculate average renewable percentage from tracked regions
        var avgRenewable = _regionRenewablePct.Count > 0
            ? _regionRenewablePct.Values.Average()
            : 30.0; // Default estimate

        // Calculate average carbon intensity from tracked regions
        var avgCarbonIntensity = _regionCarbonIntensity.Count > 0
            ? _regionCarbonIntensity.Values.Average()
            : 400.0; // Global average

        // Determine top emitting region
        var regionEmissions = entries
            .GroupBy(e => e.Region)
            .Select(g => new { Region = g.Key, Total = g.Sum(e => e.EmissionsGramsCO2e) })
            .OrderByDescending(x => x.Total)
            .FirstOrDefault();

        var topRegion = regionEmissions?.Region ?? "unknown";

        return new CarbonSummary
        {
            TotalEmissionsGramsCO2e = Math.Round(totalEmissions, 4),
            TotalEnergyWh = Math.Round(totalEnergy, 4),
            RenewablePercentage = Math.Round(avgRenewable, 2),
            AvgCarbonIntensity = Math.Round(avgCarbonIntensity, 2),
            TopEmittingRegion = topRegion,
            PeriodStart = periodStart,
            PeriodEnd = now
        };
    }

    #endregion

    #region Extended Reporting API

    /// <summary>
    /// Generates a full GHG Protocol report with executive summary.
    /// </summary>
    public Task<GhgFullReport> GenerateFullReportAsync(
        DateTimeOffset from, DateTimeOffset to, string organizationName,
        string? tenantId = null, CancellationToken ct = default)
    {
        return _ghgStrategy.GenerateFullReportAsync(from, to, organizationName, tenantId, ct);
    }

    /// <summary>
    /// Gets the dashboard data strategy for time-series queries.
    /// </summary>
    public IReadOnlyList<TimeSeriesPoint> GetCarbonIntensityTimeSeries(
        string region, TimeSpan period, TimeSpan granularity)
    {
        return _dashboardStrategy.GetCarbonIntensityTimeSeries(region, period, granularity);
    }

    /// <summary>
    /// Gets budget utilization time series for a tenant.
    /// </summary>
    public IReadOnlyList<TimeSeriesPoint> GetBudgetUtilizationTimeSeries(
        string tenantId, TimeSpan period)
    {
        return _dashboardStrategy.GetBudgetUtilizationTimeSeries(tenantId, period);
    }

    /// <summary>
    /// Gets green score trend data.
    /// </summary>
    public IReadOnlyList<TimeSeriesPoint> GetGreenScoreTrend(TimeSpan period)
    {
        return _dashboardStrategy.GetGreenScoreTrend(period);
    }

    /// <summary>
    /// Gets emissions breakdown by operation type.
    /// </summary>
    public EmissionsByOperationType GetEmissionsByOperationType(DateTimeOffset from, DateTimeOffset to)
    {
        return _dashboardStrategy.GetEmissionsByOperationType(from, to);
    }

    /// <summary>
    /// Gets top emitting tenants.
    /// </summary>
    public IReadOnlyList<TenantCarbonUsage> GetTopEmittingTenants(
        int topN, DateTimeOffset from, DateTimeOffset to)
    {
        return _dashboardStrategy.GetTopEmittingTenants(topN, from, to);
    }

    #endregion

    #region Message Bus Subscriptions

    private void SubscribeToUpdates()
    {
        if (MessageBus == null) return;

        _intensitySubscription = MessageBus.Subscribe("sustainability.carbon.intensity.updated", (PluginMessage message) =>
        {
            try
            {
                var payload = message.Payload;
                var region = payload.TryGetValue("region", out var r) ? r?.ToString() : null;
                var intensity = ExtractDouble(payload, "carbonIntensityGCO2ePerKwh");
                var renewable = ExtractDouble(payload, "renewablePercentage");

                if (!string.IsNullOrWhiteSpace(region))
                {
                    if (intensity > 0)
                        _regionCarbonIntensity[region] = intensity;
                    if (renewable > 0)
                        _regionRenewablePct[region] = renewable;
                }
            }
            catch
            {

                // Non-critical
                System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
            }

            return Task.CompletedTask;
        });

        _scoresSubscription = MessageBus.Subscribe("sustainability.placement.decision", (PluginMessage message) =>
        {
            try
            {
                var payload = message.Payload;
                var renewable = ExtractDouble(payload, "renewablePercentage");
                var carbonIntensity = ExtractDouble(payload, "carbonIntensity");

                if (renewable > 0 && payload.TryGetValue("preferredBackendId", out var bid) &&
                    bid is string backendId && !string.IsNullOrEmpty(backendId))
                {
                    _regionRenewablePct[backendId] = renewable;
                }
                if (carbonIntensity > 0 && payload.TryGetValue("preferredBackendId", out var bid2) &&
                    bid2 is string backendId2 && !string.IsNullOrEmpty(backendId2))
                {
                    _regionCarbonIntensity[backendId2] = carbonIntensity;
                }
            }
            catch
            {

                // Non-critical
                System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
            }

            return Task.CompletedTask;
        });
    }

    #endregion

    #region Helpers

    private static double ExtractDouble(Dictionary<string, object> payload, string key)
    {
        if (payload.TryGetValue(key, out var value))
        {
            if (value is double d) return d;
            if (value is int i) return i;
            if (value is long l) return l;
            if (value is float f) return f;
            if (double.TryParse(value?.ToString(), out var parsed)) return parsed;
        }
        return 0;
    }

    #endregion
}
