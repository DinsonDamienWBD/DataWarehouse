using System.Net.Http;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.ErrorTracking;

/// <summary>
/// Observability strategy for Bugsnag error monitoring and stability management.
/// Provides automated error detection, diagnostics, and stability scoring.
/// </summary>
/// <remarks>
/// Bugsnag is an error monitoring platform that provides real-time error reporting,
/// release tracking, and stability insights to help teams ship better software.
/// </remarks>
public sealed class BugsnagStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _apiKey = "";
    private string _releaseStage = "production";
    private string _appVersion = "";

    /// <inheritdoc/>
    public override string StrategyId => "bugsnag";

    /// <inheritdoc/>
    public override string Name => "Bugsnag";

    /// <summary>
    /// Initializes a new instance of the <see cref="BugsnagStrategy"/> class.
    /// </summary>
    public BugsnagStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: false,
        SupportsTracing: false,
        SupportsLogging: true,
        SupportsDistributedTracing: false,
        SupportsAlerting: true,
        SupportedExporters: new[] { "Bugsnag" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Configures the Bugsnag connection.
    /// </summary>
    /// <param name="apiKey">Bugsnag API key.</param>
    /// <param name="releaseStage">Release stage (production, staging, development).</param>
    /// <param name="appVersion">Application version.</param>
    public void Configure(string apiKey, string releaseStage = "production", string appVersion = "")
    {
        _apiKey = apiKey;
        _releaseStage = releaseStage;
        _appVersion = appVersion;

        _httpClient.DefaultRequestHeaders.Remove("Bugsnag-Api-Key");
        _httpClient.DefaultRequestHeaders.Add("Bugsnag-Api-Key", apiKey);
    }

    /// <inheritdoc/>
    protected override Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Bugsnag does not support metrics - use error tracking instead");
    }

    /// <inheritdoc/>
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Bugsnag does not support tracing");
    }

    /// <inheritdoc/>
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        IncrementCounter("bugsnag.logs_sent");
        foreach (var log in logEntries)
        {
            // Only send errors and critical logs to Bugsnag
            if (log.Level != LogLevel.Error && log.Level != LogLevel.Critical)
                continue;

            var payload = new
            {
                apiKey = _apiKey,
                notifier = new
                {
                    name = "DataWarehouse Bugsnag Strategy",
                    version = "1.0.0",
                    url = "https://github.com/datawarehouse"
                },
                events = new[]
                {
                    new
                    {
                        payloadVersion = "5",
                        exceptions = log.Exception != null
                            ? new[]
                            {
                                new
                                {
                                    errorClass = log.Exception.GetType().Name,
                                    message = log.Exception.Message,
                                    stacktrace = ParseStackTrace(log.Exception.StackTrace ?? "")
                                }
                            }
                            : new[]
                            {
                                new
                                {
                                    errorClass = "LogError",
                                    message = log.Message,
                                    stacktrace = new object[0]
                                }
                            },
                        severity = MapLogLevel(log.Level),
                        severityReason = new
                        {
                            type = "handledException"
                        },
                        unhandled = false,
                        app = new
                        {
                            version = _appVersion,
                            releaseStage = _releaseStage
                        },
                        device = new
                        {
                            hostname = Environment.MachineName,
                            osName = Environment.OSVersion.Platform.ToString()
                        },
                        metaData = new
                        {
                            custom = new
                            {
                                source = log.Properties?.GetValueOrDefault("Source")?.ToString(),
                                category = log.Properties?.GetValueOrDefault("Category")?.ToString(),
                                eventId = log.Properties?.GetValueOrDefault("EventId")?.ToString(),
                                timestamp = log.Timestamp.ToString("o")
                            }
                        }
                    }
                }
            };

            await SendToBugsnagAsync(payload, cancellationToken);
        }
    }

    /// <summary>
    /// Notifies Bugsnag of an exception.
    /// </summary>
    /// <param name="exception">Exception to notify.</param>
    /// <param name="severity">Severity (error, warning, info).</param>
    /// <param name="metadata">Optional metadata to include.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task NotifyExceptionAsync(
        Exception exception,
        string severity = "error",
        Dictionary<string, object>? metadata = null,
        CancellationToken ct = default)
    {
        var payload = new
        {
            apiKey = _apiKey,
            notifier = new
            {
                name = "DataWarehouse Bugsnag Strategy",
                version = "1.0.0",
                url = "https://github.com/datawarehouse"
            },
            events = new[]
            {
                new
                {
                    payloadVersion = "5",
                    exceptions = new[]
                    {
                        new
                        {
                            errorClass = exception.GetType().Name,
                            message = exception.Message,
                            stacktrace = ParseStackTrace(exception.StackTrace ?? "")
                        }
                    },
                    severity,
                    severityReason = new
                    {
                        type = "handledException"
                    },
                    unhandled = false,
                    app = new
                    {
                        version = _appVersion,
                        releaseStage = _releaseStage
                    },
                    device = new
                    {
                        hostname = Environment.MachineName
                    },
                    metaData = new
                    {
                        custom = metadata ?? new Dictionary<string, object>()
                    }
                }
            }
        };

        await SendToBugsnagAsync(payload, ct);
    }

    private async Task SendToBugsnagAsync(object payload, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("https://notify.bugsnag.com", content, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {

            // Bugsnag unavailable
            System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string MapLogLevel(LogLevel level)
    {
        return level switch
        {
            LogLevel.Critical => "error",
            LogLevel.Error => "error",
            LogLevel.Warning => "warning",
            _ => "info"
        };
    }

    private static object[] ParseStackTrace(string stackTrace)
    {
        var lines = stackTrace.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Select(line => new
        {
            file = "unknown",
            lineNumber = 0,
            method = line.Trim(),
            inProject = true
        }).ToArray();
    }

    /// <inheritdoc/>
    protected override Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        var isHealthy = !string.IsNullOrEmpty(_apiKey);

        return Task.FromResult(new HealthCheckResult(
            IsHealthy: isHealthy,
            Description: isHealthy ? "Bugsnag is configured" : "Bugsnag API key not configured",
            Data: new Dictionary<string, object>
            {
                ["releaseStage"] = _releaseStage,
                ["appVersion"] = _appVersion,
                ["hasApiKey"] = !string.IsNullOrEmpty(_apiKey)
            }));
    }

    /// <inheritdoc/>

    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        // Configuration validated via Configure method
        IncrementCounter("bugsnag.initialized");
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
        IncrementCounter("bugsnag.shutdown");
        await base.ShutdownAsyncCore(cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
                _apiKey = string.Empty;
        if (disposing)
        {
            _httpClient.Dispose();
        }
        base.Dispose(disposing);
    }
}
