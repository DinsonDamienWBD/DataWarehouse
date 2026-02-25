using System.Reflection;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Connectors;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
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
/// - Intelligence-aware connection optimization
/// </summary>
public sealed class UltimateConnectorPlugin : DataWarehouse.SDK.Contracts.Hierarchy.InterfacePluginBase
{
    private readonly ConnectionStrategyRegistry _registry;
    private readonly BoundedDictionary<string, long> _usageStats = new BoundedDictionary<string, long>(1000);
    private bool _initialized;

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.connector.ultimate";

    /// <inheritdoc/>
    public override string Name => "Ultimate Connector";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    /// <inheritdoc/>
    public override string Protocol => "universal-multi-protocol";

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
    /// Declares all capabilities provided by this plugin.
    /// Includes main plugin capability and auto-generated strategy capabilities.
    /// </summary>
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
                var tags = new List<string> { "connector", "integration", strategy.Category.ToString().ToLowerInvariant() };
                tags.AddRange(strategy.Tags);

                // Add feature-specific tags
                if (strategy.Capabilities.SupportsPooling)
                    tags.Add("pooling");
                if (strategy.Capabilities.SupportsStreaming)
                    tags.Add("streaming");
                if (strategy.Capabilities.SupportsTransactions)
                    tags.Add("transactions");

                capabilities.Add(new()
                {
                    CapabilityId = $"{Id}.{strategy.StrategyId.ToLowerInvariant().Replace(".", "-").Replace(" ", "-")}",
                    DisplayName = strategy.DisplayName,
                    Description = strategy.SemanticDescription,
                    Category = SDK.Contracts.CapabilityCategory.Connector,
                    SubCategory = strategy.Category.ToString(),
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = [..tags]
                });
            }

            return capabilities.AsReadOnly();
        }
    }

    /// <summary>
    /// Initializes a new instance of the Ultimate Connector plugin.
    /// </summary>
    public UltimateConnectorPlugin()
    {
        _registry = new ConnectionStrategyRegistry();
    }

    /// <inheritdoc/>
    protected override async Task OnStartCoreAsync(CancellationToken ct)
    {
        if (_initialized) return;

        // Auto-discover all strategies in this assembly
        var discovered = _registry.AutoDiscover(Assembly.GetExecutingAssembly());

        // Dual-register: populate base-class IStrategy registry alongside local ConnectionStrategyRegistry.
        // ConnectionStrategyBase : StrategyBase : IStrategy, so all discovered strategies implement IStrategy.
        var strategies = _registry.GetAll();
        foreach (var strategy in strategies)
        {
            if (strategy is IStrategy iStrategy)
            {
                RegisterStrategy(iStrategy);
            }

            await PublishStrategyRegisteredAsync(strategy);
        }

        _initialized = true;
    }

    /// <inheritdoc/>
    protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct)
    {
        // Register connector capabilities with Intelligence for enhanced connection optimization
        await RegisterConnectorCapabilitiesAsync(ct);
    }

    /// <inheritdoc/>
    protected override Task OnStartWithoutIntelligenceAsync(CancellationToken ct)
    {
        // Connector works without Intelligence, but with reduced optimization capabilities
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task OnStopCoreAsync()
    {
        _usageStats.Clear();
        _initialized = false;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        var response = await base.OnHandshakeAsync(request);

        response.Metadata["RegisteredStrategies"] = _registry.Count.ToString();
        response.Metadata["SemanticDescription"] = SemanticDescription;

        return response;
    }

    /// <inheritdoc/>
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
    {
        var strategies = _registry.GetAll();
        var categoryCounts = strategies
            .GroupBy(s => s.Category)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());

        var poolingCount = strategies.Count(s => s.Capabilities.SupportsPooling);
        var streamingCount = strategies.Count(s => s.Capabilities.SupportsStreaming);
        var transactionCount = strategies.Count(s => s.Capabilities.SupportsTransactions);

        return new List<KnowledgeObject>
        {
            new()
            {
                Id = $"{Id}:overview",
                Topic = "connector",
                SourcePluginId = Id,
                SourcePluginName = Name,
                KnowledgeType = "capability",
                Description = "Ultimate Connector Overview",
                Payload = new Dictionary<string, object>
                {
                    ["description"] = SemanticDescription,
                    ["totalStrategies"] = strategies.Count,
                    ["categories"] = categoryCounts,
                    ["features"] = new Dictionary<string, object>
                    {
                        ["poolingSupport"] = poolingCount,
                        ["streamingSupport"] = streamingCount,
                        ["transactionSupport"] = transactionCount
                    },
                    ["categoryBreakdown"] = new Dictionary<string, object>
                    {
                        ["database"] = categoryCounts.GetValueOrDefault("Database", 0),
                        ["nosql"] = categoryCounts.GetValueOrDefault("NoSql", 0),
                        ["messaging"] = categoryCounts.GetValueOrDefault("Messaging", 0),
                        ["saas"] = categoryCounts.GetValueOrDefault("SaaS", 0),
                        ["iot"] = categoryCounts.GetValueOrDefault("IoT", 0),
                        ["healthcare"] = categoryCounts.GetValueOrDefault("Healthcare", 0),
                        ["blockchain"] = categoryCounts.GetValueOrDefault("Blockchain", 0),
                        ["ai"] = categoryCounts.GetValueOrDefault("AI", 0),
                        ["cloudWarehouse"] = categoryCounts.GetValueOrDefault("CloudWarehouse", 0),
                        ["cloudPlatform"] = categoryCounts.GetValueOrDefault("CloudPlatform", 0),
                        ["protocol"] = categoryCounts.GetValueOrDefault("Protocol", 0),
                        ["legacy"] = categoryCounts.GetValueOrDefault("Legacy", 0),
                        ["fileSystem"] = categoryCounts.GetValueOrDefault("FileSystem", 0),
                        ["observability"] = categoryCounts.GetValueOrDefault("Observability", 0),
                        ["dashboard"] = categoryCounts.GetValueOrDefault("Dashboard", 0)
                    }
                },
                Tags = SemanticTags
            }
        }.AsReadOnly();
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

    /// <summary>
    /// Registers connector capabilities with Intelligence for enhanced optimization.
    /// </summary>
    private async Task RegisterConnectorCapabilitiesAsync(CancellationToken ct)
    {
        if (MessageBus == null) return;

        var strategies = _registry.GetAll();
        var categoryCounts = strategies
            .GroupBy(s => s.Category)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());

        await MessageBus.PublishAsync(IntelligenceTopics.QueryCapability, new PluginMessage
        {
            Type = "capability.register",
            Source = Id,
            Payload = new Dictionary<string, object>
            {
                ["pluginId"] = Id,
                ["pluginName"] = Name,
                ["pluginType"] = "connector",
                ["capabilities"] = new Dictionary<string, object>
                {
                    ["strategyCount"] = strategies.Count,
                    ["categories"] = categoryCounts,
                    ["supportsConnectionOptimization"] = true,
                    ["supportsSchemaDiscovery"] = true,
                    ["supportsQueryOptimization"] = true,
                    ["supportsAnomalyDetection"] = true
                },
                ["semanticDescription"] = SemanticDescription,
                ["tags"] = SemanticTags
            }
        }, ct);
    }

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
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }
    }

    #endregion
}
