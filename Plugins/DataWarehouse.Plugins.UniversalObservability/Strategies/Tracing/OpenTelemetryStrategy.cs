using System.Net.Http;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.Tracing;

/// <summary>
/// Observability strategy for OpenTelemetry Protocol (OTLP) supporting metrics, traces, and logs.
/// Provides vendor-neutral observability with standardized APIs and automatic instrumentation.
/// </summary>
/// <remarks>
/// OpenTelemetry is a collection of tools, APIs, and SDKs used to instrument, generate,
/// collect, and export telemetry data (metrics, logs, and traces).
/// </remarks>
public sealed class OpenTelemetryStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _otlpEndpoint = "http://localhost:4318";
    private string _serviceName = "datawarehouse";
    private string _serviceVersion = "1.0.0";
    private Dictionary<string, string> _resourceAttributes = new();

    public override string StrategyId => "opentelemetry";
    public override string Name => "OpenTelemetry";

    public OpenTelemetryStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: true, SupportsTracing: true, SupportsLogging: true,
        SupportsDistributedTracing: true, SupportsAlerting: false,
        SupportedExporters: new[] { "OTLP", "OTLPHttp", "OTLPGrpc" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public void Configure(string otlpEndpoint, string serviceName = "datawarehouse",
        string serviceVersion = "1.0.0", Dictionary<string, string>? resourceAttributes = null)
    {
        _otlpEndpoint = otlpEndpoint.TrimEnd('/');
        _serviceName = serviceName;
        _serviceVersion = serviceVersion;
        _resourceAttributes = resourceAttributes ?? new Dictionary<string, string>();
    }

    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        IncrementCounter("open_telemetry.metrics_sent");
        var otlpMetrics = new
        {
            resourceMetrics = new[]
            {
                new
                {
                    resource = BuildResource(),
                    scopeMetrics = new[]
                    {
                        new
                        {
                            scope = new { name = "datawarehouse.metrics", version = _serviceVersion },
                            metrics = metrics.Select(m => new
                            {
                                name = m.Name,
                                unit = m.Unit ?? "1",
                                description = $"{m.Name} metric",
                                gauge = m.Type == MetricType.Gauge ? new
                                {
                                    dataPoints = new[] { new
                                    {
                                        asDouble = m.Value,
                                        timeUnixNano = ToUnixTimeNanoseconds(m.Timestamp).ToString(),
                                        attributes = m.Labels?.Select(l => new { key = l.Name, value = new { stringValue = l.Value } }).ToArray()
                                    }}
                                } : null,
                                sum = m.Type == MetricType.Counter ? new
                                {
                                    isMonotonic = true,
                                    aggregationTemporality = 2,
                                    dataPoints = new[] { new
                                    {
                                        asDouble = m.Value,
                                        timeUnixNano = ToUnixTimeNanoseconds(m.Timestamp).ToString(),
                                        attributes = m.Labels?.Select(l => new { key = l.Name, value = new { stringValue = l.Value } }).ToArray()
                                    }}
                                } : null
                            }).ToArray()
                        }
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(otlpMetrics);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync($"{_otlpEndpoint}/v1/metrics", content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    protected override async Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        IncrementCounter("open_telemetry.traces_sent");
        var otlpTraces = new
        {
            resourceSpans = new[]
            {
                new
                {
                    resource = BuildResource(),
                    scopeSpans = new[]
                    {
                        new
                        {
                            scope = new { name = "datawarehouse.tracing", version = _serviceVersion },
                            spans = spans.Select(s => new
                            {
                                traceId = Convert.ToBase64String(HexToBytes(s.TraceId.PadLeft(32, '0'))),
                                spanId = Convert.ToBase64String(HexToBytes(s.SpanId.PadLeft(16, '0'))),
                                parentSpanId = s.ParentSpanId != null ? Convert.ToBase64String(HexToBytes(s.ParentSpanId.PadLeft(16, '0'))) : null,
                                name = s.OperationName,
                                kind = (int)s.Kind + 1,
                                startTimeUnixNano = ToUnixTimeNanoseconds(s.StartTime).ToString(),
                                endTimeUnixNano = (ToUnixTimeNanoseconds(s.StartTime) + (long)s.Duration.TotalMilliseconds * 1_000_000).ToString(),
                                attributes = s.Attributes?.Select(a => new { key = a.Key, value = new { stringValue = a.Value?.ToString() ?? "" } }).ToArray(),
                                status = new { code = s.Status == SpanStatus.Error ? 2 : 1 },
                                events = s.Events?.Select(e => new
                                {
                                    name = e.Name,
                                    timeUnixNano = ToUnixTimeNanoseconds(e.Timestamp).ToString()
                                }).ToArray()
                            }).ToArray()
                        }
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(otlpTraces);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync($"{_otlpEndpoint}/v1/traces", content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        IncrementCounter("open_telemetry.logs_sent");
        var otlpLogs = new
        {
            resourceLogs = new[]
            {
                new
                {
                    resource = BuildResource(),
                    scopeLogs = new[]
                    {
                        new
                        {
                            scope = new { name = "datawarehouse.logging", version = _serviceVersion },
                            logRecords = logEntries.Select(e => new
                            {
                                timeUnixNano = ToUnixTimeNanoseconds(e.Timestamp).ToString(),
                                severityNumber = (int)e.Level + 1,
                                severityText = e.Level.ToString().ToUpperInvariant(),
                                body = new { stringValue = e.Message },
                                attributes = e.Properties?.Select(p => new { key = p.Key, value = new { stringValue = p.Value?.ToString() ?? "" } }).ToArray()
                            }).ToArray()
                        }
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(otlpLogs);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync($"{_otlpEndpoint}/v1/logs", content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private object BuildResource()
    {
        var attrs = new List<object>
        {
            new { key = "service.name", value = new { stringValue = _serviceName } },
            new { key = "service.version", value = new { stringValue = _serviceVersion } },
            new { key = "host.name", value = new { stringValue = Environment.MachineName } }
        };

        foreach (var attr in _resourceAttributes)
        {
            attrs.Add(new { key = attr.Key, value = new { stringValue = attr.Value } });
        }

        return new { attributes = attrs };
    }

    private static byte[] HexToBytes(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    private static long ToUnixTimeNanoseconds(DateTimeOffset timestamp)
    {
        return timestamp.ToUnixTimeMilliseconds() * 1_000_000;
    }

    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken ct)
    {
        try
        {
            // Send a test metric to verify connectivity
            var testMetric = MetricValue.Gauge("otlp.health_check", 1);
            await MetricsAsyncCore(new[] { testMetric }, ct);
            return new HealthCheckResult(true, "OpenTelemetry endpoint is healthy",
                new Dictionary<string, object> { ["endpoint"] = _otlpEndpoint, ["service"] = _serviceName });
        }
        catch (Exception ex) { return new HealthCheckResult(false, $"OpenTelemetry health check failed: {ex.Message}", null); }
    }


    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_otlpEndpoint) || (!_otlpEndpoint.StartsWith("http://") && !_otlpEndpoint.StartsWith("https://")))
            throw new InvalidOperationException("OpenTelemetryStrategy: Invalid endpoint URL configured.");
        IncrementCounter("open_telemetry.initialized");
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
        IncrementCounter("open_telemetry.shutdown");
        await base.ShutdownAsyncCore(cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing) { if (disposing) _httpClient.Dispose(); base.Dispose(disposing); }
}
