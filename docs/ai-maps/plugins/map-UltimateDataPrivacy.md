# Plugin: UltimateDataPrivacy
> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.
> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.


## Project: DataWarehouse.Plugins.UltimateDataPrivacy

### File: Plugins/DataWarehouse.Plugins.UltimateDataPrivacy/DataPrivacyStrategyBase.cs
```csharp
public sealed record DataPrivacyCapabilities
{
}
    public bool SupportsAsync { get; init; }
    public bool SupportsBatch { get; init; }
    public bool SupportsReversible { get; init; }
    public bool SupportsFormatPreserving { get; init; }
}
```
```csharp
public interface IDataPrivacyStrategy
{
}
    string StrategyId { get; }
    string DisplayName { get; }
    PrivacyCategory Category { get; }
    DataPrivacyCapabilities Capabilities { get; }
    string SemanticDescription { get; }
    string[] Tags { get; }
}
```
```csharp
public abstract class DataPrivacyStrategyBase : StrategyBase, IDataPrivacyStrategy
{
}
    public abstract override string StrategyId { get; }
    public abstract string DisplayName { get; }
    public override string Name;;
    public abstract PrivacyCategory Category { get; }
    public abstract DataPrivacyCapabilities Capabilities { get; }
    public abstract string SemanticDescription { get; }
    public abstract string[] Tags { get; }
    protected override async Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public bool IsHealthy();
    public IReadOnlyDictionary<string, long> GetCounters();;
}
```
```csharp
public sealed class DataPrivacyStrategyRegistry
{
}
    public int Count;;
    public int AutoDiscover(System.Reflection.Assembly assembly);
    public IDataPrivacyStrategy? Get(string strategyId);;
    public IReadOnlyList<IDataPrivacyStrategy> GetAll();;
    public IReadOnlyList<IDataPrivacyStrategy> GetByCategory(PrivacyCategory category);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataPrivacy/UltimateDataPrivacyPlugin.cs
```csharp
public sealed class UltimateDataPrivacyPlugin : SecurityPluginBase, IDisposable
{
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override string SecurityDomain;;
    public override PluginCategory Category;;
    public string SemanticDescription;;
    public string[] SemanticTags;;
    public DataPrivacyStrategyRegistry Registry;;
    public bool AuditEnabled { get => _auditEnabled; set => _auditEnabled = value; }
    public UltimateDataPrivacyPlugin();
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request);
    protected override List<PluginCapabilityDescriptor> GetCapabilities();;
    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities
{
    get
    {
        var capabilities = new List<RegisteredCapability>
        {
            new()
            {
                CapabilityId = "dataprivacy",
                DisplayName = "Ultimate Data Privacy",
                Description = SemanticDescription,
                Category = SDK.Contracts.CapabilityCategory.Security,
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = SemanticTags
            }
        };
        foreach (var strategy in _registry.GetAll())
        {
            var tags = new List<string>
            {
                "privacy",
                strategy.Category.ToString().ToLowerInvariant()
            };
            tags.AddRange(strategy.Tags);
            capabilities.Add(new() { CapabilityId = $"privacy.{strategy.StrategyId}", DisplayName = strategy.DisplayName, Description = strategy.SemanticDescription, Category = SDK.Contracts.CapabilityCategory.Security, SubCategory = strategy.Category.ToString(), PluginId = Id, PluginName = Name, PluginVersion = Version, Tags = tags.ToArray(), Metadata = new Dictionary<string, object> { ["category"] = strategy.Category.ToString(), ["supportsAsync"] = strategy.Capabilities.SupportsAsync, ["supportsReversible"] = strategy.Capabilities.SupportsReversible, ["supportsFormatPreserving"] = strategy.Capabilities.SupportsFormatPreserving }, SemanticDescription = strategy.SemanticDescription });
        }

        return capabilities.AsReadOnly();
    }
}
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge();
    protected override Dictionary<string, object> GetMetadata();
    public override Task OnMessageAsync(PluginMessage message);;
    protected override async Task OnStartCoreAsync(CancellationToken ct);
    protected override async Task OnBeforeStatePersistAsync(CancellationToken ct);
    protected override void Dispose(bool disposing);
}
```
```csharp
public sealed record PrivacyPolicy
{
}
    public required string Id { get; init; }
    public required string Name { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
```
```csharp
public sealed record ConsentRecord
{
}
    public required string Id { get; init; }
    public required string UserId { get; init; }
    public required string ConsentType { get; init; }
    public bool Granted { get; init; }
    public DateTimeOffset RecordedAt { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataPrivacy/Strategies/Anonymization/AnonymizationStrategies.cs
```csharp
public sealed class KAnonymityStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public AnonymizationResult Anonymize(List<Dictionary<string, object>> records, string[] quasiIdentifiers, int k);
}
```
```csharp
public sealed record AnonymizationResult
{
}
    public int OriginalCount { get; init; }
    public int AnonymizedCount { get; init; }
    public int SuppressedCount { get; init; }
    public List<Dictionary<string, object>> AnonymizedRecords { get; init; };
}
```
```csharp
public sealed class LDiversityStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public AnonymizationResult Anonymize(List<Dictionary<string, object>> records, string[] quasiIdentifiers, string sensitiveAttribute, int l);
}
```
```csharp
public sealed class TClosenessStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public AnonymizationResult Anonymize(List<Dictionary<string, object>> records, string[] quasiIdentifiers, string sensitiveAttribute, double t);
}
```
```csharp
public sealed class DataSuppressionStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class GeneralizationStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataSwappingStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataPerturbationStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class TopBottomCodingStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class SyntheticDataGenerationStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataPrivacy/Strategies/DifferentialPrivacy/DifferentialPrivacyEnhancedStrategies.cs
```csharp
public sealed class EpsilonDeltaTrackingStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public PrivacyBudget InitializeBudget(string datasetId, double totalEpsilon, double totalDelta);
    public PrivacyQueryResult ConsumePrivacy(string datasetId, double queryEpsilon, double queryDelta, string queryDescription);
    public PrivacyBudget? GetBudget(string datasetId);;
    public IReadOnlyList<PrivacyQuery> GetQueryHistory(string datasetId);;
    public bool ResetBudget(string datasetId);
}
```
```csharp
public sealed class FederatedAnalyticsStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public FederatedSession CreateSession(string queryId, FederatedQueryType queryType, string[] participantIds);
    public FederatedContributionResult SubmitContribution(string sessionId, string participantId, double value);
    public FederatedResult ComputeAggregate(string sessionId, double noiseEpsilon = 1.0);
}
```
```csharp
public sealed class SyntheticDataGenerationStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public SyntheticDataResult Generate(SyntheticDataRequest request);
}
```
```csharp
public sealed class PiiDetectionStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public PiiScanResult Scan(string text, double minConfidence = 0.5);
    public PiiColumnScanResult ScanColumns(Dictionary<string, string> columns, double minConfidence = 0.5);
}
```
```csharp
public sealed record PrivacyBudget
{
}
    public required string DatasetId { get; init; }
    public double TotalEpsilon { get; init; }
    public double TotalDelta { get; init; }
    public double ConsumedEpsilon { get; init; }
    public double ConsumedDelta { get; init; }
    public double RemainingEpsilon { get; init; }
    public double RemainingDelta { get; init; }
    public int QueryCount { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public bool IsExhausted { get; init; }
}
```
```csharp
public sealed record PrivacyQuery
{
}
    public required string QueryId { get; init; }
    public required string DatasetId { get; init; }
    public double Epsilon { get; init; }
    public double Delta { get; init; }
    public required string Description { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}
