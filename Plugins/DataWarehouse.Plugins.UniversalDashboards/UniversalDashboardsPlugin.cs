using System.Reflection;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Contracts.Dashboards;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UniversalDashboards;

/// <summary>
/// Universal Dashboards Plugin - Comprehensive dashboard integration providing 40+ strategies
/// across enterprise BI, open source, cloud native, embedded analytics, real-time visualization,
/// and export capabilities.
///
/// Supported platforms:
/// - Enterprise BI: Tableau, Power BI, Qlik, Looker, SAP Analytics, IBM Cognos, MicroStrategy, Sisense
/// - Open Source: Metabase, Redash, Apache Superset, Grafana, Kibana
/// - Cloud Native: AWS QuickSight, Google Looker Studio, Azure Power BI Embedded
/// - Embedded: SDK, IFrame, API Rendering, White Label
/// - Real-time: Live Dashboards, WebSocket Updates, Streaming Visualization, Event-Driven
/// - Export: PDF Generation, Image Export, Scheduled Reports, Email Delivery
///
/// Features:
/// - Unified dashboard management across all platforms
/// - Real-time data push and streaming
/// - Dashboard provisioning and templates
/// - Authentication and connection pooling
/// - Intelligence-aware strategy selection
/// </summary>
public sealed class UniversalDashboardsPlugin : DataWarehouse.SDK.Contracts.Hierarchy.InterfacePluginBase, IDisposable
{
    private readonly BoundedDictionary<string, DashboardStrategyBase> _strategies = new BoundedDictionary<string, DashboardStrategyBase>(1000);
    private readonly BoundedDictionary<string, long> _usageStats = new BoundedDictionary<string, long>(1000);
    private readonly object _statsLock = new();
    private bool _disposed;

    // Statistics
    private long _totalDashboardsCreated;
    private long _totalDashboardsUpdated;
    private long _totalDataPushOperations;
    private long _totalRowsPushed;
    private long _totalErrors;

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.dashboards.universal";

    /// <inheritdoc/>
    public override string Name => "Universal Dashboards";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override string Protocol => "Dashboard";

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.InterfaceProvider;

    /// <summary>
    /// Gets the sub-category for this plugin.
    /// </summary>
    public string SubCategory => "Dashboards";

    /// <summary>
    /// Gets the quality level of this plugin (0-100).
    /// </summary>
    public int QualityLevel => 95;

    /// <summary>
    /// Semantic description for AI discovery.
    /// </summary>
    public string SemanticDescription =>
        "Universal dashboards plugin providing 40+ dashboard strategies across enterprise BI (Tableau, Power BI, Qlik, Looker), " +
        "open source (Metabase, Redash, Grafana, Kibana), cloud native (AWS QuickSight, Google Data Studio), " +
        "embedded analytics, real-time visualization, and export capabilities.";

    /// <summary>
    /// Semantic tags for AI discovery.
    /// </summary>
    public string[] SemanticTags => new[]
    {
        "dashboards", "visualization", "bi", "analytics", "tableau", "powerbi", "grafana",
        "real-time", "embedded", "export", "pdf", "streaming"
    };

    /// <summary>
    /// Gets the active strategy, if configured.
    /// </summary>
    public DashboardStrategyBase? ActiveStrategy { get; private set; }

    /// <summary>
    /// Initializes a new instance of the Universal Dashboards plugin.
    /// </summary>
    public UniversalDashboardsPlugin()
    {
        DiscoverAndRegisterStrategies();
    }

    /// <summary>
    /// Registers a dashboard strategy with the plugin.
    /// Registers in both the local typed dictionary and the inherited
    /// <see cref="SDK.Contracts.PluginBase.StrategyRegistry"/> (IStrategy) for unified dispatch.
    /// </summary>
    /// <param name="strategy">The strategy to register.</param>
    public void RegisterStrategy(DashboardStrategyBase strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        _strategies[strategy.StrategyId] = strategy;
        // DashboardStrategyBase : StrategyBase : IStrategy — register with inherited IStrategy registry.
        // Explicit cast to IStrategy routes to PluginBase.RegisterStrategy(IStrategy), not this overload.
        RegisterStrategy((DataWarehouse.SDK.Contracts.IStrategy)strategy);
    }

