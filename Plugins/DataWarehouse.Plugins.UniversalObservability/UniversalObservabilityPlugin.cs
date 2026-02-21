using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Contracts.Observability;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using ObservabilityHealthCheckResult = DataWarehouse.SDK.Contracts.Observability.HealthCheckResult;
using CapabilityCategory = DataWarehouse.SDK.Contracts.CapabilityCategory;

namespace DataWarehouse.Plugins.UniversalObservability;

/// <summary>
/// Universal Observability plugin (T100) that consolidates 50+ observability strategies.
/// Provides comprehensive monitoring, logging, tracing, APM, alerting, and health checking
/// capabilities with automatic backend selection and Intelligence-driven optimization.
/// </summary>
/// <remarks>
/// Supported observability categories:
/// <list type="bullet">
///   <item>Metrics: Prometheus, Datadog, CloudWatch, Azure Monitor, Stackdriver, InfluxDB, Graphite, StatsD, Telegraf, Victoria Metrics</item>
///   <item>Logging: ELK Stack, Splunk, Graylog, Loki, Fluentd, Logstash, Papertrail, Loggly, Sumo Logic</item>
///   <item>Tracing: Jaeger, Zipkin, OpenTelemetry, AWS X-Ray, Azure App Insights, Honeycomb, Lightstep, Tempo</item>
///   <item>APM: New Relic, Dynatrace, AppDynamics, Instana, Elastic APM</item>
///   <item>Alerting: PagerDuty, OpsGenie, VictorOps, AlertManager, Sensu</item>
///   <item>Health: Nagios, Zabbix, Icinga, Consul Health, Kubernetes Probes</item>
/// </list>
/// </remarks>
public sealed class UniversalObservabilityPlugin : ObservabilityPluginBase
{
    private readonly BoundedDictionary<string, IObservabilityStrategy> _strategies = new BoundedDictionary<string, IObservabilityStrategy>(1000);
    private IObservabilityStrategy? _activeMetricsStrategy;
    private IObservabilityStrategy? _activeLoggingStrategy;
    private IObservabilityStrategy? _activeTracingStrategy;

    // Statistics tracking
    private long _totalMetricsRecorded;
    private long _totalLogsRecorded;
    private long _totalSpansRecorded;
    private long _totalHealthChecks;

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.observability.universal";

    /// <inheritdoc/>
    public override string Name => "Universal Observability";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override string ObservabilityDomain => "Universal";

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.MetricsProvider;

    /// <summary>
    /// Gets the number of registered strategies.
    /// </summary>
    public int StrategyCount => _strategies.Count;

    /// <summary>
    /// Initializes the plugin and discovers observability strategies.
    /// </summary>
    public UniversalObservabilityPlugin()
    {
        DiscoverAndRegisterStrategies();
    }

    /// <summary>
    /// Registers an observability strategy with the plugin.
    /// Registers in both the local typed dictionary and the inherited
    /// <see cref="ObservabilityPluginBase.ObservabilityStrategyRegistry"/> for unified dispatch.
    /// </summary>
    /// <param name="strategy">The strategy to register.</param>
    public void RegisterStrategy(IObservabilityStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        if (strategy is ObservabilityStrategyBase baseStrategy)
        {
            _strategies[baseStrategy.StrategyId] = strategy;
            // Also register with inherited ObservabilityStrategyRegistry for unified dispatch
            RegisterObservabilityStrategy(strategy);
        }
    }

    /// <summary>
    /// Gets a strategy by identifier.
    /// </summary>
    /// <param name="strategyId">The strategy identifier (case-insensitive).</param>
    /// <returns>The matching strategy, or null if not found.</returns>
    public IObservabilityStrategy? GetStrategy(string strategyId)
    {
        _strategies.TryGetValue(strategyId, out var strategy);
        return strategy;
    }

    /// <summary>
    /// Gets all registered strategy identifiers.
    /// </summary>
    public IReadOnlyCollection<string> GetRegisteredStrategies() => _strategies.Keys.ToArray();

    /// <summary>
    /// Sets the active metrics strategy.
    /// </summary>
    /// <param name="strategyId">The strategy identifier.</param>
    public void SetActiveMetricsStrategy(string strategyId)
    {
        if (!_strategies.TryGetValue(strategyId, out var strategy))
            throw new ArgumentException($"Unknown strategy: {strategyId}");
        if (!strategy.Capabilities.SupportsMetrics)
            throw new ArgumentException($"Strategy {strategyId} does not support metrics");
        _activeMetricsStrategy = strategy;
    }

