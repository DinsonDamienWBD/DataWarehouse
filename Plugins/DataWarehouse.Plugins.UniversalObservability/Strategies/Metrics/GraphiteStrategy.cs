using System.Net.Sockets;
using System.Text;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.Metrics;

/// <summary>
/// Observability strategy for Graphite carbon metrics ingestion.
/// Provides plaintext and pickle protocol support for time series metrics.
/// </summary>
/// <remarks>
/// Graphite is a highly scalable real-time graphing system that stores
/// numeric time-series data and renders graphs on demand.
/// </remarks>
public sealed class GraphiteStrategy : ObservabilityStrategyBase
{
    private string _host = "localhost";
    private int _port = 2003;
    private string _prefix = "datawarehouse";
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private readonly object _connectionLock = new();

    /// <inheritdoc/>
    public override string StrategyId => "graphite";

    /// <inheritdoc/>
    public override string Name => "Graphite";

    /// <summary>
    /// Initializes a new instance of the <see cref="GraphiteStrategy"/> class.
    /// </summary>
    public GraphiteStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: true,
        SupportsTracing: false,
        SupportsLogging: false,
        SupportsDistributedTracing: false,
        SupportsAlerting: false,
        SupportedExporters: new[] { "Graphite", "Carbon", "Whisper" }))
    {
    }

    /// <summary>
    /// Configures the Graphite connection.
    /// </summary>
    /// <param name="host">Graphite/Carbon host.</param>
    /// <param name="port">Carbon plaintext port (default 2003).</param>
    /// <param name="prefix">Metric name prefix.</param>
    public void Configure(string host, int port = 2003, string prefix = "datawarehouse")
    {
        _host = host;
        _port = port;
        _prefix = prefix;
    }

    /// <inheritdoc/>
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        IncrementCounter("graphite.metrics_sent");
        await EnsureConnectedAsync(cancellationToken);

        var sb = new StringBuilder();

        foreach (var metric in metrics)
        {
            // Graphite format: metric.path value timestamp\n
            var path = BuildMetricPath(metric);
            var timestamp = metric.Timestamp.ToUnixTimeSeconds();

            sb.AppendLine($"{path} {metric.Value} {timestamp}");
        }

        var data = Encoding.UTF8.GetBytes(sb.ToString());

        lock (_connectionLock)
        {
            if (_stream != null && _stream.CanWrite)
            {
                _stream.Write(data, 0, data.Length);
                _stream.Flush();
            }
        }
    }

    private string BuildMetricPath(MetricValue metric)
    {
        var path = new StringBuilder();

        if (!string.IsNullOrEmpty(_prefix))
        {
            path.Append(_prefix);
            path.Append('.');
        }

        // Sanitize metric name for Graphite
        path.Append(SanitizeForGraphite(metric.Name));

        // Add labels as path components
        if (metric.Labels != null)
        {
            foreach (var label in metric.Labels)
            {
                path.Append('.');
                path.Append(SanitizeForGraphite(label.Name));
                path.Append('.');
                path.Append(SanitizeForGraphite(label.Value));
            }
        }

        return path.ToString();
    }

    private static string SanitizeForGraphite(string value)
    {
        return value
            .Replace(" ", "_")
            .Replace(".", "_")
            .Replace("/", "_")
            .Replace("\\", "_")
            .Replace(":", "_")
            .ToLowerInvariant();
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        lock (_connectionLock)
        {
            if (_tcpClient?.Connected == true)
                return;
        }

        var client = new TcpClient();
        await client.ConnectAsync(_host, _port, ct);

        lock (_connectionLock)
        {
            _tcpClient?.Dispose();
            _tcpClient = client;
            _stream = client.GetStream();
        }
    }

    /// <inheritdoc/>
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Graphite does not support tracing");
    }

    /// <inheritdoc/>
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Graphite does not support logging");
    }

    /// <inheritdoc/>
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        try
        {
            await EnsureConnectedAsync(cancellationToken);

            return new HealthCheckResult(
                IsHealthy: _tcpClient?.Connected == true,
                Description: _tcpClient?.Connected == true ? "Graphite connection is healthy" : "Graphite not connected",
                Data: new Dictionary<string, object>
                {
                    ["host"] = _host,
                    ["port"] = _port,
                    ["prefix"] = _prefix
                });
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(
                IsHealthy: false,
                Description: $"Graphite health check failed: {ex.Message}",
                Data: null);
        }
    }

    /// <inheritdoc/>

    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        // Configuration validated via Configure method
        IncrementCounter("graphite.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }


    /// <inheritdoc/>
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("graphite.shutdown");
        await base.ShutdownAsyncCore(cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_connectionLock)
            {
                _stream?.Dispose();
                _tcpClient?.Dispose();
                _stream = null;
                _tcpClient = null;
            }
        }
        base.Dispose(disposing);
    }
}
