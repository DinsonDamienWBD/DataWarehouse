using System.Collections.Concurrent;
using System.Diagnostics;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateSustainability;

/// <summary>
/// Defines the category of sustainability strategy.
/// </summary>
public enum SustainabilityCategory
{
    /// <summary>
    /// Carbon awareness strategies for tracking and reducing carbon emissions.
    /// </summary>
    CarbonAwareness,

    /// <summary>
    /// Energy optimization strategies for reducing power consumption.
    /// </summary>
    EnergyOptimization,

    /// <summary>
    /// Battery awareness strategies for mobile and UPS scenarios.
    /// </summary>
    BatteryAwareness,

    /// <summary>
    /// Thermal management strategies for temperature control.
    /// </summary>
    ThermalManagement,

    /// <summary>
    /// Resource efficiency strategies for memory, disk, and network optimization.
    /// </summary>
    ResourceEfficiency,

    /// <summary>
    /// Scheduling strategies for off-peak and renewable energy windows.
    /// </summary>
    Scheduling,

    /// <summary>
    /// Metrics strategies for energy and carbon tracking.
    /// </summary>
    Metrics,

    /// <summary>
    /// Cloud optimization strategies for sustainable cloud usage.
    /// </summary>
    CloudOptimization
}

/// <summary>
/// Defines the capabilities of a sustainability strategy.
/// </summary>
[Flags]
public enum SustainabilityCapabilities
{
    /// <summary>
    /// No special capabilities.
    /// </summary>
    None = 0,

    /// <summary>
    /// Can monitor energy consumption in real-time.
    /// </summary>
    RealTimeMonitoring = 1 << 0,

    /// <summary>
    /// Can actively control power states.
    /// </summary>
    ActiveControl = 1 << 1,

    /// <summary>
    /// Supports scheduling of workloads.
    /// </summary>
    Scheduling = 1 << 2,

    /// <summary>
    /// Provides carbon emission calculations.
    /// </summary>
    CarbonCalculation = 1 << 3,

    /// <summary>
    /// Integrates with external APIs (grid carbon, weather, etc.).
    /// </summary>
    ExternalIntegration = 1 << 4,

    /// <summary>
    /// Supports predictive analytics for optimization.
    /// </summary>
    PredictiveAnalytics = 1 << 5,

    /// <summary>
    /// Can generate sustainability reports.
    /// </summary>
    Reporting = 1 << 6,

    /// <summary>
    /// Supports AI-driven optimization.
    /// </summary>
    IntelligenceAware = 1 << 7,

    /// <summary>
    /// Can operate in batch mode.
    /// </summary>
    BatchMode = 1 << 8,

    /// <summary>
    /// Supports alerts and notifications.
    /// </summary>
    Alerting = 1 << 9
}

/// <summary>
/// Statistics tracking for sustainability operations.
/// </summary>
public sealed class SustainabilityStatistics
{
    /// <summary>
    /// Total energy saved in watt-hours.
    /// </summary>
    public double TotalEnergySavedWh { get; set; }

    /// <summary>
    /// Total carbon emissions avoided in grams CO2e.
    /// </summary>
    public double TotalCarbonAvoidedGrams { get; set; }

    /// <summary>
    /// Total number of optimization actions taken.
    /// </summary>
    public long TotalOptimizationActions { get; set; }

    /// <summary>
    /// Total monitoring samples collected.
    /// </summary>
    public long TotalSamplesCollected { get; set; }

    /// <summary>
    /// Total workloads scheduled.
    /// </summary>
    public long TotalWorkloadsScheduled { get; set; }

    /// <summary>
    /// Total cost savings in USD.
    /// </summary>
    public double TotalCostSavingsUsd { get; set; }

    /// <summary>
    /// Average power consumption in watts.
    /// </summary>
    public double AveragePowerWatts { get; set; }

    /// <summary>
    /// Peak power consumption in watts.
    /// </summary>
    public double PeakPowerWatts { get; set; }

    /// <summary>
    /// Current carbon intensity (gCO2e/kWh).
    /// </summary>
    public double CurrentCarbonIntensity { get; set; }

    /// <summary>
    /// Total time in low-power mode in seconds.
    /// </summary>
    public double TotalLowPowerTimeSeconds { get; set; }

    /// <summary>
    /// Number of thermal throttling events.
    /// </summary>
    public long ThermalThrottlingEvents { get; set; }

    /// <summary>
    /// Gets the average carbon intensity over the monitoring period.
    /// </summary>
    public double AverageCarbonIntensity { get; set; }
}

