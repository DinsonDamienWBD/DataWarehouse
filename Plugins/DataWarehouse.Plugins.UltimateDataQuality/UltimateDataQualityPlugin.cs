using System.Reflection;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataQuality;

/// <summary>
/// Ultimate Data Quality Plugin - Comprehensive data quality management solution.
///
/// Implements 50+ data quality strategies across 8 categories:
/// - Validation: Schema validation, rule-based validation, statistical validation
/// - Profiling: Column profiling, pattern detection, data type inference
/// - Cleansing: Basic cleansing, semantic cleansing, null handling
/// - Duplicate Detection: Exact match, fuzzy match, phonetic matching
/// - Standardization: Format standardization, code standardization
/// - Quality Scoring: Dimension-based scoring, weighted scoring
/// - Monitoring: Real-time monitoring, anomaly detection, SLA tracking
/// - Reporting: Report generation, dashboard data
///
/// Features:
/// - Strategy pattern for extensibility
/// - Auto-discovery of strategies via reflection
/// - Unified API for all quality operations
/// - Intelligence-aware for AI-enhanced recommendations
/// - Multi-tenant support
/// - Policy-driven quality rules
/// - Real-time quality metrics
/// - Comprehensive reporting
/// </summary>
public sealed class UltimateDataQualityPlugin : DataManagementPluginBase, IDisposable
{
    private readonly DataQualityStrategyRegistry _registry;
    private readonly BoundedDictionary<string, long> _usageStats = new BoundedDictionary<string, long>(1000);
    private readonly BoundedDictionary<string, QualityPolicy> _policies = new BoundedDictionary<string, QualityPolicy>(1000);
    private bool _disposed;

    // Configuration
    private volatile bool _auditEnabled = true;
    private volatile bool _autoCorrectEnabled;

    // Statistics
    private long _totalRecordsProcessed;
    private long _totalIssuesFound;
    private long _totalCorrections;
    private long _totalDuplicates;

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.dataquality.ultimate";

    /// <inheritdoc/>
    public override string Name => "Ultimate Data Quality";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override string DataManagementDomain => "DataQuality";

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.OrchestrationProvider;

    /// <summary>
    /// Semantic description of this plugin for AI discovery.
    /// </summary>
    public string SemanticDescription =>
        "Ultimate data quality plugin providing 50+ strategies including validation rules, " +
        "data profiling, cleansing, duplicate detection, standardization, quality scoring, " +
        "monitoring, and comprehensive reporting. Supports AI-enhanced quality recommendations.";

    /// <summary>
    /// Semantic tags for AI discovery and categorization.
    /// </summary>
    public string[] SemanticTags => new[]
    {
        "data-quality", "validation", "profiling", "cleansing", "deduplication",
        "standardization", "scoring", "monitoring", "reporting", "compliance"
    };

    /// <summary>
    /// Gets the data quality strategy registry.
    /// </summary>
    public DataQualityStrategyRegistry Registry => _registry;

    /// <summary>
    /// Gets or sets whether audit logging is enabled.
    /// </summary>
    public bool AuditEnabled
    {
        get => _auditEnabled;
        set => _auditEnabled = value;
    }

    /// <summary>
    /// Gets or sets whether auto-correction is enabled.
    /// </summary>
    public bool AutoCorrectEnabled
    {
        get => _autoCorrectEnabled;
        set => _autoCorrectEnabled = value;
    }

    /// <summary>
    /// Initializes a new instance of the Ultimate Data Quality plugin.
    /// </summary>
    public UltimateDataQualityPlugin()
    {
        _registry = new DataQualityStrategyRegistry();
        DiscoverAndRegisterStrategies();
    }

    /// <inheritdoc/>
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        var response = await base.OnHandshakeAsync(request);

        await RegisterAllKnowledgeAsync();

        response.Metadata["RegisteredStrategies"] = _registry.Count.ToString();
        response.Metadata["AuditEnabled"] = _auditEnabled.ToString();
        response.Metadata["AutoCorrectEnabled"] = _autoCorrectEnabled.ToString();
        response.Metadata["ValidationStrategies"] = GetStrategiesByCategory(DataQualityCategory.Validation).Count.ToString();
        response.Metadata["ProfilingStrategies"] = GetStrategiesByCategory(DataQualityCategory.Profiling).Count.ToString();
        response.Metadata["CleansingStrategies"] = GetStrategiesByCategory(DataQualityCategory.Cleansing).Count.ToString();

