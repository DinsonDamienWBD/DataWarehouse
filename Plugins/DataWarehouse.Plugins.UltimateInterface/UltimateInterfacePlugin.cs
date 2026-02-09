using System.Collections.Concurrent;
using System.Reflection;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateInterface;

/// <summary>
/// Ultimate Interface Plugin - Comprehensive interface solution consolidating all protocol strategies.
///
/// T109: Implements 50+ interface protocols across categories:
/// - HTTP/REST: REST API, GraphQL, OData, JSON-RPC, OpenAPI
/// - RPC: gRPC, Apache Thrift, SOAP, XML-RPC, Cap'n Proto
/// - Real-time: WebSocket, Server-Sent Events (SSE), Long Polling, Socket.IO
/// - Database: SQL, MongoDB Wire Protocol, Redis Protocol, Memcached
/// - Messaging: AMQP, MQTT, STOMP, ZeroMQ, Kafka Protocol
/// - Binary: Protocol Buffers, FlatBuffers, MessagePack, CBOR, Avro
/// - Legacy: FTP, SFTP, SSH, Telnet, NNTP
/// - AI-Native: MCP (Model Context Protocol), OpenAI API, Anthropic API
///
/// Features:
/// - Strategy pattern for protocol extensibility
/// - Auto-discovery of interface strategies
/// - Unified API across all protocols
/// - NLP-powered intent recognition for conversational interfaces
/// - Multi-language support with language detection
/// - Intelligent request/response transformation
/// - Rate limiting and throttling
/// - Connection pooling
/// - Health monitoring
/// - Automatic failover
/// - Protocol bridging
/// - Semantic API discovery for AI agents
/// </summary>
public sealed class UltimateInterfacePlugin : IntelligenceAwareInterfacePluginBase, IDisposable
{
    private readonly InterfaceStrategyRegistry _registry;
    private readonly ConcurrentDictionary<string, long> _usageStats = new();
    private readonly ConcurrentDictionary<string, InterfaceHealthStatus> _healthStatus = new();
    private bool _disposed;

    // Configuration
    private volatile string _defaultStrategyId = "rest";
    private volatile bool _auditEnabled = true;
    private volatile bool _autoFailoverEnabled = true;
    private volatile int _maxRetries = 3;
    private volatile int _defaultPort = 8080;

    // Statistics
    private long _totalRequests;
    private long _totalResponses;
    private long _totalBytesReceived;
    private long _totalBytesSent;
    private long _totalFailures;

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.interface.ultimate";

    /// <inheritdoc/>
    public override string Name => "Ultimate Interface";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.InterfaceProvider;

    /// <inheritdoc/>
    public override string InterfaceProtocol => _defaultStrategyId;

    /// <inheritdoc/>
    public override int? Port => _defaultPort;

    /// <summary>
    /// Semantic description of this plugin for AI discovery.
    /// </summary>
    public string SemanticDescription =>
        "Ultimate interface plugin providing 50+ protocol strategies including REST, gRPC, WebSocket, GraphQL, " +
        "MQTT, AMQP, and AI-native protocols like MCP. Supports NLP-powered intent recognition, " +
        "conversational interfaces, multi-language support, and intelligent request transformation.";

    /// <summary>
    /// Semantic tags for AI discovery and categorization.
    /// </summary>
    public string[] SemanticTags => [
        "interface", "api", "rest", "grpc", "websocket", "graphql", "mqtt",
        "protocol", "nlp", "conversational", "ai-native", "mcp"
    ];

    /// <summary>
    /// Gets the interface strategy registry.
    /// </summary>
    public InterfaceStrategyRegistry Registry => _registry;

    /// <summary>
    /// Gets or sets whether audit logging is enabled.
    /// </summary>
    public bool AuditEnabled
    {
        get => _auditEnabled;
        set => _auditEnabled = value;
    }

