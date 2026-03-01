# Plugin: UltimateDataFormat
> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.
> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.


## Project: DataWarehouse.Plugins.UltimateDataFormat

### File: Plugins/DataWarehouse.Plugins.UltimateDataFormat/UltimateDataFormatPlugin.cs
```csharp
public sealed class UltimateDataFormatPlugin : FormatPluginBase, IDisposable
{
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override string FormatFamily;;
    public override PluginCategory Category;;
    public string SemanticDescription;;
    public string[] SemanticTags;;
    public int StrategyCount;;
    public bool AuditEnabled { get => _auditEnabled; set => _auditEnabled = value; }
    public bool AutoOptimizationEnabled { get => _autoOptimizationEnabled; set => _autoOptimizationEnabled = value; }
    public UltimateDataFormatPlugin();
    public IDataFormatStrategy? GetStrategy(string strategyId);
    public IEnumerable<IDataFormatStrategy> GetStrategiesByDomain(DomainFamily domain);
    public IEnumerable<IDataFormatStrategy> GetAllStrategies();
    public async Task<IDataFormatStrategy?> DetectFormat(Stream stream, CancellationToken ct = default);
    public async Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default);
    public async Task<DataFormatResult> SerializeAsync(string strategyId, object data, Stream output, DataFormatContext context, CancellationToken ct = default);
    public async Task<DataFormatResult> ConvertAsync(Stream input, string sourceStrategyId, string targetStrategyId, Stream output, DataFormatContext context, CancellationToken ct = default);
    public DataFormatStatistics GetStatistics();;
    protected override void Dispose(bool disposing);
}
```
```csharp
public sealed record DataFormatStatistics
{
}
    public long TotalOperations { get; init; }
    public long TotalBytesProcessed { get; init; }
    public long TotalFailures { get; init; }
    public int RegisteredStrategies { get; init; }
    public Dictionary<string, long> UsageByStrategy { get; init; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataFormat/Strategies/Graph/RdfStrategy.cs
```csharp
public sealed class RdfStrategy : DataFormatStrategyBase
{
#endregion
}
    public override string StrategyId;;
    public override string DisplayName;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    public override DataFormatCapabilities Capabilities;;
    public override FormatInfo FormatInfo;;
    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct);
    public override async Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default);
    public override async Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default);
    protected override async Task<FormatSchema?> ExtractSchemaCoreAsync(Stream stream, CancellationToken ct);
    protected override Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct);
}
```
```csharp
public sealed class RdfTriple
{
}
    public required string Subject { get; init; }
    public required string Predicate { get; init; }
    public required string Object { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataFormat/Strategies/Graph/GraphMlStrategy.cs
```csharp
public sealed class GraphMlStrategy : DataFormatStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    public override DataFormatCapabilities Capabilities;;
    public override FormatInfo FormatInfo;;
    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct);
    public override async Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default);
    public override async Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default);
    protected override async Task<FormatSchema?> ExtractSchemaCoreAsync(Stream stream, CancellationToken ct);
    protected override async Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct);
}
```
```csharp
public sealed class GraphMlGraph
{
}
    public List<GraphMlKey> Keys { get; set; };
    public List<GraphMlNode> Nodes { get; set; };
    public List<GraphMlEdge> Edges { get; set; };
}
```
```csharp
public sealed class GraphMlKey
{
}
    public required string Id { get; init; }
    public required string For { get; init; }
    public required string AttrName { get; init; }
    public required string AttrType { get; init; }
}
```
```csharp
public sealed class GraphMlNode
{
}
    public required string Id { get; init; }
    public Dictionary<string, string> Data { get; set; };
}
```
```csharp
public sealed class GraphMlEdge
{
}
    public string? Id { get; init; }
    public required string Source { get; init; }
    public required string Target { get; init; }
    public bool Directed { get; init; }
    public Dictionary<string, string> Data { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataFormat/Strategies/Lakehouse/DeltaLakeStrategy.cs
```csharp
public sealed class DeltaLakeStrategy : DataFormatStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    public override DataFormatCapabilities Capabilities;;
    public override FormatInfo FormatInfo;;
    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct);
    public override Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default);
    public override Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default);
    protected override async Task<FormatSchema?> ExtractSchemaCoreAsync(Stream stream, CancellationToken ct);
    protected override async Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataFormat/Strategies/Lakehouse/IcebergStrategy.cs
