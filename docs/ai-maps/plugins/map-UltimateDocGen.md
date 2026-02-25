# Plugin: UltimateDocGen
> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.
> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.


## Project: DataWarehouse.Plugins.UltimateDocGen

### File: Plugins/DataWarehouse.Plugins.UltimateDocGen/UltimateDocGenPlugin.cs
```csharp
public sealed record DocGenCharacteristics
{
}
    public required string StrategyName { get; init; }
    public required string Description { get; init; }
    public required DocGenCategory Category { get; init; }
    public required DocGenCapabilities Capabilities { get; init; }
    public string[] Tags { get; init; };
}
```
```csharp
public sealed class DocGenRequest
{
}
    public required string OperationId { get; init; }
    public required string SourceType { get; init; }
    public object? Source { get; init; }
    public OutputFormat Format { get; init; };
    public Dictionary<string, object> Options { get; init; };
}
```
```csharp
public sealed class DocGenResult
{
}
    public required string OperationId { get; init; }
    public bool Success { get; init; }
    public string? Content { get; init; }
    public string? Error { get; init; }
    public OutputFormat Format { get; init; }
    public TimeSpan Duration { get; init; }
}
```
```csharp
public abstract class DocGenStrategyBase
{
}
    public abstract DocGenCharacteristics Characteristics { get; }
    public string StrategyId;;
    public abstract Task<DocGenResult> GenerateAsync(DocGenRequest request, CancellationToken ct = default);;
    protected string FormatAsMarkdown(string title, List<(string Name, string Type, string Description)> items);
    protected string FormatAsHtml(string title, List<(string Name, string Type, string Description)> items);
}
```
```csharp
public sealed class DocGenStrategyRegistry
{
}
    public int Count;;
    public IReadOnlyCollection<string> RegisteredStrategies;;
    public void Register(DocGenStrategyBase strategy);;
    public DocGenStrategyBase? Get(string name);;
    public IEnumerable<DocGenStrategyBase> GetAll();;
}
```
```csharp
public sealed class OpenApiDocStrategy : DocGenStrategyBase
{
}
    public override DocGenCharacteristics Characteristics { get; };
    public override Task<DocGenResult> GenerateAsync(DocGenRequest request, CancellationToken ct = default);
}
```
```csharp
public sealed class GraphQLSchemaDocStrategy : DocGenStrategyBase
{
}
    public override DocGenCharacteristics Characteristics { get; };
    public override Task<DocGenResult> GenerateAsync(DocGenRequest request, CancellationToken ct = default);
}
```
```csharp
public sealed class GrpcDocStrategy : DocGenStrategyBase
{
}
    public override DocGenCharacteristics Characteristics { get; };
    public override Task<DocGenResult> GenerateAsync(DocGenRequest request, CancellationToken ct = default);
}
```
```csharp
public sealed class DatabaseSchemaDocStrategy : DocGenStrategyBase
{
}
    public override DocGenCharacteristics Characteristics { get; };
    public override Task<DocGenResult> GenerateAsync(DocGenRequest request, CancellationToken ct = default);
}
```
```csharp
public sealed class JsonSchemaDocStrategy : DocGenStrategyBase
{
}
    public override DocGenCharacteristics Characteristics { get; };
    public override Task<DocGenResult> GenerateAsync(DocGenRequest request, CancellationToken ct = default);
}
```
```csharp
public sealed class MarkdownOutputStrategy : DocGenStrategyBase
{
}
    public override DocGenCharacteristics Characteristics { get; };
    public override Task<DocGenResult> GenerateAsync(DocGenRequest request, CancellationToken ct = default);
}
```
```csharp
public sealed class HtmlOutputStrategy : DocGenStrategyBase
{
}
    public override DocGenCharacteristics Characteristics { get; };
    public override Task<DocGenResult> GenerateAsync(DocGenRequest request, CancellationToken ct = default);
}
```
```csharp
public sealed class ChangeLogDocStrategy : DocGenStrategyBase
{
}
    public override DocGenCharacteristics Characteristics { get; };
    public override Task<DocGenResult> GenerateAsync(DocGenRequest request, CancellationToken ct = default);
}
```
```csharp
public sealed class InteractiveDocStrategy : DocGenStrategyBase
{
}
    public override DocGenCharacteristics Characteristics { get; };
    public override Task<DocGenResult> GenerateAsync(DocGenRequest request, CancellationToken ct = default);
}
```
```csharp
public sealed class AIEnhancedDocStrategy : DocGenStrategyBase
{
}
    public override DocGenCharacteristics Characteristics { get; };
    public override Task<DocGenResult> GenerateAsync(DocGenRequest request, CancellationToken ct = default);
}
```
```csharp
public sealed class UltimateDocGenPlugin : PlatformPluginBase, IDisposable
{
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override string PlatformDomain;;
    public override PluginCategory Category;;
    public string SemanticDescription;;
    public string[] SemanticTags;;
    public DocGenStrategyRegistry Registry;;
    public UltimateDocGenPlugin();
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request);
    protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct);
    protected override async Task OnStartWithoutIntelligenceAsync(CancellationToken ct);
    protected override async Task OnStopCoreAsync();
    public async Task<DocGenResult> GenerateDocumentationAsync(DocGenRequest request, CancellationToken ct = default);
    public override async Task OnMessageAsync(PluginMessage message);
    protected override List<PluginCapabilityDescriptor> GetCapabilities();;
    protected override Dictionary<string, object> GetMetadata();
    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities;;
    protected override void Dispose(bool disposing);
}
```
