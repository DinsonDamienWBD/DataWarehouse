// 91.H: RAID Monitoring - Dashboard, Metrics, Prometheus, Grafana, CLI, REST API, Audit
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateRAID.Features;

/// <summary>
/// 91.H: RAID Monitoring - Real-time dashboard, historical metrics, Prometheus exporter,
/// Grafana templates, CLI commands, REST API, GUI integration, and audit logging.
/// </summary>
public sealed class RaidMonitoring
{
    private readonly RealTimeDashboard _dashboard;
    private readonly HistoricalMetrics _historicalMetrics;
    private readonly PrometheusExporter _prometheusExporter;
    private readonly GrafanaTemplates _grafanaTemplates;
    private readonly RaidCliCommands _cliCommands;
    private readonly RaidRestApi _restApi;
    private readonly ScheduledOperations _scheduledOps;
    private readonly AuditLogger _auditLogger;
    private readonly ComplianceReporter _complianceReporter;
    private readonly IntegrityProof _integrityProof;

    public RaidMonitoring()
    {
        _dashboard = new RealTimeDashboard();
        _historicalMetrics = new HistoricalMetrics();
        _prometheusExporter = new PrometheusExporter();
        _grafanaTemplates = new GrafanaTemplates();
        _cliCommands = new RaidCliCommands();
        _restApi = new RaidRestApi();
        _scheduledOps = new ScheduledOperations();
        _auditLogger = new AuditLogger();
        _complianceReporter = new ComplianceReporter(_auditLogger);
        _integrityProof = new IntegrityProof();
    }

    // 91.H1.1: Real-Time Dashboard
    public RealTimeDashboard Dashboard => _dashboard;

    // 91.H1.2: Historical Metrics
    public HistoricalMetrics Metrics => _historicalMetrics;

    // 91.H1.3: Prometheus Exporter
    public PrometheusExporter Prometheus => _prometheusExporter;

    // 91.H1.4: Grafana Templates
    public GrafanaTemplates Grafana => _grafanaTemplates;

    // 91.H2.1: CLI Commands
    public RaidCliCommands Cli => _cliCommands;

    // 91.H2.2: REST API
    public RaidRestApi RestApi => _restApi;

    // 91.H2.4: Scheduled Operations
    public ScheduledOperations ScheduledOps => _scheduledOps;

    // 91.H3.1: Audit Logger
    public AuditLogger Audit => _auditLogger;

    // 91.H3.2: Compliance Reporter
    public ComplianceReporter Compliance => _complianceReporter;

    // 91.H3.3: Integrity Proof
    public IntegrityProof Integrity => _integrityProof;
}

/// <summary>
/// 91.H1.1: Real-Time Dashboard - Live array status and metrics.
/// </summary>
public sealed class RealTimeDashboard
{
    private readonly BoundedDictionary<string, ArrayStatus> _arrayStatuses = new BoundedDictionary<string, ArrayStatus>(1000);
    private readonly BoundedDictionary<string, List<MetricDataPoint>> _liveMetrics = new BoundedDictionary<string, List<MetricDataPoint>>(1000);

    public void UpdateArrayStatus(string arrayId, ArrayStatus status)
    {
        status.LastUpdate = DateTime.UtcNow;
        _arrayStatuses[arrayId] = status;
    }

    public void RecordMetric(string arrayId, string metricName, double value)
    {
        var key = $"{arrayId}:{metricName}";
        var list = _liveMetrics.GetOrAdd(key, _ => new List<MetricDataPoint>());

        lock (list)
        {
            list.Add(new MetricDataPoint { Timestamp = DateTime.UtcNow, Value = value });

            // Keep only last 5 minutes of data
            var cutoff = DateTime.UtcNow.AddMinutes(-5);
            list.RemoveAll(p => p.Timestamp < cutoff);
        }
    }

    public DashboardData GetDashboardData()
    {
        return new DashboardData
        {
            Timestamp = DateTime.UtcNow,
            ArrayStatuses = _arrayStatuses.Values.ToList(),
            TotalArrays = _arrayStatuses.Count,
            HealthyArrays = _arrayStatuses.Values.Count(s => s.Health == HealthState.Healthy),
            DegradedArrays = _arrayStatuses.Values.Count(s => s.Health == HealthState.Degraded),
            CriticalArrays = _arrayStatuses.Values.Count(s => s.Health == HealthState.Critical)
        };
    }

    public IReadOnlyList<MetricDataPoint> GetLiveMetrics(string arrayId, string metricName)
    {
        var key = $"{arrayId}:{metricName}";
        return _liveMetrics.TryGetValue(key, out var list) ? list.ToList() : new List<MetricDataPoint>();
    }
}

/// <summary>
/// 91.H1.2: Historical Metrics - Store and query historical data.
/// </summary>
public sealed class HistoricalMetrics
{
    private readonly BoundedDictionary<string, List<HistoricalDataPoint>> _history = new BoundedDictionary<string, List<HistoricalDataPoint>>(1000);