/// <summary>
/// Energy reading from a power monitor.
/// </summary>
public sealed record EnergyReading
{
    /// <summary>
    /// Timestamp of the reading.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Current power consumption in watts.
    /// </summary>
    public required double PowerWatts { get; init; }

    /// <summary>
    /// Cumulative energy consumed in watt-hours.
    /// </summary>
    public double EnergyWh { get; init; }

    /// <summary>
    /// Current carbon intensity of the grid (gCO2e/kWh).
    /// </summary>
    public double CarbonIntensity { get; init; }

    /// <summary>
    /// Source of the reading (RAPL, smart plug, estimation, etc.).
    /// </summary>
    public string Source { get; init; } = "Unknown";

    /// <summary>
    /// Component being monitored (CPU, GPU, system, etc.).
    /// </summary>
    public string Component { get; init; } = "System";
}

/// <summary>
/// Carbon emission data.
/// </summary>
public sealed record CarbonEmission
{
    /// <summary>
    /// Timestamp of the emission calculation.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Carbon emissions in grams CO2e.
    /// </summary>
    public required double EmissionsGrams { get; init; }

    /// <summary>
    /// Energy consumed for this emission (Wh).
    /// </summary>
    public double EnergyConsumedWh { get; init; }

    /// <summary>
    /// Carbon intensity used for calculation (gCO2e/kWh).
    /// </summary>
    public double CarbonIntensity { get; init; }

    /// <summary>
    /// Region/grid where emission occurred.
    /// </summary>
    public string Region { get; init; } = "Unknown";

    /// <summary>
    /// Scope of the emission (1, 2, or 3).
    /// </summary>
    public int Scope { get; init; } = 2;
}

/// <summary>
/// Optimization recommendation from a sustainability strategy.
/// </summary>
public sealed record SustainabilityRecommendation
{
    /// <summary>
    /// Unique identifier for this recommendation.
    /// </summary>
    public required string RecommendationId { get; init; }

    /// <summary>
    /// Type of recommendation.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Priority (1-10, higher is more urgent).
    /// </summary>
    public int Priority { get; init; } = 5;

    /// <summary>
    /// Human-readable description.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Estimated energy savings in Wh.
    /// </summary>
    public double EstimatedEnergySavingsWh { get; init; }

    /// <summary>
    /// Estimated carbon reduction in grams CO2e.
    /// </summary>
    public double EstimatedCarbonReductionGrams { get; init; }

    /// <summary>
    /// Estimated cost savings in USD.
    /// </summary>
    public double EstimatedCostSavingsUsd { get; init; }

    /// <summary>
    /// Implementation difficulty (1-10).
    /// </summary>
    public int ImplementationDifficulty { get; init; } = 5;

    /// <summary>
    /// Whether the recommendation can be applied automatically.
    /// </summary>
    public bool CanAutoApply { get; init; }

    /// <summary>
    /// Action to take to implement the recommendation.
    /// </summary>
    public string? Action { get; init; }

    /// <summary>
    /// Additional parameters for the action.
    /// </summary>
    public Dictionary<string, object>? ActionParameters { get; init; }
}

/// <summary>
/// Interface for sustainability strategies.
/// </summary>
public interface ISustainabilityStrategy
{
    /// <summary>
    /// Unique identifier for this strategy.
    /// </summary>
    string StrategyId { get; }

    /// <summary>
    /// Human-readable display name.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Category of this strategy.
    /// </summary>
    SustainabilityCategory Category { get; }

    /// <summary>
    /// Capabilities of this strategy.
    /// </summary>
    SustainabilityCapabilities Capabilities { get; }

    /// <summary>
    /// Semantic description for AI discovery.
    /// </summary>
    string SemanticDescription { get; }

    /// <summary>
    /// Tags for categorization and discovery.
    /// </summary>
    string[] Tags { get; }

    /// <summary>
    /// Gets statistics for this strategy.
    /// </summary>
    SustainabilityStatistics GetStatistics();

    /// <summary>
    /// Resets the statistics.
    /// </summary>
    void ResetStatistics();

    /// <summary>
    /// Initializes the strategy.
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// Disposes of the strategy resources.
    /// </summary>
    Task DisposeAsync();

    /// <summary>
    /// Gets current recommendations from this strategy.
    /// </summary>
    Task<IReadOnlyList<SustainabilityRecommendation>> GetRecommendationsAsync(CancellationToken ct = default);

    /// <summary>
    /// Applies a recommendation.
    /// </summary>
    Task<bool> ApplyRecommendationAsync(string recommendationId, CancellationToken ct = default);
}