    /// <summary>
    /// Sets the active logging strategy.
    /// </summary>
    /// <param name="strategyId">The strategy identifier.</param>
    public void SetActiveLoggingStrategy(string strategyId)
    {
        if (!_strategies.TryGetValue(strategyId, out var strategy))
            throw new ArgumentException($"Unknown strategy: {strategyId}");
        if (!strategy.Capabilities.SupportsLogging)
            throw new ArgumentException($"Strategy {strategyId} does not support logging");
        _activeLoggingStrategy = strategy;
    }

    /// <summary>
    /// Sets the active tracing strategy.
    /// </summary>
    /// <param name="strategyId">The strategy identifier.</param>
    public void SetActiveTracingStrategy(string strategyId)
    {
        if (!_strategies.TryGetValue(strategyId, out var strategy))
            throw new ArgumentException($"Unknown strategy: {strategyId}");
        if (!strategy.Capabilities.SupportsTracing)
            throw new ArgumentException($"Strategy {strategyId} does not support tracing");
        _activeTracingStrategy = strategy;
    }

    /// <summary>
    /// Records metrics using the active metrics strategy.
    /// </summary>
    /// <param name="metrics">The metrics to record.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RecordMetricsAsync(IEnumerable<MetricValue> metrics, CancellationToken ct = default)
    {
        var strategy = _activeMetricsStrategy ?? SelectBestMetricsStrategy();
        if (strategy == null)
            throw new InvalidOperationException("No metrics strategy available");

        await strategy.MetricsAsync(metrics, ct);
        Interlocked.Add(ref _totalMetricsRecorded, metrics.Count());
    }

    /// <summary>
    /// Records log entries using the active logging strategy.
    /// </summary>
    /// <param name="logEntries">The log entries to record.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RecordLogsAsync(IEnumerable<LogEntry> logEntries, CancellationToken ct = default)
    {
        var strategy = _activeLoggingStrategy ?? SelectBestLoggingStrategy();
        if (strategy == null)
            throw new InvalidOperationException("No logging strategy available");

        await strategy.LoggingAsync(logEntries, ct);
        Interlocked.Add(ref _totalLogsRecorded, logEntries.Count());
    }

    /// <summary>
    /// Records trace spans using the active tracing strategy.
    /// </summary>
    /// <param name="spans">The spans to record.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RecordTracesAsync(IEnumerable<SpanContext> spans, CancellationToken ct = default)
    {
        var strategy = _activeTracingStrategy ?? SelectBestTracingStrategy();
        if (strategy == null)
            throw new InvalidOperationException("No tracing strategy available");

        await strategy.TracingAsync(spans, ct);
        Interlocked.Add(ref _totalSpansRecorded, spans.Count());
    }

    /// <summary>
    /// Performs a health check on the specified strategy.
    /// </summary>
    /// <param name="strategyId">The strategy identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Health check result.</returns>
    public async Task<ObservabilityHealthCheckResult> HealthCheckAsync(string strategyId, CancellationToken ct = default)
    {
        if (!_strategies.TryGetValue(strategyId, out var strategy))
            throw new ArgumentException($"Unknown strategy: {strategyId}");

        Interlocked.Increment(ref _totalHealthChecks);
        return await strategy.HealthCheckAsync(ct);
    }

    /// <summary>
    /// Selects the best metrics strategy based on environment and requirements.
    /// </summary>
    private IObservabilityStrategy? SelectBestMetricsStrategy()
    {
        // Prefer OpenTelemetry for standard compliance, then Prometheus for pull-based
        return GetStrategy("opentelemetry") ??
               GetStrategy("prometheus") ??
               _strategies.Values.FirstOrDefault(s => s.Capabilities.SupportsMetrics);
    }

    /// <summary>
    /// Selects the best logging strategy based on environment and requirements.
    /// </summary>
    private IObservabilityStrategy? SelectBestLoggingStrategy()
    {
        // Prefer OpenTelemetry for unified telemetry, then ELK for powerful search
        return GetStrategy("opentelemetry") ??
               GetStrategy("elasticsearch") ??
               _strategies.Values.FirstOrDefault(s => s.Capabilities.SupportsLogging);
    }

    /// <summary>
    /// Selects the best tracing strategy based on environment and requirements.
    /// </summary>
    private IObservabilityStrategy? SelectBestTracingStrategy()
    {
        // Prefer OpenTelemetry for standard compliance, then Jaeger
        return GetStrategy("opentelemetry") ??
               GetStrategy("jaeger") ??
               _strategies.Values.FirstOrDefault(s => s.Capabilities.SupportsTracing);
    }

