using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using DataWarehouse.SDK.Contracts.Observability;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.SyntheticMonitoring;

/// <summary>
/// SSL certificate monitoring service for synthetic monitoring strategies.
/// Tracks certificate expiration, chain validity, and protocol compliance.
/// </summary>
public sealed class SslCertificateMonitorService
{
    private readonly BoundedDictionary<string, SslCertificateInfo> _certificateCache = new BoundedDictionary<string, SslCertificateInfo>(1000);
    private readonly BoundedDictionary<string, List<SslAlert>> _alerts = new BoundedDictionary<string, List<SslAlert>>(1000);
    private readonly HttpClient _httpClient;
    private readonly int _expirationWarningDays;

    public SslCertificateMonitorService(int expirationWarningDays = 30)
    {
        _expirationWarningDays = expirationWarningDays;
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
            {
                if (cert != null && message?.RequestUri?.Host != null)
                {
                    CacheCertificateInfo(message.RequestUri.Host, cert, chain, errors);
                }
                return true; // We monitor; we don't block
            }
        };
        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>
    /// Checks SSL certificate for a host.
    /// </summary>
    public async Task<SslCertificateInfo> CheckCertificateAsync(string host, CancellationToken ct = default)
    {
        try
        {
            var uri = host.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? host : $"https://{host}";
            await _httpClient.GetAsync(uri, ct);
        }
        catch (HttpRequestException ex)
        {

            // Connection may fail but we still get the cert
            System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
        }

        return _certificateCache.TryGetValue(host, out var info) ? info : new SslCertificateInfo
        {
            Host = host,
            IsValid = false,
            CheckedAt = DateTimeOffset.UtcNow,
            Error = "Could not retrieve certificate"
        };
    }

    /// <summary>
    /// Gets all tracked certificates and their status.
    /// </summary>
    public IReadOnlyList<SslCertificateInfo> GetAllCertificates() =>
        _certificateCache.Values.ToList().AsReadOnly();

    /// <summary>
    /// Gets certificates expiring within the warning window.
    /// </summary>
    public IReadOnlyList<SslCertificateInfo> GetExpiringCertificates() =>
        _certificateCache.Values
            .Where(c => c.ExpiresAt.HasValue && c.ExpiresAt.Value - DateTimeOffset.UtcNow < TimeSpan.FromDays(_expirationWarningDays))
            .OrderBy(c => c.ExpiresAt)
            .ToList().AsReadOnly();

    /// <summary>
    /// Gets alerts for a host.
    /// </summary>
    public IReadOnlyList<SslAlert> GetAlerts(string host) =>
        _alerts.TryGetValue(host, out var alerts) ? alerts.AsReadOnly() : Array.Empty<SslAlert>();

    private void CacheCertificateInfo(string host, X509Certificate2 cert, X509Chain? chain, System.Net.Security.SslPolicyErrors errors)
    {
        var daysUntilExpiry = (cert.NotAfter - DateTime.UtcNow).TotalDays;
        var info = new SslCertificateInfo
        {
            Host = host,
            Subject = cert.Subject,
            Issuer = cert.Issuer,
            Thumbprint = cert.Thumbprint,
            IssuedAt = new DateTimeOffset(cert.NotBefore, TimeSpan.Zero),
            ExpiresAt = new DateTimeOffset(cert.NotAfter, TimeSpan.Zero),
            DaysUntilExpiry = (int)daysUntilExpiry,
            IsValid = errors == System.Net.Security.SslPolicyErrors.None,
            PolicyErrors = errors.ToString(),
            ChainLength = chain?.ChainElements.Count ?? 0,
            Protocol = "TLS",
            CheckedAt = DateTimeOffset.UtcNow
        };

        _certificateCache[host] = info;

        // Generate alerts
        if (daysUntilExpiry < _expirationWarningDays)
        {
            AddAlert(host, SslAlertSeverity.Warning,
                $"Certificate expires in {(int)daysUntilExpiry} days");
        }
        if (daysUntilExpiry < 7)
        {
            AddAlert(host, SslAlertSeverity.Critical,
                $"Certificate expires in {(int)daysUntilExpiry} days - URGENT");
        }
        if (errors != System.Net.Security.SslPolicyErrors.None)
        {
            AddAlert(host, SslAlertSeverity.Error,
                $"SSL policy errors: {errors}");
        }
    }