    public void RecordMetric(string arrayId, string metricName, double value, Dictionary<string, string>? labels = null)
    {
        var key = $"{arrayId}:{metricName}";
        var list = _history.GetOrAdd(key, _ => new List<HistoricalDataPoint>());

        lock (list)
        {
            list.Add(new HistoricalDataPoint
            {
                Timestamp = DateTime.UtcNow,
                Value = value,
                Labels = labels ?? new Dictionary<string, string>()
            });
        }
    }

    public QueryResult Query(MetricQuery query)
    {
        var result = new QueryResult { Query = query };
        var key = $"{query.ArrayId}:{query.MetricName}";

        if (!_history.TryGetValue(key, out var list))
            return result;

        lock (list)
        {
            var filtered = list
                .Where(p => p.Timestamp >= query.StartTime && p.Timestamp <= query.EndTime)
                .ToList();

            result.DataPoints = filtered;
            result.Count = filtered.Count;

            if (filtered.Count > 0)
            {
                result.Min = filtered.Min(p => p.Value);
                result.Max = filtered.Max(p => p.Value);
                result.Avg = filtered.Average(p => p.Value);
            }
        }

        return result;
    }

    public void Compact(TimeSpan olderThan)
    {
        var cutoff = DateTime.UtcNow - olderThan;

        foreach (var list in _history.Values)
        {
            lock (list)
            {
                list.RemoveAll(p => p.Timestamp < cutoff);
            }
        }
    }
}

/// <summary>
/// 91.H1.3: Prometheus Exporter - Export metrics to Prometheus.
/// </summary>
public sealed class PrometheusExporter
{
    private readonly BoundedDictionary<string, PrometheusMetric> _metrics = new BoundedDictionary<string, PrometheusMetric>(1000);

    public void RegisterMetric(string name, MetricType type, string help, string[] labels)
    {
        _metrics[name] = new PrometheusMetric
        {
            Name = name,
            Type = type,
            Help = help,
            Labels = labels
        };
    }

    public void SetGauge(string name, double value, Dictionary<string, string>? labels = null)
    {
        if (_metrics.TryGetValue(name, out var metric))
        {
            var labelKey = labels != null ? string.Join(",", labels.Select(kv => $"{kv.Key}={kv.Value}")) : "";
            metric.Values[labelKey] = value;
            metric.LastUpdate = DateTime.UtcNow;
        }
    }

    public void IncrementCounter(string name, double value = 1, Dictionary<string, string>? labels = null)
    {
        if (_metrics.TryGetValue(name, out var metric))
        {
            var labelKey = labels != null ? string.Join(",", labels.Select(kv => $"{kv.Key}={kv.Value}")) : "";
            metric.Values.AddOrUpdate(labelKey, value, (_, v) => v + value);
            metric.LastUpdate = DateTime.UtcNow;
        }
    }

    public string ExportMetrics()
    {
        var sb = new StringBuilder();

        foreach (var metric in _metrics.Values)
        {
            sb.AppendLine($"# HELP {metric.Name} {metric.Help}");
            sb.AppendLine($"# TYPE {metric.Name} {metric.Type.ToString().ToLower()}");

            foreach (var value in metric.Values)
            {
                var labels = string.IsNullOrEmpty(value.Key) ? "" : $"{{{value.Key}}}";
                sb.AppendLine($"{metric.Name}{labels} {value.Value}");
            }
        }

        return sb.ToString();
    }

    public void InitializeRaidMetrics()
    {
        RegisterMetric("raid_array_health", MetricType.Gauge, "RAID array health status", new[] { "array_id" });
        RegisterMetric("raid_disk_status", MetricType.Gauge, "Disk health status", new[] { "array_id", "disk_id" });
        RegisterMetric("raid_read_iops", MetricType.Counter, "Read IOPS", new[] { "array_id" });
        RegisterMetric("raid_write_iops", MetricType.Counter, "Write IOPS", new[] { "array_id" });
        RegisterMetric("raid_read_bytes", MetricType.Counter, "Bytes read", new[] { "array_id" });
        RegisterMetric("raid_write_bytes", MetricType.Counter, "Bytes written", new[] { "array_id" });
        RegisterMetric("raid_rebuild_progress", MetricType.Gauge, "Rebuild progress", new[] { "array_id" });
        RegisterMetric("raid_capacity_used", MetricType.Gauge, "Used capacity bytes", new[] { "array_id" });
        RegisterMetric("raid_capacity_total", MetricType.Gauge, "Total capacity bytes", new[] { "array_id" });
    }
}

/// <summary>
/// 91.H1.4: Grafana Templates - Pre-built Grafana dashboards.
/// </summary>
public sealed class GrafanaTemplates
{
    public string GetOverviewDashboard()
    {
        return JsonSerializer.Serialize(new
        {
            title = "RAID Overview",
            uid = "raid-overview",
            panels = new[]
            {
                new { id = 1, title = "Array Health", type = "stat", gridPos = new { x = 0, y = 0, w = 6, h = 4 } },
                new { id = 2, title = "Total Capacity", type = "stat", gridPos = new { x = 6, y = 0, w = 6, h = 4 } },
                new { id = 3, title = "IOPS", type = "graph", gridPos = new { x = 0, y = 4, w = 12, h = 8 } },
                new { id = 4, title = "Throughput", type = "graph", gridPos = new { x = 12, y = 4, w = 12, h = 8 } },
                new { id = 5, title = "Disk Status", type = "table", gridPos = new { x = 0, y = 12, w = 24, h = 8 } }
            }
        });
    }

