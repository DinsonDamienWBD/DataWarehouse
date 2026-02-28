# Plugin: UltimateConnector
> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.
> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.


## Project: DataWarehouse.Plugins.UltimateConnector

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/UltimateConnectorPlugin.cs
```csharp
public sealed class UltimateConnectorPlugin : DataWarehouse.SDK.Contracts.Hierarchy.InterfacePluginBase
{
#endregion
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override PluginCategory Category;;
    public override string Protocol;;
    public string SemanticDescription;;
    public string[] SemanticTags;;
    public ConnectionStrategyRegistry Registry;;
    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities
{
    get
    {
        var capabilities = new List<RegisteredCapability>
        {
            // Main plugin capability
            new()
            {
                CapabilityId = $"{Id}.connector",
                DisplayName = "Ultimate Connector",
                Description = SemanticDescription,
                Category = SDK.Contracts.CapabilityCategory.Connector,
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = [..SemanticTags]
            }
        };
        // Add strategy-based capabilities
        foreach (var strategy in _registry.GetAll())
        {
            var tags = new List<string>
            {
                "connector",
                "integration",
                strategy.Category.ToString().ToLowerInvariant()
            };
            tags.AddRange(strategy.Tags);
            // Add feature-specific tags
            if (strategy.Capabilities.SupportsPooling)
                tags.Add("pooling");
            if (strategy.Capabilities.SupportsStreaming)
                tags.Add("streaming");
            if (strategy.Capabilities.SupportsTransactions)
                tags.Add("transactions");
            capabilities.Add(new() { CapabilityId = $"{Id}.{strategy.StrategyId.ToLowerInvariant().Replace(".", "-").Replace(" ", "-")}", DisplayName = strategy.DisplayName, Description = strategy.SemanticDescription, Category = SDK.Contracts.CapabilityCategory.Connector, SubCategory = strategy.Category.ToString(), PluginId = Id, PluginName = Name, PluginVersion = Version, Tags = [..tags] });
        }

        return capabilities.AsReadOnly();
    }
}
    public UltimateConnectorPlugin();
    protected override async Task OnStartCoreAsync(CancellationToken ct);
    protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct);
    protected override Task OnStartWithoutIntelligenceAsync(CancellationToken ct);
    protected override Task OnStopCoreAsync();
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request);
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge();
    protected override List<PluginCapabilityDescriptor> GetCapabilities();
    protected override Dictionary<string, object> GetMetadata();
    public override Task OnMessageAsync(PluginMessage message);
    public IConnectionStrategy? GetStrategy(string strategyId);
    public IReadOnlyCollection<IConnectionStrategy> GetStrategiesByCategory(ConnectorCategory category);
    public IReadOnlyCollection<IConnectionStrategy> ListStrategies();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Messaging/MqttConnectionStrategy.cs
```csharp
public class MqttConnectionStrategy : MessagingConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public MqttConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task PublishAsync(IConnectionHandle handle, string topic, byte[] message, Dictionary<string, string>? headers = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<byte[]> SubscribeAsync(IConnectionHandle handle, string topic, string? consumerGroup = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Messaging/AwsEventBridgeConnectionStrategy.cs
```csharp
public class AwsEventBridgeConnectionStrategy : MessagingConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public AwsEventBridgeConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task PublishAsync(IConnectionHandle handle, string topic, byte[] message, Dictionary<string, string>? headers = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<byte[]> SubscribeAsync(IConnectionHandle handle, string topic, string? consumerGroup = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Messaging/AzureEventGridConnectionStrategy.cs
```csharp
public class AzureEventGridConnectionStrategy : MessagingConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public AzureEventGridConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task PublishAsync(IConnectionHandle handle, string topic, byte[] message, Dictionary<string, string>? headers = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<byte[]> SubscribeAsync(IConnectionHandle handle, string topic, string? consumerGroup = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Messaging/ApachePulsarConnectionStrategy.cs
```csharp
public class ApachePulsarConnectionStrategy : MessagingConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public ApachePulsarConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task PublishAsync(IConnectionHandle handle, string topic, byte[] message, Dictionary<string, string>? headers = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<byte[]> SubscribeAsync(IConnectionHandle handle, string topic, string? consumerGroup = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Messaging/GooglePubSubConnectionStrategy.cs
```csharp
public class GooglePubSubConnectionStrategy : MessagingConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public GooglePubSubConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task PublishAsync(IConnectionHandle handle, string topic, byte[] message, Dictionary<string, string>? headers = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<byte[]> SubscribeAsync(IConnectionHandle handle, string topic, string? consumerGroup = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Messaging/ConfluentCloudConnectionStrategy.cs
```csharp
public class ConfluentCloudConnectionStrategy : MessagingConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public ConfluentCloudConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task PublishAsync(IConnectionHandle handle, string topic, byte[] message, Dictionary<string, string>? headers = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<byte[]> SubscribeAsync(IConnectionHandle handle, string topic, string? consumerGroup = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Messaging/KafkaConnectionStrategy.cs
```csharp
public sealed class KafkaConnectionStrategy : MessagingConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public KafkaConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task PublishAsync(IConnectionHandle handle, string topic, byte[] message, Dictionary<string, string>? headers = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<byte[]> SubscribeAsync(IConnectionHandle handle, string topic, string? consumerGroup = null, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<(bool IsValid, string[] Errors)> ValidateConfigAsync(ConnectionConfig config, CancellationToken ct = default);
    internal sealed class KafkaConnectionWrapper(IProducer<string, byte[]> producer, ProducerConfig config);
}
```
```csharp
internal sealed class KafkaConnectionWrapper(IProducer<string, byte[]> producer, ProducerConfig config)
{
}
    public IProducer<string, byte[]> Producer { get; };
    public ProducerConfig Config { get; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Messaging/RabbitMqConnectionStrategy.cs
```csharp
public sealed class RabbitMqConnectionStrategy : MessagingConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public RabbitMqConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task PublishAsync(IConnectionHandle handle, string topic, byte[] message, Dictionary<string, string>? headers = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<byte[]> SubscribeAsync(IConnectionHandle handle, string topic, string? consumerGroup = null, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<(bool IsValid, string[] Errors)> ValidateConfigAsync(ConnectionConfig config, CancellationToken ct = default);
    internal sealed class RabbitMqConnectionWrapper(IConnection connection, IChannel channel);
}
```
```csharp
internal sealed class RabbitMqConnectionWrapper(IConnection connection, IChannel channel)
{
}
    public IConnection Connection { get; };
    public IChannel Channel { get; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Messaging/ApacheRocketMqConnectionStrategy.cs
```csharp
public class ApacheRocketMqConnectionStrategy : MessagingConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public ApacheRocketMqConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task PublishAsync(IConnectionHandle handle, string topic, byte[] message, Dictionary<string, string>? headers = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<byte[]> SubscribeAsync(IConnectionHandle handle, string topic, string? consumerGroup = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Messaging/ActiveMqConnectionStrategy.cs
```csharp
public class ActiveMqConnectionStrategy : MessagingConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public ActiveMqConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task PublishAsync(IConnectionHandle handle, string topic, byte[] message, Dictionary<string, string>? headers = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<byte[]> SubscribeAsync(IConnectionHandle handle, string topic, string? consumerGroup = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Messaging/ZeroMqConnectionStrategy.cs
```csharp
public class ZeroMqConnectionStrategy : MessagingConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public ZeroMqConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task PublishAsync(IConnectionHandle handle, string topic, byte[] message, Dictionary<string, string>? headers = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<byte[]> SubscribeAsync(IConnectionHandle handle, string topic, string? consumerGroup = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Messaging/AmazonMskConnectionStrategy.cs
```csharp
public class AmazonMskConnectionStrategy : MessagingConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public AmazonMskConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task PublishAsync(IConnectionHandle handle, string topic, byte[] message, Dictionary<string, string>? headers = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<byte[]> SubscribeAsync(IConnectionHandle handle, string topic, string? consumerGroup = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Messaging/NatsConnectionStrategy.cs
```csharp
public sealed class NatsConnectionStrategy : MessagingConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public NatsConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task PublishAsync(IConnectionHandle handle, string topic, byte[] message, Dictionary<string, string>? headers = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<byte[]> SubscribeAsync(IConnectionHandle handle, string topic, string? consumerGroup = null, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<(bool IsValid, string[] Errors)> ValidateConfigAsync(ConnectionConfig config, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Messaging/RedPandaConnectionStrategy.cs
```csharp
public class RedPandaConnectionStrategy : MessagingConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public RedPandaConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task PublishAsync(IConnectionHandle handle, string topic, byte[] message, Dictionary<string, string>? headers = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<byte[]> SubscribeAsync(IConnectionHandle handle, string topic, string? consumerGroup = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/NoSql/ScyllaDbConnectionStrategy.cs
```csharp
public class ScyllaDbConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public ScyllaDbConnectionStrategy(ILogger<ScyllaDbConnectionStrategy>? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/NoSql/RedisConnectionStrategy.cs
```csharp
public sealed class RedisConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public RedisConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
    public override Task<(bool IsValid, string[] Errors)> ValidateConfigAsync(ConnectionConfig config, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/NoSql/MongoDbConnectionStrategy.cs
```csharp
public sealed class MongoDbConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public MongoDbConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
    public override Task<(bool IsValid, string[] Errors)> ValidateConfigAsync(ConnectionConfig config, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/NoSql/InfluxDbConnectionStrategy.cs
```csharp
public class InfluxDbConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public InfluxDbConnectionStrategy(ILogger<InfluxDbConnectionStrategy>? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/NoSql/CosmosDbConnectionStrategy.cs
```csharp
public sealed class CosmosDbConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public CosmosDbConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
    public override Task<(bool IsValid, string[] Errors)> ValidateConfigAsync(ConnectionConfig config, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/NoSql/OpenSearchConnectionStrategy.cs
```csharp
public class OpenSearchConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public OpenSearchConnectionStrategy(ILogger<OpenSearchConnectionStrategy>? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/NoSql/ElasticsearchConnectionStrategy.cs
```csharp
public sealed class ElasticsearchConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public ElasticsearchConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
    public override Task<(bool IsValid, string[] Errors)> ValidateConfigAsync(ConnectionConfig config, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/NoSql/CassandraConnectionStrategy.cs
```csharp
public sealed class CassandraConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public CassandraConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
    public override Task<(bool IsValid, string[] Errors)> ValidateConfigAsync(ConnectionConfig config, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/NoSql/CouchbaseConnectionStrategy.cs
```csharp
public class CouchbaseConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public CouchbaseConnectionStrategy(ILogger<CouchbaseConnectionStrategy>? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/NoSql/DynamoDbConnectionStrategy.cs
```csharp
public sealed class DynamoDbConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public DynamoDbConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
    public override Task<(bool IsValid, string[] Errors)> ValidateConfigAsync(ConnectionConfig config, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/NoSql/ArangoDbConnectionStrategy.cs
```csharp
public class ArangoDbConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public ArangoDbConnectionStrategy(ILogger<ArangoDbConnectionStrategy>? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/NoSql/Neo4jConnectionStrategy.cs
```csharp
public class Neo4jConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public Neo4jConnectionStrategy(ILogger<Neo4jConnectionStrategy>? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Legacy/Db2MainframeConnectionStrategy.cs
```csharp
public class Db2MainframeConnectionStrategy : LegacyConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Db2MainframeConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    public override async Task<string> EmulateProtocolAsync(IConnectionHandle handle, string protocolCommand, CancellationToken ct = default);
    public override Task<string> TranslateCommandAsync(IConnectionHandle handle, string modernCommand, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Legacy/SmtpConnectionStrategy.cs
```csharp
public class SmtpConnectionStrategy : LegacyConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public SmtpConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<string> EmulateProtocolAsync(IConnectionHandle handle, string protocolCommand, CancellationToken ct = default);
    public override Task<string> TranslateCommandAsync(IConnectionHandle handle, string modernCommand, CancellationToken ct = default);
    public async Task<SmtpSendResult> SendEmailAsync(IConnectionHandle handle, string from, string to, string subject, string body, bool isHtml = false, SmtpAttachment[]? attachments = null, string[]? cc = null, string[]? bcc = null, CancellationToken ct = default);
    public async Task<SmtpBatchResult> SendBatchAsync(IConnectionHandle handle, string from, IReadOnlyList<SmtpEmailMessage> messages, CancellationToken ct = default);
}
```
```csharp
public sealed class SmtpConnectionInfo
{
}
    public string Host { get; set; };
    public int Port { get; set; }
    public string Username { get; set; };
    public string Password { get; set; };
    public bool UseSsl { get; set; }
}
```
```csharp
public sealed record SmtpAttachment
{
}
    public required string FileName { get; init; }
    public required byte[] Content { get; init; }
    public string ContentType { get; init; };
}
```
```csharp
public sealed record SmtpEmailMessage
{
}
    public required string To { get; init; }
    public required string Subject { get; init; }
    public required string Body { get; init; }
    public bool IsHtml { get; init; }
    public SmtpAttachment[]? Attachments { get; init; }
}
```
```csharp
public sealed record SmtpSendResult
{
}
    public bool Success { get; init; }
    public required string From { get; init; }
    public required string To { get; init; }
    public required string Subject { get; init; }
    public string? ErrorMessage { get; init; }
}
```
```csharp
public sealed record SmtpBatchResult
{
}
    public int TotalSent { get; init; }
    public int SuccessCount { get; init; }
    public int FailureCount { get; init; }
    public List<SmtpSendResult> Results { get; init; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Legacy/Tn5250ConnectionStrategy.cs
```csharp
public class Tn5250ConnectionStrategy : LegacyConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Tn5250ConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    public override async Task<string> EmulateProtocolAsync(IConnectionHandle handle, string protocolCommand, CancellationToken ct = default);
    public override Task<string> TranslateCommandAsync(IConnectionHandle handle, string modernCommand, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Legacy/ImsConnectionStrategy.cs
```csharp
public class ImsConnectionStrategy : LegacyConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public ImsConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    public override async Task<string> EmulateProtocolAsync(IConnectionHandle handle, string protocolCommand, CancellationToken ct = default);
    public override Task<string> TranslateCommandAsync(IConnectionHandle handle, string modernCommand, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Legacy/VsamConnectionStrategy.cs
```csharp
public class VsamConnectionStrategy : LegacyConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public VsamConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    public override async Task<string> EmulateProtocolAsync(IConnectionHandle handle, string protocolCommand, CancellationToken ct = default);
    public override Task<string> TranslateCommandAsync(IConnectionHandle handle, string modernCommand, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Legacy/As400ConnectionStrategy.cs
```csharp
public class As400ConnectionStrategy : LegacyConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public As400ConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    public override async Task<string> EmulateProtocolAsync(IConnectionHandle handle, string protocolCommand, CancellationToken ct = default);
    public override Task<string> TranslateCommandAsync(IConnectionHandle handle, string modernCommand, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Legacy/LdapConnectionStrategy.cs
```csharp
public class LdapConnectionStrategy : LegacyConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public LdapConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<string> EmulateProtocolAsync(IConnectionHandle handle, string protocolCommand, CancellationToken ct = default);
    public override Task<string> TranslateCommandAsync(IConnectionHandle handle, string modernCommand, CancellationToken ct = default);
    public Task<LdapSearchResult> SearchAsync(IConnectionHandle handle, string? searchFilter = null, string? searchBase = null, LdapSearchScope scope = LdapSearchScope.Subtree, string[]? attributes = null, int sizeLimit = 1000, CancellationToken ct = default);
    public Task<LdapOperationResult> AddEntryAsync(IConnectionHandle handle, string distinguishedName, Dictionary<string, string[]> attributes, CancellationToken ct = default);
    public Task<LdapOperationResult> ModifyEntryAsync(IConnectionHandle handle, string distinguishedName, LdapModification[] modifications, CancellationToken ct = default);
    public Task<LdapOperationResult> DeleteEntryAsync(IConnectionHandle handle, string distinguishedName, CancellationToken ct = default);
    public Task<LdapBindResult> BindAsync(IConnectionHandle handle, string? bindDn = null, string? password = null, CancellationToken ct = default);
}
```
```csharp
public sealed class LdapConnectionInfo
{
}
    public string Host { get; set; };
    public int Port { get; set; }
    public string BaseDn { get; set; };
    public string BindDn { get; set; };
    public string BindPassword { get; set; };
    public bool UseSsl { get; set; }
    public bool IsConnected { get; set; }
}
```
```csharp
public sealed record LdapSearchRequest
{
}
    public required string BaseDn { get; init; }
    public required string Filter { get; init; }
    public LdapSearchScope Scope { get; init; }
    public string[] Attributes { get; init; };
    public int SizeLimit { get; init; }
}
```
```csharp
public sealed record LdapEntry
{
}
    public required string DistinguishedName { get; init; }
    public Dictionary<string, string[]> Attributes { get; init; };
}
```
```csharp
public sealed record LdapSearchResult
{
}
    public bool Success { get; init; }
    public required string BaseDn { get; init; }
    public required string Filter { get; init; }
    public List<LdapEntry> Entries { get; init; };
    public LdapSearchRequest? Request { get; init; }
    public string? ErrorMessage { get; init; }
}
```
```csharp
public sealed record LdapModification
{
}
    public required LdapModificationType Operation { get; init; }
    public required string AttributeName { get; init; }
    public string[] Values { get; init; };
}
```
```csharp
public sealed record LdapOperationResult
{
}
    public bool Success { get; init; }
    public required string Operation { get; init; }
    public required string DistinguishedName { get; init; }
    public int ModificationCount { get; init; }
    public string? ErrorMessage { get; init; }
}
```
```csharp
public sealed record LdapBindResult
{
}
    public bool Success { get; init; }
    public required string BindDn { get; init; }
    public required string AuthenticationType { get; init; }
    public string? ErrorMessage { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Legacy/OleDbConnectionStrategy.cs
```csharp
public class OleDbConnectionStrategy : LegacyConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public OleDbConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<string> EmulateProtocolAsync(IConnectionHandle handle, string protocolCommand, CancellationToken ct = default);
    public override Task<string> TranslateCommandAsync(IConnectionHandle handle, string modernCommand, CancellationToken ct = default);
    public async Task<OleDbQueryResult> ExecuteQueryAsync(IConnectionHandle handle, string sql, Dictionary<string, object?>? parameters = null, int? maxRows = null, CancellationToken ct = default);
    public async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string sql, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public Task<OleDbSchemaResult> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
}
```
```csharp
public sealed record OleDbQueryResult
{
}
    public bool Success { get; init; }
    public List<Dictionary<string, object?>> Rows { get; init; };
    public int ColumnCount { get; init; }
    public string? ErrorMessage { get; init; }
}
```
```csharp
public sealed record OleDbSchemaResult
{
}
    public bool Success { get; init; }
    public List<OleDbTableInfo> Tables { get; init; };
}
```
```csharp
public sealed record OleDbTableInfo
{
}
    public required string TableName { get; init; }
    public required string TableType { get; init; }
    public string? Catalog { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Legacy/OdbcConnectionStrategy.cs
```csharp
public class OdbcConnectionStrategy : LegacyConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public OdbcConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<string> EmulateProtocolAsync(IConnectionHandle handle, string protocolCommand, CancellationToken ct = default);
    public override Task<string> TranslateCommandAsync(IConnectionHandle handle, string modernCommand, CancellationToken ct = default);
    public async Task<OdbcQueryResult> ExecuteQueryAsync(IConnectionHandle handle, string sql, Dictionary<string, object?>? parameters = null, int? maxRows = null, CancellationToken ct = default);
    public async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string sql, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public async Task<string> ExecuteScalarQueryAsync(IConnectionHandle handle, string sql, CancellationToken ct = default);
    public async Task<OdbcSchemaResult> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
}
```
```csharp
public sealed record OdbcQueryResult
{
}
    public bool Success { get; init; }
    public List<Dictionary<string, object?>> Rows { get; init; };
    public int ColumnCount { get; init; }
    public string? ErrorMessage { get; init; }
}
```
```csharp
public sealed record OdbcSchemaResult
{
}
    public bool Success { get; init; }
    public List<OdbcTableSchema> Tables { get; init; };
}
```
```csharp
public sealed record OdbcTableSchema
{
}
    public required string TableName { get; init; }
    public required string TableType { get; init; }
    public string? Catalog { get; init; }
    public string? Schema { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Legacy/CicsConnectionStrategy.cs
```csharp
public class CicsConnectionStrategy : LegacyConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public CicsConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    public override async Task<string> EmulateProtocolAsync(IConnectionHandle handle, string protocolCommand, CancellationToken ct = default);
    public override Task<string> TranslateCommandAsync(IConnectionHandle handle, string modernCommand, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Legacy/CobolCopybookConnectionStrategy.cs
```csharp
public class CobolCopybookConnectionStrategy : LegacyConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public CobolCopybookConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    public override async Task<string> EmulateProtocolAsync(IConnectionHandle handle, string protocolCommand, CancellationToken ct = default);
    public override Task<string> TranslateCommandAsync(IConnectionHandle handle, string modernCommand, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Legacy/Tn3270ConnectionStrategy.cs
```csharp
public class Tn3270ConnectionStrategy : LegacyConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Tn3270ConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    public override async Task<string> EmulateProtocolAsync(IConnectionHandle handle, string protocolCommand, CancellationToken ct = default);
    public override Task<string> TranslateCommandAsync(IConnectionHandle handle, string modernCommand, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Legacy/FtpSftpConnectionStrategy.cs
```csharp
public class FtpSftpConnectionStrategy : LegacyConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public FtpSftpConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<string> EmulateProtocolAsync(IConnectionHandle handle, string protocolCommand, CancellationToken ct = default);
    public override Task<string> TranslateCommandAsync(IConnectionHandle handle, string modernCommand, CancellationToken ct = default);
    public async Task<FtpListResult> ListDirectoryAsync(IConnectionHandle handle, string remotePath = "/", CancellationToken ct = default);
    public async Task<FtpTransferResult> DownloadFileAsync(IConnectionHandle handle, string remotePath, string localPath, CancellationToken ct = default);
    public async Task<FtpTransferResult> UploadFileAsync(IConnectionHandle handle, string localPath, string remotePath, CancellationToken ct = default);
    public async Task<bool> DeleteFileAsync(IConnectionHandle handle, string remotePath, CancellationToken ct = default);
}
```
```csharp
public sealed class FtpConnectionInfo
{
}
    public string Host { get; set; };
    public int Port { get; set; }
    public string Username { get; set; };
    public string Password { get; set; };
    public string Protocol { get; set; };
}
```
```csharp
public sealed record FtpListResult
{
}
    public bool Success { get; init; }
    public List<FtpEntry> Entries { get; init; };
    public string? ErrorMessage { get; init; }
}
```
```csharp
public sealed record FtpEntry
{
}
    public required string Name { get; init; }
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public string Permissions { get; init; };
}
```
```csharp
public sealed record FtpTransferResult
{
}
    public bool Success { get; init; }
    public long BytesTransferred { get; init; }
    public string? RemotePath { get; init; }
    public string? LocalPath { get; init; }
    public string? ErrorMessage { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Legacy/EdiConnectionStrategy.cs
```csharp
public class EdiConnectionStrategy : LegacyConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public EdiConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> EmulateProtocolAsync(IConnectionHandle handle, string protocolCommand, CancellationToken ct = default);
    public override Task<string> TranslateCommandAsync(IConnectionHandle handle, string modernCommand, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Blockchain/IpfsConnectionStrategy.cs
```csharp
public class IpfsConnectionStrategy : BlockchainConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public IpfsConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    public override Task<string> GetBlockAsync(IConnectionHandle handle, string blockIdentifier, CancellationToken ct = default);;
    public override Task<string> SubmitTransactionAsync(IConnectionHandle handle, string signedTransaction, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Blockchain/CosmosChainConnectionStrategy.cs
```csharp
public class CosmosChainConnectionStrategy : BlockchainConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public CosmosChainConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    public override Task<string> GetBlockAsync(IConnectionHandle handle, string blockIdentifier, CancellationToken ct = default);;
    public override Task<string> SubmitTransactionAsync(IConnectionHandle handle, string signedTransaction, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Blockchain/SolanaConnectionStrategy.cs
```csharp
public class SolanaConnectionStrategy : BlockchainConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public SolanaConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<string> GetBlockAsync(IConnectionHandle handle, string blockIdentifier, CancellationToken ct = default);;
    public override Task<string> SubmitTransactionAsync(IConnectionHandle handle, string signedTransaction, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Blockchain/AvalancheConnectionStrategy.cs
```csharp
public class AvalancheConnectionStrategy : BlockchainConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public AvalancheConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    public override Task<string> GetBlockAsync(IConnectionHandle handle, string blockIdentifier, CancellationToken ct = default);;
    public override Task<string> SubmitTransactionAsync(IConnectionHandle handle, string signedTransaction, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Blockchain/EthereumConnectionStrategy.cs
```csharp
public class EthereumConnectionStrategy : BlockchainConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public EthereumConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<string> GetBlockAsync(IConnectionHandle handle, string blockIdentifier, CancellationToken ct = default);;
    public override Task<string> SubmitTransactionAsync(IConnectionHandle handle, string signedTransaction, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Blockchain/TheGraphConnectionStrategy.cs
```csharp
public class TheGraphConnectionStrategy : BlockchainConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public TheGraphConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    public override Task<string> GetBlockAsync(IConnectionHandle handle, string blockIdentifier, CancellationToken ct = default);;
    public override Task<string> SubmitTransactionAsync(IConnectionHandle handle, string signedTransaction, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Blockchain/HyperledgerFabricConnectionStrategy.cs
```csharp
public class HyperledgerFabricConnectionStrategy : BlockchainConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public HyperledgerFabricConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    public override Task<string> GetBlockAsync(IConnectionHandle handle, string blockIdentifier, CancellationToken ct = default);;
    public override Task<string> SubmitTransactionAsync(IConnectionHandle handle, string signedTransaction, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Blockchain/PolygonConnectionStrategy.cs
```csharp
public class PolygonConnectionStrategy : BlockchainConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public PolygonConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    public override Task<string> GetBlockAsync(IConnectionHandle handle, string blockIdentifier, CancellationToken ct = default);;
    public override Task<string> SubmitTransactionAsync(IConnectionHandle handle, string signedTransaction, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Blockchain/ArweaveConnectionStrategy.cs
```csharp
public class ArweaveConnectionStrategy : BlockchainConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public ArweaveConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    public override Task<string> GetBlockAsync(IConnectionHandle handle, string blockIdentifier, CancellationToken ct = default);;
    public override Task<string> SubmitTransactionAsync(IConnectionHandle handle, string signedTransaction, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SaaS/AsanaConnectionStrategy.cs
```csharp
public class AsanaConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public AsanaConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);;
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SaaS/MondayConnectionStrategy.cs
```csharp
public class MondayConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public MondayConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);;
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SaaS/FreshDeskConnectionStrategy.cs
```csharp
public class FreshDeskConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public FreshDeskConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);;
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SaaS/TwilioConnectionStrategy.cs
```csharp
public class TwilioConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public TwilioConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);;
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
    public async Task<TwilioMessageResult> SendSmsAsync(IConnectionHandle handle, string to, string from, string body, string? statusCallbackUrl = null, CancellationToken ct = default);
    public async Task<TwilioCallResult> MakeCallAsync(IConnectionHandle handle, string to, string from, string twimlUrl, string? statusCallbackUrl = null, CancellationToken ct = default);
    public async Task<TwilioPhoneNumberListResult> ListAvailableNumbersAsync(IConnectionHandle handle, string countryCode = "US", bool smsEnabled = true, bool voiceEnabled = true, string? areaCode = null, CancellationToken ct = default);
    public async Task<TwilioMessageListResult> ListMessagesAsync(IConnectionHandle handle, string? to = null, string? from = null, int pageSize = 20, CancellationToken ct = default);
}
```
```csharp
public sealed record TwilioMessageResult
{
}
    public bool Success { get; init; }
    public string? MessageSid { get; init; }
    public string? Status { get; init; }
    public string? ErrorMessage { get; init; }
}
```
```csharp
public sealed record TwilioCallResult
{
}
    public bool Success { get; init; }
    public string? CallSid { get; init; }
    public string? Status { get; init; }
    public string? ErrorMessage { get; init; }
}
```
```csharp
public sealed record TwilioPhoneNumberListResult
{
}
    public bool Success { get; init; }
    public List<TwilioPhoneNumber> PhoneNumbers { get; init; };
    public string? ErrorMessage { get; init; }
}
```
```csharp
public sealed record TwilioPhoneNumber
{
}
    public required string PhoneNumber { get; init; }
    public required string FriendlyName { get; init; }
    public string? Locality { get; init; }
    public string? Region { get; init; }
}
```
```csharp
public sealed record TwilioMessageListResult
{
}
    public bool Success { get; init; }
    public List<TwilioMessageSummary> Messages { get; init; };
    public string? ErrorMessage { get; init; }
}
```
```csharp
public sealed record TwilioMessageSummary
{
}
    public required string Sid { get; init; }
    public required string To { get; init; }
    public required string From { get; init; }
    public required string Body { get; init; }
    public required string Status { get; init; }
    public required string Direction { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SaaS/WorkdayConnectionStrategy.cs
```csharp
public class WorkdayConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public WorkdayConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SaaS/PipedriveConnectionStrategy.cs
```csharp
public class PipedriveConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public PipedriveConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);;
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SaaS/SalesforceConnectionStrategy.cs
```csharp
public class SalesforceConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public SalesforceConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
    public async Task<SoqlQueryResult> ExecuteSoqlAsync(IConnectionHandle handle, string soqlQuery, CancellationToken ct = default);
    public async Task<SObjectResult> CreateSObjectAsync(IConnectionHandle handle, string objectType, Dictionary<string, object?> fields, CancellationToken ct = default);
    public async Task<SObjectResult> UpdateSObjectAsync(IConnectionHandle handle, string objectType, string objectId, Dictionary<string, object?> fields, CancellationToken ct = default);
    public async Task<SObjectResult> DeleteSObjectAsync(IConnectionHandle handle, string objectType, string objectId, CancellationToken ct = default);
    public async Task<BulkJobResult> CreateBulkJobAsync(IConnectionHandle handle, string objectType, string operation, string csvData, CancellationToken ct = default);
    public async Task<Dictionary<string, object>?> DescribeSObjectAsync(IConnectionHandle handle, string objectType, CancellationToken ct = default);
}
```
```csharp
public sealed record SoqlQueryResult
{
}
    public bool Success { get; init; }
    public int TotalSize { get; init; }
    public bool Done { get; init; }
    public List<Dictionary<string, object?>> Records { get; init; };
    public string? NextRecordsUrl { get; init; }
    public string? ErrorMessage { get; init; }
}
```
```csharp
public sealed record SObjectResult
{
}
    public bool Success { get; init; }
    public string? Id { get; init; }
    public string? ErrorMessage { get; init; }
}
```
```csharp
public sealed record BulkJobResult
{
}
    public bool Success { get; init; }
    public string? JobId { get; init; }
    public string? State { get; init; }
    public string? ErrorMessage { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SaaS/NetSuiteConnectionStrategy.cs
```csharp
public class NetSuiteConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public NetSuiteConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);;
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SaaS/GitHubConnectionStrategy.cs
```csharp
public class GitHubConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public GitHubConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);;
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
    public async Task<GitHubListResult<GitHubRepo>> ListRepositoriesAsync(IConnectionHandle handle, string? org = null, int perPage = 30, int page = 1, CancellationToken ct = default);
    public async Task<GitHubIssueResult> CreateIssueAsync(IConnectionHandle handle, string owner, string repo, string title, string? body = null, string[]? labels = null, string[]? assignees = null, CancellationToken ct = default);
    public async Task<GitHubPrResult> CreatePullRequestAsync(IConnectionHandle handle, string owner, string repo, string title, string head, string baseBranch, string? body = null, CancellationToken ct = default);
    public async Task<GitHubWebhookResult> CreateWebhookAsync(IConnectionHandle handle, string owner, string repo, string payloadUrl, string[] events, string secret, CancellationToken ct = default);
    public GitHubRateLimit GetRateLimitStatus();;
}
```
```csharp
public sealed record GitHubRepo
{
}
    public long Id { get; init; }
    public string Name { get; init; };
    public string Full_Name { get; init; };
    public bool Private { get; init; }
    public string? Description { get; init; }
    public string Html_Url { get; init; };
    public string Default_Branch { get; init; };
}
```
```csharp
public sealed record GitHubListResult<T>
{
}
    public bool Success { get; init; }
    public List<T> Items { get; init; };
    public string? ErrorMessage { get; init; }
}
```
```csharp
public sealed record GitHubIssueResult
{
}
    public bool Success { get; init; }
    public int Number { get; init; }
    public string? HtmlUrl { get; init; }
    public string? ErrorMessage { get; init; }
}
```
```csharp
public sealed record GitHubPrResult
{
}
    public bool Success { get; init; }
    public int Number { get; init; }
    public string? HtmlUrl { get; init; }
    public string? ErrorMessage { get; init; }
}
```
```csharp
public sealed record GitHubWebhookResult
{
}
    public bool Success { get; init; }
    public long WebhookId { get; init; }
    public string? ErrorMessage { get; init; }
}
```
```csharp
public sealed record GitHubRateLimit
{
}
    public int Remaining { get; init; }
    public DateTimeOffset ResetAt { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SaaS/JiraConnectionStrategy.cs
```csharp
public class JiraConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public JiraConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
    public async Task<JiraSearchResult> SearchIssuesAsync(IConnectionHandle handle, string jql, int startAt = 0, int maxResults = 50, string[]? fields = null, CancellationToken ct = default);
    public async Task<JiraIssueResult> CreateIssueAsync(IConnectionHandle handle, string projectKey, string issueType, string summary, string? description = null, string? assignee = null, string? priority = null, Dictionary<string, object>? customFields = null, CancellationToken ct = default);
    public async Task<JiraIssueResult> UpdateIssueAsync(IConnectionHandle handle, string issueKeyOrId, Dictionary<string, object> fields, CancellationToken ct = default);
    public async Task<JiraIssueResult> TransitionIssueAsync(IConnectionHandle handle, string issueKeyOrId, string transitionId, CancellationToken ct = default);
    public async Task<JiraSprintResult> GetActiveSprintsAsync(IConnectionHandle handle, int boardId, CancellationToken ct = default);
    public async Task<JiraWebhookResult> RegisterWebhookAsync(IConnectionHandle handle, string name, string url, string[] events, string? jqlFilter = null, CancellationToken ct = default);
}
```
```csharp
public sealed record JiraSearchResult
{
}
    public bool Success { get; init; }
    public int Total { get; init; }
    public int StartAt { get; init; }
    public int MaxResults { get; init; }
    public List<JiraIssue> Issues { get; init; };
    public string? ErrorMessage { get; init; }
}
```
```csharp
public sealed record JiraIssue
{
}
    public required string Id { get; init; }
    public required string Key { get; init; }
    public string Summary { get; init; };
    public string Status { get; init; };
    public string IssueType { get; init; };
}
```
```csharp
public sealed record JiraIssueResult
{
}
    public bool Success { get; init; }
    public string? IssueId { get; init; }
    public string? IssueKey { get; init; }
    public string? ErrorMessage { get; init; }
}
```
```csharp
public sealed record JiraSprintResult
{
}
    public bool Success { get; init; }
    public List<JiraSprint> Sprints { get; init; };
    public string? ErrorMessage { get; init; }
}
```
```csharp
public sealed record JiraSprint
{
}
    public int Id { get; init; }
    public string Name { get; init; };
    public string State { get; init; };
    public string? StartDate { get; init; }
    public string? EndDate { get; init; }
}
```
```csharp
public sealed record JiraWebhookResult
{
}
    public bool Success { get; init; }
    public string? WebhookName { get; init; }
    public string? ErrorMessage { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SaaS/SendGridConnectionStrategy.cs
```csharp
public class SendGridConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public SendGridConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);;
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
    public async Task<SendGridResult> SendEmailAsync(IConnectionHandle handle, string fromEmail, string fromName, string toEmail, string toName, string subject, string? plainTextContent = null, string? htmlContent = null, CancellationToken ct = default);
    public async Task<SendGridResult> SendBatchEmailAsync(IConnectionHandle handle, string fromEmail, string fromName, string subject, string htmlContent, IReadOnlyList<SendGridRecipient> recipients, CancellationToken ct = default);
    public async Task<SendGridResult> SendTemplateEmailAsync(IConnectionHandle handle, string fromEmail, string fromName, string toEmail, string toName, string templateId, Dictionary<string, object>? dynamicData = null, CancellationToken ct = default);
    public async Task<SendGridTemplateListResult> ListTemplatesAsync(IConnectionHandle handle, string generations = "dynamic", CancellationToken ct = default);
}
```
```csharp
public sealed record SendGridRecipient
{
}
    public required string Email { get; init; }
    public string? Name { get; init; }
    public Dictionary<string, object>? Substitutions { get; init; }
}
```
```csharp
public sealed record SendGridResult
{
}
    public bool Success { get; init; }
    public int StatusCode { get; init; }
    public string? MessageId { get; init; }
    public string? ErrorMessage { get; init; }
}
```
```csharp
public sealed record SendGridTemplateListResult
{
}
    public bool Success { get; init; }
    public List<SendGridTemplate> Templates { get; init; };
    public string? ErrorMessage { get; init; }
}
```
```csharp
public sealed record SendGridTemplate
{
}
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Generation { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SaaS/ZuoraConnectionStrategy.cs
```csharp
public class ZuoraConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public ZuoraConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SaaS/IntercomConnectionStrategy.cs
```csharp
public class IntercomConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public IntercomConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);;
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SaaS/ServiceNowConnectionStrategy.cs
```csharp
public class ServiceNowConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public ServiceNowConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
    public async Task<ServiceNowQueryResult> QueryTableAsync(IConnectionHandle handle, string tableName, string? query = null, int limit = 100, int offset = 0, string[]? fields = null, CancellationToken ct = default);
    public async Task<ServiceNowRecordResult> CreateRecordAsync(IConnectionHandle handle, string tableName, Dictionary<string, string> fields, CancellationToken ct = default);
    public async Task<ServiceNowRecordResult> UpdateRecordAsync(IConnectionHandle handle, string tableName, string sysId, Dictionary<string, string> fields, CancellationToken ct = default);
    public Task<ServiceNowRecordResult> CreateIncidentAsync(IConnectionHandle handle, string shortDescription, string description, int urgency = 2, int impact = 2, string? assignmentGroup = null, CancellationToken ct = default);
    public Task<ServiceNowRecordResult> CreateChangeRequestAsync(IConnectionHandle handle, string shortDescription, string description, string type = "normal", string? assignmentGroup = null, CancellationToken ct = default);
    public async Task<ServiceNowQueryResult> GetCatalogItemsAsync(IConnectionHandle handle, string? category = null, int limit = 50, CancellationToken ct = default);
}
```
```csharp
public sealed record ServiceNowQueryResult
{
}
    public bool Success { get; init; }
    public List<Dictionary<string, string>> Records { get; init; };
    public int TotalCount { get; init; }
    public string? ErrorMessage { get; init; }
}
```
```csharp
public sealed record ServiceNowRecordResult
{
}
    public bool Success { get; init; }
    public string? SysId { get; init; }
    public string? ErrorMessage { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SaaS/SapConnectionStrategy.cs
```csharp
public class SapConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public SapConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
    public async Task<string> FetchCsrfTokenAsync(IConnectionHandle handle, CancellationToken ct = default);
    public async Task<SapODataResult> ReadEntitySetAsync(IConnectionHandle handle, string servicePath, string entitySet, string? filter = null, string? select = null, int? top = null, int? skip = null, CancellationToken ct = default);
    public async Task<SapODataResult> CreateEntityAsync(IConnectionHandle handle, string servicePath, string entitySet, Dictionary<string, object?> fields, CancellationToken ct = default);
    public async Task<SapODataResult> CallFunctionImportAsync(IConnectionHandle handle, string servicePath, string functionName, Dictionary<string, string>? parameters = null, CancellationToken ct = default);
    public async Task<SapIdocResult> SendIdocAsync(IConnectionHandle handle, string idocType, string xmlPayload, CancellationToken ct = default);
}
```
```csharp
public sealed record SapODataResult
{
}
    public bool Success { get; init; }
    public List<Dictionary<string, object?>> Records { get; init; };
    public string? RawResponse { get; init; }
    public string? ErrorMessage { get; init; }
}
```
```csharp
public sealed record SapIdocResult
{
}
    public bool Success { get; init; }
    public string? IdocType { get; init; }
    public int StatusCode { get; init; }
    public string? ErrorMessage { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SaaS/OracleFusionConnectionStrategy.cs
```csharp
public class OracleFusionConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public OracleFusionConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);;
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SaaS/StripeConnectionStrategy.cs
```csharp
public class StripeConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public StripeConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);;
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
    public async Task<StripeResult> CreateCustomerAsync(IConnectionHandle handle, string email, string? name = null, Dictionary<string, string>? metadata = null, CancellationToken ct = default);
    public async Task<StripeResult> CreatePaymentIntentAsync(IConnectionHandle handle, long amount, string currency, string? customerId = null, string? paymentMethod = null, Dictionary<string, string>? metadata = null, CancellationToken ct = default);
    public async Task<StripeResult> CreateSubscriptionAsync(IConnectionHandle handle, string customerId, string priceId, string? paymentBehavior = "default_incomplete", CancellationToken ct = default);
    public async Task<StripeListResult> ListChargesAsync(IConnectionHandle handle, string? customerId = null, int limit = 10, string? startingAfter = null, CancellationToken ct = default);
    public bool VerifyWebhookSignature(string payload, string signatureHeader, long? toleranceSeconds = null);
}
```
```csharp
public sealed record StripeResult
{
}
    public bool Success { get; init; }
    public string? ObjectId { get; init; }
    public string? ObjectType { get; init; }
    public string? RawJson { get; init; }
    public string? ErrorMessage { get; init; }
}
```
```csharp
public sealed record StripeListResult
{
}
    public bool Success { get; init; }
    public List<Dictionary<string, object?>> Items { get; init; };
    public bool HasMore { get; init; }
    public string? ErrorMessage { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SaaS/SuccessFactorsConnectionStrategy.cs
```csharp
public class SuccessFactorsConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public SuccessFactorsConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SaaS/SlackConnectionStrategy.cs
```csharp
public class SlackConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public SlackConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);;
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
    public async Task<SlackMessageResult> PostMessageAsync(IConnectionHandle handle, string channel, string text, SlackBlock[]? blocks = null, string? threadTs = null, CancellationToken ct = default);
    public async Task<SlackChannelListResult> ListChannelsAsync(IConnectionHandle handle, int limit = 100, string? cursor = null, bool excludeArchived = true, CancellationToken ct = default);
    public async Task<SlackFileResult> UploadFileAsync(IConnectionHandle handle, string channel, byte[] fileContent, string filename, string? title = null, CancellationToken ct = default);
}
```
```csharp
public sealed record SlackBlock
{
}
    public required string Type { get; init; }
    public Dictionary<string, object>? Text { get; init; }
    public object[]? Elements { get; init; }
}
```
```csharp
public sealed record SlackMessageResult
{
}
    public bool Success { get; init; }
    public string? Timestamp { get; init; }
    public string? Channel { get; init; }
    public string? ErrorMessage { get; init; }
}
```
```csharp
public sealed record SlackChannelListResult
{
}
    public bool Success { get; init; }
    public List<SlackChannel> Channels { get; init; };
}
```
```csharp
public sealed record SlackChannel
{
}
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool IsPrivate { get; init; }
    public int MemberCount { get; init; }
}
```
```csharp
public sealed record SlackFileResult
{
}
    public bool Success { get; init; }
    public string? FileId { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SaaS/NotionConnectionStrategy.cs
```csharp
public class NotionConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public NotionConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);;
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SaaS/AirtableConnectionStrategy.cs
```csharp
public class AirtableConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public AirtableConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);;
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SaaS/DocuSignConnectionStrategy.cs
```csharp
public class DocuSignConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public DocuSignConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);;
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SaaS/ZendeskConnectionStrategy.cs
```csharp
public class ZendeskConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public ZendeskConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SaaS/HubSpotConnectionStrategy.cs
```csharp
public class HubSpotConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public HubSpotConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);;
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SaaS/ShopifyConnectionStrategy.cs
```csharp
public class ShopifyConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public ShopifyConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SaaS/MicrosoftDynamicsConnectionStrategy.cs
```csharp
public class MicrosoftDynamicsConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public MicrosoftDynamicsConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);;
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Innovations/SemanticTrafficCompressionStrategy.cs
```csharp
public class SemanticTrafficCompressionStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public SemanticTrafficCompressionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Innovations/DataSovereigntyRouterStrategy.cs
```csharp
public class DataSovereigntyRouterStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public DataSovereigntyRouterStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Innovations/PassiveEndpointFingerprintingStrategy.cs
```csharp
public class PassiveEndpointFingerprintingStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public PassiveEndpointFingerprintingStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```
```csharp
private class EndpointVitals
{
}
    public VitalsTrend TcpWindowSize { get; }
    public VitalsTrend TtfbMs { get; }
    public VitalsTrend RetransmissionRate { get; }
    public VitalsTrend RstFrequency { get; }
    public VitalsTrend RoundTripTimeMs { get; }
    public EndpointVitals(int bufferSize);
}
```
```csharp
private class VitalsTrend
{
}
    public int Count
{
    get
    {
        lock (_lock)
            return _count;
    }
}
    public VitalsTrend(int capacity);;
    public void Add(double value);
    public double[] ToArray();
    public static double ComputeSlope(VitalsTrend trend);
}
```
```csharp
private class FingerprintState
{
}
    public required EndpointVitals Vitals { get; set; }
    public double DegradationThreshold { get; set; }
    public int ProbeIntervalMs { get; set; }
    public double CompositeScore { get; set; }
    public long TotalProbes { get; set; }
    public long DegradationEvents { get; set; }
    public bool IsDegraded { get; set; }
    public int ConsecutiveFailures { get; set; }
    public CancellationTokenSource MonitorCts { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Innovations/ConnectionTelemetryFabricStrategy.cs
```csharp
public class ConnectionTelemetryFabricStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public ConnectionTelemetryFabricStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Innovations/NeuralProtocolTranslationStrategy.cs
```csharp
public class NeuralProtocolTranslationStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public NeuralProtocolTranslationStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Innovations/InverseMultiplexingStrategy.cs
```csharp
public class InverseMultiplexingStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public InverseMultiplexingStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```
```csharp
private class TransportChannel
{
}
    public string Name { get; set; };
    public required HttpClient Client { get; set; }
    public double ReliabilityScore { get; set; }
    public double SpeedScore { get; set; }
    public double CostScore { get; set; }
    public bool IsMetered { get; set; }
    public bool IsActive { get; set; }
}
```
```csharp
private class MuxState
{
}
    public List<TransportChannel> Channels { get; set; };
    public List<ChunkAssignment> Assignments { get; set; };
    public int ParityRatio { get; set; }
    public int ChunkSizeKb { get; set; }
    public int ChunksInFlight { get; set; }
    public int ReassemblyBufferSize { get; set; }
    public long ParityRepairs { get; set; }
    public long TotalChunksSent { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Innovations/SelfHealingConnectionPoolStrategy.cs
```csharp
public class SelfHealingConnectionPoolStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public SelfHealingConnectionPoolStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```
```csharp
private class PoolMetrics
{
}
    public readonly object SyncRoot = new();
    public int PoolSize { get; set; }
    public int ActiveConnections { get; set; }
    public double DegradationThresholdMs { get; set; }
    public int CircuitBreakerThreshold { get; set; }
    public double AverageLatencyMs { get; set; }
    public double EmaLatencyMs { get; set; }
    public int ConsecutiveFailures { get; set; }
    public CircuitState CircuitState { get; set; }
    public DateTimeOffset CircuitOpenedAt { get; set; }
    public DateTimeOffset LastScaleEvent { get; set; }
    public bool EnablePredictiveScaling { get; set; }
    public long TotalHealedConnections { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Innovations/FederatedMultiSourceQueryStrategy.cs
```csharp
public class FederatedMultiSourceQueryStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public FederatedMultiSourceQueryStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Innovations/BgpAwareGeopoliticalRoutingStrategy.cs
```csharp
public class BgpAwareGeopoliticalRoutingStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public BgpAwareGeopoliticalRoutingStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Innovations/SchemaEvolutionTrackerStrategy.cs
```csharp
public class SchemaEvolutionTrackerStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public SchemaEvolutionTrackerStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Innovations/ZeroTrustConnectionMeshStrategy.cs
```csharp
public class ZeroTrustConnectionMeshStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public ZeroTrustConnectionMeshStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Innovations/AdaptiveCircuitBreakerStrategy.cs
```csharp
public class AdaptiveCircuitBreakerStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public AdaptiveCircuitBreakerStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```
```csharp
private class BreakerState
{
}
    public readonly object SyncRoot = new();
    public CircuitState State { get; set; }
    public int FailureCount { get; set; }
    public int SuccessCount { get; set; }
    public double CanaryPassRate { get; set; }
    public int BaseFailureThreshold { get; set; }
    public double MinCanaryRate { get; set; }
    public int SuccessStreak { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public TimeSpan OpenDuration { get; set; }
    public CircularBuffer<bool> RecentResults { get; set; };
}
```
```csharp
private class BreakerConnectionState
{
}
    public BreakerState Breaker { get; set; };
    public DateTimeOffset? RateLimitResetAt { get; set; }
    public int? RateLimitRemaining { get; set; }
    public long TotalRequests { get; set; }
    public long TotalTripped { get; set; }
}
```
```csharp
private class CircularBuffer<T>
{
}
    public int Count
{
    get
    {
        lock (_lock)
            return _count;
    }
}
    public CircularBuffer(int capacity);;
    public void Add(T item);
    public int CountWhere(Func<T, bool> predicate);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Innovations/UniversalCdcEngineStrategy.cs
