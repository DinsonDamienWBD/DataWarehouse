using System.Collections.Concurrent;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.Plugins.AppPlatform.Models;

namespace DataWarehouse.Plugins.AppPlatform.Strategies;

/// <summary>
/// Strategy that manages per-application observability configurations and routes
/// telemetry data (metrics, traces, logs) to UniversalObservability via the message bus.
/// Enforces per-app isolation by injecting <c>app_id</c> tags on all emitted telemetry
/// and filtering all queries by <c>app_id</c> to prevent cross-app data leakage.
/// </summary>
/// <remarks>
/// <para>
/// Each registered application can configure which telemetry types are enabled,
/// retention periods, log level thresholds, and alerting thresholds. The strategy
/// stores configurations in a thread-safe <see cref="ConcurrentDictionary{TKey, TValue}"/>
/// and synchronizes configuration changes to UniversalObservability.
/// </para>
/// <para>
/// All communication with UniversalObservability is through the message bus:
/// <list type="bullet">
///   <item><c>observability.app.configure</c>: Send per-app observability configuration</item>
///   <item><c>observability.app.remove</c>: Remove per-app observability configuration</item>
///   <item><c>observability.metrics.emit</c>: Emit a metric with app_id tag injection</item>
///   <item><c>observability.traces.emit</c>: Emit a trace with app_id attribute injection</item>
///   <item><c>observability.logs.emit</c>: Emit a log entry with app_id property injection</item>
///   <item><c>observability.query</c>: Query telemetry data with mandatory app_id filter</item>
/// </list>
/// </para>
/// </remarks>
internal sealed class AppObservabilityStrategy
{
    /// <summary>
    /// Thread-safe dictionary storing per-app observability configurations keyed by AppId.
    /// </summary>
    private readonly BoundedDictionary<string, AppObservabilityConfig> _configs = new BoundedDictionary<string, AppObservabilityConfig>(1000);

    /// <summary>
    /// Message bus for communicating with UniversalObservability.
    /// </summary>
    private readonly IMessageBus _messageBus;

    /// <summary>
    /// Plugin identifier used as the source in outgoing messages.
    /// </summary>
    private readonly string _pluginId;