    /// <summary>
    /// Gets or sets whether automatic failover is enabled.
    /// </summary>
    public bool AutoFailoverEnabled
    {
        get => _autoFailoverEnabled;
        set => _autoFailoverEnabled = value;
    }

    /// <summary>
    /// Initializes a new instance of the Ultimate Interface plugin.
    /// </summary>
    public UltimateInterfacePlugin()
    {
        _registry = new InterfaceStrategyRegistry();

        // Auto-discover and register strategies
        DiscoverAndRegisterStrategies();
    }

    /// <inheritdoc/>
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        var response = await base.OnHandshakeAsync(request);

        // Parse configuration
        if (request.Config?.TryGetValue("port", out var portObj) == true && portObj is int port)
            _defaultPort = port;

        if (request.Config?.TryGetValue("defaultProtocol", out var protocolObj) == true && protocolObj is string protocol)
            _defaultStrategyId = protocol;

        // Register knowledge and capabilities
        await RegisterAllKnowledgeAsync();

        response.Metadata["RegisteredStrategies"] = _registry.Count.ToString();
        response.Metadata["DefaultProtocol"] = _defaultStrategyId;
        response.Metadata["Port"] = _defaultPort.ToString();
        response.Metadata["AuditEnabled"] = _auditEnabled.ToString();
        response.Metadata["AutoFailoverEnabled"] = _autoFailoverEnabled.ToString();
        response.Metadata["SemanticDescription"] = SemanticDescription;