```csharp
public class UniversalCdcEngineStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public UniversalCdcEngineStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Innovations/QuantumSafeConnectionStrategy.cs
```csharp
public class QuantumSafeConnectionStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public QuantumSafeConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Innovations/PidAdaptiveBackpressureStrategy.cs
```csharp
public class PidAdaptiveBackpressureStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public PidAdaptiveBackpressureStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```
```csharp
private class PidState
{
}
    public double Kp { get; set; }
    public double Ki { get; set; }
    public double Kd { get; set; }
    public double LastError { get; set; }
    public double Integral { get; set; }
    public double IntegralClamp { get; set; }
    public DateTimeOffset LastTimestamp { get; set; }
}
```
```csharp
private class PidConnectionState
{
}
    public readonly object SyncRoot = new();
    public double TargetAckRate { get; set; }
    public double MeasuredAckRate { get; set; }
    public int CurrentWindowKb { get; set; }
    public int MinWindowKb { get; set; }
    public int MaxWindowKb { get; set; }
    public int MonitorIntervalMs { get; set; }
    public PidState Pid { get; set; };
    public double PidOutput { get; set; }
    public double StabilityPercent { get; set; }
    public long TotalAdjustments { get; set; }
    public CircularBuffer<double> RecentOutputs { get; set; };
    public CircularBuffer<double> AckMeasurements { get; set; };
    public CancellationTokenSource MonitorCts { get; set; };
}
```
```csharp
private class CircularBuffer<T>
{
}
    public int Count
{
    get
    {
        lock (_lock)
            return _count;
    }
}
    public CircularBuffer(int capacity);;
    public void Add(T item);
    public T[] ToArray();
    public double Average();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Innovations/ChameleonProtocolEmulatorStrategy.cs
```csharp
public class ChameleonProtocolEmulatorStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public ChameleonProtocolEmulatorStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```
```csharp
private sealed class EmulatorState : IDisposable
{
}
    public TcpListener Listener { get; }
    public CancellationTokenSource CancellationSource { get; }
    public string Protocol { get; }
    public EmulatorState(TcpListener listener, CancellationTokenSource cts, string protocol);
    public void Dispose();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Innovations/PredictiveFailoverStrategy.cs
