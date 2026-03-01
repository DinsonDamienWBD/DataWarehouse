# Plugin: UltimateInterface
> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.
> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.


## Project: DataWarehouse.Plugins.UltimateInterface

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/UltimateInterfacePlugin.cs
```csharp
public sealed class UltimateInterfacePlugin : DataWarehouse.SDK.Contracts.Hierarchy.InterfacePluginBase, IDisposable
{
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override PluginCategory Category;;
    public override string Protocol;;
    public string InterfaceProtocol;;
    public override int? Port;;
    public string SemanticDescription;;
    public string[] SemanticTags;;
    public InterfaceStrategyRegistry Registry;;
    public bool AuditEnabled { get => _auditEnabled; set => _auditEnabled = value; }
    public bool AutoFailoverEnabled { get => _autoFailoverEnabled; set => _autoFailoverEnabled = value; }
    public UltimateInterfacePlugin();
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request);
    protected override List<PluginCapabilityDescriptor> GetCapabilities();
    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities
{
    get
    {
        var capabilities = new List<RegisteredCapability>
        {
            // Main plugin capability
            new()
            {
                CapabilityId = "interface",
                DisplayName = "Ultimate Interface",
                Description = SemanticDescription,
                Category = SDK.Contracts.CapabilityCategory.Interface,
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = SemanticTags
            }
        };
        // Add strategy-based capabilities
        foreach (var strategy in _registry.GetAll())
        {
            var tags = new List<string>
            {
                "interface",
                "protocol",
                strategy.Category.ToString().ToLowerInvariant()
            };
            tags.AddRange(strategy.Tags);
            // Add feature-specific tags
            if (strategy.Capabilities.SupportsStreaming)
                tags.Add("streaming");
            if (strategy.Capabilities.SupportsBidirectionalStreaming)
                tags.Add("bidirectional");
            if (strategy.Capabilities.SupportsAuthentication)
                tags.Add("authentication");
            capabilities.Add(new() { CapabilityId = $"interface.{strategy.StrategyId.ToLowerInvariant().Replace(".", "-").Replace(" ", "-")}", DisplayName = strategy.DisplayName, Description = strategy.SemanticDescription, Category = SDK.Contracts.CapabilityCategory.Interface, SubCategory = strategy.Category.ToString(), PluginId = Id, PluginName = Name, PluginVersion = Version, Tags = [..tags] });
        }

        return capabilities.AsReadOnly();
    }
}
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge();
    protected override Dictionary<string, object> GetMetadata();
    public override Task OnMessageAsync(PluginMessage message);
    protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct);
    protected override Task OnStartWithoutIntelligenceAsync(CancellationToken ct);
    protected override Task OnStartCoreAsync(CancellationToken ct);
    protected override void Dispose(bool disposing);
}
```
```csharp
public sealed class InterfaceStrategyRegistry
{
}
    public int Count;;
    public void Register(IPluginInterfaceStrategy strategy);
    public IPluginInterfaceStrategy? Get(string strategyId);
    public IEnumerable<IPluginInterfaceStrategy> GetAll();;
    public IEnumerable<IPluginInterfaceStrategy> GetByCategory(InterfaceCategory category);
    public int AutoDiscover(Assembly assembly);
}
```
```csharp
public interface IPluginInterfaceStrategy : SdkInterface.IInterfaceStrategy
{
}
    string StrategyId { get; }
    string DisplayName { get; }
    string SemanticDescription { get; }
    InterfaceCategory Category { get; }
    string[] Tags { get; }
}
```
```csharp
internal sealed class InterfaceHealthStatus
{
}
    public required string StrategyId { get; init; }
    public bool IsHealthy { get; set; }
    public DateTimeOffset LastChecked { get; set; }
    public string? LastError { get; set; }
}
```
```csharp
internal sealed class RestInterfaceStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);;
    protected override Task StopAsyncCore(CancellationToken cancellationToken);;
    protected override Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```
```csharp
internal sealed class GrpcInterfaceStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);;
    protected override Task StopAsyncCore(CancellationToken cancellationToken);;
    protected override Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```
```csharp
internal sealed class WebSocketInterfaceStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);;
    protected override Task StopAsyncCore(CancellationToken cancellationToken);;
    protected override Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```
```csharp
internal sealed class GraphQLInterfaceStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);;
    protected override Task StopAsyncCore(CancellationToken cancellationToken);;
    protected override Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```
