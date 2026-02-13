using System.Collections.Concurrent;
using System.Reflection;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDatabaseProtocol;

/// <summary>
/// Ultimate Database Protocol Plugin - Comprehensive database wire protocol implementation.
///
/// Implements 50+ database wire protocols across categories:
/// - Relational: PostgreSQL v3, MySQL Client/Server, TDS (SQL Server), Oracle TNS, DB2 DRDA
/// - NoSQL: MongoDB Wire Protocol, Redis RESP/RESP3, Cassandra CQL Binary, Memcached
/// - NewSQL: CockroachDB, TiDB, YugabyteDB, Vitess protocols
/// - Time Series: InfluxDB Line Protocol, TimescaleDB, QuestDB ILP, Prometheus Remote Write
/// - Graph: Neo4j Bolt, TinkerPop Gremlin, ArangoDB HTTP/VelocyPack
/// - Search: Elasticsearch Transport, OpenSearch, Apache Solr
/// - Drivers: ADO.NET Provider, JDBC Bridge, ODBC Driver
///
/// Features:
/// - Full wire protocol encoding/decoding
/// - Connection lifecycle management
/// - Authentication (SCRAM, Kerberos, Certificate, OAuth)
/// - SSL/TLS encryption
/// - Connection pooling
/// - Query parameter binding
/// - Streaming results
/// - Transaction management
/// - Prepared statements
/// - Batch operations
/// - Protocol statistics and monitoring
/// - Intelligence-aware protocol selection
/// </summary>
public sealed class UltimateDatabaseProtocolPlugin : DataWarehouse.SDK.Contracts.Hierarchy.StoragePluginBase, IDisposable
{
    private readonly DatabaseProtocolStrategyRegistry _registry;
    private readonly ConcurrentDictionary<string, long> _usageStats = new();
    private readonly object _statsLock = new();
    private bool _disposed;
    private bool _initialized;

    // Statistics
    private long _totalConnections;
    private long _totalQueries;
    private long _totalTransactions;

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.protocol.ultimate";

    /// <inheritdoc/>
    public override string Name => "Ultimate Database Protocol";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    /// <summary>
    /// Semantic description of this plugin for AI discovery.
    /// </summary>
    public string SemanticDescription =>
        "Ultimate database protocol plugin providing 50+ wire protocol implementations including PostgreSQL, MySQL, " +
        "SQL Server TDS, MongoDB, Redis RESP, Cassandra CQL, Neo4j Bolt, Elasticsearch, and InfluxDB. " +
        "Supports full protocol encoding/decoding, authentication, SSL/TLS, transactions, and streaming.";

    /// <summary>
    /// Semantic tags for AI discovery and categorization.
    /// </summary>
    public string[] SemanticTags =>
    [
        "database", "protocol", "wire-protocol", "postgresql", "mysql", "mongodb", "redis",
        "cassandra", "elasticsearch", "neo4j", "influxdb", "sql-server", "oracle", "connector"
    ];

    /// <summary>
    /// Gets the protocol strategy registry.
    /// </summary>
    public IDatabaseProtocolStrategyRegistry Registry => _registry;

