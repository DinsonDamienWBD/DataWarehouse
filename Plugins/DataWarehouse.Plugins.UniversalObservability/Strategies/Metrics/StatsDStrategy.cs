using System.Net;
using System.Net.Sockets;
using System.Text;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.Metrics;

/// <summary>
/// Observability strategy for StatsD metrics aggregation.
/// Provides UDP-based metrics collection with support for counters, gauges, timers, and sets.
/// </summary>
/// <remarks>
/// StatsD is a network daemon that listens for statistics over UDP and sends
/// aggregates to one or more backend services (Graphite, InfluxDB, etc.).
/// </remarks>
public sealed class StatsDStrategy : ObservabilityStrategyBase
{
    private string _host = "localhost";
    private int _port = 8125;
    private string _prefix = "";
    private UdpClient? _udpClient;
    private IPEndPoint? _endpoint;
    private double _sampleRate = 1.0;

    /// <inheritdoc/>
    public override string StrategyId => "statsd";

    /// <inheritdoc/>
    public override string Name => "StatsD";

    /// <summary>
    /// Initializes a new instance of the <see cref="StatsDStrategy"/> class.
    /// </summary>
    public StatsDStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: true,
        SupportsTracing: false,
        SupportsLogging: false,
        SupportsDistributedTracing: false,
        SupportsAlerting: false,
        SupportedExporters: new[] { "StatsD", "DogStatsD", "Telegraf" }))
    {
    }

    /// <summary>
    /// Configures the StatsD connection.
    /// </summary>
    /// <param name="host">StatsD server host.</param>
    /// <param name="port">StatsD server port (default 8125).</param>
    /// <param name="prefix">Optional metric name prefix.</param>
    /// <param name="sampleRate">Sample rate for metrics (0.0-1.0).</param>
    public void Configure(string host, int port = 8125, string prefix = "", double sampleRate = 1.0)
    {
        _host = host;
        _port = port;
        _prefix = prefix;
        _sampleRate = Math.Clamp(sampleRate, 0.0, 1.0);

        _udpClient?.Dispose();
        _udpClient = new UdpClient();
        _endpoint = new IPEndPoint(Dns.GetHostAddresses(host)[0], port);
    }

    /// <inheritdoc/>
    protected override Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        IncrementCounter("stats_d.metrics_sent");
        EnsureConfigured();

        foreach (var metric in metrics)
        {
            if (_sampleRate < 1.0 && Random.Shared.NextDouble() > _sampleRate)
                continue;

            var statsdMessage = FormatStatsDMessage(metric);
            var data = Encoding.UTF8.GetBytes(statsdMessage);

            _udpClient!.Send(data, data.Length, _endpoint);
        }

        return Task.CompletedTask;
    }

    private string FormatStatsDMessage(MetricValue metric)
    {
        var name = BuildMetricName(metric.Name);

        // Add tags if present (DogStatsD format)
        var tags = "";
        if (metric.Labels != null && metric.Labels.Count > 0)
        {
            tags = "|#" + string.Join(",", metric.Labels.Select(l => $"{SanitizeName(l.Name)}:{SanitizeName(l.Value)}"));
        }

        var statType = metric.Type switch
        {
            MetricType.Counter => "c",
            MetricType.Gauge => "g",
            MetricType.Histogram => "ms", // StatsD timer
            MetricType.Summary => "ms",
            _ => "g"
        };

        var sampleSuffix = _sampleRate < 1.0 ? $"|@{_sampleRate}" : "";

        return $"{name}:{metric.Value}|{statType}{sampleSuffix}{tags}";
    }

    private string BuildMetricName(string name)
    {
        var sanitized = SanitizeName(name);
        return string.IsNullOrEmpty(_prefix) ? sanitized : $"{_prefix}.{sanitized}";
    }

    private static string SanitizeName(string name)
    {
        return name
            .Replace(" ", "_")
            .Replace(":", "_")
            .Replace("|", "_")
            .Replace("@", "_")
            .Replace("#", "_");
    }

    /// <summary>
    /// Increments a counter by 1.
    /// </summary>
    /// <param name="name">Counter name.</param>
    /// <param name="tags">Optional tags.</param>
    public void Increment(string name, IReadOnlyList<MetricLabel>? tags = null)
        => SendStatsDMessage(MetricValue.Counter(name, 1, tags));

    /// <summary>
    /// Decrements a counter by 1.
    /// </summary>
    /// <param name="name">Counter name.</param>
    /// <param name="tags">Optional tags.</param>
    public void Decrement(string name, IReadOnlyList<MetricLabel>? tags = null)
        => SendStatsDMessage(MetricValue.Counter(name, -1, tags));

    /// <summary>
    /// Sets a gauge value.
    /// </summary>
    /// <param name="name">Gauge name.</param>
    /// <param name="value">Gauge value.</param>
    /// <param name="tags">Optional tags.</param>
    public void Gauge(string name, double value, IReadOnlyList<MetricLabel>? tags = null)
        => SendStatsDMessage(MetricValue.Gauge(name, value, tags));

    /// <summary>
    /// Records a timing value.
    /// </summary>
    /// <param name="name">Timer name.</param>
    /// <param name="milliseconds">Duration in milliseconds.</param>
    /// <param name="tags">Optional tags.</param>
    public void Timing(string name, double milliseconds, IReadOnlyList<MetricLabel>? tags = null)
        => SendStatsDMessage(MetricValue.Histogram(name, milliseconds, tags, "milliseconds"));

    /// <summary>
    /// Sends a single StatsD message synchronously (UDP is fire-and-forget).
    /// </summary>
    private void SendStatsDMessage(MetricValue metric)
    {
        EnsureConfigured();
        if (_sampleRate < 1.0 && Random.Shared.NextDouble() > _sampleRate)
            return;
        var statsdMessage = FormatStatsDMessage(metric);
        var data = Encoding.UTF8.GetBytes(statsdMessage);
        try { _udpClient!.Send(data, data.Length, _endpoint); }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("[StatsD] Send error: {0}", ex.Message);
        }
    }

    private void EnsureConfigured()
    {
        if (_udpClient == null || _endpoint == null)
        {
            Configure(_host, _port, _prefix);
        }
    }

    /// <inheritdoc/>
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("StatsD does not support tracing");
    }

    /// <inheritdoc/>
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("StatsD does not support logging");
    }

    /// <inheritdoc/>
    protected override Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        // UDP is fire-and-forget, so we can only verify configuration
        return Task.FromResult(new HealthCheckResult(
            IsHealthy: _udpClient != null,
            Description: _udpClient != null ? "StatsD client configured" : "StatsD client not configured",
            Data: new Dictionary<string, object>
            {
                ["host"] = _host,
                ["port"] = _port,
                ["prefix"] = _prefix,
                ["sampleRate"] = _sampleRate
            }));
    }

    /// <inheritdoc/>

    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        // Configuration validated via Configure method
        IncrementCounter("stats_d.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }


    /// <inheritdoc/>
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("stats_d.shutdown");
        await base.ShutdownAsyncCore(cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _udpClient?.Dispose();
            _udpClient = null;
        }
        base.Dispose(disposing);
    }
}
