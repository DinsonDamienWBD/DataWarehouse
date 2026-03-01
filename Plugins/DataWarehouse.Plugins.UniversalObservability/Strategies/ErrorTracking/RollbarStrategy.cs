using System.Net.Http;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.ErrorTracking;

/// <summary>
/// Observability strategy for Rollbar error tracking and monitoring.
/// Provides real-time error tracking, deployment tracking, and intelligent grouping.
/// </summary>
/// <remarks>
/// Rollbar is an error tracking service that helps developers discover, predict,
/// and resolve errors faster with real-time alerts and comprehensive debugging context.
/// </remarks>
public sealed class RollbarStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _accessToken = "";
    private string _environment = "production";
    private string _codeVersion = "";

    /// <inheritdoc/>
    public override string StrategyId => "rollbar";

    /// <inheritdoc/>
    public override string Name => "Rollbar";

    /// <summary>
    /// Initializes a new instance of the <see cref="RollbarStrategy"/> class.
    /// </summary>
    public RollbarStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: false,
        SupportsTracing: false,
        SupportsLogging: true,
        SupportsDistributedTracing: false,
        SupportsAlerting: true,
        SupportedExporters: new[] { "Rollbar" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Configures the Rollbar connection.
    /// </summary>
    /// <param name="accessToken">Rollbar project access token.</param>
    /// <param name="environment">Environment name (production, staging, etc.).</param>
    /// <param name="codeVersion">Code version or Git SHA.</param>
    public void Configure(string accessToken, string environment = "production", string codeVersion = "")
    {
        _accessToken = accessToken;
        _environment = environment;
        _codeVersion = codeVersion;
    }

    /// <inheritdoc/>
    protected override Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Rollbar does not support metrics - use error tracking instead");
    }

    /// <inheritdoc/>
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Rollbar does not support tracing");
    }

    /// <inheritdoc/>
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        IncrementCounter("rollbar.logs_sent");
        foreach (var log in logEntries)
        {
            // Only send errors and critical logs to Rollbar
            if (log.Level != LogLevel.Error && log.Level != LogLevel.Critical)
                continue;

            var payload = new
            {
                access_token = _accessToken,
                data = new
                {
                    environment = _environment,
                    level = MapLogLevel(log.Level),
                    timestamp = log.Timestamp.ToUnixTimeSeconds(),
                    code_version = _codeVersion,
                    platform = "dotnet",
                    language = "csharp",
                    framework = "net10.0",
                    body = (object)(log.Exception != null
                        ? new
                        {
                            trace = new
                            {
                                exception = new
                                {
                                    @class = log.Exception.GetType().FullName,
                                    message = log.Exception.Message,
                                    description = log.Message
                                },
                                frames = ParseStackTrace(log.Exception.StackTrace ?? "")
                            }
                        }
                        : new
                        {
                            message = new
                            {
                                body = log.Message
                            }
                        }),
                    custom = new
                    {
                        source = log.Properties?.GetValueOrDefault("Source")?.ToString(),
                        category = log.Properties?.GetValueOrDefault("Category")?.ToString(),
                        eventId = log.Properties?.GetValueOrDefault("EventId")?.ToString()
                    },
                    server = new
                    {
                        host = Environment.MachineName
                    }
                }
            };

            await SendToRollbarAsync(payload, cancellationToken);
        }
    }

    /// <summary>
    /// Reports an exception to Rollbar.
    /// </summary>
    /// <param name="exception">Exception to report.</param>
    /// <param name="level">Severity level (critical, error, warning, info, debug).</param>
    /// <param name="customData">Optional custom data to include.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ReportExceptionAsync(
        Exception exception,
        string level = "error",
        Dictionary<string, object>? customData = null,
        CancellationToken ct = default)
    {
        var payload = new
        {
            access_token = _accessToken,
            data = new
            {
                environment = _environment,
                level,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                code_version = _codeVersion,
                platform = "dotnet",
                language = "csharp",
                body = new
                {
                    trace = new
                    {
                        exception = new
                        {
                            @class = exception.GetType().FullName,
                            message = exception.Message
                        },
                        frames = ParseStackTrace(exception.StackTrace ?? "")
                    }
                },
                custom = customData,
                server = new
                {
                    host = Environment.MachineName
                }
            }
        };

        await SendToRollbarAsync(payload, ct);
    }

    /// <summary>
    /// Reports a message to Rollbar.
    /// </summary>
    /// <param name="message">Message to report.</param>
    /// <param name="level">Severity level (critical, error, warning, info, debug).</param>
    /// <param name="customData">Optional custom data to include.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ReportMessageAsync(
        string message,
        string level = "info",
        Dictionary<string, object>? customData = null,
        CancellationToken ct = default)
    {
        var payload = new
        {
            access_token = _accessToken,
            data = new
            {
                environment = _environment,
                level,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                code_version = _codeVersion,
                platform = "dotnet",
                body = new
                {
                    message = new
                    {
                        body = message
                    }
                },
                custom = customData,
                server = new
                {
                    host = Environment.MachineName
                }
            }
        };

        await SendToRollbarAsync(payload, ct);
    }

    private async Task SendToRollbarAsync(object payload, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await _httpClient.PostAsync("https://api.rollbar.com/api/1/item/", content, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {

            // Rollbar unavailable
            System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
        }
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
            LogLevel.Critical => "critical",
            _ => "info"
        };
    }

    /// <summary>
    /// Parses a .NET stack trace string into Rollbar frames.
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
            string filename = "unknown";
            int lineno = 0;
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
                method = line[3..].Trim();
            }

            return (object)new { filename, lineno, method };
        }).ToArray();
    }

    /// <inheritdoc/>
    protected override Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        var isHealthy = !string.IsNullOrEmpty(_accessToken);

        return Task.FromResult(new HealthCheckResult(
            IsHealthy: isHealthy,
            Description: isHealthy ? "Rollbar is configured" : "Rollbar access token not configured",
            Data: new Dictionary<string, object>
            {
                ["environment"] = _environment,
                ["codeVersion"] = _codeVersion,
                ["hasAccessToken"] = !string.IsNullOrEmpty(_accessToken)
            }));
    }

    /// <inheritdoc/>

    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        // Configuration validated via Configure method
        IncrementCounter("rollbar.initialized");
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
        IncrementCounter("rollbar.shutdown");
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