    public string GetArrayDetailDashboard()
    {
        return JsonSerializer.Serialize(new
        {
            title = "RAID Array Detail",
            uid = "raid-array-detail",
            templating = new { list = new[] { new { name = "array_id", type = "query" } } },
            panels = new[]
            {
                new { id = 1, title = "Health Status", type = "stat" },
                new { id = 2, title = "Rebuild Progress", type = "gauge" },
                new { id = 3, title = "Read/Write Latency", type = "graph" },
                new { id = 4, title = "Disk Temperature", type = "heatmap" },
                new { id = 5, title = "Error Rates", type = "graph" }
            }
        });
    }

    public string GetAlertRules()
    {
        return JsonSerializer.Serialize(new[]
        {
            new { name = "Array Degraded", condition = "raid_array_health < 2", severity = "warning" },
            new { name = "Array Critical", condition = "raid_array_health < 1", severity = "critical" },
            new { name = "Disk Failed", condition = "raid_disk_status == 0", severity = "critical" },
            new { name = "High Latency", condition = "raid_read_latency_ms > 100", severity = "warning" },
            new { name = "Rebuild Stalled", condition = "rate(raid_rebuild_progress[5m]) == 0", severity = "warning" }
        });
    }
}

/// <summary>
/// Provides read-only access to live RAID array state for the CLI and REST API.
/// </summary>
public interface IRaidStateProvider
{
    IReadOnlyList<ArraySummary> GetArraySummaries();
    ArraySummary? GetArray(string arrayId);
    ArrayMetricsSummary GetMetrics();
}

/// <summary>Summary of a RAID array for CLI/REST responses.</summary>
public sealed class ArraySummary
{
    public string ArrayId { get; set; } = string.Empty;
    public string RaidLevel { get; set; } = string.Empty;
    public string HealthStatus { get; set; } = string.Empty;
    public int TotalDisks { get; set; }
    public int HealthyDisks { get; set; }
    public long CapacityBytes { get; set; }
    public long UsedBytes { get; set; }
}

/// <summary>Aggregate metrics snapshot for CLI/REST responses.</summary>
public sealed class ArrayMetricsSummary
{
    public double ReadIops { get; set; }
    public double WriteIops { get; set; }
    public double ThroughputReadMBps { get; set; }
    public double ThroughputWriteMBps { get; set; }
    public double LatencyReadMs { get; set; }
    public double LatencyWriteMs { get; set; }
}

/// <summary>
/// 91.H2.1: CLI Commands - Comprehensive RAID CLI.
/// Uses <see cref="IRaidStateProvider"/> to report actual array state rather than fabricated values.
/// </summary>
public sealed class RaidCliCommands
{
    private readonly IRaidStateProvider? _stateProvider;

    public RaidCliCommands() { }

    public RaidCliCommands(IRaidStateProvider stateProvider)
    {
        _stateProvider = stateProvider;
    }

    public CliResult Execute(string command, string[] args)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return new CliResult { Success = false, Output = "No command specified" };