    private void AddAlert(string host, SslAlertSeverity severity, string message)
    {
        var alert = new SslAlert
        {
            Host = host,
            Severity = severity,
            Message = message,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _alerts.AddOrUpdate(
            host,
            _ => new List<SslAlert> { alert },
            (_, list) => { lock (list) { list.Add(alert); } return list; });
    }
}

/// <summary>
/// Alert trigger engine for synthetic monitoring. Evaluates availability, response time,
/// and SSL conditions to fire alerts.
/// </summary>
public sealed class AlertTriggerEngine
{
    private readonly BoundedDictionary<string, AlertRule> _rules = new BoundedDictionary<string, AlertRule>(1000);
    private readonly BoundedDictionary<string, List<AlertEvent>> _firedAlerts = new BoundedDictionary<string, List<AlertEvent>>(1000);
    private readonly BoundedDictionary<string, AlertState> _alertStates = new BoundedDictionary<string, AlertState>(1000);

    /// <summary>
    /// Creates an alert rule.
    /// </summary>
    public AlertRule CreateRule(string ruleId, string name, AlertCondition condition, AlertAction action)
    {
        var rule = new AlertRule
        {
            RuleId = ruleId,
            Name = name,
            Condition = condition,
            Action = action,
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _rules[ruleId] = rule;
        return rule;
    }

    /// <summary>
    /// Evaluates all rules against the given metrics.
    /// </summary>
    public IReadOnlyList<AlertEvent> Evaluate(Dictionary<string, double> metrics)
    {
        var events = new List<AlertEvent>();

        foreach (var rule in _rules.Values.Where(r => r.IsEnabled))
        {
            if (!metrics.TryGetValue(rule.Condition.MetricName, out var value))
                continue;

            var triggered = rule.Condition.Operator switch
            {
                ComparisonOperator.GreaterThan => value > rule.Condition.Threshold,
                ComparisonOperator.LessThan => value < rule.Condition.Threshold,
                ComparisonOperator.GreaterThanOrEqual => value >= rule.Condition.Threshold,
                ComparisonOperator.LessThanOrEqual => value <= rule.Condition.Threshold,
                ComparisonOperator.Equal => Math.Abs(value - rule.Condition.Threshold) < 0.001,
                _ => false
            };

            var currentState = _alertStates.GetOrAdd(rule.RuleId, _ => new AlertState { IsTriggered = false });

            if (triggered && !currentState.IsTriggered)
            {
                // State transition: OK -> TRIGGERED
                var evt = new AlertEvent
                {
                    RuleId = rule.RuleId,
                    RuleName = rule.Name,
                    MetricName = rule.Condition.MetricName,
                    MetricValue = value,
                    Threshold = rule.Condition.Threshold,
                    Severity = rule.Action.Severity,
                    FiredAt = DateTimeOffset.UtcNow,
                    Type = AlertEventType.Triggered
                };
                events.Add(evt);
                RecordAlert(rule.RuleId, evt);
                _alertStates[rule.RuleId] = new AlertState { IsTriggered = true, LastTriggeredAt = DateTimeOffset.UtcNow };
            }
            else if (!triggered && currentState.IsTriggered)
            {
                // State transition: TRIGGERED -> RESOLVED
                var evt = new AlertEvent
                {
                    RuleId = rule.RuleId,
                    RuleName = rule.Name,
                    MetricName = rule.Condition.MetricName,
                    MetricValue = value,
                    Threshold = rule.Condition.Threshold,
                    Severity = rule.Action.Severity,
                    FiredAt = DateTimeOffset.UtcNow,
                    Type = AlertEventType.Resolved
                };
                events.Add(evt);
                RecordAlert(rule.RuleId, evt);
                _alertStates[rule.RuleId] = new AlertState { IsTriggered = false, LastResolvedAt = DateTimeOffset.UtcNow };
            }
        }

        return events;
    }

    /// <summary>
    /// Gets alert history for a rule.
    /// </summary>
    public IReadOnlyList<AlertEvent> GetAlertHistory(string ruleId) =>
        _firedAlerts.TryGetValue(ruleId, out var alerts) ? alerts.AsReadOnly() : Array.Empty<AlertEvent>();

    /// <summary>
    /// Gets all active (triggered) alerts.
    /// </summary>
    public IReadOnlyList<string> GetActiveAlerts() =>
        _alertStates.Where(kvp => kvp.Value.IsTriggered).Select(kvp => kvp.Key).ToList().AsReadOnly();

    /// <summary>
    /// Enables or disables a rule.
    /// </summary>
    public bool SetRuleEnabled(string ruleId, bool enabled)
    {
        if (!_rules.TryGetValue(ruleId, out var rule)) return false;
        _rules[ruleId] = rule with { IsEnabled = enabled };
        return true;
    }

    /// <summary>
    /// Deletes a rule.
    /// </summary>
    public bool DeleteRule(string ruleId) => _rules.TryRemove(ruleId, out _);

    private void RecordAlert(string ruleId, AlertEvent evt)
    {
        _firedAlerts.AddOrUpdate(
            ruleId,
            _ => new List<AlertEvent> { evt },
            (_, list) => { lock (list) { list.Add(evt); if (list.Count > 1000) list.RemoveAt(0); } return list; });
    }
}

/// <summary>
/// Response time measurement service with percentile tracking.
/// </summary>
public sealed class ResponseTimeMeasurementService
{
    private readonly BoundedDictionary<string, List<double>> _measurements = new BoundedDictionary<string, List<double>>(1000);
    private const int MaxMeasurementsPerTarget = 10000;