    /// <summary>
    /// Gets a strategy by ID.
    /// </summary>
    /// <param name="strategyId">The strategy ID (case-insensitive).</param>
    /// <returns>The matching strategy, or null if not found.</returns>
    public DashboardStrategyBase? GetStrategy(string strategyId)
    {
        _strategies.TryGetValue(strategyId, out var strategy);
        return strategy;
    }

    /// <summary>
    /// Gets all registered strategy IDs.
    /// </summary>
    public IReadOnlyCollection<string> GetRegisteredStrategies() => _strategies.Keys.ToArray();

    /// <summary>
    /// Gets strategies by category.
    /// </summary>
    /// <param name="category">The category to filter by.</param>
    /// <returns>Strategies in the specified category.</returns>
    public IReadOnlyList<DashboardStrategyBase> GetStrategiesByCategory(string category)
    {
        return _strategies.Values
            .Where(s => s.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Configures and activates a strategy.
    /// </summary>
    /// <param name="strategyId">The strategy ID to activate.</param>
    /// <param name="config">Connection configuration.</param>
    public void ConfigureStrategy(string strategyId, DashboardConnectionConfig config)
    {
        var strategy = GetStrategy(strategyId)
            ?? throw new ArgumentException($"Unknown strategy: {strategyId}");

        strategy.Configure(config);
        ActiveStrategy = strategy;
    }

    /// <summary>
    /// Selects the best strategy based on requirements.
    /// </summary>
    /// <param name="requirements">Dashboard requirements.</param>
    /// <returns>Recommended strategy.</returns>
    public DashboardStrategyBase SelectBestStrategy(DashboardRequirements requirements)
    {
        var candidates = _strategies.Values.AsEnumerable();

        // Filter by real-time requirement
        if (requirements.RequiresRealTime)
            candidates = candidates.Where(s => s.Capabilities.SupportsRealTime);

        // Filter by embedding requirement
        if (requirements.RequiresEmbedding)
            candidates = candidates.Where(s => s.Capabilities.SupportsEmbedding);

        // Filter by templates requirement
        if (requirements.RequiresTemplates)
            candidates = candidates.Where(s => s.Capabilities.SupportsTemplates);

        // Filter by category preference
        if (!string.IsNullOrEmpty(requirements.PreferredCategory))
            candidates = candidates.Where(s => s.Category.Equals(requirements.PreferredCategory, StringComparison.OrdinalIgnoreCase));

        // Filter by widget types
        if (requirements.RequiredWidgetTypes?.Count > 0)
            candidates = candidates.Where(s =>
                requirements.RequiredWidgetTypes.All(w => s.Capabilities.SupportsWidgetType(w)));

        var result = candidates.FirstOrDefault();
        if (result == null)
            throw new InvalidOperationException("No strategy matches the specified requirements");

        return result;
    }

    /// <summary>
    /// Creates a dashboard using the active strategy.
    /// </summary>
    public async Task<Dashboard> CreateDashboardAsync(Dashboard dashboard, CancellationToken ct = default)
    {
        EnsureActiveStrategy();
        var result = await ActiveStrategy!.CreateDashboardAsync(dashboard, ct);
        Interlocked.Increment(ref _totalDashboardsCreated);
        IncrementUsageStats(ActiveStrategy.StrategyId);
        return result;
    }

    /// <summary>
    /// Updates a dashboard using the active strategy.
    /// </summary>
    public async Task<Dashboard> UpdateDashboardAsync(Dashboard dashboard, CancellationToken ct = default)
    {
        EnsureActiveStrategy();
        var result = await ActiveStrategy!.UpdateDashboardAsync(dashboard, ct);
        Interlocked.Increment(ref _totalDashboardsUpdated);
        return result;
    }

    /// <summary>
    /// Gets a dashboard using the active strategy.
    /// </summary>
    public async Task<Dashboard> GetDashboardAsync(string dashboardId, CancellationToken ct = default)
    {
        EnsureActiveStrategy();
        return await ActiveStrategy!.GetDashboardAsync(dashboardId, ct);
    }

    /// <summary>
    /// Deletes a dashboard using the active strategy.
    /// </summary>
    public async Task DeleteDashboardAsync(string dashboardId, CancellationToken ct = default)
    {
        EnsureActiveStrategy();
        await ActiveStrategy!.DeleteDashboardAsync(dashboardId, ct);
    }

    /// <summary>
    /// Lists dashboards using the active strategy.
    /// </summary>
    public async Task<IReadOnlyList<Dashboard>> ListDashboardsAsync(DashboardFilter? filter = null, CancellationToken ct = default)
    {
        EnsureActiveStrategy();
        return await ActiveStrategy!.ListDashboardsAsync(filter, ct);
    }

    /// <summary>
    /// Pushes data to a dashboard using the active strategy.
    /// </summary>
    public async Task<DataPushResult> PushDataAsync(
        string targetId,
        IReadOnlyList<IReadOnlyDictionary<string, object>> data,
        CancellationToken ct = default)
    {
        EnsureActiveStrategy();
        var result = await ActiveStrategy!.PushDataAsync(targetId, data, ct);

        if (result.Success)
        {
            Interlocked.Increment(ref _totalDataPushOperations);
            Interlocked.Add(ref _totalRowsPushed, result.RowsPushed);
        }
        else
        {
            Interlocked.Increment(ref _totalErrors);
        }

        return result;
    }

    /// <summary>
    /// Provisions a dashboard using the active strategy.
    /// </summary>
    public async Task<Dashboard> ProvisionDashboardAsync(
        Dashboard dashboard,
        DashboardProvisionOptions? options = null,
        CancellationToken ct = default)
    {
        EnsureActiveStrategy();
        return await ActiveStrategy!.ProvisionDashboardAsync(dashboard, options, ct);
    }

    /// <summary>
    /// Tests the connection for the active strategy.
    /// </summary>
    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        EnsureActiveStrategy();
        return await ActiveStrategy!.TestConnectionAsync(ct);
    }

    /// <summary>
    /// Gets statistics for this plugin.
    /// </summary>
    public DashboardPluginStatistics GetStatistics()
    {
        return new DashboardPluginStatistics
        {
            TotalDashboardsCreated = Interlocked.Read(ref _totalDashboardsCreated),
            TotalDashboardsUpdated = Interlocked.Read(ref _totalDashboardsUpdated),
            TotalDataPushOperations = Interlocked.Read(ref _totalDataPushOperations),
            TotalRowsPushed = Interlocked.Read(ref _totalRowsPushed),
            TotalErrors = Interlocked.Read(ref _totalErrors),
            RegisteredStrategies = _strategies.Count,
            UsageByStrategy = new Dictionary<string, long>(_usageStats)
        };
    }

    #region Plugin Lifecycle

    /// <inheritdoc/>
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        var response = await base.OnHandshakeAsync(request);

        response.Metadata["RegisteredStrategies"] = _strategies.Count.ToString();
        response.Metadata["Categories"] = string.Join(", ", _strategies.Values.Select(s => s.Category).Distinct());
        response.Metadata["ActiveStrategy"] = ActiveStrategy?.StrategyId ?? "None";

        return response;
    }