```csharp
public class PredictiveFailoverStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public PredictiveFailoverStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```
```csharp
private class FailoverState
{
}
    public string PrimaryEndpoint { get; set; };
    public string[] ReplicaEndpoints { get; set; };
    public string ActiveEndpoint { get; set; };
    public double FailoverThreshold { get; set; }
    public CircularBuffer<double> LatencyWindow { get; set; };
    public CircularBuffer<bool> ErrorWindow { get; set; };
    public double HealthScore { get; set; }
    public int FailoverCount { get; set; }
    public DateTimeOffset LastFailoverAt { get; set; }
}
```
```csharp
private class CircularBuffer<T>
{
}
    public int Count
{
    get
    {
        lock (_lock)
            return _count;
    }
}
    public CircularBuffer(int capacity);
    public void Add(T item);
    public T[] ToArray();
    public int CountWhere(Func<T, bool> predicate);
    public double Average();
    public void Clear();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Innovations/IntentBasedServiceDiscoveryStrategy.cs
```csharp
public class IntentBasedServiceDiscoveryStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public IntentBasedServiceDiscoveryStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Innovations/PredictiveMultipathingStrategy.cs
```csharp
public class PredictiveMultipathingStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public PredictiveMultipathingStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```
```csharp
private class PathProbeResult
{
}
    public string Endpoint { get; set; };
    public bool IsReachable { get; set; }
    public double LatencyMs { get; set; }
    public double JitterMs { get; set; }
    public double Weight { get; set; }
    public double NormalizedWeight { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Innovations/AutomatedApiHealingStrategy.cs
```csharp
public class AutomatedApiHealingStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public AutomatedApiHealingStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Innovations/TimeTravelQueryStrategy.cs
```csharp
public class TimeTravelQueryStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public TimeTravelQueryStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Innovations/AdaptiveProtocolNegotiationStrategy.cs
```csharp
public class AdaptiveProtocolNegotiationStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public AdaptiveProtocolNegotiationStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Innovations/PredictivePoolWarmingStrategy.cs
```csharp
public class PredictivePoolWarmingStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public PredictivePoolWarmingStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```
```csharp
private class TrafficHistogram
{
}
    public TrafficHistogram(double alpha);
    public void RecordDemand(int bucket, int demand);
    public double PredictDemand(int currentBucket, int minutes);
    public double GetBucketValue(int bucket);
    public double GetCoveragePercent();
}
```
```csharp
private class PoolWarmingState
{
}
    public required TrafficHistogram Histogram { get; set; }
    public int CurrentPoolSize { get; set; }
    public int BasePoolSize { get; set; }
    public int MaxPoolSize { get; set; }
    public int LookAheadMinutes { get; set; }
    public int CoolingDelayMinutes { get; set; }
    public int CheckIntervalSec { get; set; }
    public long WarmingEvents { get; set; }
    public long CoolingEvents { get; set; }
    public int ActiveConnections;
    public DateTimeOffset LastCoolingCheck { get; set; }
    public DateTimeOffset? LowDemandSince { get; set; }
    public CancellationTokenSource MonitorCts { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Innovations/BatteryConsciousHandshakeStrategy.cs
```csharp
public class BatteryConsciousHandshakeStrategy : ConnectionStrategyBase
{
#endregion
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public BatteryConsciousHandshakeStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```
```csharp
[StructLayout(LayoutKind.Sequential)]
private struct SystemPowerStatus
{
}
    public byte ACLineStatus;
    public byte BatteryFlag;
    public byte BatteryLifePercent;
    public byte SystemStatusFlag;
    public int BatteryLifeTime;
    public int BatteryFullLifeTime;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Innovations/ConnectionDigitalTwinStrategy.cs
```csharp
public class ConnectionDigitalTwinStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public ConnectionDigitalTwinStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CrossCutting/CredentialResolver.cs
```csharp
public sealed class CredentialResolver
{
}
    public const string KeystoreGetTopic = "keystore.get";
    public const string KeystoreStatusTopic = "keystore.status";
    public CredentialResolver(IMessageBus? messageBus, ILogger? logger = null, TimeSpan? requestTimeout = null);
    public async Task<ResolvedCredential> ResolveCredentialAsync(ConnectionConfig config, string credentialKey, CancellationToken ct = default);
    public async Task<bool> IsKeystoreAvailableAsync(CancellationToken ct = default);
}
```
```csharp
public sealed record ResolvedCredential(string Value, CredentialSource Source, string? SecondaryValue, string AuthMethod, DateTimeOffset? ExpiresAt)
{
}
    public bool IsExpired;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CrossCutting/ConnectionInterceptorPipeline.cs
