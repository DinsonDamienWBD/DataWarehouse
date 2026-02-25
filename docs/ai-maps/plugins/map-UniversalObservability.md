# Plugin: UniversalObservability
> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.
> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.


## Project: DataWarehouse.Plugins.UniversalObservability

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/UniversalObservabilityPlugin.cs
```csharp
public sealed class UniversalObservabilityPlugin : ObservabilityPluginBase
{
#endregion
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override string ObservabilityDomain;;
    public override PluginCategory Category;;
    public int StrategyCount;;
    public UniversalObservabilityPlugin();
    public void RegisterStrategy(IObservabilityStrategy strategy);
    public IObservabilityStrategy? GetStrategy(string strategyId);
    public IReadOnlyCollection<string> GetRegisteredStrategies();;
    public void SetActiveMetricsStrategy(string strategyId);
    public void SetActiveLoggingStrategy(string strategyId);
    public void SetActiveTracingStrategy(string strategyId);
    public async Task RecordMetricsAsync(IEnumerable<MetricValue> metrics, CancellationToken ct = default);
    public async Task RecordLogsAsync(IEnumerable<LogEntry> logEntries, CancellationToken ct = default);
    public async Task RecordTracesAsync(IEnumerable<SpanContext> spans, CancellationToken ct = default);
    public async Task<ObservabilityHealthCheckResult> HealthCheckAsync(string strategyId, CancellationToken ct = default);
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
                Tags = new[]
                {
                    "observability",
                    "metrics",
                    "monitoring"
                }
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
                Tags = new[]
                {
                    "observability",
                    "logging",
                    "monitoring"
                }
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
                Tags = new[]
                {
                    "observability",
                    "tracing",
                    "distributed-tracing"
                }
            }
        };
        // Per AD-05 (Phase 25b): capability registration is now plugin-level responsibility.
        // Strategy.GetStrategyCapability() removed; plugin constructs capabilities from strategy metadata.
        foreach (var kvp in _strategies)
        {
            if (kvp.Value is ObservabilityStrategyBase strategy)
            {
                capabilities.Add(new RegisteredCapability { CapabilityId = $"observability.{strategy.StrategyId}", DisplayName = strategy.Name, Description = strategy.Description, Category = CapabilityCategory.Custom, SubCategory = "Observability", PluginId = Id, PluginName = Name, PluginVersion = Version, Tags = new[] { "observability", strategy.StrategyId }, SemanticDescription = strategy.Description });
            }
        }

        return capabilities;
    }
}
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge();
    public override Task OnMessageAsync(PluginMessage message);
    public string SemanticDescription;;
    public string[] SemanticTags;;
    protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct);
    protected override Task OnStartCoreAsync(CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/Alerting/AlertManagerStrategy.cs
```csharp
public sealed class AlertManagerStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public AlertManagerStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: false, SupportsTracing: false, SupportsLogging: false, SupportsDistributedTracing: false, SupportsAlerting: true, SupportedExporters: new[] { "AlertManager", "Prometheus", "Webhook" }));
    public void Configure(string url);
    public async Task PostAlertsAsync(IEnumerable<Alert> alerts, CancellationToken ct = default);
    public async Task<string> GetAlertsAsync(bool? active = null, bool? silenced = null, bool? inhibited = null, CancellationToken ct = default);
    public async Task<string> CreateSilenceAsync(string createdBy, string comment, Dictionary<string, string> matchers, DateTime startsAt, DateTime endsAt, CancellationToken ct = default);
    public async Task<string> GetStatusAsync(CancellationToken ct = default);
    protected override Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken ct);;
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken ct);;
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken ct);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
    public class Alert;
}
```
```csharp
public class Alert
{
}
    public string AlertName { get; set; };
    public string Severity { get; set; };
    public Dictionary<string, string> Labels { get; set; };
    public Dictionary<string, string>? Annotations { get; set; }
    public DateTime? StartsAt { get; set; }
    public DateTime? EndsAt { get; set; }
    public string? GeneratorUrl { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/Alerting/OpsGenieStrategy.cs
```csharp
public sealed class OpsGenieStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public OpsGenieStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: false, SupportsTracing: false, SupportsLogging: false, SupportsDistributedTracing: false, SupportsAlerting: true, SupportedExporters: new[] { "OpsGenie", "AlertAPI", "IncidentAPI" }));
    public void Configure(string apiKey, string region = "us");
    public async Task CreateAlertAsync(string message, string priority = "P3", string? alias = null, string? description = null, Dictionary<string, string>? details = null, string[]? tags = null, CancellationToken ct = default);
    public async Task AcknowledgeAlertAsync(string alias, string? note = null, CancellationToken ct = default);
    public async Task CloseAlertAsync(string alias, string? note = null, CancellationToken ct = default);
    public async Task AddNoteAsync(string alias, string note, CancellationToken ct = default);
    protected override Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken ct);;
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken ct);;
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken ct);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/Alerting/PagerDutyStrategy.cs
```csharp
public sealed class PagerDutyStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public PagerDutyStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: false, SupportsTracing: false, SupportsLogging: false, SupportsDistributedTracing: false, SupportsAlerting: true, SupportedExporters: new[] { "PagerDuty", "EventsAPI", "ChangeEvents" }));
    public void Configure(string routingKey, string apiToken = "");
    public async Task TriggerAlertAsync(string summary, string severity, string source, Dictionary<string, object>? customDetails = null, string? dedupKey = null, CancellationToken ct = default);
    public async Task AcknowledgeAlertAsync(string dedupKey, CancellationToken ct = default);
    public async Task ResolveAlertAsync(string dedupKey, CancellationToken ct = default);
    protected override Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken ct);;
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken ct);;
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken ct);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/Alerting/SensuStrategy.cs
```csharp
public sealed class SensuStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public SensuStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: true, SupportsTracing: false, SupportsLogging: false, SupportsDistributedTracing: false, SupportsAlerting: true, SupportedExporters: new[] { "Sensu", "InfluxDB", "Prometheus" }));
    public void Configure(string apiUrl, string apiKey, string sensuNamespace = "default");
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    public async Task SendCheckAsync(string checkName, int status, string output, CancellationToken ct = default);
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/Alerting/VictorOpsStrategy.cs
```csharp
public sealed class VictorOpsStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public VictorOpsStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: false, SupportsTracing: false, SupportsLogging: false, SupportsDistributedTracing: false, SupportsAlerting: true, SupportedExporters: new[] { "VictorOps", "SplunkOnCall" }));
    public void Configure(string apiKey, string routingKey = "datawarehouse");
    protected override Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    public async Task SendAlertAsync(string messageType, string entityId, string stateMessage, Dictionary<string, object>? additionalData = null, CancellationToken ct = default);
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/APM/AppDynamicsStrategy.cs
```csharp
public sealed class AppDynamicsStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public AppDynamicsStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: true, SupportsTracing: true, SupportsLogging: false, SupportsDistributedTracing: true, SupportsAlerting: true, SupportedExporters: new[] { "AppDynamics", "AppDynamicsAgent", "BRUM" }));
    public void Configure(string controllerUrl, string accountName, string apiClientName, string apiClientSecret, string applicationName = "datawarehouse");
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override async Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken ct);;
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken ct);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/APM/DynatraceStrategy.cs
```csharp
public sealed class DynatraceStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public DynatraceStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: true, SupportsTracing: true, SupportsLogging: true, SupportsDistributedTracing: true, SupportsAlerting: true, SupportedExporters: new[] { "Dynatrace", "OneAgent", "ActiveGate" }));
    public void Configure(string environmentUrl, string apiToken, string entityId = "");
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override async Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken ct);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/APM/ElasticApmStrategy.cs
```csharp
public sealed class ElasticApmStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public ElasticApmStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: true, SupportsTracing: true, SupportsLogging: false, SupportsDistributedTracing: true, SupportsAlerting: true, SupportedExporters: new[] { "ElasticAPM", "Elasticsearch" }));
    public void Configure(string serverUrl, string secretToken, string serviceName = "datawarehouse");
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override async Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/APM/InstanaStrategy.cs
```csharp
public sealed class InstanaStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public InstanaStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: true, SupportsTracing: true, SupportsLogging: false, SupportsDistributedTracing: true, SupportsAlerting: true, SupportedExporters: new[] { "Instana", "OpenTelemetry" }));
    public void Configure(string endpoint, string agentKey);
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override async Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/APM/NewRelicStrategy.cs
```csharp
public sealed class NewRelicStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public NewRelicStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: true, SupportsTracing: true, SupportsLogging: true, SupportsDistributedTracing: true, SupportsAlerting: true, SupportedExporters: new[] { "NewRelic", "OTLP", "NewRelicMetrics" }));
    public void Configure(string licenseKey, string accountId, string region = "US", string serviceName = "datawarehouse");
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override async Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken ct);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/ErrorTracking/AirbrakeStrategy.cs
```csharp
public sealed class AirbrakeStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public AirbrakeStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: true, SupportsTracing: false, SupportsLogging: true, SupportsDistributedTracing: false, SupportsAlerting: true, SupportedExporters: new[] { "Airbrake" }));
    public void Configure(string projectId, string projectKey, string environment = "production", string host = "https://api.airbrake.io");
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    public async Task NotifyAsync(Exception exception, Dictionary<string, object>? context = null, CancellationToken ct = default);
    protected override Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/ErrorTracking/BugsnagStrategy.cs