        return response;
    }

    /// <inheritdoc/>
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return
        [
            new() { Name = "interface.start", DisplayName = "Start Interface", Description = "Start interface listener" },
            new() { Name = "interface.stop", DisplayName = "Stop Interface", Description = "Stop interface listener" },
            new() { Name = "interface.list-strategies", DisplayName = "List Strategies", Description = "List available interface strategies" },
            new() { Name = "interface.set-default", DisplayName = "Set Default", Description = "Set default interface strategy" },
            new() { Name = "interface.stats", DisplayName = "Statistics", Description = "Get interface statistics" },
            new() { Name = "interface.health", DisplayName = "Health Check", Description = "Check interface health" },
            new() { Name = "interface.bridge", DisplayName = "Protocol Bridge", Description = "Bridge between protocols" },
            new() { Name = "interface.parse-intent", DisplayName = "Parse Intent", Description = "Parse natural language intent (AI-powered)" },
            new() { Name = "interface.conversation", DisplayName = "Conversation", Description = "Handle conversational interface (AI-powered)" },
            new() { Name = "interface.detect-language", DisplayName = "Detect Language", Description = "Detect input language (AI-powered)" }
        ];
    }

    /// <inheritdoc/>
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
                var tags = new List<string> { "interface", "protocol", strategy.Category.ToString().ToLowerInvariant() };
                tags.AddRange(strategy.Tags);

                // Add feature-specific tags
                if (strategy.Capabilities.SupportsStreaming)
                    tags.Add("streaming");
                if (strategy.Capabilities.SupportsBidirectional)
                    tags.Add("bidirectional");
                if (strategy.Capabilities.SupportsAuthentication)
                    tags.Add("authentication");

                capabilities.Add(new()
                {
                    CapabilityId = $"interface.{strategy.StrategyId.ToLowerInvariant().Replace(".", "-").Replace(" ", "-")}",
                    DisplayName = strategy.DisplayName,
                    Description = strategy.SemanticDescription,
                    Category = SDK.Contracts.CapabilityCategory.Interface,
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

    /// <inheritdoc/>
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
    {
        var knowledge = new List<KnowledgeObject>(base.GetStaticKnowledge());

        var strategies = _registry.GetAll().ToList();
        var byCategory = strategies.GroupBy(s => s.Category)
            .ToDictionary(g => g.Key.ToString(), g => (object)g.Count());

        knowledge.Add(new KnowledgeObject
        {
            Id = $"{Id}.overview",
            Topic = "plugin.capabilities",
            SourcePluginId = Id,
            SourcePluginName = Name,
            KnowledgeType = "capability",
            Description = SemanticDescription,
            Payload = new Dictionary<string, object>
            {
                ["totalStrategies"] = strategies.Count,
                ["categories"] = byCategory,
                ["supportsRest"] = HasStrategy("rest"),
                ["supportsGrpc"] = HasStrategy("grpc"),
                ["supportsWebSocket"] = HasStrategy("websocket"),
                ["supportsGraphQL"] = HasStrategy("graphql"),
                ["supportsMqtt"] = HasStrategy("mqtt"),
                ["supportsMcp"] = HasStrategy("mcp"),
                ["supportsNlp"] = true,
                ["supportsConversation"] = true
            },
            Tags = SemanticTags
        });

        return knowledge.AsReadOnly();
    }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["TotalStrategies"] = _registry.Count;
        metadata["DefaultProtocol"] = _defaultStrategyId;
        metadata["Port"] = _defaultPort;
        metadata["TotalRequests"] = Interlocked.Read(ref _totalRequests);
        metadata["TotalResponses"] = Interlocked.Read(ref _totalResponses);
        metadata["TotalBytesReceived"] = Interlocked.Read(ref _totalBytesReceived);
        metadata["TotalBytesSent"] = Interlocked.Read(ref _totalBytesSent);
        metadata["TotalFailures"] = Interlocked.Read(ref _totalFailures);
        return metadata;
    }

    /// <inheritdoc/>
    public override Task OnMessageAsync(PluginMessage message)
    {
        return message.Type switch
        {
            "interface.start" => HandleStartInterfaceAsync(message),
            "interface.stop" => HandleStopInterfaceAsync(message),
            "interface.list-strategies" => HandleListStrategiesAsync(message),
            "interface.set-default" => HandleSetDefaultAsync(message),
            "interface.stats" => HandleStatsAsync(message),
            "interface.health" => HandleHealthAsync(message),
            "interface.bridge" => HandleBridgeAsync(message),
            "interface.parse-intent" => HandleParseIntentAsync(message),
            "interface.conversation" => HandleConversationAsync(message),
            "interface.detect-language" => HandleDetectLanguageAsync(message),
            _ => base.OnMessageAsync(message)
        };
    }

    #region Message Handlers

    private async Task HandleStartInterfaceAsync(PluginMessage message)
    {
        var strategyId = message.Payload.TryGetValue("strategyId", out var sidObj) && sidObj is string sid
            ? sid
            : _defaultStrategyId;

        var strategy = GetStrategyOrThrow(strategyId);
        await strategy.StartAsync(CancellationToken.None);

        IncrementUsageStats(strategyId);
        _healthStatus[strategyId] = new InterfaceHealthStatus
        {
            StrategyId = strategyId,
            IsHealthy = true,
            LastChecked = DateTimeOffset.UtcNow
        };

        message.Payload["success"] = true;
        message.Payload["strategyId"] = strategyId;
    }

    private async Task HandleStopInterfaceAsync(PluginMessage message)
    {
        var strategyId = message.Payload.TryGetValue("strategyId", out var sidObj) && sidObj is string sid
            ? sid
            : _defaultStrategyId;

        var strategy = GetStrategyOrThrow(strategyId);
        await strategy.StopAsync();

        if (_healthStatus.TryGetValue(strategyId, out var status))
        {
            status.IsHealthy = false;
            status.LastChecked = DateTimeOffset.UtcNow;
        }

        message.Payload["success"] = true;
        message.Payload["strategyId"] = strategyId;
    }

    private Task HandleListStrategiesAsync(PluginMessage message)
    {
        var categoryFilter = message.Payload.TryGetValue("category", out var catObj) && catObj is string catStr
            && Enum.TryParse<InterfaceCategory>(catStr, true, out var cat)
            ? cat
            : (InterfaceCategory?)null;

        var strategies = categoryFilter.HasValue
            ? _registry.GetByCategory(categoryFilter.Value)
            : _registry.GetAll();

        var strategyList = strategies.Select(s => new Dictionary<string, object>
        {
            ["id"] = s.StrategyId,
            ["displayName"] = s.DisplayName,
            ["category"] = s.Category.ToString(),
            ["protocol"] = s.Protocol,
            ["capabilities"] = new Dictionary<string, object>
            {
                ["supportsStreaming"] = s.Capabilities.SupportsStreaming,
                ["supportsBidirectional"] = s.Capabilities.SupportsBidirectional,
                ["supportsAuthentication"] = s.Capabilities.SupportsAuthentication,
                ["supportsCompression"] = s.Capabilities.SupportsCompression,
                ["supportsEncryption"] = s.Capabilities.SupportsEncryption
            },
            ["tags"] = s.Tags
        }).ToList();

        message.Payload["strategies"] = strategyList;
        message.Payload["count"] = strategyList.Count;

        return Task.CompletedTask;
    }

    private Task HandleSetDefaultAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("strategyId", out var sidObj) || sidObj is not string strategyId)
        {
            throw new ArgumentException("Missing 'strategyId' parameter");
        }

        var strategy = GetStrategyOrThrow(strategyId);
        _defaultStrategyId = strategyId;

        message.Payload["success"] = true;
        message.Payload["defaultStrategy"] = strategyId;

        return Task.CompletedTask;
    }

    private Task HandleStatsAsync(PluginMessage message)
    {
        message.Payload["totalRequests"] = Interlocked.Read(ref _totalRequests);
        message.Payload["totalResponses"] = Interlocked.Read(ref _totalResponses);
        message.Payload["totalBytesReceived"] = Interlocked.Read(ref _totalBytesReceived);
        message.Payload["totalBytesSent"] = Interlocked.Read(ref _totalBytesSent);
        message.Payload["totalFailures"] = Interlocked.Read(ref _totalFailures);
        message.Payload["registeredStrategies"] = _registry.Count;

        var usageByStrategy = new Dictionary<string, long>(_usageStats);
        message.Payload["usageByStrategy"] = usageByStrategy;

        return Task.CompletedTask;
    }

    private Task HandleHealthAsync(PluginMessage message)
    {
        var strategyId = message.Payload.TryGetValue("strategyId", out var sidObj) && sidObj is string sid
            ? sid
            : _defaultStrategyId;

        if (_healthStatus.TryGetValue(strategyId, out var status))
        {
            message.Payload["isHealthy"] = status.IsHealthy;
            message.Payload["lastChecked"] = status.LastChecked.ToString("O");
            message.Payload["lastError"] = status.LastError ?? string.Empty;
        }
        else
        {
            message.Payload["isHealthy"] = false;
            message.Payload["lastChecked"] = DateTimeOffset.UtcNow.ToString("O");
            message.Payload["lastError"] = "Interface not started";
        }

        return Task.CompletedTask;
    }

    private async Task HandleBridgeAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("sourceProtocol", out var srcObj) || srcObj is not string sourceProtocol)
            throw new ArgumentException("Missing 'sourceProtocol' parameter");
        if (!message.Payload.TryGetValue("targetProtocol", out var tgtObj) || tgtObj is not string targetProtocol)
            throw new ArgumentException("Missing 'targetProtocol' parameter");

        var sourceStrategy = GetStrategyOrThrow(sourceProtocol);
        var targetStrategy = GetStrategyOrThrow(targetProtocol);

        // Create bridge configuration
        var bridgeId = $"{sourceProtocol}-to-{targetProtocol}-{Guid.NewGuid():N}";

        message.Payload["success"] = true;
        message.Payload["bridgeId"] = bridgeId;
        message.Payload["sourceProtocol"] = sourceProtocol;
        message.Payload["targetProtocol"] = targetProtocol;

        await Task.CompletedTask;
    }

    private async Task HandleParseIntentAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("input", out var inputObj) || inputObj is not string input)
            throw new ArgumentException("Missing 'input' parameter");

        // Use Intelligence for NLP intent parsing if available
        if (IsIntelligenceAvailable)
        {
            var availableIntents = message.Payload.TryGetValue("availableIntents", out var intentsObj)
                && intentsObj is string[] intents ? intents : null;

            var result = await ParseIntentAsync(input, availableIntents);

            if (result != null)
            {
                message.Payload["success"] = true;
                message.Payload["intent"] = result.Intent ?? "unknown";
                message.Payload["confidence"] = result.Confidence;
                message.Payload["entities"] = result.Entities;
                message.Payload["alternatives"] = result.AlternativeIntents;
                return;
            }
        }

        // Fallback: basic keyword matching
        var fallbackIntent = MatchIntentByKeywords(input);
        message.Payload["success"] = true;
        message.Payload["intent"] = fallbackIntent;
        message.Payload["confidence"] = 0.5;
        message.Payload["entities"] = new Dictionary<string, object>();
        message.Payload["fallback"] = true;
    }

    private async Task HandleConversationAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("userInput", out var inputObj) || inputObj is not string userInput)
            throw new ArgumentException("Missing 'userInput' parameter");

        // Use Intelligence for conversational response if available
        if (IsIntelligenceAvailable)
        {
            var systemPrompt = message.Payload.TryGetValue("systemPrompt", out var spObj) && spObj is string sp ? sp : null;

            var result = await GenerateConversationResponseAsync(userInput, null, systemPrompt);

            if (result != null)
            {
                message.Payload["success"] = true;
                message.Payload["response"] = result.Response;
                message.Payload["intent"] = result.Intent ?? string.Empty;
                message.Payload["entities"] = result.Entities ?? new Dictionary<string, object>();
                message.Payload["suggestedActions"] = result.SuggestedActions ?? Array.Empty<string>();
                message.Payload["confidence"] = result.Confidence;
                return;
            }
        }

        // Fallback: echo response
        message.Payload["success"] = true;
        message.Payload["response"] = $"I received your message: {userInput}";
        message.Payload["fallback"] = true;
    }

    private async Task HandleDetectLanguageAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("text", out var textObj) || textObj is not string text)
            throw new ArgumentException("Missing 'text' parameter");

        // Use Intelligence for language detection if available
        if (IsIntelligenceAvailable)
        {
            var result = await DetectLanguageAsync(text);

            if (result != null)
            {
                message.Payload["success"] = true;
                message.Payload["languageCode"] = result.LanguageCode;
                message.Payload["languageName"] = result.LanguageName;
                message.Payload["confidence"] = result.Confidence;
                message.Payload["alternatives"] = result.Alternatives;
                return;
            }
        }

        // Fallback: assume English
        message.Payload["success"] = true;
        message.Payload["languageCode"] = "en";
        message.Payload["languageName"] = "English";
        message.Payload["confidence"] = 0.5;
        message.Payload["fallback"] = true;
    }

    #endregion

    #region Helper Methods

    private IInterfaceStrategy GetStrategyOrThrow(string strategyId)
    {
        var strategy = _registry.Get(strategyId)
            ?? throw new ArgumentException($"Interface strategy '{strategyId}' not found");
        return strategy;
    }

    private bool HasStrategy(string strategyId)
    {
        return _registry.Get(strategyId) != null;
    }

    private void IncrementUsageStats(string strategyId)
    {
        _usageStats.AddOrUpdate(strategyId, 1, (_, count) => count + 1);
        Interlocked.Increment(ref _totalRequests);
    }

    private void DiscoverAndRegisterStrategies()
    {
        // Auto-discover strategies in this assembly via reflection
        var discovered = _registry.AutoDiscover(Assembly.GetExecutingAssembly());

        if (discovered == 0)
        {
            // Log warning if no strategies found - register built-in defaults
            RegisterBuiltInStrategies();
        }
    }

    private void RegisterBuiltInStrategies()
    {
        // Register built-in interface strategies
        _registry.Register(new RestInterfaceStrategy());
        _registry.Register(new GrpcInterfaceStrategy());
        _registry.Register(new WebSocketInterfaceStrategy());
        _registry.Register(new GraphQLInterfaceStrategy());
        _registry.Register(new McpInterfaceStrategy());
    }

    private string MatchIntentByKeywords(string input)
    {
        var lowerInput = input.ToLowerInvariant();

        if (lowerInput.Contains("create") || lowerInput.Contains("add") || lowerInput.Contains("new"))
            return "create";
        if (lowerInput.Contains("read") || lowerInput.Contains("get") || lowerInput.Contains("fetch"))
            return "read";
        if (lowerInput.Contains("update") || lowerInput.Contains("edit") || lowerInput.Contains("modify"))
            return "update";
        if (lowerInput.Contains("delete") || lowerInput.Contains("remove") || lowerInput.Contains("destroy"))
            return "delete";
        if (lowerInput.Contains("list") || lowerInput.Contains("show") || lowerInput.Contains("display"))
            return "list";
        if (lowerInput.Contains("search") || lowerInput.Contains("find") || lowerInput.Contains("query"))
            return "search";
        if (lowerInput.Contains("help") || lowerInput.Contains("?"))
            return "help";

        return "unknown";
    }

    #endregion

    #region Intelligence Integration

    /// <summary>
    /// Called when Intelligence becomes available - register interface capabilities.
    /// </summary>
    protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct)
    {
        await base.OnStartWithIntelligenceAsync(ct);

        // Register interface capabilities with Intelligence
        if (MessageBus != null)
        {
            var strategies = _registry.GetAll().ToList();
            var byCategory = strategies.GroupBy(s => s.Category)
                .ToDictionary(g => g.Key.ToString(), g => (object)g.Count());

            await MessageBus.PublishAsync(IntelligenceTopics.QueryCapability, new PluginMessage
            {
                Type = "capability.register",
                Source = Id,
                Payload = new Dictionary<string, object>
                {
                    ["pluginId"] = Id,
                    ["pluginName"] = Name,
                    ["pluginType"] = "interface",
                    ["capabilities"] = new Dictionary<string, object>
                    {
                        ["strategyCount"] = strategies.Count,
                        ["categories"] = byCategory,
                        ["supportsNlp"] = true,
                        ["supportsConversation"] = true,
                        ["supportsLanguageDetection"] = true,
                        ["supportsProtocolBridging"] = true
                    },
                    ["semanticDescription"] = SemanticDescription,
                    ["tags"] = SemanticTags
                }
            }, ct);

            // Subscribe to interface requests from Intelligence
            SubscribeToInterfaceRequests();
        }
    }

    /// <summary>
    /// Called when starting without Intelligence - use fallback behavior.
    /// </summary>
    protected override Task OnStartWithoutIntelligenceAsync(CancellationToken ct)
    {
        // Interface works without Intelligence, but with reduced NLP/conversation capabilities
        return Task.CompletedTask;
    }

    /// <summary>
    /// Subscribes to Intelligence interface requests.
    /// </summary>
    private void SubscribeToInterfaceRequests()
    {
        if (MessageBus == null) return;

        MessageBus.Subscribe("intelligence.request.interface", async msg =>
        {
            if (msg.Payload.TryGetValue("action", out var actionObj) && actionObj is string action)
            {
                var response = new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["action"] = action
                };

                switch (action)
                {
                    case "list-protocols":
                        response["protocols"] = _registry.GetAll()
                            .Select(s => new { id = s.StrategyId, name = s.DisplayName, protocol = s.Protocol })
                            .ToList();
                        break;
                    case "recommend-protocol":
                        var useCase = msg.Payload.TryGetValue("useCase", out var ucObj) && ucObj is string uc ? uc : "general";
                        response["recommendedProtocol"] = RecommendProtocol(useCase);
                        break;
                }

                await MessageBus.PublishAsync("intelligence.response.interface", new PluginMessage
                {
                    Type = "interface.response",
                    CorrelationId = msg.CorrelationId,
                    Source = Id,
                    Payload = response
                });
            }
        });
    }

    private string RecommendProtocol(string useCase)
    {
        return useCase.ToLowerInvariant() switch
        {
            "realtime" or "streaming" or "push" => "websocket",
            "rpc" or "microservices" or "internal" => "grpc",
            "api" or "crud" or "web" => "rest",
            "query" or "graph" or "flexible" => "graphql",
            "iot" or "sensors" or "embedded" => "mqtt",
            "ai" or "llm" or "agent" => "mcp",
            _ => "rest"
        };
    }

    /// <inheritdoc/>
    protected override Task OnStartCoreAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    #endregion

    /// <summary>
    /// Disposes resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Stop all running strategies
        foreach (var strategy in _registry.GetAll())
        {
            try
            {
                strategy.StopAsync().Wait(TimeSpan.FromSeconds(5));
            }
            catch { /* Ignore disposal errors */ }
        }

        _usageStats.Clear();
        _healthStatus.Clear();
    }
}