```csharp
public sealed class IcebergStrategy : DataFormatStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    public override DataFormatCapabilities Capabilities;;
    public override FormatInfo FormatInfo;;
    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct);
    public override Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default);
    public override Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default);
    protected override async Task<FormatSchema?> ExtractSchemaCoreAsync(Stream stream, CancellationToken ct);
    protected override async Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataFormat/Strategies/Schema/AvroStrategy.cs
```csharp
public sealed class AvroStrategy : DataFormatStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    public override DataFormatCapabilities Capabilities;;
    public override FormatInfo FormatInfo;;
    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct);
    public override Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default);
    public override Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default);
    protected override async Task<FormatSchema?> ExtractSchemaCoreAsync(Stream stream, CancellationToken ct);
    protected override Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataFormat/Strategies/Schema/ThriftStrategy.cs
```csharp
public sealed class ThriftStrategy : DataFormatStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    public override DataFormatCapabilities Capabilities;;
    public override FormatInfo FormatInfo;;
    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct);
    public override Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default);
    public override Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default);
    protected override Task<FormatSchema?> ExtractSchemaCoreAsync(Stream stream, CancellationToken ct);
    protected override Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataFormat/Strategies/Scientific/PointCloudStrategy.cs
```csharp
public sealed class PointCloudStrategy : DataFormatStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    public override DataFormatCapabilities Capabilities;;
    public override FormatInfo FormatInfo;;
    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct);
    public override async Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default);
    public override Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default);;
    protected override Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataFormat/Strategies/Scientific/NetCdfStrategy.cs
```csharp
public sealed class NetCdfStrategy : DataFormatStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    public override DataFormatCapabilities Capabilities;;
    public override FormatInfo FormatInfo;;
    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct);
    public override Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default);
    public override Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default);
    protected override async Task<FormatSchema?> ExtractSchemaCoreAsync(Stream stream, CancellationToken ct);
    protected override async Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataFormat/Strategies/Scientific/Hdf5Strategy.cs
```csharp
public sealed class Hdf5Strategy : DataFormatStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    public override DataFormatCapabilities Capabilities;;
    public override FormatInfo FormatInfo;;
    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct);
    public override Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default);
    public override Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default);
    protected override async Task<FormatSchema?> ExtractSchemaCoreAsync(Stream stream, CancellationToken ct);
    protected override async Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataFormat/Strategies/Scientific/FitsStrategy.cs
```csharp
public sealed class FitsStrategy : DataFormatStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    public override DataFormatCapabilities Capabilities;;
    public override FormatInfo FormatInfo;;
    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct);
    public override Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default);
    public override Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default);
    protected override async Task<FormatSchema?> ExtractSchemaCoreAsync(Stream stream, CancellationToken ct);
    protected override async Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataFormat/Strategies/Scientific/ProcessMiningStrategy.cs
```csharp
public sealed class ProcessMiningStrategy : DataFormatStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    public override DataFormatCapabilities Capabilities;;
    public override FormatInfo FormatInfo;;
    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct);
    public override async Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default);
    public override Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default);;
    protected override Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct);;
    public AlphaAlgorithmResult DiscoverProcessModel(Dictionary<string, int> directlyFollowsGraph, HashSet<string> activities);
}
```
```csharp
public sealed class AlphaAlgorithmResult
{
}
    public required List<string> Activities { get; init; }
    public required List<string> CausalRelations { get; init; }
    public required List<string> ParallelRelations { get; init; }
    public required List<string> ChoiceRelations { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataFormat/Strategies/Text/XmlStrategy.cs
```csharp
public sealed class XmlStrategy : DataFormatStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    public override DataFormatCapabilities Capabilities;;
    public override FormatInfo FormatInfo;;
    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct);
    public override async Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default);
    public override async Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default);
    protected override async Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataFormat/Strategies/Text/YamlStrategy.cs
```csharp
public sealed class YamlStrategy : DataFormatStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    public override DataFormatCapabilities Capabilities;;
    public override FormatInfo FormatInfo;;
    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct);
    public override async Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default);
    public override async Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default);
    protected override async Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataFormat/Strategies/Text/CsvStrategy.cs
```csharp
public sealed class CsvStrategy : DataFormatStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    public override DataFormatCapabilities Capabilities;;
    public override FormatInfo FormatInfo;;
    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct);
    public override async Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default);
    public override async Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default);
    protected override Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataFormat/Strategies/Text/JsonStrategy.cs
```csharp
public sealed class JsonStrategy : DataFormatStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    public override DataFormatCapabilities Capabilities;;
    public override FormatInfo FormatInfo;;
    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct);
    public override async Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default);
    public override async Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default);
    protected override async Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataFormat/Strategies/Text/TomlStrategy.cs