```csharp
public sealed class BugsnagStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public BugsnagStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: false, SupportsTracing: false, SupportsLogging: true, SupportsDistributedTracing: false, SupportsAlerting: true, SupportedExporters: new[] { "Bugsnag" }));
    public void Configure(string apiKey, string releaseStage = "production", string appVersion = "");
    protected override Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    public async Task NotifyExceptionAsync(Exception exception, string severity = "error", Dictionary<string, object>? metadata = null, CancellationToken ct = default);
    protected override Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/ErrorTracking/RollbarStrategy.cs
```csharp
public sealed class RollbarStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public RollbarStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: false, SupportsTracing: false, SupportsLogging: true, SupportsDistributedTracing: false, SupportsAlerting: true, SupportedExporters: new[] { "Rollbar" }));
    public void Configure(string accessToken, string environment = "production", string codeVersion = "");
    protected override Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    public async Task ReportExceptionAsync(Exception exception, string level = "error", Dictionary<string, object>? customData = null, CancellationToken ct = default);
    public async Task ReportMessageAsync(string message, string level = "info", Dictionary<string, object>? customData = null, CancellationToken ct = default);
    protected override Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/ErrorTracking/SentryStrategy.cs
```csharp
public sealed class SentryStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public SentryStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: true, SupportsTracing: true, SupportsLogging: true, SupportsDistributedTracing: true, SupportsAlerting: true, SupportedExporters: new[] { "Sentry" }));
    public void Configure(string dsn, string environment = "production", string release = "");
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override async Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    public async Task CaptureExceptionAsync(Exception exception, Dictionary<string, object>? additionalData = null, CancellationToken ct = default);
    protected override Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/Health/ConsulHealthStrategy.cs
```csharp
public sealed class ConsulHealthStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public ConsulHealthStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: false, SupportsTracing: false, SupportsLogging: false, SupportsDistributedTracing: false, SupportsAlerting: true, SupportedExporters: new[] { "Consul", "ConsulHealth", "ServiceMesh" }));
    public void Configure(string consulUrl, string token = "", string serviceId = "datawarehouse");
    public async Task RegisterServiceAsync(string serviceName, int port, string[]? tags = null, int ttlSeconds = 30, CancellationToken ct = default);
    public async Task DeregisterServiceAsync(CancellationToken ct = default);
    public async Task PassTtlCheckAsync(string? note = null, CancellationToken ct = default);
    public async Task WarnTtlCheckAsync(string? note = null, CancellationToken ct = default);
    public async Task FailTtlCheckAsync(string? note = null, CancellationToken ct = default);
    public async Task<string> GetServiceHealthAsync(string serviceName, bool passingOnly = false, CancellationToken ct = default);
    public async Task<string> GetHealthChecksByStateAsync(string state = "any", CancellationToken ct = default);
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken ct);;
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken ct);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/Health/IcingaStrategy.cs
```csharp
public sealed class IcingaStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public IcingaStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: true, SupportsTracing: false, SupportsLogging: false, SupportsDistributedTracing: false, SupportsAlerting: true, SupportedExporters: new[] { "Icinga", "Graphite", "InfluxDB" }));
    public void Configure(string apiUrl, string username, string password, bool verifySsl = true);
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    public async Task SubmitServiceCheckAsync(string serviceName, int exitStatus, string pluginOutput, string[]? performanceData = null, CancellationToken ct = default);
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/Health/KubernetesProbesStrategy.cs
```csharp
public sealed class KubernetesProbesStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public KubernetesProbesStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: false, SupportsTracing: false, SupportsLogging: false, SupportsDistributedTracing: false, SupportsAlerting: false, SupportedExporters: new[] { "Kubernetes", "K8sProbes", "HTTP" }));
    public void Configure(string prefix = "http://+:8080/");
    public void StartProbeServer();
    public void StopProbeServer();
    public void SetLive(bool isLive);;
    public void SetReady(bool isReady);;
    public void SetStarted(bool isStarted);;
    public void RegisterHealthCheck(string name, bool isHealthy, string message = "");
    public void RemoveHealthCheck(string name);;
    public void SetMetadata(string key, string value);;
    protected override Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken ct);
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken ct);;
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    protected override Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken ct);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```
```csharp
private class HealthCheck
{
}
    public bool IsHealthy { get; set; }
    public string Message { get; set; };
    public DateTime LastCheck { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/Health/NagiosStrategy.cs
```csharp
public sealed class NagiosStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public NagiosStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: true, SupportsTracing: false, SupportsLogging: false, SupportsDistributedTracing: false, SupportsAlerting: true, SupportedExporters: new[] { "Nagios", "NSCA", "NRPE", "CGI" }));
    public void Configure(string nagiosUrl, string username, string password, string hostname = "");
    public async Task SendPassiveCheckAsync(string service, NagiosStatus status, string output, string? performanceData = null, CancellationToken ct = default);
    public async Task SendHostCheckAsync(NagiosStatus status, string output, CancellationToken ct = default);
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken ct);;
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken ct);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
    public enum NagiosStatus;
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/Health/ZabbixStrategy.cs
```csharp
public sealed class ZabbixStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public ZabbixStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: true, SupportsTracing: false, SupportsLogging: false, SupportsDistributedTracing: false, SupportsAlerting: true, SupportedExporters: new[] { "Zabbix", "ZabbixSender", "ZabbixAPI" }));
    public void Configure(string apiUrl, string username = "", string password = "", string apiToken = "", string hostname = "");
    public async Task SendItemValuesAsync(IEnumerable<(string Key, string Value)> items, CancellationToken ct = default);
    public async Task<string> GetHostsAsync(CancellationToken ct = default);
    public async Task<string> GetProblemsAsync(string? severity = null, CancellationToken ct = default);
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken ct);;
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken ct);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/Logging/ElasticsearchStrategy.cs
```csharp
public sealed class ElasticsearchStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public ElasticsearchStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: false, SupportsTracing: false, SupportsLogging: true, SupportsDistributedTracing: false, SupportsAlerting: true, SupportedExporters: new[] { "Elasticsearch", "Logstash", "Kibana" }));
    public void Configure(string url, string indexPrefix = "datawarehouse-logs", string username = "", string password = "");
    public void ConfigureWithApiKey(string url, string apiKey, string indexPrefix = "datawarehouse-logs");
    protected override async Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    public async Task<string> SearchAsync(object query, int size = 100, CancellationToken ct = default);
    public Task<string> SearchByMessageAsync(string messageText, DateTimeOffset? startTime = null, DateTimeOffset? endTime = null, int size = 100, CancellationToken ct = default);
    public async Task<string> GetLogCountByLevelAsync(DateTimeOffset startTime, DateTimeOffset endTime, CancellationToken ct = default);
    public async Task CreateIndexTemplateAsync(CancellationToken ct = default);
    protected override Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/Logging/FluentdStrategy.cs
```csharp
public sealed class FluentdStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public FluentdStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: false, SupportsTracing: false, SupportsLogging: true, SupportsDistributedTracing: false, SupportsAlerting: false, SupportedExporters: new[] { "Fluentd", "FluentBit", "Forward" }));
    public void Configure(string url, string tag = "datawarehouse");
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    protected override Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);;
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);;
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/Logging/GraylogStrategy.cs
```csharp
public sealed class GraylogStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public GraylogStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: false, SupportsTracing: false, SupportsLogging: true, SupportsDistributedTracing: false, SupportsAlerting: true, SupportedExporters: new[] { "GELF", "GraylogHTTP", "GraylogUDP" }));
    public void Configure(string host, int port = 12201, bool useUdp = false, string facility = "datawarehouse");
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    protected override Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken ct);;
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken ct);;
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken ct);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/Logging/LogglyStrategy.cs
```csharp
public sealed class LogglyStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public LogglyStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: false, SupportsTracing: false, SupportsLogging: true, SupportsDistributedTracing: false, SupportsAlerting: true, SupportedExporters: new[] { "Loggly", "HTTP" }));
    public void Configure(string token, string tag = "datawarehouse");
    protected override Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    public async Task SendLogAsync(string message, LogLevel level, Dictionary<string, object>? additionalData = null, CancellationToken ct = default);
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/Logging/LokiStrategy.cs
```csharp
public sealed class LokiStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public LokiStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: false, SupportsTracing: false, SupportsLogging: true, SupportsDistributedTracing: false, SupportsAlerting: true, SupportedExporters: new[] { "Loki", "LogQL", "Promtail" }));
    public void Configure(string url, string tenant = "", Dictionary<string, string>? staticLabels = null);
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    public async Task<string> QueryAsync(string query, DateTimeOffset start, DateTimeOffset end, int limit = 1000, CancellationToken ct = default);
    public Task<string> QueryByLabelAsync(string labelSelector, string? filter = null, TimeSpan? lookback = null, int limit = 1000, CancellationToken ct = default);
    public async Task<string[]> GetLabelNamesAsync(CancellationToken ct = default);
    protected override Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/Logging/PapertrailStrategy.cs
```csharp
public sealed class PapertrailStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public PapertrailStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: false, SupportsTracing: false, SupportsLogging: true, SupportsDistributedTracing: false, SupportsAlerting: true, SupportedExporters: new[] { "Syslog", "Papertrail" }));
    public void Configure(string host, int port, string programName = "datawarehouse");
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    protected override Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/Logging/SplunkStrategy.cs
```csharp
public sealed class SplunkStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public SplunkStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: true, SupportsTracing: false, SupportsLogging: true, SupportsDistributedTracing: false, SupportsAlerting: true, SupportedExporters: new[] { "SplunkHEC", "SplunkForwarder", "SplunkAPI" }));
    public void Configure(string hecUrl, string hecToken, string index = "main", string source = "datawarehouse", string sourcetype = "_json");
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    public async Task SendRawAsync(string rawData, CancellationToken ct = default);
    public async Task<string> GetHecHealthAsync(CancellationToken ct = default);
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/Logging/SumoLogicStrategy.cs
```csharp
public sealed class SumoLogicStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public SumoLogicStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: true, SupportsTracing: false, SupportsLogging: true, SupportsDistributedTracing: false, SupportsAlerting: true, SupportedExporters: new[] { "SumoLogic", "HTTP" }));
    public void Configure(string collectorUrl, string sourceName = "datawarehouse", string? sourceHost = null);
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    public async Task SendEventAsync(string message, string category = "application", Dictionary<string, object>? additionalFields = null, CancellationToken ct = default);
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/Metrics/AzureMonitorStrategy.cs
```csharp
public sealed class AzureMonitorStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public AzureMonitorStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: true, SupportsTracing: true, SupportsLogging: true, SupportsDistributedTracing: true, SupportsAlerting: true, SupportedExporters: new[] { "AzureMonitor", "LogAnalytics", "ApplicationInsights" }));
    public void Configure(string workspaceId, string sharedKey, string instrumentationKey = "", string logType = "DataWarehouse");
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override async Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/Metrics/CloudWatchStrategy.cs
```csharp
public sealed class CloudWatchStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public CloudWatchStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: true, SupportsTracing: false, SupportsLogging: true, SupportsDistributedTracing: false, SupportsAlerting: true, SupportedExporters: new[] { "CloudWatch", "CloudWatchLogs", "CloudWatchAlarms" }));
    public void Configure(string region, string accessKeyId, string secretAccessKey, string metricsNamespace = "DataWarehouse", string logGroupName = "/datawarehouse/application");
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/Metrics/DatadogStrategy.cs
```csharp
public sealed class DatadogStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public DatadogStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: true, SupportsTracing: true, SupportsLogging: true, SupportsDistributedTracing: true, SupportsAlerting: true, SupportedExporters: new[] { "Datadog", "DogStatsD", "DatadogAPM" }));
    public void Configure(string apiKey, string site = "datadoghq.com", string service = "datawarehouse");
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override async Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/Metrics/GraphiteStrategy.cs
```csharp
public sealed class GraphiteStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public GraphiteStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: true, SupportsTracing: false, SupportsLogging: false, SupportsDistributedTracing: false, SupportsAlerting: false, SupportedExporters: new[] { "Graphite", "Carbon", "Whisper" }));
    public void Configure(string host, int port = 2003, string prefix = "datawarehouse");
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/Metrics/InfluxDbStrategy.cs
```csharp
public sealed class InfluxDbStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public InfluxDbStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: true, SupportsTracing: false, SupportsLogging: false, SupportsDistributedTracing: false, SupportsAlerting: true, SupportedExporters: new[] { "InfluxDB", "LineProtocol", "Flux" }));
    public void Configure(string url, string token, string org = "datawarehouse", string bucket = "metrics");
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    public async Task<string> QueryAsync(string fluxQuery, CancellationToken ct = default);
    public Task<string> GetLastValuesAsync(string metricName, int count = 10, CancellationToken ct = default);
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/Metrics/PrometheusStrategy.cs
```csharp
public sealed class PrometheusStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public PrometheusStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: true, SupportsTracing: false, SupportsLogging: false, SupportsDistributedTracing: false, SupportsAlerting: true, SupportedExporters: new[] { "Prometheus", "OpenMetrics", "PushGateway" }));
    public void Configure(string url, string jobName = "datawarehouse");
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    public string GetMetricsText();
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/Metrics/StackdriverStrategy.cs
```csharp
public sealed class StackdriverStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public StackdriverStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: true, SupportsTracing: true, SupportsLogging: true, SupportsDistributedTracing: true, SupportsAlerting: true, SupportedExporters: new[] { "CloudMonitoring", "CloudLogging", "CloudTrace" }));
    public void Configure(string projectId, string accessToken, string metricPrefix = "custom.googleapis.com/datawarehouse");
    protected override async Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override async Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/Metrics/StatsDStrategy.cs
```csharp
public sealed class StatsDStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public StatsDStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: true, SupportsTracing: false, SupportsLogging: false, SupportsDistributedTracing: false, SupportsAlerting: false, SupportedExporters: new[] { "StatsD", "DogStatsD", "Telegraf" }));
    public void Configure(string host, int port = 8125, string prefix = "", double sampleRate = 1.0);
    protected override Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    public void Increment(string name, IReadOnlyList<MetricLabel>? tags = null);
    public void Decrement(string name, IReadOnlyList<MetricLabel>? tags = null);
    public void Gauge(string name, double value, IReadOnlyList<MetricLabel>? tags = null);
    public void Timing(string name, double milliseconds, IReadOnlyList<MetricLabel>? tags = null);
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    protected override Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/Metrics/TelegrafStrategy.cs
```csharp
public sealed class TelegrafStrategy : ObservabilityStrategyBase
{
}
    public enum OutputFormat;
    public override string StrategyId;;
    public override string Name;;
    public TelegrafStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: true, SupportsTracing: false, SupportsLogging: false, SupportsDistributedTracing: false, SupportsAlerting: false, SupportedExporters: new[] { "Telegraf", "InfluxLineProtocol", "JSON" }));
    public void Configure(string url, string database = "telegraf", OutputFormat format = OutputFormat.InfluxLineProtocol);
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/Metrics/VictoriaMetricsStrategy.cs
```csharp
public sealed class VictoriaMetricsStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public VictoriaMetricsStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: true, SupportsTracing: false, SupportsLogging: false, SupportsDistributedTracing: false, SupportsAlerting: true, SupportedExporters: new[] { "VictoriaMetrics", "Prometheus", "InfluxDB", "Graphite", "OpenTSDB" }));
    public void Configure(string url, string tenant = "0:0");
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    public async Task ImportInfluxLineProtocolAsync(IEnumerable<MetricValue> metrics, CancellationToken ct = default);
    public async Task<string> QueryAsync(string query, DateTimeOffset? time = null, CancellationToken ct = default);
    public async Task<string> QueryRangeAsync(string query, DateTimeOffset start, DateTimeOffset end, TimeSpan step, CancellationToken ct = default);
    public async Task<string[]> GetMetricNamesAsync(CancellationToken ct = default);
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/Profiling/DatadogProfilerStrategy.cs
```csharp
public sealed class DatadogProfilerStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public DatadogProfilerStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: true, SupportsTracing: false, SupportsLogging: false, SupportsDistributedTracing: false, SupportsAlerting: false, SupportedExporters: new[] { "Datadog", "Pprof" }));
    public void Configure(string apiKey, string site = "datadoghq.com", string serviceName = "datawarehouse", string environment = "production");
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    public async Task UploadPprofAsync(byte[] profileData, string profileType, CancellationToken ct = default);
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/Profiling/PprofStrategy.cs
```csharp
public sealed class PprofStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public PprofStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: true, SupportsTracing: false, SupportsLogging: false, SupportsDistributedTracing: false, SupportsAlerting: false, SupportedExporters: new[] { "Pprof", "File" }));
    public void Configure(string endpoint, string outputDirectory = "./profiles");
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    public async Task<byte[]?> CollectCpuProfileAsync(int durationSeconds = 30, CancellationToken ct = default);
    public async Task<byte[]?> CollectHeapProfileAsync(CancellationToken ct = default);
    public async Task<byte[]?> CollectGoroutineProfileAsync(CancellationToken ct = default);
    public async Task<byte[]?> CollectAllocProfileAsync(CancellationToken ct = default);
    public async Task<byte[]?> CollectBlockProfileAsync(CancellationToken ct = default);
    public async Task<byte[]?> CollectMutexProfileAsync(CancellationToken ct = default);
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/Profiling/PyroscopeStrategy.cs
```csharp
public sealed class PyroscopeStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public PyroscopeStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: true, SupportsTracing: false, SupportsLogging: false, SupportsDistributedTracing: false, SupportsAlerting: false, SupportedExporters: new[] { "Pyroscope", "Pprof" }));
    public void Configure(string serverUrl, string applicationName = "datawarehouse");
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    public async Task UploadCpuProfileAsync(byte[] profileData, string profileType = "cpu", CancellationToken ct = default);
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/RealUserMonitoring/AmplitudeStrategy.cs
```csharp
public sealed class AmplitudeStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public AmplitudeStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: true, SupportsTracing: false, SupportsLogging: false, SupportsDistributedTracing: false, SupportsAlerting: false, SupportedExporters: new[] { "Amplitude" }));
    public void Configure(string apiKey, string userId = "system", string? deviceId = null);
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    public async Task TrackEventAsync(string eventType, Dictionary<string, object>? eventProperties = null, Dictionary<string, object>? userProperties = null, CancellationToken ct = default);
    public async Task IdentifyUserAsync(string userId, Dictionary<string, object> userProperties, CancellationToken ct = default);
    protected override Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/RealUserMonitoring/GoogleAnalyticsStrategy.cs