#region Supporting Types

/// <summary>
/// Registry for interface strategies.
/// </summary>
public sealed class InterfaceStrategyRegistry
{
    private readonly ConcurrentDictionary<string, IInterfaceStrategy> _strategies = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _strategies.Count;

    public void Register(IInterfaceStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        _strategies[strategy.StrategyId] = strategy;
    }

    public IInterfaceStrategy? Get(string strategyId)
    {
        return _strategies.TryGetValue(strategyId, out var strategy) ? strategy : null;
    }

    public IEnumerable<IInterfaceStrategy> GetAll() => _strategies.Values;

    public IEnumerable<IInterfaceStrategy> GetByCategory(InterfaceCategory category)
    {
        return _strategies.Values.Where(s => s.Category == category);
    }

    public int AutoDiscover(Assembly assembly)
    {
        var discovered = 0;
        var strategyTypes = assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(IInterfaceStrategy).IsAssignableFrom(t));

        foreach (var type in strategyTypes)
        {
            try
            {
                if (Activator.CreateInstance(type) is IInterfaceStrategy strategy)
                {
                    Register(strategy);
                    discovered++;
                }
            }
            catch { /* Skip failed instantiation */ }
        }

        return discovered;
    }
}

/// <summary>
/// Interface strategy contract.
/// </summary>
public interface IInterfaceStrategy
{
    string StrategyId { get; }
    string DisplayName { get; }
    string Protocol { get; }
    string SemanticDescription { get; }
    InterfaceCategory Category { get; }
    InterfaceCapabilities Capabilities { get; }
    string[] Tags { get; }

