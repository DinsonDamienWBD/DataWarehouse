# Plugin: UltimateSustainability
> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.
> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.


## Project: DataWarehouse.Plugins.UltimateSustainability

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/UltimateSustainabilityPlugin.cs
```csharp
public sealed class UltimateSustainabilityPlugin : InfrastructurePluginBase
{
#endregion
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override string InfrastructureDomain;;
    public override PluginCategory Category;;
    public void RegisterStrategy(ISustainabilityStrategy strategy);
    public ISustainabilityStrategy? GetStrategy(string strategyId);
    public IReadOnlyCollection<string> GetRegisteredStrategies();;
    public IReadOnlyCollection<ISustainabilityStrategy> GetStrategiesByCategory(SustainabilityCategory category);
    public void SetActiveStrategy(string strategyId);
    public async Task<IReadOnlyList<SustainabilityRecommendation>> GetAllRecommendationsAsync(CancellationToken ct = default);
    public SustainabilityStatistics GetAggregateStatistics();
    public UltimateSustainabilityPlugin();
    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities
{
    get
    {
        // Return cached value if still valid (volatile read for thread visibility).
        if (_cachedCapabilities != null)
            return _cachedCapabilities;
        var capabilities = new List<RegisteredCapability>
        {
            new RegisteredCapability
            {
                CapabilityId = $"{Id}.monitor",
                DisplayName = $"{Name} - Monitor",
                Description = "Monitor energy consumption and carbon emissions",
                Category = CapabilityCategory.Custom,
                SubCategory = "Sustainability",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[]
                {
                    "sustainability",
                    "monitoring",
                    "energy",
                    "carbon"
                }
            },
            new RegisteredCapability
            {
                CapabilityId = $"{Id}.optimize",
                DisplayName = $"{Name} - Optimize",
                Description = "Optimize energy consumption and reduce carbon footprint",
                Category = CapabilityCategory.Custom,
                SubCategory = "Sustainability",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[]
                {
                    "sustainability",
                    "optimization",
                    "green-computing"
                }
            }
        };
        foreach (var kvp in _strategies)
        {
            var strategy = kvp.Value;
            capabilities.Add(new RegisteredCapability { CapabilityId = $"{Id}.strategy.{strategy.StrategyId}", DisplayName = strategy.DisplayName, Description = strategy.SemanticDescription, Category = CapabilityCategory.Custom, SubCategory = strategy.Category.ToString(), PluginId = Id, PluginName = Name, PluginVersion = Version, Tags = strategy.Tags, Metadata = new Dictionary<string, object> { ["strategyId"] = strategy.StrategyId, ["category"] = strategy.Category.ToString(), ["capabilities"] = strategy.Capabilities.ToString() }, SemanticDescription = strategy.SemanticDescription });
        }

        var built = capabilities.AsReadOnly();
        // Store in cache field (volatile write ensures visibility to other threads).
        _cachedCapabilities = built;
        return built;
    }
}
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge();
    public override Task OnMessageAsync(PluginMessage message);
    public string SemanticDescription;;
    public string[] SemanticTags;;
    protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct);
    protected override Task OnStartCoreAsync(CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/SustainabilityStrategyBase.cs
```csharp
public sealed class SustainabilityStatistics
{
}
    public double TotalEnergySavedWh { get; set; }
    public double TotalCarbonAvoidedGrams { get; set; }
    public long TotalOptimizationActions { get; set; }
    public long TotalSamplesCollected { get; set; }
    public long TotalWorkloadsScheduled { get; set; }
    public double TotalCostSavingsUsd { get; set; }
    public double AveragePowerWatts { get; set; }
    public double PeakPowerWatts { get; set; }
    public double CurrentCarbonIntensity { get; set; }
    public double TotalLowPowerTimeSeconds { get; set; }
    public long ThermalThrottlingEvents { get; set; }
    public double AverageCarbonIntensity { get; set; }
}
```
```csharp
public sealed record EnergyReading
{
}
    public required DateTimeOffset Timestamp { get; init; }
    public required double PowerWatts { get; init; }
    public double EnergyWh { get; init; }
    public double CarbonIntensity { get; init; }
    public string Source { get; init; };
    public string Component { get; init; };
}
```
```csharp
public sealed record CarbonEmission
{
}
    public required DateTimeOffset Timestamp { get; init; }
    public required double EmissionsGrams { get; init; }
    public double EnergyConsumedWh { get; init; }
    public double CarbonIntensity { get; init; }
    public string Region { get; init; };
    public int Scope { get; init; };
}
```
```csharp
public sealed record SustainabilityRecommendation
{
}
    public required string RecommendationId { get; init; }
    public required string Type { get; init; }
    public int Priority { get; init; };
    public required string Description { get; init; }
    public double EstimatedEnergySavingsWh { get; init; }
    public double EstimatedCarbonReductionGrams { get; init; }
    public double EstimatedCostSavingsUsd { get; init; }
    public int ImplementationDifficulty { get; init; };
    public bool CanAutoApply { get; init; }
    public string? Action { get; init; }
    public Dictionary<string, object>? ActionParameters { get; init; }
}
```
```csharp
public interface ISustainabilityStrategy
{
}
    string StrategyId { get; }
    string DisplayName { get; }
    SustainabilityCategory Category { get; }
    SustainabilityCapabilities Capabilities { get; }
    string SemanticDescription { get; }
    string[] Tags { get; }
    SustainabilityStatistics GetStatistics();;
    void ResetStatistics();;
    Task InitializeAsync(CancellationToken ct = default);;
    Task DisposeAsync();;
    Task<IReadOnlyList<SustainabilityRecommendation>> GetRecommendationsAsync(CancellationToken ct = default);;
    Task<bool> ApplyRecommendationAsync(string recommendationId, CancellationToken ct = default);;
}
```
```csharp
public abstract class SustainabilityStrategyBase : StrategyBase, ISustainabilityStrategy
{
#endregion
}
    public abstract override string StrategyId { get; }
    public abstract string DisplayName { get; }
    public override string Name;;
    public abstract SustainabilityCategory Category { get; }
    public abstract SustainabilityCapabilities Capabilities { get; }
    public abstract string SemanticDescription { get; }
    public abstract string[] Tags { get; }
    protected override async Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public SustainabilityStatistics GetStatistics();
    public void ResetStatistics();
    public virtual Task<IReadOnlyList<SustainabilityRecommendation>> GetRecommendationsAsync(CancellationToken ct = default);
    public virtual Task<bool> ApplyRecommendationAsync(string recommendationId, CancellationToken ct = default);
    protected virtual Task InitializeCoreAsync(CancellationToken ct);;
    protected virtual Task DisposeCoreAsync();;
    protected void AddRecommendation(SustainabilityRecommendation recommendation);
    protected bool RemoveRecommendation(string recommendationId);
    protected void ClearRecommendations();
    protected void RecordEnergySaved(double wattHours, double costSavingsUsd = 0);
    protected void RecordCarbonAvoided(double grams);
    protected void RecordOptimizationAction();
    protected void RecordSample(double powerWatts, double carbonIntensity);
    protected void RecordWorkloadScheduled();
    protected void RecordLowPowerTime(double seconds);
    protected void RecordThermalThrottling();
    public virtual KnowledgeObject GetStrategyKnowledge();
    public virtual RegisteredCapability GetStrategyCapability();
    protected virtual Dictionary<string, object> GetKnowledgePayload();
    protected virtual Dictionary<string, object> GetCapabilityMetadata();
    protected virtual string[] GetKnowledgeTags();
}
```
```csharp
public sealed class SustainabilityStrategyRegistry
{
}
    public void Register(ISustainabilityStrategy strategy);
    public bool Unregister(string strategyId);
    public ISustainabilityStrategy? Get(string strategyId);
    public IReadOnlyCollection<ISustainabilityStrategy> GetAll();
    public IReadOnlyCollection<ISustainabilityStrategy> GetByCategory(SustainabilityCategory category);
    public int Count;;
    public int AutoDiscover(params System.Reflection.Assembly[] assemblies);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/SustainabilityRenewableRoutingStrategy.cs
```csharp
public sealed class RenewableRoutingStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public RenewableRoutingStrategy();
    public void Configure(string electricityMapsApiToken);
    public async Task<RenewableRegionData?> FetchRegionDataAsync(string zone, CancellationToken ct = default);
    public async Task<RenewableRoutingRecommendation> RecommendRegionAsync(string workloadId, string[] candidateZones, double estimatedKwh, double minRenewablePercentage = 0, CancellationToken ct = default);
    public void UpdateRegionData(string zone, double renewablePercentage, double fossilFreePercentage, double carbonIntensity);
    public IReadOnlyList<RenewableRegionData> GetAllRegions();;
}
```
```csharp
public sealed record RenewableRegionData
{
}
    public required string Zone { get; init; }
    public double RenewablePercentage { get; init; }
    public double FossilFreePercentage { get; init; }
    public double CarbonIntensityGCo2PerKwh { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public required string Source { get; init; }
}
```
```csharp
public sealed record RenewableRoutingRecommendation
{
}
    public required string WorkloadId { get; init; }
    public bool HasRecommendation { get; init; }
    public string? RecommendedZone { get; init; }
    public double RenewablePercentage { get; init; }
    public double FossilFreePercentage { get; init; }
    public double CarbonIntensity { get; init; }
    public double EstimatedRenewableKwh { get; init; }
    public string? Reason { get; init; }
    public List<RenewableRanking> Rankings { get; init; };
}
```
```csharp
public sealed record RenewableRanking
{
}
    public int Rank { get; init; }
    public required string Zone { get; init; }
    public double RenewablePercentage { get; init; }
    public double CarbonIntensity { get; init; }
}
```
```csharp
public sealed record RoutingDecision
{
}
    public required string WorkloadId { get; init; }
    public required string SelectedZone { get; init; }
    public double RenewablePercentage { get; init; }
    public double EstimatedKwh { get; init; }
    public double EstimatedRenewableKwh { get; init; }
    public DateTimeOffset DecidedAt { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/SustainabilityEnhancedStrategies.cs
```csharp
public sealed class CarbonAwareSchedulingStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public void UpdateCarbonIntensity(string region, double gCo2PerKwh, string gridOperator, DateTimeOffset validUntil);
    public CarbonSchedulingRecommendation RecommendRegion(string workloadId, string[] candidateRegions, double estimatedKwh, bool preferRenewable = true);
    public SchedulingCarbonBudget InitializeBudget(string budgetId, double totalBudgetGrams, TimeSpan period);
    public CarbonBudgetResult ConsumeBudget(string budgetId, double emissionsGrams);
    public IReadOnlyList<CarbonIntensityData> GetCurrentIntensity();;
}
```
```csharp
public sealed class EnergyTrackingStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public void RecordConsumption(string operationId, string operationType, double kwhConsumed, string region, double? carbonIntensity = null);
    public GhgReport GenerateGhgReport(DateTimeOffset from, DateTimeOffset to);
    public EnergySummary GetSummary();
}
```
```csharp
public sealed record CarbonIntensityData
{
}
    public required string Region { get; init; }
    public double GCo2PerKwh { get; init; }
    public required string GridOperator { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset ValidUntil { get; init; }
}
```
```csharp
public sealed record CarbonSchedulingRecommendation
{
}
    public required string WorkloadId { get; init; }
    public bool HasRecommendation { get; init; }
    public string? RecommendedRegion { get; init; }
    public double CarbonIntensity { get; init; }
    public double EstimatedEmissionsGrams { get; init; }
    public double CarbonSavingsGrams { get; init; }
    public List<RegionCarbonRanking> RegionRankings { get; init; };
}
```
```csharp
public sealed record RegionCarbonRanking
{
}
    public int Rank { get; init; }
    public required string Region { get; init; }
    public double GCo2PerKwh { get; init; }
    public double EstimatedEmissionsGrams { get; init; }
}
```
```csharp
public sealed record CarbonSchedulingDecision
{
}
    public required string WorkloadId { get; init; }
    public required string SelectedRegion { get; init; }
    public double CarbonIntensity { get; init; }
    public double EstimatedEmissionsGrams { get; init; }
    public double AvoidedEmissionsGrams { get; init; }
    public DateTimeOffset DecidedAt { get; init; }
}
```
```csharp
public sealed record SchedulingCarbonBudget
{
}
    public required string BudgetId { get; init; }
    public double TotalBudgetGrams { get; init; }
    public double ConsumedGrams { get; init; }
    public double RemainingGrams { get; init; }
    public TimeSpan Period { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
}
```
```csharp
public sealed record CarbonBudgetResult
{
}
    public bool Allowed { get; init; }
    public string? Reason { get; init; }
    public double ConsumedGrams { get; init; }
    public double RemainingGrams { get; init; }
    public double BudgetUtilization { get; init; }
}
```
```csharp
public sealed record TrackingEnergyMeasurement
{
}
    public required string OperationId { get; init; }
    public required string OperationType { get; init; }
    public double KwhConsumed { get; init; }
    public required string Region { get; init; }
    public double CarbonIntensityGCo2PerKwh { get; init; }
    public double EmissionsGCo2 { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}