        return parts[0].ToLower() switch
        {
            "status" => GetStatus(args),
            "list" => ListArrays(args),
            "create" => CreateArray(args),
            "delete" => DeleteArray(args),
            "rebuild" => StartRebuild(args),
            "scrub" => StartScrub(args),
            "add-disk" => AddDisk(args),
            "remove-disk" => RemoveDisk(args),
            "replace-disk" => ReplaceDisk(args),
            "health" => GetHealth(args),
            "metrics" => GetMetrics(args),
            "help" => ShowHelp(args),
            _ => new CliResult { Success = false, Output = $"Unknown command: {parts[0]}" }
        };
    }

    private CliResult GetStatus(string[] args)
    {
        var arrayId = args.FirstOrDefault();
        if (_stateProvider != null)
        {
            if (arrayId != null && arrayId != "all")
            {
                var arr = _stateProvider.GetArray(arrayId);
                if (arr == null)
                    return new CliResult { Success = false, Output = $"Array not found: {arrayId}" };
                return new CliResult
                {
                    Success = true,
                    Output = $"Array: {arr.ArrayId}\nStatus: {arr.HealthStatus}\nLevel: {arr.RaidLevel}\n" +
                             $"Disks: {arr.HealthyDisks}/{arr.TotalDisks}\n" +
                             $"Capacity: {arr.CapacityBytes / 1024 / 1024 / 1024} GB\n" +
                             $"Used: {arr.UsedBytes / 1024 / 1024 / 1024} GB " +
                             $"({(arr.CapacityBytes > 0 ? arr.UsedBytes * 100 / arr.CapacityBytes : 0)}%)"
                };
            }

            var summaries = _stateProvider.GetArraySummaries();
            if (summaries.Count == 0)
                return new CliResult { Success = true, Output = "No arrays configured." };
            var sb = new StringBuilder();
            foreach (var a in summaries)
                sb.AppendLine($"  {a.ArrayId} ({a.RaidLevel}, {a.HealthStatus}, {a.HealthyDisks}/{a.TotalDisks} disks)");
            return new CliResult { Success = true, Output = sb.ToString().TrimEnd() };
        }
        return new CliResult { Success = false, Output = "No state provider configured. Wire IRaidStateProvider to enable live status." };
    }

    private CliResult ListArrays(string[] args)
    {
        if (_stateProvider != null)
        {
            var arrays = _stateProvider.GetArraySummaries();
            if (arrays.Count == 0)
                return new CliResult { Success = true, Output = "No arrays configured." };
            var sb = new StringBuilder("Arrays:\n");
            foreach (var a in arrays)
                sb.AppendLine($"  {a.ArrayId} ({a.RaidLevel}, {a.HealthStatus})");
            return new CliResult { Success = true, Output = sb.ToString().TrimEnd() };
        }
        return new CliResult { Success = false, Output = "No state provider configured." };
    }

    private CliResult CreateArray(string[] args) =>
        new() { Success = true, Output = $"Array creation requested: {args.FirstOrDefault() ?? "new-array"}. Initiate via RaidConfiguration API." };

    private CliResult DeleteArray(string[] args) =>
        new() { Success = true, Output = $"Array deletion requested: {args.FirstOrDefault()}. Confirm via RaidConfiguration API." };

    private CliResult StartRebuild(string[] args) =>
        new() { Success = true, Output = "Rebuild requested. Initiate via IRaidStrategy.RebuildAsync()." };

    private CliResult StartScrub(string[] args) =>
        new() { Success = true, Output = "Scrub requested. Initiate via IRaidStrategy.ScrubAsync()." };

    private CliResult AddDisk(string[] args) =>
        new() { Success = true, Output = "Disk add requested. Initiate via IRaidStrategy.AddDiskAsync()." };

    private CliResult RemoveDisk(string[] args) =>
        new() { Success = true, Output = "Disk remove requested. Initiate via IRaidStrategy.RemoveDiskAsync()." };

    private CliResult ReplaceDisk(string[] args) =>
        new() { Success = true, Output = "Disk replacement requested. Initiate via IRaidStrategy.ReplaceDiskAsync()." };

    private CliResult GetHealth(string[] args)
    {
        if (_stateProvider != null)
        {
            var arrays = _stateProvider.GetArraySummaries();
            var degraded = arrays.Where(a => a.HealthStatus != "Healthy").ToList();
            return degraded.Count == 0
                ? new CliResult { Success = true, Output = $"All {arrays.Count} array(s) healthy." }
                : new CliResult { Success = true, Output = $"{degraded.Count} array(s) degraded: {string.Join(", ", degraded.Select(a => $"{a.ArrayId}({a.HealthStatus})"))}" };
        }
        return new CliResult { Success = false, Output = "No state provider configured." };
    }

    private CliResult GetMetrics(string[] args)
    {
        if (_stateProvider != null)
        {
            var m = _stateProvider.GetMetrics();
            return new CliResult
            {
                Success = true,
                Output = $"Read IOPS: {m.ReadIops:F0}\nWrite IOPS: {m.WriteIops:F0}\n" +
                         $"Read Throughput: {m.ThroughputReadMBps:F1} MB/s\nWrite Throughput: {m.ThroughputWriteMBps:F1} MB/s\n" +
                         $"Read Latency: {m.LatencyReadMs:F2} ms\nWrite Latency: {m.LatencyWriteMs:F2} ms"
            };
        }
        return new CliResult { Success = false, Output = "No state provider configured." };
    }

    private CliResult ShowHelp(string[] args) =>
        new() { Success = true, Output = "Commands: status, list, create, delete, rebuild, scrub, add-disk, remove-disk, replace-disk, health, metrics, help" };
}

/// <summary>
/// 91.H2.2: REST API - RAID management via REST.
/// Uses <see cref="IRaidStateProvider"/> to return live array state.
/// </summary>
public sealed class RaidRestApi
{
    private readonly IRaidStateProvider? _stateProvider;

    public RaidRestApi() { }

    public RaidRestApi(IRaidStateProvider stateProvider)
    {
        _stateProvider = stateProvider;
    }

    public ApiResponse HandleRequest(string method, string path, string? body = null)
    {
        return (method.ToUpper(), path.ToLower()) switch
        {
            ("GET", "/api/v1/arrays") => GetArrays(),
            ("GET", var p) when p.StartsWith("/api/v1/arrays/") => GetArray(p.Split('/').Last()),
            ("POST", "/api/v1/arrays") => CreateArray(body),
            ("DELETE", var p) when p.StartsWith("/api/v1/arrays/") => DeleteArray(p.Split('/').Last()),
            ("POST", var p) when p.Contains("/rebuild") => StartRebuild(p),
            ("POST", var p) when p.Contains("/scrub") => StartScrub(p),
            ("GET", "/api/v1/metrics") => GetMetrics(),
            ("GET", "/api/v1/health") => GetHealth(),
            _ => new ApiResponse { StatusCode = 404, Body = JsonSerializer.Serialize(new { error = "Not found" }) }
        };
    }

    private ApiResponse GetArrays()
    {
        if (_stateProvider == null)
            return new ApiResponse { StatusCode = 503, Body = JsonSerializer.Serialize(new { error = "State provider not configured" }) };
        var arrays = _stateProvider.GetArraySummaries().Select(a => new { a.ArrayId, a.RaidLevel, a.HealthStatus, a.TotalDisks, a.HealthyDisks });
        return new() { StatusCode = 200, Body = JsonSerializer.Serialize(new { arrays }) };
    }