```
```csharp
public sealed record PrivacyQueryResult
{
}
    public bool Allowed { get; init; }
    public string? Reason { get; init; }
    public string? QueryId { get; init; }
    public double ConsumedEpsilon { get; init; }
    public double ConsumedDelta { get; init; }
    public double RemainingEpsilon { get; init; }
    public double RemainingDelta { get; init; }
    public double BudgetUtilization { get; init; }
}
```
```csharp
public sealed record FederatedSession
{
}
    public required string SessionId { get; init; }
    public required string QueryId { get; init; }
    public FederatedQueryType QueryType { get; init; }
    public required string[] ParticipantIds { get; init; }
    public BoundedDictionary<string, double> Contributions { get; init; };
    public FederatedSessionStatus Status { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
```
```csharp
public sealed record FederatedContributionResult
{
}
    public bool Accepted { get; init; }
    public string? Reason { get; init; }
    public int ContributionsReceived { get; init; }
    public int TotalExpected { get; init; }
    public bool IsComplete { get; init; }
}
```
```csharp
public sealed record FederatedResult
{
}
    public bool Success { get; init; }
    public string? Error { get; init; }
    public double RawResult { get; init; }
    public double NoisyResult { get; init; }
    public double NoiseAdded { get; init; }
    public double Epsilon { get; init; }
    public int ParticipantCount { get; init; }
}
```
```csharp
public sealed record SyntheticDataRequest
{
}
    public int RecordCount { get; init; };
    public List<SyntheticColumnSpec> ColumnSpecs { get; init; };
    public int? Seed { get; init; }
}
```
```csharp
public sealed record SyntheticColumnSpec
{
}
    public required string Name { get; init; }
    public SyntheticColumnType Type { get; init; }
    public double? Mean { get; init; }
    public double? StdDev { get; init; }
    public string[]? Categories { get; init; }
    public DateTimeOffset? MinDate { get; init; }
    public DateTimeOffset? MaxDate { get; init; }
    public string? TextPattern { get; init; }
    public int? TextLength { get; init; }
    public double? TrueProbability { get; init; }
}
```
```csharp
public sealed record SyntheticDataResult
{
}
    public List<Dictionary<string, object>> Records { get; init; };
    public int RecordCount { get; init; }
    public int ColumnCount { get; init; }
    public DateTimeOffset GeneratedAt { get; init; }
    public int? Seed { get; init; }
}
```
```csharp
public sealed record PiiDetection
{
}
    public PiiType Type { get; init; }
    public required string Value { get; init; }
    public int Position { get; init; }
    public int Length { get; init; }
    public double Confidence { get; init; }
    public required string Category { get; init; }
}
```
```csharp
public sealed record PiiScanResult
{
}
    public List<PiiDetection> Detections { get; init; };
    public int TotalDetections { get; init; }
    public int HighConfidenceCount { get; init; }
    public DateTimeOffset ScannedAt { get; init; }
    public int TextLength { get; init; }
}
```
```csharp
public sealed record PiiColumnScanResult
{
}
    public Dictionary<string, PiiScanResult> ColumnResults { get; init; };
    public List<string> PiiColumnNames { get; init; };
    public int TotalColumnsScanned { get; init; }
    public int ColumnsWithPii { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataPrivacy/Strategies/DifferentialPrivacy/DifferentialPrivacyStrategies.cs
```csharp
public sealed class LaplaceNoiseStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class GaussianNoiseStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ExponentialMechanismStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class PrivacyBudgetManagementStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class CompositionAnalysisStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class LocalDifferentialPrivacyStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class GlobalDifferentialPrivacyStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ApproximateDPStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataPrivacy/Strategies/Masking/MaskingStrategies.cs
```csharp
public sealed class StaticDataMaskingStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DynamicDataMaskingStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DeterministicMaskingStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ConditionalMaskingStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class PartialMaskingStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class RedactionMaskingStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class SubstitutionMaskingStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ShufflingMaskingStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataPrivacy/Strategies/PrivacyCompliance/PrivacyComplianceStrategies.cs
```csharp
public sealed class GDPRRightToErasureStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class GDPRRightToAccessStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class GDPRDataPortabilityStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class CCPAOptOutStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ConsentCollectionStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ConsentWithdrawalStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataProcessingRecordStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class PrivacyImpactAssessmentStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataPrivacy/Strategies/PrivacyMetrics/PrivacyMetricsStrategies.cs
```csharp
public sealed class ReIdentificationRiskStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class PrivacyScoringStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataUtilityMeasurementStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class PrivacyUtilityTradeoffStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class SensitivityAnalysisStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class LinkageRiskAssessmentStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class PrivacyBudgetConsumptionStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class AnonymityMeasurementStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataPrivacy/Strategies/PrivacyPreservingAnalytics/PrivacyPreservingAnalyticsStrategies.cs
```csharp
public sealed class SecureAggregationStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class HomomorphicEncryptionAnalyticsStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class FederatedLearningStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class SecureMultiPartyComputationStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class SyntheticDataAnalyticsStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class PrivateInformationRetrievalStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class PrivateSetIntersectionStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ConfidentialComputingStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataPrivacy/Strategies/Pseudonymization/PseudonymizationStrategies.cs
```csharp
public sealed class DeterministicPseudonymizationStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class FormatPreservingPseudonymizationStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ReversiblePseudonymizationStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class HashBasedPseudonymizationStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class EncryptionBasedPseudonymizationStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class PseudonymMappingStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class KeyedPseudonymizationStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class CrossDatasetPseudonymizationStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataPrivacy/Strategies/Tokenization/TokenizationStrategies.cs
```csharp
public sealed class VaultedTokenizationStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class VaultlessTokenizationStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class FormatPreservingTokenizationStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class PCICompliantTokenizationStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DeterministicTokenizationStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class RandomTokenizationStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class TokenLifecycleStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class HighValueTokenizationStrategy : DataPrivacyStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override PrivacyCategory Category;;
    public override DataPrivacyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