    /// <summary>
    /// Records a response time measurement.
    /// </summary>
    public void Record(string targetId, double responseTimeMs)
    {
        _measurements.AddOrUpdate(
            targetId,
            _ => new List<double> { responseTimeMs },
            (_, list) =>
            {
                lock (list)
                {
                    list.Add(responseTimeMs);
                    if (list.Count > MaxMeasurementsPerTarget)
                        list.RemoveAt(0);
                }
                return list;
            });
    }

    /// <summary>
    /// Gets statistics for a target.
    /// </summary>
    public ResponseTimeStats GetStats(string targetId)
    {
        if (!_measurements.TryGetValue(targetId, out var measurements) || measurements.Count == 0)
            return new ResponseTimeStats { TargetId = targetId };

        List<double> sorted;
        lock (measurements)
        {
            sorted = measurements.OrderBy(x => x).ToList();
        }

        return new ResponseTimeStats
        {
            TargetId = targetId,
            SampleCount = sorted.Count,
            Min = sorted[0],
            Max = sorted[^1],
            Average = sorted.Average(),
            Median = Percentile(sorted, 50),
            P90 = Percentile(sorted, 90),
            P95 = Percentile(sorted, 95),
            P99 = Percentile(sorted, 99)
        };
    }

    private static double Percentile(List<double> sorted, int percentile)
    {
        var index = (int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1;
        return sorted[Math.Max(0, Math.Min(index, sorted.Count - 1))];
    }
}

#region Models

public sealed record SslCertificateInfo
{
    public required string Host { get; init; }
    public string? Subject { get; init; }
    public string? Issuer { get; init; }
    public string? Thumbprint { get; init; }
    public DateTimeOffset? IssuedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public int DaysUntilExpiry { get; init; }
    public bool IsValid { get; init; }
    public string? PolicyErrors { get; init; }
    public int ChainLength { get; init; }
    public string? Protocol { get; init; }
    public DateTimeOffset CheckedAt { get; init; }
    public string? Error { get; init; }
}

public sealed record SslAlert
{
    public required string Host { get; init; }
    public SslAlertSeverity Severity { get; init; }
    public required string Message { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public enum SslAlertSeverity { Info, Warning, Error, Critical }

public sealed record AlertRule
{
    public required string RuleId { get; init; }
    public required string Name { get; init; }
    public required AlertCondition Condition { get; init; }
    public required AlertAction Action { get; init; }
    public bool IsEnabled { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record AlertCondition
{
    public required string MetricName { get; init; }
    public ComparisonOperator Operator { get; init; }
    public double Threshold { get; init; }
    public TimeSpan? Duration { get; init; }
}

public enum ComparisonOperator { GreaterThan, LessThan, GreaterThanOrEqual, LessThanOrEqual, Equal }

public sealed record AlertAction
{
    public AlertSeverity Severity { get; init; }
    public string? NotificationChannel { get; init; }
    public string? WebhookUrl { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = new();
}

public enum AlertSeverity { Info, Warning, Error, Critical }

public sealed record AlertEvent
{
    public required string RuleId { get; init; }
    public required string RuleName { get; init; }
    public required string MetricName { get; init; }
    public double MetricValue { get; init; }
    public double Threshold { get; init; }
    public AlertSeverity Severity { get; init; }
    public DateTimeOffset FiredAt { get; init; }
    public AlertEventType Type { get; init; }
}

public enum AlertEventType { Triggered, Resolved }

public sealed record AlertState
{
    public bool IsTriggered { get; init; }
    public DateTimeOffset? LastTriggeredAt { get; init; }
    public DateTimeOffset? LastResolvedAt { get; init; }
}

public sealed record ResponseTimeStats
{
    public required string TargetId { get; init; }
    public int SampleCount { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public double Average { get; init; }
    public double Median { get; init; }
    public double P90 { get; init; }
    public double P95 { get; init; }
    public double P99 { get; init; }
}

#endregion