```csharp
public sealed class ConnectionInterceptorPipeline
{
}
    public ConnectionInterceptorPipeline(ILogger? logger = null);
    public int Count
{
    get
    {
        lock (_registrationLock)
        {
            return _interceptors.Count;
        }
    }
}
    public void Register(IConnectionInterceptor interceptor);
    public bool Unregister(IConnectionInterceptor interceptor);
    public IReadOnlyList<IConnectionInterceptor> GetInterceptors();
    public async Task<BeforeRequestContext> ExecuteBeforeRequestAsync(BeforeRequestContext context, CancellationToken ct = default);
    public async Task<AfterResponseContext> ExecuteAfterResponseAsync(AfterResponseContext context, CancellationToken ct = default);
    public async Task ExecuteSchemaDiscoveredAsync(SchemaDiscoveryContext context, CancellationToken ct = default);
    public async Task<ErrorContext> ExecuteErrorAsync(ErrorContext context, CancellationToken ct = default);
    public async Task ExecuteConnectionEstablishedAsync(ConnectionEstablishedContext context, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CrossCutting/ConnectionCircuitBreaker.cs
```csharp
public sealed class ConnectionCircuitBreaker : IDisposable
{
}
    public void RegisterEndpoint(string endpointKey, CircuitBreakerConfiguration? config = null);
    public bool AllowRequest(string endpointKey);
    public void RecordSuccess(string endpointKey);
    public void RecordFailure(string endpointKey);
    public CircuitBreakerStatus? GetStatus(string endpointKey);
    public void Reset(string endpointKey);
    public void Dispose();
}
```
```csharp
public sealed record CircuitBreakerConfiguration(int FailureThreshold = 5, TimeSpan? RecoveryTimeoutValue = null)
{
}
    public TimeSpan RecoveryTimeout { get; };
}
```
```csharp
internal sealed class CircuitState
{
}
    public CircuitBreakerConfiguration Config { get; }
    public SemaphoreSlim HalfOpenGate { get; }
    public int State;
    public long ConsecutiveFailures;
    public long TotalFailures;
    public long SuccessCount;
    public long RejectedCount;
    public DateTimeOffset? OpenedAt
{
    get
    {
        var ticks = Interlocked.Read(ref _openedAtTicks);
        return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    set
    {
        Interlocked.Exchange(ref _openedAtTicks, value?.UtcTicks ?? 0);
    }
}
    public CircuitState(CircuitBreakerConfiguration config);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CrossCutting/ConnectionAuditLogger.cs
```csharp
public sealed class ConnectionAuditLogger : IAsyncDisposable
{
}
    public const string AuditTopicPrefix = "connector.audit";
    public ConnectionAuditLogger(IMessageBus? messageBus, ILogger? logger = null, TimeSpan? flushInterval = null, int maxBufferSize = 500);
    public void LogConnect(string strategyId, string connectionId, TimeSpan duration, bool success, string? errorMessage = null);
    public void LogDisconnect(string strategyId, string connectionId, TimeSpan duration, string? reason = null);
    public void LogTest(string strategyId, string connectionId, TimeSpan duration, bool isAlive);
    public void LogHealthCheck(string strategyId, string connectionId, TimeSpan duration, bool isHealthy, string statusMessage);
    public IReadOnlyList<AuditEntry> GetBufferedEntries();;
    public async Task FlushAsync();
    public async ValueTask DisposeAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CrossCutting/MessageBusInterceptorBridge.cs
```csharp
public record TransformResponse
{
}
    public bool Success { get; init; }
    public Dictionary<string, object> Payload { get; init; };
    public string? ErrorMessage { get; init; }
    public TimeSpan ProcessingTime { get; init; }
}
```
```csharp
public sealed class MessageBusInterceptorBridge : ConnectionInterceptorBase
{
}
    public const string BeforeRequestTopic = "connector.interceptor.before-request";
    public const string AfterResponseTopic = "connector.interceptor.after-response";
    public const string SchemaDiscoveredTopic = "connector.interceptor.on-schema";
    public const string ErrorTopic = "connector.interceptor.on-error";
    public const string ConnectionEstablishedTopic = "connector.interceptor.on-connect";
    public const string TransformRequestTopic = "intelligence.connector.transform-request";
    public const string OptimizeQueryTopic = "intelligence.connector.optimize-query";
    public const string TransformResponseTopic = "intelligence.connector.transform-response";
    public const string OptimizeQueryResponseTopic = "intelligence.connector.optimize-query.response";
    public override string Name;;
    public override int Priority;;
    public MessageBusInterceptorBridge(IMessageBus messageBus, ILogger? logger = null, bool publishAsync = true, IntelligenceIntegrationMode integrationMode = IntelligenceIntegrationMode.AsyncOnly, TimeSpan? transformTimeout = null);
    public override async Task<BeforeRequestContext> OnBeforeRequestAsync(BeforeRequestContext context, CancellationToken ct = default);
    public override async Task<AfterResponseContext> OnAfterResponseAsync(AfterResponseContext context, CancellationToken ct = default);
    public override async Task OnSchemaDiscoveredAsync(SchemaDiscoveryContext context, CancellationToken ct = default);
    public override async Task<ErrorContext> OnErrorAsync(ErrorContext context, CancellationToken ct = default);
    public override async Task OnConnectionEstablishedAsync(ConnectionEstablishedContext context, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CrossCutting/InterceptorContext.cs
```csharp
public sealed record BeforeRequestContext(string StrategyId, string ConnectionId, DateTimeOffset Timestamp, string OperationType, Dictionary<string, object> RequestPayload)
{
}
    public bool IsCancelled { get; set; }
    public string? CancellationReason { get; set; }
    public Dictionary<string, object> InterceptorMetadata { get; init; };
}
```
```csharp
public sealed record AfterResponseContext(string StrategyId, string ConnectionId, DateTimeOffset Timestamp, string OperationType, TimeSpan Duration, bool Success, Dictionary<string, object> ResponsePayload)
{
}
    public string? ErrorMessage { get; init; }
    public Dictionary<string, object> InterceptorMetadata { get; init; };
}
```
```csharp
public sealed record SchemaDiscoveryContext(string StrategyId, string ConnectionId, DateTimeOffset Timestamp, string SchemaName, IReadOnlyList<SchemaFieldInfo> Fields)
{
}
    public string DiscoveryMethod { get; init; };
    public Dictionary<string, object> InterceptorMetadata { get; init; };
}
```
```csharp
public sealed record ErrorContext(string StrategyId, string ConnectionId, DateTimeOffset Timestamp, Exception Exception, string OperationType)
{
}
    public bool IsHandled { get; set; }
    public object? FallbackResult { get; set; }
    public Dictionary<string, object> InterceptorMetadata { get; init; };
}
```
```csharp
public sealed record ConnectionEstablishedContext(string StrategyId, string ConnectionId, DateTimeOffset Timestamp, IReadOnlyDictionary<string, object> ConnectionInfo, TimeSpan Latency)
{
}
    public Dictionary<string, object> InterceptorMetadata { get; init; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CrossCutting/BulkConnectionTester.cs
```csharp
public sealed class BulkConnectionTester
{
}
    public BulkConnectionTester(ILogger? logger = null, int maxParallelism = 10);
    public void Register(IConnectionStrategy strategy, IEnumerable<IConnectionHandle> handles);
    public void AddHandle(string strategyId, IConnectionHandle handle);
    public void RemoveHandle(string strategyId, string connectionId);
    public async Task<BulkHealthReport> RunHealthChecksAsync(TimeSpan? timeout = null, CancellationToken ct = default);
}
```
```csharp
public sealed record BulkHealthReport(int TotalChecked, int HealthyCount, int UnhealthyCount, TimeSpan TotalDuration, DateTimeOffset CheckedAt, IReadOnlyList<ConnectionCheckResult> Results)
{
}
    public double HealthPercentage;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CrossCutting/ConnectionMetricsCollector.cs