```csharp
internal sealed class McpInterfaceStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);;
    protected override Task StopAsyncCore(CancellationToken cancellationToken);;
    protected override Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Messaging/AmqpStrategy.cs
```csharp
internal sealed class AmqpStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```
```csharp
internal sealed class AmqpExchange
{
}
    public required string Name { get; init; }
    public required string Type { get; init; }
    public bool Durable { get; init; }
    public bool AutoDelete { get; init; }
}
```
```csharp
internal sealed class AmqpQueue
{
}
    public required string Name { get; init; }
    public bool Durable { get; init; }
    public bool Exclusive { get; init; }
    public bool AutoDelete { get; init; }
    public void EnqueueMessage(AmqpMessage message);
    public List<AmqpMessage> DequeueMessages(int count);
    public int MessageCount
{
    get
    {
        lock (_messagesLock)
        {
            return _messages.Count;
        }
    }
}
}
```
```csharp
internal sealed class AmqpBinding
{
}
    public required string Queue { get; init; }
    public required string Exchange { get; init; }
    public required string RoutingKey { get; init; }
}
```
```csharp
internal sealed class AmqpMessage
{
}
    public required byte[] Body { get; init; }
    public required string RoutingKey { get; init; }
    public required AmqpMessageProperties Properties { get; init; }
}
```
```csharp
internal sealed class AmqpMessageProperties
{
}
    public string ContentType { get; init; };
    public string? CorrelationId { get; init; }
    public string? ReplyTo { get; init; }
    public string? Expiration { get; init; }
    public byte Priority { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Messaging/KafkaRestStrategy.cs
```csharp
internal sealed class KafkaRestStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```
```csharp
internal sealed class KafkaTopic
{
}
    public required string Name { get; init; }
    public required int Partitions { get; init; }
    public required int ReplicationFactor { get; init; }
    public int TotalRecords;;
    public long GetNextOffset(int partition);
    public void AddRecord(KafkaRecord record);
    public List<KafkaRecord> GetRecordsFromOffset(long offset, int maxRecords);
}
```
```csharp
internal sealed class KafkaConsumerGroup
{
}
    public required string Name { get; init; }
    public void AddInstance(string instanceId);
    public IReadOnlyList<string> GetInstances();
}
```
```csharp
internal sealed class KafkaConsumerInstance
{
}
    public required string Id { get; init; }
    public required string GroupName { get; init; }
    public required string Format { get; init; }
    public required string AutoOffsetReset { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public void AddSubscriptions(IEnumerable<string> topics);
    public IReadOnlyList<string> Subscriptions
{
    get
    {
        lock (_subscriptionsLock)
        {
            return _subscriptions.Distinct().ToList();
        }
    }
}
    public System.Collections.Concurrent.ConcurrentDictionary<string, long> CurrentOffsets { get; };
    public System.Collections.Concurrent.ConcurrentDictionary<string, long> CommittedOffsets { get; };
}
```
```csharp
internal sealed class KafkaRecord
{
}
    public required string Topic { get; init; }
    public required int Partition { get; init; }
    public required long Offset { get; init; }
    public string? Key { get; init; }
    public string? Value { get; init; }
    public Dictionary<string, string> Headers { get; init; };
    public DateTimeOffset Timestamp { get; init; }
}
```
```csharp
internal sealed class KafkaProduceRequest
{
}
    public List<KafkaProduceRecord>? Records { get; set; }
}
```
```csharp
internal sealed class KafkaProduceRecord
{
}
    public string? Key { get; set; }
    public string? Value { get; set; }
    public int? Partition { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
}
```
```csharp
internal sealed class KafkaProduceOffset
{
}
    public required int Partition { get; init; }
    public required long Offset { get; init; }
}
```
```csharp
internal sealed class KafkaCreateConsumerRequest
{
}
    public string? Name { get; set; }
    public string? Format { get; set; }
    public string? AutoOffsetReset { get; set; }
}
```
```csharp
internal sealed class KafkaSubscribeRequest
{
}
    public List<string>? Topics { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Messaging/CoApStrategy.cs
```csharp
[SdkCompatibility("3.0.0", Notes = "Phase 36: CoAP resource strategy (EDGE-03)")]
internal sealed class CoApStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    public CoApStrategy(string serverUri = "coap://localhost:5683");
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override async Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
    public Task RegisterResourceAsync(string path, Func<byte[], Task<byte[]>> handler, CancellationToken ct = default);
    public async Task<IReadOnlyList<string>> DiscoverResourcesAsync(CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Messaging/MqttStrategy.cs
```csharp
internal sealed class MqttStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```
```csharp
internal sealed class MqttSubscription
{
}
    public required string ClientId { get; init; }
    public required string Topic { get; init; }
    public int QoS { get; set; }
    public DateTimeOffset SubscribedAt { get; init; }
    public void EnqueueMessage(MqttMessage message);
    public List<MqttMessage> DequeueAll();
}
```
```csharp
internal sealed record MqttMessage
{
}
    public required string Topic { get; init; }
    public required byte[] Payload { get; init; }
    public int QoS { get; init; }
    public bool Retain { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public int? ExpiryInterval { get; init; }
    public Dictionary<string, string> UserProperties { get; init; };
    public bool IsExpired();;
}
```
```csharp
internal sealed class MqttSession
{
}
    public required string ClientId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public bool CleanSession { get; set; }
    public Dictionary<string, int> Subscriptions { get; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Messaging/NatsStrategy.cs
```csharp
internal sealed class NatsStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```
```csharp
internal sealed class NatsSubscription
{
}
    public required string Id { get; init; }
    public required string Subject { get; init; }
    public string? QueueGroup { get; init; }
    public bool Durable { get; init; }
    public DateTimeOffset SubscribedAt { get; init; }
    public void AddMessage(NatsMessage message);
    public IReadOnlyList<NatsMessage> GetMessages();
}
```
```csharp
internal sealed class NatsMessage
{
}
    public required string Subject { get; init; }
    public required byte[] Payload { get; init; }
    public string? ReplyTo { get; init; }
    public Dictionary<string, string> Headers { get; init; };
    public DateTimeOffset Timestamp { get; init; }
}
```
```csharp
internal sealed class NatsStream
{
}
    public required string Name { get; init; }
    public required string[] Subjects { get; init; }
    public required string Retention { get; init; }
    public required int MaxMessages { get; init; }
    public void AddMessage(NatsMessage message);
    public IReadOnlyList<NatsMessage> GetMessages();
    public int Count
{
    get
    {
        lock (_messagesLock)
        {
            return _messages.Count;
        }
    }
}
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Messaging/StompStrategy.cs
```csharp
internal sealed class StompStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```
```csharp
internal sealed class StompSubscription
{
}
    public required string Id { get; init; }
    public required string Destination { get; init; }
    public required string SessionId { get; init; }
    public required string AckMode { get; init; }
    public DateTimeOffset SubscribedAt { get; init; }
    public List<StompMessage> PendingMessages { get; };
}
```
```csharp
internal sealed class StompMessage
{
}
    public required string MessageId { get; init; }
    public required string Destination { get; init; }
    public required byte[] Body { get; init; }
    public required string ContentType { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public Dictionary<string, string> Headers { get; init; };
}
```
```csharp
internal sealed class StompTransaction
{
}
    public required string Id { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public List<StompMessage> Messages { get; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Security/QuantumSafeApiStrategy.cs
```csharp
internal sealed class QuantumSafeApiStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override bool IsProductionReady;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Security/EdgeCachedApiStrategy.cs
```csharp
internal sealed class EdgeCachedApiStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```
```csharp
private sealed class CacheEntry
{
}
    public required string ETag { get; init; }
    public required DateTimeOffset LastModified { get; init; }
    public required byte[] Body { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Security/AnomalyDetectionApiStrategy.cs
```csharp
internal sealed class AnomalyDetectionApiStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override bool IsProductionReady;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```
```csharp
private sealed class ClientBaseline
{
}
    public required string ClientId { get; init; }
    public readonly object Lock = new();
    public List<RequestPattern> RecentRequests { get; };
    public Dictionary<string, int> PathFrequency { get; };
    public Dictionary<string, int> MethodFrequency { get; };
    public Dictionary<int, int> HourOfDayFrequency { get; };
    public RunningStats PayloadSizeStats { get; };
    public RunningStats RequestIntervalStats { get; };
    public DateTimeOffset? LastRequestTime { get; set; }
    public int TotalRequests { get; set; }
}
```
```csharp
private sealed class RequestPattern
{
}
    public required DateTimeOffset Timestamp { get; init; }
    public required string Method { get; init; }
    public required string Path { get; init; }
    public required int PayloadSize { get; init; }
}
```
```csharp
private sealed class RunningStats
{
}
    public void Add(double value);
    public double Mean;;
    public double StdDev
{
    get
    {
        if (_count < 2)
            return 0;
        var variance = (_sumOfSquares - (_sum * _sum / _count)) / (_count - 1);
        return Math.Sqrt(Math.Max(0, variance));
    }
}
    public double ZScore(double value);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Security/CostAwareApiStrategy.cs