```csharp
public sealed class GoogleAnalyticsStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public GoogleAnalyticsStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: true, SupportsTracing: false, SupportsLogging: false, SupportsDistributedTracing: false, SupportsAlerting: false, SupportedExporters: new[] { "GoogleAnalytics", "GA4" }));
    public void Configure(string measurementId, string apiSecret, string? clientId = null);
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    public async Task TrackEventAsync(string eventName, Dictionary<string, object>? parameters = null, CancellationToken ct = default);
    public async Task TrackPageViewAsync(string pageTitle, string pageLocation, CancellationToken ct = default);
    protected override Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/RealUserMonitoring/MixpanelStrategy.cs
```csharp
public sealed class MixpanelStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public MixpanelStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: true, SupportsTracing: false, SupportsLogging: false, SupportsDistributedTracing: false, SupportsAlerting: false, SupportedExporters: new[] { "Mixpanel" }));
    public void Configure(string projectToken, string? distinctId = null);
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    public async Task TrackAsync(string eventName, Dictionary<string, object>? properties = null, CancellationToken ct = default);
    public async Task SetProfileAsync(string distinctId, Dictionary<string, object> properties, CancellationToken ct = default);
    public async Task IncrementProfilePropertyAsync(string distinctId, string property, double value = 1, CancellationToken ct = default);
    protected override Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/RealUserMonitoring/RumEnhancedStrategies.cs