```csharp
public sealed class ConnectionMetricsCollector : IAsyncDisposable
{
}
    public const string MetricsTopicPrefix = "connector.metrics";
    public ConnectionMetricsCollector(IMessageBus? messageBus = null, ILogger? logger = null, TimeSpan? publishInterval = null);
    public void RecordConnection(string strategyId, TimeSpan latency, bool success);
    public void RecordTransfer(string strategyId, long bytesSent, long bytesReceived);
    public void RecordError(string strategyId, string errorType);
    public void SetActiveConnections(string strategyId, int activeCount);
    public MetricsSnapshot? GetSnapshot(string strategyId);
    public Dictionary<string, MetricsSnapshot> GetAllSnapshots();
    public async Task PublishMetricsAsync();
    public async ValueTask DisposeAsync();
}
```
```csharp
public sealed record MetricsSnapshot(string StrategyId, long TotalConnections, long SuccessfulConnections, long FailedConnections, int ActiveConnections, long ErrorCount, long BytesSent, long BytesReceived, long TransferCount, double AverageLatencyMs, double P50LatencyMs, double P95LatencyMs, double P99LatencyMs, Dictionary<string, long> ErrorsByType, DateTimeOffset Timestamp)
{
}
    public double SuccessRate;;
}
```
```csharp
internal sealed class StrategyMetrics
{
}
    public long TotalConnections;
    public long SuccessfulConnections;
    public long FailedConnections;
    public long ActiveConnections;
    public long ErrorCount;
    public long BytesSent;
    public long BytesReceived;
    public long TransferCount;
    public readonly ConcurrentQueue<double> LatencySamples = new();
    public readonly BoundedDictionary<string, long> ErrorsByType = new BoundedDictionary<string, long>(1000);
    public readonly int MaxSamples = 1000;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CrossCutting/AutoReconnectionHandler.cs
```csharp
public sealed class AutoReconnectionHandler : IAsyncDisposable
{
}
    public event Func<ReconnectionEventArgs, Task>? OnReconnecting;
    public event Func<ReconnectionEventArgs, Task>? OnReconnected;
    public event Func<ReconnectionEventArgs, Task>? OnReconnectionFailed;
    public AutoReconnectionHandler(IConnectionStrategy strategy, ReconnectionConfiguration? config = null, ILogger? logger = null);
    public IConnectionHandle? CurrentHandle;;
    public bool IsReconnecting;;
    public int TotalReconnectAttempts;;
    public void Attach(IConnectionHandle handle, ConnectionConfig connectionConfig, CancellationToken ct = default);
    public Task NotifyDisconnectedAsync();
    public async ValueTask DisposeAsync();
}
```
```csharp
public sealed record ReconnectionConfiguration(int MaxAttempts = 10, TimeSpan? BaseDelayValue = null, TimeSpan? MaxDelayValue = null, double JitterFactor = 0.25, TimeSpan? ProbeIntervalValue = null)
{
}
    public TimeSpan BaseDelay { get; };
    public TimeSpan MaxDelay { get; };
    public TimeSpan ProbeInterval { get; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CrossCutting/ConnectionPoolManager.cs
```csharp
public sealed class ConnectionPoolManager : IAsyncDisposable
{
}
    public ConnectionPoolManager(TimeSpan? evictionInterval = null);
    public void RegisterPool(string strategyId, PoolConfiguration poolConfig);
    public async Task<IConnectionHandle> GetConnectionAsync(string strategyId, Func<CancellationToken, Task<IConnectionHandle>> factory, CancellationToken ct = default);
    public async Task ReturnConnection(string strategyId, IConnectionHandle handle);
    public async Task DrainPool(string strategyId);
    public Dictionary<string, PoolStats> GetPoolStats();
    public async ValueTask DisposeAsync();
}
```
```csharp
public sealed record PoolConfiguration(int MinSize = 2, int MaxSize = 20, TimeSpan? IdleTimeoutValue = null, TimeSpan? HealthCheckIntervalValue = null)
{
}
    public TimeSpan IdleTimeout { get; };
    public TimeSpan HealthCheckInterval { get; };
}
```
```csharp
internal sealed class StrategyPool
{
}
    public PoolConfiguration Config { get; }
    public ConcurrentQueue<PooledConnection> Connections { get; };
    public SemaphoreSlim Semaphore { get; }
    public long HitCount;
    public long MissCount;
    public long ActiveCount;
    public StrategyPool(PoolConfiguration config);
}
```
```csharp
internal sealed class PooledConnection
{
}
    public IConnectionHandle Handle { get; }
    public DateTimeOffset LastUsed { get; set; }
    public PooledConnection(IConnectionHandle handle);
    public bool IsExpired(TimeSpan idleTimeout);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CrossCutting/ConnectionTagManager.cs
```csharp
public sealed class ConnectionTagManager
{
}
    public static class WellKnownTags;
    public void SetTags(string connectionId, Dictionary<string, string> tags);
    public void SetTag(string connectionId, string key, string value);
    public bool RemoveTag(string connectionId, string key);
    public bool RemoveConnection(string connectionId);;
    public IReadOnlyDictionary<string, string> GetTags(string connectionId);
    public IReadOnlyList<string> QueryByTag(string key, string value);
    public IReadOnlyList<string> QueryByTags(Dictionary<string, string> filters);
    public IReadOnlyList<string> QueryByTagPrefix(string key, string valuePrefix);
    public Dictionary<string, List<string>> GroupByTag(string key);
    public int Count;;
    public Dictionary<string, Dictionary<string, int>> GetTagSummary();
}
```
```csharp
public static class WellKnownTags
{
}
    public const string Environment = "env";
    public const string Tenant = "tenant";
    public const string Region = "region";
    public const string Strategy = "strategy";
    public const string Priority = "priority";
    public const string Owner = "owner";
    public const string Application = "app";
}
```
```csharp
private sealed class ConnectionTagSet
{
}
    public string ConnectionId { get; }
    public BoundedDictionary<string, string> Tags { get; };
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset LastModified { get; set; }
    public ConnectionTagSet(string connectionId);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CrossCutting/IConnectionInterceptor.cs
```csharp
public interface IConnectionInterceptor
{
}
    string Name { get; }
    int Priority { get; }
    bool IsEnabled { get; }
    Task<BeforeRequestContext> OnBeforeRequestAsync(BeforeRequestContext context, CancellationToken ct = default);;
    Task<AfterResponseContext> OnAfterResponseAsync(AfterResponseContext context, CancellationToken ct = default);;
    Task OnSchemaDiscoveredAsync(SchemaDiscoveryContext context, CancellationToken ct = default);;
    Task<ErrorContext> OnErrorAsync(ErrorContext context, CancellationToken ct = default);;
    Task OnConnectionEstablishedAsync(ConnectionEstablishedContext context, CancellationToken ct = default);;
}
```
```csharp
public abstract class ConnectionInterceptorBase : IConnectionInterceptor
{
}
    public abstract string Name { get; }
    public virtual int Priority;;
    public virtual bool IsEnabled;;
    public virtual Task<BeforeRequestContext> OnBeforeRequestAsync(BeforeRequestContext context, CancellationToken ct = default);;
    public virtual Task<AfterResponseContext> OnAfterResponseAsync(AfterResponseContext context, CancellationToken ct = default);;
    public virtual Task OnSchemaDiscoveredAsync(SchemaDiscoveryContext context, CancellationToken ct = default);;
    public virtual Task<ErrorContext> OnErrorAsync(ErrorContext context, CancellationToken ct = default);;
    public virtual Task OnConnectionEstablishedAsync(ConnectionEstablishedContext context, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CrossCutting/ConnectionRateLimiter.cs
```csharp
public sealed class ConnectionRateLimiter : IAsyncDisposable
{
}
    public ConnectionRateLimiter(TimeSpan? replenishInterval = null);
    public void RegisterStrategy(string strategyId, RateLimitConfiguration rateConfig);
    public async Task<RateLimitLease> AcquireAsync(string strategyId, CancellationToken ct = default);
    public bool TryAcquire(string strategyId, out RateLimitLease? lease);
    public RateLimitStats? GetStats(string strategyId);
    public async ValueTask DisposeAsync();
}
```
```csharp
public sealed class RateLimitLease : IDisposable
{
}
    internal RateLimitLease(TokenBucket bucket);;
    public void Dispose();
}
```
```csharp
internal sealed class TokenBucket
{
}
    public RateLimitConfiguration Config { get; }
    public SemaphoreSlim Semaphore { get; }
    public long LastReplenishTimestamp;
    public long ConsumedCount;
    public long RejectedCount;
    public TokenBucket(RateLimitConfiguration config);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SpecializedDb/VoltDbConnectionStrategy.cs
