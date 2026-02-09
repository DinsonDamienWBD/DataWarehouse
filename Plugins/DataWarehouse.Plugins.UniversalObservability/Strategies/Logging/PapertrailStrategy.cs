using System.Net.Sockets;
using System.Text;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.Logging;

/// <summary>
/// Observability strategy for Papertrail cloud-hosted log management.
/// Provides real-time log aggregation, search, and alerting via syslog.
/// </summary>
/// <remarks>
/// Papertrail is a cloud-hosted log management service that aggregates logs
/// from applications, servers, and infrastructure with powerful search and alerting.
/// </remarks>
public sealed class PapertrailStrategy : ObservabilityStrategyBase
{
    private UdpClient? _udpClient;
    private string _host = "logs.papertrailapp.com";
    private int _port = 514;
    private string _programName = "datawarehouse";
    private string _hostname = Environment.MachineName;

    /// <inheritdoc/>
    public override string StrategyId => "papertrail";

    /// <inheritdoc/>
    public override string Name => "Papertrail";

    /// <summary>
    /// Initializes a new instance of the <see cref="PapertrailStrategy"/> class.
    /// </summary>
    public PapertrailStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: false,
        SupportsTracing: false,
        SupportsLogging: true,
        SupportsDistributedTracing: false,
        SupportsAlerting: true,
        SupportedExporters: new[] { "Syslog", "Papertrail" }))
    {
    }

    /// <summary>
    /// Configures the Papertrail destination.
    /// </summary>
    /// <param name="host">Papertrail syslog host.</param>
    /// <param name="port">Papertrail syslog port.</param>
    /// <param name="programName">Program name for log identification.</param>
    public void Configure(string host, int port, string programName = "datawarehouse")
    {
        _host = host;
        _port = port;
        _programName = programName;
    }

    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        _udpClient = new UdpClient();
        _udpClient.Connect(_host, _port);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Papertrail does not support metrics - use logging instead");
    }

    /// <inheritdoc/>
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Papertrail does not support tracing");
    }

    /// <inheritdoc/>
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        if (_udpClient == null)
        {
            throw new InvalidOperationException("Papertrail client not initialized");
        }

        foreach (var log in logEntries)
        {
            var syslogMessage = FormatSyslogMessage(log);
            var bytes = Encoding.UTF8.GetBytes(syslogMessage);

            await _udpClient.SendAsync(bytes, bytes.Length);
        }
    }

    private string FormatSyslogMessage(LogEntry log)
    {
        // RFC 5424 Syslog format
        var priority = CalculatePriority(log.Level);
        var timestamp = log.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        // <priority>VERSION TIMESTAMP HOSTNAME APP-NAME PROCID MSGID STRUCTURED-DATA MSG
        var message = $"<{priority}>1 {timestamp} {_hostname} {_programName} - - - {log.Message}";

        return message;
    }

    private static int CalculatePriority(LogLevel level)
    {
        // Syslog facility 16 (local use 0) + severity
        var facility = 16;
        var severity = level switch
        {
            LogLevel.Trace => 7,      // Debug
            LogLevel.Debug => 7,      // Debug
            LogLevel.Information => 6, // Informational
            LogLevel.Warning => 4,    // Warning
            LogLevel.Error => 3,      // Error
            LogLevel.Critical => 2,   // Critical
            _ => 6
        };

        return (facility * 8) + severity;
    }

    /// <inheritdoc/>
    protected override Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        var isHealthy = _udpClient != null;

        return Task.FromResult(new HealthCheckResult(
            IsHealthy: isHealthy,
            Description: isHealthy ? "Papertrail connection is healthy" : "Papertrail not initialized",
            Data: new Dictionary<string, object>
            {
                ["host"] = _host,
                ["port"] = _port,
                ["programName"] = _programName,
                ["hostname"] = _hostname
            }));
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _udpClient?.Dispose();
        }
        base.Dispose(disposing);
    }
}
