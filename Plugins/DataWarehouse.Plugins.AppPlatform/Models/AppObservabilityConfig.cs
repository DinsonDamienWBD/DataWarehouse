namespace DataWarehouse.Plugins.AppPlatform.Models;

/// <summary>
/// Defines the observability log level thresholds for per-app telemetry filtering.
/// Log entries below the configured level are suppressed.
/// </summary>
public enum ObservabilityLevel
{
    /// <summary>
    /// Most verbose level. Captures all telemetry including fine-grained diagnostic data.
    /// </summary>
    Trace,

    /// <summary>
    /// Debug-level telemetry for development and troubleshooting.
    /// </summary>
    Debug,

    /// <summary>
    /// Informational messages describing normal application flow. Default level.
    /// </summary>
    Info,

    /// <summary>
    /// Indicates a potential issue that does not prevent normal operation.
    /// </summary>
    Warning,

    /// <summary>
    /// An error occurred that may affect specific operations but not overall system health.
    /// </summary>
    Error,

    /// <summary>
    /// A critical failure requiring immediate attention. System integrity may be compromised.
    /// </summary>
    Critical
}

/// <summary>
/// Per-application observability configuration that controls which telemetry types
/// (metrics, traces, logs) are enabled, how long data is retained, the minimum log level,
/// and alerting thresholds for automated notifications.
/// </summary>
/// <remarks>
/// <para>
/// Each application independently configures its observability settings. All telemetry
/// emitted by the application is tagged with <see cref="AppId"/> for isolation, ensuring
/// one application cannot access another's observability data.
/// </para>
/// <para>
/// Alert thresholds enable proactive monitoring: when error rates, P95 latency, or
/// request rates exceed the configured limits, notifications are sent to the channels
/// specified in <see cref="AlertNotificationChannels"/>.
/// </para>
/// </remarks>
public sealed record AppObservabilityConfig
{
    /// <summary>
    /// Identifier of the application this observability configuration belongs to.
    /// Used as the <c>app_id</c> tag on all emitted telemetry for isolation.
    /// </summary>
    public required string AppId { get; init; }

    /// <summary>
    /// Whether metrics collection is enabled for this application. Defaults to <c>true</c>.
    /// When disabled, metric emission calls are silently ignored.
    /// </summary>
    public bool MetricsEnabled { get; init; } = true;

    /// <summary>
    /// Whether distributed tracing is enabled for this application. Defaults to <c>true</c>.
    /// When disabled, trace emission calls are silently ignored.
    /// </summary>
    public bool TracingEnabled { get; init; } = true;

    /// <summary>
    /// Whether log collection is enabled for this application. Defaults to <c>true</c>.
    /// When disabled, log emission calls are silently ignored.
    /// </summary>
    public bool LoggingEnabled { get; init; } = true;

    /// <summary>
    /// Whether automated alerting is enabled for this application. Defaults to <c>false</c>.
    /// Requires at least one alert threshold and notification channel to be configured.
    /// </summary>
    public bool AlertingEnabled { get; init; }

    /// <summary>
    /// Number of days to retain metrics data for this application. Defaults to 30.
    /// After this period, metrics data is eligible for automated cleanup.
    /// </summary>
    public int MetricsRetentionDays { get; init; } = 30;

    /// <summary>
    /// Number of days to retain distributed trace data for this application. Defaults to 7.
    /// Trace data is typically more voluminous and retained for shorter periods.
    /// </summary>
    public int TracingRetentionDays { get; init; } = 7;

    /// <summary>
    /// Number of days to retain log data for this application. Defaults to 14.
    /// After this period, log data is eligible for automated cleanup.
    /// </summary>
    public int LogRetentionDays { get; init; } = 14;

    /// <summary>
    /// Minimum log level for this application. Log entries below this level are suppressed.
    /// Defaults to <see cref="ObservabilityLevel.Info"/>.
    /// </summary>
    public ObservabilityLevel LogLevel { get; init; } = ObservabilityLevel.Info;

    /// <summary>
    /// Error rate percentage threshold for alerting. When the error rate exceeds this value,
    /// an alert is triggered. <c>null</c> means error rate alerting is disabled.
    /// </summary>
    public double? AlertThresholdErrorRate { get; init; }

    /// <summary>
    /// P95 latency threshold in milliseconds for alerting. When the 95th percentile latency
    /// exceeds this value, an alert is triggered. <c>null</c> means latency alerting is disabled.
    /// </summary>
    public double? AlertThresholdLatencyMs { get; init; }

    /// <summary>
    /// Request rate threshold (requests per minute) for alerting. When the rate exceeds
    /// this value, an alert is triggered. <c>null</c> means rate alerting is disabled.
    /// </summary>
    public int? AlertThresholdRequestsPerMinute { get; init; }

    /// <summary>
    /// Notification channels for alert delivery. Each entry specifies a channel type and
    /// destination, e.g., <c>"email:admin@app.com"</c>, <c>"webhook:https://..."</c>.
    /// Defaults to an empty array (no notifications).
    /// </summary>
    public string[] AlertNotificationChannels { get; init; } = [];

    /// <summary>
    /// UTC timestamp when this observability configuration was created.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// UTC timestamp of the most recent update, or <c>null</c> if never updated.
    /// </summary>
    public DateTime? UpdatedAt { get; init; }
}