```csharp
public class VoltDbConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public VoltDbConnectionStrategy(ILogger<VoltDbConnectionStrategy>? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SpecializedDb/EventStoreDbConnectionStrategy.cs
```csharp
public class EventStoreDbConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public EventStoreDbConnectionStrategy(ILogger<EventStoreDbConnectionStrategy>? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SpecializedDb/FoundationDbConnectionStrategy.cs
```csharp
public class FoundationDbConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public FoundationDbConnectionStrategy(ILogger<FoundationDbConnectionStrategy>? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SpecializedDb/MemgraphConnectionStrategy.cs
```csharp
public class MemgraphConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public MemgraphConnectionStrategy(ILogger<MemgraphConnectionStrategy>? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SpecializedDb/ApacheIgniteConnectionStrategy.cs
```csharp
public class ApacheIgniteConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public ApacheIgniteConnectionStrategy(ILogger<ApacheIgniteConnectionStrategy>? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SpecializedDb/ClickHouseConnectionStrategy.cs
```csharp
public class ClickHouseConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public ClickHouseConnectionStrategy(ILogger<ClickHouseConnectionStrategy>? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SpecializedDb/ApacheDruidConnectionStrategy.cs
```csharp
public class ApacheDruidConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public ApacheDruidConnectionStrategy(ILogger<ApacheDruidConnectionStrategy>? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SpecializedDb/DuckDbConnectionStrategy.cs
```csharp
public class DuckDbConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public DuckDbConnectionStrategy(ILogger<DuckDbConnectionStrategy>? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SpecializedDb/HazelcastConnectionStrategy.cs
```csharp
public class HazelcastConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public HazelcastConnectionStrategy(ILogger<HazelcastConnectionStrategy>? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SpecializedDb/CrateDbConnectionStrategy.cs
```csharp
public class CrateDbConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public CrateDbConnectionStrategy(ILogger<CrateDbConnectionStrategy>? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SpecializedDb/FaunaDbConnectionStrategy.cs
```csharp
public class FaunaDbConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public FaunaDbConnectionStrategy(ILogger<FaunaDbConnectionStrategy>? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SpecializedDb/NuoDbConnectionStrategy.cs
```csharp
public class NuoDbConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public NuoDbConnectionStrategy(ILogger<NuoDbConnectionStrategy>? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SpecializedDb/RethinkDbConnectionStrategy.cs
```csharp
public class RethinkDbConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public RethinkDbConnectionStrategy(ILogger<RethinkDbConnectionStrategy>? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SpecializedDb/QuestDbConnectionStrategy.cs
```csharp
public class QuestDbConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public QuestDbConnectionStrategy(ILogger<QuestDbConnectionStrategy>? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SpecializedDb/SurrealDbConnectionStrategy.cs
```csharp
public class SurrealDbConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public SurrealDbConnectionStrategy(ILogger<SurrealDbConnectionStrategy>? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SpecializedDb/TigerGraphConnectionStrategy.cs
```csharp
public class TigerGraphConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public TigerGraphConnectionStrategy(ILogger<TigerGraphConnectionStrategy>? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SpecializedDb/ApachePinotConnectionStrategy.cs
```csharp
public class ApachePinotConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public ApachePinotConnectionStrategy(ILogger<ApachePinotConnectionStrategy>? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/SpecializedDb/SingleStoreConnectionStrategy.cs
```csharp
public class SingleStoreConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public SingleStoreConnectionStrategy(ILogger<SingleStoreConnectionStrategy>? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/FileSystem/MinioConnectionStrategy.cs
```csharp
public class MinioConnectionStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public MinioConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/FileSystem/SmbConnectionStrategy.cs
```csharp
public class SmbConnectionStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public SmbConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/FileSystem/CephConnectionStrategy.cs
```csharp
public class CephConnectionStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public CephConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/FileSystem/HdfsConnectionStrategy.cs
```csharp
public class HdfsConnectionStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public HdfsConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/FileSystem/GlusterFsConnectionStrategy.cs
```csharp
public class GlusterFsConnectionStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public GlusterFsConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/FileSystem/NfsConnectionStrategy.cs
```csharp
public class NfsConnectionStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public NfsConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/FileSystem/LustreConnectionStrategy.cs
```csharp
public class LustreConnectionStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public LustreConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/FileSystem/MinioFsConnectionStrategy.cs
```csharp
public class MinioFsConnectionStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public MinioFsConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CloudWarehouse/DremioConnectionStrategy.cs
```csharp
public class DremioConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public DremioConnectionStrategy(ILogger<DremioConnectionStrategy>? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);;
    public override Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);;
    public override Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CloudWarehouse/RedshiftConnectionStrategy.cs
```csharp
public class RedshiftConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public RedshiftConnectionStrategy(ILogger<RedshiftConnectionStrategy>? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);;
    public override Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);;
    public override Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CloudWarehouse/MotherDuckConnectionStrategy.cs
```csharp
public class MotherDuckConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public MotherDuckConnectionStrategy(ILogger<MotherDuckConnectionStrategy>? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);;
    public override Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);;
    public override Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CloudWarehouse/DatabricksConnectionStrategy.cs
```csharp
public class DatabricksConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public DatabricksConnectionStrategy(ILogger<DatabricksConnectionStrategy>? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);;
    public override Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);;
    public override Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CloudWarehouse/SnowflakeConnectionStrategy.cs
```csharp
public class SnowflakeConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public SnowflakeConnectionStrategy(ILogger<SnowflakeConnectionStrategy>? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);;
    public override Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);;
    public override Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CloudWarehouse/BigQueryConnectionStrategy.cs
```csharp
public class BigQueryConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public BigQueryConnectionStrategy(ILogger<BigQueryConnectionStrategy>? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);;
    public override Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);;
    public override Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CloudWarehouse/GoogleAlloyDbConnectionStrategy.cs
```csharp
public class GoogleAlloyDbConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public GoogleAlloyDbConnectionStrategy(ILogger<GoogleAlloyDbConnectionStrategy>? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);;
    public override Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);;
    public override Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CloudWarehouse/FireboltConnectionStrategy.cs
```csharp
public class FireboltConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public FireboltConnectionStrategy(ILogger<FireboltConnectionStrategy>? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);;
    public override Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);;
    public override Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CloudWarehouse/AzureSynapseConnectionStrategy.cs
```csharp
public class AzureSynapseConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public AzureSynapseConnectionStrategy(ILogger<AzureSynapseConnectionStrategy>? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);;
    public override Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);;
    public override Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CloudWarehouse/StarburstConnectionStrategy.cs
```csharp
public class StarburstConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public StarburstConnectionStrategy(ILogger<StarburstConnectionStrategy>? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);;
    public override Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);;
    public override Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Protocol/RestGenericConnectionStrategy.cs
```csharp
public class RestGenericConnectionStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public RestGenericConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Protocol/JsonRpcConnectionStrategy.cs
```csharp
public class JsonRpcConnectionStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public JsonRpcConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Protocol/SyslogConnectionStrategy.cs
```csharp
public class SyslogConnectionStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public SyslogConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Protocol/SoapConnectionStrategy.cs
```csharp
public class SoapConnectionStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public SoapConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Protocol/SmtpConnectionStrategy.cs
```csharp
public class SmtpConnectionStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public SmtpConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Protocol/ODataConnectionStrategy.cs
```csharp
public class ODataConnectionStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public ODataConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Protocol/WebSocketConnectionStrategy.cs
```csharp
public class WebSocketConnectionStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public WebSocketConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Protocol/DnsConnectionStrategy.cs
```csharp
public class DnsConnectionStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public DnsConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Protocol/SnmpConnectionStrategy.cs
```csharp
public class SnmpConnectionStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public SnmpConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Protocol/SseConnectionStrategy.cs
```csharp
public class SseConnectionStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public SseConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Protocol/LdapConnectionStrategy.cs
```csharp
public class LdapConnectionStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public LdapConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Protocol/SftpConnectionStrategy.cs
```csharp
public class SftpConnectionStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public SftpConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Protocol/SshConnectionStrategy.cs
```csharp
public class SshConnectionStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public SshConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Protocol/GrpcConnectionStrategy.cs
```csharp
public class GrpcConnectionStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public GrpcConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Protocol/GraphQlConnectionStrategy.cs
```csharp
public class GraphQlConnectionStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public GraphQlConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    public void ValidateQuery(string queryString);
    internal static int CalculateQueryDepth(string query);
    internal static int EstimateQueryComplexity(string query);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Protocol/FtpConnectionStrategy.cs
```csharp
public class FtpConnectionStrategy : ConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public FtpConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Database/MySqlConnectionStrategy.cs
```csharp
public sealed class MySqlConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public MySqlConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
    public override Task<(bool IsValid, string[] Errors)> ValidateConfigAsync(ConnectionConfig config, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Database/TimescaleDbConnectionStrategy.cs
```csharp
public sealed class TimescaleDbConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public TimescaleDbConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
    public override Task<(bool IsValid, string[] Errors)> ValidateConfigAsync(ConnectionConfig config, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Database/VitessConnectionStrategy.cs
```csharp
public sealed class VitessConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public VitessConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
    public override Task<(bool IsValid, string[] Errors)> ValidateConfigAsync(ConnectionConfig config, CancellationToken ct = default);
}
```
```csharp
private sealed class VitessTcpConnection
{
}
    public string Host { get; }
    public int Port { get; }
    public string ConnectionString { get; }
    public VitessTcpConnection(string host, int port, string connectionString);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Database/CitusConnectionStrategy.cs
```csharp
public sealed class CitusConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public CitusConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
    public override Task<(bool IsValid, string[] Errors)> ValidateConfigAsync(ConnectionConfig config, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Database/MariaDbConnectionStrategy.cs
```csharp
public sealed class MariaDbConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public MariaDbConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
    public override Task<(bool IsValid, string[] Errors)> ValidateConfigAsync(ConnectionConfig config, CancellationToken ct = default);
}
```
```csharp
private sealed class MariaDbTcpConnection
{
}
    public string Host { get; }
    public int Port { get; }
    public string ConnectionString { get; }
    public MariaDbTcpConnection(string host, int port, string connectionString);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Database/CockroachDbConnectionStrategy.cs
```csharp
public sealed class CockroachDbConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public CockroachDbConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
    public override Task<(bool IsValid, string[] Errors)> ValidateConfigAsync(ConnectionConfig config, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Database/OracleConnectionStrategy.cs
```csharp
public sealed class OracleConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public OracleConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
    public override Task<(bool IsValid, string[] Errors)> ValidateConfigAsync(ConnectionConfig config, CancellationToken ct = default);
}
```
```csharp
private sealed class OracleTcpConnection
{
}
    public string Host { get; }
    public int Port { get; }
    public string ConnectionString { get; }
    public OracleTcpConnection(string host, int port, string connectionString);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Database/YugabyteDbConnectionStrategy.cs
```csharp
public sealed class YugabyteDbConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public YugabyteDbConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
    public override Task<(bool IsValid, string[] Errors)> ValidateConfigAsync(ConnectionConfig config, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Database/TiDbConnectionStrategy.cs
```csharp
public sealed class TiDbConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public TiDbConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
    public override Task<(bool IsValid, string[] Errors)> ValidateConfigAsync(ConnectionConfig config, CancellationToken ct = default);
}
```
```csharp
private sealed class TiDbTcpConnection
{
}
    public string Host { get; }
    public int Port { get; }
    public string ConnectionString { get; }
    public TiDbTcpConnection(string host, int port, string connectionString);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Database/SqliteConnectionStrategy.cs
```csharp
public sealed class SqliteConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public SqliteConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
    public override Task<(bool IsValid, string[] Errors)> ValidateConfigAsync(ConnectionConfig config, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Database/SqlServerConnectionStrategy.cs
```csharp
public sealed class SqlServerConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public SqlServerConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
    public override Task<(bool IsValid, string[] Errors)> ValidateConfigAsync(ConnectionConfig config, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Database/PostgreSqlConnectionStrategy.cs
```csharp
public sealed class PostgreSqlConnectionStrategy : DatabaseConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public PostgreSqlConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default);
    public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default);
    public override Task<(bool IsValid, string[] Errors)> ValidateConfigAsync(ConnectionConfig config, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/AI/WeightsAndBiasesConnectionStrategy.cs
```csharp
public sealed class WeightsAndBiasesConnectionStrategy : AiConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public WeightsAndBiasesConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> SendRequestAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<string> StreamResponseAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/AI/GroqConnectionStrategy.cs
```csharp
public sealed class GroqConnectionStrategy : AiConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public GroqConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> SendRequestAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<string> StreamResponseAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/AI/MilvusConnectionStrategy.cs
```csharp
public sealed class MilvusConnectionStrategy : AiConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public MilvusConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> SendRequestAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<string> StreamResponseAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/AI/PerplexityConnectionStrategy.cs
```csharp
public sealed class PerplexityConnectionStrategy : AiConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public PerplexityConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> SendRequestAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<string> StreamResponseAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/AI/AwsSageMakerConnectionStrategy.cs
```csharp
public sealed class AwsSageMakerConnectionStrategy : AiConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public AwsSageMakerConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> SendRequestAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<string> StreamResponseAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/AI/OpenAiConnectionStrategy.cs
```csharp
public sealed class OpenAiConnectionStrategy : AiConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public OpenAiConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> SendRequestAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<string> StreamResponseAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/AI/QdrantConnectionStrategy.cs
```csharp
public sealed class QdrantConnectionStrategy : AiConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public QdrantConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> SendRequestAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<string> StreamResponseAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/AI/OllamaConnectionStrategy.cs
```csharp
public sealed class OllamaConnectionStrategy : AiConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public OllamaConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> SendRequestAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<string> StreamResponseAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/AI/ChromaConnectionStrategy.cs
```csharp
public sealed class ChromaConnectionStrategy : AiConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public ChromaConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> SendRequestAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<string> StreamResponseAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/AI/HuggingFaceConnectionStrategy.cs
```csharp
public sealed class HuggingFaceConnectionStrategy : AiConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public HuggingFaceConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> SendRequestAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<string> StreamResponseAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/AI/PineconeConnectionStrategy.cs
```csharp
public sealed class PineconeConnectionStrategy : AiConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public PineconeConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> SendRequestAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<string> StreamResponseAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/AI/GoogleGeminiConnectionStrategy.cs
```csharp
public sealed class GoogleGeminiConnectionStrategy : AiConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public GoogleGeminiConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> SendRequestAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<string> StreamResponseAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/AI/PgVectorConnectionStrategy.cs
```csharp
public sealed class PgVectorConnectionStrategy : AiConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public PgVectorConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<string> SendRequestAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<string> StreamResponseAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/AI/KubeflowConnectionStrategy.cs
```csharp
public sealed class KubeflowConnectionStrategy : AiConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public KubeflowConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> SendRequestAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<string> StreamResponseAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/AI/AnthropicConnectionStrategy.cs
```csharp
public sealed class AnthropicConnectionStrategy : AiConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public AnthropicConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> SendRequestAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<string> StreamResponseAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/AI/AwsBedrockConnectionStrategy.cs
```csharp
public sealed class AwsBedrockConnectionStrategy : AiConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public AwsBedrockConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> SendRequestAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<string> StreamResponseAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/AI/LlamaCppConnectionStrategy.cs
```csharp
public sealed class LlamaCppConnectionStrategy : AiConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public LlamaCppConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> SendRequestAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<string> StreamResponseAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/AI/LangSmithConnectionStrategy.cs
```csharp
public sealed class LangSmithConnectionStrategy : AiConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public LangSmithConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> SendRequestAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<string> StreamResponseAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/AI/HeliconeConnectionStrategy.cs
```csharp
public sealed class HeliconeConnectionStrategy : AiConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public HeliconeConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> SendRequestAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<string> StreamResponseAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/AI/AzureOpenAiConnectionStrategy.cs
```csharp
public sealed class AzureOpenAiConnectionStrategy : AiConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public AzureOpenAiConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> SendRequestAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<string> StreamResponseAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/AI/WhisperConnectionStrategy.cs
```csharp
public sealed class WhisperConnectionStrategy : AiConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public WhisperConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> SendRequestAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<string> StreamResponseAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/AI/MlFlowConnectionStrategy.cs
```csharp
public sealed class MlFlowConnectionStrategy : AiConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public MlFlowConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> SendRequestAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<string> StreamResponseAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/AI/VertexAiConnectionStrategy.cs
```csharp
public sealed class VertexAiConnectionStrategy : AiConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public VertexAiConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> SendRequestAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<string> StreamResponseAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/AI/VllmConnectionStrategy.cs
```csharp
public sealed class VllmConnectionStrategy : AiConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public VllmConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> SendRequestAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<string> StreamResponseAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/AI/MistralConnectionStrategy.cs
```csharp
public sealed class MistralConnectionStrategy : AiConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public MistralConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> SendRequestAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<string> StreamResponseAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/AI/TgiConnectionStrategy.cs
```csharp
public sealed class TgiConnectionStrategy : AiConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public TgiConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> SendRequestAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<string> StreamResponseAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/AI/StabilityAiConnectionStrategy.cs
```csharp
public sealed class StabilityAiConnectionStrategy : AiConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public StabilityAiConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> SendRequestAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<string> StreamResponseAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/AI/ElevenLabsConnectionStrategy.cs
```csharp
public sealed class ElevenLabsConnectionStrategy : AiConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public ElevenLabsConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> SendRequestAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<string> StreamResponseAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/AI/TritonConnectionStrategy.cs
```csharp
public sealed class TritonConnectionStrategy : AiConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public TritonConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> SendRequestAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<string> StreamResponseAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/AI/WeaviateConnectionStrategy.cs
```csharp
public sealed class WeaviateConnectionStrategy : AiConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public WeaviateConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> SendRequestAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<string> StreamResponseAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/AI/AzureMlConnectionStrategy.cs
```csharp
public sealed class AzureMlConnectionStrategy : AiConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public AzureMlConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> SendRequestAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<string> StreamResponseAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/AI/CohereConnectionStrategy.cs
```csharp
public sealed class CohereConnectionStrategy : AiConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public CohereConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> SendRequestAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<string> StreamResponseAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/AI/LlamaIndexConnectionStrategy.cs
```csharp
public sealed class LlamaIndexConnectionStrategy : AiConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public LlamaIndexConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> SendRequestAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<string> StreamResponseAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/AI/TogetherAiConnectionStrategy.cs
```csharp
public sealed class TogetherAiConnectionStrategy : AiConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public TogetherAiConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> SendRequestAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<string> StreamResponseAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/AI/DeepgramConnectionStrategy.cs
```csharp
public sealed class DeepgramConnectionStrategy : AiConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public DeepgramConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> SendRequestAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, CancellationToken ct = default);
    public override async IAsyncEnumerable<string> StreamResponseAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Dashboard/TableauConnectionStrategy.cs
```csharp
public sealed class TableauConnectionStrategy : DashboardConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public TableauConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> ProvisionDashboardAsync(IConnectionHandle handle, string dashboardDefinition, CancellationToken ct = default);
    public override async Task PushDataAsync(IConnectionHandle handle, string datasetId, IReadOnlyList<Dictionary<string, object?>> data, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Dashboard/KlipfolioConnectionStrategy.cs
```csharp
public sealed class KlipfolioConnectionStrategy : DashboardConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public KlipfolioConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> ProvisionDashboardAsync(IConnectionHandle handle, string dashboardDefinition, CancellationToken ct = default);
    public override async Task PushDataAsync(IConnectionHandle handle, string datasetId, IReadOnlyList<Dictionary<string, object?>> data, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Dashboard/DataboxConnectionStrategy.cs
```csharp
public sealed class DataboxConnectionStrategy : DashboardConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public DataboxConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<string> ProvisionDashboardAsync(IConnectionHandle handle, string dashboardDefinition, CancellationToken ct = default);;
    public override async Task PushDataAsync(IConnectionHandle handle, string datasetId, IReadOnlyList<Dictionary<string, object?>> data, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Dashboard/PowerBiConnectionStrategy.cs
```csharp
public sealed class PowerBiConnectionStrategy : DashboardConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public PowerBiConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> ProvisionDashboardAsync(IConnectionHandle handle, string dashboardDefinition, CancellationToken ct = default);
    public override Task PushDataAsync(IConnectionHandle handle, string datasetId, IReadOnlyList<Dictionary<string, object?>> data, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Dashboard/MetricubeConnectionStrategy.cs
```csharp
public sealed class MetricubeConnectionStrategy : DashboardConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public MetricubeConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> ProvisionDashboardAsync(IConnectionHandle handle, string dashboardDefinition, CancellationToken ct = default);
    public override async Task PushDataAsync(IConnectionHandle handle, string datasetId, IReadOnlyList<Dictionary<string, object?>> data, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Dashboard/MetabaseConnectionStrategy.cs
```csharp
public sealed class MetabaseConnectionStrategy : DashboardConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public MetabaseConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> ProvisionDashboardAsync(IConnectionHandle handle, string dashboardDefinition, CancellationToken ct = default);
    public override async Task PushDataAsync(IConnectionHandle handle, string datasetId, IReadOnlyList<Dictionary<string, object?>> data, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Dashboard/AwsQuickSightConnectionStrategy.cs
```csharp
public sealed class AwsQuickSightConnectionStrategy : DashboardConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public AwsQuickSightConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    public override Task<string> ProvisionDashboardAsync(IConnectionHandle handle, string dashboardDefinition, CancellationToken ct = default);;
    public override Task PushDataAsync(IConnectionHandle handle, string datasetId, IReadOnlyList<Dictionary<string, object?>> data, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Dashboard/PersesConnectionStrategy.cs
```csharp
public sealed class PersesConnectionStrategy : DashboardConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public PersesConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> ProvisionDashboardAsync(IConnectionHandle handle, string dashboardDefinition, CancellationToken ct = default);
    public override Task PushDataAsync(IConnectionHandle handle, string datasetId, IReadOnlyList<Dictionary<string, object?>> data, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Dashboard/ChronografConnectionStrategy.cs
```csharp
public sealed class ChronografConnectionStrategy : DashboardConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public ChronografConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> ProvisionDashboardAsync(IConnectionHandle handle, string dashboardDefinition, CancellationToken ct = default);
    public override Task PushDataAsync(IConnectionHandle handle, string datasetId, IReadOnlyList<Dictionary<string, object?>> data, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Dashboard/GoogleLookerConnectionStrategy.cs
```csharp
public sealed class GoogleLookerConnectionStrategy : DashboardConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public GoogleLookerConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> ProvisionDashboardAsync(IConnectionHandle handle, string dashboardDefinition, CancellationToken ct = default);
    public override Task PushDataAsync(IConnectionHandle handle, string datasetId, IReadOnlyList<Dictionary<string, object?>> data, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Dashboard/RedashConnectionStrategy.cs
```csharp
public sealed class RedashConnectionStrategy : DashboardConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public RedashConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> ProvisionDashboardAsync(IConnectionHandle handle, string dashboardDefinition, CancellationToken ct = default);
    public override async Task PushDataAsync(IConnectionHandle handle, string datasetId, IReadOnlyList<Dictionary<string, object?>> data, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Dashboard/QlikConnectionStrategy.cs
```csharp
public sealed class QlikConnectionStrategy : DashboardConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public QlikConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> ProvisionDashboardAsync(IConnectionHandle handle, string dashboardDefinition, CancellationToken ct = default);
    public override async Task PushDataAsync(IConnectionHandle handle, string datasetId, IReadOnlyList<Dictionary<string, object?>> data, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Dashboard/CubeJsConnectionStrategy.cs
```csharp
public sealed class CubeJsConnectionStrategy : DashboardConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public CubeJsConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<string> ProvisionDashboardAsync(IConnectionHandle handle, string dashboardDefinition, CancellationToken ct = default);;
    public override async Task PushDataAsync(IConnectionHandle handle, string datasetId, IReadOnlyList<Dictionary<string, object?>> data, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Dashboard/GeckoboardConnectionStrategy.cs
```csharp
public sealed class GeckoboardConnectionStrategy : DashboardConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public GeckoboardConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> ProvisionDashboardAsync(IConnectionHandle handle, string dashboardDefinition, CancellationToken ct = default);
    public override async Task PushDataAsync(IConnectionHandle handle, string datasetId, IReadOnlyList<Dictionary<string, object?>> data, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Dashboard/GrafanaConnectionStrategy.cs
```csharp
public sealed class GrafanaConnectionStrategy : DashboardConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public GrafanaConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> ProvisionDashboardAsync(IConnectionHandle handle, string dashboardDefinition, CancellationToken ct = default);
    public override async Task PushDataAsync(IConnectionHandle handle, string datasetId, IReadOnlyList<Dictionary<string, object?>> data, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Dashboard/LightdashConnectionStrategy.cs
```csharp
public sealed class LightdashConnectionStrategy : DashboardConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public LightdashConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> ProvisionDashboardAsync(IConnectionHandle handle, string dashboardDefinition, CancellationToken ct = default);
    public override Task PushDataAsync(IConnectionHandle handle, string datasetId, IReadOnlyList<Dictionary<string, object?>> data, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Dashboard/ApacheSupersetConnectionStrategy.cs
```csharp
public sealed class ApacheSupersetConnectionStrategy : DashboardConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public ApacheSupersetConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<string> ProvisionDashboardAsync(IConnectionHandle handle, string dashboardDefinition, CancellationToken ct = default);
    public override async Task PushDataAsync(IConnectionHandle handle, string datasetId, IReadOnlyList<Dictionary<string, object?>> data, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Observability/ZabbixConnectionStrategy.cs
```csharp
public sealed class ZabbixConnectionStrategy : ObservabilityConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public ZabbixConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default);
    public override Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default);;
    public override Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Observability/JaegerConnectionStrategy.cs
```csharp
public sealed class JaegerConnectionStrategy : ObservabilityConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public JaegerConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default);;
    public override Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default);;
    public override async Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Observability/AwsCloudWatchConnectionStrategy.cs
```csharp
public sealed class AwsCloudWatchConnectionStrategy : ObservabilityConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public AwsCloudWatchConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    public override async Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default);
    public override async Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default);
    public override Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Observability/GcpCloudMonitoringConnectionStrategy.cs
```csharp
public sealed class GcpCloudMonitoringConnectionStrategy : ObservabilityConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public GcpCloudMonitoringConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    public override async Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default);
    public override async Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default);
    public override async Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Observability/NagiosConnectionStrategy.cs
```csharp
public sealed class NagiosConnectionStrategy : ObservabilityConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public NagiosConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default);
    public override Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default);;
    public override Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Observability/HoneycombConnectionStrategy.cs
```csharp
public sealed class HoneycombConnectionStrategy : ObservabilityConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public HoneycombConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default);;
    public override async Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default);
    public override async Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Observability/NewRelicConnectionStrategy.cs
```csharp
public sealed class NewRelicConnectionStrategy : ObservabilityConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public NewRelicConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    public override async Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default);
    public override async Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default);
    public override async Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Observability/GrafanaLokiConnectionStrategy.cs
```csharp
public sealed class GrafanaLokiConnectionStrategy : ObservabilityConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public GrafanaLokiConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default);
    public override async Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default);
    public override Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Observability/TempoConnectionStrategy.cs
```csharp
public sealed class TempoConnectionStrategy : ObservabilityConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public TempoConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default);;
    public override Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default);;
    public override async Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Observability/LogzioConnectionStrategy.cs
```csharp
public sealed class LogzioConnectionStrategy : ObservabilityConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public LogzioConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    public override async Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default);
    public override async Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default);
    public override async Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Observability/AppDynamicsConnectionStrategy.cs
```csharp
public sealed class AppDynamicsConnectionStrategy : ObservabilityConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public AppDynamicsConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default);
    public override Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default);;
    public override Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Observability/FluentdConnectionStrategy.cs
```csharp
public sealed class FluentdConnectionStrategy : ObservabilityConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public FluentdConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default);;
    public override async Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default);
    public override Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Observability/ZipkinConnectionStrategy.cs
```csharp
public sealed class ZipkinConnectionStrategy : ObservabilityConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public ZipkinConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default);;
    public override Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default);;
    public override async Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Observability/OtlpCollectorConnectionStrategy.cs
```csharp
public sealed class OtlpCollectorConnectionStrategy : ObservabilityConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public OtlpCollectorConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default);
    public override async Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default);
    public override async Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Observability/LogicMonitorConnectionStrategy.cs
```csharp
public sealed class LogicMonitorConnectionStrategy : ObservabilityConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public LogicMonitorConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default);
    public override Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default);;
    public override Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Observability/SigNozConnectionStrategy.cs
```csharp
public sealed class SigNozConnectionStrategy : ObservabilityConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public SigNozConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default);
    public override async Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default);
    public override async Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Observability/CortexConnectionStrategy.cs
```csharp
public sealed class CortexConnectionStrategy : ObservabilityConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public CortexConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default);
    public override Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default);
    public override Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Observability/DynatraceConnectionStrategy.cs
```csharp
public sealed class DynatraceConnectionStrategy : ObservabilityConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public DynatraceConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default);
    public override Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default);;
    public override Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Observability/VictoriaMetricsConnectionStrategy.cs