```csharp
internal sealed class CostAwareApiStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```
```csharp
private sealed class ClientBudget
{
}
    public required string ClientId { get; init; }
    public readonly object Lock = new();
    public decimal TotalBudget { get; set; };
    public decimal UsedBudget { get; set; }
    public DateTimeOffset ResetAt { get; set; }
    public List<CostEntry> CostHistory { get; };
}
```
```csharp
private sealed class CostEntry
{
}
    public required DateTimeOffset Timestamp { get; init; }
    public required string Operation { get; init; }
    public required decimal Cost { get; init; }
    public required Dictionary<string, decimal> CostBreakdown { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Security/SmartRateLimitStrategy.cs
```csharp
internal sealed class SmartRateLimitStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override bool IsProductionReady;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```
```csharp
private sealed class RateLimitTier
{
}
    public required int RequestsPerMinute { get; init; }
    public required int RequestsPerHour { get; init; }
    public required int BurstSize { get; init; }
}
```
```csharp
private sealed class ClientRateData
{
}
    public required string ClientId { get; init; }
    public required string Tier { get; init; }
    public ConcurrentQueue<DateTimeOffset> RequestTimestamps { get; };
    public int TotalRequests { get; set; }
    public DateTimeOffset? BlockedUntil { get; set; }
    public double AbuseScore { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Security/ZeroTrustApiStrategy.cs
```csharp
internal sealed class ZeroTrustApiStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override bool IsProductionReady;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/REST/OpenApiStrategy.cs
```csharp
internal sealed class OpenApiStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/REST/JsonApiStrategy.cs
```csharp
internal sealed class JsonApiStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/REST/ODataStrategy.cs
```csharp
internal sealed class ODataStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```
```csharp
private record ODataQueryOptions
{
}
    public string? Filter { get; init; }
    public string? Select { get; init; }
    public string? OrderBy { get; init; }
    public int Top { get; init; };
    public int Skip { get; init; }
    public bool Count { get; init; }
    public string? Expand { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/REST/RestInterfaceStrategy.cs
```csharp
internal sealed class RestInterfaceStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/REST/FalcorStrategy.cs
```csharp
internal sealed class FalcorStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override bool IsProductionReady;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```
```csharp
private class FalcorRequest
{
}
    public string? Method { get; set; }
    public object[][]? Paths { get; set; }
    public object? JsonGraphEnvelope { get; set; }
    public object[]? CallPath { get; set; }
    public object[]? Arguments { get; set; }
    public object[]? PathSuffixes { get; set; }
    public object[]? RefPaths { get; set; }
    public object[]? ThisPaths { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/REST/HateoasStrategy.cs
```csharp
internal sealed class HateoasStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Dashboards/DashboardStrategyBase.cs
```csharp
public sealed record DashboardConnectionConfig
{
}
    public required string BaseUrl { get; init; }
    public AuthenticationType AuthType { get; init; };
    public string? ApiKey { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? BearerToken { get; init; }
    public string? OAuth2ClientId { get; init; }
    public string? OAuth2ClientSecret { get; init; }
    public string? OAuth2TokenEndpoint { get; init; }
    public string[]? OAuth2Scopes { get; init; }
    public int TimeoutSeconds { get; init; };
    public int MaxRetries { get; init; };
    public bool VerifySsl { get; init; };
    public IReadOnlyDictionary<string, string>? CustomHeaders { get; init; }
    public string? OrganizationId { get; init; }
    public string? ProjectId { get; init; }
}
```
```csharp
public sealed record DataPushResult
{
}
    public required bool Success { get; init; }
    public long RowsPushed { get; init; }
    public double DurationMs { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }
}
```
```csharp
public sealed record DashboardProvisionOptions
{
}
    public bool Overwrite { get; init; };
    public string? TargetFolder { get; init; }
    public IReadOnlyDictionary<string, string>? Permissions { get; init; }
    public IReadOnlyDictionary<string, object>? Variables { get; init; }
    public bool EnableImmediately { get; init; };
}
```
```csharp
public sealed class DashboardStrategyStatistics
{
}
    public long DashboardsCreated { get; set; }
    public long DashboardsUpdated { get; set; }
    public long DashboardsDeleted { get; set; }
    public long DataPushOperations { get; set; }
    public long TotalRowsPushed { get; set; }
    public long TotalBytesPushed { get; set; }
    public long Errors { get; set; }
    public double AverageOperationDurationMs { get; set; }
}
```
```csharp
public abstract class DashboardStrategyBase : StrategyBase, IDashboardStrategy
{
#endregion
}
    protected static readonly HttpClient SharedHttpClient = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(30)
};
    public abstract override string StrategyId { get; }
    public abstract string StrategyName { get; }
    public override string Name;;
    public abstract string VendorName { get; }
    public abstract string Category { get; }
    public abstract DashboardCapabilities Capabilities { get; }
    protected DashboardConnectionConfig? Config { get; private set; }
    protected virtual int RateLimitPerSecond;;
    protected DashboardStrategyBase();
    public virtual void Configure(DashboardConnectionConfig config);
    public DashboardStrategyStatistics GetStatistics();
    public abstract Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken cancellationToken = default);;
    public abstract Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken cancellationToken = default);;
    public abstract Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);;
    public async Task<Dashboard> CreateDashboardAsync(Dashboard dashboard, CancellationToken cancellationToken = default);
    public async Task<Dashboard> UpdateDashboardAsync(Dashboard dashboard, CancellationToken cancellationToken = default);
    public async Task<Dashboard> GetDashboardAsync(string dashboardId, CancellationToken cancellationToken = default);
    public async Task DeleteDashboardAsync(string dashboardId, CancellationToken cancellationToken = default);
    public async Task<IReadOnlyList<Dashboard>> ListDashboardsAsync(DashboardFilter? filter = null, CancellationToken cancellationToken = default);
    public async Task<Dashboard> CreateFromTemplateAsync(string templateId, IReadOnlyDictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
    protected abstract Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken);;
    protected abstract Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken);;
    protected abstract Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken);;
    protected abstract Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken);;
    protected abstract Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken cancellationToken);;
    protected virtual Task<Dashboard> CreateFromTemplateCoreAsync(string templateId, IReadOnlyDictionary<string, object>? parameters, CancellationToken cancellationToken);
    protected HttpClient GetHttpClient();
    protected async Task<HttpResponseMessage> SendAuthenticatedRequestAsync(HttpMethod method, string endpoint, HttpContent? content = null, CancellationToken cancellationToken = default);
    protected virtual async Task ApplyAuthenticationAsync(HttpRequestMessage request, CancellationToken cancellationToken);
    protected virtual async Task<string?> GetOAuth2TokenAsync(CancellationToken cancellationToken);
    protected void EnsureConfigured();
    protected static string SerializeToJson(object obj);
    protected static T? DeserializeFromJson<T>(string json);
    protected static StringContent CreateJsonContent(object obj);
    protected static string GenerateId();
    protected static string ComputeHash(string input);
    protected virtual object ConvertToPlatformFormat(Dashboard dashboard);
    protected virtual Dashboard ConvertFromPlatformFormat(JsonElement element);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Dashboards/ConsciousnessDashboardStrategies.cs
```csharp
public sealed class ConsciousnessOverviewDashboardStrategy
{
}
    public ConsciousnessOverviewDashboardStrategy(Func<IReadOnlyList<ConsciousnessScore>> scoreProvider);
    public Task<ConsciousnessDashboardData> GenerateOverviewAsync(CancellationToken ct = default);
}
```
```csharp
public sealed class ConsciousnessTrendDashboardStrategy
{
}
    public int SnapshotCount;;
    public void RecordSnapshot(DashboardConsciousnessStatistics statistics);
    public Task<List<ConsciousnessTrendPoint>> GenerateTrendAsync(DateTime from, DateTime to, TimeSpan interval, CancellationToken ct = default);
    public ConsciousnessTrendPoint? GetSnapshot(DateTime timestamp);
}
```
```csharp
public sealed class DarkDataDashboardStrategy
{
}
    public DarkDataDashboardStrategy(Func<IReadOnlyList<ConsciousnessScore>> scoreProvider);
    public void RecordDiscovery(string objectId, long estimatedSizeBytes, string reason);
    public void RecordRemediation(string objectId);
    public int RemediatedCount;;
    public Task<DarkDataReport> GenerateReportAsync(CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/RPC/GrpcWebStrategy.cs
```csharp
internal sealed class GrpcWebStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);;
    protected override Task StopAsyncCore(CancellationToken cancellationToken);;
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/RPC/GrpcInterfaceStrategy.cs
```csharp
internal sealed class GrpcInterfaceStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);;
    protected override Task StopAsyncCore(CancellationToken cancellationToken);;
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/RPC/XmlRpcStrategy.cs
```csharp
internal sealed class XmlRpcStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);;
    protected override Task StopAsyncCore(CancellationToken cancellationToken);;
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/RPC/TwirpStrategy.cs
```csharp
internal sealed class TwirpStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);;
    protected override Task StopAsyncCore(CancellationToken cancellationToken);;
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/RPC/ConnectRpcStrategy.cs
```csharp
internal sealed class ConnectRpcStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);;
    protected override Task StopAsyncCore(CancellationToken cancellationToken);;
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/RPC/JsonRpcStrategy.cs
```csharp
internal sealed class JsonRpcStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);;
    protected override Task StopAsyncCore(CancellationToken cancellationToken);;
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/RealTime/WebSocketInterfaceStrategy.cs
```csharp
internal sealed class WebSocketInterfaceStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override async Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```
```csharp
private sealed class WebSocketConnection : IDisposable
{
}
    public string ConnectionId { get; }
    public DateTimeOffset LastPingTime
{
    get
    {
        lock (_pingLock)
        {
            return _lastPingTime;
        }
    }

    set
    {
        lock (_pingLock)
        {
            _lastPingTime = value;
        }
    }
}
    public WebSocketConnection(string connectionId);
    public void SendMessage(string message);
    public void Dispose();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/RealTime/SignalRStrategy.cs
```csharp
internal sealed class SignalRStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```
```csharp
private sealed class SignalRConnection : IDisposable
{
}
    public string ConnectionId { get; }
    public SignalRConnectionState State { get; set; }
    public DateTimeOffset LastPingTime { get; set; }
    public SignalRConnection(string connectionId);
    public void QueueMessage(string message);
    public void Dispose();
}
```
```csharp
private sealed class SignalRHub
{
}
    public string Name { get; }
    public SignalRHub(string name);
}
```
```csharp
private sealed class SignalRGroup
{
}
    public string Name { get; }
    public SignalRGroup(string name);
    public void AddConnection(string connectionId);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/RealTime/SocketIoStrategy.cs
```csharp
internal sealed class SocketIoStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```
```csharp
private sealed class SocketIoConnection : IDisposable
{
}
    public string SessionId { get; }
    public SocketIoConnection(string sessionId);
    public void JoinNamespace(string ns);;
    public void LeaveNamespace(string ns);
    public void QueuePacket(string packet);
    public void Dispose();
}
```
```csharp
private sealed class SocketIoNamespace
{
}
    public string Name { get; }
    public SocketIoNamespace(string name);
    public void AddConnection(string sid);;
    public void RemoveConnection(string sid);
    public void BroadcastEvent(string eventName, string data, BoundedDictionary<string, SocketIoConnection> connections);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/RealTime/LongPollingStrategy.cs