```
```csharp
public sealed record GhgReport
{
}
    public DateTimeOffset ReportPeriodFrom { get; init; }
    public DateTimeOffset ReportPeriodTo { get; init; }
    public double TotalEnergyKwh { get; init; }
    public double Scope1EmissionsGCo2 { get; init; }
    public double Scope2EmissionsGCo2 { get; init; }
    public double Scope3EmissionsGCo2 { get; init; }
    public double TotalEmissionsKgCo2 { get; init; }
    public List<OperationEnergyBreakdown> ByOperationType { get; init; };
    public List<RegionEnergyBreakdown> ByRegion { get; init; };
    public int MeasurementCount { get; init; }
    public DateTimeOffset GeneratedAt { get; init; }
}
```
```csharp
public sealed record OperationEnergyBreakdown
{
}
    public required string OperationType { get; init; }
    public double TotalKwh { get; init; }
    public double TotalEmissionsGCo2 { get; init; }
    public int OperationCount { get; init; }
}
```
```csharp
public sealed record RegionEnergyBreakdown
{
}
    public required string Region { get; init; }
    public double TotalKwh { get; init; }
    public double AverageCarbonIntensity { get; init; }
    public double TotalEmissionsGCo2 { get; init; }
}
```
```csharp
public sealed record EnergySummary
{
}
    public int TotalOperations { get; init; }
    public double TotalKwhConsumed { get; init; }
    public double TotalEmissionsGCo2 { get; init; }
    public int UniqueOperationTypes { get; init; }
    public int UniqueRegions { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/EnergyMeasurement/CloudProviderEnergyStrategy.cs
```csharp
public sealed class CloudProviderEnergyStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public CloudProvider DetectedProvider;;
    public static bool IsAvailable();
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override Task DisposeCoreAsync();
    public async Task<CarbonEnergyMeasurement> MeasureOperationAsync(string operationId, string operationType, long dataSizeBytes, Func<Task> operation, string? tenantId = null, CancellationToken ct = default);
    public async Task<double> GetCurrentPowerDrawWattsAsync(CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/EnergyMeasurement/RaplEnergyMeasurementStrategy.cs
```csharp
public sealed class RaplEnergyMeasurementStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public static bool IsAvailable();
    protected override Task InitializeCoreAsync(CancellationToken ct);
    public async Task<CarbonEnergyMeasurement> MeasureOperationAsync(string operationId, string operationType, long dataSizeBytes, Func<Task> operation, string? tenantId = null, CancellationToken ct = default);
    public async Task<double> GetCurrentPowerDrawWattsAsync(CancellationToken ct = default);
    public IReadOnlyDictionary<string, double> GetDomainReadings();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/EnergyMeasurement/EnergyMeasurementService.cs
```csharp
public sealed class EnergyMeasurementService : IEnergyMeasurementService
{
}
    public EnergySource ActiveSource;;
    public int MeasurementCount;;
    public EnergyMeasurementService(IMessageBus? messageBus = null);
    public async Task InitializeAsync(CancellationToken ct = default);
    public async Task<CarbonEnergyMeasurement> MeasureOperationAsync(string operationId, string operationType, long dataSizeBytes, CancellationToken ct = default);
    public async Task<CarbonEnergyMeasurement> MeasureOperationAsync(string operationId, string operationType, long dataSizeBytes, Func<Task> operation, string? tenantId = null, CancellationToken ct = default);
    public Task<double> GetWattsPerOperationAsync(string operationType, CancellationToken ct = default);
    public Task<IReadOnlyList<CarbonEnergyMeasurement>> GetMeasurementsAsync(DateTimeOffset from, DateTimeOffset to, string? tenantId = null, CancellationToken ct = default);
    public async Task<double> GetCurrentPowerDrawWattsAsync(CancellationToken ct = default);
    public async Task DisposeAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/EnergyMeasurement/EstimationEnergyStrategy.cs
```csharp
public sealed class EstimationEnergyStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public static bool IsAvailable();;
    public double BaseTdpWatts;;
    protected override Task InitializeCoreAsync(CancellationToken ct);
    public async Task<CarbonEnergyMeasurement> MeasureOperationAsync(string operationId, string operationType, long dataSizeBytes, Func<Task> operation, string? tenantId = null, CancellationToken ct = default);
    public Task<double> GetCurrentPowerDrawWattsAsync(CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/EnergyMeasurement/PowercapEnergyMeasurementStrategy.cs
```csharp
public sealed class PowercapEnergyMeasurementStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public static bool IsAvailable();
    protected override Task InitializeCoreAsync(CancellationToken ct);
    public async Task<CarbonEnergyMeasurement> MeasureOperationAsync(string operationId, string operationType, long dataSizeBytes, Func<Task> operation, string? tenantId = null, CancellationToken ct = default);
    public async Task<double> GetCurrentPowerDrawWattsAsync(CancellationToken ct = default);
    public IReadOnlyDictionary<string, string> GetDiscoveredZones();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/CarbonBudget/CarbonBudgetEnforcementStrategy.cs
```csharp
public sealed class CarbonBudgetEnforcementStrategy : SustainabilityStrategyBase, ICarbonBudgetService
{
#endregion
}
    public double DefaultThrottleThresholdPercent { get; set; };
    public double DefaultHardLimitPercent { get; set; };
    public int MinDelayMs { get; set; };
    public int MaxDelayMs { get; set; };
    public double DefaultCarbonIntensityGCO2ePerKwh { get; set; };
    public int ThrottleCountForRecommendation { get; set; };
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public CarbonBudgetEnforcementStrategy(CarbonBudgetStore store);
    public CarbonBudgetStore Store;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task DisposeCoreAsync();
    public async Task<SDK.Contracts.Carbon.CarbonBudget> GetBudgetAsync(string tenantId, CancellationToken ct = default);
    public async Task SetBudgetAsync(string tenantId, double budgetGramsCO2e, CarbonBudgetPeriod period, CancellationToken ct = default);
    public async Task<bool> CanProceedAsync(string tenantId, double estimatedCarbonGramsCO2e, CancellationToken ct = default);
    public async Task RecordUsageAsync(string tenantId, double carbonGramsCO2e, string operationType, CancellationToken ct = default);
    public async Task<CarbonThrottleDecision> EvaluateThrottleAsync(string tenantId, CancellationToken ct = default);
    internal void TrackThrottleEvent(string tenantId);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/CarbonBudget/CarbonThrottlingStrategy.cs
```csharp
public sealed class CarbonThrottlingStrategy : SustainabilityStrategyBase
{
#endregion
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public CarbonThrottlingStrategy(CarbonBudgetEnforcementStrategy enforcement);
    public async Task<ThrottleResult> ThrottleIfNeededAsync(string tenantId, CancellationToken ct = default);
    public ThrottleStats? GetThrottleStats(string tenantId);
    public IReadOnlyDictionary<string, ThrottleStats> GetAllThrottleStats();
}
```
```csharp
public sealed class ThrottleStats
{
}
    public long TotalEvaluations { get; set; }
    public long AllowedCount { get; set; }
    public long DelayedCount { get; set; }
    public long RejectedCount { get; set; }
    public DateTimeOffset LastEvaluated { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/CarbonBudget/CarbonBudgetStore.cs
```csharp
public sealed class CarbonBudgetStore : IDisposable, IAsyncDisposable
{
#endregion
}
    public CarbonBudgetStore(string? dataDirectory = null);
    public int Count;;
    public Task<SDK.Contracts.Carbon.CarbonBudget?> GetAsync(string tenantId);
    public Task SetAsync(string tenantId, SDK.Contracts.Carbon.CarbonBudget budget);
    public Task<bool> RecordUsageAsync(string tenantId, double carbonGramsCO2e);
    public Task ResetExpiredBudgetsAsync();
    public async Task LoadAsync(CancellationToken ct = default);
    public async Task SaveAsync(CancellationToken ct = default);
    public IReadOnlyList<string> GetAllTenantIds();
    public double GetUsagePercent(string tenantId);
    public static DateTimeOffset ComputePeriodEnd(DateTimeOffset periodStart, CarbonBudgetPeriod period);
    public async ValueTask DisposeAsync();
    public void Dispose();
}
```
```csharp
private sealed class MutableBudgetEntry
{
}
    public readonly object Lock = new();
    public string TenantId { get; set; };
    public CarbonBudgetPeriod BudgetPeriod { get; set; }
    public double BudgetGramsCO2e { get; set; }
    public double UsedGramsCO2e { get; set; }
    public double ThrottleThresholdPercent { get; set; };
    public double HardLimitPercent { get; set; };
    public DateTimeOffset PeriodStart { get; set; }
    public DateTimeOffset PeriodEnd { get; set; }
    public SDK.Contracts.Carbon.CarbonBudget ToImmutable();
    public StoredBudgetEntry ToStored();
    public static MutableBudgetEntry FromImmutable(SDK.Contracts.Carbon.CarbonBudget budget);
    public static MutableBudgetEntry FromStored(StoredBudgetEntry stored);
}
```
```csharp
private sealed class StoredBudgetEntry
{
}
    public string TenantId { get; set; };
    public CarbonBudgetPeriod BudgetPeriod { get; set; }
    public double BudgetGramsCO2e { get; set; }
    public double UsedGramsCO2e { get; set; }
    public double ThrottleThresholdPercent { get; set; };
    public double HardLimitPercent { get; set; };
    public DateTimeOffset PeriodStart { get; set; }
    public DateTimeOffset PeriodEnd { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/GreenPlacement/ElectricityMapsApiStrategy.cs
```csharp
public sealed class ElectricityMapsApiStrategy : SustainabilityStrategyBase
{
#endregion
}
    public string? ApiKey { get; set; }
    public string ApiEndpoint { get; set; };
    public int CacheTtlSeconds { get; set; };
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public ElectricityMapsApiStrategy(HttpClient httpClient);
    public ElectricityMapsApiStrategy() : this(new HttpClient());
    public bool IsAvailable();
    public async Task<GridCarbonData> GetGridCarbonDataAsync(string region, CancellationToken ct = default);
    public async Task<GridCarbonData> GetForecastAsync(string region, CancellationToken ct = default);
    public static string ResolveZone(string region);
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task DisposeCoreAsync();
}
```
```csharp
private sealed class EmCarbonIntensityResponse
{
}
    [JsonPropertyName("zone")]
public string? Zone { get; set; }
    [JsonPropertyName("carbonIntensity")]
public double? CarbonIntensity { get; set; }
    [JsonPropertyName("datetime")]
public DateTimeOffset? Datetime { get; set; }
    [JsonPropertyName("updatedAt")]
public DateTimeOffset? UpdatedAt { get; set; }
    [JsonPropertyName("emissionFactorType")]
public string? EmissionFactorType { get; set; }
    [JsonPropertyName("isEstimated")]
public bool? IsEstimated { get; set; }
    [JsonPropertyName("estimationMethod")]
public string? EstimationMethod { get; set; }
}
```
```csharp
private sealed class EmPowerBreakdownResponse
{
}
    [JsonPropertyName("zone")]
public string? Zone { get; set; }
    [JsonPropertyName("datetime")]
public DateTimeOffset? Datetime { get; set; }
    [JsonPropertyName("renewablePercentage")]
public double? RenewablePercentage { get; set; }
    [JsonPropertyName("fossilFreePercentage")]
public double? FossilFreePercentage { get; set; }
    [JsonPropertyName("powerConsumptionTotal")]
public double? PowerConsumptionTotal { get; set; }
    [JsonPropertyName("powerProductionTotal")]
public double? PowerProductionTotal { get; set; }
    [JsonPropertyName("powerImportTotal")]
public double? PowerImportTotal { get; set; }
    [JsonPropertyName("powerExportTotal")]
public double? PowerExportTotal { get; set; }
    [JsonPropertyName("isEstimated")]
public bool? IsEstimated { get; set; }
}
```
```csharp
private sealed class EmForecastResponse
{
}
    [JsonPropertyName("zone")]
public string? Zone { get; set; }
    [JsonPropertyName("forecast")]
public List<EmForecastPoint>? Forecast { get; set; }
    [JsonPropertyName("updatedAt")]
public DateTimeOffset? UpdatedAt { get; set; }
}
```
```csharp
private sealed class EmForecastPoint
{
}
    [JsonPropertyName("carbonIntensity")]
public double? CarbonIntensity { get; set; }
    [JsonPropertyName("datetime")]
public DateTimeOffset? Datetime { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/GreenPlacement/BackendGreenScoreRegistry.cs
```csharp
public sealed class BackendGreenScoreRegistry
{
}
    public BackendGreenScoreRegistry(string? persistencePath = null);
    public async Task InitializeAsync(CancellationToken ct = default);
    public void RegisterBackend(string backendId, string region, double renewablePct, double pue, double? wue, double carbonIntensity = 475.0);
    public void UpdateCarbonIntensity(string region, double intensity);
    public GreenScore? GetScore(string backendId);
    public IReadOnlyList<GreenScore> GetAllScores();
    public string? GetBestBackend(IReadOnlyList<string> candidates);
    public string? GetBackendRegion(string backendId);
    public int Count;;
    public async Task PersistAsync(CancellationToken ct = default);
}
```
```csharp
private sealed class GreenScorePersisted
{
}
    public string BackendId { get; set; };
    public string Region { get; set; };
    public double RenewablePercentage { get; set; }
    public double CarbonIntensityGCO2ePerKwh { get; set; }
    public double PowerUsageEffectiveness { get; set; }
    public double? WaterUsageEffectiveness { get; set; }
    public double Score { get; set; }
    public DateTimeOffset LastUpdated { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/GreenPlacement/WattTimeGridApiStrategy.cs
```csharp
public sealed class WattTimeGridApiStrategy : SustainabilityStrategyBase
{
#endregion
}
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string ApiEndpoint { get; set; };
    public int CacheTtlSeconds { get; set; };
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public WattTimeGridApiStrategy(HttpClient httpClient);
    public WattTimeGridApiStrategy() : this(new HttpClient());
    public bool IsAvailable();
    public async Task<GridCarbonData> GetGridCarbonDataAsync(string region, CancellationToken ct = default);
    public async Task<GridCarbonData> GetForecastAsync(string region, CancellationToken ct = default);
    public static string ResolveBalancingAuthority(string region);
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task DisposeCoreAsync();
}
```
```csharp
private sealed class WattTimeLoginResponse
{
}
    [JsonPropertyName("token")]
public string? Token { get; set; }
}
```
```csharp
private sealed class WattTimeSignalResponse
{
}
    [JsonPropertyName("data")]
public List<WattTimeDataPoint>? Data { get; set; }
    [JsonPropertyName("meta")]
public WattTimeMetadata? Meta { get; set; }
}
```
```csharp
private sealed class WattTimeDataPoint
{
}
    [JsonPropertyName("point_time")]
public DateTimeOffset? PointTime { get; set; }
    [JsonPropertyName("value")]
public double Value { get; set; }
    [JsonPropertyName("frequency")]
public int? Frequency { get; set; }
    [JsonPropertyName("market")]
public string? Market { get; set; }
}
```
```csharp
private sealed class WattTimeMetadata
{
}
    [JsonPropertyName("region")]
public string? Region { get; set; }
    [JsonPropertyName("signal_type")]
public string? SignalType { get; set; }
    [JsonPropertyName("model")]
public WattTimeModelInfo? Model { get; set; }
}
```
```csharp
private sealed class WattTimeModelInfo
{
}
    [JsonPropertyName("type")]
public string? Type { get; set; }
    [JsonPropertyName("date")]
public string? Date { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/GreenPlacement/GreenPlacementService.cs
```csharp
public sealed class GreenPlacementService : SustainabilityStrategyBase, IGreenPlacementService
{
#endregion
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public GreenPlacementService(WattTimeGridApiStrategy wattTimeStrategy, ElectricityMapsApiStrategy electricityMapsStrategy, BackendGreenScoreRegistry registry);
    public GreenPlacementService() : this(new WattTimeGridApiStrategy(), new ElectricityMapsApiStrategy(), new BackendGreenScoreRegistry());
    public override void ConfigureIntelligence(IMessageBus? messageBus);
    public async Task<CarbonPlacementDecision> SelectGreenestBackendAsync(IReadOnlyList<string> candidateBackendIds, long dataSizeBytes, CancellationToken ct = default);
    public Task<IReadOnlyList<GreenScore>> GetGreenScoresAsync(CancellationToken ct = default);
    public async Task<GridCarbonData> GetGridCarbonDataAsync(string region, CancellationToken ct = default);
    public async Task RefreshGridDataAsync(CancellationToken ct = default);
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task DisposeCoreAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/CarbonReporting/CarbonReportingService.cs
```csharp
public sealed class CarbonReportingService : SustainabilityStrategyBase, ICarbonReportingService
{
#endregion
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public CarbonReportingService(GhgProtocolReportingStrategy ghgStrategy, CarbonDashboardDataStrategy dashboardStrategy);
    public CarbonReportingService() : this(new GhgProtocolReportingStrategy(), new CarbonDashboardDataStrategy());
    public GhgProtocolReportingStrategy GhgStrategy;;
    public CarbonDashboardDataStrategy DashboardStrategy;;
    public override void ConfigureIntelligence(IMessageBus? messageBus);
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task DisposeCoreAsync();
    public async Task<IReadOnlyList<GhgReportEntry>> GenerateGhgReportAsync(DateTimeOffset from, DateTimeOffset to, string? tenantId = null, CancellationToken ct = default);
    public async Task<double> GetTotalEmissionsAsync(GhgScopeCategory scope, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
    public async Task<IReadOnlyDictionary<string, double>> GetEmissionsByRegionAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
    public async Task<CarbonSummary> GetCarbonSummaryAsync(string? tenantId = null, CancellationToken ct = default);
    public Task<GhgFullReport> GenerateFullReportAsync(DateTimeOffset from, DateTimeOffset to, string organizationName, string? tenantId = null, CancellationToken ct = default);
    public IReadOnlyList<TimeSeriesPoint> GetCarbonIntensityTimeSeries(string region, TimeSpan period, TimeSpan granularity);
    public IReadOnlyList<TimeSeriesPoint> GetBudgetUtilizationTimeSeries(string tenantId, TimeSpan period);
    public IReadOnlyList<TimeSeriesPoint> GetGreenScoreTrend(TimeSpan period);
    public EmissionsByOperationType GetEmissionsByOperationType(DateTimeOffset from, DateTimeOffset to);
    public IReadOnlyList<TenantCarbonUsage> GetTopEmittingTenants(int topN, DateTimeOffset from, DateTimeOffset to);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/CarbonReporting/GhgProtocolReportingStrategy.cs
```csharp
public sealed record GhgFullReport
{
}
    public required string ReportId { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
    public required string OrganizationName { get; init; }
    public required DateTimeOffset PeriodStart { get; init; }
    public required DateTimeOffset PeriodEnd { get; init; }
    public required IReadOnlyList<GhgReportEntry> Entries { get; init; }
    public required double TotalScope2 { get; init; }
    public required double TotalScope3 { get; init; }
    public double TotalEmissions;;
    public required string Methodology { get; init; }
    public string? ExecutiveSummary { get; init; }
    public string? TenantId { get; init; }
}
```
```csharp
public sealed class GhgProtocolReportingStrategy : SustainabilityStrategyBase
{
#endregion
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task DisposeCoreAsync();
    public Task<IReadOnlyList<GhgReportEntry>> GenerateScope2ReportAsync(DateTimeOffset from, DateTimeOffset to, string? tenantId = null, CancellationToken ct = default);
    public Task<IReadOnlyList<GhgReportEntry>> GenerateScope3ReportAsync(DateTimeOffset from, DateTimeOffset to, string? tenantId = null, CancellationToken ct = default);
    public async Task<GhgFullReport> GenerateFullReportAsync(DateTimeOffset from, DateTimeOffset to, string organizationName, string? tenantId = null, CancellationToken ct = default);
    public void RecordMeasurement(EnergyMeasurementRecord record);
    public void UpdateRegionCarbonIntensity(string region, double intensityGCO2ePerKwh);
    public int MeasurementCount;;
}
```
```csharp
public sealed record EnergyMeasurementRecord
{
}
    public required DateTimeOffset Timestamp { get; init; }
    public required double WattsConsumed { get; init; }
    public required double DurationMs { get; init; }
    public required double EnergyWh { get; init; }
    public required EnergySource Source { get; init; }
    public required string OperationType { get; init; }
    public string? TenantId { get; init; }
    public string? Region { get; init; }
    public long DataSizeBytes { get; init; }
    public double CarbonIntensity { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/CarbonReporting/CarbonDashboardDataStrategy.cs
```csharp
public sealed record TimeSeriesPoint
{
}
    public required DateTimeOffset Timestamp { get; init; }
    public required double Value { get; init; }
    public required string Unit { get; init; }
}
```
```csharp
public sealed record EmissionsByOperationType
{
}
    public double ReadGramsCO2e { get; init; }
    public double WriteGramsCO2e { get; init; }
    public double DeleteGramsCO2e { get; init; }
    public double ListGramsCO2e { get; init; }
    public double TotalGramsCO2e;;
}
```
```csharp
public sealed record TenantCarbonUsage
{
}
    public required string TenantId { get; init; }
    public required double TotalEmissionsGramsCO2e { get; init; }
    public required double TotalEnergyWh { get; init; }
    public required long OperationCount { get; init; }
}
```
```csharp
public sealed class CarbonDashboardDataStrategy : SustainabilityStrategyBase
{
#endregion
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task DisposeCoreAsync();
    public IReadOnlyList<TimeSeriesPoint> GetCarbonIntensityTimeSeries(string region, TimeSpan period, TimeSpan granularity);
    public IReadOnlyList<TimeSeriesPoint> GetBudgetUtilizationTimeSeries(string tenantId, TimeSpan period);
    public IReadOnlyList<TimeSeriesPoint> GetGreenScoreTrend(TimeSpan period);
    public EmissionsByOperationType GetEmissionsByOperationType(DateTimeOffset from, DateTimeOffset to);
    public IReadOnlyList<TenantCarbonUsage> GetTopEmittingTenants(int topN, DateTimeOffset from, DateTimeOffset to);
    public void RecordCarbonIntensity(string region, double intensityGCO2ePerKwh);
    public void RecordBudgetUtilization(string tenantId, double usagePercent);
    public void RecordGreenScore(double averageScore);
    public void RecordOperationEmission(string operationType, double emissionsGramsCO2e);
    public void RecordTenantEmission(string tenantId, double emissionsGramsCO2e, double energyWh);
}
```
```csharp
private sealed class TenantAccumulator
{
}
    public TenantAccumulator(string tenantId);
    public void Add(double emissionsGrams, double energyWh);
    public TenantCarbonUsage ToUsage();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/CloudOptimization/ReservedCapacityOptimizationStrategy.cs
```csharp
public sealed class ReservedCapacityOptimizationStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public double MinCoveragePercent { get; set; };
    public int MaxCommitmentMonths { get; set; };
    public void RegisterReservation(string reservationId, string instanceType, string region, int quantity, DateTimeOffset startDate, int termMonths, double monthlyCost, double savingsPercent);
    public void RecordUsage(string instanceType, string region, int onDemandCount, int reservedCount);
    public CoverageAnalysis GetCoverageAnalysis(TimeSpan? period = null);
    public IReadOnlyList<ReservationRecommendation> GetPurchaseRecommendations();
    public IReadOnlyList<ReservedCapacity> GetExpiringReservations(int withinDays = 30);
}
```
```csharp
public sealed record ReservedCapacity
{
}
    public required string ReservationId { get; init; }
    public required string InstanceType { get; init; }
    public required string Region { get; init; }
    public required int Quantity { get; init; }
    public required DateTimeOffset StartDate { get; init; }
    public required DateTimeOffset EndDate { get; init; }
    public required int TermMonths { get; init; }
    public required double MonthlyCostUsd { get; init; }
    public required double SavingsPercent { get; init; }
}
```
```csharp
public sealed record UsageRecord
{
}
    public required DateTimeOffset Timestamp { get; init; }
    public required string InstanceType { get; init; }
    public required string Region { get; init; }
    public required int OnDemandCount { get; init; }
    public required int ReservedCount { get; init; }
    public required int TotalCount { get; init; }
    public required double CoveragePercent { get; init; }
}
```
```csharp
public sealed record CoverageAnalysis
{
}
    public bool HasData { get; init; }
    public double OverallCoverage { get; init; }
    public long TotalOnDemandHours { get; init; }
    public long TotalReservedHours { get; init; }
    public List<TypeCoverage> ByInstanceType { get; init; };
}
```
```csharp
public sealed record TypeCoverage
{
}
    public required string InstanceType { get; init; }
    public required string Region { get; init; }
    public required double AvgCoverage { get; init; }
    public required int MaxOnDemand { get; init; }
    public required double AvgTotal { get; init; }
}
```
```csharp
public sealed record ReservationRecommendation
{
}
    public required string InstanceType { get; init; }
    public required string Region { get; init; }
    public required int RecommendedQuantity { get; init; }
    public required double CurrentCoverage { get; init; }
    public required double TargetCoverage { get; init; }
    public required double EstimatedMonthlySavingsUsd { get; init; }
    public required int RecommendedTerm { get; init; }
    public required string Reason { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/CloudOptimization/ContainerDensityStrategy.cs
```csharp
public sealed class ContainerDensityStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public double TargetNodeCpuPercent { get; set; };
    public double TargetNodeMemoryPercent { get; set; };
    public double NodeIdlePowerWatts { get; set; };
    public double NodeMaxPowerWatts { get; set; };
    public void RegisterNode(string nodeId, string name, int cpuMillicores, long memoryBytes);
    public void UpdateNodeUtilization(string nodeId, int usedCpuMillicores, long usedMemoryBytes, int podCount);
    public void RegisterPod(string podId, string name, string nodeId, int cpuRequest, int cpuLimit, long memoryRequest, long memoryLimit);
    public void UpdatePodUtilization(string podId, int usedCpuMillicores, long usedMemoryBytes);
    public ClusterDensityMetrics GetClusterMetrics();
    public IReadOnlyList<ResourceRecommendation> GetResourceRecommendations();
}
```
```csharp
public sealed class K8sNode
{
}
    public required string NodeId { get; init; }
    public required string Name { get; init; }
    public required int CpuMillicores { get; init; }
    public required long MemoryBytes { get; init; }
    public int UsedCpuMillicores { get; set; }
    public long UsedMemoryBytes { get; set; }
    public int PodCount { get; set; }
    public double CpuUtilization { get; set; }
    public double MemoryUtilization { get; set; }
    public double EstimatedPowerWatts { get; set; }
}
```
```csharp
public sealed class K8sPod
{
}
    public required string PodId { get; init; }
    public required string Name { get; init; }
    public required string NodeId { get; init; }
    public required int CpuRequestMillicores { get; init; }
    public required int CpuLimitMillicores { get; init; }
    public required long MemoryRequestBytes { get; init; }
    public required long MemoryLimitBytes { get; init; }
    public int UsedCpuMillicores { get; set; }
    public long UsedMemoryBytes { get; set; }
}
```
```csharp
public sealed record ClusterDensityMetrics
{
}
    public int TotalNodes { get; init; }
    public int ActiveNodes { get; init; }
    public int UnderutilizedNodes { get; init; }
    public int TotalPods { get; init; }
    public double AvgCpuUtilization { get; init; }
    public double AvgMemoryUtilization { get; init; }
    public double TotalPowerWatts { get; init; }
    public int PotentialNodeReduction { get; init; }
    public double PotentialPowerSavingsWatts { get; init; }
}
```
```csharp
public sealed record ResourceRecommendation
{
}
    public required string PodId { get; init; }
    public required string PodName { get; init; }
    public required string ResourceType { get; init; }
    public required long CurrentRequest { get; init; }
    public required long RecommendedRequest { get; init; }
    public required double UtilizationPercent { get; init; }
    public required string Reason { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/CloudOptimization/SpotInstanceStrategy.cs
```csharp
public sealed class SpotInstanceStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public int MaxSpotPricePercent { get; set; };
    public bool EnableCheckpointing { get; set; };
    public IReadOnlyDictionary<string, SpotInstance> Instances;;
    public double EstimatedCostSavingsUsd;;
    public SpotInstance RequestSpotInstance(string instanceType, double maxBidUsd, string region, string provider = "AWS");
    public void AllocateInstance(string instanceId);
    public void HandleInterruption(string instanceId, string reason);
    public void TerminateInstance(string instanceId);
}
```
```csharp
public sealed class SpotInstance
{
}
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
```
```csharp
public sealed record InterruptionEvent
{
}
    public required string InstanceId { get; init; }
    public required string Reason { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/CloudOptimization/RightSizingStrategy.cs
```csharp
public sealed class RightSizingStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public double CpuDownsizeThreshold { get; set; };
    public double MemoryDownsizeThreshold { get; set; };
    public double CpuUpsizeThreshold { get; set; };
    public int MinObservationDays { get; set; };
    public void RecordUtilization(string resourceId, string resourceType, double cpuPercent, double memoryPercent, double costPerHour);
    public IReadOnlyList<RightSizingRecommendation> GetRecommendations();
}
```
```csharp
public sealed class ResourceUtilization
{
}
    public required string ResourceId { get; init; }
    public required string ResourceType { get; init; }
    public required double CostPerHour { get; init; }
    public required DateTimeOffset FirstSeen { get; init; }
    public DateTimeOffset LastSeen { get; set; }
    public List<double> CpuSamples { get; };
    public List<double> MemorySamples { get; };
}
```
```csharp
public sealed record RightSizingRecommendation
{
}
    public required string ResourceId { get; init; }
    public required string CurrentType { get; init; }
    public required RightSizeAction RecommendedAction { get; init; }
    public required string Reason { get; init; }
    public required double EstimatedSavingsPercent { get; init; }
    public required double EstimatedSavingsPerHour { get; init; }
    public required double AvgCpuPercent { get; init; }
    public required double AvgMemoryPercent { get; init; }
    public required double MaxCpuPercent { get; init; }
    public required double MaxMemoryPercent { get; init; }
    public string? RecommendedType { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/CloudOptimization/ServerlessOptimizationStrategy.cs
```csharp
public sealed class ServerlessOptimizationStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public double ColdStartAlertThreshold { get; set; };
    public double TargetMemoryEfficiency { get; set; };
    public void RegisterFunction(string functionId, string name, string runtime, int memoryMb, string provider);
    public void RecordExecution(string functionId, double durationMs, double billedDurationMs, int memoryUsedMb, bool wasColdStart);
    public IReadOnlyList<ServerlessRecommendation> GetRecommendations(string functionId);
    public ServerlessFunctionStats GetFunctionStats(string functionId);
}
```
```csharp
public sealed class ServerlessFunction
{
}
    public required string FunctionId { get; init; }
    public required string Name { get; init; }
    public required string Runtime { get; init; }
    public required int MemoryMb { get; init; }
    public required string Provider { get; init; }
    public long InvocationCount { get; set; }
    public long ColdStartCount { get; set; }
}
```
```csharp
public sealed record FunctionExecution
{
}
    public required string FunctionId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required double DurationMs { get; init; }
    public required double BilledDurationMs { get; init; }
    public required int MemoryUsedMb { get; init; }
    public required bool WasColdStart { get; init; }
    public required double MemoryEfficiency { get; init; }
}
```
```csharp
public sealed record ServerlessRecommendation
{
}
    public required string FunctionId { get; init; }
    public required ServerlessRecommendationType Type { get; init; }
    public required string Description { get; init; }
    public required int Priority { get; init; }
    public int? RecommendedMemoryMb { get; init; }
    public double EstimatedCostSavingsPercent { get; init; }
    public double EstimatedSavingsMs { get; init; }
}
```
```csharp
public sealed record ServerlessFunctionStats
{
}
    public required string FunctionId { get; init; }
    public string? FunctionName { get; init; }
    public long TotalInvocations { get; init; }
    public long ColdStartCount { get; init; }
    public double ColdStartRate { get; init; }
    public double AvgDurationMs { get; init; }
    public double P95DurationMs { get; init; }
    public double AvgMemoryEfficiency { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/CloudOptimization/CarbonAwareRegionSelectionStrategy.cs
```csharp
public sealed class CarbonAwareRegionSelectionStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public int MaxLatencyMs { get; set; };
    public double CarbonWeight { get; set; };
    public double LatencyWeight { get; set; };
    public double CostWeight { get; set; };
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task DisposeCoreAsync();
    public RegionSelectionResult SelectRegion(string provider, IEnumerable<string>? preferredRegions = null, int? maxLatencyMs = null, bool prioritizeCarbon = true);
    public IReadOnlyList<RegionCarbonData> GetRegionData();
}
```
```csharp
public sealed class RegionCarbonData
{
}
    public required string Provider { get; init; }
    public required string RegionId { get; init; }
    public required string Name { get; init; }
    public double CarbonIntensity { get; set; }
    public required int LatencyMs { get; init; }
    public required double CostMultiplier { get; init; }
    public DateTimeOffset LastUpdated { get; set; }
}
```
```csharp
public sealed record RegionSelectionResult
{
}
    public required bool Success { get; init; }
    public string? Reason { get; init; }
    public string? SelectedRegion { get; init; }
    public string? Provider { get; init; }
    public double CarbonIntensity { get; init; }
    public int LatencyMs { get; init; }
    public double CostMultiplier { get; init; }
    public double CarbonSavedGCO2ePerKwh { get; init; }
    public List<string>? AlternativeRegions { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/CloudOptimization/MultiCloudOptimizationStrategy.cs
```csharp
public sealed class MultiCloudOptimizationStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public double CostWeight { get; set; };
    public double CarbonWeight { get; set; };
    public double LatencyWeight { get; set; };
    public void RegisterProvider(string providerId, string name, double baseCostMultiplier, double carbonIntensity, int avgLatencyMs);
    public void UpdateProviderMetrics(string providerId, double currentCostMultiplier, double currentCarbonIntensity, int currentLatencyMs, bool isAvailable);
    public void RegisterWorkload(string workloadId, string name, string currentProvider, double baselineCostUsd, bool isPortable);
    public PlacementRecommendation GetOptimalPlacement(string workloadId);
    public IReadOnlyList<PlacementRecommendation> GetAllRecommendations();
    public IReadOnlyList<ProviderComparison> GetProviderComparison();
}
```
```csharp
public sealed class CloudProvider
{
}
    public required string ProviderId { get; init; }
    public required string Name { get; init; }
    public double BaseCostMultiplier { get; set; }
    public double CarbonIntensity { get; set; }
    public int AvgLatencyMs { get; set; }
    public bool IsAvailable { get; set; }
    public DateTimeOffset LastUpdated { get; set; }
}
```
```csharp
public sealed class MultiCloudWorkload
{
}
    public required string WorkloadId { get; init; }
    public required string Name { get; init; }
    public required string CurrentProvider { get; init; }
    public required double BaselineCostUsd { get; init; }
    public required bool IsPortable { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
```
```csharp
public sealed record PlacementRecommendation
{
}
    public required string WorkloadId { get; init; }
    public bool Success { get; init; }
    public string? Reason { get; init; }
    public string? CurrentProvider { get; init; }
    public string? RecommendedProvider { get; init; }
    public double Score { get; init; }
    public double EstimatedCostSavingsUsd { get; init; }
    public double EstimatedCarbonReductionGrams { get; init; }
    public bool ShouldMigrate { get; init; }
}
```
```csharp
public sealed record ProviderComparison
{
}
    public required string ProviderId { get; init; }
    public required string Name { get; init; }
    public required double CostMultiplier { get; init; }
    public required double CarbonIntensity { get; init; }
    public required int AvgLatencyMs { get; init; }
    public required bool IsAvailable { get; init; }
    public required int WorkloadCount { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/Scheduling/WorkloadMigrationStrategy.cs
```csharp
public sealed class WorkloadMigrationStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public double MigrationThreshold { get; set; };
    public int MinMigrationIntervalHours { get; set; };
    public void RegisterDataCenter(string dcId, string name, string region, double carbonIntensity, bool hasRenewable);
    public void UpdateCarbonIntensity(string dcId, double carbonIntensity, double renewablePercent);
    public void RegisterWorkload(string workloadId, string name, string currentDcId, double powerWatts, bool isMigratable);
    public IReadOnlyList<MigrationRecommendation> GetMigrationRecommendations();
    public bool MigrateWorkload(string workloadId, string targetDcId);
}
```
```csharp
public sealed class DataCenter
{
}
    public required string DataCenterId { get; init; }
    public required string Name { get; init; }
    public required string Region { get; init; }
    public double CarbonIntensity { get; set; }
    public bool HasRenewableEnergy { get; set; }
    public double RenewablePercent { get; set; }
    public double AvailableCapacityPercent { get; set; }
    public DateTimeOffset LastUpdated { get; set; }
}
```
```csharp
public sealed class Workload
{
}
    public required string WorkloadId { get; init; }
    public required string Name { get; init; }
    public string CurrentDataCenterId { get; set; };
    public required double PowerWatts { get; init; }
    public required bool IsMigratable { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastMigratedAt { get; set; }
}
```
```csharp
public sealed record MigrationRecommendation
{
}
    public required string WorkloadId { get; init; }
    public required string WorkloadName { get; init; }
    public required string CurrentDataCenterId { get; init; }
    public required string TargetDataCenterId { get; init; }
    public required double CurrentCarbonIntensity { get; init; }
    public required double TargetCarbonIntensity { get; init; }
    public required double CarbonSavedGramsPerHour { get; init; }
    public required int Priority { get; init; }
}
```
```csharp
public sealed record MigrationEvent
{
}
    public required string WorkloadId { get; init; }
    public required string SourceDataCenterId { get; init; }
    public required string TargetDataCenterId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/Scheduling/DemandResponseStrategy.cs
```csharp
public sealed class DemandResponseStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public bool AutoRespond { get; set; };
    public int MaxReductionPercent { get; set; };
    public DemandResponseEvent? ActiveEvent
{
    get
    {
        lock (_lock)
            return _activeEvent;
    }
}
    public bool InDemandResponseEvent
{
    get
    {
        lock (_lock)
            return _activeEvent != null;
    }
}
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task DisposeCoreAsync();
    public async Task RespondToEventAsync(DemandResponseEvent drEvent, CancellationToken ct = default);
    public void EndParticipation();
    public string? DrApiEndpoint { get; set; }
    public string? DrApiKey { get; set; }
}
```
```csharp
public sealed record DemandResponseEvent
{
}
    public required string EventId { get; init; }
    public required DateTimeOffset StartTime { get; init; }
    public required DateTimeOffset EndTime { get; init; }
    public required int RequestedReductionPercent { get; init; }
    public required double IncentivePerKwh { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/Scheduling/OffPeakSchedulingStrategy.cs
```csharp
public sealed class OffPeakSchedulingStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public int OffPeakStartHour { get; set; };
    public int OffPeakEndHour { get; set; };
    public bool WeekendIsOffPeak { get; set; };
    public bool IsOffPeak
{
    get
    {
        var now = DateTime.Now;
        if (WeekendIsOffPeak && (now.DayOfWeek == DayOfWeek.Saturday || now.DayOfWeek == DayOfWeek.Sunday))
            return true;
        var hour = now.Hour;
        return OffPeakStartHour > OffPeakEndHour ? hour >= OffPeakStartHour || hour < OffPeakEndHour : hour >= OffPeakStartHour && hour < OffPeakEndHour;
    }
}
    public int PendingJobCount;;
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task DisposeCoreAsync();
    public string ScheduleJob(Func<CancellationToken, Task> job, string name, DateTimeOffset? deadline = null);
    public DateTimeOffset GetNextOffPeakWindow();
}
```
```csharp
internal sealed record ScheduledJob
{
}
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required Func<CancellationToken, Task> Job { get; init; }
    public required DateTimeOffset ScheduledAt { get; init; }
    public required DateTimeOffset Deadline { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/Scheduling/BatchJobOptimizationStrategy.cs
```csharp
public sealed class BatchJobOptimizationStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public double LowCarbonThreshold { get; set; };
    public int MaxDelayHours { get; set; };
    public double CarbonWeight { get; set; };
    public double CostWeight { get; set; };
    public void SetCarbonIntensityProvider(Func<Task<double>> provider);;
    public void SetElectricityPriceProvider(Func<Task<double>> provider);;
    public string SubmitJob(string name, double estimatedKwh, TimeSpan maxDelay, int priority = 5);
    public async Task<ScheduleRecommendation> GetOptimalScheduleAsync(string jobId);
    public async Task<JobExecutionResult> ExecuteJobAsync(string jobId, CancellationToken ct = default);
    public IReadOnlyList<BatchJob> GetPendingJobs();
    public BatchJobStatistics GetJobStatistics();
}
```
```csharp
public sealed class BatchJob
{
}
    public required string JobId { get; init; }
    public required string Name { get; init; }
    public required double EstimatedKwh { get; init; }
    public required TimeSpan MaxDelay { get; init; }
    public required int Priority { get; init; }
    public required DateTimeOffset SubmittedAt { get; init; }
    public required DateTimeOffset Deadline { get; init; }
}
```
```csharp
public sealed record ScheduleRecommendation
{
}
    public required string JobId { get; init; }
    public bool Success { get; init; }
    public string? Reason { get; init; }
    public DateTimeOffset? RecommendedExecutionTime { get; init; }
    public double ExpectedCarbonIntensity { get; init; }
    public double ExpectedPrice { get; init; }
}
```
```csharp
public sealed record JobExecutionResult
{
}
    public required string JobId { get; init; }
    public bool Success { get; init; }
    public string? Reason { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public double CarbonIntensity { get; init; }
    public double EstimatedEmissionsGrams { get; init; }
}
```
```csharp
public sealed class BatchJobExecution
{
}
    public required string JobId { get; init; }
    public required string JobName { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required double CarbonIntensity { get; init; }
    public required double EstimatedEmissionsGrams { get; init; }
}
```
```csharp
public sealed record BatchJobStatistics
{
}
    public int TotalJobsExecuted { get; init; }
    public double TotalEmissionsGrams { get; init; }
    public double AverageCarbonIntensity { get; init; }
    public int PendingJobCount { get; init; }
    public double PendingJobsKwh { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/Scheduling/RenewableEnergyWindowStrategy.cs
```csharp
public sealed class RenewableEnergyWindowStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public double MinRenewablePercent { get; set; };
    public double CurrentRenewablePercent
{
    get
    {
        lock (_lock)
            return _currentRenewablePercent;
    }
}
    public bool IsHighRenewable;;
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task DisposeCoreAsync();
    public RenewableWindow? FindNextWindow(TimeSpan lookahead);
}
```
```csharp
public sealed record RenewableWindow
{
}
    public required DateTimeOffset StartTime { get; init; }
    public required DateTimeOffset EndTime { get; init; }
    public required double AverageRenewablePercent { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/ThermalManagement/LiquidCoolingOptimizationStrategy.cs
```csharp
public sealed class LiquidCoolingOptimizationStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public double TargetDeltaT { get; set; };
    public double MaxInletTemperature { get; set; };
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task DisposeCoreAsync();
    public void RegisterLoop(string loopId, string name, double maxFlowRateLpm, double pumpPowerWatts);
    public void UpdateLoopReadings(string loopId, double inletTempC, double outletTempC, double flowRateLpm);
    public FlowRateRecommendation GetOptimalFlowRate(string loopId, double heatLoadKw);
    public bool SetFlowRate(string loopId, double flowRateLpm);
}
```
```csharp
public sealed class CoolingLoop
{
}
    public required string LoopId { get; init; }
    public required string Name { get; init; }
    public required double MaxFlowRateLpm { get; init; }
    public required double PumpPowerWatts { get; init; }
    public double CurrentFlowRateLpm { get; set; }
    public double InletTemperatureC { get; set; }
    public double OutletTemperatureC { get; set; }
    public double DeltaT { get; set; }
    public double HeatRemovalKw { get; set; }
}
```
```csharp
public sealed record FlowRateRecommendation
{
}
    public required string LoopId { get; init; }
    public bool Success { get; init; }
    public string? Reason { get; init; }
    public double CurrentFlowRateLpm { get; init; }
    public double OptimalFlowRateLpm { get; init; }
    public double CurrentPumpPowerWatts { get; init; }
    public double OptimalPumpPowerWatts { get; init; }
    public double PotentialSavingsWatts { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/ThermalManagement/CoolingOptimizationStrategy.cs
```csharp
public sealed class CoolingOptimizationStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public double TargetTemperatureC { get; set; };
    public CoolingProfile CurrentProfile
{
    get
    {
        lock (_lock)
            return _currentProfile;
    }
}
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task DisposeCoreAsync();
    public async Task SetProfileAsync(CoolingProfile profile, CancellationToken ct = default);
}
```
```csharp
public sealed class FanZone
{
}
    public required string Name { get; init; }
    public string? SysFsPath { get; init; }
    public int MaxRpm { get; init; }
    public int CurrentRpm { get; set; }
    public int CurrentPercent { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/ThermalManagement/HotColdAisleStrategy.cs
```csharp
public sealed class HotColdAisleStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public double TargetColdAisleTemp { get; set; };
    public double MaxHotAisleTemp { get; set; };
    public double TargetDeltaT { get; set; };
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task DisposeCoreAsync();
    public void RegisterZone(string zoneId, string name, AisleType type, int rackCount);
    public void UpdateZoneReadings(string zoneId, double temperature, double humidity, double airflowCfm);
    public ContainmentEffectiveness GetContainmentEffectiveness();
    public IReadOnlyList<AirflowRecommendation> GetAirflowRecommendations();
}
```
```csharp
public sealed class AisleZone
{
}
    public required string ZoneId { get; init; }
    public required string Name { get; init; }
    public required AisleType Type { get; init; }
    public required int RackCount { get; init; }
    public double CurrentTemperature { get; set; }
    public double CurrentHumidity { get; set; }
    public double AirflowCfm { get; set; }
    public DateTimeOffset LastUpdated { get; set; }
}
```
```csharp
public sealed record ContainmentEffectiveness
{
}
    public bool Success { get; init; }
    public string? Reason { get; init; }
    public double EffectivenessPercent { get; init; }
    public double AverageColdAisleTemp { get; init; }
    public double AverageHotAisleTemp { get; init; }
    public double ActualDeltaT { get; init; }
    public int HotSpotCount { get; init; }
    public List<string> HotSpotZones { get; init; };
}
```
```csharp
public sealed record AirflowRecommendation
{
}
    public required string ZoneId { get; init; }
    public required AirflowRecommendationType Type { get; init; }
    public required string Reason { get; init; }
    public required double EstimatedSavingsWatts { get; init; }
    public required int Priority { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/ThermalManagement/ThermalThrottlingStrategy.cs
```csharp
public sealed class ThermalThrottlingStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public double LightThrottleC { get; set; };
    public double ModerateThrottleC { get; set; };
    public double HeavyThrottleC { get; set; };
    public double EmergencyShutdownC { get; set; };
    public ThrottleLevel CurrentLevel
{
    get
    {
        lock (_lock)
            return _currentLevel;
    }
}
    public double CurrentTemperature
{
    get
    {
        lock (_lock)
            return _currentTemp;
    }
}
    public void SetTemperatureProvider(Func<Task<double>> provider);;
    public void SetFrequencyController(Func<int, Task> controller);;
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task DisposeCoreAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/ThermalManagement/TemperatureMonitoringStrategy.cs
```csharp
public sealed class TemperatureMonitoringStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public double WarningThresholdC { get; set; };
    public double CriticalThresholdC { get; set; };
    public IReadOnlyDictionary<string, ThermalZone> Zones
{
    get
    {
        lock (_lock)
            return new Dictionary<string, ThermalZone>(_zones);
    }
}
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task DisposeCoreAsync();
}
```
```csharp
public sealed class ThermalZone
{
}
    public required string Name { get; init; }
    public required string Type { get; init; }
    public string? SysPath { get; init; }
    public double CurrentTempC { get; set; }
    public double MaxTempC { get; set; }
    public DateTimeOffset LastUpdated { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/GreenTiering/GreenTieringStrategy.cs
```csharp
public sealed record GreenMigrationCandidate
{
}
    public required string ObjectKey { get; init; }
    public required string CurrentBackendId { get; init; }
    public required double CurrentGreenScore { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTimeOffset LastAccessed { get; init; }
    public required string TenantId { get; init; }
}
```
```csharp
public sealed record GreenMigrationBatch
{
}
    public required IReadOnlyList<GreenMigrationCandidate> Candidates { get; init; }
    public required string TargetBackendId { get; init; }
    public required double TargetGreenScore { get; init; }
    public required double EstimatedCarbonCostGrams { get; init; }
    public required double EstimatedEnergyWh { get; init; }
    public required DateTimeOffset ScheduledFor { get; init; }
    public required string TenantId { get; init; }
    public long TotalSizeBytes;;
}
```
```csharp
public sealed class GreenTieringStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public GreenTieringStrategy(GreenTieringPolicyEngine policyEngine);
    public GreenTieringPolicyEngine PolicyEngine;;
    public int PendingBatchCount;;
    public IReadOnlyList<GreenMigrationBatch> GetPendingBatches();
    public GreenMigrationBatch? DequeueBatch();
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task DisposeCoreAsync();
    public async Task<IReadOnlyList<GreenMigrationCandidate>> IdentifyColdDataAsync(string tenantId, CancellationToken ct = default);
    public async Task<GreenMigrationBatch?> PlanMigrationBatchAsync(IReadOnlyList<GreenMigrationCandidate> candidates, string tenantId, CancellationToken ct = default);
    public async Task<bool> ShouldMigrateNowAsync(GreenMigrationSchedule schedule, CancellationToken ct = default);
    public override Task<IReadOnlyList<SustainabilityRecommendation>> GetRecommendationsAsync(CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/GreenTiering/ColdDataCarbonMigrationStrategy.cs
```csharp
public sealed record MigrationRecord
{
}
    public required string ObjectKey { get; init; }
    public required string SourceBackendId { get; init; }
    public required string TargetBackendId { get; init; }
    public required long SizeBytes { get; init; }
    public required double CarbonSavedGrams { get; init; }
    public required double EnergySavedWh { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public required string TenantId { get; init; }
}
```
```csharp
public sealed class ColdDataCarbonMigrationStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public long TotalDataMigratedBytes
{
    get
    {
        lock (_statsLock2)
            return _totalDataMigratedBytes;
    }
}
    public double TotalCarbonSavedGrams
{
    get
    {
        lock (_statsLock2)
            return _totalCarbonSavedGrams;
    }
}
    public long TotalMigrationErrors
{
    get
    {
        lock (_statsLock2)
            return _totalMigrationErrors;
    }
}
    public long TotalMigrationsCompleted
{
    get
    {
        lock (_statsLock2)
            return _totalMigrationsCompleted;
    }
}
    public int RetryQueueCount;;
    public async Task<BatchExecutionResult> ExecuteBatchAsync(GreenMigrationBatch batch, CancellationToken ct = default);
    public IReadOnlyList<MigrationRecord> GetMigrationHistory(string? tenantId = null, int maxEntries = 100);
    public MigrationStatistics GetMigrationStatistics();
    public override Task<IReadOnlyList<SustainabilityRecommendation>> GetRecommendationsAsync(CancellationToken ct = default);
}
```
```csharp
private sealed record MigrationObjectResult
{
}
    public required bool Success { get; init; }
    public required long SizeBytes { get; init; }
    public required double CarbonSavedGrams { get; init; }
    public required double EnergySavedWh { get; init; }
    public string? ErrorMessage { get; init; }
}
```
```csharp
public sealed record BatchExecutionResult
{
}
    public required string TenantId { get; init; }
    public required string TargetBackendId { get; init; }
    public required int TotalCandidates { get; init; }
    public required int SuccessfulMigrations { get; init; }
    public required int FailedMigrations { get; init; }
    public required double TotalCarbonSavedGrams { get; init; }
    public required double TotalEnergySavedWh { get; init; }
    public required long TotalBytesMigrated { get; init; }
    public string? ErrorMessage { get; init; }
    public bool AllSucceeded;;
}
```
```csharp
public sealed record MigrationStatistics
{
}
    public required long TotalMigrationsCompleted { get; init; }
    public required long TotalMigrationErrors { get; init; }
    public required long TotalDataMigratedBytes { get; init; }
    public required double TotalCarbonSavedGrams { get; init; }
    public required int RetryQueueSize { get; init; }
    public required int HistorySize { get; init; }
    public required double EstimatedAnnualCarbonSavingsGrams { get; init; }
    public string TotalDataMigratedFormatted
{
    get
    {
        if (TotalDataMigratedBytes >= 1L << 40)
            return $"{TotalDataMigratedBytes / (double)(1L << 40):F2} TB";
        if (TotalDataMigratedBytes >= 1L << 30)
            return $"{TotalDataMigratedBytes / (double)(1L << 30):F2} GB";
        if (TotalDataMigratedBytes >= 1L << 20)
            return $"{TotalDataMigratedBytes / (double)(1L << 20):F2} MB";
        return $"{TotalDataMigratedBytes / 1024.0:F2} KB";
    }
}
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/GreenTiering/GreenTieringPolicyEngine.cs
```csharp
public sealed record GreenTieringPolicy
{
}
    public required string TenantId { get; init; }
    public TimeSpan ColdThreshold { get; init; };
    public double TargetGreenScore { get; init; };
    public GreenMigrationSchedule MigrationSchedule { get; init; };
    public long MaxMigrationBatchSizeBytes { get; init; };
    public int MaxConcurrentMigrations { get; init; };
    public bool RespectCarbonBudget { get; init; };
    public bool Enabled { get; init; };
    public int BatchHourUtc { get; init; };
    public DayOfWeek BatchDayOfWeek { get; init; };
}
```
```csharp
public sealed class GreenTieringPolicyEngine : IDisposable
{
}
    public GreenTieringPolicyEngine(string dataDirectory);
    public void SetPolicy(string tenantId, GreenTieringPolicy policy);
    public GreenTieringPolicy GetPolicy(string tenantId);
    public IReadOnlyList<string> GetEnabledTenants();
    public bool RemovePolicy(string tenantId);
    public int PolicyCount;;
    public void Flush();
    public void Dispose();
}
```
```csharp
private sealed class TimeSpanJsonConverter : JsonConverter<TimeSpan>
{
}
    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options);
    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/BatteryAwareness/ChargeAwareSchedulingStrategy.cs
```csharp
public sealed class ChargeAwareSchedulingStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public int DeferThreshold { get; set; };
    public bool OnBattery
{
    get
    {
        lock (_lock)
            return _onBattery;
    }
}
    public int DeferredCount
{
    get
    {
        lock (_lock)
            return _deferredWorkloads.Count;
    }
}
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task DisposeCoreAsync();
    public string ScheduleWorkload(Func<CancellationToken, Task> workload, string name, bool intensive);
}
```
```csharp
internal sealed record DeferredWorkload
{
}
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required Func<CancellationToken, Task> Workload { get; init; }
    public required DateTimeOffset DeferredAt { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/BatteryAwareness/BatteryLevelMonitoringStrategy.cs
```csharp
public sealed class BatteryLevelMonitoringStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public int LowBatteryThreshold { get; set; };
    public int CriticalBatteryThreshold { get; set; };
    public BatteryStatus CurrentStatus
{
    get
    {
        lock (_lock)
            return _currentStatus;
    }
}
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task DisposeCoreAsync();
}
```
```csharp
public sealed class BatteryStatus
{
}
    public bool IsPresent { get; set; }
    public int ChargePercent { get; set; }
    public bool IsCharging { get; set; }
    public bool IsDischarging { get; set; }
    public bool IsFull { get; set; }
    public double DischargingWatts { get; set; }
    public double DesignCapacityWh { get; set; }
    public double HealthPercent { get; set; };
    public TimeSpan? TimeRemaining { get; set; }
}
```
```csharp
public sealed record BatteryReading
{
}
    public required DateTimeOffset Timestamp { get; init; }
    public required int ChargePercent { get; init; }
    public required bool IsCharging { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/BatteryAwareness/PowerSourceSwitchingStrategy.cs
```csharp
public sealed class PowerSourceSwitchingStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public List<string> SourcePriority { get; set; };
    public int MinBatteryReservePercent { get; set; };
    public void RegisterSource(string sourceId, PowerSourceType type, double maxPowerWatts, double carbonIntensity = 0, double costPerKwh = 0);
    public void UpdateSourceAvailability(string sourceId, bool isAvailable, double? currentPowerWatts = null, double? batteryPercent = null);
    public PowerSourceRecommendation GetRecommendedSource(double requiredPowerWatts);
    public bool SwitchTo(string sourceId, string reason);
    public IReadOnlyList<SwitchEvent> GetSwitchHistory(int count = 100);
}
```
```csharp
public sealed class PowerSource
{
}
    public required string SourceId { get; init; }
    public required PowerSourceType Type { get; init; }
    public required double MaxPowerWatts { get; init; }
    public double CarbonIntensityGCO2ePerKwh { get; set; }
    public double CostPerKwh { get; set; }
    public bool IsAvailable { get; set; }
    public double CurrentPowerWatts { get; set; }
    public double? BatteryPercent { get; set; }
}
```
```csharp
public sealed record PowerSourceRecommendation
{
}
    public string? RecommendedSourceId { get; init; }
    public PowerSourceType? SourceType { get; init; }
    public required string Reason { get; init; }
    public double CarbonIntensity { get; init; }
    public double CostPerKwh { get; init; }
    public bool IsEmergency { get; init; }
}
```
```csharp
public sealed record SwitchEvent
{
}
    public required DateTimeOffset Timestamp { get; init; }
    public string? FromSource { get; init; }
    public required string ToSource { get; init; }
    public required string Reason { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/BatteryAwareness/SmartChargingStrategy.cs
```csharp
public sealed class SmartChargingStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public int LongevityChargeTarget { get; set; };
    public int MinimumChargeLevel { get; set; };
    public bool CarbonAwareCharging { get; set; };
    public double LowCarbonThreshold { get; set; };
    public void SetCarbonIntensityProvider(Func<Task<double>> provider);;
    public async Task<ChargingAction> GetChargingActionAsync(int currentChargePercent, bool isPluggedIn);
    public void StartSession(int initialChargePercent);
    public void EndSession(int finalChargePercent, double energyUsedWh);
    public ChargingStatistics GetChargingStatistics();
}
```
```csharp
public sealed record ChargingAction
{
}
    public required ChargingActionType Action { get; init; }
    public required string Reason { get; init; }
    public double? CarbonIntensity { get; init; }
}
```
```csharp
public sealed class ChargingProfile
{
}
    public int MaxChargePercent { get; set; };
    public int MinChargePercent { get; set; };
    public TimeSpan? ChargeByTime { get; set; }
    public bool PreferLowCarbon { get; set; };
}
```
```csharp
public sealed class ChargingSession
{
}
    public required string SessionId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; set; }
    public required int InitialChargePercent { get; init; }
    public int FinalChargePercent { get; set; }
    public double EnergyUsedWh { get; set; }
}
```
```csharp
public sealed record ChargingStatistics
{
}
    public int TotalSessions { get; init; }
    public double TotalEnergyWh { get; init; }
    public double AverageSessionDurationMinutes { get; init; }
    public double AverageChargeGained { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/BatteryAwareness/UpsIntegrationStrategy.cs
```csharp
public sealed class UpsIntegrationStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public string UpsHost { get; set; };
    public string UpsName { get; set; };
    public int ShutdownThreshold { get; set; };
    public int ShutdownRuntimeSeconds { get; set; };
    public UpsStatus CurrentStatus
{
    get
    {
        lock (_lock)
            return _currentStatus;
    }
}
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task DisposeCoreAsync();
    public int NutPort { get; set; };
    public string? NutUsername { get; set; }
    public string? NutPassword { get; set; }
    public int ConnectTimeoutMs { get; set; };
    public async Task InitiateShutdownAsync(string reason, CancellationToken ct = default);
}
```
```csharp
public sealed record UpsStatus
{
}
    public bool IsOnline { get; init; }
    public bool OnUtility { get; init; }
    public bool OnBattery;;
    public int BatteryPercent { get; init; }
    public int RuntimeSeconds { get; init; }
    public double LoadPercent { get; init; }
    public double NominalPowerWatts { get; init; };
    public string Model { get; init; };
    public DateTimeOffset LastUpdate { get; init; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/ResourceEfficiency/CacheOptimizationStrategy.cs
```csharp
public sealed class CacheOptimizationStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public double TargetHitRatePercent { get; set; };
    public double WattsPerGb { get; set; };
    public void RegisterCache(string cacheId, string name, CacheType type, long maxSizeBytes);
    public void RecordCacheStats(string cacheId, long hits, long misses, long currentSizeBytes, long evictions);
    public IReadOnlyList<CacheRecommendation> GetRecommendations();
    public double GetTotalPowerWatts();
}
```
```csharp
public sealed class CacheInstance
{
}
    public required string CacheId { get; init; }
    public required string Name { get; init; }
    public required CacheType Type { get; init; }
    public required long MaxSizeBytes { get; init; }
    public long CurrentSizeBytes { get; set; }
    public long TotalHits { get; set; }
    public long TotalMisses { get; set; }
    public long TotalEvictions { get; set; }
    public double HitRate { get; set; }
}
```
```csharp
public sealed record CacheRecommendation
{
}
    public required string CacheId { get; init; }
    public required CacheRecommendationType Type { get; init; }
    public required long CurrentSizeBytes { get; init; }
    public required long RecommendedSizeBytes { get; init; }
    public required double HitRate { get; init; }
    public required double EstimatedSavingsWatts { get; init; }
    public required string Reason { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/ResourceEfficiency/DiskSpinDownStrategy.cs
```csharp
public sealed class DiskSpinDownStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public int SpinDownSeconds { get; set; };
    public int ApmLevel { get; set; };
    public IReadOnlyDictionary<string, DiskState> Disks
{
    get
    {
        lock (_lock)
        {
            return _disks.ToDictionary(kvp => kvp.Key, kvp => new DiskState { Id = kvp.Value.Id, Type = kvp.Value.Type, IsSpunDown = kvp.Value.IsSpunDown, IdleSeconds = kvp.Value.IdleSeconds, LastSpinDown = kvp.Value.LastSpinDown });
        }
    }
}
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task DisposeCoreAsync();
    public async Task SpinDownAsync(string diskId, CancellationToken ct = default);
}
```
```csharp
public sealed class DiskState
{
}
    public required string Id { get; init; }
    public required DiskType Type { get; init; }
    public bool IsSpunDown { get; set; }
    public int IdleSeconds { get; set; }
    public DateTimeOffset? LastSpinDown { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/ResourceEfficiency/MemoryOptimizationStrategy.cs
```csharp
public sealed class MemoryOptimizationStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public double UsagePercent
{
    get
    {
        lock (_lock)
            return _totalMemoryBytes > 0 ? (double)_usedMemoryBytes / _totalMemoryBytes * 100 : 0;
    }
}
    public double CompressionRatio
{
    get
    {
        lock (_lock)
            return _managedBytesAfterGc > 0 && _managedBytesBeforeGc > 0 ? (double)_managedBytesBeforeGc / _managedBytesAfterGc : 1.0;
    }
}
    public double TargetUsagePercent { get; set; };
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task DisposeCoreAsync();
    public void OptimizeMemory();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/ResourceEfficiency/IdleResourceDetectionStrategy.cs
```csharp
public sealed class IdleResourceDetectionStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public int IdleThresholdMinutes { get; set; };
    public double IdleCpuThresholdPercent { get; set; };
    public long IdleNetworkThresholdBytesPerSec { get; set; };
    public void RegisterResource(string resourceId, string name, ResourceType type, double powerWatts);
    public void RecordActivity(string resourceId, double cpuPercent, long networkBytesPerSec, int activeConnections);
    public IReadOnlyList<IdleResource> GetIdleResources();
    public IdleSavingsSummary GetPotentialSavings();
    public bool TerminateResource(string resourceId);
}
```
```csharp
public sealed class ResourceTracker
{
}
    public required string ResourceId { get; init; }
    public required string Name { get; init; }
    public required ResourceType Type { get; init; }
    public required double PowerWatts { get; init; }
    public required DateTimeOffset FirstSeen { get; init; }
    public DateTimeOffset LastActivityCheck { get; set; }
    public DateTimeOffset? IdleSince { get; set; }
    public DateTimeOffset? LastActiveAt { get; set; }
    public double LastCpuPercent { get; set; }
    public long LastNetworkBytesPerSec { get; set; }
    public int LastActiveConnections { get; set; }
}
```
```csharp
public sealed record IdleResource
{
}
    public required string ResourceId { get; init; }
    public required string Name { get; init; }
    public required ResourceType Type { get; init; }
    public required TimeSpan IdleDuration { get; init; }
    public required double PowerWatts { get; init; }
    public DateTimeOffset? LastActiveAt { get; init; }
    public bool CanShutdown { get; init; }
}
```
```csharp
public sealed record IdleSavingsSummary
{
}
    public int IdleResourceCount { get; init; }
    public double TotalPowerWatts { get; init; }
    public double EstimatedDailyKwh { get; init; }
    public double EstimatedMonthlyCostUsd { get; init; }
    public Dictionary<ResourceType, double> ByType { get; init; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/ResourceEfficiency/NetworkPowerSavingStrategy.cs
```csharp
public sealed class NetworkPowerSavingStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public bool EnableEee { get; set; };
    public bool ReduceIdleSpeed { get; set; };
    public int IdleSpeedMbps { get; set; };
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task DisposeCoreAsync();
    public async Task SetLowPowerModeAsync(string interfaceId, bool enable, CancellationToken ct = default);
}
```
```csharp
public sealed class NetworkInterfaceState
{
}
    public required string Id { get; init; }
    public int MaxSpeedMbps { get; init; }
    public int CurrentSpeedMbps { get; set; }
    public bool LowPowerMode { get; set; }
    public int IdleSeconds { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/EnergyOptimization/WorkloadConsolidationStrategy.cs
```csharp
public sealed class WorkloadConsolidationStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public int ActiveCoreCount
{
    get
    {
        lock (_lock)
            return _activeCoreCount;
    }
}
    public int TotalCoreCount
{
    get
    {
        lock (_lock)
            return _totalCoreCount;
    }
}
    public double ConsolidationRatio
{
    get
    {
        lock (_lock)
            return _consolidationRatio;
    }
}
    public int MinActiveCores { get; set; };
    public double TargetUtilizationPercent { get; set; };
    public bool AutoConsolidate { get; set; };
    public TimeSpan ConsolidationCooldown { get; set; };
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task DisposeCoreAsync();
    public async Task SetActiveCoresAsync(int coreCount, CancellationToken ct = default);
    public ConsolidationState GetConsolidationState();
    public void SetProcessAffinity(int processId);
}
```
```csharp
public sealed class CoreState
{
}
    public required int CoreId { get; init; }
    public bool IsActive { get; set; }
    public double Utilization { get; set; }
    public DateTimeOffset LastUpdated { get; set; }
}
```
```csharp
public sealed record ConsolidationState
{
}
    public required DateTimeOffset Timestamp { get; init; }
    public required int TotalCores { get; init; }
    public required int ActiveCores { get; init; }
    public required int InactiveCores { get; init; }
    public required double ConsolidationRatio { get; init; }
    public required double AverageActiveUtilization { get; init; }
    public required double EstimatedPowerSavingsPercent { get; init; }
    public required IReadOnlyDictionary<int, bool> CoreStates { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/EnergyOptimization/GpuPowerManagementStrategy.cs
```csharp
public sealed class GpuPowerManagementStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public int PowerLimitPercent { get; set; };
    public bool AggressiveIdleMode { get; set; };
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task DisposeCoreAsync();
    public bool SetPowerLimit(string gpuId, int powerLimitWatts);
    public IReadOnlyList<GpuInfo> GetGpus();;
}
```
```csharp
public sealed class GpuInfo
{
}
    public int Index { get; init; }
    public required string Name { get; init; }
    public required string Vendor { get; init; }
    public double PowerLimitWatts { get; set; }
    public double PowerDrawWatts { get; set; }
    public double TemperatureCelsius { get; set; }
    public int UtilizationPercent { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/EnergyOptimization/VirtualizationOptimizationStrategy.cs
```csharp
public sealed class VirtualizationOptimizationStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public int TargetHostCpuPercent { get; set; };
    public int MinVmsPerHost { get; set; };
    public void RegisterHost(string hostId, int cpuCores, long memoryBytes, double idlePowerWatts, double maxPowerWatts);
    public void RegisterVm(string vmId, string hostId, int vCpus, long memoryBytes, double cpuUtilization);
    public void UpdateVmUtilization(string vmId, double cpuUtilization);
    public IReadOnlyList<ConsolidationRecommendation> GetConsolidationRecommendations();
    public double GetHostPowerEfficiency(string hostId);
}
```
```csharp
public sealed class HostInfo
{
}
    public required string HostId { get; init; }
    public required int CpuCores { get; init; }
    public required long MemoryBytes { get; init; }
    public required double IdlePowerWatts { get; init; }
    public required double MaxPowerWatts { get; init; }
    public double CurrentCpuPercent { get; set; }
    public int VmCount { get; set; }
}
```
```csharp
public sealed record VmInfo
{
}
    public required string VmId { get; init; }
    public required string HostId { get; init; }
    public required int VCpus { get; init; }
    public required long MemoryBytes { get; init; }
    public required double CpuUtilization { get; init; }
}
```
```csharp
public sealed record ConsolidationRecommendation
{
}
    public required string SourceHostId { get; init; }
    public required string TargetHostId { get; init; }
    public required List<string> VmIds { get; init; }
    public required double EstimatedPowerSavingsWatts { get; init; }
    public required double SourceHostUtilization { get; init; }
    public required double TargetHostUtilization { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/EnergyOptimization/CpuFrequencyScalingStrategy.cs
```csharp
public sealed class CpuFrequencyScalingStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public FrequencyGovernor CurrentGovernor
{
    get
    {
        lock (_lock)
            return _currentGovernor;
    }
}
    public double CurrentFrequencyMhz
{
    get
    {
        lock (_lock)
            return _currentFrequencyMhz;
    }
}
    public double MinFrequencyMhz
{
    get
    {
        lock (_lock)
            return _minFrequencyMhz;
    }
}
    public double MaxFrequencyMhz
{
    get
    {
        lock (_lock)
            return _maxFrequencyMhz;
    }
}
    public bool AllowFrequencyControl { get; set; };
    public int MinFrequencyPercent { get; set; };
    public int MaxFrequencyPercent { get; set; };
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task DisposeCoreAsync();
    public async Task SetGovernorAsync(FrequencyGovernor governor, CancellationToken ct = default);
    public async Task SetFrequencyLimitsAsync(int minPercent, int maxPercent, CancellationToken ct = default);
    public CpuFrequencyInfo GetFrequencyInfo();
    public async Task<IDisposable> BoostFrequencyAsync(TimeSpan duration, CancellationToken ct = default);
}
```
```csharp
private sealed class FrequencyBoostHandle : IDisposable
{
}
    public FrequencyBoostHandle(CpuFrequencyScalingStrategy strategy, FrequencyGovernor originalGovernor, TimeSpan duration);
    public void Dispose();
}
```
```csharp
public sealed record CpuFrequencyInfo
{
}
    public required DateTimeOffset Timestamp { get; init; }
    public required double CurrentFrequencyMhz { get; init; }
    public required double MinFrequencyMhz { get; init; }
    public required double MaxFrequencyMhz { get; init; }
    public required double BaseFrequencyMhz { get; init; }
    public required FrequencyGovernor Governor { get; init; }
    public required double FrequencyPercent { get; init; }
    public required double EstimatedPowerSavingsPercent { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/EnergyOptimization/PowerCappingStrategy.cs
```csharp
public sealed class PowerCappingStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public double CurrentPowerWatts
{
    get
    {
        lock (_lock)
            return _currentPowerWatts;
    }
}
    public double PowerCapWatts
{
    get
    {
        lock (_lock)
            return _powerCapWatts;
    }

    set
    {
        lock (_lock)
            _powerCapWatts = value;
    }
}
    public double DefaultTdpWatts
{
    get
    {
        lock (_lock)
            return _defaultTdpWatts;
    }
}
    public bool IsCapEnforced
{
    get
    {
        lock (_lock)
            return _capEnforced;
    }
}
    public PowerCappingMethod Method { get; set; };
    public PowerCapAction CapAction { get; set; };
    public double HysteresisPercent { get; set; };
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task DisposeCoreAsync();
    public async Task SetPowerCapAsync(double capWatts, CancellationToken ct = default);
    public async Task RemovePowerCapAsync(CancellationToken ct = default);
    public PowerBreakdown GetPowerBreakdown();
    public IReadOnlyList<PowerReading> GetPowerHistory(TimeSpan duration);
}
```
```csharp
public sealed record PowerBreakdown
{
}
    public required DateTimeOffset Timestamp { get; init; }
    public required double TotalPowerWatts { get; init; }
    public required double CpuPowerWatts { get; init; }
    public required double DramPowerWatts { get; init; }
    public required double GpuPowerWatts { get; init; }
    public required double OtherPowerWatts { get; init; }
    public required double PowerCapWatts { get; init; }
    public required double CapUtilizationPercent { get; init; }
    public required bool IsCapEnforced { get; init; }
}
```
```csharp
public sealed record PowerReading
{
}
    public required DateTimeOffset Timestamp { get; init; }
    public required double PowerWatts { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/EnergyOptimization/StorageTieringStrategy.cs
```csharp
public sealed class StorageTieringStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public int WarmTierThresholdDays { get; set; };
    public int ColdTierThresholdDays { get; set; };
    public void RegisterTier(string tierId, string name, TierType type, double powerWattsPerTb);
    public void TrackObject(string objectId, string currentTier, long sizeBytes, DateTimeOffset lastAccessed);
    public void RecordAccess(string objectId);
    public IReadOnlyList<TieringRecommendation> GetTieringRecommendations();
    public double GetTotalPowerWatts();
}
```
```csharp
public sealed record StorageTier
{
}
    public required string TierId { get; init; }
    public required string Name { get; init; }
    public required TierType Type { get; init; }
    public required double PowerWattsPerTb { get; init; }
}
```
```csharp
public sealed record DataObjectInfo
{
}
    public required string ObjectId { get; init; }
    public required string CurrentTier { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTimeOffset LastAccessed { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public int AccessCount { get; init; }
}
```
```csharp
public sealed record TieringRecommendation
{
}
    public required string ObjectId { get; init; }
    public required string CurrentTier { get; init; }
    public required string RecommendedTier { get; init; }
    public required int DaysSinceAccess { get; init; }
    public required long SizeBytes { get; init; }
    public required double EstimatedPowerSavingsWatts { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/EnergyOptimization/SleepStatesStrategy.cs
```csharp
public sealed class SleepStatesStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public CState CurrentCState
{
    get
    {
        lock (_lock)
            return _currentCState;
    }
}
    public SState CurrentSState
{
    get
    {
        lock (_lock)
            return _currentSState;
    }
}
    public CState MaxCState { get; set; };
    public TimeSpan StandbyTimeout { get; set; };
    public TimeSpan HibernateTimeout { get; set; };
    public bool AllowStandby { get; set; };
    public bool AllowHibernate { get; set; };
    public bool AggressiveCStates { get; set; };
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task DisposeCoreAsync();
    public void RecordActivity();
    public SleepStateInfo GetSleepStateInfo();
    public async Task SetMaxCStateAsync(CState maxCState, CancellationToken ct = default);
    public async Task RequestSStateAsync(SState sState, CancellationToken ct = default);
    public async Task SetAggressiveModeAsync(bool aggressive, CancellationToken ct = default);
    public IReadOnlyDictionary<CState, double> GetCStateResidency();
}
```
```csharp
public sealed record SleepStateInfo
{
}
    public required DateTimeOffset Timestamp { get; init; }
    public required CState CurrentCState { get; init; }
    public required SState CurrentSState { get; init; }
    public required TimeSpan IdleTime { get; init; }
    public required DateTimeOffset LastActivityTime { get; init; }
    public required CState MaxAllowedCState { get; init; }
    public required double EstimatedPowerSavingsPercent { get; init; }
    public required IReadOnlyDictionary<CState, double> ResidencyPercent { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/CarbonAwareness/CarbonAwareSchedulingStrategy.cs
```csharp
public sealed class CarbonAwareSchedulingStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public TimeSpan MaxDelay { get; set; };
    public double CarbonThreshold { get; set; };
    public int PendingWorkloadCount;;
    public void SetCarbonIntensityProvider(Func<Task<double>> provider);
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task DisposeCoreAsync();
    public string ScheduleWorkload(Func<CancellationToken, Task> workload, string name, TimeSpan estimatedDuration, double estimatedEnergyWh, WorkloadPriority priority = WorkloadPriority.Normal, DateTimeOffset? deadline = null);
    public bool CancelWorkload(string workloadId);
    public IReadOnlyList<ScheduledWorkloadInfo> GetPendingWorkloads();
    public IReadOnlyList<CompletedWorkload> GetCompletedWorkloads(int count = 100);
    public async Task<WorkloadResult> ExecuteImmediatelyAsync(string workloadId, CancellationToken ct = default);
}
```
```csharp
internal sealed class ScheduledWorkload
{
}
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required Func<CancellationToken, Task> Workload { get; init; }
    public required DateTimeOffset ScheduledAt { get; init; }
    public required TimeSpan EstimatedDuration { get; init; }
    public required double EstimatedEnergyWh { get; init; }
    public required WorkloadPriority Priority { get; init; }
    public required DateTimeOffset Deadline { get; init; }
}
```
```csharp
public sealed record ScheduledWorkloadInfo
{
}
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required DateTimeOffset ScheduledAt { get; init; }
    public required DateTimeOffset Deadline { get; init; }
    public required WorkloadPriority Priority { get; init; }
    public required double EstimatedEnergyWh { get; init; }
}
```
```csharp
public sealed record CompletedWorkload
{
}
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required DateTimeOffset ScheduledAt { get; init; }
    public required DateTimeOffset ExecutedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public required double CarbonIntensityAtExecution { get; init; }
    public required double EnergyConsumedWh { get; init; }
    public required double CarbonEmittedGrams { get; init; }
    public required bool Success { get; init; }
}
```
```csharp
public sealed record WorkloadResult
{
}
    public required string WorkloadId { get; init; }
    public required bool Success { get; init; }
    public DateTimeOffset? ExecutedAt { get; init; }
    public TimeSpan? Duration { get; init; }
    public double? CarbonIntensity { get; init; }
    public double? CarbonEmittedGrams { get; init; }
    public string? ErrorMessage { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/CarbonAwareness/GridCarbonApiStrategy.cs
```csharp
public sealed class GridCarbonApiStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public enum ApiProvider;
    public ApiProvider Provider { get => Enum.Parse<ApiProvider>(_selectedProvider); set => _selectedProvider = value.ToString(); }
    public string? WattTimeUsername { get; set; }
    public string? WattTimePassword { get; set; }
    public string? ElectricityMapsApiKey { get; set; }
    public double Latitude { get; set; };
    public double Longitude { get; set; };
    public string Region { get; set; };
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task DisposeCoreAsync();
    public async Task<GridCarbonData> GetCurrentCarbonIntensityAsync(CancellationToken ct = default);
    public async Task<IReadOnlyList<GridCarbonForecast>> GetForecastAsync(int hours = 24, CancellationToken ct = default);
    public async Task<OptimalScheduleWindow?> FindOptimalWindowAsync(TimeSpan duration, TimeSpan maxDelay, CancellationToken ct = default);
}
```
```csharp
public sealed record GridCarbonData
{
}
    public required DateTimeOffset Timestamp { get; init; }
    public required double CarbonIntensity { get; init; }
    public required string Region { get; init; }
    public required string Provider { get; init; }
    public double FossilFuelPercent { get; init; }
    public double RenewablePercent { get; init; }
    public double Confidence { get; init; };
}
```
```csharp
public sealed record GridCarbonForecast
{
}
    public required DateTimeOffset Timestamp { get; init; }
    public required double CarbonIntensity { get; init; }
    public double Confidence { get; init; }
}
```
```csharp
public sealed record OptimalScheduleWindow
{
}
    public required DateTimeOffset StartTime { get; init; }
    public required DateTimeOffset EndTime { get; init; }
    public required double AverageCarbonIntensity { get; init; }
    public double CarbonSavingsPercent { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/CarbonAwareness/ScienceBasedTargetsStrategy.cs
```csharp
public sealed class ScienceBasedTargetsStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public string Pathway { get; set; };
    public void SetTarget(int baselineYear, double baselineEmissionsKg, int targetYear, double targetReductionPercent);
    public void RecordYearlyEmissions(int year, double emissionsKg);
    public double GetRequiredEmissionsKg(int year);
    public SbtProgress GetProgress();
}
```
```csharp
public sealed record EmissionRecord
{
}
    public required int Year { get; init; }
    public required double EmissionsKg { get; init; }
}
```
```csharp
public sealed record SbtProgress
{
}
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
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/CarbonAwareness/CarbonIntensityTrackingStrategy.cs
```csharp
public sealed class CarbonIntensityTrackingStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public double CurrentIntensity
{
    get
    {
        lock (_lock)
            return _currentIntensity;
    }
}
    public double Average24Hour
{
    get
    {
        lock (_lock)
            return _24HourAverage;
    }
}
    public TimeSpan PollingInterval { get; set; };
    public string Region { get; set; };
    public string? ApiEndpoint { get; set; }
    public string? ApiKey { get; set; }
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override Task DisposeCoreAsync();
    public void RecordIntensity(double intensityGCO2ePerKwh, DateTimeOffset? timestamp = null);
    public IReadOnlyList<CarbonIntensityDataPoint> GetHistory(TimeSpan? duration = null);
    public LowCarbonWindow? FindNextLowCarbonWindow(TimeSpan lookahead, double thresholdGCO2ePerKwh);
    public CarbonEmission CalculateEmissions(double energyWh);
}
```
```csharp
public sealed record CarbonIntensityDataPoint
{
}
    public required DateTimeOffset Timestamp { get; init; }
    public required double Intensity { get; init; }
    public string Region { get; init; };
}
```
```csharp
public sealed record LowCarbonWindow
{
}
    public required DateTimeOffset StartTime { get; init; }
    public required DateTimeOffset EndTime { get; init; }
    public required double PredictedIntensity { get; init; }
    public double Confidence { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/CarbonAwareness/EmbodiedCarbonStrategy.cs
```csharp
public sealed class EmbodiedCarbonStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public int DefaultLifespanYears { get; set; };
    public void RegisterAsset(string assetId, string assetType, double embodiedCarbonKg, DateTimeOffset purchaseDate, int? lifespanYears = null);
    public double GetDailyCarbonGrams(string assetId);
    public double GetTotalEmbodiedCarbonKg();
    public double GetTotalDailyCarbonGrams();
    public IReadOnlyList<HardwareAsset> GetAssetsNearingEndOfLife(int monthsThreshold = 6);
}
```
```csharp
public sealed class HardwareAsset
{
}
    public required string AssetId { get; init; }
    public required string AssetType { get; init; }
    public required double EmbodiedCarbonKg { get; init; }
    public required DateTimeOffset PurchaseDate { get; init; }
    public required int LifespanYears { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/CarbonAwareness/CarbonOffsettingStrategy.cs
```csharp
public sealed class CarbonOffsettingStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public double TotalEmissionsGrams
{
    get
    {
        lock (_lock)
            return _totalEmissionsGrams;
    }
}
    public double TotalOffsetsGrams
{
    get
    {
        lock (_lock)
            return _totalOffsetsGrams;
    }
}
    public double NetCarbonBalanceGrams
{
    get
    {
        lock (_lock)
            return _totalOffsetsGrams - _totalEmissionsGrams;
    }
}
    public bool IsCarbonNeutral;;
    public double TargetOffsetPercent { get; set; };
    public OffsetProjectType[] PreferredProjectTypes { get; set; };
    public double OffsetCostPerTon { get; set; };
    public void RecordEmission(double emissionsGrams, string source, string? description = null);
    public void RecordOffsetPurchase(double offsetGrams, string provider, OffsetProjectType projectType, string projectName, double costUsd, string? certificateId = null);
    public OffsetRequirement CalculateOffsetRequirement();
    public IReadOnlyDictionary<string, double> GetEmissionsBySource(TimeSpan? period = null);
    public IReadOnlyList<OffsetPurchase> GetOffsetPurchases();
    public CarbonNeutralityReport GenerateReport(TimeSpan period);
    public IReadOnlyList<OffsetProjectRecommendation> GetRecommendedProjects(double offsetGramsNeeded);
}
```
```csharp
public sealed record CarbonFootprintEntry
{
}
    public required string Id { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required double EmissionsGrams { get; init; }
    public required string Source { get; init; }
    public string? Description { get; init; }
}
```
```csharp
public sealed record OffsetPurchase
{
}
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
```
```csharp
public sealed record OffsetRequirement
{
}
    public required double TotalEmissionsGrams { get; init; }
    public required double CurrentOffsetsGrams { get; init; }
    public required double TargetOffsetsGrams { get; init; }
    public required double AdditionalOffsetsNeededGrams { get; init; }
    public required double EstimatedCostUsd { get; init; }
    public required double CurrentOffsetPercent { get; init; }
    public required double TargetOffsetPercent { get; init; }
}
```
```csharp
public sealed record CarbonNeutralityReport
{
}
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
```
```csharp
public sealed record OffsetProjectRecommendation
{
}
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
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/Metrics/WaterUsageTrackingStrategy.cs
```csharp
public sealed class WaterUsageTrackingStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public double TargetWue { get; set; };
    public double AlertThresholdWue { get; set; };
    public double WaterCostPerLiter { get; set; };
    public void SetWaterUsageProvider(Func<Task<double>> provider);;
    public void SetItLoadProvider(Func<Task<double>> provider);;
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task DisposeCoreAsync();
    public void RecordWaterUsage(double waterLitersPerHour, double itLoadKw);
    public double GetCurrentWue();
    public WaterStatistics GetStatistics(TimeSpan? period = null);
    public IReadOnlyList<WaterSavingRecommendation> GetWaterSavingRecommendations();
}
```
```csharp
public sealed record WaterReading
{
}
    public required DateTimeOffset Timestamp { get; init; }
    public required double WaterLitersPerHour { get; init; }
    public required double ItLoadKw { get; init; }
    public required double Wue { get; init; }
    public required double CostPerHour { get; init; }
}
```
```csharp
public sealed record WaterStatistics
{
}
    public double CurrentWue { get; init; }
    public double AverageWue { get; init; }
    public double MinWue { get; init; }
    public double MaxWue { get; init; }
    public double TotalWaterLiters { get; init; }
    public double TotalWaterCostUsd { get; init; }
    public int ReadingCount { get; init; }
}
```
```csharp
public sealed record WaterSavingRecommendation
{
}
    public required WaterSavingType Type { get; init; }
    public required string Description { get; init; }
    public required double EstimatedSavingsLitersPerDay { get; init; }
    public required double EstimatedCostSavingsUsd { get; init; }
    public required int Priority { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/Metrics/CarbonFootprintCalculationStrategy.cs
```csharp
public sealed class CarbonFootprintCalculationStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public double DefaultCarbonIntensity { get; set; };
    public double Scope3Multiplier { get; set; };
    public double TotalScope2Grams
{
    get
    {
        lock (_lock)
            return _totalScope2Grams;
    }
}
    public double TotalScope3Grams
{
    get
    {
        lock (_lock)
            return _totalScope3Grams;
    }
}
    public double TotalEmissionsGrams;;
    public void SetCarbonIntensityProvider(Func<Task<double>> provider);;
    public void SetEnergyProvider(Func<Task<double>> provider);;
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task DisposeCoreAsync();
    public async Task<EmissionResult> CalculateEmissionsAsync(double energyWh);
    public void RecordEmissions(double scope2Grams, double scope3Grams = 0);
    public EmissionsSummary GetSummary();
}
```
```csharp
public sealed record EmissionResult
{
}
    public required double EnergyWh { get; init; }
    public required double CarbonIntensity { get; init; }
    public required double Scope2Grams { get; init; }
    public required double Scope3Grams { get; init; }
    public required double TotalGrams { get; init; }
}
```
```csharp
public sealed record EmissionsSummary
{
}
    public required double TotalScope2Grams { get; init; }
    public required double TotalScope3Grams { get; init; }
    public required double TotalEmissionsGrams { get; init; }
    public required double TotalKgCO2e { get; init; }
    public required double TotalTonsCO2e { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/Metrics/PueTrackingStrategy.cs
```csharp
public sealed class PueTrackingStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public double TargetPue { get; set; };
    public double AlertThresholdPue { get; set; };
    public void SetItLoadProvider(Func<Task<double>> provider);;
    public void SetTotalPowerProvider(Func<Task<double>> provider);;
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task DisposeCoreAsync();
    public void RecordPue(double itLoadKw, double totalPowerKw);
    public double GetCurrentPue();
    public PueStatistics GetStatistics(TimeSpan? period = null);
    public PueTrend GetTrend();
}
```
```csharp
public sealed record PueReading
{
}
    public required DateTimeOffset Timestamp { get; init; }
    public required double ItLoadKw { get; init; }
    public required double TotalPowerKw { get; init; }
    public required double Pue { get; init; }
    public required double OverheadKw { get; init; }
}
```
```csharp
public sealed record PueStatistics
{
}
    public double CurrentPue { get; init; }
    public double AveragePue { get; init; }
    public double MinPue { get; init; }
    public double MaxPue { get; init; }
    public double TotalItLoadKwh { get; init; }
    public double TotalOverheadKwh { get; init; }
    public int ReadingCount { get; init; }
    public TimeSpan TimeAboveTarget { get; init; }
}
```
```csharp
public sealed record PueTrend
{
}
    public bool HasSufficientData { get; init; }
    public double CurrentAverage { get; init; }
    public double PreviousAverage { get; init; }
    public double Change { get; init; }
    public double ChangePercent { get; init; }
    public bool IsImproving { get; init; }
    public bool IsDegrading { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/Metrics/EnergyConsumptionTrackingStrategy.cs
```csharp
public sealed class EnergyConsumptionTrackingStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public TimeSpan SamplingInterval { get; set; };
    public double TotalEnergyWh
{
    get
    {
        lock (_lock)
            return _totalEnergyWh;
    }
}
    public double CurrentPowerWatts
{
    get
    {
        lock (_lock)
            return _currentPowerWatts;
    }
}
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task DisposeCoreAsync();
    public IReadOnlyList<EnergyDataPoint> GetHistory(TimeSpan? duration = null);
    public EnergySummary GetSummary(TimeSpan period);
}
```
```csharp
public sealed record EnergyDataPoint
{
}
    public required DateTimeOffset Timestamp { get; init; }
    public required double PowerWatts { get; init; }
    public required double EnergyWh { get; init; }
    public required string Source { get; init; }
}
```
```csharp
public sealed record EnergySummary
{
}
    public required TimeSpan Period { get; init; }
    public required double TotalEnergyWh { get; init; }
    public double AveragePowerWatts { get; init; }
    public double PeakPowerWatts { get; init; }
    public double MinPowerWatts { get; init; }
    public int SampleCount { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/Metrics/SustainabilityReportingStrategy.cs
```csharp
public sealed class SustainabilityReportingStrategy : SustainabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override SustainabilityCategory Category;;
    public override SustainabilityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public ReportFormat Format { get; set; };
    public string OrganizationName { get; set; };
    public SustainabilityReport GenerateReport(TimeSpan period, double totalEnergyWh, double totalEmissionsGrams, double energySavedWh, double carbonAvoidedGrams, double costSavingsUsd);
    public string ExportReport(SustainabilityReport report, ReportFormat? format = null);
    public IReadOnlyList<SustainabilityReport> GetReportHistory();
}
```
```csharp
public sealed record SustainabilityReport
{
}
    public required string ReportId { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
    public required string Organization { get; init; }
    public required DateTimeOffset PeriodStart { get; init; }
    public required DateTimeOffset PeriodEnd { get; init; }
    public required double TotalEnergyConsumedWh { get; init; }
    public required double TotalEnergyConsumedKwh { get; init; }
    public required double TotalEmissionsGrams { get; init; }
    public required double TotalEmissionsKgCO2e { get; init; }
    public required double TotalEmissionsTonsCO2e { get; init; }
    public required double EnergySavedWh { get; init; }
    public required double EnergySavedKwh { get; init; }
    public required double CarbonAvoidedGrams { get; init; }
    public required double CarbonAvoidedKgCO2e { get; init; }
    public required double CostSavingsUsd { get; init; }
    public required double EfficiencyRatio { get; init; }
    public required double CarbonReductionPercent { get; init; }
    public required double EquivalentTreesPlanted { get; init; }
    public required double EquivalentMilesDriven { get; init; }
    public required double EquivalentHomesEnergy { get; init; }
}
```
```csharp
internal sealed record ReportRecord
{
}
    public required SustainabilityReport Report { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
}
```
