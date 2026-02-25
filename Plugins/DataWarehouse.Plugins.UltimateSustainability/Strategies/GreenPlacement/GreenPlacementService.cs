using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Carbon;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.GreenPlacement;

/// <summary>
/// Composite placement service implementing <see cref="IGreenPlacementService"/>.
/// Combines real-time grid carbon data from WattTime (primary) and ElectricityMaps (fallback)
/// with the backend green score registry to select the most sustainable storage backend
/// for data placement decisions.
///
/// Subscribes to <c>storage.backend.registered</c> message bus topic to auto-register
/// new backends and publishes <c>sustainability.placement.decision</c> for audit trails.
/// </summary>
public sealed class GreenPlacementService : SustainabilityStrategyBase, IGreenPlacementService
{
    private const string PluginId = "com.datawarehouse.sustainability.ultimate";
    private const string BackendRegisteredTopic = "storage.backend.registered";
    private const string PlacementDecisionTopic = "sustainability.placement.decision";

    /// <summary>
    /// Energy per byte for storage operations in Wh/byte.
    /// Based on average SSD write energy: ~0.005 J/MB = 1.39e-12 Wh/byte.
    /// </summary>
    private const double StorageEnergyPerByteWh = 1.39e-12;

    private readonly WattTimeGridApiStrategy _wattTimeStrategy;
    private readonly ElectricityMapsApiStrategy _electricityMapsStrategy;
    private readonly BackendGreenScoreRegistry _registry;
    private readonly BoundedDictionary<string, GridCarbonData> _regionGridCache = new BoundedDictionary<string, GridCarbonData>(1000);
    private IDisposable? _backendRegistrationSubscription;
    private IMessageBus? _messageBus;

    /// <inheritdoc/>
    public override string StrategyId => "green-placement-service";

