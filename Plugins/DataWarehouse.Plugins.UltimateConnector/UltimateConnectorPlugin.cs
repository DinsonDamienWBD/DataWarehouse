using System.Collections.Concurrent;
using System.Reflection;
using DataWarehouse.SDK.Connectors;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateConnector;

/// <summary>
/// Ultimate Connector Plugin - Universal data integration through 283 connection strategies.
///
/// Provides connection strategies across categories:
/// - Database (PostgreSQL, MySQL, SQL Server, Oracle, SQLite, MariaDB)
/// - NoSQL (MongoDB, Cassandra, Redis, DynamoDB, CouchDB, Neo4j)
/// - Specialized databases (TimescaleDB, InfluxDB, QuestDB, ClickHouse)
/// - Cloud warehouses (Snowflake, BigQuery, Redshift, Databricks, Synapse)
/// - Cloud platforms (AWS, Azure, GCP services)
/// - Messaging (Kafka, RabbitMQ, Pulsar, NATS, ActiveMQ, SQS, EventHub)
/// - SaaS (Salesforce, HubSpot, Zendesk, Jira, ServiceNow, Workday)
/// - Protocol (gRPC, GraphQL, WebSocket, REST, OData, SOAP)
/// - IoT (MQTT, OPC-UA, Modbus, BACnet, CoAP, LwM2M)
/// - Legacy (Mainframe 3270, AS/400, COBOL, FTP, SFTP)
/// - Healthcare (HL7 v2, FHIR R4, DICOM, X12 EDI)
/// - Blockchain (Ethereum, Solana, IPFS, Hyperledger, Polygon)
/// - File system (NFS, SMB/CIFS, FUSE, S3-compatible, MinIO)
/// - Observability (Prometheus, Datadog, Grafana, Splunk, ELK)
/// - Dashboard (Tableau, PowerBI, Metabase, Redash, Superset)
/// - AI (OpenAI, Anthropic, Ollama, AWS Bedrock, Azure OpenAI, Cohere)
/// - Cross-cutting (connection pooling, health monitoring, circuit breaker)
///
/// Features:
/// - Strategy pattern for protocol extensibility
/// - Auto-discovery of strategies via assembly scanning
/// - Retry with exponential backoff
/// - Connection health monitoring
/// - Connection metrics and statistics
/// - Semantic descriptions for AI discovery
/// </summary>
public sealed class UltimateConnectorPlugin : FeaturePluginBase
{
    private readonly ConnectionStrategyRegistry _registry;
    private readonly ConcurrentDictionary<string, long> _usageStats = new();
    private bool _initialized;

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.connector.ultimate";

    /// <inheritdoc/>
    public override string Name => "Ultimate Connector";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    /// <summary>
    /// Semantic description of this plugin for AI discovery.
    /// </summary>
    public string SemanticDescription =>
        "Ultimate connector plugin providing 283 connection strategies for universal data integration. " +
        "Supports databases (SQL/NoSQL), messaging systems (Kafka, RabbitMQ), SaaS platforms (Salesforce, HubSpot), " +
        "IoT protocols (MQTT, OPC-UA), healthcare standards (HL7, FHIR), blockchain networks (Ethereum, Solana), " +
        "AI platforms (OpenAI, Anthropic, Bedrock), and observability systems (Prometheus, Datadog).";

    /// <summary>
    /// Semantic tags for AI discovery and categorization.
    /// </summary>
    public string[] SemanticTags =>
    [
        "connector", "integration", "database", "messaging", "saas", "iot",
        "healthcare", "blockchain", "ai", "observability", "protocol", "legacy"
    ];

    /// <summary>
    /// Gets the connection strategy registry.
    /// </summary>
    public ConnectionStrategyRegistry Registry => _registry;

    /// <summary>
    /// Initializes a new instance of the Ultimate Connector plugin.
    /// </summary>
    public UltimateConnectorPlugin()
    {
        _registry = new ConnectionStrategyRegistry();
    }

    /// <inheritdoc/>
    public override async Task StartAsync(CancellationToken ct)
    {
        if (_initialized) return;

        // Auto-discover all strategies in this assembly
        var discovered = _registry.AutoDiscover(Assembly.GetExecutingAssembly());

        // Publish registration events for each discovered strategy
        var strategies = _registry.GetAll();
        foreach (var strategy in strategies)
        {
            await PublishStrategyRegisteredAsync(strategy);
        }

        _initialized = true;
    }

    /// <inheritdoc/>
    public override Task StopAsync()
    {
        _usageStats.Clear();
        _initialized = false;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        var response = base.OnHandshakeAsync(request).GetAwaiter().GetResult();

        response.Metadata["RegisteredStrategies"] = _registry.Count.ToString();
        response.Metadata["SemanticDescription"] = SemanticDescription;

        return Task.FromResult(response);
    }