    private ApiResponse GetArray(string id)
    {
        if (_stateProvider == null)
            return new ApiResponse { StatusCode = 503, Body = JsonSerializer.Serialize(new { error = "State provider not configured" }) };
        var arr = _stateProvider.GetArray(id);
        if (arr == null)
            return new ApiResponse { StatusCode = 404, Body = JsonSerializer.Serialize(new { error = $"Array '{id}' not found" }) };
        return new() { StatusCode = 200, Body = JsonSerializer.Serialize(new { arr.ArrayId, arr.RaidLevel, arr.HealthStatus, arr.TotalDisks, arr.HealthyDisks, arr.CapacityBytes, arr.UsedBytes }) };
    }

    private ApiResponse CreateArray(string? body) =>
        new() { StatusCode = 202, Body = JsonSerializer.Serialize(new { status = "accepted", message = "Array creation must be initiated via IRaidStrategy.InitializeAsync()" }) };

    private ApiResponse DeleteArray(string id) =>
        new() { StatusCode = 202, Body = JsonSerializer.Serialize(new { status = "accepted", message = $"Array '{id}' deletion must be confirmed via management API" }) };

    private ApiResponse StartRebuild(string path) =>
        new() { StatusCode = 202, Body = JsonSerializer.Serialize(new { status = "accepted", message = "Rebuild must be initiated via IRaidStrategy.RebuildAsync()" }) };

    private ApiResponse StartScrub(string path) =>
        new() { StatusCode = 202, Body = JsonSerializer.Serialize(new { status = "accepted", message = "Scrub must be initiated via IRaidStrategy.ScrubAsync()" }) };

    private ApiResponse GetMetrics()
    {
        if (_stateProvider == null)
            return new ApiResponse { StatusCode = 503, Body = JsonSerializer.Serialize(new { error = "State provider not configured" }) };
        var m = _stateProvider.GetMetrics();
        return new() { StatusCode = 200, Body = JsonSerializer.Serialize(new { read_iops = m.ReadIops, write_iops = m.WriteIops, read_throughput_mbps = m.ThroughputReadMBps, write_throughput_mbps = m.ThroughputWriteMBps, read_latency_ms = m.LatencyReadMs, write_latency_ms = m.LatencyWriteMs }) };
    }

    private ApiResponse GetHealth()
    {
        if (_stateProvider == null)
            return new ApiResponse { StatusCode = 503, Body = JsonSerializer.Serialize(new { error = "State provider not configured" }) };
        var arrays = _stateProvider.GetArraySummaries();
        var degraded = arrays.Count(a => a.HealthStatus != "Healthy");
        var status = degraded == 0 ? "healthy" : "degraded";
        return new() { StatusCode = 200, Body = JsonSerializer.Serialize(new { status, total_arrays = arrays.Count, degraded_arrays = degraded }) };
    }
}

/// <summary>
/// 91.H2.4: Scheduled Operations - Schedule scrubs, maintenance windows.
/// </summary>
public sealed class ScheduledOperations
{
    private readonly BoundedDictionary<string, ScheduledOperation> _operations = new BoundedDictionary<string, ScheduledOperation>(1000);

    public ScheduledOperation ScheduleOperation(
        string name,
        OperationType type,
        string cronExpression,
        string? arrayId = null)
    {
        var op = new ScheduledOperation
        {
            OperationId = Guid.NewGuid().ToString(),
            Name = name,
            Type = type,
            CronExpression = cronExpression,
            ArrayId = arrayId,
            IsEnabled = true,
            CreatedTime = DateTime.UtcNow,
            NextRunTime = CalculateNextRun(cronExpression)
        };

        _operations[op.OperationId] = op;
        return op;
    }

    public IReadOnlyList<ScheduledOperation> GetScheduledOperations() =>
        _operations.Values.ToList();

    public void EnableOperation(string operationId)
    {
        if (_operations.TryGetValue(operationId, out var op))
            op.IsEnabled = true;
    }

    public void DisableOperation(string operationId)
    {
        if (_operations.TryGetValue(operationId, out var op))
            op.IsEnabled = false;
    }

    private DateTime CalculateNextRun(string cronExpression)
    {
        // Parse a 5-field cron expression (minute hour dom month dow).
        // Supports numeric values and '*' wildcards.
        var now = DateTime.UtcNow;
        var parts = cronExpression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5)
        {
            // Malformed cron - default to 1 hour from now.
            return now.AddHours(1);
        }

        int ParseField(string field, int current, int min, int max)
        {
            if (field == "*") return current;
            if (int.TryParse(field, out var v) && v >= min && v <= max) return v;
            return current;
        }

        var minute = ParseField(parts[0], now.Minute, 0, 59);
        var hour = ParseField(parts[1], now.Hour, 0, 23);
        var dom = ParseField(parts[2], now.Day, 1, 31);

        // Build the next candidate run time.
        var candidate = new DateTime(now.Year, now.Month,
            Math.Min(dom, DateTime.DaysInMonth(now.Year, now.Month)),
            hour, minute, 0, DateTimeKind.Utc);

