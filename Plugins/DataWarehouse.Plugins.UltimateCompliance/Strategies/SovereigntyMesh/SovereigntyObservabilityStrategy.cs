using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.SovereigntyMesh;

// ==================================================================================
// Sovereignty Observability Strategy: Real-time metrics, alerts, and health
// monitoring for the sovereignty mesh. Collects counters, gauges, and distribution
// metrics with rolling windows. Generates alerts on threshold breaches and provides
// overall mesh health status.
// ==================================================================================

/// <summary>
/// Collects operational metrics, generates alerts, and reports health status for the
/// sovereignty mesh.
/// <para>
/// Tracks 15+ counter metrics (passport operations, zone enforcement, transfers, ZK proofs),
/// 3+ gauge metrics (active passports, zones, agreements), and 3+ distribution metrics
/// (operation durations with rolling windows). Automatically detects anomalies and generates
/// alerts when configurable thresholds are breached.
/// </para>
/// </summary>
public sealed class SovereigntyObservabilityStrategy : ComplianceStrategyBase
{
    // ==================================================================================
    // Metric name constants
    // ==================================================================================

    // Counter metrics
    public const string PassportsIssuedTotal = "passports_issued_total";
    public const string PassportsVerifiedTotal = "passports_verified_total";
    public const string PassportsValidTotal = "passports_valid_total";
    public const string PassportsInvalidTotal = "passports_invalid_total";
    public const string PassportsExpiredTotal = "passports_expired_total";
    public const string PassportsRevokedTotal = "passports_revoked_total";
    public const string ZoneEnforcementTotal = "zone_enforcement_total";
    public const string ZoneEnforcementAllowedTotal = "zone_enforcement_allowed_total";
    public const string ZoneEnforcementDeniedTotal = "zone_enforcement_denied_total";
    public const string ZoneEnforcementConditionalTotal = "zone_enforcement_conditional_total";
    public const string TransfersTotal = "transfers_total";
    public const string TransfersApprovedTotal = "transfers_approved_total";
    public const string TransfersDeniedTotal = "transfers_denied_total";
    public const string ZkProofsGeneratedTotal = "zk_proofs_generated_total";
    public const string ZkProofsVerifiedTotal = "zk_proofs_verified_total";

    // Gauge metrics
    public const string ActivePassports = "active_passports";
    public const string ActiveZones = "active_zones";
    public const string ActiveAgreements = "active_agreements";

    // Distribution metrics
    public const string PassportIssuanceDurationMs = "passport_issuance_duration_ms";
    public const string ZoneEnforcementDurationMs = "zone_enforcement_duration_ms";
    public const string TransferNegotiationDurationMs = "transfer_negotiation_duration_ms";

    // ==================================================================================
    // Storage
    // ==================================================================================

    private const int MaxRollingWindowSize = 1000;

    private readonly BoundedDictionary<string, long> _counters = new BoundedDictionary<string, long>(1000);
    private readonly BoundedDictionary<string, long> _gauges = new BoundedDictionary<string, long>(1000);
    private readonly BoundedDictionary<string, RollingWindow> _distributions = new BoundedDictionary<string, RollingWindow>(1000);

    // Alert thresholds (configurable via InitializeAsync)
    private double _expirationRateThreshold = 0.10; // 10%
    private double _enforcementDenialRateThreshold = 0.20; // 20%
    private double _transferDenialRateThreshold = 0.30; // 30%

    // ==================================================================================
    // Strategy identity
    // ==================================================================================

    /// <inheritdoc/>
    public override string StrategyId => "sovereignty-observability";

    /// <inheritdoc/>
    public override string StrategyName => "Sovereignty Mesh Observability";

    /// <inheritdoc/>
    public override string Framework => "SovereigntyMesh";

    // ==================================================================================
    // Configuration
    // ==================================================================================

    /// <inheritdoc/>
    public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
    {
        base.InitializeAsync(configuration, cancellationToken);

        if (configuration.TryGetValue("expirationRateThreshold", out var ert) && ert is double ertVal)
            _expirationRateThreshold = ertVal;
        if (configuration.TryGetValue("enforcementDenialRateThreshold", out var edrt) && edrt is double edrtVal)
            _enforcementDenialRateThreshold = edrtVal;
        if (configuration.TryGetValue("transferDenialRateThreshold", out var tdrt) && tdrt is double tdrtVal)
            _transferDenialRateThreshold = tdrtVal;

        return Task.CompletedTask;
    }

    // ==================================================================================
    // Metric collection methods
    // ==================================================================================

