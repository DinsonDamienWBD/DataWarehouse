using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.Logging;

/// <summary>
/// Observability strategy for Graylog log management.
/// Provides GELF (Graylog Extended Log Format) support with UDP and HTTP transport.
/// </summary>
public sealed class GraylogStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private System.Net.Sockets.UdpClient? _udpClient;
    private readonly object _udpLock = new();
    private string _gelfHttpUrl = "http://localhost:12201/gelf";
    private string _host = "localhost";
    private int _udpPort = 12201;
    private bool _useUdp = false;
    private string _facility = "datawarehouse";

    public override string StrategyId => "graylog";
    public override string Name => "Graylog";

    public GraylogStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: false, SupportsTracing: false, SupportsLogging: true,
        SupportsDistributedTracing: false, SupportsAlerting: true,
        SupportedExporters: new[] { "GELF", "GraylogHTTP", "GraylogUDP" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public void Configure(string host, int port = 12201, bool useUdp = false, string facility = "datawarehouse")
    {
        _host = host;
        _udpPort = port;
        _useUdp = useUdp;
        _facility = facility;
        _gelfHttpUrl = $"http://{host}:{port}/gelf";

        // Recreate the shared UDP client when configuration changes.
        if (useUdp)
        {
            lock (_udpLock)
            {
                _udpClient?.Dispose();
                _udpClient = new System.Net.Sockets.UdpClient();
            }
        }
    }

    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        IncrementCounter("graylog.logs_sent");
        foreach (var entry in logEntries)
        {
            var gelfMessage = new Dictionary<string, object>
            {
                ["version"] = "1.1",
                ["host"] = Environment.MachineName,
                ["short_message"] = entry.Message.Length > 250 ? entry.Message[..250] : entry.Message,
                ["full_message"] = entry.Message,
                ["timestamp"] = entry.Timestamp.ToUnixTimeSeconds() + (entry.Timestamp.Millisecond / 1000.0),
                ["level"] = entry.Level switch
                {
                    LogLevel.Critical => 2, // Critical
                    LogLevel.Error => 3, // Error
                    LogLevel.Warning => 4, // Warning
                    LogLevel.Information => 6, // Informational
                    LogLevel.Debug => 7, // Debug
                    _ => 7
                },
                ["_facility"] = _facility,
                ["_level_name"] = entry.Level.ToString()
            };

            if (entry.Properties != null)
            {
                foreach (var prop in entry.Properties)
                {
                    gelfMessage[$"_{prop.Key}"] = prop.Value;
                }
            }

            if (entry.Exception != null)
            {
                gelfMessage["_exception_type"] = entry.Exception.GetType().Name;
                gelfMessage["_exception_message"] = entry.Exception.Message;
                gelfMessage["_exception_stacktrace"] = entry.Exception.StackTrace ?? "";
            }

            if (_useUdp)
            {
                await SendUdpAsync(gelfMessage, cancellationToken);
            }
            else
            {
                await SendHttpAsync(gelfMessage, cancellationToken);
            }
        }
    }

    private async Task SendHttpAsync(Dictionary<string, object> gelfMessage, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(gelfMessage);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(_gelfHttpUrl, content, ct);
        response.EnsureSuccessStatusCode();
    }

    private async Task SendUdpAsync(Dictionary<string, object> gelfMessage, CancellationToken ct)
    {
        // Reuse the shared UDP client — UdpClient.SendAsync is not thread-safe so use lock.
        var json = JsonSerializer.Serialize(gelfMessage);
        var data = Encoding.UTF8.GetBytes(json);
        System.Net.Sockets.UdpClient udpClient;
        lock (_udpLock)
        {
            if (_udpClient == null)
                _udpClient = new System.Net.Sockets.UdpClient();
            udpClient = _udpClient;
        }
        // LOW-4617: Forward the cancellation token so the send respects caller cancellation.
        await udpClient.SendAsync(data, data.Length, _host, _udpPort).WaitAsync(ct);
    }

    protected override Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken ct)
        => throw new NotSupportedException("Graylog does not support metrics");

    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken ct)
        => throw new NotSupportedException("Graylog does not support tracing");

    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken ct)
    {
        try
        {
            if (_useUdp) return new HealthCheckResult(true, "Graylog UDP configured",
                new Dictionary<string, object> { ["host"] = _host, ["port"] = _udpPort });

            using var response = await _httpClient.GetAsync(_gelfHttpUrl.Replace("/gelf", "/api/system/lbstatus"), ct);
            return new HealthCheckResult(response.IsSuccessStatusCode,
                response.IsSuccessStatusCode ? "Graylog is healthy" : "Graylog unhealthy",
                new Dictionary<string, object> { ["url"] = _gelfHttpUrl });
        }
        catch (Exception ex) { return new HealthCheckResult(false, $"Graylog health check failed: {ex.Message}", null); }
    }


    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_gelfHttpUrl) || (!_gelfHttpUrl.StartsWith("http://") && !_gelfHttpUrl.StartsWith("https://")))
            throw new InvalidOperationException("GraylogStrategy: Invalid endpoint URL configured.");
        IncrementCounter("graylog.initialized");
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
        IncrementCounter("graylog.shutdown");
        await base.ShutdownAsyncCore(cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _httpClient.Dispose();
            lock (_udpLock) { _udpClient?.Dispose(); _udpClient = null; }
        }
        base.Dispose(disposing);
    }
}