```csharp
public sealed class VictoriaMetricsConnectionStrategy : ObservabilityConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public VictoriaMetricsConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default);
    public override Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default);
    public override Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Observability/SplunkHecConnectionStrategy.cs
```csharp
public sealed class SplunkHecConnectionStrategy : ObservabilityConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public SplunkHecConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default);
    public override async Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default);
    public override Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Observability/AzureMonitorConnectionStrategy.cs
```csharp
public sealed class AzureMonitorConnectionStrategy : ObservabilityConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public AzureMonitorConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    public override async Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default);
    public override async Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default);
    public override Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Observability/NetdataConnectionStrategy.cs
```csharp
public sealed class NetdataConnectionStrategy : ObservabilityConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public NetdataConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default);;
    public override Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default);;
    public override Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Observability/MimirConnectionStrategy.cs
```csharp
public sealed class MimirConnectionStrategy : ObservabilityConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public MimirConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default);
    public override Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default);
    public override Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Observability/ElasticsearchLoggingConnectionStrategy.cs
```csharp
public sealed class ElasticsearchLoggingConnectionStrategy : ObservabilityConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public ElasticsearchLoggingConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default);;
    public override async Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default);
    public override Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Observability/DatadogConnectionStrategy.cs
```csharp
public sealed class DatadogConnectionStrategy : ObservabilityConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public DatadogConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default);
    public override async Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default);
    public override async Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Observability/InstanaConnectionStrategy.cs
```csharp
public sealed class InstanaConnectionStrategy : ObservabilityConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public InstanaConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default);
    public override Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default);;
    public override Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Observability/PrometheusConnectionStrategy.cs
