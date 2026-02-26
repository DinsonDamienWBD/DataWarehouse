using System.Net.Http;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.ErrorTracking;

/// <summary>
/// Observability strategy for Sentry error tracking and performance monitoring.
/// Provides real-time error tracking, release health, and performance monitoring.
/// </summary>
/// <remarks>
/// Sentry is an application monitoring platform that helps developers identify,
/// triage, and resolve errors and performance issues in real-time.
/// </remarks>
public sealed class SentryStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _dsn = "";
    private string _environment = "production";
    private string _release = "";

    /// <inheritdoc/>
    public override string StrategyId => "sentry";

    /// <inheritdoc/>
    public override string Name => "Sentry";

    /// <summary>
    /// Initializes a new instance of the <see cref="SentryStrategy"/> class.
    /// </summary>
    public SentryStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: true,
        SupportsTracing: true,
        SupportsLogging: true,
        SupportsDistributedTracing: true,
        SupportsAlerting: true,
        SupportedExporters: new[] { "Sentry" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Configures the Sentry connection.
    /// </summary>
    /// <param name="dsn">Sentry DSN (Data Source Name).</param>
    /// <param name="environment">Environment name (production, staging, etc.).</param>
    /// <param name="release">Release version identifier.</param>
    public void Configure(string dsn, string environment = "production", string release = "")
    {
        _dsn = dsn;
        _environment = environment;
        _release = release;
    }

    /// <inheritdoc/>
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        IncrementCounter("sentry.metrics_sent");
        // Convert metrics to Sentry measurements
        var measurements = metrics.ToDictionary(
            m => m.Name,
            m => new { value = m.Value, unit = "none" }
        );

        var envelope = new
        {
            event_id = Guid.NewGuid().ToString("N"),
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            environment = _environment,
            release = _release,
            measurements
        };

        await SendToSentryAsync(envelope, cancellationToken);
    }

    /// <inheritdoc/>
    protected override async Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        IncrementCounter("sentry.traces_sent");
        foreach (var span in spans)
        {
            var transaction = new
            {
                event_id = Guid.NewGuid().ToString("N"),
                type = "transaction",
                transaction = span.OperationName,
                start_timestamp = span.StartTime.ToUnixTimeSeconds(),
                timestamp = span.StartTime.Add(span.Duration).ToUnixTimeSeconds(),
                contexts = new
                {
                    trace = new
                    {
                        trace_id = span.TraceId,
                        span_id = span.SpanId,
                        parent_span_id = span.ParentSpanId,
                        op = span.OperationName,
                        status = span.Attributes?.ContainsKey("error") == true ? "internal_error" : "ok"
                    }
                },
                tags = span.Attributes,
                environment = _environment,
                release = _release
            };

            await SendToSentryAsync(transaction, cancellationToken);
        }
    }

    /// <inheritdoc/>
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        IncrementCounter("sentry.logs_sent");
        foreach (var log in logEntries)
        {
            // Only send errors and critical logs to Sentry
            if (log.Level != LogLevel.Error && log.Level != LogLevel.Critical)
                continue;

            var sentryEvent = new
            {
                event_id = Guid.NewGuid().ToString("N"),
                timestamp = log.Timestamp.ToUnixTimeSeconds(),
                level = MapLogLevel(log.Level),
                message = new { formatted = log.Message },
                logger = log.Properties?.GetValueOrDefault("Source")?.ToString() ?? "datawarehouse",
                environment = _environment,
                release = _release,
                exception = log.Exception != null ? new
                {
                    values = new[]
                    {
                        new
                        {
                            type = log.Exception.GetType().Name,
                            value = log.Exception.Message,
                            stacktrace = new
                            {
                                frames = ParseStackTrace(log.Exception.StackTrace ?? "")
                            }
                        }
                    }
                } : null,
                tags = new Dictionary<string, string>
                {
                    ["category"] = log.Properties?.GetValueOrDefault("Category")?.ToString() ?? "application",
                    ["eventId"] = log.Properties?.GetValueOrDefault("EventId")?.ToString() ?? "0"
                }
            };

            await SendToSentryAsync(sentryEvent, cancellationToken);
        }
    }

    /// <summary>
    /// Captures an exception and sends it to Sentry.
    /// </summary>
    /// <param name="exception">Exception to capture.</param>
    /// <param name="additionalData">Optional additional context data.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task CaptureExceptionAsync(
        Exception exception,
        Dictionary<string, object>? additionalData = null,
        CancellationToken ct = default)
    {
        var sentryEvent = new
        {
            event_id = Guid.NewGuid().ToString("N"),
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            level = "error",
            environment = _environment,
            release = _release,
            exception = new
            {
                values = new[]
                {
                    new
                    {
                        type = exception.GetType().Name,
                        value = exception.Message,
                        stacktrace = new
                        {
                            frames = ParseStackTrace(exception.StackTrace ?? "")
                        }
                    }
                }
            },
            extra = additionalData
        };

        await SendToSentryAsync(sentryEvent, ct);
    }

    private async Task SendToSentryAsync(object payload, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_dsn))
            return;

        try
        {
            var dsnUri = new Uri(_dsn);
            var projectId = dsnUri.AbsolutePath.Trim('/');
            var sentryUrl = $"{dsnUri.Scheme}://{dsnUri.Host}/api/{projectId}/envelope/";

            var json = JsonSerializer.Serialize(payload);
            var envelope = $"{{\n  \"event_id\": \"{Guid.NewGuid():N}\"\n}}\n{json}";

            var content = new StringContent(envelope, Encoding.UTF8, "application/x-sentry-envelope");
            content.Headers.Add("X-Sentry-Auth", BuildAuthHeader(dsnUri));

            using var response = await _httpClient.PostAsync(sentryUrl, content, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {

            // Sentry unavailable
            System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
        }
    }

    private string BuildAuthHeader(Uri dsnUri)
    {
        var publicKey = dsnUri.UserInfo;
        return $"Sentry sentry_version=7, sentry_key={publicKey}, sentry_timestamp={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
    }

    private static string MapLogLevel(LogLevel level)
    {
        return level switch
        {
            LogLevel.Trace => "debug",
            LogLevel.Debug => "debug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warning",
            LogLevel.Error => "error",
            LogLevel.Critical => "fatal",
            _ => "info"
        };
    }

    private static object[] ParseStackTrace(string stackTrace)
    {
        var lines = stackTrace.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Select(line => new
        {
            filename = "unknown",
            function = line.Trim()
        }).ToArray();
    }

    /// <inheritdoc/>
    protected override Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        var isHealthy = !string.IsNullOrEmpty(_dsn);

        return Task.FromResult(new HealthCheckResult(
            IsHealthy: isHealthy,
            Description: isHealthy ? "Sentry is configured" : "Sentry DSN not configured",
            Data: new Dictionary<string, object>
            {
                ["environment"] = _environment,
                ["release"] = _release,
                ["hasDsn"] = !string.IsNullOrEmpty(_dsn)
            }));
    }

    /// <inheritdoc/>

    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        // Configuration validated via Configure method
        IncrementCounter("sentry.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }


    /// <inheritdoc/>
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromMilliseconds(100), cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* Shutdown grace period elapsed */ }
        IncrementCounter("sentry.shutdown");
        await base.ShutdownAsyncCore(cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _httpClient.Dispose();
        }
        base.Dispose(disposing);
    }
}