    /// <summary>
    /// Increments a counter metric by the given value (thread-safe).
    /// </summary>
    /// <param name="metricName">Counter metric name.</param>
    /// <param name="value">Value to increment by (default 1).</param>
    public Task IncrementCounterAsync(string metricName, long value = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metricName);

        _counters.AddOrUpdate(metricName, value, (_, current) =>
        {
            Interlocked.Add(ref current, value);
            return current;
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Sets a gauge metric to a specific value (thread-safe).
    /// </summary>
    /// <param name="metricName">Gauge metric name.</param>
    /// <param name="value">The current value.</param>
    public Task SetGaugeAsync(string metricName, long value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metricName);

        _gauges.AddOrUpdate(metricName, value, (_, _) => value);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Records a duration sample in a distribution metric's rolling window.
    /// </summary>
    /// <param name="metricName">Distribution metric name.</param>
    /// <param name="durationMs">Duration in milliseconds.</param>
    public Task RecordDurationAsync(string metricName, double durationMs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metricName);

        var window = _distributions.GetOrAdd(metricName, _ => new RollingWindow(MaxRollingWindowSize));
        window.Add(durationMs);

        return Task.CompletedTask;
    }

    // ==================================================================================
    // Metrics snapshot
    // ==================================================================================

    /// <summary>
    /// Returns a point-in-time snapshot of all collected metrics.
    /// </summary>
    public Task<SovereigntyMetricsSnapshot> GetMetricsSnapshotAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var counters = new Dictionary<string, long>(_counters);
        var gauges = new Dictionary<string, long>(_gauges);
        var distributions = new Dictionary<string, DistributionSummary>();

        foreach (var kvp in _distributions)
        {
            distributions[kvp.Key] = kvp.Value.GetSummary();
        }

        var snapshot = new SovereigntyMetricsSnapshot
        {
            Counters = counters,
            Gauges = gauges,
            Distributions = distributions,
            Timestamp = DateTimeOffset.UtcNow
        };

        return Task.FromResult(snapshot);
    }

    // ==================================================================================
    // Alert generation
    // ==================================================================================

    /// <summary>
    /// Evaluates current metrics against configured thresholds and returns active alerts.
    /// </summary>
    public Task<IReadOnlyList<SovereigntyAlert>> GetAlertsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var alerts = new List<SovereigntyAlert>();
        var now = DateTimeOffset.UtcNow;

        // Check passport expiration rate
        var activeCount = _gauges.GetValueOrDefault(ActivePassports, 0);
        var expiredCount = _counters.GetValueOrDefault(PassportsExpiredTotal, 0);
        if (activeCount > 0 && expiredCount > 0)
        {
            var rate = (double)expiredCount / activeCount;
            if (rate > _expirationRateThreshold)
            {
                alerts.Add(new SovereigntyAlert
                {
                    AlertId = Guid.NewGuid().ToString("N"),
                    Severity = rate > _expirationRateThreshold * 2 ? AlertSeverity.Critical : AlertSeverity.Warning,
                    Message = $"High passport expiration rate: {rate:P1} exceeds threshold of {_expirationRateThreshold:P1}",
                    MetricName = PassportsExpiredTotal,
                    CurrentValue = rate,
                    Threshold = _expirationRateThreshold,
                    Timestamp = now
                });
            }
        }

        // Check zone enforcement denial rate
        var totalEnforcement = _counters.GetValueOrDefault(ZoneEnforcementTotal, 0);
        var deniedEnforcement = _counters.GetValueOrDefault(ZoneEnforcementDeniedTotal, 0);
        if (totalEnforcement > 0 && deniedEnforcement > 0)
        {
            var rate = (double)deniedEnforcement / totalEnforcement;
            if (rate > _enforcementDenialRateThreshold)
            {
                alerts.Add(new SovereigntyAlert
                {
                    AlertId = Guid.NewGuid().ToString("N"),
                    Severity = rate > _enforcementDenialRateThreshold * 2 ? AlertSeverity.Critical : AlertSeverity.Warning,
                    Message = $"High enforcement denial rate: {rate:P1} exceeds threshold of {_enforcementDenialRateThreshold:P1}",
                    MetricName = ZoneEnforcementDeniedTotal,
                    CurrentValue = rate,
                    Threshold = _enforcementDenialRateThreshold,
                    Timestamp = now
                });
            }
        }