        // Advance by one period (day if dom-based, otherwise minute-granularity).
        if (candidate <= now)
        {
            if (parts[2] == "*" && parts[1] != "*")
                candidate = candidate.AddDays(1);  // Daily job: next day same time
            else if (parts[0] != "*" && parts[1] == "*")
                candidate = candidate.AddHours(1); // Hourly job: next hour
            else
                candidate = candidate.AddDays(1);  // Default: advance by 1 day
        }

        return candidate;
    }
}

/// <summary>
/// 91.H3.1: Audit Logger - Log all RAID operations.
/// </summary>
public sealed class AuditLogger
{
    private readonly ConcurrentQueue<AuditEntry> _entries = new();
    private const int MaxEntries = 100000;

    public void Log(
        string operation,
        string arrayId,
        string? userId = null,
        Dictionary<string, object>? details = null,
        AuditResult result = AuditResult.Success)
    {
        var entry = new AuditEntry
        {
            EntryId = Guid.NewGuid().ToString(),
            Timestamp = DateTime.UtcNow,
            Operation = operation,
            ArrayId = arrayId,
            UserId = userId ?? "system",
            Details = details ?? new Dictionary<string, object>(),
            Result = result
        };

        _entries.Enqueue(entry);

        // Trim oldest entries when over capacity.  ConcurrentQueue.Count is a snapshot;
        // concurrent trimmers may each attempt one dequeue — that is acceptable (ring-buffer
        // approximation). The critical property is that no entry is lost without being
        // replaced by a newer one; over-trimming by a few entries is preferable to an
        // unbounded queue.
        var excess = _entries.Count - MaxEntries;
        for (int i = 0; i < excess; i++)
        {
            if (!_entries.TryDequeue(out _)) break;
        }
    }

    public IReadOnlyList<AuditEntry> Query(AuditQuery query)
    {
        return _entries
            .Where(e => (query.StartTime == null || e.Timestamp >= query.StartTime) &&
                       (query.EndTime == null || e.Timestamp <= query.EndTime) &&
                       (query.ArrayId == null || e.ArrayId == query.ArrayId) &&
                       (query.Operation == null || e.Operation == query.Operation) &&
                       (query.UserId == null || e.UserId == query.UserId))
            .OrderByDescending(e => e.Timestamp)
            .Take(query.Limit ?? 1000)
            .ToList();
    }

    public IEnumerable<AuditEntry> GetAllEntries() => _entries.ToList();
}

/// <summary>
/// 91.H3.2: Compliance Reporter - Generate compliance reports.
/// </summary>
public sealed class ComplianceReporter
{
    private readonly AuditLogger _auditLogger;

    public ComplianceReporter(AuditLogger auditLogger)
    {
        _auditLogger = auditLogger;
    }

    public ComplianceReport GenerateReport(
        ComplianceStandard standard,
        DateTime startTime,
        DateTime endTime)
    {
        var entries = _auditLogger.Query(new AuditQuery { StartTime = startTime, EndTime = endTime });

        var report = new ComplianceReport
        {
            ReportId = Guid.NewGuid().ToString(),
            Standard = standard,
            StartTime = startTime,
            EndTime = endTime,
            GeneratedTime = DateTime.UtcNow
        };

        // Analyze entries for compliance
        report.TotalOperations = entries.Count;
        report.SuccessfulOperations = entries.Count(e => e.Result == AuditResult.Success);
        report.FailedOperations = entries.Count(e => e.Result == AuditResult.Failure);

        // Check compliance requirements based on standard
        report.Checks = standard switch
        {
            ComplianceStandard.SOC2 => CheckSoc2Compliance(entries),
            ComplianceStandard.HIPAA => CheckHipaaCompliance(entries),
            ComplianceStandard.GDPR => CheckGdprCompliance(entries),
            ComplianceStandard.PCI_DSS => CheckPciCompliance(entries),
            _ => new List<ComplianceCheck>()
        };

        report.IsCompliant = report.Checks.All(c => c.Passed);

        return report;
    }

    private List<ComplianceCheck> CheckSoc2Compliance(IReadOnlyList<AuditEntry> entries)
    {
        var hasUserIds = entries.Count == 0 || entries.All(e => !string.IsNullOrWhiteSpace(e.UserId));
        var failedOps = entries.Where(e => e.Result == AuditResult.Failure).ToList();
        var successRate = entries.Count > 0 ? (double)(entries.Count - failedOps.Count) / entries.Count : 1.0;
        var availabilityPassed = successRate >= 0.995; // 99.5% minimum for SOC 2 Type II

        return new()
        {
            new() { Name = "Access Control", Passed = hasUserIds, Description = hasUserIds ? "All operations logged with user ID" : $"{entries.Count(e => string.IsNullOrWhiteSpace(e.UserId))} operations missing user ID" },
            new() { Name = "Data Integrity", Passed = failedOps.Count == 0, Description = failedOps.Count == 0 ? "All write operations succeeded" : $"{failedOps.Count} failed operations detected" },
            new() { Name = "Availability", Passed = availabilityPassed, Description = $"Success rate: {successRate:P2} (required >= 99.5%)" }
        };
    }