    /// <summary>
    /// Initializes a new instance of the <see cref="AppObservabilityStrategy"/> class.
    /// </summary>
    /// <param name="messageBus">The message bus for inter-plugin communication.</param>
    /// <param name="pluginId">The plugin identifier used as message source.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="messageBus"/> or <paramref name="pluginId"/> is <c>null</c>.
    /// </exception>
    public AppObservabilityStrategy(IMessageBus messageBus, string pluginId)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _pluginId = pluginId ?? throw new ArgumentNullException(nameof(pluginId));
    }

    /// <summary>
    /// Configures observability for an application by storing the configuration locally
    /// and sending it to UniversalObservability via the <c>observability.app.configure</c>
    /// message bus topic.
    /// </summary>
    /// <param name="config">The observability configuration for the application.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="MessageResponse"/> from UniversalObservability confirming the configuration.</returns>
    public async Task<MessageResponse> ConfigureObservabilityAsync(AppObservabilityConfig config, CancellationToken ct = default)
    {
        _configs[config.AppId] = config;

        var response = await _messageBus.SendAsync("observability.app.configure", new PluginMessage
        {
            Type = "observability.app.configure",
            SourcePluginId = _pluginId,
            Payload = new Dictionary<string, object>
            {
                ["AppId"] = config.AppId,
                ["MetricsEnabled"] = config.MetricsEnabled,
                ["TracingEnabled"] = config.TracingEnabled,
                ["LoggingEnabled"] = config.LoggingEnabled,
                ["MetricsRetentionDays"] = config.MetricsRetentionDays,
                ["TracingRetentionDays"] = config.TracingRetentionDays,
                ["LogRetentionDays"] = config.LogRetentionDays,
                ["LogLevel"] = config.LogLevel.ToString(),
                ["AlertingEnabled"] = config.AlertingEnabled,
                ["AlertThresholdErrorRate"] = config.AlertThresholdErrorRate?.ToString() ?? string.Empty,
                ["AlertThresholdLatencyMs"] = config.AlertThresholdLatencyMs?.ToString() ?? string.Empty,
                ["AlertThresholdRequestsPerMinute"] = config.AlertThresholdRequestsPerMinute?.ToString() ?? string.Empty,
                ["AlertNotificationChannels"] = config.AlertNotificationChannels
            }
        }, ct);

        return response;
    }

    /// <summary>
    /// Removes the observability configuration for the specified application and notifies
    /// UniversalObservability via the <c>observability.app.remove</c> message bus topic.
    /// </summary>
    /// <param name="appId">The application identifier whose observability configuration should be removed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="MessageResponse"/> from UniversalObservability confirming the removal.</returns>
    public async Task<MessageResponse> RemoveObservabilityAsync(string appId, CancellationToken ct = default)
    {
        _configs.TryRemove(appId, out _);

        var response = await _messageBus.SendAsync("observability.app.remove", new PluginMessage
        {
            Type = "observability.app.remove",
            SourcePluginId = _pluginId,
            Payload = new Dictionary<string, object>
            {
                ["AppId"] = appId
            }
        }, ct);

        return response;
    }

    /// <summary>
    /// Retrieves the current observability configuration for the specified application.
    /// </summary>
    /// <param name="appId">The application identifier to look up.</param>
    /// <returns>The <see cref="AppObservabilityConfig"/> if found; <c>null</c> otherwise.</returns>
    public Task<AppObservabilityConfig?> GetObservabilityConfigAsync(string appId)
    {
        _configs.TryGetValue(appId, out var config);
        return Task.FromResult<AppObservabilityConfig?>(config);
    }

    /// <summary>
    /// Updates an existing observability configuration by replacing it locally, setting
    /// the <see cref="AppObservabilityConfig.UpdatedAt"/> timestamp, and re-sending the
    /// configuration to UniversalObservability.
    /// </summary>
    /// <param name="updatedConfig">The updated observability configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="MessageResponse"/> from UniversalObservability confirming the update.</returns>
    public async Task<MessageResponse> UpdateObservabilityAsync(AppObservabilityConfig updatedConfig, CancellationToken ct = default)
    {
        var withTimestamp = updatedConfig with { UpdatedAt = DateTime.UtcNow };
        _configs[withTimestamp.AppId] = withTimestamp;

        return await ConfigureObservabilityAsync(withTimestamp, ct);
    }

    /// <summary>
    /// Emits a metric for the specified application with automatic <c>app_id</c> tag injection
    /// for isolation. If metrics are disabled for this app, the call is silently ignored.
    /// </summary>
    /// <param name="appId">The application identifier that owns this metric.</param>
    /// <param name="metricName">The name of the metric (e.g., "request_count", "error_rate").</param>
    /// <param name="value">The numeric value of the metric.</param>
    /// <param name="tags">Optional additional tags to attach to the metric.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="MessageResponse"/> from UniversalObservability, or <c>null</c> if metrics are disabled.</returns>
    public async Task<MessageResponse?> EmitMetricAsync(
        string appId,
        string metricName,
        double value,
        Dictionary<string, string>? tags = null,
        CancellationToken ct = default)
    {
        if (!_configs.TryGetValue(appId, out var config) || !config.MetricsEnabled)
            return null;

        // Inject app_id and source tags for isolation
        var enrichedTags = new Dictionary<string, string>(tags ?? new Dictionary<string, string>())
        {
            ["app_id"] = appId,
            ["source"] = "platform"
        };

        var response = await _messageBus.SendAsync("observability.metrics.emit", new PluginMessage
        {
            Type = "observability.metrics.emit",
            SourcePluginId = _pluginId,
            Payload = new Dictionary<string, object>
            {
                ["MetricName"] = metricName,
                ["Value"] = value,
                ["Tags"] = enrichedTags,
                ["AppId"] = appId
            }
        }, ct);

        return response;
    }

    /// <summary>
    /// Emits a trace span for the specified application with automatic <c>app_id</c> attribute
    /// injection for isolation. If tracing is disabled for this app, the call is silently ignored.
    /// </summary>
    /// <param name="appId">The application identifier that owns this trace.</param>
    /// <param name="spanName">The name of the trace span.</param>
    /// <param name="traceId">The distributed trace identifier.</param>
    /// <param name="attributes">Optional additional attributes to attach to the span.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="MessageResponse"/> from UniversalObservability, or <c>null</c> if tracing is disabled.</returns>
    public async Task<MessageResponse?> EmitTraceAsync(
        string appId,
        string spanName,
        string traceId,
        Dictionary<string, string>? attributes = null,
        CancellationToken ct = default)
    {
        if (!_configs.TryGetValue(appId, out var config) || !config.TracingEnabled)
            return null;

        // Inject app_id attribute for isolation
        var enrichedAttributes = new Dictionary<string, string>(attributes ?? new Dictionary<string, string>())
        {
            ["app_id"] = appId,
            ["source"] = "platform"
        };

        var response = await _messageBus.SendAsync("observability.traces.emit", new PluginMessage
        {
            Type = "observability.traces.emit",
            SourcePluginId = _pluginId,
            Payload = new Dictionary<string, object>
            {
                ["SpanName"] = spanName,
                ["TraceId"] = traceId,
                ["Attributes"] = enrichedAttributes,
                ["AppId"] = appId
            }
        }, ct);

        return response;
    }

    /// <summary>
    /// Emits a log entry for the specified application with automatic <c>app_id</c> property
    /// injection for isolation. If logging is disabled or the log level is below the configured
    /// minimum, the call is silently ignored.
    /// </summary>
    /// <param name="appId">The application identifier that owns this log entry.</param>
    /// <param name="level">The severity level of the log entry.</param>
    /// <param name="message">The log message text.</param>
    /// <param name="properties">Optional additional properties to attach to the log entry.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="MessageResponse"/> from UniversalObservability, or <c>null</c> if logging is disabled or filtered.</returns>
    public async Task<MessageResponse?> EmitLogAsync(
        string appId,
        ObservabilityLevel level,
        string message,
        Dictionary<string, string>? properties = null,
        CancellationToken ct = default)
    {
        if (!_configs.TryGetValue(appId, out var config) || !config.LoggingEnabled)
            return null;

        // Filter by configured minimum log level
        if (level < config.LogLevel)
            return null;

        // Inject app_id property for isolation
        var enrichedProperties = new Dictionary<string, string>(properties ?? new Dictionary<string, string>())
        {
            ["app_id"] = appId,
            ["source"] = "platform"
        };

        var response = await _messageBus.SendAsync("observability.logs.emit", new PluginMessage
        {
            Type = "observability.logs.emit",
            SourcePluginId = _pluginId,
            Payload = new Dictionary<string, object>
            {
                ["Level"] = level.ToString(),
                ["Message"] = message,
                ["Properties"] = enrichedProperties,
                ["AppId"] = appId
            }
        }, ct);

        return response;
    }

    /// <summary>
    /// Queries metrics for the specified application with mandatory <c>app_id</c> filter
    /// to enforce isolation. One application cannot query another application's metrics.
    /// </summary>
    /// <param name="appId">The application identifier whose metrics to query. Always used as a filter.</param>
    /// <param name="metricName">The name of the metric to query.</param>
    /// <param name="from">Start of the query time range (UTC).</param>
    /// <param name="to">End of the query time range (UTC).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="MessageResponse"/> containing the query results.</returns>
    public async Task<MessageResponse> QueryMetricsAsync(
        string appId,
        string metricName,
        DateTime from,
        DateTime to,
        CancellationToken ct = default)
    {
        // ALWAYS include app_id filter to enforce isolation -- prevents cross-app data leakage
        var response = await _messageBus.SendAsync("observability.query", new PluginMessage
        {
            Type = "observability.query",
            SourcePluginId = _pluginId,
            Payload = new Dictionary<string, object>
            {
                ["QueryType"] = "metrics",
                ["MetricName"] = metricName,
                ["From"] = from.ToString("O"),
                ["To"] = to.ToString("O"),
                ["Filter_AppId"] = appId
            }
        }, ct);

        return response;
    }

    /// <summary>
    /// Queries traces for the specified application with mandatory <c>app_id</c> filter
    /// to enforce isolation. One application cannot query another application's traces.
    /// </summary>
    /// <param name="appId">The application identifier whose traces to query. Always used as a filter.</param>
    /// <param name="traceId">The distributed trace identifier to look up.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="MessageResponse"/> containing the query results.</returns>
    public async Task<MessageResponse> QueryTracesAsync(
        string appId,
        string traceId,
        CancellationToken ct = default)
    {
        // ALWAYS include app_id filter to enforce isolation -- prevents cross-app data leakage
        var response = await _messageBus.SendAsync("observability.query", new PluginMessage
        {
            Type = "observability.query",
            SourcePluginId = _pluginId,
            Payload = new Dictionary<string, object>
            {
                ["QueryType"] = "traces",
                ["TraceId"] = traceId,
                ["Filter_AppId"] = appId
            }
        }, ct);

        return response;
    }

    /// <summary>
    /// Queries logs for the specified application with mandatory <c>app_id</c> filter
    /// to enforce isolation. One application cannot query another application's logs.
    /// </summary>
    /// <param name="appId">The application identifier whose logs to query. Always used as a filter.</param>
    /// <param name="minLevel">Optional minimum log level filter. <c>null</c> returns all levels.</param>
    /// <param name="from">Start of the query time range (UTC).</param>
    /// <param name="to">End of the query time range (UTC).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="MessageResponse"/> containing the query results.</returns>
    public async Task<MessageResponse> QueryLogsAsync(
        string appId,
        ObservabilityLevel? minLevel,
        DateTime from,
        DateTime to,
        CancellationToken ct = default)
    {
        // ALWAYS include app_id filter to enforce isolation -- prevents cross-app data leakage
        var payload = new Dictionary<string, object>
        {
            ["QueryType"] = "logs",
            ["From"] = from.ToString("O"),
            ["To"] = to.ToString("O"),
            ["Filter_AppId"] = appId
        };

        if (minLevel.HasValue)
        {
            payload["MinLevel"] = minLevel.Value.ToString();
        }

        var response = await _messageBus.SendAsync("observability.query", new PluginMessage
        {
            Type = "observability.query",
            SourcePluginId = _pluginId,
            Payload = payload
        }, ct);

        return response;
    }
}