        // Check transfer denial rate
        var totalTransfers = _counters.GetValueOrDefault(TransfersTotal, 0);
        var deniedTransfers = _counters.GetValueOrDefault(TransfersDeniedTotal, 0);
        if (totalTransfers > 0 && deniedTransfers > 0)
        {
            var rate = (double)deniedTransfers / totalTransfers;
            if (rate > _transferDenialRateThreshold)
            {
                alerts.Add(new SovereigntyAlert
                {
                    AlertId = Guid.NewGuid().ToString("N"),
                    Severity = rate > _transferDenialRateThreshold * 2 ? AlertSeverity.Critical : AlertSeverity.Warning,
                    Message = $"High transfer denial rate: {rate:P1} exceeds threshold of {_transferDenialRateThreshold:P1}",
                    MetricName = TransfersDeniedTotal,
                    CurrentValue = rate,
                    Threshold = _transferDenialRateThreshold,
                    Timestamp = now
                });
            }
        }

        return Task.FromResult<IReadOnlyList<SovereigntyAlert>>(alerts);
    }

    // ==================================================================================
    // Health endpoint
    // ==================================================================================

    /// <summary>
    /// Returns the overall health status of the sovereignty mesh based on active alerts.
    /// </summary>
    public async Task<SovereigntyHealth> GetHealthAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var alerts = await GetAlertsAsync(ct);
        var snapshot = await GetMetricsSnapshotAsync(ct);

        var status = HealthStatus.Healthy;
        if (alerts.Any(a => a.Severity == AlertSeverity.Critical))
            status = HealthStatus.Unhealthy;
        else if (alerts.Any(a => a.Severity == AlertSeverity.Warning))
            status = HealthStatus.Degraded;

        return new SovereigntyHealth
        {
            Status = status,
            ActiveAlerts = alerts,
            MetricsSnapshot = snapshot,
            Timestamp = DateTimeOffset.UtcNow
        };
    }

    // ==================================================================================
    // Compliance check: health-based
    // ==================================================================================

    /// <inheritdoc/>
    protected override async Task<ComplianceResult> CheckComplianceCoreAsync(
        ComplianceContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IncrementCounter("sovereignty_observability.compliance_check");

        var health = await GetHealthAsync(cancellationToken);

        var isCompliant = health.Status != HealthStatus.Unhealthy;
        var violations = new List<ComplianceViolation>();

        if (!isCompliant)
        {
            foreach (var alert in health.ActiveAlerts.Where(a => a.Severity == AlertSeverity.Critical))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = $"OBSERVE-{alert.MetricName}",
                    Description = alert.Message,
                    Severity = ViolationSeverity.High,
                    AffectedResource = alert.MetricName,
                    Remediation = "Investigate and resolve the underlying metric anomaly"
                });
            }
        }

        var recommendations = new List<string>();
        if (health.Status == HealthStatus.Degraded)
            recommendations.Add("Warning-level alerts detected; review sovereignty mesh metrics");

        return new ComplianceResult
        {
            IsCompliant = isCompliant,
            Framework = Framework,
            Status = health.Status switch
            {
                HealthStatus.Healthy => ComplianceStatus.Compliant,
                HealthStatus.Degraded => ComplianceStatus.PartiallyCompliant,
                _ => ComplianceStatus.NonCompliant
            },
            Violations = violations,
            Recommendations = recommendations,
            Metadata = new Dictionary<string, object>
            {
                ["healthStatus"] = health.Status.ToString(),
                ["activeAlertCount"] = health.ActiveAlerts.Count,
                ["counterCount"] = health.MetricsSnapshot.Counters.Count,
                ["gaugeCount"] = health.MetricsSnapshot.Gauges.Count,
                ["distributionCount"] = health.MetricsSnapshot.Distributions.Count
            }
        };
    }
}

// ==================================================================================
// Rolling window for distribution metrics
// ==================================================================================

/// <summary>
/// Thread-safe bounded rolling window for recording distribution samples.
/// </summary>
internal sealed class RollingWindow
{
    private readonly double[] _buffer;
    private readonly int _capacity;
    private int _head;
    private int _count;
    private readonly object _lock = new();

    public RollingWindow(int capacity)
    {
        _capacity = capacity;
        _buffer = new double[capacity];
    }

    public void Add(double value)
    {
        lock (_lock)
        {
            _buffer[_head] = value;
            _head = (_head + 1) % _capacity;
            if (_count < _capacity) _count++;
        }
    }