    private List<ComplianceCheck> CheckHipaaCompliance(IReadOnlyList<AuditEntry> entries)
    {
        // HIPAA requires audit logs to record who accessed PHI and when.
        var allLogged = entries.Count == 0 || entries.All(e => !string.IsNullOrWhiteSpace(e.UserId) && e.Timestamp != default);
        var unauthorizedAccess = entries.Any(e => e.Result == AuditResult.Failure && e.Operation.Contains("read", StringComparison.OrdinalIgnoreCase));

        return new()
        {
            new() { Name = "Access Logging", Passed = allLogged, Description = allLogged ? "All PHI access logged with user and timestamp" : "Some access entries are missing user ID or timestamp" },
            new() { Name = "No Unauthorized Access", Passed = !unauthorizedAccess, Description = unauthorizedAccess ? "Unauthorized read attempts detected" : "No unauthorized read attempts" }
        };
    }

    private List<ComplianceCheck> CheckGdprCompliance(IReadOnlyList<AuditEntry> entries)
    {
        // GDPR requires data processing activities to be logged and auditable.
        var processingLogged = entries.Count == 0 || entries.Any(e => e.Operation.Contains("write", StringComparison.OrdinalIgnoreCase) || e.Operation.Contains("delete", StringComparison.OrdinalIgnoreCase));
        var auditTrailAvailable = entries.Count > 0;

        return new()
        {
            new() { Name = "Data Processing", Passed = processingLogged, Description = processingLogged ? "Data processing activities are logged" : "No processing activities found in audit log" },
            new() { Name = "Right to Access", Passed = auditTrailAvailable, Description = auditTrailAvailable ? $"Audit trail available ({entries.Count} entries)" : "No audit trail available — GDPR data subject requests cannot be fulfilled" }
        };
    }

    private List<ComplianceCheck> CheckPciCompliance(IReadOnlyList<AuditEntry> entries)
    {
        // PCI DSS requires restricted access logging and no failed auth attempts beyond threshold.
        var failedAuthAttempts = entries.Count(e => e.Result == AuditResult.Failure && e.Operation.Contains("auth", StringComparison.OrdinalIgnoreCase));
        var pciFailedAuthThreshold = 6; // PCI DSS: lock account after 6 attempts
        var authPassed = failedAuthAttempts < pciFailedAuthThreshold;
        var allAccessLogged = entries.Count == 0 || entries.All(e => e.Timestamp != default);

        return new()
        {
            new() { Name = "Cardholder Data Access", Passed = allAccessLogged, Description = allAccessLogged ? "All access events have timestamps" : "Some events missing timestamps" },
            new() { Name = "Authentication Controls", Passed = authPassed, Description = authPassed ? $"Failed auth attempts ({failedAuthAttempts}) within threshold" : $"Failed auth attempts ({failedAuthAttempts}) exceed PCI DSS threshold ({pciFailedAuthThreshold})" }
        };
    }
}

/// <summary>
/// 91.H3.3: Integrity Proof - Cryptographic proof of data integrity.
/// </summary>
public sealed class IntegrityProof
{
    private readonly BoundedDictionary<string, IntegrityRecord> _records = new BoundedDictionary<string, IntegrityRecord>(1000);
    // Per-instance HMAC key — provides tamper-evidence against external forgers.
    // For cross-process verification, persist and share this key securely.
    private readonly byte[] _hmacKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    public IntegrityRecord CreateProof(string arrayId, byte[] dataHash)
    {
        var record = new IntegrityRecord
        {
            RecordId = Guid.NewGuid().ToString(),
            ArrayId = arrayId,
            DataHash = dataHash,
            Timestamp = DateTime.UtcNow,
            Signature = SignData(dataHash)
        };

        _records[record.RecordId] = record;
        return record;
    }

    public bool VerifyProof(string recordId, byte[] currentDataHash)
    {
        if (!_records.TryGetValue(recordId, out var record))
            return false;

        return record.DataHash.Length == currentDataHash.Length && CryptographicOperations.FixedTimeEquals(record.DataHash, currentDataHash) &&
               VerifySignature(record.DataHash, record.Signature);
    }

    public IntegrityChain CreateChain(string arrayId, IEnumerable<byte[]> blockHashes)
    {
        var chain = new IntegrityChain
        {
            ChainId = Guid.NewGuid().ToString(),
            ArrayId = arrayId,
            CreatedTime = DateTime.UtcNow
        };

        byte[]? previousHash = null;
        foreach (var blockHash in blockHashes)
        {
            var link = new ChainLink
            {
                BlockHash = blockHash,
                PreviousLinkHash = previousHash,
                Timestamp = DateTime.UtcNow
            };
            link.LinkHash = ComputeLinkHash(link);
            previousHash = link.LinkHash;
            chain.Links.Add(link);
        }

        chain.RootHash = previousHash ?? Array.Empty<byte>();
        return chain;
    }

    private byte[] SignData(byte[] data)
    {
        // HMAC-SHA256 with per-instance key provides tamper-evidence.
        // A bare SHA256 hash (no key) is not a signature — anyone can recompute it.
        return System.Security.Cryptography.HMACSHA256.HashData(_hmacKey, data);
    }