    /// <inheritdoc/>
    public override string DisplayName => "Green Placement Service";

    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.CarbonAwareness;

    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.CarbonCalculation |
        SustainabilityCapabilities.ExternalIntegration |
        SustainabilityCapabilities.Scheduling;

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Composite green placement service that selects the most sustainable storage backend " +
        "using real-time grid carbon data from WattTime and ElectricityMaps APIs combined with " +
        "backend green scores (renewable %, PUE, WUE). Supports dynamic re-scoring as grid " +
        "conditions change and publishes placement decisions for carbon audit trails.";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "carbon", "placement", "green", "routing", "sustainability",
        "grid-aware", "renewable", "composite", "audit"
    };

    /// <summary>
    /// Creates a new GreenPlacementService.
    /// </summary>
    /// <param name="wattTimeStrategy">WattTime grid API strategy (primary grid data source).</param>
    /// <param name="electricityMapsStrategy">ElectricityMaps grid API strategy (fallback).</param>
    /// <param name="registry">Backend green score registry.</param>
    public GreenPlacementService(
        WattTimeGridApiStrategy wattTimeStrategy,
        ElectricityMapsApiStrategy electricityMapsStrategy,
        BackendGreenScoreRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(wattTimeStrategy);
        ArgumentNullException.ThrowIfNull(electricityMapsStrategy);
        ArgumentNullException.ThrowIfNull(registry);

        _wattTimeStrategy = wattTimeStrategy;
        _electricityMapsStrategy = electricityMapsStrategy;
        _registry = registry;
    }

    /// <summary>
    /// Creates a new GreenPlacementService with default dependencies.
    /// Provided for auto-discovery scenarios.
    /// </summary>
    public GreenPlacementService()
        : this(new WattTimeGridApiStrategy(), new ElectricityMapsApiStrategy(), new BackendGreenScoreRegistry())
    {
    }

    /// <summary>
    /// Configures Intelligence integration and subscribes to backend registration events.
    /// </summary>
    public override void ConfigureIntelligence(IMessageBus? messageBus)
    {
        base.ConfigureIntelligence(messageBus);
        _messageBus = messageBus;

        if (messageBus != null)
        {
            _backendRegistrationSubscription = messageBus.Subscribe(BackendRegisteredTopic, OnBackendRegisteredAsync);
        }
    }

    /// <inheritdoc/>
    public async Task<CarbonPlacementDecision> SelectGreenestBackendAsync(
        IReadOnlyList<string> candidateBackendIds,
        long dataSizeBytes,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentNullException.ThrowIfNull(candidateBackendIds);

        if (candidateBackendIds.Count == 0)
        {
            throw new ArgumentException("At least one candidate backend must be provided.", nameof(candidateBackendIds));
        }

        // Step 1-2: For each candidate, fetch latest grid data and update scores
        var scoredCandidates = new List<(string BackendId, GreenScore Score, GridCarbonData GridData)>();

        foreach (var backendId in candidateBackendIds)
        {
            var existingScore = _registry.GetScore(backendId);
            var region = _registry.GetBackendRegion(backendId);

            GridCarbonData gridData;
            if (!string.IsNullOrEmpty(region))
            {
                // Step 2: Fetch live grid data for this backend's region
                gridData = await GetGridCarbonDataAsync(region, ct);

                // Step 3: Update registry with fresh carbon intensity
                _registry.UpdateCarbonIntensity(region, gridData.CarbonIntensityGCO2ePerKwh);
            }
            else
            {
                gridData = CreateDefaultGridData();
            }

            // Step 4: Re-read score after update
            var updatedScore = _registry.GetScore(backendId);
            if (updatedScore != null)
            {
                scoredCandidates.Add((backendId, updatedScore, gridData));
            }
            else
            {
                // Backend not in registry -- create a default score for unknown backends
                var defaultScore = new GreenScore
                {
                    BackendId = backendId,
                    Region = region ?? "unknown",
                    RenewablePercentage = 30.0,
                    CarbonIntensityGCO2ePerKwh = gridData.CarbonIntensityGCO2ePerKwh,
                    PowerUsageEffectiveness = 1.5,
                    Score = 30.0,
                    LastUpdated = DateTimeOffset.UtcNow
                };
                scoredCandidates.Add((backendId, defaultScore, gridData));
            }
        }

        // Step 5: Select highest score
        scoredCandidates.Sort((a, b) => b.Score.Score.CompareTo(a.Score.Score));
        var best = scoredCandidates[0];

        // Step 6: Estimate carbon for the operation
        var pue = best.Score.PowerUsageEffectiveness;
        var carbonIntensity = best.GridData.CarbonIntensityGCO2ePerKwh;
        var energyWh = dataSizeBytes * StorageEnergyPerByteWh * pue;
        var carbonGrams = energyWh * carbonIntensity / 1000.0;

        // Step 7: Build decision with alternatives sorted by score
        var alternatives = scoredCandidates
            .Skip(1)
            .Select(c => c.BackendId)
            .ToList()
            .AsReadOnly();

        var decision = new CarbonPlacementDecision
        {
            PreferredBackendId = best.BackendId,
            GreenScore = best.Score,
            Reason = BuildDecisionReason(best.Score, best.GridData),
            AlternativeBackendIds = alternatives,
            EstimatedCarbonGramsCO2e = Math.Round(carbonGrams, 6),
            EstimatedEnergyWh = Math.Round(energyWh, 6)
        };

        RecordOptimizationAction();
        RecordCarbonAvoided(CalculateCarbonSavings(scoredCandidates, dataSizeBytes));

        // Publish decision for audit trail
        await PublishPlacementDecisionAsync(decision, dataSizeBytes, ct);

        return decision;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<GreenScore>> GetGreenScoresAsync(CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        return Task.FromResult(_registry.GetAllScores());
    }

    /// <inheritdoc/>
    public async Task<GridCarbonData> GetGridCarbonDataAsync(string region, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(region);

        // Try WattTime first (primary)
        if (_wattTimeStrategy.IsAvailable())
        {
            try
            {
                var data = await _wattTimeStrategy.GetGridCarbonDataAsync(region, ct);
                if (data.Source != GridDataSource.Estimation)
                {
                    _regionGridCache[region] = data;
                    return data;
                }
            }
            catch
            {

                // Fall through to ElectricityMaps
                System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
            }
        }

        // Fallback to ElectricityMaps
        if (_electricityMapsStrategy.IsAvailable())
        {
            try
            {
                var data = await _electricityMapsStrategy.GetGridCarbonDataAsync(region, ct);
                if (data.Source != GridDataSource.Estimation)
                {
                    _regionGridCache[region] = data;
                    return data;
                }
            }
            catch
            {

                // Fall through to estimation
                System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
            }
        }

        // Final fallback: cached data or estimation
        if (_regionGridCache.TryGetValue(region, out var cached))
        {
            return cached;
        }

        return CreateDefaultGridData(region);
    }

    /// <inheritdoc/>
    public async Task RefreshGridDataAsync(CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        // Collect all unique regions from the registry
        var regions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var score in _registry.GetAllScores())
        {
            if (!string.IsNullOrWhiteSpace(score.Region))
            {
                regions.Add(score.Region);
            }
        }

        // Fetch data for all regions (with limited parallelism to respect rate limits)
        var semaphore = new SemaphoreSlim(3); // Max 3 concurrent API calls
        var tasks = regions.Select(async region =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var data = await GetGridCarbonDataAsync(region, ct);
                _registry.UpdateCarbonIntensity(region, data.CarbonIntensityGCO2ePerKwh);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        // Persist updated scores
        await _registry.PersistAsync(ct);
    }

    /// <inheritdoc/>
    protected override async Task InitializeCoreAsync(CancellationToken ct)
    {
        await _wattTimeStrategy.InitializeAsync(ct);
        await _electricityMapsStrategy.InitializeAsync(ct);
        await _registry.InitializeAsync(ct);
    }

    /// <inheritdoc/>
    protected override async Task DisposeCoreAsync()
    {
        _backendRegistrationSubscription?.Dispose();
        await _wattTimeStrategy.DisposeAsync();
        await _electricityMapsStrategy.DisposeAsync();
        await _registry.PersistAsync();
    }

    /// <summary>
    /// Handles storage.backend.registered messages to auto-register new backends.
    /// Expected payload: backendId, region, renewablePct (optional), pue (optional), wue (optional).
    /// </summary>
    private Task OnBackendRegisteredAsync(PluginMessage message)
    {
        try
        {
            if (message.Payload == null)
                return Task.CompletedTask;

            var backendId = GetPayloadString(message.Payload, "backendId");
            var region = GetPayloadString(message.Payload, "region");

            if (string.IsNullOrWhiteSpace(backendId) || string.IsNullOrWhiteSpace(region))
                return Task.CompletedTask;

            var renewablePct = GetPayloadDouble(message.Payload, "renewablePct", 40.0);
            var pue = GetPayloadDouble(message.Payload, "pue", 1.5);
            var wue = GetPayloadNullableDouble(message.Payload, "wue");

            _registry.RegisterBackend(backendId, region, renewablePct, pue, wue);
        }
        catch
        {

            // Registration from message bus is best-effort
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Publishes a placement decision to the message bus for audit trail.
    /// </summary>
    private async Task PublishPlacementDecisionAsync(CarbonPlacementDecision decision, long dataSizeBytes, CancellationToken ct)
    {
        if (_messageBus == null)
            return;

        try
        {
            var message = new PluginMessage
            {
                Type = "placement.decision",
                SourcePluginId = PluginId,
                Source = "GreenPlacementService",
                Description = $"Green placement: selected {decision.PreferredBackendId} (score: {decision.GreenScore.Score:F1}, " +
                              $"carbon: {decision.EstimatedCarbonGramsCO2e:F4} gCO2e)",
                Payload = new Dictionary<string, object>
                {
                    ["preferredBackendId"] = decision.PreferredBackendId,
                    ["greenScore"] = decision.GreenScore.Score,
                    ["renewablePercentage"] = decision.GreenScore.RenewablePercentage,
                    ["carbonIntensity"] = decision.GreenScore.CarbonIntensityGCO2ePerKwh,
                    ["estimatedCarbonGrams"] = decision.EstimatedCarbonGramsCO2e,
                    ["estimatedEnergyWh"] = decision.EstimatedEnergyWh,
                    ["dataSizeBytes"] = dataSizeBytes,
                    ["alternativeCount"] = decision.AlternativeBackendIds.Count,
                    ["reason"] = decision.Reason,
                    ["timestamp"] = DateTimeOffset.UtcNow.ToString("O")
                }
            };

            await _messageBus.PublishAsync(PlacementDecisionTopic, message, ct);
        }
        catch
        {

            // Audit publish failure is non-fatal
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }
    }

    /// <summary>
    /// Builds a human-readable explanation for the placement decision.
    /// </summary>
    private static string BuildDecisionReason(GreenScore score, GridCarbonData gridData)
    {
        var parts = new List<string>
        {
            $"Renewable: {score.RenewablePercentage:F0}%",
            $"Carbon intensity: {gridData.CarbonIntensityGCO2ePerKwh:F0} gCO2e/kWh",
            $"PUE: {score.PowerUsageEffectiveness:F2}"
        };

        if (score.WaterUsageEffectiveness.HasValue)
        {
            parts.Add($"WUE: {score.WaterUsageEffectiveness.Value:F2} L/kWh");
        }

        parts.Add($"Composite score: {score.Score:F1}/100");

        var sourceLabel = gridData.Source switch
        {
            GridDataSource.WattTime => "WattTime API",
            GridDataSource.ElectricityMaps => "Electricity Maps API",
            _ => "estimation"
        };
        parts.Add($"Grid data source: {sourceLabel}");

        return string.Join(" | ", parts);
    }

    /// <summary>
    /// Calculates carbon savings from choosing the greenest backend over the worst candidate.
    /// </summary>
    private static double CalculateCarbonSavings(
        List<(string BackendId, GreenScore Score, GridCarbonData GridData)> scoredCandidates,
        long dataSizeBytes)
    {
        if (scoredCandidates.Count < 2)
            return 0;

        var best = scoredCandidates[0];
        var worst = scoredCandidates[^1];

        var bestEnergy = dataSizeBytes * StorageEnergyPerByteWh * best.Score.PowerUsageEffectiveness;
        var bestCarbon = bestEnergy * best.GridData.CarbonIntensityGCO2ePerKwh / 1000.0;

        var worstEnergy = dataSizeBytes * StorageEnergyPerByteWh * worst.Score.PowerUsageEffectiveness;
        var worstCarbon = worstEnergy * worst.GridData.CarbonIntensityGCO2ePerKwh / 1000.0;

        return Math.Max(0, worstCarbon - bestCarbon);
    }

    /// <summary>
    /// Creates default grid carbon data for regions without API access.
    /// </summary>
    private static GridCarbonData CreateDefaultGridData(string? region = null)
    {
        return new GridCarbonData
        {
            Region = region ?? "unknown",
            Timestamp = DateTimeOffset.UtcNow,
            CarbonIntensityGCO2ePerKwh = 475.0,
            RenewablePercentage = 30.0,
            Source = GridDataSource.Estimation,
            ForecastHours = 0,
            AverageIntensity = 475.0
        };
    }

    #region Payload Helpers

    private static string GetPayloadString(Dictionary<string, object> payload, string key)
    {
        return payload.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;
    }

    private static double GetPayloadDouble(Dictionary<string, object> payload, string key, double defaultValue)
    {
        if (payload.TryGetValue(key, out var value))
        {
            if (value is double d) return d;
            if (value is int i) return i;
            if (value is long l) return l;
            if (value is float f) return f;
            if (double.TryParse(value?.ToString(), out var parsed)) return parsed;
        }
        return defaultValue;
    }

    private static double? GetPayloadNullableDouble(Dictionary<string, object> payload, string key)
    {
        if (payload.TryGetValue(key, out var value))
        {
            if (value is double d) return d;
            if (value is int i) return i;
            if (value is long l) return l;
            if (value is float f) return f;
            if (double.TryParse(value?.ToString(), out var parsed)) return parsed;
        }
        return null;
    }

    #endregion
}
