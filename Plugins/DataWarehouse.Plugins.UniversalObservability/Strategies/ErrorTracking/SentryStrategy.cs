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

            var eventId = Guid.NewGuid().ToString("N");
            var payloadJson = JsonSerializer.Serialize(payload);

            // Sentry envelope format requires 3 parts:
            //   1. Envelope header:  {"event_id":"<id>","sent_at":"<iso>"}
            //   2. Item header:      {"type":"event","length":<bytes>}
            //   3. Item payload:     <json>
            // Each part is separated by a newline.
            var itemBytes = Encoding.UTF8.GetByteCount(payloadJson);
            var envelopeHeader = JsonSerializer.Serialize(new
            {
                event_id = eventId,
                sent_at = DateTimeOffset.UtcNow.ToString("o")
            });
            var itemHeader = JsonSerializer.Serialize(new { type = "event", length = itemBytes });
            var envelope = $"{envelopeHeader}\n{itemHeader}\n{payloadJson}";

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

    /// <summary>
    /// Parses a .NET stack trace into Sentry stacktrace frames.
    /// Extracts filename, line number, and function from each "at ... in file:line N" frame.
    /// </summary>
    private static object[] ParseStackTrace(string stackTrace)
    {
        if (string.IsNullOrEmpty(stackTrace))
            return Array.Empty<object>();

        var lines = stackTrace.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // Sentry expects frames in innermost-first order (reverse of .NET's outermost-first).
        return lines.Reverse().Select(line =>
        {
            line = line.Trim();
            string filename = "unknown";
            int lineno = 0;
            string function = line;

            var inIdx = line.LastIndexOf(" in ", StringComparison.Ordinal);
            if (inIdx >= 0)
            {
                function = line[..inIdx].TrimStart().TrimStart("at ".ToCharArray()).Trim();
                var fileAndLine = line[(inIdx + 4)..];
                var lineColonIdx = fileAndLine.LastIndexOf(":line ", StringComparison.Ordinal);
                if (lineColonIdx >= 0)
                {
                    filename = fileAndLine[..lineColonIdx];
                    int.TryParse(fileAndLine[(lineColonIdx + 6)..], out lineno);
                }
                else
                {
                    filename = fileAndLine;
                }
            }
            else if (line.StartsWith("at ", StringComparison.Ordinal))
            {
                function = line[3..].Trim();
            }

            return (object)new { filename, lineno, function };
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
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        // Finding 4584: removed decorative Task.Delay(100ms) — no real in-flight queue to drain.
        IncrementCounter("sentry.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
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