    /// <summary>
    /// Discovers and registers all observability strategies via reflection.
    /// Strategies are registered in both the local typed dictionary and the inherited
    /// <see cref="ObservabilityPluginBase.ObservabilityStrategyRegistry"/> for unified dispatch.
    /// </summary>
    private void DiscoverAndRegisterStrategies()
    {
        var strategyTypes = GetType().Assembly
            .GetTypes()
            .Where(t => !t.IsAbstract && typeof(ObservabilityStrategyBase).IsAssignableFrom(t));

        foreach (var strategyType in strategyTypes)
        {
            try
            {
                if (Activator.CreateInstance(strategyType) is ObservabilityStrategyBase strategy)
                {
                    _strategies[strategy.StrategyId] = strategy;
                    // Also register with inherited ObservabilityStrategyRegistry for unified dispatch
                    RegisterObservabilityStrategy(strategy);
                }
            }
            catch
            {
                // Strategy failed to instantiate, skip
            }
        }
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
                    CapabilityId = $"{Id}.metrics",
                    DisplayName = $"{Name} - Metrics",
                    Description = "Record and export metrics to various backends",
                    Category = CapabilityCategory.Observability,
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = new[] { "observability", "metrics", "monitoring" }
                },
                new()
                {
                    CapabilityId = $"{Id}.logging",
                    DisplayName = $"{Name} - Logging",
                    Description = "Record and export structured logs to various backends",
                    Category = CapabilityCategory.Observability,
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = new[] { "observability", "logging", "monitoring" }
                },
                new()
                {
                    CapabilityId = $"{Id}.tracing",
                    DisplayName = $"{Name} - Tracing",
                    Description = "Record and export distributed traces to various backends",
                    Category = CapabilityCategory.Observability,
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = new[] { "observability", "tracing", "distributed-tracing" }
                }
            };

            // Per AD-05 (Phase 25b): capability registration is now plugin-level responsibility.
            // Strategy.GetStrategyCapability() removed; plugin constructs capabilities from strategy metadata.
            foreach (var kvp in _strategies)
            {
                if (kvp.Value is ObservabilityStrategyBase strategy)
                {
                    capabilities.Add(new RegisteredCapability
                    {
                        CapabilityId = $"observability.{strategy.StrategyId}",
                        DisplayName = strategy.Name,
                        Description = strategy.Description,
                        Category = CapabilityCategory.Custom,
                        SubCategory = "Observability",
                        PluginId = Id,
                        PluginName = Name,
                        PluginVersion = Version,
                        Tags = new[] { "observability", strategy.StrategyId },
                        SemanticDescription = strategy.Description
                    });
                }
            }

            return capabilities;
        }
    }

    /// <inheritdoc/>
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
    {
        var knowledge = new List<KnowledgeObject>(base.GetStaticKnowledge())
        {
            new()
            {
                Id = $"{Id}.strategies",
                Topic = "observability.strategies",
                SourcePluginId = Id,
                SourcePluginName = Name,
                KnowledgeType = "capability",
                Description = $"{_strategies.Count} observability strategies available",
                Payload = new Dictionary<string, object>
                {
                    ["count"] = _strategies.Count,
                    ["metricsStrategies"] = _strategies.Values.Count(s => s.Capabilities.SupportsMetrics),
                    ["loggingStrategies"] = _strategies.Values.Count(s => s.Capabilities.SupportsLogging),
                    ["tracingStrategies"] = _strategies.Values.Count(s => s.Capabilities.SupportsTracing),
                    ["alertingStrategies"] = _strategies.Values.Count(s => s.Capabilities.SupportsAlerting)
                },
                Tags = new[] { "observability", "strategies", "summary" }
            }
        };

        return knowledge;
    }

    /// <inheritdoc/>
    public override Task OnMessageAsync(PluginMessage message)
    {
        switch (message.Type)
        {
            case "observability.universal.list":
                // Return list of available strategies
                break;

            case "observability.universal.select.metrics":
                if (message.Payload.TryGetValue("strategyId", out var mId) && mId is string metricsId)
                    SetActiveMetricsStrategy(metricsId);
                break;

            case "observability.universal.select.logging":
                if (message.Payload.TryGetValue("strategyId", out var lId) && lId is string loggingId)
                    SetActiveLoggingStrategy(loggingId);
                break;

            case "observability.universal.select.tracing":
                if (message.Payload.TryGetValue("strategyId", out var tId) && tId is string tracingId)
                    SetActiveTracingStrategy(tracingId);
                break;
        }

        return base.OnMessageAsync(message);
    }

    #region Intelligence Integration

    /// <summary>
    /// Semantic description for AI discovery.
    /// </summary>
    public string SemanticDescription =>
        "Universal observability plugin providing 50+ monitoring strategies. " +
        "Supports metrics (Prometheus, Datadog, CloudWatch), logging (ELK, Splunk, Loki), " +
        "tracing (Jaeger, Zipkin, OpenTelemetry), APM (New Relic, Dynatrace), " +
        "alerting (PagerDuty, OpsGenie), and health monitoring (Nagios, Zabbix).";

    /// <summary>
    /// Semantic tags for AI discovery.
    /// </summary>
    public string[] SemanticTags => new[]
    {
        "observability", "metrics", "logging", "tracing", "apm", "alerting",
        "monitoring", "prometheus", "datadog", "jaeger", "opentelemetry"
    };

    /// <summary>
    /// Called when Intelligence becomes available - register observability capabilities.
    /// </summary>
    protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct)
    {
        await base.OnStartWithIntelligenceAsync(ct);

        if (MessageBus != null)
        {
            var strategies = _strategies.Values.ToList();

            await MessageBus.PublishAsync(IntelligenceTopics.QueryCapability, new PluginMessage
            {
                Type = "capability.register",
                Source = Id,
                Payload = new Dictionary<string, object>
                {
                    ["pluginId"] = Id,
                    ["pluginName"] = Name,
                    ["pluginType"] = "observability",
                    ["capabilities"] = new Dictionary<string, object>
                    {
                        ["strategyCount"] = strategies.Count,
                        ["metricsCount"] = strategies.Count(s => s.Capabilities.SupportsMetrics),
                        ["loggingCount"] = strategies.Count(s => s.Capabilities.SupportsLogging),
                        ["tracingCount"] = strategies.Count(s => s.Capabilities.SupportsTracing),
                        ["alertingCount"] = strategies.Count(s => s.Capabilities.SupportsAlerting),
                        ["supportsObservabilityRecommendation"] = true
                    },
                    ["semanticDescription"] = SemanticDescription,
                    ["tags"] = SemanticTags
                }
            }, ct);

            SubscribeToObservabilityRecommendationRequests();
        }
    }

    /// <summary>
    /// Subscribes to Intelligence observability recommendation requests.
    /// </summary>
    private void SubscribeToObservabilityRecommendationRequests()
    {
        if (MessageBus == null) return;

        MessageBus.Subscribe("intelligence.request.observability-recommendation", async msg =>
        {
            if (msg.Payload.TryGetValue("environment", out var envObj) && envObj is string environment &&
                msg.Payload.TryGetValue("requirements", out var reqObj) && reqObj is Dictionary<string, object> requirements)
            {
                var recommendation = RecommendObservabilityStack(environment, requirements);

                await MessageBus.PublishAsync("intelligence.response.observability-recommendation", new PluginMessage
                {
                    Type = "observability-recommendation.response",
                    CorrelationId = msg.CorrelationId,
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["success"] = true,
                        ["metricsStrategy"] = recommendation.MetricsStrategy,
                        ["loggingStrategy"] = recommendation.LoggingStrategy,
                        ["tracingStrategy"] = recommendation.TracingStrategy,
                        ["alertingStrategy"] = recommendation.AlertingStrategy,
                        ["reasoning"] = recommendation.Reasoning
                    }
                });
            }
        });
    }

    /// <summary>
    /// Recommends an observability stack based on environment and requirements.
    /// </summary>
    private (string MetricsStrategy, string LoggingStrategy, string TracingStrategy, string AlertingStrategy, string Reasoning)
        RecommendObservabilityStack(string environment, Dictionary<string, object> requirements)
    {
        var isCloud = requirements.TryGetValue("cloud", out var cloudObj) && cloudObj is string cloud;
        var preferOtel = requirements.TryGetValue("preferOpenTelemetry", out var otelObj) && otelObj is true;

        // Cloud-specific recommendations
        if (isCloud && requirements["cloud"] is string cloudProvider)
        {
            return cloudProvider.ToLowerInvariant() switch
            {
                "aws" => ("cloudwatch", "cloudwatch-logs", "xray", "sns-alerting",
                    "AWS-native stack recommended for seamless integration and cost optimization"),
                "azure" => ("azure-monitor", "azure-logs", "app-insights", "azure-alerting",
                    "Azure-native stack recommended for best Azure integration"),
                "gcp" => ("stackdriver", "stackdriver-logging", "cloud-trace", "gcp-alerting",
                    "GCP-native stack recommended for Google Cloud integration"),
                _ => GetDefaultStack(preferOtel)
            };
        }

        return GetDefaultStack(preferOtel);
    }

    private (string, string, string, string, string) GetDefaultStack(bool preferOtel)
    {
        if (preferOtel)
        {
            return ("opentelemetry", "opentelemetry", "opentelemetry", "alertmanager",
                "OpenTelemetry recommended for vendor-neutral observability with standardized APIs");
        }

        return ("prometheus", "elasticsearch", "jaeger", "alertmanager",
            "Industry-standard open-source stack recommended for flexibility and community support");
    }

    /// <inheritdoc/>
    protected override Task OnStartCoreAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    #endregion
}