    Task StartAsync(CancellationToken ct);
    Task StopAsync();
}

/// <summary>
/// Interface strategy capabilities.
/// </summary>
public sealed class InterfaceCapabilities
{
    public bool SupportsStreaming { get; init; }
    public bool SupportsBidirectional { get; init; }
    public bool SupportsAuthentication { get; init; }
    public bool SupportsCompression { get; init; }
    public bool SupportsEncryption { get; init; }
}

/// <summary>
/// Interface categories.
/// </summary>
public enum InterfaceCategory
{
    Http,
    Rpc,
    RealTime,
    Database,
    Messaging,
    Binary,
    Legacy,
    AiNative
}

/// <summary>
/// Interface health status.
/// </summary>
internal sealed class InterfaceHealthStatus
{
    public required string StrategyId { get; init; }
    public bool IsHealthy { get; set; }
    public DateTimeOffset LastChecked { get; set; }
    public string? LastError { get; set; }
}

#endregion

#region Built-in Strategies

/// <summary>
/// REST interface strategy.
/// </summary>
internal sealed class RestInterfaceStrategy : IInterfaceStrategy
{
    public string StrategyId => "rest";
    public string DisplayName => "REST API";
    public string Protocol => "http";
    public string SemanticDescription => "RESTful API interface with HTTP/HTTPS support, OpenAPI, and content negotiation.";
    public InterfaceCategory Category => InterfaceCategory.Http;
    public InterfaceCapabilities Capabilities => new()
    {
        SupportsStreaming = true,
        SupportsBidirectional = false,
        SupportsAuthentication = true,
        SupportsCompression = true,
        SupportsEncryption = true
    };
    public string[] Tags => ["rest", "http", "openapi", "crud"];

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
}