    /// <summary>
    /// Declares all capabilities provided by this plugin.
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
                    CapabilityId = $"{Id}.protocol",
                    DisplayName = "Ultimate Database Protocol",
                    Description = SemanticDescription,
                    Category = SDK.Contracts.CapabilityCategory.Connector,
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = [.. SemanticTags]
                }
            };

            // Add strategy-based capabilities
            foreach (var strategy in _registry.GetAllStrategies())
            {
                var tags = new List<string>
                {
                    "database-protocol",
                    "connector",
                    strategy.ProtocolInfo.Family.ToString().ToLowerInvariant()
                };

                if (strategy.ProtocolInfo.Capabilities.SupportsTransactions)
                    tags.Add("transactions");
                if (strategy.ProtocolInfo.Capabilities.SupportsSsl)
                    tags.Add("ssl");
                if (strategy.ProtocolInfo.Capabilities.SupportsStreaming)
                    tags.Add("streaming");

                capabilities.Add(new()
                {
                    CapabilityId = $"{Id}.{strategy.StrategyId.ToLowerInvariant().Replace(".", "-")}",
                    DisplayName = strategy.StrategyName,
                    Description = $"{strategy.StrategyName} wire protocol for {strategy.ProtocolInfo.ProtocolName}",
                    Category = SDK.Contracts.CapabilityCategory.Connector,
                    SubCategory = strategy.ProtocolInfo.Family.ToString(),
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = [.. tags]
                });
            }

            return capabilities.AsReadOnly();
        }
    }

    /// <summary>
    /// Initializes a new instance of the Ultimate Database Protocol plugin.
    /// </summary>
    public UltimateDatabaseProtocolPlugin()
    {
        _registry = new DatabaseProtocolStrategyRegistry();
    }

    /// <inheritdoc/>
    protected override async Task OnStartCoreAsync(CancellationToken ct)
    {
        if (_initialized) return;

        // Auto-discover all strategies in this assembly
        _registry.DiscoverStrategies(Assembly.GetExecutingAssembly());

        // Publish registration events for each strategy
        var strategies = _registry.GetAllStrategies();
        foreach (var strategy in strategies)
        {
            await PublishStrategyRegisteredAsync(strategy);

            // Configure Intelligence if available
            if (strategy is DatabaseProtocolStrategyBase baseStrategy && MessageBus != null)
            {
                baseStrategy.ConfigureIntelligence(MessageBus);
            }
        }

        _initialized = true;
    }

    /// <inheritdoc/>
    protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct)
    {
        // Register connector capabilities with Intelligence
        await RegisterProtocolCapabilitiesAsync(ct);
    }

    /// <inheritdoc/>
    protected override Task OnStartWithoutIntelligenceAsync(CancellationToken ct)
    {
        // Works without Intelligence with reduced optimization
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

        response.Metadata["RegisteredStrategies"] = _registry.GetAllStrategies().Count.ToString();
        response.Metadata["SemanticDescription"] = SemanticDescription;

        // Add category counts
        var families = _registry.GetAllStrategies()
            .GroupBy(s => s.ProtocolInfo.Family)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());

        foreach (var family in families)
        {
            response.Metadata[$"Family.{family.Key}"] = family.Value.ToString();
        }

        return response;
    }

    /// <inheritdoc/>
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
    {
        var strategies = _registry.GetAllStrategies();
        var familyCounts = strategies
            .GroupBy(s => s.ProtocolInfo.Family)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());

        var transactionSupport = strategies.Count(s => s.ProtocolInfo.Capabilities.SupportsTransactions);
        var sslSupport = strategies.Count(s => s.ProtocolInfo.Capabilities.SupportsSsl);
        var streamingSupport = strategies.Count(s => s.ProtocolInfo.Capabilities.SupportsStreaming);

        return new List<KnowledgeObject>
        {
            new()
            {
                Id = $"{Id}:overview",
                Topic = "database-protocol",
                SourcePluginId = Id,
                SourcePluginName = Name,
                KnowledgeType = "capability",
                Description = "Ultimate Database Protocol Overview",
                Payload = new Dictionary<string, object>
                {
                    ["description"] = SemanticDescription,
                    ["totalStrategies"] = strategies.Count,
                    ["families"] = familyCounts,
                    ["features"] = new Dictionary<string, object>
                    {
                        ["transactionSupport"] = transactionSupport,
                        ["sslSupport"] = sslSupport,
                        ["streamingSupport"] = streamingSupport
                    },
                    ["familyBreakdown"] = new Dictionary<string, object>
                    {
                        ["relational"] = familyCounts.GetValueOrDefault("Relational", 0),
                        ["nosql"] = familyCounts.GetValueOrDefault("NoSQL", 0),
                        ["newsql"] = familyCounts.GetValueOrDefault("NewSQL", 0),
                        ["timeseries"] = familyCounts.GetValueOrDefault("TimeSeries", 0),
                        ["graph"] = familyCounts.GetValueOrDefault("Graph", 0),
                        ["search"] = familyCounts.GetValueOrDefault("Search", 0),
                        ["driver"] = familyCounts.GetValueOrDefault("Driver", 0)
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
            new() { Name = "protocol.connect", DisplayName = "Connect", Description = "Establish database connection using wire protocol" },
            new() { Name = "protocol.disconnect", DisplayName = "Disconnect", Description = "Close database connection" },
            new() { Name = "protocol.query", DisplayName = "Execute Query", Description = "Execute SQL/query using wire protocol" },
            new() { Name = "protocol.execute", DisplayName = "Execute Command", Description = "Execute non-query command" },
            new() { Name = "protocol.begin-transaction", DisplayName = "Begin Transaction", Description = "Start a transaction" },
            new() { Name = "protocol.commit", DisplayName = "Commit", Description = "Commit a transaction" },
            new() { Name = "protocol.rollback", DisplayName = "Rollback", Description = "Rollback a transaction" },
            new() { Name = "protocol.ping", DisplayName = "Ping", Description = "Send keepalive/ping message" },
            new() { Name = "protocol.list-strategies", DisplayName = "List Strategies", Description = "List available protocol strategies" },
            new() { Name = "protocol.list-families", DisplayName = "List Families", Description = "List strategies by protocol family" },
            new() { Name = "protocol.stats", DisplayName = "Statistics", Description = "Get protocol usage statistics" }
        ];
    }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["TotalStrategies"] = _registry.GetAllStrategies().Count;
        metadata["SemanticDescription"] = SemanticDescription;
        metadata["SemanticTags"] = SemanticTags;
        return metadata;
    }

    /// <inheritdoc/>
    public override Task OnMessageAsync(PluginMessage message)
    {
        return message.Type switch
        {
            "protocol.list-strategies" => HandleListStrategiesAsync(message),
            "protocol.list-families" => HandleListFamiliesAsync(message),
            "protocol.stats" => HandleStatsAsync(message),
            _ => base.OnMessageAsync(message)
        };
    }

    /// <summary>
    /// Gets a protocol strategy by its identifier.
    /// </summary>
    public IDatabaseProtocolStrategy? GetStrategy(string strategyId)
    {
        var strategy = _registry.GetStrategy(strategyId);
        if (strategy != null)
        {
            _usageStats.AddOrUpdate(strategyId, 1, (_, count) => count + 1);
        }
        return strategy;
    }

    /// <summary>
    /// Gets all strategies belonging to a specific protocol family.
    /// </summary>
    public IReadOnlyCollection<IDatabaseProtocolStrategy> GetStrategiesByFamily(ProtocolFamily family)
    {
        return _registry.GetStrategiesByFamily(family);
    }

    /// <summary>
    /// Lists all registered strategies.
    /// </summary>
    public IReadOnlyCollection<IDatabaseProtocolStrategy> ListStrategies()
    {
        return _registry.GetAllStrategies();
    }

    /// <summary>
    /// Creates a connection using the specified strategy.
    /// </summary>
    public async Task<IDatabaseProtocolStrategy> CreateConnectionAsync(
        string strategyId,
        ConnectionParameters parameters,
        CancellationToken ct = default)
    {
        var strategy = GetStrategy(strategyId);
        if (strategy == null)
        {
            throw new ArgumentException($"Strategy '{strategyId}' not found", nameof(strategyId));
        }

        await strategy.ConnectAsync(parameters, ct);
        Interlocked.Increment(ref _totalConnections);

        return strategy;
    }

    #region Message Handlers

    private Task HandleListStrategiesAsync(PluginMessage message)
    {
        var strategies = _registry.GetAllStrategies();

        var strategyList = strategies.Select(s => new Dictionary<string, object>
        {
            ["id"] = s.StrategyId,
            ["displayName"] = s.StrategyName,
            ["protocolName"] = s.ProtocolInfo.ProtocolName,
            ["protocolVersion"] = s.ProtocolInfo.ProtocolVersion,
            ["family"] = s.ProtocolInfo.Family.ToString(),
            ["defaultPort"] = s.ProtocolInfo.DefaultPort,
            ["supportsTransactions"] = s.ProtocolInfo.Capabilities.SupportsTransactions,
            ["supportsSsl"] = s.ProtocolInfo.Capabilities.SupportsSsl,
            ["supportsStreaming"] = s.ProtocolInfo.Capabilities.SupportsStreaming
        }).ToList();

        message.Payload["strategies"] = strategyList;
        message.Payload["count"] = strategyList.Count;

        return Task.CompletedTask;
    }

    private Task HandleListFamiliesAsync(PluginMessage message)
    {
        if (message.Payload.TryGetValue("family", out var famObj) && famObj is string familyStr
            && Enum.TryParse<ProtocolFamily>(familyStr, true, out var family))
        {
            var strategies = _registry.GetStrategiesByFamily(family);
            message.Payload["strategies"] = strategies.Select(s => new Dictionary<string, object>
            {
                ["id"] = s.StrategyId,
                ["displayName"] = s.StrategyName,
                ["protocolName"] = s.ProtocolInfo.ProtocolName
            }).ToList();
            message.Payload["count"] = strategies.Count;
            message.Payload["family"] = family.ToString();
        }
        else
        {
            // Return all families with counts
            var familyCounts = _registry.GetAllStrategies()
                .GroupBy(s => s.ProtocolInfo.Family)
                .ToDictionary(g => g.Key.ToString(), g => g.Count());
            message.Payload["families"] = familyCounts;
        }

        return Task.CompletedTask;
    }

    private Task HandleStatsAsync(PluginMessage message)
    {
        message.Payload["registeredStrategies"] = _registry.GetAllStrategies().Count;
        message.Payload["usageByStrategy"] = new Dictionary<string, long>(_usageStats);
        message.Payload["totalConnections"] = Interlocked.Read(ref _totalConnections);
        message.Payload["totalQueries"] = Interlocked.Read(ref _totalQueries);
        message.Payload["totalTransactions"] = Interlocked.Read(ref _totalTransactions);
        message.Payload["initialized"] = _initialized;

        return Task.CompletedTask;
    }

    #endregion

    #region Internal Helpers

    private async Task RegisterProtocolCapabilitiesAsync(CancellationToken ct)
    {
        if (MessageBus == null) return;

        var strategies = _registry.GetAllStrategies();
        var familyCounts = strategies
            .GroupBy(s => s.ProtocolInfo.Family)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());

        await MessageBus.PublishAsync(IntelligenceTopics.QueryCapability, new PluginMessage
        {
            Type = "capability.register",
            Source = Id,
            Payload = new Dictionary<string, object>
            {
                ["pluginId"] = Id,
                ["pluginName"] = Name,
                ["pluginType"] = "database-protocol",
                ["capabilities"] = new Dictionary<string, object>
                {
                    ["strategyCount"] = strategies.Count,
                    ["families"] = familyCounts,
                    ["supportsTransactions"] = true,
                    ["supportsSsl"] = true,
                    ["supportsStreaming"] = true,
                    ["supportsAuthentication"] = true
                },
                ["semanticDescription"] = SemanticDescription,
                ["tags"] = SemanticTags
            }
        }, ct);
    }

    private async Task PublishStrategyRegisteredAsync(IDatabaseProtocolStrategy strategy)
    {
        if (MessageBus == null) return;

        try
        {
            var eventMessage = new PluginMessage
            {
                Type = "protocol.strategy.registered",
                Source = Id,
                Payload = new Dictionary<string, object>
                {
                    ["strategyId"] = strategy.StrategyId,
                    ["displayName"] = strategy.StrategyName,
                    ["protocolName"] = strategy.ProtocolInfo.ProtocolName,
                    ["family"] = strategy.ProtocolInfo.Family.ToString()
                }
            };

            await MessageBus.PublishAsync("protocol.strategy.registered", eventMessage);
        }
        catch
        {
            // Gracefully handle message bus unavailability
        }
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes plugin resources.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_disposed) return;

            // Dispose all strategies
            foreach (var strategy in _registry.GetAllStrategies())
            {
            if (strategy is IDisposable disposable)
            {
            try
            {
            disposable.Dispose();
            }
            catch
            {
            // Ignore disposal errors
            }
            }
            }

            _disposed = true;
        }
        base.Dispose(disposing);
    }

    #endregion

    #region Hierarchy StoragePluginBase Abstract Methods
    /// <inheritdoc/>
    public override Task<StorageObjectMetadata> StoreAsync(string key, Stream data, IDictionary<string, string>? metadata = null, CancellationToken ct = default)
        => throw new NotSupportedException("Use protocol-specific strategy methods instead.");
    /// <inheritdoc/>
    public override Task<Stream> RetrieveAsync(string key, CancellationToken ct = default)
        => throw new NotSupportedException("Use protocol-specific strategy methods instead.");
    /// <inheritdoc/>
    public override Task DeleteAsync(string key, CancellationToken ct = default)
        => throw new NotSupportedException("Use protocol-specific strategy methods instead.");
    /// <inheritdoc/>
    public override Task<bool> ExistsAsync(string key, CancellationToken ct = default)
        => throw new NotSupportedException("Use protocol-specific strategy methods instead.");
    /// <inheritdoc/>
    public override async IAsyncEnumerable<StorageObjectMetadata> ListAsync(string? prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    { await Task.CompletedTask; yield break; }
    /// <inheritdoc/>
    public override Task<StorageObjectMetadata> GetMetadataAsync(string key, CancellationToken ct = default)
        => Task.FromResult(new StorageObjectMetadata { Key = key });
    /// <inheritdoc/>
    public override Task<StorageHealthInfo> GetHealthAsync(CancellationToken ct = default)
        => Task.FromResult(new StorageHealthInfo { Status = DataWarehouse.SDK.Contracts.Storage.HealthStatus.Healthy });
    #endregion
}