```csharp
public sealed class TomlStrategy : DataFormatStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    public override DataFormatCapabilities Capabilities;;
    public override FormatInfo FormatInfo;;
    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct);
    public override async Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default);
    public override async Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default);
    protected override Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataFormat/Strategies/ML/PmmlStrategy.cs
```csharp
public sealed class PmmlStrategy : DataFormatStrategyBase
{
#endregion
}
    public override string StrategyId;;
    public override string DisplayName;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    public override DataFormatCapabilities Capabilities;;
    public override FormatInfo FormatInfo;;
    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct);
    public override async Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default);
    public override async Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default);
    protected override async Task<FormatSchema?> ExtractSchemaCoreAsync(Stream stream, CancellationToken ct);
    protected override async Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct);
}
```
```csharp
public sealed class PmmlModel
{
}
    public required string Version { get; init; }
    public required string Application { get; init; }
    public required string Timestamp { get; init; }
    public required List<PmmlDataField> DataFields { get; init; }
    public required string ModelType { get; init; }
}
```
```csharp
public sealed class PmmlDataField
{
}
    public required string Name { get; init; }
    public required string Optype { get; init; }
    public required string DataType { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataFormat/Strategies/Simulation/VtkStrategy.cs
```csharp
public sealed class VtkStrategy : DataFormatStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override bool IsProductionReady;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    public override DataFormatCapabilities Capabilities;;
    public override FormatInfo FormatInfo;;
    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct);
    public override Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default);
    public override Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default);
    protected override async Task<FormatSchema?> ExtractSchemaCoreAsync(Stream stream, CancellationToken ct);
    protected override async Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataFormat/Strategies/Simulation/CgnsStrategy.cs
```csharp
public sealed class CgnsStrategy : DataFormatStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override bool IsProductionReady;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    public override DataFormatCapabilities Capabilities;;
    public override FormatInfo FormatInfo;;
    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct);
    public override Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default);
    public override Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default);
    protected override async Task<FormatSchema?> ExtractSchemaCoreAsync(Stream stream, CancellationToken ct);
    protected override async Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataFormat/Strategies/AI/OnnxStrategy.cs
```csharp
public sealed class OnnxStrategy : DataFormatStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override bool IsProductionReady;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    public override DataFormatCapabilities Capabilities;;
    public override FormatInfo FormatInfo;;
    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct);
    public override Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default);
    public override Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default);
    protected override async Task<FormatSchema?> ExtractSchemaCoreAsync(Stream stream, CancellationToken ct);
    protected override async Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataFormat/Strategies/AI/SafeTensorsStrategy.cs
```csharp
public sealed class SafeTensorsStrategy : DataFormatStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    public override DataFormatCapabilities Capabilities;;
    public override FormatInfo FormatInfo;;
    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct);
    public override async Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default);
    public override async Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default);
    protected override async Task<FormatSchema?> ExtractSchemaCoreAsync(Stream stream, CancellationToken ct);
    protected override async Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct);
}
```
```csharp
public sealed class SafeTensor
{
}
    public required string Name { get; init; }
    public required string Dtype { get; init; }
    public required long[] Shape { get; init; }
    public required byte[] Data { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataFormat/Strategies/Binary/MessagePackStrategy.cs
```csharp
public sealed class MessagePackStrategy : DataFormatStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    public override DataFormatCapabilities Capabilities;;
    public override FormatInfo FormatInfo;;
    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct);
    public override async Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default);
    public override async Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default);
    protected override async Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataFormat/Strategies/Binary/ProtobufStrategy.cs
```csharp
public sealed class ProtobufStrategy : DataFormatStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override bool IsProductionReady;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    public override DataFormatCapabilities Capabilities;;
    public override FormatInfo FormatInfo;;
    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct);
    public override Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default);
    public override Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default);
    protected override async Task<FormatSchema?> ExtractSchemaCoreAsync(Stream stream, CancellationToken ct);
    protected override Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataFormat/Strategies/Geo/KmlStrategy.cs
```csharp
public sealed class KmlStrategy : DataFormatStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    public override DataFormatCapabilities Capabilities;;
    public override FormatInfo FormatInfo;;
    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct);
    public override async Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default);
    public override async Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default);
    protected override async Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataFormat/Strategies/Geo/GeoJsonStrategy.cs
```csharp
public sealed class GeoJsonStrategy : DataFormatStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    public override DataFormatCapabilities Capabilities;;
    public override FormatInfo FormatInfo;;
    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct);
    public override async Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default);
    public override async Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default);
    protected override async Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataFormat/Strategies/Geo/ShapefileStrategy.cs