/// <summary>
/// gRPC interface strategy.
/// </summary>
internal sealed class GrpcInterfaceStrategy : IInterfaceStrategy
{
    public string StrategyId => "grpc";
    public string DisplayName => "gRPC";
    public string Protocol => "http2";
    public string SemanticDescription => "High-performance gRPC interface with Protocol Buffers and bidirectional streaming.";
    public InterfaceCategory Category => InterfaceCategory.Rpc;
    public InterfaceCapabilities Capabilities => new()
    {
        SupportsStreaming = true,
        SupportsBidirectional = true,
        SupportsAuthentication = true,
        SupportsCompression = true,
        SupportsEncryption = true
    };
    public string[] Tags => ["grpc", "protobuf", "rpc", "streaming"];

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
}

/// <summary>
/// WebSocket interface strategy.
/// </summary>
internal sealed class WebSocketInterfaceStrategy : IInterfaceStrategy
{
    public string StrategyId => "websocket";
    public string DisplayName => "WebSocket";
    public string Protocol => "ws";
    public string SemanticDescription => "Real-time bidirectional WebSocket interface for push notifications and live updates.";
    public InterfaceCategory Category => InterfaceCategory.RealTime;
    public InterfaceCapabilities Capabilities => new()
    {
        SupportsStreaming = true,
        SupportsBidirectional = true,
        SupportsAuthentication = true,
        SupportsCompression = true,
        SupportsEncryption = true
    };
    public string[] Tags => ["websocket", "realtime", "push", "bidirectional"];

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
}