/// <summary>
/// Abstract base class for sustainability strategies.
/// Provides common functionality including statistics tracking, Intelligence integration,
/// and standardized operation management.
/// </summary>
public abstract class SustainabilityStrategyBase : ISustainabilityStrategy
{
    private readonly SustainabilityStatistics _statistics = new();
    private readonly object _statsLock = new();
    private bool _initialized;
    private readonly ConcurrentDictionary<string, SustainabilityRecommendation> _activeRecommendations = new();

    /// <summary>
    /// Message bus for Intelligence communication.
    /// </summary>
    protected IMessageBus? MessageBus { get; private set; }

    /// <summary>
    /// Whether Intelligence integration is available.
    /// </summary>
    protected bool IsIntelligenceAvailable => MessageBus != null;

    /// <inheritdoc/>
    public abstract string StrategyId { get; }

    /// <inheritdoc/>
    public abstract string DisplayName { get; }

    /// <inheritdoc/>
    public abstract SustainabilityCategory Category { get; }

    /// <inheritdoc/>
    public abstract SustainabilityCapabilities Capabilities { get; }

    /// <inheritdoc/>
    public abstract string SemanticDescription { get; }

    /// <inheritdoc/>
    public abstract string[] Tags { get; }

    /// <summary>
    /// Gets whether the strategy has been initialized.
    /// </summary>
    protected bool IsInitialized => _initialized;

    /// <inheritdoc/>
    public SustainabilityStatistics GetStatistics()
    {
        lock (_statsLock)
        {
            return new SustainabilityStatistics
            {
                TotalEnergySavedWh = _statistics.TotalEnergySavedWh,
                TotalCarbonAvoidedGrams = _statistics.TotalCarbonAvoidedGrams,
                TotalOptimizationActions = _statistics.TotalOptimizationActions,
                TotalSamplesCollected = _statistics.TotalSamplesCollected,
                TotalWorkloadsScheduled = _statistics.TotalWorkloadsScheduled,
                TotalCostSavingsUsd = _statistics.TotalCostSavingsUsd,
                AveragePowerWatts = _statistics.AveragePowerWatts,
                PeakPowerWatts = _statistics.PeakPowerWatts,
                CurrentCarbonIntensity = _statistics.CurrentCarbonIntensity,
                TotalLowPowerTimeSeconds = _statistics.TotalLowPowerTimeSeconds,
                ThermalThrottlingEvents = _statistics.ThermalThrottlingEvents,
                AverageCarbonIntensity = _statistics.AverageCarbonIntensity
            };
        }
    }

    /// <inheritdoc/>
    public void ResetStatistics()
    {
        lock (_statsLock)
        {
            _statistics.TotalEnergySavedWh = 0;
            _statistics.TotalCarbonAvoidedGrams = 0;
            _statistics.TotalOptimizationActions = 0;
            _statistics.TotalSamplesCollected = 0;
            _statistics.TotalWorkloadsScheduled = 0;
            _statistics.TotalCostSavingsUsd = 0;
            _statistics.AveragePowerWatts = 0;
            _statistics.PeakPowerWatts = 0;
            _statistics.CurrentCarbonIntensity = 0;
            _statistics.TotalLowPowerTimeSeconds = 0;
            _statistics.ThermalThrottlingEvents = 0;
            _statistics.AverageCarbonIntensity = 0;
        }
    }