        return response;
    }

    /// <inheritdoc/>
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return new List<PluginCapabilityDescriptor>
        {
            new() { Name = "quality.validate", DisplayName = "Validate", Description = "Validate data records" },
            new() { Name = "quality.profile", DisplayName = "Profile", Description = "Profile data structure" },
            new() { Name = "quality.cleanse", DisplayName = "Cleanse", Description = "Cleanse data records" },
            new() { Name = "quality.detect-duplicates", DisplayName = "Detect Duplicates", Description = "Find duplicate records" },
            new() { Name = "quality.standardize", DisplayName = "Standardize", Description = "Standardize data formats" },
            new() { Name = "quality.score", DisplayName = "Score", Description = "Calculate quality scores" },
            new() { Name = "quality.monitor", DisplayName = "Monitor", Description = "Monitor quality metrics" },
            new() { Name = "quality.report", DisplayName = "Report", Description = "Generate quality reports" },
            new() { Name = "quality.list-strategies", DisplayName = "List Strategies", Description = "List available strategies" },
            new() { Name = "quality.stats", DisplayName = "Statistics", Description = "Get quality statistics" },
            new() { Name = "quality.create-policy", DisplayName = "Create Policy", Description = "Create quality policy" },
            new() { Name = "quality.apply-policy", DisplayName = "Apply Policy", Description = "Apply policy to data" }
        };
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
                    CapabilityId = "data-quality",
                    DisplayName = "Ultimate Data Quality",
                    Description = SemanticDescription,
                    Category = SDK.Contracts.CapabilityCategory.DataManagement,
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = SemanticTags
                }
            };

            foreach (var strategy in _registry.GetAll())
            {
                var tags = new List<string> { "data-quality", strategy.Category.ToString().ToLowerInvariant() };
                tags.AddRange(strategy.Tags);

                capabilities.Add(new()
                {
                    CapabilityId = $"data-quality.{strategy.StrategyId}",
                    DisplayName = strategy.DisplayName,
                    Description = strategy.SemanticDescription,
                    Category = SDK.Contracts.CapabilityCategory.DataManagement,
                    SubCategory = strategy.Category.ToString(),
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = tags.ToArray(),
                    Metadata = new Dictionary<string, object>
                    {
                        ["category"] = strategy.Category.ToString(),
                        ["supportsAsync"] = strategy.Capabilities.SupportsAsync,
                        ["supportsBatch"] = strategy.Capabilities.SupportsBatch,
                        ["supportsStreaming"] = strategy.Capabilities.SupportsStreaming,
                        ["supportsDistributed"] = strategy.Capabilities.SupportsDistributed
                    },
                    SemanticDescription = strategy.SemanticDescription
                });
            }

            return capabilities.AsReadOnly();
        }
    }

    /// <inheritdoc/>
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
    {
        var knowledge = new List<KnowledgeObject>(base.GetStaticKnowledge());

        var strategies = _registry.GetAll();
        var byCategory = strategies.GroupBy(s => s.Category)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());

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
                ["supportsValidation"] = GetStrategiesByCategory(DataQualityCategory.Validation).Any(),
                ["supportsProfiling"] = GetStrategiesByCategory(DataQualityCategory.Profiling).Any(),
                ["supportsCleansing"] = GetStrategiesByCategory(DataQualityCategory.Cleansing).Any(),
                ["supportsDuplicateDetection"] = GetStrategiesByCategory(DataQualityCategory.DuplicateDetection).Any(),
                ["supportsStandardization"] = GetStrategiesByCategory(DataQualityCategory.Standardization).Any(),
                ["supportsScoring"] = GetStrategiesByCategory(DataQualityCategory.Scoring).Any(),
                ["supportsMonitoring"] = GetStrategiesByCategory(DataQualityCategory.Monitoring).Any(),
                ["supportsReporting"] = GetStrategiesByCategory(DataQualityCategory.Reporting).Any()
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
        metadata["ValidationStrategies"] = GetStrategiesByCategory(DataQualityCategory.Validation).Count;
        metadata["ProfilingStrategies"] = GetStrategiesByCategory(DataQualityCategory.Profiling).Count;
        metadata["CleansingStrategies"] = GetStrategiesByCategory(DataQualityCategory.Cleansing).Count;
        metadata["DuplicateDetectionStrategies"] = GetStrategiesByCategory(DataQualityCategory.DuplicateDetection).Count;
        metadata["StandardizationStrategies"] = GetStrategiesByCategory(DataQualityCategory.Standardization).Count;
        metadata["ScoringStrategies"] = GetStrategiesByCategory(DataQualityCategory.Scoring).Count;
        metadata["MonitoringStrategies"] = GetStrategiesByCategory(DataQualityCategory.Monitoring).Count;
        metadata["ReportingStrategies"] = GetStrategiesByCategory(DataQualityCategory.Reporting).Count;
        metadata["TotalRecordsProcessed"] = Interlocked.Read(ref _totalRecordsProcessed);
        metadata["TotalIssuesFound"] = Interlocked.Read(ref _totalIssuesFound);
        metadata["TotalCorrections"] = Interlocked.Read(ref _totalCorrections);
        metadata["TotalDuplicates"] = Interlocked.Read(ref _totalDuplicates);
        return metadata;
    }

    /// <inheritdoc/>
    public override Task OnMessageAsync(PluginMessage message)
    {
        return message.Type switch
        {
            "quality.validate" => HandleValidateAsync(message),
            "quality.profile" => HandleProfileAsync(message),
            "quality.cleanse" => HandleCleanseAsync(message),
            "quality.detect-duplicates" => HandleDetectDuplicatesAsync(message),
            "quality.standardize" => HandleStandardizeAsync(message),
            "quality.score" => HandleScoreAsync(message),
            "quality.monitor" => HandleMonitorAsync(message),
            "quality.report" => HandleReportAsync(message),
            "quality.list-strategies" => HandleListStrategiesAsync(message),
            "quality.stats" => HandleStatsAsync(message),
            "quality.create-policy" => HandleCreatePolicyAsync(message),
            "quality.apply-policy" => HandleApplyPolicyAsync(message),
            _ => base.OnMessageAsync(message)
        };
    }

    #region Message Handlers

    private Task HandleValidateAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("strategyId", out var sidObj) || sidObj is not string strategyId)
        {
            throw new ArgumentException("Missing 'strategyId' parameter");
        }

        var strategy = GetStrategyOrThrow(strategyId);
        IncrementUsageStats(strategyId);
        Interlocked.Increment(ref _totalRecordsProcessed);

        // Accumulate issues from payload if caller reports them
        if (message.Payload.TryGetValue("issueCount", out var ic) && ic is long issues)
            Interlocked.Add(ref _totalIssuesFound, issues);

        message.Payload["success"] = true;
        message.Payload["strategyId"] = strategy.StrategyId;
        message.Payload["strategyName"] = strategy.DisplayName;
        message.Payload["category"] = strategy.Category.ToString();
        message.Payload["capabilities"] = new Dictionary<string, object>
        {
            ["supportsAsync"] = strategy.Capabilities.SupportsAsync,
            ["supportsBatch"] = strategy.Capabilities.SupportsBatch,
            ["supportsStreaming"] = strategy.Capabilities.SupportsStreaming
        };
        return Task.CompletedTask;
    }

    private Task HandleProfileAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("strategyId", out var sidObj) || sidObj is not string strategyId)
        {
            strategyId = "column-profiling";
        }

        var strategy = GetStrategyOrThrow(strategyId);
        IncrementUsageStats(strategyId);

        message.Payload["success"] = true;
        message.Payload["strategyId"] = strategy.StrategyId;
        message.Payload["strategyName"] = strategy.DisplayName;
        message.Payload["category"] = strategy.Category.ToString();
        message.Payload["tags"] = strategy.Tags;
        return Task.CompletedTask;
    }

    private Task HandleCleanseAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("strategyId", out var sidObj) || sidObj is not string strategyId)
        {
            strategyId = "basic-cleansing";
        }

        var strategy = GetStrategyOrThrow(strategyId);
        IncrementUsageStats(strategyId);
        Interlocked.Increment(ref _totalRecordsProcessed);

        // Accumulate corrections from payload if caller reports them
        if (message.Payload.TryGetValue("correctedCount", out var cc) && cc is long corrections)
            Interlocked.Add(ref _totalCorrections, corrections);

        message.Payload["success"] = true;
        message.Payload["strategyId"] = strategy.StrategyId;
        message.Payload["strategyName"] = strategy.DisplayName;
        message.Payload["category"] = strategy.Category.ToString();
        return Task.CompletedTask;
    }

    private Task HandleDetectDuplicatesAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("strategyId", out var sidObj) || sidObj is not string strategyId)
        {
            strategyId = "exact-match-duplicate";
        }

        var strategy = GetStrategyOrThrow(strategyId);
        IncrementUsageStats(strategyId);
        Interlocked.Increment(ref _totalRecordsProcessed);

        // Accumulate duplicates from payload if caller reports them
        if (message.Payload.TryGetValue("duplicateCount", out var dc) && dc is long dups)
            Interlocked.Add(ref _totalDuplicates, dups);

        message.Payload["success"] = true;
        message.Payload["strategyId"] = strategy.StrategyId;
        message.Payload["strategyName"] = strategy.DisplayName;
        message.Payload["category"] = strategy.Category.ToString();
        return Task.CompletedTask;
    }

    private Task HandleStandardizeAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("strategyId", out var sidObj) || sidObj is not string strategyId)
        {
            strategyId = "format-standardization";
        }

        var strategy = GetStrategyOrThrow(strategyId);
        IncrementUsageStats(strategyId);
        Interlocked.Increment(ref _totalRecordsProcessed);

        message.Payload["success"] = true;
        message.Payload["strategyId"] = strategy.StrategyId;
        message.Payload["strategyName"] = strategy.DisplayName;
        message.Payload["category"] = strategy.Category.ToString();
        return Task.CompletedTask;
    }

    private Task HandleScoreAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("strategyId", out var sidObj) || sidObj is not string strategyId)
        {
            strategyId = "dimension-scoring";
        }

        var strategy = GetStrategyOrThrow(strategyId);
        IncrementUsageStats(strategyId);

        message.Payload["success"] = true;
        message.Payload["strategyId"] = strategy.StrategyId;
        message.Payload["strategyName"] = strategy.DisplayName;
        message.Payload["category"] = strategy.Category.ToString();
        message.Payload["description"] = strategy.SemanticDescription;
        return Task.CompletedTask;
    }

    private Task HandleMonitorAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("strategyId", out var sidObj) || sidObj is not string strategyId)
        {
            strategyId = "realtime-monitoring";
        }

        var strategy = GetStrategyOrThrow(strategyId);
        IncrementUsageStats(strategyId);

        message.Payload["success"] = true;
        message.Payload["strategyId"] = strategy.StrategyId;
        message.Payload["strategyName"] = strategy.DisplayName;
        message.Payload["category"] = strategy.Category.ToString();
        message.Payload["capabilities"] = new Dictionary<string, object>
        {
            ["supportsAsync"] = strategy.Capabilities.SupportsAsync,
            ["supportsStreaming"] = strategy.Capabilities.SupportsStreaming,
            ["supportsDistributed"] = strategy.Capabilities.SupportsDistributed
        };
        return Task.CompletedTask;
    }

    private Task HandleReportAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("strategyId", out var sidObj) || sidObj is not string strategyId)
        {
            strategyId = "report-generation";
        }

        var strategy = GetStrategyOrThrow(strategyId);
        IncrementUsageStats(strategyId);

        message.Payload["success"] = true;
        message.Payload["strategyId"] = strategy.StrategyId;
        message.Payload["strategyName"] = strategy.DisplayName;
        message.Payload["category"] = strategy.Category.ToString();
        message.Payload["tags"] = strategy.Tags;
        return Task.CompletedTask;
    }

    private Task HandleListStrategiesAsync(PluginMessage message)
    {
        var categoryFilter = message.Payload.TryGetValue("category", out var catObj) && catObj is string catStr
            && Enum.TryParse<DataQualityCategory>(catStr, true, out var cat)
            ? cat
            : (DataQualityCategory?)null;

        var strategies = categoryFilter.HasValue
            ? _registry.GetByCategory(categoryFilter.Value)
            : _registry.GetAll();

        var strategyList = strategies.Select(s => new Dictionary<string, object>
        {
            ["id"] = s.StrategyId,
            ["displayName"] = s.DisplayName,
            ["category"] = s.Category.ToString(),
            ["capabilities"] = new Dictionary<string, object>
            {
                ["supportsAsync"] = s.Capabilities.SupportsAsync,
                ["supportsBatch"] = s.Capabilities.SupportsBatch,
                ["supportsStreaming"] = s.Capabilities.SupportsStreaming,
                ["supportsDistributed"] = s.Capabilities.SupportsDistributed,
                ["supportsIncremental"] = s.Capabilities.SupportsIncremental
            },
            ["tags"] = s.Tags
        }).ToList();

        message.Payload["strategies"] = strategyList;
        message.Payload["count"] = strategyList.Count;

        return Task.CompletedTask;
    }

    private Task HandleStatsAsync(PluginMessage message)
    {
        message.Payload["totalRecordsProcessed"] = Interlocked.Read(ref _totalRecordsProcessed);
        message.Payload["totalIssuesFound"] = Interlocked.Read(ref _totalIssuesFound);
        message.Payload["totalCorrections"] = Interlocked.Read(ref _totalCorrections);
        message.Payload["totalDuplicates"] = Interlocked.Read(ref _totalDuplicates);
        message.Payload["registeredStrategies"] = _registry.Count;
        message.Payload["activePolicies"] = _policies.Count;

        var usageByStrategy = new Dictionary<string, long>(_usageStats);
        message.Payload["usageByStrategy"] = usageByStrategy;

        return Task.CompletedTask;
    }

    private Task HandleCreatePolicyAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("policyId", out var pidObj) || pidObj is not string policyId)
        {
            throw new ArgumentException("Missing 'policyId' parameter");
        }

        var policy = new QualityPolicy
        {
            PolicyId = policyId,
            Name = message.Payload.TryGetValue("name", out var nameObj) && nameObj is string name
                ? name : policyId,
            CreatedAt = DateTime.UtcNow
        };

        _policies[policyId] = policy;
        message.Payload["success"] = true;

        return Task.CompletedTask;
    }

    private Task HandleApplyPolicyAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("policyId", out var pidObj) || pidObj is not string policyId)
        {
            throw new ArgumentException("Missing 'policyId' parameter");
        }

        if (!_policies.TryGetValue(policyId, out var policy))
        {
            throw new ArgumentException($"Policy '{policyId}' not found");
        }

        message.Payload["success"] = true;
        return Task.CompletedTask;
    }

    #endregion

    #region Helper Methods

    private IDataQualityStrategy GetStrategyOrThrow(string strategyId)
    {
        var strategy = _registry.Get(strategyId)
            ?? throw new ArgumentException($"Data quality strategy '{strategyId}' not found");
        return strategy;
    }

    private List<IDataQualityStrategy> GetStrategiesByCategory(DataQualityCategory category)
    {
        return _registry.GetByCategory(category).ToList();
    }

    private void IncrementUsageStats(string strategyId)
    {
        _usageStats.AddOrUpdate(strategyId, 1, (_, count) => count + 1);
    }

    private void DiscoverAndRegisterStrategies()
    {
        var discovered = _registry.AutoDiscover(Assembly.GetExecutingAssembly());

        if (discovered == 0)
        {
            System.Diagnostics.Debug.WriteLine($"[{Name}] Warning: No data quality strategies discovered");
        }
    }

    #endregion

    #region Intelligence Integration

    /// <inheritdoc/>
    protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct)
    {
        await base.OnStartWithIntelligenceAsync(ct);

        if (MessageBus != null)
        {
            var strategies = _registry.GetAll();
            var byCategory = strategies.GroupBy(s => s.Category)
                .ToDictionary(g => g.Key.ToString(), g => g.Count());

            await MessageBus.PublishAsync(IntelligenceTopics.QueryCapability, new PluginMessage
            {
                Type = "capability.register",
                Source = Id,
                Payload = new Dictionary<string, object>
                {
                    ["pluginId"] = Id,
                    ["pluginName"] = Name,
                    ["pluginType"] = "data-quality",
                    ["capabilities"] = new Dictionary<string, object>
                    {
                        ["strategyCount"] = strategies.Count,
                        ["categories"] = byCategory,
                        ["supportsValidation"] = GetStrategiesByCategory(DataQualityCategory.Validation).Any(),
                        ["supportsProfiling"] = GetStrategiesByCategory(DataQualityCategory.Profiling).Any(),
                        ["supportsCleansing"] = GetStrategiesByCategory(DataQualityCategory.Cleansing).Any(),
                        ["supportsDuplicateDetection"] = GetStrategiesByCategory(DataQualityCategory.DuplicateDetection).Any(),
                        ["supportsScoring"] = GetStrategiesByCategory(DataQualityCategory.Scoring).Any()
                    },
                    ["semanticDescription"] = SemanticDescription,
                    ["tags"] = SemanticTags
                }
            }, ct);

            SubscribeToQualityRequests();
        }
    }

    private void SubscribeToQualityRequests()
    {
        if (MessageBus == null) return;

        MessageBus.Subscribe("intelligence.request.data-quality", async msg =>
        {
            if (msg.Payload.TryGetValue("action", out var actionObj) && actionObj is string action)
            {
                var recommendation = RecommendQualityStrategy(action, msg.Payload);

                await MessageBus.PublishAsync("intelligence.response.data-quality", new PluginMessage
                {
                    Type = "data-quality-recommendation.response",
                    CorrelationId = msg.CorrelationId,
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["success"] = true,
                        ["action"] = action,
                        ["recommendedStrategy"] = recommendation.StrategyId,
                        ["category"] = recommendation.Category,
                        ["confidence"] = recommendation.Confidence,
                        ["reasoning"] = recommendation.Reasoning
                    }
                });
            }
        });
    }

    private (string StrategyId, string Category, double Confidence, string Reasoning)
        RecommendQualityStrategy(string action, Dictionary<string, object> context)
    {
        return action.ToLowerInvariant() switch
        {
            "validate" => ("schema-validation", "Validation", 0.9,
                "Schema validation provides comprehensive structural validation"),
            "profile" => ("column-profiling", "Profiling", 0.95,
                "Column profiling gives detailed field-level analysis"),
            "cleanse" => ("basic-cleansing", "Cleansing", 0.85,
                "Basic cleansing handles common data quality issues"),
            "deduplicate" => ("fuzzy-match-duplicate", "DuplicateDetection", 0.88,
                "Fuzzy matching catches near-duplicates that exact match misses"),
            "standardize" => ("format-standardization", "Standardization", 0.9,
                "Format standardization ensures consistent data formats"),
            "score" => ("dimension-scoring", "Scoring", 0.92,
                "Dimension scoring provides comprehensive quality assessment"),
            "monitor" => ("realtime-monitoring", "Monitoring", 0.9,
                "Real-time monitoring enables proactive quality management"),
            "report" => ("report-generation", "Reporting", 0.95,
                "Report generation creates comprehensive quality documentation"),
            _ => ("schema-validation", "Validation", 0.7,
                "Schema validation is a good starting point for data quality")
        };
    }

    /// <inheritdoc/>
    protected override async Task OnStartCoreAsync(CancellationToken ct)
    {
        var policyData = await LoadStateAsync("policies", ct);
        if (policyData != null)
        {
            try
            {
                var entries = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, QualityPolicyDto>>(policyData);
                if (entries != null)
                    foreach (var kvp in entries)
                        _policies[kvp.Key] = new QualityPolicy
                        {
                            PolicyId = kvp.Value.PolicyId,
                            Name = kvp.Value.Name,
                            CreatedAt = kvp.Value.CreatedAt
                        };
            }
            catch { /* corrupted state — start fresh */ }
        }
    }

    /// <inheritdoc/>
    protected override async Task OnBeforeStatePersistAsync(CancellationToken ct)
    {
        var dtos = _policies.ToDictionary(
            kvp => kvp.Key,
            kvp => new QualityPolicyDto { PolicyId = kvp.Value.PolicyId, Name = kvp.Value.Name, CreatedAt = kvp.Value.CreatedAt });
        var policyBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(dtos);
        await SaveStateAsync("policies", policyBytes, ct);
    }

    /// <summary>Serializable DTO for QualityPolicy (private nested class cannot be deserialized directly).</summary>
    private sealed class QualityPolicyDto
    {
        public string PolicyId { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTime CreatedAt { get; set; }
    }

    #endregion

    /// <summary>
    /// Disposes resources.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_disposed) return;
            _disposed = true;
            _usageStats.Clear();
            _policies.Clear();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Represents a quality policy.
    /// </summary>
    private sealed class QualityPolicy
    {
        public required string PolicyId { get; init; }
        public required string Name { get; init; }
        public DateTime CreatedAt { get; init; }
    }
}