```csharp
public sealed class PrometheusConnectionStrategy : ObservabilityConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public PrometheusConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default);
    public override Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default);
    public override Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Observability/OpenSearchLoggingConnectionStrategy.cs
```csharp
public sealed class OpenSearchLoggingConnectionStrategy : ObservabilityConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public OpenSearchLoggingConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default);;
    public override async Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default);
    public override Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Observability/ThanosConnectionStrategy.cs
```csharp
public sealed class ThanosConnectionStrategy : ObservabilityConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public ThanosConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default);
    public override Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default);
    public override Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CloudPlatform/MinioConnectionStrategy.cs
```csharp
public class MinioConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public MinioConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);;
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CloudPlatform/GcpDataflowConnectionStrategy.cs
```csharp
public class GcpDataflowConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public GcpDataflowConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);;
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CloudPlatform/AwsGlueConnectionStrategy.cs
```csharp
public class AwsGlueConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public AwsGlueConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CloudPlatform/AwsSnsConnectionStrategy.cs
```csharp
public sealed class AwsSnsConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public AwsSnsConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);;
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CloudPlatform/AwsKinesisConnectionStrategy.cs
```csharp
public class AwsKinesisConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public AwsKinesisConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CloudPlatform/AzureCosmosConnectionStrategy.cs
```csharp
public class AzureCosmosConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public AzureCosmosConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);;
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CloudPlatform/CloudflareR2ConnectionStrategy.cs
```csharp
public class CloudflareR2ConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public CloudflareR2ConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);;
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CloudPlatform/AwsS3ConnectionStrategy.cs
```csharp
public class AwsS3ConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public AwsS3ConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CloudPlatform/GcpBigtableConnectionStrategy.cs
```csharp
public class GcpBigtableConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public GcpBigtableConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);;
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CloudPlatform/AzureEventHubConnectionStrategy.cs
```csharp
public sealed class AzureEventHubConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public AzureEventHubConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);;
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CloudPlatform/AzureBlobConnectionStrategy.cs
```csharp
public class AzureBlobConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public AzureBlobConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CloudPlatform/GcpPubSubConnectionStrategy.cs
```csharp
public sealed class GcpPubSubConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public GcpPubSubConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);;
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
    internal sealed class GcpPubSubWrapper(PublisherServiceApiClient publisherClient, SubscriberServiceApiClient subscriberClient, string projectId);
}
```
```csharp
internal sealed class GcpPubSubWrapper(PublisherServiceApiClient publisherClient, SubscriberServiceApiClient subscriberClient, string projectId)
{
}
    public PublisherServiceApiClient PublisherClient { get; };
    public SubscriberServiceApiClient SubscriberClient { get; };
    public string ProjectId { get; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CloudPlatform/AwsLambdaConnectionStrategy.cs
```csharp
public class AwsLambdaConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public AwsLambdaConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CloudPlatform/GcpFirestoreConnectionStrategy.cs
```csharp
public class GcpFirestoreConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public GcpFirestoreConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);;
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CloudPlatform/WasabiConnectionStrategy.cs
```csharp
public class WasabiConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public WasabiConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);;
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CloudPlatform/AzureDataLakeConnectionStrategy.cs
```csharp
public class AzureDataLakeConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public AzureDataLakeConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);;
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CloudPlatform/BackblazeB2ConnectionStrategy.cs
```csharp
public class BackblazeB2ConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public BackblazeB2ConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);;
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CloudPlatform/GcpStorageConnectionStrategy.cs
```csharp
public class GcpStorageConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public GcpStorageConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);;
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CloudPlatform/AzureServiceBusConnectionStrategy.cs
```csharp
public sealed class AzureServiceBusConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public AzureServiceBusConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);;
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CloudPlatform/OracleCloudConnectionStrategy.cs
```csharp
public class OracleCloudConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public OracleCloudConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);;
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CloudPlatform/AzureFunctionsConnectionStrategy.cs
```csharp
public class AzureFunctionsConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public AzureFunctionsConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);;
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CloudPlatform/DigitalOceanSpacesConnectionStrategy.cs
```csharp
public class DigitalOceanSpacesConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public DigitalOceanSpacesConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);;
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CloudPlatform/AwsSqsConnectionStrategy.cs
```csharp
public sealed class AwsSqsConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public AwsSqsConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/CloudPlatform/GcpSpannerConnectionStrategy.cs
```csharp
public class GcpSpannerConnectionStrategy : SaaSConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectorCategory Category;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public GcpSpannerConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default);;
    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/IoT/GoogleIoTConnectionStrategy.cs
```csharp
public class GoogleIoTConnectionStrategy : IoTConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public GoogleIoTConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<Dictionary<string, object>> ReadTelemetryAsync(IConnectionHandle handle, string deviceId, CancellationToken ct = default);
    public override async Task<string> SendCommandAsync(IConnectionHandle handle, string deviceId, string command, Dictionary<string, object>? parameters = null, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/IoT/ModbusConnectionStrategy.cs
```csharp
public class ModbusConnectionStrategy : IoTConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public ModbusConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<Dictionary<string, object>> ReadTelemetryAsync(IConnectionHandle handle, string deviceId, CancellationToken ct = default);
    public override Task<string> SendCommandAsync(IConnectionHandle handle, string deviceId, string command, Dictionary<string, object>? parameters = null, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/IoT/AwsIoTCoreConnectionStrategy.cs
```csharp
public class AwsIoTCoreConnectionStrategy : IoTConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public AwsIoTCoreConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<Dictionary<string, object>> ReadTelemetryAsync(IConnectionHandle handle, string deviceId, CancellationToken ct = default);
    public override async Task<string> SendCommandAsync(IConnectionHandle handle, string deviceId, string command, Dictionary<string, object>? parameters = null, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/IoT/MqttIoTConnectionStrategy.cs
```csharp
public class MqttIoTConnectionStrategy : IoTConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public MqttIoTConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<Dictionary<string, object>> ReadTelemetryAsync(IConnectionHandle handle, string deviceId, CancellationToken ct = default);
    public override Task<string> SendCommandAsync(IConnectionHandle handle, string deviceId, string command, Dictionary<string, object>? parameters = null, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/IoT/AmqpIoTConnectionStrategy.cs
```csharp
public class AmqpIoTConnectionStrategy : IoTConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public AmqpIoTConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<Dictionary<string, object>> ReadTelemetryAsync(IConnectionHandle handle, string deviceId, CancellationToken ct = default);
    public override Task<string> SendCommandAsync(IConnectionHandle handle, string deviceId, string command, Dictionary<string, object>? parameters = null, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/IoT/BacNetConnectionStrategy.cs
```csharp
public class BacNetConnectionStrategy : IoTConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public BacNetConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<Dictionary<string, object>> ReadTelemetryAsync(IConnectionHandle handle, string deviceId, CancellationToken ct = default);
    public override Task<string> SendCommandAsync(IConnectionHandle handle, string deviceId, string command, Dictionary<string, object>? parameters = null, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/IoT/LoRaWanConnectionStrategy.cs
```csharp
public class LoRaWanConnectionStrategy : IoTConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public LoRaWanConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    public override Task<Dictionary<string, object>> ReadTelemetryAsync(IConnectionHandle handle, string deviceId, CancellationToken ct = default);
    public override Task<string> SendCommandAsync(IConnectionHandle handle, string deviceId, string command, Dictionary<string, object>? parameters = null, CancellationToken ct = default);
    public LoRaJoinResult ProcessOtaaJoin(string devEui, byte[] joinRequest);
    public LoRaJoinResult RegisterAbpDevice(string devEui, string devAddr, byte[] nwkSKey, byte[] appSKey);
    public LoRaUplinkResult ProcessUplink(string devEui, byte[] payload, int fPort, double rssi, double snr, int dataRate);
    public void ScheduleDownlink(string devEui, byte[] payload, int fPort = 1, bool confirmed = false);
    public AdrRecommendation? ProcessAdr(string devEui, double rssi, double snr, int currentDataRate);
    public int DeviceCount;;
}
```
```csharp
public sealed class LoRaDevice
{
}
    public string DevEui { get; set; };
    public string DevAddr { get; set; };
    public string ActivationType { get; set; };
    public byte[] NwkSKey { get; set; };
    public byte[] AppSKey { get; set; };
    public ushort DevNonce { get; set; }
    public uint FrameCounterUp { get; set; }
    public uint FrameCounterDown { get; set; }
    public int DataRate { get; set; }
    public double LastRssi { get; set; }
    public double LastSnr { get; set; }
    public DateTimeOffset JoinedAt { get; set; }
    public DateTimeOffset LastSeen { get; set; }
}
```
```csharp
public sealed record LoRaJoinResult
{
}
    public bool Success { get; init; }
    public string? DevAddr { get; init; }
    public string? DevEui { get; init; }
    public string? ActivationType { get; init; }
    public string? Reason { get; init; }
}
```
```csharp
public sealed record LoRaUplinkResult
{
}
    public bool Success { get; init; }
    public string? DevEui { get; init; }
    public uint FrameCounter { get; init; }
    public int FPort { get; init; }
    public int PayloadSize { get; init; }
    public double Rssi { get; init; }
    public double Snr { get; init; }
    public int DataRate { get; init; }
    public AdrRecommendation? AdrRecommendation { get; init; }
    public bool HasPendingDownlink { get; init; }
    public string? Reason { get; init; }
}
```
```csharp
public sealed record LoRaDownlink
{
}
    public required string DevEui { get; init; }
    public required byte[] Payload { get; init; }
    public int FPort { get; init; }
    public bool Confirmed { get; init; }
    public DateTimeOffset QueuedAt { get; init; }
}
```
```csharp
public sealed class AdrState
{
}
    public List<double> SnrHistory { get; };
}
```
```csharp
public sealed record AdrRecommendation
{
}
    public required string DevEui { get; init; }
    public int CurrentDataRate { get; init; }
    public int RecommendedDataRate { get; init; }
    public double AverageSnr { get; init; }
    public double Margin { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/IoT/ZigbeeConnectionStrategy.cs
```csharp
public class ZigbeeConnectionStrategy : IoTConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public ZigbeeConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    public override Task<Dictionary<string, object>> ReadTelemetryAsync(IConnectionHandle handle, string deviceId, CancellationToken ct = default);
    public override Task<string> SendCommandAsync(IConnectionHandle handle, string deviceId, string command, Dictionary<string, object>? parameters = null, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/IoT/OpcUaConnectionStrategy.cs
```csharp
public class OpcUaConnectionStrategy : IoTConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public OpcUaConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<Dictionary<string, object>> ReadTelemetryAsync(IConnectionHandle handle, string deviceId, CancellationToken ct = default);
    public override Task<string> SendCommandAsync(IConnectionHandle handle, string deviceId, string command, Dictionary<string, object>? parameters = null, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/IoT/AzureIoTHubConnectionStrategy.cs
```csharp
public class AzureIoTHubConnectionStrategy : IoTConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public AzureIoTHubConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override async Task<Dictionary<string, object>> ReadTelemetryAsync(IConnectionHandle handle, string deviceId, CancellationToken ct = default);
    public override async Task<string> SendCommandAsync(IConnectionHandle handle, string deviceId, string command, Dictionary<string, object>? parameters = null, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/IoT/CoApConnectionStrategy.cs
```csharp
public class CoApConnectionStrategy : IoTConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public CoApConnectionStrategy(ILogger? logger = null) : base(logger);
    public ushort GetNextMessageId();
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<Dictionary<string, object>> ReadTelemetryAsync(IConnectionHandle handle, string deviceId, CancellationToken ct = default);
    public override Task<string> SendCommandAsync(IConnectionHandle handle, string deviceId, string command, Dictionary<string, object>? parameters = null, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/IoT/KnxConnectionStrategy.cs
```csharp
public class KnxConnectionStrategy : IoTConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public KnxConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    public override Task<Dictionary<string, object>> ReadTelemetryAsync(IConnectionHandle handle, string deviceId, CancellationToken ct = default);
    public override Task<string> SendCommandAsync(IConnectionHandle handle, string deviceId, string command, Dictionary<string, object>? parameters = null, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Healthcare/FhirR4ConnectionStrategy.cs
```csharp
public class FhirR4ConnectionStrategy : HealthcareConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public FhirR4ConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<(bool IsValid, string[] Errors)> ValidateHl7Async(IConnectionHandle handle, string hl7Message, CancellationToken ct = default);
    public override async Task<string> QueryFhirAsync(IConnectionHandle handle, string resourceType, string? query = null, CancellationToken ct = default);
    public async Task<FhirResourceWrapper> DeserializeResourceAsync(string json, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Healthcare/X12ConnectionStrategy.cs
```csharp
public class X12ConnectionStrategy : HealthcareConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public X12ConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<(bool IsValid, string[] Errors)> ValidateHl7Async(IConnectionHandle handle, string hl7Message, CancellationToken ct = default);
    public override async Task<string> QueryFhirAsync(IConnectionHandle handle, string resourceType, string? query = null, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Healthcare/CdaConnectionStrategy.cs
```csharp
public class CdaConnectionStrategy : HealthcareConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public CdaConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);
    public override Task<(bool IsValid, string[] Errors)> ValidateHl7Async(IConnectionHandle handle, string hl7Message, CancellationToken ct = default);
    public override async Task<string> QueryFhirAsync(IConnectionHandle handle, string resourceType, string? query = null, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Healthcare/DicomConnectionStrategy.cs
```csharp
public class DicomConnectionStrategy : HealthcareConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public DicomConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    public override Task<(bool IsValid, string[] Errors)> ValidateHl7Async(IConnectionHandle handle, string hl7Message, CancellationToken ct = default);
    public override Task<string> QueryFhirAsync(IConnectionHandle handle, string resourceType, string? query = null, CancellationToken ct = default);
    public async Task<DicomStudy> ParseDicomFileAsync(byte[] dicomData, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Healthcare/Hl7v2ConnectionStrategy.cs
```csharp
public class Hl7v2ConnectionStrategy : HealthcareConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Hl7v2ConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    public override Task<(bool IsValid, string[] Errors)> ValidateHl7Async(IConnectionHandle handle, string hl7Message, CancellationToken ct = default);
    public override Task<string> QueryFhirAsync(IConnectionHandle handle, string resourceType, string? query = null, CancellationToken ct = default);
    public async Task<Hl7ParsedMessage> ParseHl7MessageAsync(string hl7Message, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Healthcare/NcpdpConnectionStrategy.cs
```csharp
public class NcpdpConnectionStrategy : HealthcareConnectionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConnectionStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public NcpdpConnectionStrategy(ILogger? logger = null) : base(logger);
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);;
    public override Task<(bool IsValid, string[] Errors)> ValidateHl7Async(IConnectionHandle handle, string hl7Message, CancellationToken ct = default);
    public override Task<string> QueryFhirAsync(IConnectionHandle handle, string resourceType, string? query = null, CancellationToken ct = default);
}
```