```csharp
internal sealed class LongPollingStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
    public async Task PublishData(string topic, object data, CancellationToken cancellationToken = default);
}
```
```csharp
private sealed class ClientState
{
}
    public string ClientId { get; }
    public ClientState(string clientId);
    public void Notify();
    public void Release();
}
```
```csharp
private sealed class DataQueue
{
}
    public string Topic { get; }
    public DataQueue(string topic);
    public void Enqueue(string data);
    public string? TryDequeue();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/RealTime/ServerSentEventsStrategy.cs
```csharp
internal sealed class ServerSentEventsStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
    public async Task PublishEvent(string topic, string eventType, object data, CancellationToken cancellationToken = default);
}
```
```csharp
private sealed class EventStream : IDisposable
{
}
    public string StreamId { get; }
    public string Topic { get; }
    public string? Filter { get; }
    public long LastEventId { get; set; }
    public EventStream(string streamId, string topic, string? filter);
    public bool MatchesFilter(string eventType);
    public bool TryDequeueEvent(out string? sseMessage);
    public void QueueEvent(string sseMessage);
    public void Dispose();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Convergence/MergeResultsSummaryStrategy.cs
```csharp
internal sealed class MergeResultsSummaryStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override bool IsProductionReady;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Convergence/SchemaConflictResolutionUIStrategy.cs
```csharp
internal sealed class SchemaConflictResolutionUIStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override bool IsProductionReady;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Convergence/InstanceArrivalNotificationStrategy.cs
```csharp
internal sealed class InstanceArrivalNotificationStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override bool IsProductionReady;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Convergence/MasterInstanceSelectionStrategy.cs
```csharp
internal sealed class MasterInstanceSelectionStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override bool IsProductionReady;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Convergence/MergePreviewStrategy.cs
```csharp
internal sealed class MergePreviewStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override bool IsProductionReady;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Convergence/MergeStrategySelectionStrategy.cs
```csharp
internal sealed class MergeStrategySelectionStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Convergence/ConvergenceChoiceDialogStrategy.cs
```csharp
internal sealed class ConvergenceChoiceDialogStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override bool IsProductionReady;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Convergence/MergeProgressTrackingStrategy.cs
```csharp
internal sealed class MergeProgressTrackingStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override bool IsProductionReady;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Conversational/AlexaChannelStrategy.cs
```csharp
internal sealed class AlexaChannelStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Conversational/GenericWebhookStrategy.cs
```csharp
internal sealed class GenericWebhookStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public void ConfigureWebhookSecret(string secret);
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Conversational/ClaudeMcpStrategy.cs
```csharp
internal sealed class ClaudeMcpStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override bool IsProductionReady;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Conversational/GoogleAssistantChannelStrategy.cs
```csharp
internal sealed class GoogleAssistantChannelStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Conversational/TeamsChannelStrategy.cs
```csharp
internal sealed class TeamsChannelStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public void ConfigureAuthentication(string appId, string? tenantId = null);
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Conversational/DiscordChannelStrategy.cs
```csharp
internal sealed class DiscordChannelStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public void ConfigurePublicKey(string publicKeyHex);
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Conversational/SiriChannelStrategy.cs
```csharp
internal sealed class SiriChannelStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Conversational/SlackChannelStrategy.cs
```csharp
internal sealed class SlackChannelStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public void ConfigureSigningSecret(string signingSecret);
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Conversational/ChatGptPluginStrategy.cs
```csharp
internal sealed class ChatGptPluginStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public void ConfigureVerificationToken(string verificationToken);
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override bool IsProductionReady;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Innovation/UnifiedApiStrategy.cs
```csharp
internal sealed class UnifiedApiStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Innovation/VersionlessApiStrategy.cs
```csharp
internal sealed class VersionlessApiStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Innovation/IntentBasedApiStrategy.cs
```csharp
internal sealed class IntentBasedApiStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override bool IsProductionReady;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Innovation/ZeroConfigApiStrategy.cs
```csharp
internal sealed class ZeroConfigApiStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Innovation/PredictiveApiStrategy.cs
```csharp
internal sealed class PredictiveApiStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override bool IsProductionReady;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Innovation/ProtocolMorphingStrategy.cs
```csharp
internal sealed class ProtocolMorphingStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Innovation/AdaptiveApiStrategy.cs
```csharp
internal sealed class AdaptiveApiStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```
```csharp
private class ClientProfile
{
}
    public required string DeviceType { get; init; }
    public required string Bandwidth { get; init; }
    public required string DetailLevel { get; init; }
    public required bool SupportsCompression { get; init; }
    public required string[] AcceptedFormats { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Innovation/SelfDocumentingApiStrategy.cs
```csharp
internal sealed class SelfDocumentingApiStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Innovation/VoiceFirstApiStrategy.cs
```csharp
internal sealed class VoiceFirstApiStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override bool IsProductionReady;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Innovation/NaturalLanguageApiStrategy.cs
```csharp
internal sealed class NaturalLanguageApiStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override bool IsProductionReady;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Query/HasuraStrategy.cs
```csharp
internal sealed class HasuraStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override bool IsProductionReady;;
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);;
    protected override Task StopAsyncCore(CancellationToken cancellationToken);;
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```
```csharp
private sealed class HasuraRequest
{
}
    public string? Query { get; set; }
    public string? OperationName { get; set; }
    public Dictionary<string, object>? Variables { get; set; }
}
```
```csharp
private sealed class HasuraQueryArguments
{
}
    public bool HasWhere { get; set; }
    public bool HasOrderBy { get; set; }
    public int? Limit { get; set; }
    public int? Offset { get; set; }
    public bool HasDistinctOn { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Query/PrismaStrategy.cs
```csharp
internal sealed class PrismaStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override bool IsProductionReady;;
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);;
    protected override Task StopAsyncCore(CancellationToken cancellationToken);;
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```
```csharp
private sealed class PrismaRequest
{
}
    public string? Query { get; set; }
    public string? OperationName { get; set; }
    public Dictionary<string, object>? Variables { get; set; }
}
```
```csharp
private sealed class PrismaQueryArguments
{
}
    public bool HasWhere { get; set; }
    public bool HasSelect { get; set; }
    public bool HasInclude { get; set; }
    public bool HasOrderBy { get; set; }
    public int? Take { get; set; }
    public int? Skip { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Query/SqlInterfaceStrategy.cs
```csharp
internal sealed class SqlInterfaceStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override bool IsProductionReady;;
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);;
    protected override Task StopAsyncCore(CancellationToken cancellationToken);;
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```
```csharp
private sealed class SqlRequest
{
}
    public string? Query { get; set; }
    public Dictionary<string, object>? Parameters { get; set; }
}
```
```csharp
private sealed class SqlResult
{
}
    public int RowCount { get; set; }
    public int ColumnCount { get; set; }
    public string[] Columns { get; set; };
    public object[] Rows { get; set; };
    public long ExecutionTimeMs { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Query/ApolloFederationStrategy.cs
```csharp
internal sealed class ApolloFederationStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);;
    protected override Task StopAsyncCore(CancellationToken cancellationToken);;
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```
```csharp
private sealed class FederationRequest
{
}
    public string? Query { get; set; }
    public string? OperationName { get; set; }
    public Dictionary<string, object>? Variables { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Query/GraphQLInterfaceStrategy.cs
```csharp
internal sealed class GraphQLInterfaceStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override bool IsProductionReady;;
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);;
    protected override Task StopAsyncCore(CancellationToken cancellationToken);;
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```
```csharp
private sealed class GraphQLRequest
{
}
    public string? Query { get; set; }
    public string? OperationName { get; set; }
    public Dictionary<string, object>? Variables { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Query/PostGraphileStrategy.cs
```csharp
internal sealed class PostGraphileStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override bool IsProductionReady;;
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);;
    protected override Task StopAsyncCore(CancellationToken cancellationToken);;
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```
```csharp
private sealed class PostGraphileRequest
{
}
    public string? Query { get; set; }
    public string? OperationName { get; set; }
    public Dictionary<string, object>? Variables { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Query/RelayStrategy.cs
```csharp
internal sealed class RelayStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override bool IsProductionReady;;
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);;
    protected override Task StopAsyncCore(CancellationToken cancellationToken);;
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```
```csharp
private sealed class RelayRequest
{
}
    public string? Query { get; set; }
    public string? OperationName { get; set; }
    public Dictionary<string, object>? Variables { get; set; }
}
```
```csharp
private sealed class PaginationArguments
{
}
    public int? First { get; set; }
    public string? After { get; set; }
    public int? Last { get; set; }
    public string? Before { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/DeveloperExperience/ApiVersioningStrategy.cs
```csharp
internal sealed class ApiVersioningStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override bool IsProductionReady;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```
```csharp
private sealed class VersionInfo
{
}
    public string Version { get; set; };
    public VersionStatus Status { get; set; }
    public DateTime ReleaseDate { get; set; }
    public DateTime? SunsetDate { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/DeveloperExperience/MockServerStrategy.cs
```csharp
internal sealed class MockServerStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```
```csharp
private sealed class MockResponse
{
}
    public int StatusCode { get; set; }
    public string ResponseBody { get; set; };
    public int DelayMs { get; set; }
}
```
```csharp
private sealed class RecordedRequest
{
}
    public string Method { get; set; };
    public string Path { get; set; };
    public DateTimeOffset Timestamp { get; set; }
    public string Body { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/DeveloperExperience/ChangelogGenerationStrategy.cs
```csharp
internal sealed class ChangelogGenerationStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```
```csharp
private sealed class ChangelogVersion
{
}
    public string Version { get; set; };
    public DateTime ReleaseDate { get; set; }
    public List<ChangeEntry> Changes { get; set; };
}
```
```csharp
private sealed class ChangeEntry
{
}
    public ChangeType Type { get; set; }
    public string Description { get; set; };
    public string Impact { get; set; };
    public string? Migration { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/DeveloperExperience/InteractivePlaygroundStrategy.cs
```csharp
internal sealed class InteractivePlaygroundStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override bool IsProductionReady;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/DeveloperExperience/BreakingChangeDetectionStrategy.cs
```csharp
internal sealed class BreakingChangeDetectionStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```
```csharp
private sealed class ApiSpec
{
}
    public List<ApiEndpoint> Endpoints { get; set; };
}
```
```csharp
private sealed class ApiEndpoint
{
}
    public string Path { get; set; };
    public List<string> Methods { get; set; };
    public List<string> Parameters { get; set; };
}
```
```csharp
private sealed class BreakingChange
{
}
    public ChangeType Type { get; set; }
    public ChangeSeverity Severity { get; set; }
    public string Description { get; set; };
    public string Path { get; set; };
    public string Recommendation { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/DeveloperExperience/InstantSdkGenerationStrategy.cs
```csharp
internal sealed class InstantSdkGenerationStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
}
    public override string StrategyId;;
    public string DisplayName;;
    public string SemanticDescription;;
    public InterfaceCategory Category;;
    public string[] Tags;;
    public override bool IsProductionReady;;
    public override SdkInterface.InterfaceProtocol Protocol;;
    public override SdkInterface.InterfaceCapabilities Capabilities;;
    protected override Task StartAsyncCore(CancellationToken cancellationToken);
    protected override Task StopAsyncCore(CancellationToken cancellationToken);
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Moonshots/Dashboard/MoonshotMetricsCollector.cs
```csharp
public sealed class MoonshotMetricsCollector : IDisposable
{
}
    public MoonshotMetricsCollector(IMessageBus messageBus, ILogger<MoonshotMetricsCollector> logger);
    public Task StartCollectingAsync(CancellationToken ct);
    public MoonshotMetrics GetMetrics(MoonshotId id);
    public IReadOnlyList<MoonshotMetrics> GetAllMetrics();
    public IReadOnlyList<MoonshotTrendPoint> GetTrends(MoonshotId id, string metricName, DateTimeOffset from, DateTimeOffset to);
    public void Dispose();
}
```
```csharp
private sealed class MoonshotMetricState
{
}
    public long TotalInvocations;
    public long SuccessCount;
    public long FailureCount;
    public readonly ConcurrentQueue<double> LatencySamples = new();
    public long WindowStartTicks = DateTimeOffset.UtcNow.Ticks;
    public DateTimeOffset WindowStart { get => new DateTimeOffset(Interlocked.Read(ref WindowStartTicks), TimeSpan.Zero); }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Moonshots/Dashboard/MoonshotDashboardStrategy.cs
```csharp
public sealed class MoonshotDashboardStrategy
{
}
    public string Name;;
    public string Description;;
    public string Category;;
    public MoonshotDashboardStrategy(MoonshotDashboardProvider provider, ILogger<MoonshotDashboardStrategy> logger);
    public async Task<MoonshotDashboardRenderResult> RenderAsync(CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Moonshots/Dashboard/MoonshotDashboardProvider.cs
```csharp
public sealed class MoonshotDashboardProvider : IMoonshotDashboardProvider
{
}
    public MoonshotDashboardProvider(IMoonshotRegistry registry, MoonshotMetricsCollector metricsCollector, ILogger<MoonshotDashboardProvider> logger);
    public Task<MoonshotDashboardSnapshot> GetSnapshotAsync(CancellationToken ct);
    public Task<IReadOnlyList<MoonshotTrendPoint>> GetTrendsAsync(MoonshotId id, string metricName, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
    public Task<MoonshotMetrics> GetMetricsAsync(MoonshotId id, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Services/Dashboard/DashboardAccessControlService.cs
```csharp
public sealed class DashboardAccessControlService
{
}
    public void SetPermissions(string tenantId, string dashboardId, string ownerId, DashboardVisibility visibility = DashboardVisibility.Private);
    public DashboardAccessGrant GrantAccess(string tenantId, string dashboardId, string granteeId, DashboardRole role, string grantedBy);
    public bool RevokeAccess(string tenantId, string dashboardId, string granteeId, string revokedBy);
    public DashboardAccessCheck CheckAccess(string tenantId, string dashboardId, string userId, DashboardRole requiredRole = DashboardRole.Viewer);
    public IReadOnlyList<DashboardAccessAuditEntry> GetAuditLog(string tenantId, string dashboardId, int limit = 100);
    public IReadOnlyList<DashboardAccessGrant> ListGrants(string tenantId, string dashboardId);
}
```
```csharp
public sealed record DashboardPermissions
{
}
    public required string TenantId { get; init; }
    public required string DashboardId { get; init; }
    public required string OwnerId { get; init; }
    public DashboardVisibility Visibility { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
```
```csharp
public sealed record DashboardAccessGrant
{
}
    public required string GrantId { get; init; }
    public required string TenantId { get; init; }
    public required string DashboardId { get; init; }
    public required string GranteeId { get; init; }
    public DashboardRole Role { get; init; }
    public required string GrantedBy { get; init; }
    public DateTimeOffset GrantedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
}
```
```csharp
public sealed record DashboardAccessCheck
{
}
    public bool Allowed { get; init; }
    public DashboardRole EffectiveRole { get; init; }
    public required string Reason { get; init; }
}
```
```csharp
public sealed record DashboardAccessAuditEntry
{
}
    public required string TenantId { get; init; }
    public required string DashboardId { get; init; }
    public required string UserId { get; init; }
    public required string Action { get; init; }
    public required string Details { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Services/Dashboard/DashboardDataSourceService.cs
```csharp
public sealed class DashboardDataSourceService
{
}
    public void RegisterDataSource(IDashboardDataSource dataSource);
    public IDashboardDataSource? GetDataSource(string dataSourceId);;
    public async Task<DataSourceResult> QueryAsync(string dataSourceId, string query, TimeRange? timeRange = null, Dictionary<string, object>? parameters = null, CancellationToken ct = default);
    public string Subscribe(string dataSourceId, Action<DataSourceUpdate> callback);
    public void PushUpdate(string dataSourceId, DataSourceUpdate update);
    public IReadOnlyList<DataSourceInfo> ListDataSources();;
}
```
```csharp
public interface IDashboardDataSource
{
}
    string DataSourceId { get; }
    string Name { get; }
    string Type { get; }
    bool SupportsRealTime { get; }
    Task<DataSourceResult> ExecuteQueryAsync(string query, TimeRange? timeRange, Dictionary<string, object>? parameters, CancellationToken ct);;
}
```
```csharp
public sealed class BusTopicDataSource : IDashboardDataSource
{
}
    public string DataSourceId { get; }
    public string Name { get; }
    public string Type;;
    public bool SupportsRealTime;;
    public string TopicPattern { get; }
    public BusTopicDataSource(string id, string name, string topicPattern);
    public void IngestMessage(string topic, Dictionary<string, object> data);
    public Task<DataSourceResult> ExecuteQueryAsync(string query, TimeRange? timeRange, Dictionary<string, object>? parameters, CancellationToken ct);
}
```
```csharp
public sealed class QueryDataSource : IDashboardDataSource
{
}
    public string DataSourceId { get; }
    public string Name { get; }
    public string Type;;
    public bool SupportsRealTime;;
    public QueryDataSource(string id, string name, Func<string, Dictionary<string, object>?, Task<List<Dictionary<string, object>>>> queryExecutor);
    public async Task<DataSourceResult> ExecuteQueryAsync(string query, TimeRange? timeRange, Dictionary<string, object>? parameters, CancellationToken ct);
}
```
```csharp
public sealed class StaticDataSource : IDashboardDataSource
{
}
    public string DataSourceId { get; }
    public string Name { get; }
    public string Type;;
    public bool SupportsRealTime;;
    public StaticDataSource(string id, string name, List<Dictionary<string, object>> data);
    public Task<DataSourceResult> ExecuteQueryAsync(string query, TimeRange? timeRange, Dictionary<string, object>? parameters, CancellationToken ct);
}
```
```csharp
public sealed record DataSourceResult
{
}
    public bool Success { get; init; }
    public List<Dictionary<string, object>> Data { get; init; };
    public int TotalCount { get; init; }
    public string? DataSourceId { get; init; }
    public string? ErrorMessage { get; init; }
}
```
```csharp
public sealed record DataSourceUpdate
{
}
    public required string DataSourceId { get; init; }
    public required string UpdateType { get; init; }
    public Dictionary<string, object> Data { get; init; };
    public DateTimeOffset Timestamp { get; init; };
}
```
```csharp
public sealed record DataSourceInfo
{
}
    public required string DataSourceId { get; init; }
    public required string Name { get; init; }
    public required string Type { get; init; }
    public bool SupportsRealTime { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Services/Dashboard/DashboardTemplateService.cs
```csharp
public sealed class DashboardTemplateService
{
}
    public DashboardTemplateService();
    public DashboardTemplate? GetTemplate(string templateId);;
    public IReadOnlyList<DashboardTemplate> ListTemplates(string? category = null);
    public Dashboard InstantiateTemplate(string templateId, Dictionary<string, object>? variables = null);
    public void RegisterTemplate(DashboardTemplate template);
}
```
```csharp
public sealed record DashboardTemplate
{
}
    public required string TemplateId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? Category { get; init; }
    public string[]? Tags { get; init; }
    public int Columns { get; init; };
    public int RowHeight { get; init; };
    public int? DefaultRefreshInterval { get; init; }
    public TimeRange? DefaultTimeRange { get; init; }
    public TemplateWidgetDefinition[] WidgetDefinitions { get; init; };
}
```
```csharp
public sealed record TemplateWidgetDefinition
{
}
    public required string Title { get; init; }
    public WidgetType WidgetType { get; init; }
    public required string DataSourceType { get; init; }
    public required string Query { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; };
    public int Height { get; init; };
    public IReadOnlyDictionary<string, object>? Parameters { get; init; }
    public IReadOnlyDictionary<string, object>? Configuration { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Services/Dashboard/DashboardPersistenceService.cs
```csharp
public sealed class DashboardPersistenceService
{
}
    public DashboardPersistenceService(string? storagePath = null);
    public PersistedDashboard Save(string tenantId, Dashboard dashboard, string userId);
    public PersistedDashboard? Update(string tenantId, string dashboardId, Dashboard dashboard, string userId);
    public PersistedDashboard? Get(string tenantId, string dashboardId);
    public IReadOnlyList<PersistedDashboard> List(string tenantId, DashboardListOptions? options = null);
    public bool Delete(string tenantId, string dashboardId);
    public IReadOnlyList<DashboardVersion> GetVersionHistory(string tenantId, string dashboardId);
    public PersistedDashboard? RestoreVersion(string tenantId, string dashboardId, int targetVersion, string userId);
    public void SaveWidgetConfig(string tenantId, string dashboardId, string widgetId, WidgetConfiguration config);
    public WidgetConfiguration? GetWidgetConfig(string tenantId, string dashboardId, string widgetId);
    public IReadOnlyList<WidgetConfiguration> ListWidgetConfigs(string tenantId, string dashboardId);
    public DashboardShareLink CreateShareLink(string tenantId, string dashboardId, SharePermission permission, TimeSpan? expiresIn = null);
    public DashboardShareLink? ValidateShareLink(string linkId);
    public EmbedConfiguration CreateEmbedConfig(string tenantId, string dashboardId, EmbedOptions options);
    public EmbedConfiguration? GetEmbedConfig(string embedId);;
    public async Task FlushToStorageAsync(CancellationToken ct = default);
    public async Task LoadFromStorageAsync(CancellationToken ct = default);
    public Dictionary<string, int> GetDashboardCountsByTenant();;
}
```
```csharp
public sealed record PersistedDashboard
{
}
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required Dashboard Dashboard { get; init; }
    public required string CreatedBy { get; init; }
    public string? UpdatedBy { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public int Version { get; init; }
    public List<string> Tags { get; init; };
}
```
```csharp
public sealed record DashboardVersion
{
}
    public int Version { get; init; }
    public required Dashboard Snapshot { get; init; }
    public DateTimeOffset SavedAt { get; init; }
    public required string SavedBy { get; init; }
}
```
```csharp
public sealed record DashboardListOptions
{
}
    public string? CreatedBy { get; init; }
    public string? Tag { get; init; }
    public int Offset { get; init; }
    public int Limit { get; init; };
}
```
```csharp
public sealed record WidgetConfiguration
{
}
    public required string WidgetId { get; init; }
    public required string WidgetType { get; init; }
    public required string Title { get; init; }
    public Dictionary<string, object> DataSource { get; init; };
    public Dictionary<string, object> Visualization { get; init; };
    public Dictionary<string, object> Filters { get; init; };
    public int PositionX { get; init; }
    public int PositionY { get; init; }
    public int Width { get; init; };
    public int Height { get; init; };
    public int RefreshIntervalSeconds { get; init; };
}
```
```csharp
public sealed record DashboardShareLink
{
}
    public required string LinkId { get; init; }
    public required string DashboardId { get; init; }
    public required string TenantId { get; init; }
    public SharePermission Permission { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public bool IsActive { get; init; }
}
```
```csharp
public sealed record EmbedConfiguration
{
}
    public required string EmbedId { get; init; }
    public required string DashboardId { get; init; }
    public required string TenantId { get; init; }
    public required EmbedOptions Options { get; init; }
    public required string IframeUrl { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
```
```csharp
public sealed record EmbedOptions
{
}
    public bool ShowHeader { get; init; };
    public bool ShowFilters { get; init; };
    public bool AllowInteraction { get; init; };
    public string? Theme { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public string[] AllowedDomains { get; init; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Dashboards/OpenSource/MetabaseStrategy.cs
```csharp
public sealed class MetabaseStrategy : DashboardStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken cancellationToken = default);
    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken cancellationToken = default);
    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken);
    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken);
    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken);
    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken);
    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken cancellationToken);
    protected override Task ApplyAuthenticationAsync(HttpRequestMessage request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Dashboards/OpenSource/OpenSourceStrategies.cs
```csharp
public sealed class RedashStrategy : DashboardStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    public override async Task<bool> TestConnectionAsync(CancellationToken ct = default);
    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default);
    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default);
    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct);
}
```
```csharp
public sealed class ApacheSupersetStrategy : DashboardStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    public override async Task<bool> TestConnectionAsync(CancellationToken ct = default);
    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default);
    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default);
    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct);
    protected override Task ApplyAuthenticationAsync(HttpRequestMessage request, CancellationToken ct);
}
```
```csharp
public sealed class GrafanaDashboardsStrategy : DashboardStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    public override async Task<bool> TestConnectionAsync(CancellationToken ct = default);
    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default);
    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default);
    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct);
}
```
```csharp
public sealed class KibanaDashboardsStrategy : DashboardStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    public override async Task<bool> TestConnectionAsync(CancellationToken ct = default);
    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default);
    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default);
    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Dashboards/CloudNative/CloudNativeStrategies.cs
```csharp
public sealed class AwsQuickSightStrategy : DashboardStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    public override async Task<bool> TestConnectionAsync(CancellationToken ct = default);
    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default);
    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default);
    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct);
    protected override async Task<Dashboard> CreateFromTemplateCoreAsync(string templateId, IReadOnlyDictionary<string, object>? parameters, CancellationToken ct);
}
```
```csharp
public sealed class GoogleDataStudioStrategy : DashboardStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    public override async Task<bool> TestConnectionAsync(CancellationToken ct = default);
    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default);
    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default);
    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct);
    protected override async Task<Dashboard> CreateFromTemplateCoreAsync(string templateId, IReadOnlyDictionary<string, object>? parameters, CancellationToken ct);
}
```
```csharp
public sealed class AzurePowerBiEmbeddedStrategy : DashboardStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    public override async Task<bool> TestConnectionAsync(CancellationToken ct = default);
    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default);
    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default);
    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct);
    public async Task<string> GetEmbedTokenAsync(string dashboardId, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Dashboards/Analytics/AnalyticsStrategies.cs
```csharp
public sealed class DomoStrategy : DashboardStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken cancellationToken = default);
    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken cancellationToken = default);
    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken);
    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken);
    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken);
    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken);
    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken cancellationToken);
}
```
```csharp
public sealed class ThoughtSpotStrategy : DashboardStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken cancellationToken = default);
    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken cancellationToken = default);
    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken);
    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken);
    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken);
    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken);
    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken cancellationToken);
}
```
```csharp
public sealed class ModeAnalyticsStrategy : DashboardStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken cancellationToken = default);
    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken cancellationToken = default);
    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken);
    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken);
    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken);
    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken);
    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken cancellationToken);
}
```
```csharp
public sealed class ObservableHqStrategy : DashboardStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken cancellationToken = default);
    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken cancellationToken = default);
    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken);
    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken);
    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken);
    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken);
    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken cancellationToken);
}
```
```csharp
public sealed class HexStrategy : DashboardStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken cancellationToken = default);
    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken cancellationToken = default);
    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken);
    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken);
    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken);
    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken);
    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken cancellationToken);
}
```
```csharp
public sealed class SigmaComputingStrategy : DashboardStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken cancellationToken = default);
    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken cancellationToken = default);
    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken);
    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken);
    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken);
    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken);
    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken cancellationToken);
}
```
```csharp
public sealed class LightdashStrategy : DashboardStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken cancellationToken = default);
    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken cancellationToken = default);;
    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken);
    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken);
    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken);
    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken);
    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken cancellationToken);
}
```
```csharp
public sealed class EvidenceStrategy : DashboardStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken cancellationToken = default);
    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken cancellationToken = default);;
    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken);
    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken);
    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken);
    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken);
    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken cancellationToken);
}
```
```csharp
public sealed class CubeJsStrategy : DashboardStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken cancellationToken = default);
    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken cancellationToken = default);;
    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken);
    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken);
    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken);
    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken);
    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken cancellationToken);
}
```
```csharp
public sealed class PresetIoStrategy : DashboardStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken cancellationToken = default);
    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken cancellationToken = default);;
    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken);
    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken);
    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken);
    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken);
    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken cancellationToken);
}
```
```csharp
public sealed class RetoolStrategy : DashboardStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken cancellationToken = default);
    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken cancellationToken = default);;
    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken);
    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken);
    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken);
    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken);
    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken cancellationToken);
}
```
```csharp
public sealed class AppsmithStrategy : DashboardStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken cancellationToken = default);
    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken cancellationToken = default);;
    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken);
    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken);
    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken);
    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken);
    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Dashboards/EnterpriseBi/LookerStrategy.cs
```csharp
public sealed class LookerStrategy : DashboardStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken cancellationToken = default);
    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken cancellationToken = default);
    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken);
    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken);
    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken);
    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken);
    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken cancellationToken);
    protected override async Task<Dashboard> CreateFromTemplateCoreAsync(string templateId, IReadOnlyDictionary<string, object>? parameters, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Dashboards/EnterpriseBi/QlikStrategy.cs
```csharp
public sealed class QlikStrategy : DashboardStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken cancellationToken = default);
    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken cancellationToken = default);
    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken);
    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken);
    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken);
    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken);
    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken cancellationToken);
    protected override async Task<Dashboard> CreateFromTemplateCoreAsync(string templateId, IReadOnlyDictionary<string, object>? parameters, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Dashboards/EnterpriseBi/PowerBiStrategy.cs
```csharp
public sealed class PowerBiStrategy : DashboardStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    protected override int RateLimitPerSecond;;
    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken cancellationToken = default);
    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken cancellationToken = default);
    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken);
    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken);
    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken);
    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken);
    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken cancellationToken);
    protected override async Task<Dashboard> CreateFromTemplateCoreAsync(string templateId, IReadOnlyDictionary<string, object>? parameters, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Dashboards/EnterpriseBi/EnterpriseBiStrategies.cs
```csharp
public sealed class SapAnalyticsStrategy : DashboardStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    public override async Task<bool> TestConnectionAsync(CancellationToken ct = default);
    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default);
    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default);
    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct);
}
```
```csharp
public sealed class IbmCognosStrategy : DashboardStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    public override async Task<bool> TestConnectionAsync(CancellationToken ct = default);
    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default);
    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default);
    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct);
}
```
```csharp
public sealed class MicroStrategyStrategy : DashboardStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    public override async Task<bool> TestConnectionAsync(CancellationToken ct = default);
    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default);
    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default);
    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct);
}
```
```csharp
public sealed class SisenseStrategy : DashboardStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    public override async Task<bool> TestConnectionAsync(CancellationToken ct = default);
    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default);
    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default);
    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Dashboards/EnterpriseBi/TableauStrategy.cs
```csharp
public sealed class TableauStrategy : DashboardStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    public override void Configure(DashboardConnectionConfig config);
    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken cancellationToken = default);
    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken cancellationToken = default);
    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken);
    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken);
    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken);
    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken);
    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken cancellationToken);
    protected override async Task<Dashboard> CreateFromTemplateCoreAsync(string templateId, IReadOnlyDictionary<string, object>? parameters, CancellationToken cancellationToken);
    protected override async Task ApplyAuthenticationAsync(HttpRequestMessage request, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Dashboards/RealTime/RealTimeStrategies.cs
```csharp
public sealed class LiveDashboardStrategy : DashboardStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    public override async Task<bool> TestConnectionAsync(CancellationToken ct = default);
    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default);
    public override Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default);
    protected override Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct);
    protected override Task<Dashboard> CreateFromTemplateCoreAsync(string templateId, IReadOnlyDictionary<string, object>? parameters, CancellationToken ct);
    public void Subscribe(string dashboardId, ClientWebSocket socket);
    public void Unsubscribe(string dashboardId, ClientWebSocket socket);
    public async Task StartContinuousPushAsync(string dashboardId, Func<Task<IReadOnlyList<IReadOnlyDictionary<string, object>>>> dataProvider, int intervalMs = 1000);
    public void StopContinuousPush(string dashboardId);
}
```
```csharp
public sealed class WebSocketUpdatesStrategy : DashboardStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    public override async Task<bool> TestConnectionAsync(CancellationToken ct = default);
    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default);
    public override Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default);
    protected override Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct);
    protected override void Dispose(bool disposing);
}
```
```csharp
public sealed class StreamingVisualizationStrategy : DashboardStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    public override Task<bool> TestConnectionAsync(CancellationToken ct = default);
    public override Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default);
    public override Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default);
    protected override Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct);
    protected override Task<Dashboard> CreateFromTemplateCoreAsync(string templateId, IReadOnlyDictionary<string, object>? parameters, CancellationToken ct);
    public IReadOnlyList<DataPoint> GetStreamData(string streamId, int maxPoints = 1000);
    public void ClearStream(string streamId);
}
```
```csharp
public sealed class EventDrivenDashboardStrategy : DashboardStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    public override Task<bool> TestConnectionAsync(CancellationToken ct = default);
    public override Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default);
    public override Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default);
    protected override Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct);
    public void OnEvent(string dashboardId, Action<DashboardEvent> handler);
    public void OffEvent(string dashboardId, Action<DashboardEvent> handler);
}
```
```csharp
public sealed class DataPoint
{
}
    public DateTimeOffset Timestamp { get; init; }
    public required Dictionary<string, object> Data { get; init; }
}
```
```csharp
public sealed class DashboardEvent
{
}
    public required string Type { get; init; }
    public required string TargetId { get; init; }
    public IReadOnlyList<IReadOnlyDictionary<string, object>>? Data { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Dashboards/Embedded/EmbeddedStrategies.cs
```csharp
public sealed class EmbeddedSdkStrategy : DashboardStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    public override Task<bool> TestConnectionAsync(CancellationToken ct = default);
    public override Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default);
    public override Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default);
    protected override Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct);
    protected override Task<Dashboard> CreateFromTemplateCoreAsync(string templateId, IReadOnlyDictionary<string, object>? parameters, CancellationToken ct);
    public string GenerateEmbedHtml(string dashboardId, int width = 800, int height = 600);
}
```
```csharp
public sealed class IframeIntegrationStrategy : DashboardStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    public override Task<bool> TestConnectionAsync(CancellationToken ct = default);
    public override Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default);
    public override Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default);
    protected override Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct);
    public void SetEmbedUrl(string dashboardId, string url);
    public string GenerateIframeHtml(string dashboardId, int width = 800, int height = 600);
}
```
```csharp
public sealed class ApiRenderingStrategy : DashboardStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    public override async Task<bool> TestConnectionAsync(CancellationToken ct = default);
    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default);
    public override Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default);
    protected override Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct);
    protected override Task<Dashboard> CreateFromTemplateCoreAsync(string templateId, IReadOnlyDictionary<string, object>? parameters, CancellationToken ct);
    public string RenderToJson(string dashboardId);
}
```
```csharp
public sealed class WhiteLabelStrategy : DashboardStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    public override Task<bool> TestConnectionAsync(CancellationToken ct = default);
    public override Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default);
    public override Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default);
    protected override Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct);
    protected override Task<Dashboard> CreateFromTemplateCoreAsync(string templateId, IReadOnlyDictionary<string, object>? parameters, CancellationToken ct);
    public object ApplyBranding(string dashboardId, BrandingOptions branding);
}
```
```csharp
public sealed record BrandingOptions
{
}
    public string? PrimaryColor { get; init; }
    public string? SecondaryColor { get; init; }
    public string? LogoUrl { get; init; }
    public string? FontFamily { get; init; }
    public string? CustomCss { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/Dashboards/Export/ExportStrategies.cs
```csharp
public sealed class PdfGenerationStrategy : DashboardStrategyBase
{
}
    public override bool IsProductionReady;;
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    public override Task<bool> TestConnectionAsync(CancellationToken ct = default);
    public override Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default);
    public override Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default);
    protected override Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct);
    public byte[] GeneratePdf(string dashboardId, PdfExportOptions? options = null);
    public async Task<byte[]> GeneratePdfAsync(string dashboardId, PdfExportOptions? options = null, CancellationToken ct = default);
}
```
```csharp
public sealed record PdfExportOptions
{
}
    public string PageSize { get; init; };
    public string Orientation { get; init; };
    public bool IncludeHeader { get; init; };
    public bool IncludeFooter { get; init; };
    public bool IncludeTimestamp { get; init; };
    public string? WatermarkText { get; init; }
    public int Quality { get; init; };
}
```
```csharp
public sealed class ImageExportStrategy : DashboardStrategyBase
{
}
    public override bool IsProductionReady;;
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    public override Task<bool> TestConnectionAsync(CancellationToken ct = default);
    public override Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default);
    public override Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default);
    protected override Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct);
    public byte[] ExportToImage(string dashboardId, ImageExportOptions? options = null);
    public byte[] ExportWidgetToImage(string dashboardId, string widgetId, ImageExportOptions? options = null);
}
```
```csharp
public sealed record ImageExportOptions
{
}
    public string Format { get; init; };
    public int Width { get; init; };
    public int Height { get; init; };
    public int Quality { get; init; };
    public string? BackgroundColor { get; init; }
    public bool Transparent { get; init; };
    public int Scale { get; init; };
}
```
```csharp
public sealed class ScheduledReportsStrategy : DashboardStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    public override Task<bool> TestConnectionAsync(CancellationToken ct = default);
    public override Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default);
    public override Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default);
    protected override Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct);
    public ScheduledReport CreateSchedule(string dashboardId, ScheduleConfig config);
    public IReadOnlyList<ScheduledReport> GetSchedules(string dashboardId);
    public void DeleteSchedule(string dashboardId, string scheduleId);
    public async Task<ReportResult> TriggerReportAsync(string dashboardId, string scheduleId, CancellationToken ct = default);
}
```
```csharp
public sealed class EmailDeliveryStrategy : DashboardStrategyBase
{
}
    public override bool IsProductionReady;;
    public override string StrategyId;;
    public override string StrategyName;;
    public override string VendorName;;
    public override string Category;;
    public override DashboardCapabilities Capabilities { get; };
    public override Task<bool> TestConnectionAsync(CancellationToken ct = default);
    public override Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default);
    public override Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default);
    protected override Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct);
    protected override Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct);
    protected override Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct);
    public async Task<EmailDeliveryResult> SendDashboardAsync(string dashboardId, EmailOptions options, CancellationToken ct = default);
    public async Task<IReadOnlyList<EmailDeliveryResult>> BroadcastDashboardAsync(string dashboardId, IReadOnlyList<RecipientConfig> recipients, CancellationToken ct = default);
}
```
```csharp
public sealed record ScheduleConfig
{
}
    public ScheduleFrequency Frequency { get; init; };
    public TimeSpan TimeOfDay { get; init; };
    public DayOfWeek DayOfWeek { get; init; };
    public int DayOfMonth { get; init; };
    public string Format { get; init; };
    public IReadOnlyList<string> Recipients { get; init; };
    public string? Subject { get; init; }
    public string? Message { get; init; }
}
```
```csharp
public sealed class ScheduledReport
{
}
    public required string Id { get; init; }
    public required string DashboardId { get; init; }
    public required ScheduleConfig Config { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset NextRunAt { get; set; }
    public bool Enabled { get; set; }
}
```
```csharp
public sealed class ReportResult
{
}
    public required string ScheduleId { get; init; }
    public required string DashboardId { get; init; }
    public DateTimeOffset GeneratedAt { get; init; }
    public required string Format { get; init; }
    public required byte[] Content { get; init; }
    public IReadOnlyList<string>? Recipients { get; init; }
}
```
```csharp
public sealed record EmailOptions
{
}
    public required IReadOnlyList<string> Recipients { get; init; }
    public string? Subject { get; init; }
    public string? Body { get; init; }
    public string Format { get; init; };
    public bool IncludeAttachment { get; init; };
    public bool IncludeInlineImages { get; init; };
}
```
```csharp
public sealed record RecipientConfig
{
}
    public required string Email { get; init; }
    public string? CustomSubject { get; init; }
    public string PreferredFormat { get; init; };
    public bool IncludeAttachment { get; init; };
}
```
```csharp
public sealed class EmailDeliveryResult
{
}
    public bool Success { get; init; }
    public string? MessageId { get; init; }
    public IReadOnlyList<string>? Recipients { get; init; }
    public DateTimeOffset SentAt { get; init; }
    public string? DashboardId { get; init; }
    public string? Subject { get; init; }
    public string? Error { get; init; }
}
```