    /// <inheritdoc/>
    public virtual async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;
        await InitializeCoreAsync(ct);
        _initialized = true;
    }

    /// <inheritdoc/>
    public virtual async Task DisposeAsync()
    {
        if (!_initialized) return;
        await DisposeCoreAsync();
        _initialized = false;
    }

    /// <inheritdoc/>
    public virtual Task<IReadOnlyList<SustainabilityRecommendation>> GetRecommendationsAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<SustainabilityRecommendation>>(_activeRecommendations.Values.ToList().AsReadOnly());
    }

    /// <inheritdoc/>
    public virtual Task<bool> ApplyRecommendationAsync(string recommendationId, CancellationToken ct = default)
    {
        if (_activeRecommendations.TryRemove(recommendationId, out _))
        {
            RecordOptimizationAction();
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    /// <summary>
    /// Core initialization logic. Override in derived classes.
    /// </summary>
    protected virtual Task InitializeCoreAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Core disposal logic. Override in derived classes.
    /// </summary>
    protected virtual Task DisposeCoreAsync() => Task.CompletedTask;

    /// <summary>
    /// Adds a recommendation.
    /// </summary>
    protected void AddRecommendation(SustainabilityRecommendation recommendation)
    {
        _activeRecommendations[recommendation.RecommendationId] = recommendation;
    }

    /// <summary>
    /// Removes a recommendation.
    /// </summary>
    protected bool RemoveRecommendation(string recommendationId)
    {
        return _activeRecommendations.TryRemove(recommendationId, out _);
    }

    /// <summary>
    /// Clears all recommendations.
    /// </summary>
    protected void ClearRecommendations()
    {
        _activeRecommendations.Clear();
    }

    /// <summary>
    /// Records energy saved.
    /// </summary>
    protected void RecordEnergySaved(double wattHours, double costSavingsUsd = 0)
    {
        lock (_statsLock)
        {
            _statistics.TotalEnergySavedWh += wattHours;
            _statistics.TotalCostSavingsUsd += costSavingsUsd;
        }
    }

    /// <summary>
    /// Records carbon avoided.
    /// </summary>
    protected void RecordCarbonAvoided(double grams)
    {
        lock (_statsLock)
        {
            _statistics.TotalCarbonAvoidedGrams += grams;
        }
    }

    /// <summary>
    /// Records an optimization action.
    /// </summary>
    protected void RecordOptimizationAction()
    {
        lock (_statsLock)
        {
            _statistics.TotalOptimizationActions++;
        }
    }

    /// <summary>
    /// Records a monitoring sample.
    /// </summary>
    protected void RecordSample(double powerWatts, double carbonIntensity)
    {
        lock (_statsLock)
        {
            _statistics.TotalSamplesCollected++;
            var n = _statistics.TotalSamplesCollected;
            _statistics.AveragePowerWatts = ((_statistics.AveragePowerWatts * (n - 1)) + powerWatts) / n;
            _statistics.AverageCarbonIntensity = ((_statistics.AverageCarbonIntensity * (n - 1)) + carbonIntensity) / n;
            if (powerWatts > _statistics.PeakPowerWatts)
                _statistics.PeakPowerWatts = powerWatts;
            _statistics.CurrentCarbonIntensity = carbonIntensity;
        }
    }

    /// <summary>
    /// Records a scheduled workload.
    /// </summary>
    protected void RecordWorkloadScheduled()
    {
        lock (_statsLock)
        {
            _statistics.TotalWorkloadsScheduled++;
        }
    }

    /// <summary>
    /// Records time in low power mode.
    /// </summary>
    protected void RecordLowPowerTime(double seconds)
    {
        lock (_statsLock)
        {
            _statistics.TotalLowPowerTimeSeconds += seconds;
        }
    }

    /// <summary>
    /// Records a thermal throttling event.
    /// </summary>
    protected void RecordThermalThrottling()
    {
        lock (_statsLock)
        {
            _statistics.ThermalThrottlingEvents++;
        }
    }

    /// <summary>
    /// Throws if the strategy has not been initialized.
    /// </summary>
    protected void ThrowIfNotInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException($"Strategy '{StrategyId}' has not been initialized.");
    }

    #region Intelligence Integration

    /// <summary>
    /// Configures Intelligence integration for this strategy.
    /// </summary>
    public virtual void ConfigureIntelligence(IMessageBus? messageBus)
    {
        MessageBus = messageBus;
    }

    /// <summary>
    /// Gets static knowledge about this strategy for AI discovery.
    /// </summary>
    public virtual KnowledgeObject GetStrategyKnowledge()
    {
        return new KnowledgeObject
        {
            Id = $"sustainability.strategy.{StrategyId}",
            Topic = "sustainability.strategies",
            SourcePluginId = "com.datawarehouse.sustainability.ultimate",
            SourcePluginName = DisplayName,
            KnowledgeType = "capability",
            Description = SemanticDescription,
            Payload = GetKnowledgePayload(),
            Tags = GetKnowledgeTags(),
            Confidence = 1.0f,
            Timestamp = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Gets the registered capability for this strategy.
    /// </summary>
    public virtual RegisteredCapability GetStrategyCapability()
    {
        return new RegisteredCapability
        {
            CapabilityId = $"sustainability.{StrategyId}",
            DisplayName = DisplayName,
            Description = SemanticDescription,
            Category = CapabilityCategory.Custom,
            SubCategory = "Sustainability",
            PluginId = "com.datawarehouse.sustainability.ultimate",
            PluginName = "Ultimate Sustainability",
            PluginVersion = "1.0.0",
            Tags = GetKnowledgeTags(),
            Metadata = GetCapabilityMetadata(),
            SemanticDescription = SemanticDescription
        };
    }

    /// <summary>
    /// Gets knowledge payload for this strategy.
    /// </summary>
    protected virtual Dictionary<string, object> GetKnowledgePayload()
    {
        return new Dictionary<string, object>
        {
            ["strategyId"] = StrategyId,
            ["displayName"] = DisplayName,
            ["category"] = Category.ToString(),
            ["capabilities"] = Capabilities.ToString(),
            ["supportsRealTimeMonitoring"] = Capabilities.HasFlag(SustainabilityCapabilities.RealTimeMonitoring),
            ["supportsActiveControl"] = Capabilities.HasFlag(SustainabilityCapabilities.ActiveControl),
            ["supportsScheduling"] = Capabilities.HasFlag(SustainabilityCapabilities.Scheduling),
            ["supportsCarbonCalculation"] = Capabilities.HasFlag(SustainabilityCapabilities.CarbonCalculation),
            ["supportsReporting"] = Capabilities.HasFlag(SustainabilityCapabilities.Reporting),
            ["intelligenceAware"] = Capabilities.HasFlag(SustainabilityCapabilities.IntelligenceAware)
        };
    }

    /// <summary>
    /// Gets capability metadata for this strategy.
    /// </summary>
    protected virtual Dictionary<string, object> GetCapabilityMetadata()
    {
        return new Dictionary<string, object>
        {
            ["strategyId"] = StrategyId,
            ["category"] = Category.ToString(),
            ["capabilities"] = (int)Capabilities
        };
    }

    /// <summary>
    /// Gets tags for this strategy.
    /// </summary>
    protected virtual string[] GetKnowledgeTags()
    {
        var tags = new List<string>
        {
            "sustainability",
            "strategy",
            "green-computing",
            Category.ToString().ToLowerInvariant(),
            StrategyId.ToLowerInvariant()
        };

        if (Capabilities.HasFlag(SustainabilityCapabilities.CarbonCalculation))
            tags.Add("carbon");
        if (Capabilities.HasFlag(SustainabilityCapabilities.RealTimeMonitoring))
            tags.Add("monitoring");
        if (Capabilities.HasFlag(SustainabilityCapabilities.IntelligenceAware))
            tags.Add("ai-enhanced");

        return tags.ToArray();
    }

    #endregion
}

/// <summary>
/// Thread-safe registry for sustainability strategies.
/// </summary>
public sealed class SustainabilityStrategyRegistry
{
    private readonly ConcurrentDictionary<string, ISustainabilityStrategy> _strategies =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a strategy.
    /// </summary>
    public void Register(ISustainabilityStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        _strategies[strategy.StrategyId] = strategy;
    }

    /// <summary>
    /// Unregisters a strategy by ID.
    /// </summary>
    public bool Unregister(string strategyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        return _strategies.TryRemove(strategyId, out _);
    }

    /// <summary>
    /// Gets a strategy by ID.
    /// </summary>
    public ISustainabilityStrategy? Get(string strategyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        return _strategies.TryGetValue(strategyId, out var strategy) ? strategy : null;
    }

    /// <summary>
    /// Gets all registered strategies.
    /// </summary>
    public IReadOnlyCollection<ISustainabilityStrategy> GetAll()
    {
        return _strategies.Values.ToList().AsReadOnly();
    }

    /// <summary>
    /// Gets strategies by category.
    /// </summary>
    public IReadOnlyCollection<ISustainabilityStrategy> GetByCategory(SustainabilityCategory category)
    {
        return _strategies.Values
            .Where(s => s.Category == category)
            .OrderBy(s => s.DisplayName)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Gets the count of registered strategies.
    /// </summary>
    public int Count => _strategies.Count;

    /// <summary>
    /// Auto-discovers and registers strategies from assemblies.
    /// </summary>
    public int AutoDiscover(params System.Reflection.Assembly[] assemblies)
    {
        var strategyType = typeof(ISustainabilityStrategy);
        int discovered = 0;

        foreach (var assembly in assemblies)
        {
            try
            {
                var types = assembly.GetTypes()
                    .Where(t => !t.IsAbstract && !t.IsInterface && strategyType.IsAssignableFrom(t));

                foreach (var type in types)
                {
                    try
                    {
                        if (Activator.CreateInstance(type) is ISustainabilityStrategy strategy)
                        {
                            Register(strategy);
                            discovered++;
                        }
                    }
                    catch
                    {
                        // Skip types that cannot be instantiated
                    }
                }
            }
            catch
            {
                // Skip assemblies that cannot be scanned
            }
        }

        return discovered;
    }
}