    private bool VerifySignature(byte[] data, byte[] signature)
    {
        var expected = SignData(data);
        return expected.Length == signature.Length && CryptographicOperations.FixedTimeEquals(expected, signature);
    }

    private byte[] ComputeLinkHash(ChainLink link)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var combined = link.BlockHash.Concat(link.PreviousLinkHash ?? Array.Empty<byte>()).ToArray();
        return sha.ComputeHash(combined);
    }
}

// Data classes
public sealed class ArrayStatus
{
    public string ArrayId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public HealthState Health { get; set; }
    public int TotalDisks { get; set; }
    public int HealthyDisks { get; set; }
    public long TotalCapacity { get; set; }
    public long UsedCapacity { get; set; }
    public bool RebuildInProgress { get; set; }
    public double RebuildProgress { get; set; }
    public DateTime LastUpdate { get; set; }
}

public enum HealthState { Healthy, Warning, Degraded, Critical, Offline }

public sealed class MetricDataPoint
{
    public DateTime Timestamp { get; set; }
    public double Value { get; set; }
}

public sealed class HistoricalDataPoint
{
    public DateTime Timestamp { get; set; }
    public double Value { get; set; }
    public Dictionary<string, string> Labels { get; set; } = new();
}

public sealed class DashboardData
{
    public DateTime Timestamp { get; set; }
    public List<ArrayStatus> ArrayStatuses { get; set; } = new();
    public int TotalArrays { get; set; }
    public int HealthyArrays { get; set; }
    public int DegradedArrays { get; set; }
    public int CriticalArrays { get; set; }
}

public sealed class MetricQuery
{
    public string ArrayId { get; set; } = string.Empty;
    public string MetricName { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
}

public sealed class QueryResult
{
    public MetricQuery Query { get; set; } = new();
    public List<HistoricalDataPoint> DataPoints { get; set; } = new();
    public int Count { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
    public double Avg { get; set; }
}

public sealed class PrometheusMetric
{
    public string Name { get; set; } = string.Empty;
    public MetricType Type { get; set; }
    public string Help { get; set; } = string.Empty;
    public string[] Labels { get; set; } = Array.Empty<string>();
    public BoundedDictionary<string, double> Values { get; set; } = new BoundedDictionary<string, double>(1000);
    public DateTime LastUpdate { get; set; }
}

public enum MetricType { Counter, Gauge, Histogram, Summary }

public sealed class CliResult
{
    public bool Success { get; set; }
    public string Output { get; set; } = string.Empty;
    public int ExitCode => Success ? 0 : 1;
}

public sealed class ApiResponse
{
    public int StatusCode { get; set; }
    public string Body { get; set; } = string.Empty;
    public Dictionary<string, string> Headers { get; set; } = new();
}

public sealed class ScheduledOperation
{
    public string OperationId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public OperationType Type { get; set; }
    public string CronExpression { get; set; } = string.Empty;
    public string? ArrayId { get; set; }
    public bool IsEnabled { get; set; }
    public DateTime CreatedTime { get; set; }
    public DateTime NextRunTime { get; set; }
    public DateTime? LastRunTime { get; set; }
}

public enum OperationType { Scrub, Verify, Backup, Snapshot, Maintenance }

public sealed class AuditEntry
{
    public string EntryId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string Operation { get; set; } = string.Empty;
    public string ArrayId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public Dictionary<string, object> Details { get; set; } = new();
    public AuditResult Result { get; set; }
}

public enum AuditResult { Success, Failure, Warning }

public sealed class AuditQuery
{
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string? ArrayId { get; set; }
    public string? Operation { get; set; }
    public string? UserId { get; set; }
    public int? Limit { get; set; }
}

public enum ComplianceStandard { SOC2, HIPAA, GDPR, PCI_DSS, ISO27001 }

public sealed class ComplianceReport
{
    public string ReportId { get; set; } = string.Empty;
    public ComplianceStandard Standard { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public DateTime GeneratedTime { get; set; }
    public int TotalOperations { get; set; }
    public int SuccessfulOperations { get; set; }
    public int FailedOperations { get; set; }
    public List<ComplianceCheck> Checks { get; set; } = new();
    public bool IsCompliant { get; set; }
}

public sealed class ComplianceCheck
{
    public string Name { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string Description { get; set; } = string.Empty;
}

public sealed class IntegrityRecord
{
    public string RecordId { get; set; } = string.Empty;
    public string ArrayId { get; set; } = string.Empty;
    public byte[] DataHash { get; set; } = Array.Empty<byte>();
    public byte[] Signature { get; set; } = Array.Empty<byte>();
    public DateTime Timestamp { get; set; }
}

public sealed class IntegrityChain
{
    public string ChainId { get; set; } = string.Empty;
    public string ArrayId { get; set; } = string.Empty;
    public DateTime CreatedTime { get; set; }
    public List<ChainLink> Links { get; set; } = new();
    public byte[] RootHash { get; set; } = Array.Empty<byte>();
}

public sealed class ChainLink
{
    public byte[] BlockHash { get; set; } = Array.Empty<byte>();
    public byte[]? PreviousLinkHash { get; set; }
    public byte[] LinkHash { get; set; } = Array.Empty<byte>();
    public DateTime Timestamp { get; set; }
}
