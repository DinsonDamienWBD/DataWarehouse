using System.Net.Http;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.Health;

/// <summary>
/// Observability strategy for Zabbix enterprise monitoring.
/// Provides agent-based and agentless monitoring with Zabbix Sender protocol support.
/// </summary>
public sealed class ZabbixStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _apiUrl = "http://localhost/zabbix/api_jsonrpc.php";
    private string _username = "";
    private string _password = "";
    private string _apiToken = "";
    private string _hostname = "";
    private string? _authToken;

    public override string StrategyId => "zabbix";
    public override string Name => "Zabbix";

    public ZabbixStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: true, SupportsTracing: false, SupportsLogging: false,
        SupportsDistributedTracing: false, SupportsAlerting: true,
        SupportedExporters: new[] { "Zabbix", "ZabbixSender", "ZabbixAPI" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _hostname = Environment.MachineName;
    }

    public void Configure(string apiUrl, string username = "", string password = "", string apiToken = "", string hostname = "")
    {
        _apiUrl = apiUrl;
        _username = username;
        _password = password;
        _apiToken = apiToken;
        _hostname = string.IsNullOrEmpty(hostname) ? Environment.MachineName : hostname;
    }

    private async Task EnsureAuthenticatedAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_apiToken))
        {
            _authToken = _apiToken;
            return;
        }

        if (!string.IsNullOrEmpty(_authToken)) return;

        var loginRequest = new
        {
            jsonrpc = "2.0",
            method = "user.login",
            @params = new { user = _username, password = _password },
            id = 1
        };

        var response = await SendApiRequestAsync(loginRequest, ct);
        var result = JsonSerializer.Deserialize<JsonElement>(response);
        _authToken = result.GetProperty("result").GetString();
    }

    private async Task<string> SendApiRequestAsync(object request, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(request);
        var content = new StringContent(json, Encoding.UTF8, "application/json-rpc");
        using var response = await _httpClient.PostAsync(_apiUrl, content, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Sends item values to Zabbix via API.
    /// </summary>
    public async Task SendItemValuesAsync(IEnumerable<(string Key, string Value)> items, CancellationToken ct = default)
    {
        await EnsureAuthenticatedAsync(ct);

        foreach (var item in items)
        {
            var request = new
            {
                jsonrpc = "2.0",
                method = "history.push",
                @params = new
                {
                    host = _hostname,
                    key = item.Key,
                    value = item.Value,
                    clock = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                },
                auth = _authToken,
                id = 1
            };

            await SendApiRequestAsync(request, ct);
        }
    }

    /// <summary>
    /// Gets hosts from Zabbix.
    /// </summary>
    public async Task<string> GetHostsAsync(CancellationToken ct = default)
    {
        await EnsureAuthenticatedAsync(ct);

        var request = new
        {
            jsonrpc = "2.0",
            method = "host.get",
            @params = new { output = new[] { "hostid", "host", "name" } },
            auth = _authToken,
            id = 1
        };

        return await SendApiRequestAsync(request, ct);
    }

    /// <summary>
    /// Gets problems/alerts from Zabbix.
    /// </summary>
    public async Task<string> GetProblemsAsync(string? severity = null, CancellationToken ct = default)
    {
        await EnsureAuthenticatedAsync(ct);

        var @params = new Dictionary<string, object>
        {
            ["output"] = "extend",
            ["recent"] = true,
            ["sortfield"] = new[] { "eventid" },
            ["sortorder"] = "DESC"
        };

        if (!string.IsNullOrEmpty(severity))
        {
            @params["severities"] = new[] { int.Parse(severity) };
        }

        var request = new
        {
            jsonrpc = "2.0",
            method = "problem.get",
            @params,
            auth = _authToken,
            id = 1
        };

        return await SendApiRequestAsync(request, ct);
    }

    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        IncrementCounter("zabbix.metrics_sent");
        var items = metrics.Select(m =>
        {
            var key = $"datawarehouse.{m.Name.Replace(".", "_").Replace("-", "_")}";
            return (key, m.Value.ToString());
        });

        await SendItemValuesAsync(items, cancellationToken);
    }

    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken ct)
        => throw new NotSupportedException("Zabbix does not support tracing");

    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        IncrementCounter("zabbix.logs_sent");
        var items = logEntries.Where(e => e.Level >= LogLevel.Warning).Select(e =>
        {
            var severity = e.Level switch
            {
                LogLevel.Critical => "5",
                LogLevel.Error => "4",
                LogLevel.Warning => "2",
                _ => "1"
            };
            return ($"datawarehouse.log.{e.Level.ToString().ToLowerInvariant()}", severity);
        });

        if (items.Any()) await SendItemValuesAsync(items, cancellationToken);
    }

    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken ct)
    {
        try
        {
            var request = new { jsonrpc = "2.0", method = "apiinfo.version", @params = new { }, id = 1 };
            var response = await SendApiRequestAsync(request, ct);
            var result = JsonSerializer.Deserialize<JsonElement>(response);
            var version = result.GetProperty("result").GetString();

            return new HealthCheckResult(true, $"Zabbix API version: {version}",
                new Dictionary<string, object> { ["url"] = _apiUrl, ["version"] = version ?? "unknown" });
        }
        catch (Exception ex) { return new HealthCheckResult(false, $"Zabbix health check failed: {ex.Message}", null); }
    }


    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_apiUrl) || (!_apiUrl.StartsWith("http://") && !_apiUrl.StartsWith("https://")))
            throw new InvalidOperationException("ZabbixStrategy: Invalid endpoint URL configured.");
        IncrementCounter("zabbix.initialized");
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
        IncrementCounter("zabbix.shutdown");
        await base.ShutdownAsyncCore(cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing) {
                _password = string.Empty; if (disposing) _httpClient.Dispose(); base.Dispose(disposing); }
}
