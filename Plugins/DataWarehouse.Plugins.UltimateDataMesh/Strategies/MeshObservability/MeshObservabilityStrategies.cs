using DataWarehouse.SDK.Contracts.DataMesh;

namespace DataWarehouse.Plugins.UltimateDataMesh.Strategies.MeshObservability;

/// <summary>
/// Mesh Metrics Strategy - Comprehensive mesh-wide metrics.
/// Implements T113.7: Data mesh observability.
/// </summary>
public sealed class MeshMetricsStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "mesh-metrics";
    public override string DisplayName => "Mesh Metrics";
    public override DataMeshCategory Category => DataMeshCategory.MeshObservability;
    public override DataMeshCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsRealTime = true,
        SupportsBatch = true,
        SupportsEventDriven = true,
        SupportsMultiTenancy = true,
        SupportsFederation = true,
        MaxDomains = 0,
        MaxDataProducts = 0
    };
    public override string SemanticDescription =>
        "Comprehensive mesh-wide metrics collection including data product health, latency, throughput, " +
        "error rates, and SLA compliance. Supports Prometheus, OpenTelemetry, and custom metrics.";
    public override string[] Tags => ["metrics", "prometheus", "opentelemetry", "sla", "health"];
}

/// <summary>
/// Distributed Tracing Strategy - Cross-domain request tracing.
/// Implements T113.7: Data mesh observability.
/// </summary>
public sealed class DistributedTracingStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "distributed-tracing";
    public override string DisplayName => "Distributed Tracing";
    public override DataMeshCategory Category => DataMeshCategory.MeshObservability;
    public override DataMeshCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsRealTime = true,
        SupportsBatch = false,
        SupportsEventDriven = true,
        SupportsMultiTenancy = true,
        SupportsFederation = true,
        MaxDomains = 0,
        MaxDataProducts = 0
    };
    public override string SemanticDescription =>
        "Distributed tracing for cross-domain data requests with trace propagation, span correlation, " +
        "and latency breakdown. Supports Jaeger, Zipkin, and OpenTelemetry tracing.";
    public override string[] Tags => ["tracing", "jaeger", "zipkin", "spans", "correlation"];
}

/// <summary>
/// Data Quality Monitoring Strategy - Continuous quality monitoring.
/// Implements T113.7: Data mesh observability.
/// </summary>
public sealed class DataQualityMonitoringStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "data-quality-monitoring";
    public override string DisplayName => "Data Quality Monitoring";
    public override DataMeshCategory Category => DataMeshCategory.MeshObservability;
    public override DataMeshCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsRealTime = true,
        SupportsBatch = true,
        SupportsEventDriven = true,
        SupportsMultiTenancy = true,
        SupportsFederation = true,
        MaxDomains = 0,
        MaxDataProducts = 0
    };
    public override string SemanticDescription =>
        "Continuous data quality monitoring with anomaly detection, freshness tracking, " +
        "completeness checks, and automated alerting for quality degradation.";
    public override string[] Tags => ["quality", "anomaly", "freshness", "completeness", "alerting"];
}

/// <summary>
/// Usage Analytics Strategy - Data product usage analytics.
/// Implements T113.7: Data mesh observability.
/// </summary>
public sealed class UsageAnalyticsStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "usage-analytics";
    public override string DisplayName => "Usage Analytics";
    public override DataMeshCategory Category => DataMeshCategory.MeshObservability;
    public override DataMeshCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsRealTime = true,
        SupportsBatch = true,
        SupportsEventDriven = true,
        SupportsMultiTenancy = true,
        SupportsFederation = true,
        MaxDomains = 0,
        MaxDataProducts = 0
    };
    public override string SemanticDescription =>
        "Data product usage analytics including access patterns, consumer behavior, popular queries, " +
        "and trend analysis for product improvement and capacity planning.";
    public override string[] Tags => ["usage", "analytics", "patterns", "trends", "behavior"];
}

/// <summary>
/// Alerting Strategy - Intelligent alerting system.
/// Implements T113.7: Data mesh observability.
/// </summary>
public sealed class MeshAlertingStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "mesh-alerting";
    public override string DisplayName => "Mesh Alerting";
    public override DataMeshCategory Category => DataMeshCategory.MeshObservability;
    public override DataMeshCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsRealTime = true,
        SupportsBatch = false,
        SupportsEventDriven = true,
        SupportsMultiTenancy = true,
        SupportsFederation = true,
        MaxDomains = 0,
        MaxDataProducts = 0
    };
    public override string SemanticDescription =>
        "Intelligent alerting system with configurable thresholds, anomaly-based alerts, " +
        "alert routing, escalation policies, and integration with PagerDuty, Slack, and OpsGenie.";
    public override string[] Tags => ["alerting", "pagerduty", "slack", "escalation", "thresholds"];
}

/// <summary>
/// Dashboard Strategy - Unified observability dashboards.
/// Implements T113.7: Data mesh observability.
/// </summary>
public sealed class MeshDashboardStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "mesh-dashboard";
    public override string DisplayName => "Mesh Dashboards";
    public override DataMeshCategory Category => DataMeshCategory.MeshObservability;
    public override DataMeshCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsRealTime = true,
        SupportsBatch = false,
        SupportsEventDriven = false,
        SupportsMultiTenancy = true,
        SupportsFederation = true,
        MaxDomains = 0,
        MaxDataProducts = 0
    };
    public override string SemanticDescription =>
        "Unified observability dashboards with customizable views, drill-down capabilities, " +
        "real-time updates, and Grafana/Kibana integration for mesh-wide visibility.";
    public override string[] Tags => ["dashboard", "grafana", "kibana", "visualization", "drill-down"];
}

/// <summary>
/// Log Aggregation Strategy - Centralized log management.
/// Implements T113.7: Data mesh observability.
/// </summary>
public sealed class LogAggregationStrategy : DataMeshStrategyBase
{
    public override string StrategyId => "log-aggregation";
    public override string DisplayName => "Log Aggregation";
    public override DataMeshCategory Category => DataMeshCategory.MeshObservability;
    public override DataMeshCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsRealTime = true,
        SupportsBatch = true,
        SupportsEventDriven = true,
        SupportsMultiTenancy = true,
        SupportsFederation = true,
        MaxDomains = 0,
        MaxDataProducts = 0
    };
    public override string SemanticDescription =>
        "Centralized log aggregation across all domains with structured logging, log search, " +
        "pattern detection, and integration with ELK, Loki, and Splunk.";
    public override string[] Tags => ["logs", "elk", "loki", "splunk", "aggregation"];
}
