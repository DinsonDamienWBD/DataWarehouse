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
        // Do NOT set DefaultRequestHeaders — inject per-request to avoid duplicate header accumulation
        // and thread-safety issues when Configure is called multiple times or concurrently.
    }

    /// <summary>Adds the Bugsnag API key to the request headers (per-request, thread-safe).</summary>
    private void AddApiKey(HttpRequestMessage request) =>
        request.Headers.Add("Bugsnag-Api-Key", _apiKey);

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

            using var bugsnagRequest = new HttpRequestMessage(HttpMethod.Post, "https://notify.bugsnag.com") { Content = content };
            AddApiKey(bugsnagRequest);
            using var response = await _httpClient.SendAsync(bugsnagRequest, ct);
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

    /// <summary>
    /// Parses a .NET stack trace string into Bugsnag stacktrace frames.
    /// Extracts file path, line number, and method name from each "at ... in file:line N" frame.
    /// </summary>
    private static object[] ParseStackTrace(string stackTrace)
    {
        if (string.IsNullOrEmpty(stackTrace))
            return Array.Empty<object>();

        var lines = stackTrace.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Select(line =>
        {
            line = line.Trim();
            string file = "unknown";
            int lineNumber = 0;
            string method = line;

            // .NET stack frame: "   at Namespace.Class.Method(...) in /path/file.cs:line 42"
            var inIdx = line.LastIndexOf(" in ", StringComparison.Ordinal);
            if (inIdx >= 0)
            {
                method = line[..inIdx].TrimStart().TrimStart("at ".ToCharArray()).Trim();
                var fileAndLine = line[(inIdx + 4)..];
                var lineColonIdx = fileAndLine.LastIndexOf(":line ", StringComparison.Ordinal);
                if (lineColonIdx >= 0)
                {
                    file = fileAndLine[..lineColonIdx];
                    int.TryParse(fileAndLine[(lineColonIdx + 6)..], out lineNumber);
                }
                else
                {
                    file = fileAndLine;
                }
            }
            else if (line.StartsWith("at ", StringComparison.Ordinal))
            {
                method = line[3..].Trim();
            }

            return (object)new { file, lineNumber, method, inProject = true };
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
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        // Finding 4584: removed decorative Task.Delay(100ms) — no real in-flight queue to drain.
        IncrementCounter("bugsnag.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
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