    /// <inheritdoc/>
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return
        [
            new() { Name = "dashboard.create", DisplayName = "Create Dashboard", Description = "Create a new dashboard" },
            new() { Name = "dashboard.update", DisplayName = "Update Dashboard", Description = "Update an existing dashboard" },
            new() { Name = "dashboard.delete", DisplayName = "Delete Dashboard", Description = "Delete a dashboard" },
            new() { Name = "dashboard.list", DisplayName = "List Dashboards", Description = "List all dashboards" },
            new() { Name = "dashboard.push", DisplayName = "Push Data", Description = "Push data to a dashboard" },
            new() { Name = "dashboard.provision", DisplayName = "Provision", Description = "Provision from template" },
            new() { Name = "dashboard.test", DisplayName = "Test Connection", Description = "Test strategy connection" },
            new() { Name = "dashboard.export.pdf", DisplayName = "Export PDF", Description = "Export dashboard to PDF" },
            new() { Name = "dashboard.export.image", DisplayName = "Export Image", Description = "Export dashboard to image" },
            new() { Name = "dashboard.schedule", DisplayName = "Schedule Report", Description = "Schedule automated reports" }
        ];
    }

    /// <inheritdoc/>
    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities
    {
        get
        {
            var capabilities = new List<RegisteredCapability>
            {
                new()
                {
                    CapabilityId = $"{Id}.manage",
                    DisplayName = $"{Name} - Manage Dashboards",
                    Description = "Create, update, delete, and list dashboards",
                    Category = DataWarehouse.SDK.Contracts.CapabilityCategory.Interface,
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = new[] { "dashboards", "visualization", "management" }
                },
                new()
                {
                    CapabilityId = $"{Id}.realtime",
                    DisplayName = $"{Name} - Real-time Updates",
                    Description = "Push data and stream updates to dashboards",
                    Category = DataWarehouse.SDK.Contracts.CapabilityCategory.Interface,
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = new[] { "dashboards", "real-time", "streaming" }
                },
                new()
                {
                    CapabilityId = $"{Id}.export",
                    DisplayName = $"{Name} - Export",
                    Description = "Export dashboards to PDF, images, and scheduled reports",
                    Category = DataWarehouse.SDK.Contracts.CapabilityCategory.Interface,
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = new[] { "dashboards", "export", "pdf", "reports" }
                }
            };

            // Add strategy-specific capabilities
            foreach (var strategy in _strategies.Values)
            {
                var tags = new List<string> { "dashboards", "strategy", strategy.Category.ToLowerInvariant() };
                if (strategy.Capabilities.SupportsRealTime) tags.Add("real-time");
                if (strategy.Capabilities.SupportsEmbedding) tags.Add("embedding");
                if (strategy.Capabilities.SupportsTemplates) tags.Add("templates");

                capabilities.Add(new RegisteredCapability
                {
                    CapabilityId = $"{Id}.strategy.{strategy.StrategyId}",
                    DisplayName = strategy.StrategyName,
                    Description = $"{strategy.StrategyName} dashboard strategy by {strategy.VendorName}",
                    Category = DataWarehouse.SDK.Contracts.CapabilityCategory.Interface,
                    SubCategory = strategy.Category,
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = tags.ToArray(),
                    Priority = strategy.Capabilities.SupportsRealTime ? 80 : 50,
                    Metadata = new Dictionary<string, object>
                    {
                        ["strategyId"] = strategy.StrategyId,
                        ["vendor"] = strategy.VendorName,
                        ["category"] = strategy.Category,
                        ["supportsRealTime"] = strategy.Capabilities.SupportsRealTime,
                        ["supportsTemplates"] = strategy.Capabilities.SupportsTemplates,
                        ["supportsEmbedding"] = strategy.Capabilities.SupportsEmbedding,
                        ["widgetTypes"] = strategy.Capabilities.SupportedWidgetTypes
                    },
                    SemanticDescription = $"Use {strategy.StrategyName} for {strategy.Category} dashboards"
                });
            }

            return capabilities;
        }
    }

    /// <inheritdoc/>
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
    {
        var knowledge = new List<KnowledgeObject>(base.GetStaticKnowledge());

        // Strategy summary
        knowledge.Add(new KnowledgeObject
        {
            Id = $"{Id}.strategies",
            Topic = "dashboards.strategies",
            SourcePluginId = Id,
            SourcePluginName = Name,
            KnowledgeType = "capability",
            Description = $"{_strategies.Count} dashboard strategies available",
            Payload = new Dictionary<string, object>
            {
                ["count"] = _strategies.Count,
                ["categories"] = _strategies.Values.Select(s => s.Category).Distinct().ToArray(),
                ["realTimeCount"] = _strategies.Values.Count(s => s.Capabilities.SupportsRealTime),
                ["embeddingCount"] = _strategies.Values.Count(s => s.Capabilities.SupportsEmbedding),
                ["templateCount"] = _strategies.Values.Count(s => s.Capabilities.SupportsTemplates)
            },
            Tags = new[] { "dashboards", "strategies", "summary" }
        });

        // Category breakdowns
        foreach (var category in _strategies.Values.Select(s => s.Category).Distinct())
        {
            var categoryStrategies = _strategies.Values.Where(s => s.Category == category).ToList();
            knowledge.Add(new KnowledgeObject
            {
                Id = $"{Id}.category.{category.ToLowerInvariant().Replace(" ", "-")}",
                Topic = $"dashboards.{category.ToLowerInvariant().Replace(" ", "-")}",
                SourcePluginId = Id,
                SourcePluginName = Name,
                KnowledgeType = "capability",
                Description = $"{categoryStrategies.Count} {category} dashboard strategies",
                Payload = new Dictionary<string, object>
                {
                    ["category"] = category,
                    ["count"] = categoryStrategies.Count,
                    ["strategies"] = categoryStrategies.Select(s => s.StrategyId).ToArray(),
                    ["vendors"] = categoryStrategies.Select(s => s.VendorName).Distinct().ToArray()
                },
                Tags = new[] { "dashboards", category.ToLowerInvariant().Replace(" ", "-") }
            });
        }

        return knowledge;
    }

    /// <inheritdoc/>
    public override Task OnMessageAsync(PluginMessage message)
    {
        return message.Type switch
        {
            "dashboard.strategy.list" => HandleListStrategiesAsync(message),
            "dashboard.strategy.configure" => HandleConfigureStrategyAsync(message),
            "dashboard.create" => HandleCreateDashboardAsync(message),
            "dashboard.update" => HandleUpdateDashboardAsync(message),
            "dashboard.delete" => HandleDeleteDashboardAsync(message),
            "dashboard.list" => HandleListDashboardsAsync(message),
            "dashboard.push" => HandlePushDataAsync(message),
            "dashboard.stats" => HandleStatsAsync(message),
            _ => base.OnMessageAsync(message)
        };
    }

    /// <inheritdoc/>
    protected override Task OnStartCoreAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct)
    {
        await base.OnStartWithIntelligenceAsync(ct);

        if (MessageBus != null)
        {
            await MessageBus.PublishAsync(IntelligenceTopics.QueryCapability, new PluginMessage
            {
                Type = "capability.register",
                Source = Id,
                Payload = new Dictionary<string, object>
                {
                    ["pluginId"] = Id,
                    ["pluginName"] = Name,
                    ["pluginType"] = "dashboards",
                    ["capabilities"] = new Dictionary<string, object>
                    {
                        ["strategyCount"] = _strategies.Count,
                        ["categories"] = _strategies.Values.Select(s => s.Category).Distinct().ToArray(),
                        ["realTimeStrategies"] = _strategies.Values.Count(s => s.Capabilities.SupportsRealTime),
                        ["supportsDashboardRecommendation"] = true
                    },
                    ["semanticDescription"] = SemanticDescription,
                    ["tags"] = SemanticTags
                }
            }, ct);

            SubscribeToDashboardRecommendationRequests();
        }
    }

    #endregion

    #region Message Handlers

    private Task HandleListStrategiesAsync(PluginMessage message)
    {
        var strategies = _strategies.Values.Select(s => new Dictionary<string, object>
        {
            ["id"] = s.StrategyId,
            ["name"] = s.StrategyName,
            ["vendor"] = s.VendorName,
            ["category"] = s.Category,
            ["supportsRealTime"] = s.Capabilities.SupportsRealTime,
            ["supportsTemplates"] = s.Capabilities.SupportsTemplates,
            ["supportsEmbedding"] = s.Capabilities.SupportsEmbedding,
            ["widgetTypes"] = s.Capabilities.SupportedWidgetTypes
        }).ToList();

        message.Payload["strategies"] = strategies;
        message.Payload["count"] = strategies.Count;

        return Task.CompletedTask;
    }

    private Task HandleConfigureStrategyAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("strategyId", out var sidObj) || sidObj is not string strategyId)
            throw new ArgumentException("Missing 'strategyId' parameter");

        if (!message.Payload.TryGetValue("config", out var configObj) || configObj is not Dictionary<string, object> configDict)
            throw new ArgumentException("Missing 'config' parameter");

        var config = new DashboardConnectionConfig
        {
            BaseUrl = configDict.TryGetValue("baseUrl", out var url) ? url.ToString()! : "",
            AuthType = configDict.TryGetValue("authType", out var auth) && Enum.TryParse<AuthenticationType>(auth.ToString(), out var authType)
                ? authType : AuthenticationType.None,
            ApiKey = configDict.TryGetValue("apiKey", out var apiKey) ? apiKey.ToString() : null,
            Username = configDict.TryGetValue("username", out var user) ? user.ToString() : null,
            Password = configDict.TryGetValue("password", out var pass) ? pass.ToString() : null,
            BearerToken = configDict.TryGetValue("bearerToken", out var token) ? token.ToString() : null,
            OrganizationId = configDict.TryGetValue("organizationId", out var org) ? org.ToString() : null,
            ProjectId = configDict.TryGetValue("projectId", out var proj) ? proj.ToString() : null
        };

        ConfigureStrategy(strategyId, config);

        message.Payload["success"] = true;
        message.Payload["activeStrategy"] = strategyId;

        return Task.CompletedTask;
    }

    private async Task HandleCreateDashboardAsync(PluginMessage message)
    {
        EnsureActiveStrategy();

        if (!message.Payload.TryGetValue("title", out var titleObj) || titleObj is not string title)
            throw new ArgumentException("Missing 'title' parameter");

        var dashboard = Dashboard.Create(title,
            message.Payload.TryGetValue("description", out var desc) ? desc.ToString() : null);

        var result = await CreateDashboardAsync(dashboard);
        message.Payload["dashboard"] = new Dictionary<string, object>
        {
            ["id"] = result.Id!,
            ["title"] = result.Title,
            ["createdAt"] = result.CreatedAt?.ToString("O") ?? ""
        };
    }

    private async Task HandleUpdateDashboardAsync(PluginMessage message)
    {
        EnsureActiveStrategy();

        if (!message.Payload.TryGetValue("dashboardId", out var idObj) || idObj is not string dashboardId)
            throw new ArgumentException("Missing 'dashboardId' parameter");

        var existing = await GetDashboardAsync(dashboardId);

        var title = message.Payload.TryGetValue("title", out var t) ? t.ToString()! : existing.Title;
        var description = message.Payload.TryGetValue("description", out var d) ? d.ToString() : existing.Description;

        var updated = existing with { Title = title, Description = description };
        var result = await UpdateDashboardAsync(updated);

        message.Payload["dashboard"] = new Dictionary<string, object>
        {
            ["id"] = result.Id!,
            ["title"] = result.Title,
            ["version"] = result.Version,
            ["updatedAt"] = result.UpdatedAt?.ToString("O") ?? ""
        };
    }

    private async Task HandleDeleteDashboardAsync(PluginMessage message)
    {
        EnsureActiveStrategy();

        if (!message.Payload.TryGetValue("dashboardId", out var idObj) || idObj is not string dashboardId)
            throw new ArgumentException("Missing 'dashboardId' parameter");

        await DeleteDashboardAsync(dashboardId);
        message.Payload["success"] = true;
    }

    private async Task HandleListDashboardsAsync(PluginMessage message)
    {
        EnsureActiveStrategy();

        DashboardFilter? filter = null;
        if (message.Payload.TryGetValue("searchQuery", out var sq))
        {
            filter = new DashboardFilter(SearchQuery: sq.ToString());
        }

        var dashboards = await ListDashboardsAsync(filter);
        message.Payload["dashboards"] = dashboards.Select(d => new Dictionary<string, object>
        {
            ["id"] = d.Id!,
            ["title"] = d.Title,
            ["createdAt"] = d.CreatedAt?.ToString("O") ?? ""
        }).ToArray();
        message.Payload["count"] = dashboards.Count;
    }

    private async Task HandlePushDataAsync(PluginMessage message)
    {
        EnsureActiveStrategy();

        if (!message.Payload.TryGetValue("targetId", out var tidObj) || tidObj is not string targetId)
            throw new ArgumentException("Missing 'targetId' parameter");

        if (!message.Payload.TryGetValue("data", out var dataObj) ||
            dataObj is not IReadOnlyList<IReadOnlyDictionary<string, object>> data)
            throw new ArgumentException("Missing or invalid 'data' parameter");

        var result = await PushDataAsync(targetId, data);
        message.Payload["success"] = result.Success;
        message.Payload["rowsPushed"] = result.RowsPushed;
        message.Payload["durationMs"] = result.DurationMs;
        if (!result.Success && !string.IsNullOrEmpty(result.ErrorMessage))
            message.Payload["error"] = result.ErrorMessage;
    }

    private Task HandleStatsAsync(PluginMessage message)
    {
        var stats = GetStatistics();
        message.Payload["totalDashboardsCreated"] = stats.TotalDashboardsCreated;
        message.Payload["totalDashboardsUpdated"] = stats.TotalDashboardsUpdated;
        message.Payload["totalDataPushOperations"] = stats.TotalDataPushOperations;
        message.Payload["totalRowsPushed"] = stats.TotalRowsPushed;
        message.Payload["totalErrors"] = stats.TotalErrors;
        message.Payload["registeredStrategies"] = stats.RegisteredStrategies;
        message.Payload["usageByStrategy"] = stats.UsageByStrategy ?? new Dictionary<string, long>();

        return Task.CompletedTask;
    }

    #endregion

    #region Intelligence Integration

    private void SubscribeToDashboardRecommendationRequests()
    {
        if (MessageBus == null) return;

        MessageBus.Subscribe("intelligence.request.dashboard-recommendation", async msg =>
        {
            if (msg.Payload.TryGetValue("requirements", out var reqObj) && reqObj is Dictionary<string, object> reqDict)
            {
                var requirements = new DashboardRequirements
                {
                    RequiresRealTime = reqDict.TryGetValue("realTime", out var rt) && rt is true,
                    RequiresEmbedding = reqDict.TryGetValue("embedding", out var em) && em is true,
                    RequiresTemplates = reqDict.TryGetValue("templates", out var tm) && tm is true,
                    PreferredCategory = reqDict.TryGetValue("category", out var cat) ? cat.ToString() : null,
                    RequiredWidgetTypes = reqDict.TryGetValue("widgetTypes", out var wt) && wt is string[] widgets
                        ? widgets.ToList() : null
                };

                try
                {
                    var recommendation = SelectBestStrategy(requirements);

                    await MessageBus.PublishAsync("intelligence.response.dashboard-recommendation", new PluginMessage
                    {
                        Type = "dashboard-recommendation.response",
                        CorrelationId = msg.CorrelationId,
                        Source = Id,
                        Payload = new Dictionary<string, object>
                        {
                            ["success"] = true,
                            ["strategyId"] = recommendation.StrategyId,
                            ["strategyName"] = recommendation.StrategyName,
                            ["vendor"] = recommendation.VendorName,
                            ["category"] = recommendation.Category,
                            ["reasoning"] = $"{recommendation.StrategyName} selected for {recommendation.Category} dashboards"
                        }
                    });
                }
                catch (Exception ex)
                {
                    await MessageBus.PublishAsync("intelligence.response.dashboard-recommendation", new PluginMessage
                    {
                        Type = "dashboard-recommendation.response",
                        CorrelationId = msg.CorrelationId,
                        Source = Id,
                        Payload = new Dictionary<string, object>
                        {
                            ["success"] = false,
                            ["error"] = ex.Message
                        }
                    });
                }
            }
        });
    }

    #endregion

    #region Helper Methods

    private void EnsureActiveStrategy()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ActiveStrategy == null)
            throw new InvalidOperationException("No active strategy configured. Call ConfigureStrategy() first.");
    }

    private void DiscoverAndRegisterStrategies()
    {
        var strategyTypes = GetType().Assembly
            .GetTypes()
            .Where(t => !t.IsAbstract && typeof(DashboardStrategyBase).IsAssignableFrom(t));

        foreach (var strategyType in strategyTypes)
        {
            try
            {
                if (Activator.CreateInstance(strategyType) is DashboardStrategyBase strategy)
                {
                    // RegisterStrategy(DashboardStrategyBase) registers in both the local typed
                    // dictionary and the inherited PluginBase.StrategyRegistry (IStrategy).
                    RegisterStrategy(strategy);
                }
            }
            catch
            {
                // Strategy failed to instantiate, skip
            }
        }
    }

    private void IncrementUsageStats(string strategyId)
    {
        _usageStats.AddOrUpdate(strategyId, 1, (_, count) => count + 1);
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes resources.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var strategy in _strategies.Values)
            {
            strategy.Dispose();
            }
            _strategies.Clear();
            _usageStats.Clear();
        }
        base.Dispose(disposing);
    }

    #endregion
}

