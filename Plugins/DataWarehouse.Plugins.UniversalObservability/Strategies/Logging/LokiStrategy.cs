using System.Net.Http;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.Logging;

/// <summary>
/// Observability strategy for Grafana Loki log aggregation system.
/// Provides horizontally-scalable, cost-effective log storage with Prometheus-like query language (LogQL).
/// </summary>
/// <remarks>
/// Loki is a horizontally scalable, highly available, multi-tenant log aggregation system
/// inspired by Prometheus. It is designed to be very cost effective and easy to operate.
/// </remarks>
public sealed class LokiStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _url = "http://localhost:3100";
    private string _tenant = "";
    private Dictionary<string, string> _staticLabels = new() { ["application"] = "datawarehouse" };

    /// <inheritdoc/>
    public override string StrategyId => "loki";

    /// <inheritdoc/>
    public override string Name => "Grafana Loki";

    /// <summary>
    /// Initializes a new instance of the <see cref="LokiStrategy"/> class.
    /// </summary>
    public LokiStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: false,
        SupportsTracing: false,
        SupportsLogging: true,
        SupportsDistributedTracing: false,
        SupportsAlerting: true,
        SupportedExporters: new[] { "Loki", "LogQL", "Promtail" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Configures the Loki connection.
    /// </summary>
    /// <param name="url">Loki server URL.</param>
    /// <param name="tenant">Tenant ID for multi-tenant mode.</param>
    /// <param name="staticLabels">Static labels to add to all log entries.</param>
    public void Configure(string url, string tenant = "", Dictionary<string, string>? staticLabels = null)
    {
        _url = url.TrimEnd('/');
        _tenant = tenant;
        _staticLabels = staticLabels ?? new Dictionary<string, string> { ["application"] = "datawarehouse" };

        _httpClient.DefaultRequestHeaders.Clear();
        if (!string.IsNullOrEmpty(_tenant))
        {
            _httpClient.DefaultRequestHeaders.Add("X-Scope-OrgID", _tenant);
        }
    }

    /// <inheritdoc/>
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        // Group entries by label set (Loki streams are defined by unique label combinations)
        var streams = new Dictionary<string, List<(long, string)>>();

        foreach (var entry in logEntries)
        {
            var labels = new Dictionary<string, string>(_staticLabels)
            {
                ["level"] = entry.Level.ToString().ToLowerInvariant(),
                ["host"] = Environment.MachineName
            };

            // Add properties as labels (be careful with high cardinality)
            if (entry.Properties != null)
            {
                foreach (var prop in entry.Properties.Take(5)) // Limit to avoid cardinality explosion
                {
                    if (prop.Value is string strValue && strValue.Length < 50)
                    {
                        labels[SanitizeLabelName(prop.Key)] = strValue;
                    }
                }
            }

            var labelKey = FormatLabels(labels);
            var timestamp = entry.Timestamp.ToUnixTimeMilliseconds() * 1_000_000;
            var logLine = FormatLogLine(entry);

            if (!streams.ContainsKey(labelKey))
            {
                streams[labelKey] = new List<(long, string)>();
            }
            streams[labelKey].Add((timestamp, logLine));
        }

        // Build Loki push format
        var lokiStreams = streams.Select(kvp => new
        {
            stream = ParseLabels(kvp.Key),
            values = kvp.Value.Select(v => new[] { v.Item1.ToString(), v.Item2 }).ToArray()
        }).ToArray();

        var payload = JsonSerializer.Serialize(new { streams = lokiStreams });
        var content = new StringContent(payload, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync($"{_url}/loki/api/v1/push", content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Queries logs using LogQL.
    /// </summary>
    /// <param name="query">LogQL query string.</param>
    /// <param name="start">Start time.</param>
    /// <param name="end">End time.</param>
    /// <param name="limit">Maximum number of log lines.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Query results as JSON.</returns>
    public async Task<string> QueryAsync(string query, DateTimeOffset start, DateTimeOffset end,
        int limit = 1000, CancellationToken ct = default)
    {
        var queryParams = $"query={Uri.EscapeDataString(query)}" +
                         $"&start={start.ToUnixTimeMilliseconds() * 1_000_000}" +
                         $"&end={end.ToUnixTimeMilliseconds() * 1_000_000}" +
                         $"&limit={limit}";

        var response = await _httpClient.GetAsync($"{_url}/loki/api/v1/query_range?{queryParams}", ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Queries logs by label selector.
    /// </summary>
    /// <param name="labelSelector">Label selector (e.g., {application="datawarehouse"}).</param>
    /// <param name="filter">Optional line filter (e.g., |= "error").</param>
    /// <param name="lookback">Time range to look back.</param>
    /// <param name="limit">Maximum number of log lines.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Query results as JSON.</returns>
    public Task<string> QueryByLabelAsync(string labelSelector, string? filter = null,
        TimeSpan? lookback = null, int limit = 1000, CancellationToken ct = default)
    {
        lookback ??= TimeSpan.FromHours(1);
        var query = filter != null ? $"{labelSelector} {filter}" : labelSelector;
        var end = DateTimeOffset.UtcNow;
        var start = end - lookback.Value;

        return QueryAsync(query, start, end, limit, ct);
    }

    /// <summary>
    /// Gets available label names.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of label names.</returns>
    public async Task<string[]> GetLabelNamesAsync(CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync($"{_url}/loki/api/v1/labels", ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<JsonElement>(json);

        if (result.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            return data.EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
        }

        return Array.Empty<string>();
    }

    private string FormatLogLine(LogEntry entry)
    {
        var sb = new StringBuilder();
        sb.Append(entry.Message);

        if (entry.Exception != null)
        {
            sb.Append($" | Exception: {entry.Exception.GetType().Name}: {entry.Exception.Message}");
            if (!string.IsNullOrEmpty(entry.Exception.StackTrace))
            {
                sb.Append($" | StackTrace: {entry.Exception.StackTrace.Replace('\n', ' ')}");
            }
        }

        return sb.ToString();
    }

    private static string FormatLabels(Dictionary<string, string> labels)
    {
        var pairs = labels.OrderBy(l => l.Key).Select(l => $"{l.Key}=\"{EscapeLabelValue(l.Value)}\"");
        return "{" + string.Join(",", pairs) + "}";
    }

    private static Dictionary<string, string> ParseLabels(string labelString)
    {
        var result = new Dictionary<string, string>();
        var content = labelString.TrimStart('{').TrimEnd('}');

        foreach (var pair in content.Split(','))
        {
            var parts = pair.Split('=');
            if (parts.Length == 2)
            {
                result[parts[0]] = parts[1].Trim('"');
            }
        }

        return result;
    }

    private static string SanitizeLabelName(string name)
    {
        return name.Replace("-", "_").Replace(".", "_").Replace(" ", "_").ToLowerInvariant();
    }

    private static string EscapeLabelValue(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
    }

    /// <inheritdoc/>
    protected override Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Loki does not support metrics - use Prometheus/Mimir");
    }

    /// <inheritdoc/>
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Loki does not support tracing - use Tempo");
    }

    /// <inheritdoc/>
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_url}/ready", cancellationToken);

            return new HealthCheckResult(
                IsHealthy: response.IsSuccessStatusCode,
                Description: response.IsSuccessStatusCode ? "Loki is ready" : "Loki not ready",
                Data: new Dictionary<string, object>
                {
                    ["url"] = _url,
                    ["tenant"] = _tenant,
                    ["staticLabels"] = _staticLabels.Count
                });
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(
                IsHealthy: false,
                Description: $"Loki health check failed: {ex.Message}",
                Data: null);
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _httpClient.Dispose();
        }
        base.Dispose(disposing);
    }
}