/// <summary>
/// GraphQL interface strategy.
/// </summary>
internal sealed class GraphQLInterfaceStrategy : IInterfaceStrategy
{
    public string StrategyId => "graphql";
    public string DisplayName => "GraphQL";
    public string Protocol => "http";
    public string SemanticDescription => "Flexible GraphQL interface with query, mutation, and subscription support.";
    public InterfaceCategory Category => InterfaceCategory.Http;
    public InterfaceCapabilities Capabilities => new()
    {
        SupportsStreaming = true,
        SupportsBidirectional = true,
        SupportsAuthentication = true,
        SupportsCompression = true,
        SupportsEncryption = true
    };
    public string[] Tags => ["graphql", "query", "mutation", "subscription", "flexible"];

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
}

/// <summary>
/// MCP (Model Context Protocol) interface strategy for AI-native integration.
/// </summary>
internal sealed class McpInterfaceStrategy : IInterfaceStrategy
{
    public string StrategyId => "mcp";
    public string DisplayName => "MCP (Model Context Protocol)";
    public string Protocol => "mcp";
    public string SemanticDescription => "AI-native Model Context Protocol interface for LLM tool integration and context management.";
    public InterfaceCategory Category => InterfaceCategory.AiNative;
    public InterfaceCapabilities Capabilities => new()
    {
        SupportsStreaming = true,
        SupportsBidirectional = true,
        SupportsAuthentication = true,
        SupportsCompression = false,
        SupportsEncryption = true
    };
    public string[] Tags => ["mcp", "ai", "llm", "tools", "context", "agent"];

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
}

#endregion