/// <summary>
/// Dashboard requirements for strategy selection.
/// </summary>
public sealed record DashboardRequirements
{
    /// <summary>Whether real-time updates are required.</summary>
    public bool RequiresRealTime { get; init; }

    /// <summary>Whether embedding support is required.</summary>
    public bool RequiresEmbedding { get; init; }

    /// <summary>Whether template support is required.</summary>
    public bool RequiresTemplates { get; init; }

    /// <summary>Preferred strategy category.</summary>
    public string? PreferredCategory { get; init; }

    /// <summary>Required widget types.</summary>
    public IReadOnlyList<string>? RequiredWidgetTypes { get; init; }
}

/// <summary>
/// Statistics for the dashboard plugin.
/// </summary>
public sealed class DashboardPluginStatistics
{
    /// <summary>Total dashboards created.</summary>
    public long TotalDashboardsCreated { get; init; }

    /// <summary>Total dashboards updated.</summary>
    public long TotalDashboardsUpdated { get; init; }

    /// <summary>Total data push operations.</summary>
    public long TotalDataPushOperations { get; init; }

    /// <summary>Total rows pushed.</summary>
    public long TotalRowsPushed { get; init; }

    /// <summary>Total errors.</summary>
    public long TotalErrors { get; init; }

    /// <summary>Number of registered strategies.</summary>
    public int RegisteredStrategies { get; init; }

    /// <summary>Usage count by strategy.</summary>
    public IReadOnlyDictionary<string, long>? UsageByStrategy { get; init; }
}