```csharp
public sealed class SessionTrackingService
{
}
    public SessionTrackingService(TimeSpan? sessionTimeout = null, int maxSessionsPerUser = 10);
    public UserSession StartOrResumeSession(string userId, string? deviceId = null, Dictionary<string, object>? properties = null);
    public void RecordEvent(string sessionId, string eventName, Dictionary<string, object>? eventData = null);
    public void IdentifyUser(string userId, string? email = null, string? name = null, Dictionary<string, object>? traits = null);
    public SessionSummary? EndSession(string sessionId);
    public int ActiveSessionCount;;
    public IReadOnlyList<UserSession> GetUserSessions(string userId);;
    public int CleanupExpiredSessions();
}
```
```csharp
public sealed class FunnelAnalysisEngine
{
}
    public FunnelDefinition DefineFunnel(string funnelId, string name, string[] steps, TimeSpan? completionWindow = null);
    public FunnelStepResult RecordStep(string funnelId, string userId, string stepName);
    public FunnelMetrics GetMetrics(string funnelId);
    public IReadOnlyList<FunnelDefinition> GetAllFunnels();;
}
```
```csharp
public sealed record UserSession
{
}
    public required string SessionId { get; init; }
    public required string UserId { get; init; }
    public string? DeviceId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset LastActivityAt { get; init; }
    public int EventCount { get; init; }
    public Dictionary<string, object> Properties { get; init; };
}
```
```csharp
public sealed record SessionEvent
{
}
    public required string SessionId { get; init; }
    public required string EventName { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public Dictionary<string, object> Data { get; init; };
}
```
```csharp
public sealed record UserIdentity
{
}
    public required string UserId { get; init; }
    public string? Email { get; init; }
    public string? Name { get; init; }
    public Dictionary<string, object> Traits { get; init; };
    public DateTimeOffset IdentifiedAt { get; init; }
}
```
```csharp
public sealed record SessionSummary
{
}
    public required string SessionId { get; init; }
    public required string UserId { get; init; }
    public TimeSpan Duration { get; init; }
    public int EventCount { get; init; }
    public int UniqueEvents { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset EndedAt { get; init; }
}
```
```csharp
public sealed record FunnelDefinition
{
}
    public required string FunnelId { get; init; }
    public required string Name { get; init; }
    public required string[] Steps { get; init; }
    public TimeSpan CompletionWindow { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
```
```csharp
public sealed record FunnelProgress
{
}
    public required string FunnelId { get; init; }
    public required string UserId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public HashSet<string> CompletedSteps { get; init; };
}
```
```csharp
public sealed record FunnelStepResult
{
}
    public bool Recorded { get; init; }
    public string? Reason { get; init; }
    public int StepIndex { get; init; }
    public int TotalSteps { get; init; }
    public int CompletedSteps { get; init; }
    public bool IsComplete { get; init; }
}
```
```csharp
public sealed record FunnelMetrics
{
}
    public required string FunnelId { get; init; }
    public string? FunnelName { get; init; }
    public int TotalEntries { get; init; }
    public int Completions { get; init; }
    public double ConversionRate { get; init; }
    public TimeSpan AverageCompletionTime { get; init; }
    public List<StepConversion> StepConversions { get; init; };
}
```
```csharp
public sealed record StepConversion
{
}
    public required string StepName { get; init; }
    public int StepIndex { get; init; }
    public int Count { get; init; }
    public double Rate { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/ResourceMonitoring/ContainerResourceStrategy.cs
```csharp
public sealed class ContainerResourceStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public ContainerResourceStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: true, SupportsTracing: false, SupportsLogging: true, SupportsDistributedTracing: false, SupportsAlerting: true, SupportedExporters: new[] { "Container", "Docker", "Kubernetes", "Cgroup" }));
    public void Configure(string? cgroupPath = null, string? kubeletUrl = null, string? podName = null, string? @namespace = null);
    public async Task<IReadOnlyList<MetricValue>> CollectContainerMetricsAsync(CancellationToken ct = default);
    public async Task<ContainerResourceLimits> GetResourceLimitsAsync(CancellationToken ct = default);
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```
```csharp
public class ContainerResourceLimits
{
}
    public double CpuLimitCores { get; set; };
    public long MemoryLimitBytes { get; set; };
    public long MemorySwapLimitBytes { get; set; };
    public long PidsLimit { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/ResourceMonitoring/SystemResourceStrategy.cs
```csharp
public sealed class SystemResourceStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public SystemResourceStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: true, SupportsTracing: false, SupportsLogging: true, SupportsDistributedTracing: false, SupportsAlerting: true, SupportedExporters: new[] { "SystemResources", "ProcessMetrics", "RuntimeMetrics" }));
    public void Configure(int collectionIntervalMs = 5000);
    public void SetThreshold(string metricName, double warningThreshold, double criticalThreshold);
    public void StartCollection();
    public void StopCollection();
    public IReadOnlyList<MetricValue> CollectCurrentMetrics();
    public ResourceUtilizationSummary GetResourceSummary();
    public IReadOnlyList<ThresholdViolation> CheckThresholds();
    protected override Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    protected override Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```
```csharp
public class ResourceUtilizationSummary
{
}
    public DateTimeOffset Timestamp { get; init; }
    public long WorkingSetBytes { get; init; }
    public long PrivateMemoryBytes { get; init; }
    public long GcHeapSizeBytes { get; init; }
    public long GcMemoryLoadBytes { get; init; }
    public long TotalAvailableMemoryBytes { get; init; }
    public double MemoryUtilizationPercent { get; init; }
    public double ThreadPoolUtilizationPercent { get; init; }
    public int HandleCount { get; init; }
    public int ThreadCount { get; init; }
    public int ProcessorCount { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/ServiceMesh/EnvoyProxyStrategy.cs
```csharp
public sealed class EnvoyProxyStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public EnvoyProxyStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: true, SupportsTracing: true, SupportsLogging: true, SupportsDistributedTracing: true, SupportsAlerting: true, SupportedExporters: new[] { "Envoy", "EnvoyProxy", "xDS" }));
    public void Configure(string adminUrl, string clusterName = "datawarehouse");
    public async Task<IReadOnlyList<MetricValue>> CollectMetricsAsync(CancellationToken ct = default);
    public async Task<IReadOnlyList<ClusterHealth>> GetClustersHealthAsync(CancellationToken ct = default);
    public async Task<IReadOnlyList<ListenerInfo>> GetListenersAsync(CancellationToken ct = default);
    public async Task<ServerInfo> GetServerInfoAsync(CancellationToken ct = default);
    public async Task<bool> DrainListenersAsync(CancellationToken ct = default);
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```
```csharp
public class ClusterHealth
{
}
    public string Name { get; set; };
    public List<HostHealth> Hosts { get; };
    public List<CircuitBreakerConfig> CircuitBreakers { get; };
}
```
```csharp
public class HostHealth
{
}
    public string Address { get; set; };
    public int Port { get; set; }
    public bool IsHealthy { get; set; }
    public string EdsHealthStatus { get; set; };
    public bool FailedActiveHealthCheck { get; set; }
    public bool FailedOutlierCheck { get; set; }
    public long ConnectionsTotal { get; set; }
    public long ConnectionsActive { get; set; }
    public long RequestsTotal { get; set; }
    public long RequestsSuccess { get; set; }
    public long RequestsError { get; set; }
}
```
```csharp
public class CircuitBreakerConfig
{
}
    public string Priority { get; set; };
    public int MaxConnections { get; set; }
    public int MaxPendingRequests { get; set; }
    public int MaxRequests { get; set; }
    public int MaxRetries { get; set; }
}
```
```csharp
public class ListenerInfo
{
}
    public string Name { get; set; };
    public string Address { get; set; };
    public int Port { get; set; }
}
```
```csharp
public class ServerInfo
{
}
    public string Version { get; set; };
    public string State { get; set; };
    public long UptimeSeconds { get; set; }
    public string HotRestartVersion { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/ServiceMesh/IstioStrategy.cs
```csharp
public sealed class IstioStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public IstioStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: true, SupportsTracing: true, SupportsLogging: true, SupportsDistributedTracing: true, SupportsAlerting: false, SupportedExporters: new[] { "Istio", "Envoy", "ServiceMesh", "Kiali" }));
    public void Configure(string? pilotUrl = null, string? kialiUrl = null, string? serviceName = null, string? @namespace = null);
    public async Task<IReadOnlyList<MetricValue>> CollectEnvoyMetricsAsync(CancellationToken ct = default);
    public async Task<MtlsStatus> GetMtlsStatusAsync(CancellationToken ct = default);
    public async Task<ServiceTrafficInfo> GetServiceTrafficAsync(CancellationToken ct = default);
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```
```csharp
public class MtlsStatus
{
}
    public string ServiceName { get; set; };
    public string Namespace { get; set; };
    public bool MtlsEnabled { get; set; }
    public string? SpiffeId { get; set; }
    public string? CertificateValidFrom { get; set; }
    public string? CertificateExpiration { get; set; }
    public bool PolicyConfigured { get; set; }
}
```
```csharp
public class ServiceTrafficInfo
{
}
    public string ServiceName { get; set; };
    public string Namespace { get; set; };
    public double HttpRequestsPerSecond { get; set; }
    public double ErrorRatePercent { get; set; }
    public double AverageResponseTimeMs { get; set; }
}
```
```csharp
public class TrafficManagementConfig
{
}
    public string ServiceName { get; set; };
    public string Namespace { get; set; };
    public bool HasVirtualService { get; set; }
    public string? VirtualServiceName { get; set; }
    public bool HasDestinationRule { get; set; }
    public string? DestinationRuleName { get; set; }
    public bool HasCircuitBreaker { get; set; }
    public bool HasOutlierDetection { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/ServiceMesh/LinkerdStrategy.cs
```csharp
public sealed class LinkerdStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public LinkerdStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: true, SupportsTracing: true, SupportsLogging: true, SupportsDistributedTracing: true, SupportsAlerting: false, SupportedExporters: new[] { "Linkerd", "LinkerdViz", "ServiceMesh" }));
    public void Configure(string? vizUrl = null, string? serviceName = null, string? @namespace = null);
    public async Task<IReadOnlyList<MetricValue>> CollectProxyMetricsAsync(CancellationToken ct = default);
    public async Task<LinkerdServiceStats> GetServiceStatsAsync(CancellationToken ct = default);
    public async Task<IReadOnlyList<TrafficSplit>> GetTrafficSplitsAsync(CancellationToken ct = default);
    public async Task<IReadOnlyList<ServiceEdge>> GetServiceEdgesAsync(CancellationToken ct = default);
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```
```csharp
public class LinkerdServiceStats
{
}
    public string ServiceName { get; set; };
    public string Namespace { get; set; };
    public long SuccessCount { get; set; }
    public long FailureCount { get; set; }
    public double LatencyP50Ms { get; set; }
    public double LatencyP95Ms { get; set; }
    public double LatencyP99Ms { get; set; }
    public long TcpOpenConnections { get; set; }
}
```
```csharp
public class TrafficSplit
{
}
    public string Name { get; set; };
    public string Service { get; set; };
    public Dictionary<string, int> Backends { get; };
}
```
```csharp
public class ServiceProfile
{
}
    public string ServiceName { get; set; };
    public string Namespace { get; set; };
    public List<RouteInfo> Routes { get; };
    public double RetryRatio { get; set; }
    public int MinRetriesPerSecond { get; set; }
    public string? RetryTtl { get; set; }
}
```
```csharp
public class RouteInfo
{
}
    public string Name { get; set; };
    public string? PathRegex { get; set; }
    public bool IsRetryable { get; set; }
    public string? Timeout { get; set; }
}
```
```csharp
public class ServiceEdge
{
}
    public string SourceService { get; set; };
    public string SourceNamespace { get; set; };
    public string DestinationService { get; set; };
    public string DestinationNamespace { get; set; };
    public string? ClientId { get; set; }
    public string? ServerId { get; set; }
    public string? NoTlsReason { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/SyntheticMonitoring/PingdomStrategy.cs
```csharp
public sealed class PingdomStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public PingdomStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: true, SupportsTracing: false, SupportsLogging: false, SupportsDistributedTracing: false, SupportsAlerting: true, SupportedExporters: new[] { "Pingdom" }));
    public void Configure(string apiToken);
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    public async Task<int?> CreateCheckAsync(string name, string host, string type = "http", CancellationToken ct = default);
    public async Task<Dictionary<string, object>?> GetCheckResultsAsync(int checkId, CancellationToken ct = default);
    public async Task<List<Dictionary<string, object>>?> ListChecksAsync(CancellationToken ct = default);
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/SyntheticMonitoring/StatusCakeStrategy.cs
```csharp
public sealed class StatusCakeStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public StatusCakeStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: true, SupportsTracing: false, SupportsLogging: false, SupportsDistributedTracing: false, SupportsAlerting: true, SupportedExporters: new[] { "StatusCake" }));
    public void Configure(string apiKey);
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    public async Task<string?> CreateUptimeTestAsync(string name, string websiteUrl, string testType = "HTTP", int checkRate = 300, CancellationToken ct = default);
    public async Task<List<Dictionary<string, object>>?> GetUptimeTestsAsync(CancellationToken ct = default);
    public async Task<Dictionary<string, object>?> GetUptimeTestHistoryAsync(string testId, CancellationToken ct = default);
    public async Task<string?> CreatePageSpeedTestAsync(string name, string websiteUrl, int checkRate = 3600, CancellationToken ct = default);
    public async Task<bool> DeleteUptimeTestAsync(string testId, CancellationToken ct = default);
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/SyntheticMonitoring/SyntheticEnhancedStrategies.cs
```csharp
public sealed class SslCertificateMonitorService
{
}
    public SslCertificateMonitorService(int expirationWarningDays = 30);
    public async Task<SslCertificateInfo> CheckCertificateAsync(string host, CancellationToken ct = default);
    public IReadOnlyList<SslCertificateInfo> GetAllCertificates();;
    public IReadOnlyList<SslCertificateInfo> GetExpiringCertificates();;
    public IReadOnlyList<SslAlert> GetAlerts(string host);;
}
```
```csharp
public sealed class AlertTriggerEngine
{
}
    public AlertRule CreateRule(string ruleId, string name, AlertCondition condition, AlertAction action);
    public IReadOnlyList<AlertEvent> Evaluate(Dictionary<string, double> metrics);
    public IReadOnlyList<AlertEvent> GetAlertHistory(string ruleId);;
    public IReadOnlyList<string> GetActiveAlerts();;
    public bool SetRuleEnabled(string ruleId, bool enabled);
    public bool DeleteRule(string ruleId);;
}
```
```csharp
public sealed class ResponseTimeMeasurementService
{
}
    public void Record(string targetId, double responseTimeMs);
    public ResponseTimeStats GetStats(string targetId);
}
```
```csharp
public sealed record SslCertificateInfo
{
}
    public required string Host { get; init; }
    public string? Subject { get; init; }
    public string? Issuer { get; init; }
    public string? Thumbprint { get; init; }
    public DateTimeOffset? IssuedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public int DaysUntilExpiry { get; init; }
    public bool IsValid { get; init; }
    public string? PolicyErrors { get; init; }
    public int ChainLength { get; init; }
    public string? Protocol { get; init; }
    public DateTimeOffset CheckedAt { get; init; }
    public string? Error { get; init; }
}
```
```csharp
public sealed record SslAlert
{
}
    public required string Host { get; init; }
    public SslAlertSeverity Severity { get; init; }
    public required string Message { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
```
```csharp
public sealed record AlertRule
{
}
    public required string RuleId { get; init; }
    public required string Name { get; init; }
    public required AlertCondition Condition { get; init; }
    public required AlertAction Action { get; init; }
    public bool IsEnabled { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
```
```csharp
public sealed record AlertCondition
{
}
    public required string MetricName { get; init; }
    public ComparisonOperator Operator { get; init; }
    public double Threshold { get; init; }
    public TimeSpan? Duration { get; init; }
}
```
```csharp
public sealed record AlertAction
{
}
    public AlertSeverity Severity { get; init; }
    public string? NotificationChannel { get; init; }
    public string? WebhookUrl { get; init; }
    public Dictionary<string, string> Metadata { get; init; };
}
```
```csharp
public sealed record AlertEvent
{
}
    public required string RuleId { get; init; }
    public required string RuleName { get; init; }
    public required string MetricName { get; init; }
    public double MetricValue { get; init; }
    public double Threshold { get; init; }
    public AlertSeverity Severity { get; init; }
    public DateTimeOffset FiredAt { get; init; }
    public AlertEventType Type { get; init; }
}
```
```csharp
public sealed record AlertState
{
}
    public bool IsTriggered { get; init; }
    public DateTimeOffset? LastTriggeredAt { get; init; }
    public DateTimeOffset? LastResolvedAt { get; init; }
}
```
```csharp
public sealed record ResponseTimeStats
{
}
    public required string TargetId { get; init; }
    public int SampleCount { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public double Average { get; init; }
    public double Median { get; init; }
    public double P90 { get; init; }
    public double P95 { get; init; }
    public double P99 { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/SyntheticMonitoring/UptimeRobotStrategy.cs
```csharp
public sealed class UptimeRobotStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public UptimeRobotStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: true, SupportsTracing: false, SupportsLogging: false, SupportsDistributedTracing: false, SupportsAlerting: true, SupportedExporters: new[] { "UptimeRobot" }));
    public void Configure(string apiKey);
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    public async Task<int?> CreateMonitorAsync(string friendlyName, string url, int type = 1, CancellationToken ct = default);
    public async Task<List<Dictionary<string, object>>?> GetMonitorsAsync(CancellationToken ct = default);
    public async Task<Dictionary<string, object>?> GetAccountDetailsAsync(CancellationToken ct = default);
    public async Task<bool> DeleteMonitorAsync(int monitorId, CancellationToken ct = default);
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/Tracing/JaegerStrategy.cs
```csharp
public sealed class JaegerStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public JaegerStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: false, SupportsTracing: true, SupportsLogging: false, SupportsDistributedTracing: true, SupportsAlerting: false, SupportedExporters: new[] { "Jaeger", "Thrift", "OTLP" }));
    public void Configure(string collectorUrl, string serviceName = "datawarehouse");
    protected override async Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    public async Task<string> GetServicesAsync(CancellationToken ct = default);
    public async Task<string> FindTracesAsync(string service, string operation = "", int limit = 20, CancellationToken ct = default);
    protected override Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken ct);;
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken ct);;
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken ct);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```
```csharp
internal static class DateTimeOffsetExtensions
{
}
    public static long ToUnixTimeMicroseconds(this DateTimeOffset dto);;
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/Tracing/OpenTelemetryStrategy.cs
```csharp
public sealed class OpenTelemetryStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public OpenTelemetryStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: true, SupportsTracing: true, SupportsLogging: true, SupportsDistributedTracing: true, SupportsAlerting: false, SupportedExporters: new[] { "OTLP", "OTLPHttp", "OTLPGrpc" }));
    public void Configure(string otlpEndpoint, string serviceName = "datawarehouse", string serviceVersion = "1.0.0", Dictionary<string, string>? resourceAttributes = null);
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);
    protected override async Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken ct);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/Tracing/XRayStrategy.cs
```csharp
public sealed class XRayStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public XRayStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: false, SupportsTracing: true, SupportsLogging: false, SupportsDistributedTracing: true, SupportsAlerting: false, SupportedExporters: new[] { "XRay", "XRayDaemon", "OTLPXRay" }));
    public void Configure(string region, string accessKeyId, string secretAccessKey, string serviceName = "datawarehouse");
    protected override async Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    protected override Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken ct);;
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken ct);;
    protected override Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken ct);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalObservability/Strategies/Tracing/ZipkinStrategy.cs
```csharp
public sealed class ZipkinStrategy : ObservabilityStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public ZipkinStrategy() : base(new ObservabilityCapabilities(SupportsMetrics: false, SupportsTracing: true, SupportsLogging: false, SupportsDistributedTracing: true, SupportsAlerting: false, SupportedExporters: new[] { "Zipkin", "ZipkinV2", "B3" }));
    public void Configure(string url, string serviceName = "datawarehouse");
    protected override async Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);
    public async Task<string> GetServicesAsync(CancellationToken ct = default);
    public async Task<string> GetTraceAsync(string traceId, CancellationToken ct = default);
    protected override Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken ct);;
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken ct);;
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken ct);
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override void Dispose(bool disposing);
}
```