    /// <inheritdoc/>
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return
        [
            new() { Name = "connector.connect", DisplayName = "Connect", Description = "Establish a connection using a strategy" },
            new() { Name = "connector.disconnect", DisplayName = "Disconnect", Description = "Disconnect an active connection" },
            new() { Name = "connector.test", DisplayName = "Test Connection", Description = "Test an active connection" },
            new() { Name = "connector.health", DisplayName = "Health Check", Description = "Get health status of a connection" },
            new() { Name = "connector.list-strategies", DisplayName = "List Strategies", Description = "List available connection strategies" },
            new() { Name = "connector.list-categories", DisplayName = "List Categories", Description = "List strategies by category" },
            new() { Name = "connector.stats", DisplayName = "Statistics", Description = "Get connector usage statistics" }
        ];
    }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["TotalStrategies"] = _registry.Count;
        metadata["SemanticDescription"] = SemanticDescription;
        metadata["SemanticTags"] = SemanticTags;
        return metadata;
    }

    /// <inheritdoc/>
    public override Task OnMessageAsync(PluginMessage message)
    {
        return message.Type switch
        {
            "connector.list-strategies" => HandleListStrategiesAsync(message),
            "connector.list-categories" => HandleListCategoriesAsync(message),
            "connector.stats" => HandleStatsAsync(message),
            _ => base.OnMessageAsync(message)
        };
    }

    /// <summary>
    /// Gets a connection strategy by its identifier.
    /// </summary>
    /// <param name="strategyId">The strategy identifier (case-insensitive).</param>
    /// <returns>The strategy, or null if not found.</returns>
    public IConnectionStrategy? GetStrategy(string strategyId)
    {
        var strategy = _registry.Get(strategyId);
        if (strategy != null)
        {
            _usageStats.AddOrUpdate(strategyId, 1, (_, count) => count + 1);
        }
        return strategy;
    }

    /// <summary>
    /// Gets all strategies belonging to a specific connector category.
    /// </summary>
    /// <param name="category">The category to filter by.</param>
    /// <returns>Read-only collection of matching strategies.</returns>
    public IReadOnlyCollection<IConnectionStrategy> GetStrategiesByCategory(ConnectorCategory category)
    {
        return _registry.GetByCategory(category);
    }

    /// <summary>
    /// Lists all registered strategies with their metadata.
    /// </summary>
    /// <returns>Read-only collection of all registered strategies.</returns>
    public IReadOnlyCollection<IConnectionStrategy> ListStrategies()
    {
        return _registry.GetAll();
    }

    #region Message Handlers

    private Task HandleListStrategiesAsync(PluginMessage message)
    {
        var strategies = _registry.GetAll();

        var strategyList = strategies.Select(s => new Dictionary<string, object>
        {
            ["id"] = s.StrategyId,
            ["displayName"] = s.DisplayName,
            ["category"] = s.Category.ToString(),
            ["semanticDescription"] = s.SemanticDescription,
            ["tags"] = s.Tags,
            ["supportsPooling"] = s.Capabilities.SupportsPooling,
            ["supportsStreaming"] = s.Capabilities.SupportsStreaming,
            ["supportsTransactions"] = s.Capabilities.SupportsTransactions
        }).ToList();

        message.Payload["strategies"] = strategyList;
        message.Payload["count"] = strategyList.Count;

        return Task.CompletedTask;
    }

    private Task HandleListCategoriesAsync(PluginMessage message)
    {
        if (message.Payload.TryGetValue("category", out var catObj) && catObj is string categoryStr
            && Enum.TryParse<ConnectorCategory>(categoryStr, true, out var category))
        {
            var strategies = _registry.GetByCategory(category);
            message.Payload["strategies"] = strategies.Select(s => new Dictionary<string, object>
            {
                ["id"] = s.StrategyId,
                ["displayName"] = s.DisplayName,
                ["semanticDescription"] = s.SemanticDescription
            }).ToList();
            message.Payload["count"] = strategies.Count;
            message.Payload["category"] = category.ToString();
        }
        else
        {
            // Return all categories with counts
            var categoryCounts = _registry.GetAll()
                .GroupBy(s => s.Category)
                .ToDictionary(g => g.Key.ToString(), g => g.Count());
            message.Payload["categories"] = categoryCounts;
        }

        return Task.CompletedTask;
    }

    private Task HandleStatsAsync(PluginMessage message)
    {
        message.Payload["registeredStrategies"] = _registry.Count;
        message.Payload["usageByStrategy"] = new Dictionary<string, long>(_usageStats);
        message.Payload["initialized"] = _initialized;

        return Task.CompletedTask;
    }

    #endregion

    #region Internal Helpers

    private async Task PublishStrategyRegisteredAsync(IConnectionStrategy strategy)
    {
        if (MessageBus == null) return;

        try
        {
            var eventMessage = new PluginMessage
            {
                Type = "connector.strategy.registered",
                Source = Id,
                Payload = new Dictionary<string, object>
                {
                    ["strategyId"] = strategy.StrategyId,
                    ["displayName"] = strategy.DisplayName,
                    ["category"] = strategy.Category.ToString(),
                    ["semanticDescription"] = strategy.SemanticDescription
                }
            };

            await MessageBus.PublishAsync("connector.strategy.registered", eventMessage);
        }
        catch
        {
            // Gracefully handle message bus unavailability
        }
    }

    #endregion
}