    public DistributionSummary GetSummary()
    {
        double[] snapshot;
        lock (_lock)
        {
            if (_count == 0)
            {
                return new DistributionSummary
                {
                    Count = 0, Min = 0, Max = 0, Average = 0,
                    P50 = 0, P95 = 0, P99 = 0
                };
            }

            snapshot = new double[_count];
            if (_count == _capacity)
            {
                // Full buffer: copy from head (oldest) to end, then start to head
                var tailLen = _capacity - _head;
                Array.Copy(_buffer, _head, snapshot, 0, tailLen);
                Array.Copy(_buffer, 0, snapshot, tailLen, _head);
            }
            else
            {
                Array.Copy(_buffer, 0, snapshot, 0, _count);
            }
        }

        Array.Sort(snapshot);
        var count = snapshot.Length;

        return new DistributionSummary
        {
            Count = count,
            Min = snapshot[0],
            Max = snapshot[count - 1],
            Average = snapshot.Average(),
            P50 = Percentile(snapshot, 0.50),
            P95 = Percentile(snapshot, 0.95),
            P99 = Percentile(snapshot, 0.99)
        };
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0) return 0;
        if (sorted.Length == 1) return sorted[0];

        var index = percentile * (sorted.Length - 1);
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        if (lower == upper) return sorted[lower];

        var fraction = index - lower;
        return sorted[lower] + (sorted[upper] - sorted[lower]) * fraction;
    }
}

// ==================================================================================
// Metrics, alert, and health records
// ==================================================================================

/// <summary>
/// Point-in-time snapshot of all sovereignty mesh metrics.
/// </summary>
public sealed record SovereigntyMetricsSnapshot
{
    /// <summary>Counter metric values (monotonically increasing).</summary>
    public required IReadOnlyDictionary<string, long> Counters { get; init; }

    /// <summary>Gauge metric values (current state).</summary>
    public required IReadOnlyDictionary<string, long> Gauges { get; init; }

    /// <summary>Distribution metric summaries (percentiles, avg, min, max).</summary>
    public required IReadOnlyDictionary<string, DistributionSummary> Distributions { get; init; }

    /// <summary>When this snapshot was taken.</summary>
    public required DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// Statistical summary of a distribution metric.
/// </summary>
public sealed record DistributionSummary
{
    /// <summary>Number of samples in the window.</summary>
    public int Count { get; init; }

    /// <summary>Minimum observed value.</summary>
    public double Min { get; init; }

    /// <summary>Maximum observed value.</summary>
    public double Max { get; init; }

    /// <summary>Mean of all samples.</summary>
    public double Average { get; init; }

    /// <summary>50th percentile (median).</summary>
    public double P50 { get; init; }

    /// <summary>95th percentile.</summary>
    public double P95 { get; init; }

    /// <summary>99th percentile.</summary>
    public double P99 { get; init; }
}

/// <summary>
/// Severity levels for sovereignty mesh alerts.
/// </summary>
public enum AlertSeverity
{
    /// <summary>Informational alert, no action required.</summary>
    Info,

    /// <summary>Warning alert, should be investigated.</summary>
    Warning,

    /// <summary>Critical alert, immediate action required.</summary>
    Critical
}

/// <summary>
/// An alert generated when a sovereignty mesh metric breaches a threshold.
/// </summary>
public sealed record SovereigntyAlert
{
    /// <summary>Unique alert identifier.</summary>
    public required string AlertId { get; init; }

    /// <summary>Severity of the alert.</summary>
    public required AlertSeverity Severity { get; init; }

    /// <summary>Human-readable alert message.</summary>
    public required string Message { get; init; }

    /// <summary>Name of the metric that triggered the alert.</summary>
    public required string MetricName { get; init; }

    /// <summary>Current value of the metric (as rate).</summary>
    public required double CurrentValue { get; init; }

    /// <summary>Threshold that was breached.</summary>
    public required double Threshold { get; init; }

    /// <summary>When the alert was generated.</summary>
    public required DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// Health status levels for the sovereignty mesh.
/// </summary>
public enum HealthStatus
{
    /// <summary>All metrics within normal parameters.</summary>
    Healthy,

    /// <summary>Warning-level alerts present but no critical issues.</summary>
    Degraded,

    /// <summary>Critical alerts present, immediate attention required.</summary>
    Unhealthy
}

/// <summary>
/// Overall health report for the sovereignty mesh.
/// </summary>
public sealed record SovereigntyHealth
{
    /// <summary>Current health status.</summary>
    public required HealthStatus Status { get; init; }

    /// <summary>Currently active alerts.</summary>
    public required IReadOnlyList<SovereigntyAlert> ActiveAlerts { get; init; }

    /// <summary>Current metrics snapshot.</summary>
    public required SovereigntyMetricsSnapshot MetricsSnapshot { get; init; }

    /// <summary>When this health check was performed.</summary>
    public required DateTimeOffset Timestamp { get; init; }
}
