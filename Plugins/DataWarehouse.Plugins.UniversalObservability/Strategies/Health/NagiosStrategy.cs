using System.Net.Http;
using System.Text;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.Health;

/// <summary>
/// Observability strategy for Nagios infrastructure monitoring.
/// Provides host and service monitoring with passive check support via NSCA.
/// </summary>
public sealed class NagiosStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _nagiosUrl = "http://localhost/nagios";
    private string _username = "";
    private string _password = "";
    private string _hostname = "";

    public override string StrategyId => "nagios";
    public override string Name => "Nagios";

    public NagiosStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: true, SupportsTracing: false, SupportsLogging: false,
        SupportsDistributedTracing: false, SupportsAlerting: true,
        SupportedExporters: new[] { "Nagios", "NSCA", "NRPE", "CGI" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _hostname = Environment.MachineName;
    }

    public void Configure(string nagiosUrl, string username, string password, string hostname = "")
    {
        _nagiosUrl = nagiosUrl.TrimEnd('/');
        _username = username;
        _password = password;
        _hostname = string.IsNullOrEmpty(hostname) ? Environment.MachineName : hostname;

        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_username}:{_password}"));
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Remove("Authorization");
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Basic {auth}");
    }

    /// <summary>
    /// Sends a passive check result to Nagios via CGI.
    /// </summary>
    public async Task SendPassiveCheckAsync(string service, NagiosStatus status, string output,
        string? performanceData = null, CancellationToken ct = default)
    {
        var statusCode = (int)status;
        var fullOutput = performanceData != null ? $"{output}|{performanceData}" : output;

        var formData = new Dictionary<string, string>
        {
            ["cmd_typ"] = "30", // PROCESS_SERVICE_CHECK_RESULT
            ["cmd_mod"] = "2",
            ["host"] = _hostname,
            ["service"] = service,
            ["plugin_state"] = statusCode.ToString(),
            ["plugin_output"] = fullOutput,
            ["btnSubmit"] = "Commit"
        };

        var content = new FormUrlEncodedContent(formData);
        using var response = await _httpClient.PostAsync($"{_nagiosUrl}/cgi-bin/cmd.cgi", content, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Sends a host check result to Nagios.
    /// </summary>
    public async Task SendHostCheckAsync(NagiosStatus status, string output, CancellationToken ct = default)
    {
        var formData = new Dictionary<string, string>
        {
            ["cmd_typ"] = "87", // PROCESS_HOST_CHECK_RESULT
            ["cmd_mod"] = "2",
            ["host"] = _hostname,
            ["plugin_state"] = ((int)status).ToString(),
            ["plugin_output"] = output,
            ["btnSubmit"] = "Commit"
        };

        var content = new FormUrlEncodedContent(formData);
        using var response = await _httpClient.PostAsync($"{_nagiosUrl}/cgi-bin/cmd.cgi", content, ct);
        response.EnsureSuccessStatusCode();
    }

    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        IncrementCounter("nagios.metrics_sent");
        foreach (var metric in metrics)
        {
            var status = DetermineStatus(metric);
            var perfData = $"{metric.Name}={metric.Value}{metric.Unit ?? ""}";
            await SendPassiveCheckAsync(metric.Name, status, $"{metric.Name}: {metric.Value}", perfData, cancellationToken);
        }
    }

    private NagiosStatus DetermineStatus(MetricValue metric)
    {
        // Simple threshold-based status (can be customized)
        if (metric.Name.Contains("error", StringComparison.OrdinalIgnoreCase) && metric.Value > 0)
            return NagiosStatus.Critical;
        if (metric.Name.Contains("warning", StringComparison.OrdinalIgnoreCase) && metric.Value > 0)
            return NagiosStatus.Warning;
        return NagiosStatus.Ok;
    }

    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken ct)
        => throw new NotSupportedException("Nagios does not support tracing");

    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        IncrementCounter("nagios.logs_sent");
        foreach (var entry in logEntries.Where(e => e.Level >= LogLevel.Warning))
        {
            var status = entry.Level switch
            {
                LogLevel.Critical => NagiosStatus.Critical,
                LogLevel.Error => NagiosStatus.Critical,
                LogLevel.Warning => NagiosStatus.Warning,
                _ => NagiosStatus.Ok
            };

            await SendPassiveCheckAsync("datawarehouse_logs", status, entry.Message, null, cancellationToken);
        }
    }

    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken ct)
    {
        try
        {
            using var response = await _httpClient.GetAsync($"{_nagiosUrl}/cgi-bin/statusjson.cgi?query=host&hostname={_hostname}", ct);
            return new HealthCheckResult(response.IsSuccessStatusCode,
                response.IsSuccessStatusCode ? "Nagios is accessible" : "Nagios inaccessible",
                new Dictionary<string, object> { ["url"] = _nagiosUrl, ["hostname"] = _hostname });
        }
        catch (Exception ex) { return new HealthCheckResult(false, $"Nagios health check failed: {ex.Message}", null); }
    }


    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_nagiosUrl) || (!_nagiosUrl.StartsWith("http://") && !_nagiosUrl.StartsWith("https://")))
            throw new InvalidOperationException("NagiosStrategy: Invalid endpoint URL configured.");
        IncrementCounter("nagios.initialized");
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
        IncrementCounter("nagios.shutdown");
        await base.ShutdownAsyncCore(cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing) {
                _password = string.Empty; if (disposing) _httpClient.Dispose(); base.Dispose(disposing); }

    public enum NagiosStatus { Ok = 0, Warning = 1, Critical = 2, Unknown = 3 }
}
