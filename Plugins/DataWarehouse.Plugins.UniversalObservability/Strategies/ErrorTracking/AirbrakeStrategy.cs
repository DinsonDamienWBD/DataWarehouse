using System.Net.Http;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.ErrorTracking;

/// <summary>
/// Observability strategy for Airbrake error monitoring and performance tracking.
/// Provides error tracking, performance monitoring, and deployment tracking.
/// </summary>
/// <remarks>
/// Airbrake is an error monitoring service that captures errors in real-time,
/// groups them intelligently, and provides actionable insights for developers.
/// </remarks>
public sealed class AirbrakeStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _projectId = "";
    private string _projectKey = "";
    private string _host = "https://api.airbrake.io";
    private string _environment = "production";

    /// <inheritdoc/>
    public override string StrategyId => "airbrake";

    /// <inheritdoc/>
    public override string Name => "Airbrake";

    /// <summary>
    /// Initializes a new instance of the <see cref="AirbrakeStrategy"/> class.
    /// </summary>
    public AirbrakeStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: true,
        SupportsTracing: false,
        SupportsLogging: true,
        SupportsDistributedTracing: false,
        SupportsAlerting: true,
        SupportedExporters: new[] { "Airbrake" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Configures the Airbrake connection.
    /// </summary>
    /// <param name="projectId">Airbrake project ID.</param>
    /// <param name="projectKey">Airbrake project API key.</param>
    /// <param name="environment">Environment name (production, staging, etc.).</param>
    /// <param name="host">Airbrake API host (default: https://api.airbrake.io).</param>
    public void Configure(string projectId, string projectKey, string environment = "production", string host = "https://api.airbrake.io")
    {
        _projectId = projectId;
        _projectKey = projectKey;
        _environment = environment;
        _host = host;
    }

    /// <inheritdoc/>
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        IncrementCounter("airbrake.metrics_sent");
        // Airbrake supports performance metrics
        foreach (var metric in metrics)
        {
            var route = new
            {
                method = "GET",
                route = $"/metrics/{metric.Name}",
                statusCode = 200,
                time = DateTimeOffset.UtcNow.ToString("o")
            };

            var timing = new
            {
                value = metric.Value,
                unit = "ms"
            };

            var payload = new
            {
                routes = new[] { route },
                environment = _environment,
                timing
            };

            await SendPerformanceDataAsync(payload, cancellationToken);
        }
    }

    /// <inheritdoc/>
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Airbrake does not support tracing");
    }

    /// <inheritdoc/>
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        IncrementCounter("airbrake.logs_sent");
        foreach (var log in logEntries)
        {
            // Only send errors and critical logs to Airbrake
            if (log.Level != LogLevel.Error && log.Level != LogLevel.Critical)
                continue;

            var notice = new
            {
                notifier = new
                {
                    name = "DataWarehouse Airbrake Strategy",
                    version = "1.0.0",
                    url = "https://github.com/datawarehouse"
                },
                errors = log.Exception != null
                    ? new[]
                    {
                        new
                        {
                            type = log.Exception.GetType().Name,
                            message = log.Exception.Message,
                            backtrace = ParseBacktrace(log.Exception.StackTrace ?? "")
                        }
                    }
                    : new[]
                    {
                        new
                        {
                            type = "LogError",
                            message = log.Message,
                            backtrace = new object[0]
                        }
                    },
                context = new
                {
                    environment = _environment,
                    severity = MapLogLevel(log.Level),
                    component = log.Properties?.GetValueOrDefault("Source")?.ToString(),
                    action = log.Properties?.GetValueOrDefault("Category")?.ToString()
                },
                environment = new Dictionary<string, object>
                {
                    ["hostname"] = Environment.MachineName,
                    ["platform"] = Environment.OSVersion.Platform.ToString()
                },
                @params = new
                {
                    eventId = log.Properties?.GetValueOrDefault("EventId")?.ToString(),
                    timestamp = log.Timestamp.ToString("o")
                }
            };

            await SendNoticeAsync(notice, cancellationToken);
        }
    }

    /// <summary>
    /// Notifies Airbrake of an exception.
    /// </summary>
    /// <param name="exception">Exception to notify.</param>
    /// <param name="context">Optional context information.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task NotifyAsync(
        Exception exception,
        Dictionary<string, object>? context = null,
        CancellationToken ct = default)
    {
        var notice = new
        {
            notifier = new
            {
                name = "DataWarehouse Airbrake Strategy",
                version = "1.0.0",
                url = "https://github.com/datawarehouse"
            },
            errors = new[]
            {
                new
                {
                    type = exception.GetType().Name,
                    message = exception.Message,
                    backtrace = ParseBacktrace(exception.StackTrace ?? "")
                }
            },
            context = new
            {
                environment = _environment,
                severity = "error"
            },
            environment = new Dictionary<string, object>
            {
                ["hostname"] = Environment.MachineName
            },
            @params = context ?? new Dictionary<string, object>()
        };

        await SendNoticeAsync(notice, ct);
    }

    private async Task SendNoticeAsync(object notice, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(notice);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var url = $"{_host}/api/v3/projects/{_projectId}/notices?key={_projectKey}";
            var response = await _httpClient.PostAsync(url, content, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {

            // Airbrake unavailable
            System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task SendPerformanceDataAsync(object data, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(data);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var url = $"{_host}/api/v5/projects/{_projectId}/routes-stats?key={_projectKey}";
            var response = await _httpClient.PostAsync(url, content, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {

            // Airbrake unavailable
            System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string MapLogLevel(LogLevel level)
    {
        return level switch
        {
            LogLevel.Critical => "critical",
            LogLevel.Error => "error",
            LogLevel.Warning => "warning",
            LogLevel.Information => "info",
            _ => "debug"
        };
    }

    private static object[] ParseBacktrace(string stackTrace)
    {
        var lines = stackTrace.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Select((line, index) => new
        {
            file = "unknown",
            line = index + 1,
            function = line.Trim()
        }).ToArray();
    }

    /// <inheritdoc/>
    protected override Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        var isHealthy = !string.IsNullOrEmpty(_projectId) && !string.IsNullOrEmpty(_projectKey);

        return Task.FromResult(new HealthCheckResult(
            IsHealthy: isHealthy,
            Description: isHealthy ? "Airbrake is configured" : "Airbrake project credentials not configured",
            Data: new Dictionary<string, object>
            {
                ["projectId"] = _projectId,
                ["environment"] = _environment,
                ["host"] = _host,
                ["hasProjectKey"] = !string.IsNullOrEmpty(_projectKey)
            }));
    }

    /// <inheritdoc/>

    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        // Configuration validated via Configure method
        IncrementCounter("airbrake.initialized");
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
        IncrementCounter("airbrake.shutdown");
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