```csharp
public sealed class ShapefileStrategy : DataFormatStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    public override DataFormatCapabilities Capabilities;;
    public override FormatInfo FormatInfo;;
    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct);
    public override Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default);
    public override Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default);
    protected override async Task<FormatSchema?> ExtractSchemaCoreAsync(Stream stream, CancellationToken ct);
    protected override async Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataFormat/Strategies/Geo/GeoTiffStrategy.cs
```csharp
public sealed class GeoTiffStrategy : DataFormatStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override bool IsProductionReady;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    public override DataFormatCapabilities Capabilities;;
    public override FormatInfo FormatInfo;;
    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct);
    public override Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default);
    public override Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default);
    protected override async Task<FormatSchema?> ExtractSchemaCoreAsync(Stream stream, CancellationToken ct);
    protected override async Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataFormat/Strategies/Columnar/ParquetStrategy.cs
```csharp
public sealed class ParquetStrategy : DataFormatStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    public override DataFormatCapabilities Capabilities;;
    public override FormatInfo FormatInfo;;
    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct);
    public override async Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default);
    public override async Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default);
    protected override async Task<FormatSchema?> ExtractSchemaCoreAsync(Stream stream, CancellationToken ct);
    protected override async Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct);
    public sealed class RowGroupColumnStatistics;
    public sealed class RowGroupStatistics;
    public static RowGroupStatistics ComputeRowGroupStatistics(ColumnarBatch batch, int rowOffset, int rowCount);
    public static bool SkipRowGroup(RowGroupStatistics stats, FilterPredicate? predicate);
}
```
```csharp
public sealed class RowGroupColumnStatistics
{
}
    public string ColumnName { get; init; };
    public ColumnDataType DataType { get; init; }
    public object? MinValue { get; init; }
    public object? MaxValue { get; init; }
    public long NullCount { get; init; }
    public long ValueCount { get; init; }
}
```
```csharp
public sealed class RowGroupStatistics
{
}
    public IReadOnlyList<RowGroupColumnStatistics> ColumnStats { get; init; };
    public int RowCount { get; init; }
}
```
```csharp
public sealed class FilterPredicate
{
}
    public string ColumnName { get; init; };
    public FilterOperator Operator { get; init; }
    public object Value { get; init; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataFormat/Strategies/Columnar/ColumnarFormatVerification.cs
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 89: Columnar format verification (ECOS-03/04/05)")]
public sealed class ColumnarFormatVerification
{
}
    public sealed class FormatDataTypeCoverage;
    public sealed class TypeCompatibilityEntry;
    public sealed class ColumnarFormatCoverage;
    public static ColumnarFormatCoverage GetFormatCoverage();
    public sealed class RoundTripResult;
    public static async Task<RoundTripResult> VerifyRoundTrip(DataFormatStrategyBase strategy, ColumnarBatch testBatch, CancellationToken ct = default);
    public static ColumnarBatch CreateTestBatch();
    public sealed class ExternalToolCompatibility;
    public enum CompatibilityStatus;
    public static IReadOnlyList<ExternalToolCompatibility> GetExternalToolCompatibility();
}
```
```csharp
public sealed class FormatDataTypeCoverage
{
}
    public string FormatId { get; init; };
    public IReadOnlyList<ColumnDataType> SupportedDataTypes { get; init; };
    public IReadOnlyList<string> SupportedCompressionCodecs { get; init; };
    public bool SupportsStatistics { get; init; }
    public bool SupportsColumnPruning { get; init; }
    public bool SupportsZeroCopyReads { get; init; }
}
```
```csharp
public sealed class TypeCompatibilityEntry
{
}
    public ColumnDataType DataType { get; init; }
    public string ParquetNativeType { get; init; };
    public string ArrowNativeType { get; init; };
    public string OrcNativeType { get; init; };
    public bool UniversallySupported { get; init; }
}
```
```csharp
public sealed class ColumnarFormatCoverage
{
}
    public IReadOnlyList<FormatDataTypeCoverage> FormatCoverage { get; init; };
    public IReadOnlyList<TypeCompatibilityEntry> TypeCompatibilityMatrix { get; init; };
}
```
```csharp
public sealed class RoundTripResult
{
}
    public bool Success { get; init; }
    public string[] Differences { get; init; };
}
```
```csharp
public sealed class ExternalToolCompatibility
{
}
    public string ToolName { get; init; };
    public string FormatId { get; init; };
    public CompatibilityStatus Status { get; init; }
    public string Notes { get; init; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataFormat/Strategies/Columnar/ArrowStrategy.cs
```csharp
public sealed class ArrowStrategy : DataFormatStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    public bool SupportsArrowFlight;;
    public override DataFormatCapabilities Capabilities;;
    public override FormatInfo FormatInfo;;
    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct);
    public override async Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default);
    public override Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default);
    public byte[] AsArrowFlightPayload(ColumnarBatch batch);
    protected override async Task<FormatSchema?> ExtractSchemaCoreAsync(Stream stream, CancellationToken ct);
    protected override async Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct);
    public sealed class ArrowFieldDescriptor;
    public enum ArrowTypeId : byte;
}
```
```csharp
public sealed class ArrowFieldDescriptor
{
}
    public string Name { get; init; };
    public ColumnDataType DataType { get; init; }
    public bool IsNullable { get; init; };
    public ArrowTypeId TypeId { get; init; }
}
```
```csharp
private sealed class ArrowParseResult
{
}
    public ColumnarBatch Batch { get; init; };
    public IReadOnlyList<ArrowFieldDescriptor> Schema { get; init; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataFormat/Strategies/Columnar/OrcStrategy.cs
```csharp
public sealed class OrcStrategy : DataFormatStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    public override DataFormatCapabilities Capabilities;;
    public override FormatInfo FormatInfo;;
    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct);
    public override async Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default);
    public override Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default);
    protected override async Task<FormatSchema?> ExtractSchemaCoreAsync(Stream stream, CancellationToken ct);
    protected override async Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct);
    public enum OrcCompressionCodec : byte;
    public enum OrcType : byte;
    public sealed class OrcFieldDescriptor;
    public sealed record StripeColumnStatistics;
    public sealed record StripeStatistics;
    public static StripeStatistics ComputeStripeStatistics(ColumnarBatch batch, int stripeIndex, int rowOffset, int rowCount);
}
```
```csharp
public sealed class OrcFieldDescriptor
{
}
    public string Name { get; init; };
    public ColumnDataType DataType { get; init; }
    public OrcType OrcType { get; init; }
}
```
```csharp
public sealed record StripeColumnStatistics
{
}
    public string ColumnName { get; init; };
    public object? Min { get; init; }
    public object? Max { get; init; }
    public long Count { get; init; }
    public bool HasNull { get; init; }
}
```
```csharp
public sealed record StripeStatistics
{
}
    public IReadOnlyList<StripeColumnStatistics> ColumnStats { get; init; };
    public int RowCount { get; init; }
    public int StripeIndex { get; init; }
}
```
```csharp
private sealed class OrcParseResult
{
}
    public ColumnarBatch Batch { get; init; };
    public IReadOnlyList<OrcFieldDescriptor> Schema { get; init; };
    public int StripeCount { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataFormat/Strategies/Columnar/ArrowFlightStrategy.cs
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 89: Arrow Flight protocol (ECOS-06)")]
public sealed record FlightDescriptor(FlightDescriptorType Type, string[] Path, string? Command)
{
}
    public static FlightDescriptor ForPath(params string[] path);;
    public static FlightDescriptor ForCommand(string command);;
}
```
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 89: Arrow Flight protocol (ECOS-06)")]
public sealed class ArrowFlightStrategy : DataFormatStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);;
    public override DataFormatCapabilities Capabilities;;
    public override FormatInfo FormatInfo;;
    public Task<FlightInfo> GetFlightInfo(FlightDescriptor descriptor, CancellationToken ct = default);
    public Task<ArrowSchema> GetSchema(FlightDescriptor descriptor, CancellationToken ct = default);
    public async IAsyncEnumerable<ArrowRecordBatch> DoGet(FlightTicket ticket, [EnumeratorCancellation] CancellationToken ct = default);
    public async Task DoPut(FlightDescriptor descriptor, IAsyncEnumerable<ArrowRecordBatch> batches, CancellationToken ct = default);
    public async IAsyncEnumerable<ArrowRecordBatch> DoExchange(FlightDescriptor descriptor, IAsyncEnumerable<ArrowRecordBatch> input, [EnumeratorCancellation] CancellationToken ct = default);
    public async IAsyncEnumerable<FlightInfo> ListFlights(FlightCriteria criteria, [EnumeratorCancellation] CancellationToken ct = default);
    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct);
    public override async Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default);
    public override async Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default);
    protected override async Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct);
}
```
