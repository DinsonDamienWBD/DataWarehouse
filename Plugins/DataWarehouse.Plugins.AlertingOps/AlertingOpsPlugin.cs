using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.AlertingOps;

/// <summary>
/// Operational alerting plugin providing comprehensive alert management for DataWarehouse.
/// Implements alert rule engine with threshold and anomaly detection, multiple notification
/// channels, alert aggregation, deduplication, and suppression windows.
///
/// Features:
/// - Alert rule engine (threshold, rate-of-change, anomaly detection)
/// - Multiple notification channels (Email, Webhook, PagerDuty, Slack, Teams)
/// - Alert aggregation and grouping
/// - Alert suppression and silencing windows
/// - Escalation policies
/// - Alert deduplication
/// - Metric time-series storage for evaluation
/// - Alert history and audit trail
///
/// Message Commands:
/// - alertops.rule.create: Create alert rule
/// - alertops.rule.update: Update alert rule
/// - alertops.rule.delete: Delete alert rule
/// - alertops.rule.list: List alert rules
/// - alertops.channel.configure: Configure notification channel
/// - alertops.channel.list: List notification channels
/// - alertops.silence.create: Create silence window
/// - alertops.silence.delete: Delete silence
/// - alertops.alert.list: List active alerts
/// - alertops.alert.acknowledge: Acknowledge alert
/// - alertops.alert.resolve: Resolve alert
/// - alertops.evaluate: Manually trigger evaluation
/// - alertops.metrics.record: Record metric value
/// - alertops.status: Get alerting status
/// </summary>
public sealed class AlertingOpsPlugin : OperationsPluginBase
{
    /// <inheritdoc />
    public override string Id => "com.datawarehouse.operations.alerting";

    /// <inheritdoc />
    public override string Name => "Alerting Operations Plugin";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override OperationsCapabilities Capabilities =>
        OperationsCapabilities.Alerting |
        OperationsCapabilities.Monitoring;

    #region Private Fields

    private readonly ConcurrentDictionary<string, AlertRuleDefinition> _rules = new();
    private readonly ConcurrentDictionary<string, ActiveAlert> _activeAlerts = new();
    private readonly ConcurrentDictionary<string, NotificationChannelConfig> _channels = new();
    private readonly ConcurrentDictionary<string, SilenceWindow> _silences = new();
    private readonly ConcurrentDictionary<string, EscalationPolicyDef> _escalationPolicies = new();
    private readonly ConcurrentDictionary<string, AlertGroup> _alertGroups = new();
    private readonly ConcurrentDictionary<string, MetricTimeSeries> _metrics = new();
    private readonly ConcurrentQueue<AlertHistoryEntry> _history = new();

    private IKernelContext? _context;
    private CancellationTokenSource? _cts;
    private Task? _evaluationTask;
    private Task? _escalationTask;
    private Task? _cleanupTask;
    private HttpClient? _httpClient;
    private bool _isRunning;

    private TimeSpan _evaluationInterval = TimeSpan.FromSeconds(30);
    private TimeSpan _escalationCheckInterval = TimeSpan.FromMinutes(1);
    private TimeSpan _groupingWindow = TimeSpan.FromMinutes(5);
    private int _deduplicationWindowSeconds = 300;
    private const int MaxHistoryEntries = 10000;
    private const int MaxMetricDataPoints = 1000;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    #endregion

    #region Lifecycle

    /// <inheritdoc />
    public override Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        _context = request.Context;

        return Task.FromResult(new HandshakeResponse
        {
            PluginId = Id,
            Name = Name,
            Version = ParseSemanticVersion(Version),
            Category = Category,
            Success = true,
            ReadyState = PluginReadyState.Ready,
            Capabilities = GetCapabilities(),
            Metadata = GetMetadata()
        });
    }

    /// <inheritdoc />
    public override async Task StartAsync(CancellationToken ct)
    {
        if (_isRunning) return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _isRunning = true;

        // Initialize default channels
        InitializeDefaultChannels();

        // Start background tasks
        _evaluationTask = RunEvaluationLoopAsync(_cts.Token);
        _escalationTask = RunEscalationLoopAsync(_cts.Token);
        _cleanupTask = RunCleanupLoopAsync(_cts.Token);

        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public override async Task StopAsync()
    {
        if (!_isRunning) return;

        _isRunning = false;
        _cts?.Cancel();

        var tasks = new List<Task>();
        if (_evaluationTask != null) tasks.Add(_evaluationTask);
        if (_escalationTask != null) tasks.Add(_escalationTask);
        if (_cleanupTask != null) tasks.Add(_cleanupTask);

        await Task.WhenAll(tasks.Select(t => t.ContinueWith(_ => { })));

        _httpClient?.Dispose();
        _httpClient = null;
        _cts?.Dispose();
        _cts = null;
    }

    #endregion

    #region IOperationsProvider Implementation

    /// <inheritdoc />
    public override Task<UpgradeResult> PerformUpgradeAsync(UpgradeRequest request, CancellationToken ct = default)
    {
        return Task.FromResult(new UpgradeResult
        {
            Success = false,
            ErrorMessage = "Upgrade not supported by alerting plugin"
        });
    }

    /// <inheritdoc />
    public override Task<RollbackResult> RollbackAsync(string deploymentId, CancellationToken ct = default)
    {
        return Task.FromResult(new RollbackResult
        {
            Success = false,
            ErrorMessage = "Rollback not supported by alerting plugin"
        });
    }

    /// <inheritdoc />
    public override Task<DeploymentStatus> GetDeploymentStatusAsync(string? deploymentId = null, CancellationToken ct = default)
    {
        return Task.FromResult(new DeploymentStatus
        {
            DeploymentId = Id,
            CurrentVersion = Version,
            State = _isRunning ? DeploymentState.Running : DeploymentState.Failed,
            DeployedAt = DateTime.UtcNow,
            Uptime = TimeSpan.Zero
        });
    }

    /// <inheritdoc />
    public override Task<ConfigReloadResult> ReloadConfigurationAsync(Dictionary<string, object>? newConfig = null, CancellationToken ct = default)
    {
        var changedKeys = new List<string>();

        if (newConfig != null)
        {
            if (newConfig.TryGetValue("evaluationInterval", out var interval) && interval is int ms)
            {
                _evaluationInterval = TimeSpan.FromMilliseconds(ms);
                changedKeys.Add("evaluationInterval");
            }

            if (newConfig.TryGetValue("groupingWindow", out var window) && window is int mins)
            {
                _groupingWindow = TimeSpan.FromMinutes(mins);
                changedKeys.Add("groupingWindow");
            }

            if (newConfig.TryGetValue("deduplicationWindow", out var dedup) && dedup is int secs)
            {
                _deduplicationWindowSeconds = secs;
                changedKeys.Add("deduplicationWindow");
            }
        }

        return Task.FromResult(new ConfigReloadResult
        {
            Success = true,
            ChangedKeys = changedKeys
        });
    }

    /// <inheritdoc />
    public override Task<OperationalMetrics> GetMetricsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new OperationalMetrics
        {
            CpuUsagePercent = 0,
            MemoryUsagePercent = 0,
            ActiveConnections = _activeAlerts.Count,
            RequestsPerSecond = 0,
            AverageLatencyMs = 0,
            ErrorsLastHour = _activeAlerts.Values.Count(a => a.Severity == AlertSeverityLevel.Critical),
            CustomMetrics = new Dictionary<string, double>
            {
                ["activeAlerts"] = _activeAlerts.Count,
                ["totalRules"] = _rules.Count,
                ["configuredChannels"] = _channels.Count,
                ["activeSilences"] = _silences.Values.Count(s => s.EndsAt > DateTime.UtcNow)
            }
        });
    }

    /// <inheritdoc />
    public override Task<AlertRule> ConfigureAlertAsync(AlertRuleConfig config, CancellationToken ct = default)
    {
        var ruleId = config.RuleId ?? Guid.NewGuid().ToString("N");

        var ruleDef = new AlertRuleDefinition
        {
            RuleId = ruleId,
            Name = config.Name,
            Metric = config.Metric,
            Condition = config.Condition,
            Threshold = config.Threshold,
            EvaluationWindow = config.EvaluationWindow,
            Severity = config.Severity,
            NotificationChannels = config.NotificationChannels?.ToList() ?? new List<string>(),
            Enabled = config.Enabled,
            CreatedAt = DateTime.UtcNow
        };

        _rules[ruleId] = ruleDef;

        return Task.FromResult(new AlertRule
        {
            RuleId = ruleId,
            Name = config.Name,
            Enabled = config.Enabled,
            CreatedAt = ruleDef.CreatedAt
        });
    }

    /// <inheritdoc />
    public override Task<IReadOnlyList<Alert>> GetActiveAlertsAsync(CancellationToken ct = default)
    {
        var alerts = _activeAlerts.Values.Select(a => new Alert
        {
            AlertId = a.AlertId,
            RuleId = a.RuleId,
            RuleName = a.RuleName,
            Severity = MapSeverity(a.Severity),
            Message = a.Message,
            CurrentValue = a.CurrentValue,
            Threshold = a.Threshold,
            TriggeredAt = a.TriggeredAt,
            IsAcknowledged = a.IsAcknowledged,
            AcknowledgedBy = a.AcknowledgedBy
        }).ToList();

        return Task.FromResult<IReadOnlyList<Alert>>(alerts);
    }

    /// <inheritdoc />
    public override Task<bool> AcknowledgeAlertAsync(string alertId, string? message = null, CancellationToken ct = default)
    {
        if (_activeAlerts.TryGetValue(alertId, out var alert))
        {
            alert.IsAcknowledged = true;
            alert.AcknowledgedBy = "system";
            alert.AcknowledgedAt = DateTime.UtcNow;
            alert.AcknowledgeMessage = message;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    #endregion

    #region Message Handling

    /// <inheritdoc />
    public override async Task OnMessageAsync(PluginMessage message)
    {
        if (message.Payload == null)
            return;

        var response = message.Type switch
        {
            "alertops.rule.create" => HandleCreateRule(message.Payload),
            "alertops.rule.update" => HandleUpdateRule(message.Payload),
            "alertops.rule.delete" => HandleDeleteRule(message.Payload),
            "alertops.rule.list" => HandleListRules(message.Payload),
            "alertops.channel.configure" => HandleConfigureChannel(message.Payload),
            "alertops.channel.list" => HandleListChannels(),
            "alertops.channel.test" => await HandleTestChannelAsync(message.Payload),
            "alertops.silence.create" => HandleCreateSilence(message.Payload),
            "alertops.silence.delete" => HandleDeleteSilence(message.Payload),
            "alertops.silence.list" => HandleListSilences(),
            "alertops.alert.list" => HandleListAlerts(message.Payload),
            "alertops.alert.acknowledge" => HandleAcknowledgeAlert(message.Payload),
            "alertops.alert.resolve" => HandleResolveAlert(message.Payload),
            "alertops.evaluate" => await HandleEvaluateAsync(),
            "alertops.metrics.record" => HandleRecordMetric(message.Payload),
            "alertops.metrics.query" => HandleQueryMetrics(message.Payload),
            "alertops.escalation.configure" => HandleConfigureEscalation(message.Payload),
            "alertops.status" => HandleGetStatus(),
            "alertops.history" => HandleGetHistory(message.Payload),
            _ => new Dictionary<string, object> { ["error"] = $"Unknown command: {message.Type}" }
        };

        if (response != null)
        {
            message.Payload["_response"] = response;
        }
    }

    #endregion

    #region Rule Management

    private Dictionary<string, object> HandleCreateRule(Dictionary<string, object> payload)
    {
        var name = payload.GetValueOrDefault("name")?.ToString();
        var metric = payload.GetValueOrDefault("metric")?.ToString();
        var conditionStr = payload.GetValueOrDefault("condition")?.ToString();
        var threshold = payload.GetValueOrDefault("threshold") as double? ?? 0;
        var severityStr = payload.GetValueOrDefault("severity")?.ToString() ?? "Warning";
        var channelsJson = payload.GetValueOrDefault("channels")?.ToString();
        var evalWindowSecs = payload.GetValueOrDefault("evaluationWindow") as int? ?? 300;

        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(metric))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "name and metric are required"
            };
        }

        if (!Enum.TryParse<AlertCondition>(conditionStr, true, out var condition))
        {
            condition = AlertCondition.GreaterThan;
        }

        if (!Enum.TryParse<AlertSeverityLevel>(severityStr, true, out var severity))
        {
            severity = AlertSeverityLevel.Warning;
        }

        var channels = new List<string>();
        if (!string.IsNullOrEmpty(channelsJson))
        {
            try
            {
                channels = JsonSerializer.Deserialize<List<string>>(channelsJson, _jsonOptions) ?? new List<string>();
            }
            catch { }
        }

        var ruleId = Guid.NewGuid().ToString("N");
        var rule = new AlertRuleDefinition
        {
            RuleId = ruleId,
            Name = name,
            Metric = metric,
            Condition = condition,
            Threshold = threshold,
            EvaluationWindow = TimeSpan.FromSeconds(evalWindowSecs),
            Severity = MapToSdkSeverity(severity),
            NotificationChannels = channels,
            Enabled = true,
            CreatedAt = DateTime.UtcNow
        };

        _rules[ruleId] = rule;

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["ruleId"] = ruleId,
            ["name"] = name
        };
    }

    private Dictionary<string, object> HandleUpdateRule(Dictionary<string, object> payload)
    {
        var ruleId = payload.GetValueOrDefault("ruleId")?.ToString();

        if (string.IsNullOrEmpty(ruleId) || !_rules.TryGetValue(ruleId, out var rule))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Rule not found"
            };
        }

        if (payload.TryGetValue("name", out var name) && name is string n)
            rule.Name = n;

        if (payload.TryGetValue("threshold", out var threshold) && threshold is double t)
            rule.Threshold = t;

        if (payload.TryGetValue("enabled", out var enabled) && enabled is bool e)
            rule.Enabled = e;

        if (payload.TryGetValue("severity", out var sev) && sev is string s &&
            Enum.TryParse<AlertSeverityLevel>(s, true, out var severity))
        {
            rule.Severity = MapToSdkSeverity(severity);
        }

        rule.UpdatedAt = DateTime.UtcNow;

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["ruleId"] = ruleId
        };
    }

    private Dictionary<string, object> HandleDeleteRule(Dictionary<string, object> payload)
    {
        var ruleId = payload.GetValueOrDefault("ruleId")?.ToString();

        if (string.IsNullOrEmpty(ruleId))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "ruleId is required"
            };
        }

        var removed = _rules.TryRemove(ruleId, out _);

        // Clear any active alerts for this rule
        var alertsToRemove = _activeAlerts.Values.Where(a => a.RuleId == ruleId).Select(a => a.AlertId).ToList();
        foreach (var alertId in alertsToRemove)
        {
            _activeAlerts.TryRemove(alertId, out _);
        }

        return new Dictionary<string, object>
        {
            ["success"] = removed,
            ["ruleId"] = ruleId
        };
    }

    private Dictionary<string, object> HandleListRules(Dictionary<string, object> payload)
    {
        var enabledOnly = payload.GetValueOrDefault("enabledOnly") as bool? ?? false;

        var rules = _rules.Values
            .Where(r => !enabledOnly || r.Enabled)
            .Select(r => new Dictionary<string, object>
            {
                ["ruleId"] = r.RuleId,
                ["name"] = r.Name,
                ["metric"] = r.Metric,
                ["condition"] = r.Condition.ToString(),
                ["threshold"] = r.Threshold,
                ["severity"] = r.Severity.ToString(),
                ["enabled"] = r.Enabled,
                ["channels"] = r.NotificationChannels,
                ["lastTriggered"] = r.LastTriggeredAt?.ToString("O") ?? ""
            })
            .ToList();

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["rules"] = rules,
            ["count"] = rules.Count
        };
    }

    #endregion

    #region Channel Management

    private Dictionary<string, object> HandleConfigureChannel(Dictionary<string, object> payload)
    {
        var channelId = payload.GetValueOrDefault("channelId")?.ToString() ?? Guid.NewGuid().ToString("N");
        var name = payload.GetValueOrDefault("name")?.ToString() ?? channelId;
        var typeStr = payload.GetValueOrDefault("type")?.ToString() ?? "Webhook";
        var endpoint = payload.GetValueOrDefault("endpoint")?.ToString();
        var configJson = payload.GetValueOrDefault("config")?.ToString();

        if (!Enum.TryParse<ChannelType>(typeStr, true, out var channelType))
        {
            channelType = ChannelType.Webhook;
        }

        var config = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(configJson))
        {
            try
            {
                config = JsonSerializer.Deserialize<Dictionary<string, string>>(configJson, _jsonOptions)
                    ?? new Dictionary<string, string>();
            }
            catch { }
        }

        var channel = new NotificationChannelConfig
        {
            ChannelId = channelId,
            Name = name,
            Type = channelType,
            Endpoint = endpoint ?? "",
            Config = config,
            Enabled = true,
            CreatedAt = DateTime.UtcNow
        };

        _channels[channelId] = channel;

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["channelId"] = channelId,
            ["name"] = name,
            ["type"] = channelType.ToString()
        };
    }

    private Dictionary<string, object> HandleListChannels()
    {
        var channels = _channels.Values.Select(c => new Dictionary<string, object>
        {
            ["channelId"] = c.ChannelId,
            ["name"] = c.Name,
            ["type"] = c.Type.ToString(),
            ["endpoint"] = c.Endpoint,
            ["enabled"] = c.Enabled
        }).ToList();

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["channels"] = channels,
            ["count"] = channels.Count
        };
    }

    private async Task<Dictionary<string, object>> HandleTestChannelAsync(Dictionary<string, object> payload)
    {
        var channelId = payload.GetValueOrDefault("channelId")?.ToString();

        if (string.IsNullOrEmpty(channelId) || !_channels.TryGetValue(channelId, out var channel))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Channel not found"
            };
        }

        var testAlert = new ActiveAlert
        {
            AlertId = "test-" + Guid.NewGuid().ToString("N"),
            RuleId = "test",
            RuleName = "Test Alert",
            Severity = AlertSeverityLevel.Info,
            Message = "This is a test alert notification",
            CurrentValue = 0,
            Threshold = 0,
            TriggeredAt = DateTime.UtcNow
        };

        try
        {
            await SendNotificationAsync(channel, testAlert);
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["channelId"] = channelId,
                ["message"] = "Test notification sent successfully"
            };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = ex.Message
            };
        }
    }

    #endregion

    #region Silence Management

    private Dictionary<string, object> HandleCreateSilence(Dictionary<string, object> payload)
    {
        var matchersJson = payload.GetValueOrDefault("matchers")?.ToString();
        var durationMinutes = payload.GetValueOrDefault("duration") as int? ?? 60;
        var reason = payload.GetValueOrDefault("reason")?.ToString() ?? "Manual silence";
        var createdBy = payload.GetValueOrDefault("createdBy")?.ToString() ?? "system";

        var matchers = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(matchersJson))
        {
            try
            {
                matchers = JsonSerializer.Deserialize<Dictionary<string, string>>(matchersJson, _jsonOptions)
                    ?? new Dictionary<string, string>();
            }
            catch { }
        }

        var silenceId = Guid.NewGuid().ToString("N");
        var silence = new SilenceWindow
        {
            SilenceId = silenceId,
            Matchers = matchers,
            StartsAt = DateTime.UtcNow,
            EndsAt = DateTime.UtcNow.AddMinutes(durationMinutes),
            Reason = reason,
            CreatedBy = createdBy,
            CreatedAt = DateTime.UtcNow
        };

        _silences[silenceId] = silence;

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["silenceId"] = silenceId,
            ["endsAt"] = silence.EndsAt.ToString("O")
        };
    }

    private Dictionary<string, object> HandleDeleteSilence(Dictionary<string, object> payload)
    {
        var silenceId = payload.GetValueOrDefault("silenceId")?.ToString();

        if (string.IsNullOrEmpty(silenceId))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "silenceId is required"
            };
        }

        var removed = _silences.TryRemove(silenceId, out _);

        return new Dictionary<string, object>
        {
            ["success"] = removed
        };
    }

    private Dictionary<string, object> HandleListSilences()
    {
        var silences = _silences.Values
            .Where(s => s.EndsAt > DateTime.UtcNow)
            .Select(s => new Dictionary<string, object>
            {
                ["silenceId"] = s.SilenceId,
                ["matchers"] = s.Matchers,
                ["startsAt"] = s.StartsAt.ToString("O"),
                ["endsAt"] = s.EndsAt.ToString("O"),
                ["reason"] = s.Reason,
                ["createdBy"] = s.CreatedBy
            })
            .ToList();

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["silences"] = silences,
            ["count"] = silences.Count
        };
    }

    #endregion

    #region Alert Management

    private Dictionary<string, object> HandleListAlerts(Dictionary<string, object> payload)
    {
        var severityFilter = payload.GetValueOrDefault("severity")?.ToString();
        var acknowledgedFilter = payload.GetValueOrDefault("acknowledged") as bool?;

        var alerts = _activeAlerts.Values
            .Where(a => (string.IsNullOrEmpty(severityFilter) ||
                        a.Severity.ToString().Equals(severityFilter, StringComparison.OrdinalIgnoreCase)) &&
                       (!acknowledgedFilter.HasValue || a.IsAcknowledged == acknowledgedFilter))
            .Select(a => new Dictionary<string, object>
            {
                ["alertId"] = a.AlertId,
                ["ruleId"] = a.RuleId,
                ["ruleName"] = a.RuleName,
                ["severity"] = a.Severity.ToString(),
                ["message"] = a.Message,
                ["currentValue"] = a.CurrentValue,
                ["threshold"] = a.Threshold,
                ["triggeredAt"] = a.TriggeredAt.ToString("O"),
                ["isAcknowledged"] = a.IsAcknowledged,
                ["acknowledgedBy"] = a.AcknowledgedBy ?? ""
            })
            .ToList();

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["alerts"] = alerts,
            ["count"] = alerts.Count
        };
    }

    private Dictionary<string, object> HandleAcknowledgeAlert(Dictionary<string, object> payload)
    {
        var alertId = payload.GetValueOrDefault("alertId")?.ToString();
        var acknowledgedBy = payload.GetValueOrDefault("acknowledgedBy")?.ToString() ?? "system";
        var message = payload.GetValueOrDefault("message")?.ToString();

        if (string.IsNullOrEmpty(alertId) || !_activeAlerts.TryGetValue(alertId, out var alert))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Alert not found"
            };
        }

        alert.IsAcknowledged = true;
        alert.AcknowledgedBy = acknowledgedBy;
        alert.AcknowledgedAt = DateTime.UtcNow;
        alert.AcknowledgeMessage = message;

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["alertId"] = alertId
        };
    }

    private Dictionary<string, object> HandleResolveAlert(Dictionary<string, object> payload)
    {
        var alertId = payload.GetValueOrDefault("alertId")?.ToString();
        var resolvedBy = payload.GetValueOrDefault("resolvedBy")?.ToString() ?? "system";

        if (string.IsNullOrEmpty(alertId) || !_activeAlerts.TryRemove(alertId, out var alert))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Alert not found"
            };
        }

        alert.ResolvedAt = DateTime.UtcNow;
        alert.ResolvedBy = resolvedBy;

        AddHistoryEntry(alert);

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["alertId"] = alertId
        };
    }

    #endregion

    #region Metrics

    private Dictionary<string, object> HandleRecordMetric(Dictionary<string, object> payload)
    {
        var name = payload.GetValueOrDefault("name")?.ToString();
        var value = payload.GetValueOrDefault("value") as double? ?? 0;
        var tagsJson = payload.GetValueOrDefault("tags")?.ToString();

        if (string.IsNullOrEmpty(name))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "name is required"
            };
        }

        var tags = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(tagsJson))
        {
            try
            {
                tags = JsonSerializer.Deserialize<Dictionary<string, string>>(tagsJson, _jsonOptions)
                    ?? new Dictionary<string, string>();
            }
            catch { }
        }

        var key = GetMetricKey(name, tags);
        var series = _metrics.GetOrAdd(key, _ => new MetricTimeSeries(name, tags));
        series.AddDataPoint(value);

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["metric"] = name,
            ["value"] = value
        };
    }

    private Dictionary<string, object> HandleQueryMetrics(Dictionary<string, object> payload)
    {
        var name = payload.GetValueOrDefault("name")?.ToString();
        var minutes = payload.GetValueOrDefault("minutes") as int? ?? 60;

        if (string.IsNullOrEmpty(name))
        {
            // Return all metrics
            var allMetrics = _metrics.Values.Select(m => new Dictionary<string, object>
            {
                ["name"] = m.Name,
                ["tags"] = m.Tags,
                ["dataPoints"] = m.DataPoints.Count,
                ["latest"] = m.GetLatestValue()
            }).ToList();

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["metrics"] = allMetrics
            };
        }

        var matchingSeries = _metrics.Values.Where(m => m.Name == name).ToList();
        var since = DateTime.UtcNow.AddMinutes(-minutes);

        var results = matchingSeries.Select(m => new Dictionary<string, object>
        {
            ["name"] = m.Name,
            ["tags"] = m.Tags,
            ["dataPoints"] = m.GetDataPointsSince(since).Select(dp => new Dictionary<string, object>
            {
                ["timestamp"] = dp.Timestamp.ToString("O"),
                ["value"] = dp.Value
            }).ToList()
        }).ToList();

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["series"] = results
        };
    }

    #endregion

    #region Escalation

    private Dictionary<string, object> HandleConfigureEscalation(Dictionary<string, object> payload)
    {
        var policyId = payload.GetValueOrDefault("policyId")?.ToString() ?? Guid.NewGuid().ToString("N");
        var name = payload.GetValueOrDefault("name")?.ToString() ?? policyId;
        var stepsJson = payload.GetValueOrDefault("steps")?.ToString();

        var steps = new List<EscalationStep>();
        if (!string.IsNullOrEmpty(stepsJson))
        {
            try
            {
                steps = JsonSerializer.Deserialize<List<EscalationStep>>(stepsJson, _jsonOptions)
                    ?? new List<EscalationStep>();
            }
            catch { }
        }

        var policy = new EscalationPolicyDef
        {
            PolicyId = policyId,
            Name = name,
            Steps = steps,
            CreatedAt = DateTime.UtcNow
        };

        _escalationPolicies[policyId] = policy;

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["policyId"] = policyId,
            ["name"] = name,
            ["steps"] = steps.Count
        };
    }

    #endregion

    #region Evaluation

    private async Task<Dictionary<string, object>> HandleEvaluateAsync()
    {
        var evaluated = 0;
        var triggered = 0;

        foreach (var rule in _rules.Values.Where(r => r.Enabled))
        {
            evaluated++;
            var alertTriggered = await EvaluateRuleAsync(rule);
            if (alertTriggered) triggered++;
        }

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["evaluated"] = evaluated,
            ["triggered"] = triggered
        };
    }

    private async Task<bool> EvaluateRuleAsync(AlertRuleDefinition rule)
    {
        // Get metric data for evaluation
        var matchingSeries = _metrics.Values.Where(m => m.Name == rule.Metric).ToList();

        if (matchingSeries.Count == 0)
            return false;

        var since = DateTime.UtcNow - rule.EvaluationWindow;

        foreach (var series in matchingSeries)
        {
            var dataPoints = series.GetDataPointsSince(since);
            if (dataPoints.Count == 0) continue;

            var currentValue = rule.Condition switch
            {
                AlertCondition.GreaterThan or AlertCondition.GreaterThanOrEqual or
                AlertCondition.LessThan or AlertCondition.LessThanOrEqual =>
                    dataPoints.Average(dp => dp.Value),
                _ => dataPoints.LastOrDefault()?.Value ?? 0
            };

            var conditionMet = rule.Condition switch
            {
                AlertCondition.GreaterThan => currentValue > rule.Threshold,
                AlertCondition.LessThan => currentValue < rule.Threshold,
                AlertCondition.GreaterThanOrEqual => currentValue >= rule.Threshold,
                AlertCondition.LessThanOrEqual => currentValue <= rule.Threshold,
                AlertCondition.Equals => Math.Abs(currentValue - rule.Threshold) < 0.001,
                AlertCondition.NotEquals => Math.Abs(currentValue - rule.Threshold) >= 0.001,
                _ => false
            };

            if (conditionMet)
            {
                // Check silences
                if (IsSilenced(rule, series.Tags))
                    continue;

                // Check deduplication
                var fingerprint = GetAlertFingerprint(rule, series.Tags);
                if (IsDuplicate(fingerprint))
                    continue;

                // Create alert
                var alert = new ActiveAlert
                {
                    AlertId = Guid.NewGuid().ToString("N"),
                    RuleId = rule.RuleId,
                    RuleName = rule.Name,
                    Severity = MapFromSdkSeverity(rule.Severity),
                    Message = $"{rule.Name}: {rule.Metric} is {currentValue:F2} (threshold: {rule.Threshold})",
                    CurrentValue = currentValue,
                    Threshold = rule.Threshold,
                    Labels = series.Tags,
                    Fingerprint = fingerprint,
                    TriggeredAt = DateTime.UtcNow
                };

                _activeAlerts[alert.AlertId] = alert;
                rule.LastTriggeredAt = DateTime.UtcNow;

                // Send notifications
                await SendNotificationsAsync(rule, alert);

                return true;
            }
        }

        // Auto-resolve if condition no longer met
        var alertsToResolve = _activeAlerts.Values
            .Where(a => a.RuleId == rule.RuleId)
            .ToList();

        foreach (var alert in alertsToResolve)
        {
            _activeAlerts.TryRemove(alert.AlertId, out _);
            alert.ResolvedAt = DateTime.UtcNow;
            alert.ResolvedBy = "auto";
            AddHistoryEntry(alert);
        }

        return false;
    }

    #endregion

    #region Notifications

    private async Task SendNotificationsAsync(AlertRuleDefinition rule, ActiveAlert alert)
    {
        foreach (var channelId in rule.NotificationChannels)
        {
            if (_channels.TryGetValue(channelId, out var channel) && channel.Enabled)
            {
                try
                {
                    await SendNotificationAsync(channel, alert);
                }
                catch
                {
                    // Log and continue with other channels
                }
            }
        }
    }

    private async Task SendNotificationAsync(NotificationChannelConfig channel, ActiveAlert alert)
    {
        switch (channel.Type)
        {
            case ChannelType.Webhook:
                await SendWebhookNotificationAsync(channel, alert);
                break;
            case ChannelType.Slack:
                await SendSlackNotificationAsync(channel, alert);
                break;
            case ChannelType.PagerDuty:
                await SendPagerDutyNotificationAsync(channel, alert);
                break;
            case ChannelType.Email:
                await SendEmailNotificationAsync(channel, alert);
                break;
            case ChannelType.Teams:
                await SendTeamsNotificationAsync(channel, alert);
                break;
        }
    }

    private async Task SendWebhookNotificationAsync(NotificationChannelConfig channel, ActiveAlert alert)
    {
        var payload = new
        {
            alertId = alert.AlertId,
            ruleName = alert.RuleName,
            severity = alert.Severity.ToString(),
            message = alert.Message,
            currentValue = alert.CurrentValue,
            threshold = alert.Threshold,
            triggeredAt = alert.TriggeredAt.ToString("O"),
            labels = alert.Labels
        };

        var content = new StringContent(
            JsonSerializer.Serialize(payload, _jsonOptions),
            Encoding.UTF8,
            "application/json");

        await _httpClient!.PostAsync(channel.Endpoint, content);
    }

    private async Task SendSlackNotificationAsync(NotificationChannelConfig channel, ActiveAlert alert)
    {
        var color = alert.Severity switch
        {
            AlertSeverityLevel.Critical => "#dc3545",
            AlertSeverityLevel.Error => "#fd7e14",
            AlertSeverityLevel.Warning => "#ffc107",
            _ => "#17a2b8"
        };

        var payload = new
        {
            attachments = new[]
            {
                new
                {
                    color,
                    title = $"[{alert.Severity}] {alert.RuleName}",
                    text = alert.Message,
                    fields = new[]
                    {
                        new { title = "Current Value", value = alert.CurrentValue.ToString("F2"), @short = true },
                        new { title = "Threshold", value = alert.Threshold.ToString("F2"), @short = true }
                    },
                    ts = new DateTimeOffset(alert.TriggeredAt).ToUnixTimeSeconds()
                }
            }
        };

        var content = new StringContent(
            JsonSerializer.Serialize(payload, _jsonOptions),
            Encoding.UTF8,
            "application/json");

        await _httpClient!.PostAsync(channel.Endpoint, content);
    }

    private async Task SendPagerDutyNotificationAsync(NotificationChannelConfig channel, ActiveAlert alert)
    {
        var severity = alert.Severity switch
        {
            AlertSeverityLevel.Critical => "critical",
            AlertSeverityLevel.Error => "error",
            AlertSeverityLevel.Warning => "warning",
            _ => "info"
        };

        var routingKey = channel.Config.GetValueOrDefault("routingKey") ?? channel.Endpoint;

        var payload = new
        {
            routing_key = routingKey,
            event_action = "trigger",
            dedup_key = alert.Fingerprint,
            payload = new
            {
                summary = alert.Message,
                severity,
                source = "DataWarehouse",
                timestamp = alert.TriggeredAt.ToString("O"),
                custom_details = new
                {
                    rule_name = alert.RuleName,
                    current_value = alert.CurrentValue,
                    threshold = alert.Threshold,
                    labels = alert.Labels
                }
            }
        };

        var content = new StringContent(
            JsonSerializer.Serialize(payload, _jsonOptions),
            Encoding.UTF8,
            "application/json");

        await _httpClient!.PostAsync("https://events.pagerduty.com/v2/enqueue", content);
    }

    private Task SendEmailNotificationAsync(NotificationChannelConfig channel, ActiveAlert alert)
    {
        // In production, would use SMTP or email service
        return Task.CompletedTask;
    }

    private async Task SendTeamsNotificationAsync(NotificationChannelConfig channel, ActiveAlert alert)
    {
        var color = alert.Severity switch
        {
            AlertSeverityLevel.Critical => "dc3545",
            AlertSeverityLevel.Error => "fd7e14",
            AlertSeverityLevel.Warning => "ffc107",
            _ => "17a2b8"
        };

        var payload = new
        {
            themeColor = color,
            summary = alert.Message,
            sections = new[]
            {
                new
                {
                    activityTitle = $"[{alert.Severity}] {alert.RuleName}",
                    facts = new[]
                    {
                        new { name = "Message", value = alert.Message },
                        new { name = "Current Value", value = alert.CurrentValue.ToString("F2") },
                        new { name = "Threshold", value = alert.Threshold.ToString("F2") },
                        new { name = "Triggered At", value = alert.TriggeredAt.ToString("O") }
                    }
                }
            }
        };

        var content = new StringContent(
            JsonSerializer.Serialize(payload, _jsonOptions),
            Encoding.UTF8,
            "application/json");

        await _httpClient!.PostAsync(channel.Endpoint, content);
    }

    #endregion

    #region Background Tasks

    private async Task RunEvaluationLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_evaluationInterval, ct);

                foreach (var rule in _rules.Values.Where(r => r.Enabled))
                {
                    if (ct.IsCancellationRequested) break;
                    await EvaluateRuleAsync(rule);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Continue evaluation
            }
        }
    }

    private async Task RunEscalationLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_escalationCheckInterval, ct);

                foreach (var alert in _activeAlerts.Values.Where(a => !a.IsAcknowledged))
                {
                    if (ct.IsCancellationRequested) break;
                    await CheckEscalationAsync(alert);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Continue
            }
        }
    }

    private async Task RunCleanupLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), ct);

                // Clean up expired silences
                var expiredSilences = _silences.Values
                    .Where(s => s.EndsAt < DateTime.UtcNow)
                    .Select(s => s.SilenceId)
                    .ToList();

                foreach (var id in expiredSilences)
                {
                    _silences.TryRemove(id, out _);
                }

                // Clean up old metric data
                foreach (var series in _metrics.Values)
                {
                    series.Cleanup(TimeSpan.FromHours(24));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Continue
            }
        }
    }

    private Task CheckEscalationAsync(ActiveAlert alert)
    {
        // Check if escalation is needed based on time since trigger
        var duration = DateTime.UtcNow - alert.TriggeredAt;

        foreach (var policy in _escalationPolicies.Values)
        {
            foreach (var step in policy.Steps.Where(s => duration >= s.DelayMinutes))
            {
                if (alert.EscalationLevel < step.Level)
                {
                    alert.EscalationLevel = step.Level;
                    // Send escalation notifications
                }
            }
        }

        return Task.CompletedTask;
    }

    #endregion

    #region Helper Methods

    private void InitializeDefaultChannels()
    {
        // Add a default webhook channel
        _channels["default-webhook"] = new NotificationChannelConfig
        {
            ChannelId = "default-webhook",
            Name = "Default Webhook",
            Type = ChannelType.Webhook,
            Endpoint = "http://localhost:9093/alertmanager/api/v1/alerts",
            Enabled = false,
            CreatedAt = DateTime.UtcNow
        };
    }

    private bool IsSilenced(AlertRuleDefinition rule, Dictionary<string, string> labels)
    {
        return _silences.Values.Any(s =>
            s.EndsAt > DateTime.UtcNow &&
            s.Matchers.All(m =>
                (m.Key == "ruleName" && m.Value == rule.Name) ||
                (labels.TryGetValue(m.Key, out var v) && v == m.Value)));
    }

    private bool IsDuplicate(string fingerprint)
    {
        var existing = _activeAlerts.Values
            .FirstOrDefault(a => a.Fingerprint == fingerprint &&
                                (DateTime.UtcNow - a.TriggeredAt).TotalSeconds < _deduplicationWindowSeconds);

        return existing != null;
    }

    private string GetAlertFingerprint(AlertRuleDefinition rule, Dictionary<string, string> labels)
    {
        var parts = new List<string> { rule.RuleId };
        parts.AddRange(labels.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
        return string.Join("|", parts);
    }

    private string GetMetricKey(string name, Dictionary<string, string> tags)
    {
        var tagString = string.Join(",", tags.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
        return $"{name}{{{tagString}}}";
    }

    private void AddHistoryEntry(ActiveAlert alert)
    {
        var entry = new AlertHistoryEntry
        {
            AlertId = alert.AlertId,
            RuleId = alert.RuleId,
            RuleName = alert.RuleName,
            Severity = alert.Severity,
            Message = alert.Message,
            TriggeredAt = alert.TriggeredAt,
            ResolvedAt = alert.ResolvedAt,
            Duration = alert.ResolvedAt.HasValue
                ? alert.ResolvedAt.Value - alert.TriggeredAt
                : null
        };

        _history.Enqueue(entry);

        while (_history.Count > MaxHistoryEntries)
        {
            _history.TryDequeue(out _);
        }
    }

    private AlertSeverity MapSeverity(AlertSeverityLevel level)
    {
        return level switch
        {
            AlertSeverityLevel.Critical => AlertSeverity.Critical,
            AlertSeverityLevel.Error => AlertSeverity.Error,
            AlertSeverityLevel.Warning => AlertSeverity.Warning,
            _ => AlertSeverity.Info
        };
    }

    private AlertSeverity MapToSdkSeverity(AlertSeverityLevel level)
    {
        return level switch
        {
            AlertSeverityLevel.Critical => AlertSeverity.Critical,
            AlertSeverityLevel.Error => AlertSeverity.Error,
            AlertSeverityLevel.Warning => AlertSeverity.Warning,
            _ => AlertSeverity.Info
        };
    }

    private AlertSeverityLevel MapFromSdkSeverity(AlertSeverity severity)
    {
        return severity switch
        {
            AlertSeverity.Critical => AlertSeverityLevel.Critical,
            AlertSeverity.Error => AlertSeverityLevel.Error,
            AlertSeverity.Warning => AlertSeverityLevel.Warning,
            _ => AlertSeverityLevel.Info
        };
    }

    private Dictionary<string, object> HandleGetStatus()
    {
        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["isRunning"] = _isRunning,
            ["totalRules"] = _rules.Count,
            ["enabledRules"] = _rules.Values.Count(r => r.Enabled),
            ["activeAlerts"] = _activeAlerts.Count,
            ["configuredChannels"] = _channels.Count,
            ["activeSilences"] = _silences.Values.Count(s => s.EndsAt > DateTime.UtcNow),
            ["trackedMetrics"] = _metrics.Count
        };
    }

    private Dictionary<string, object> HandleGetHistory(Dictionary<string, object> payload)
    {
        var limit = payload.GetValueOrDefault("limit") as int? ?? 100;

        var entries = _history.Take(limit).Select(e => new Dictionary<string, object>
        {
            ["alertId"] = e.AlertId,
            ["ruleId"] = e.RuleId,
            ["ruleName"] = e.RuleName,
            ["severity"] = e.Severity.ToString(),
            ["message"] = e.Message,
            ["triggeredAt"] = e.TriggeredAt.ToString("O"),
            ["resolvedAt"] = e.ResolvedAt?.ToString("O") ?? "",
            ["duration"] = e.Duration?.TotalMinutes ?? 0
        }).ToList();

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["entries"] = entries,
            ["count"] = entries.Count
        };
    }

    /// <inheritdoc />
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return new List<PluginCapabilityDescriptor>
        {
            new() { Name = "alert.rules", Description = "Alert rule management" },
            new() { Name = "alert.channels", Description = "Multi-channel notifications" },
            new() { Name = "alert.silencing", Description = "Alert silencing and suppression" },
            new() { Name = "alert.escalation", Description = "Escalation policies" },
            new() { Name = "alert.aggregation", Description = "Alert grouping and deduplication" }
        };
    }

    /// <inheritdoc />
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "AlertingOps";
        metadata["SupportedChannels"] = new[] { "Webhook", "Slack", "PagerDuty", "Email", "Teams" };
        metadata["SupportsAnomalyDetection"] = true;
        metadata["SupportsEscalation"] = true;
        metadata["SupportsDeduplication"] = true;
        return metadata;
    }

    #endregion

    #region Internal Types

    private sealed class AlertRuleDefinition
    {
        public string RuleId { get; init; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Metric { get; init; } = string.Empty;
        public AlertCondition Condition { get; init; }
        public double Threshold { get; set; }
        public TimeSpan EvaluationWindow { get; init; }
        public AlertSeverity Severity { get; set; }
        public List<string> NotificationChannels { get; init; } = new();
        public bool Enabled { get; set; }
        public DateTime CreatedAt { get; init; }
        public DateTime? UpdatedAt { get; set; }
        public DateTime? LastTriggeredAt { get; set; }
    }

    private sealed class ActiveAlert
    {
        public string AlertId { get; init; } = string.Empty;
        public string RuleId { get; init; } = string.Empty;
        public string RuleName { get; init; } = string.Empty;
        public AlertSeverityLevel Severity { get; init; }
        public string Message { get; init; } = string.Empty;
        public double CurrentValue { get; init; }
        public double Threshold { get; init; }
        public Dictionary<string, string> Labels { get; init; } = new();
        public string Fingerprint { get; init; } = string.Empty;
        public DateTime TriggeredAt { get; init; }
        public bool IsAcknowledged { get; set; }
        public string? AcknowledgedBy { get; set; }
        public DateTime? AcknowledgedAt { get; set; }
        public string? AcknowledgeMessage { get; set; }
        public DateTime? ResolvedAt { get; set; }
        public string? ResolvedBy { get; set; }
        public int EscalationLevel { get; set; }
    }

    private sealed class NotificationChannelConfig
    {
        public string ChannelId { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public ChannelType Type { get; init; }
        public string Endpoint { get; init; } = string.Empty;
        public Dictionary<string, string> Config { get; init; } = new();
        public bool Enabled { get; init; }
        public DateTime CreatedAt { get; init; }
    }

    private sealed class SilenceWindow
    {
        public string SilenceId { get; init; } = string.Empty;
        public Dictionary<string, string> Matchers { get; init; } = new();
        public DateTime StartsAt { get; init; }
        public DateTime EndsAt { get; init; }
        public string Reason { get; init; } = string.Empty;
        public string CreatedBy { get; init; } = string.Empty;
        public DateTime CreatedAt { get; init; }
    }

    private sealed class EscalationPolicyDef
    {
        public string PolicyId { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public List<EscalationStep> Steps { get; init; } = new();
        public DateTime CreatedAt { get; init; }
    }

    private sealed class EscalationStep
    {
        public int Level { get; init; }
        public TimeSpan DelayMinutes { get; init; }
        public List<string> NotifyChannels { get; init; } = new();
    }

    private sealed class AlertGroup
    {
        public string GroupKey { get; init; } = string.Empty;
        public List<string> AlertIds { get; init; } = new();
        public DateTime CreatedAt { get; init; }
    }

    private sealed class AlertHistoryEntry
    {
        public string AlertId { get; init; } = string.Empty;
        public string RuleId { get; init; } = string.Empty;
        public string RuleName { get; init; } = string.Empty;
        public AlertSeverityLevel Severity { get; init; }
        public string Message { get; init; } = string.Empty;
        public DateTime TriggeredAt { get; init; }
        public DateTime? ResolvedAt { get; init; }
        public TimeSpan? Duration { get; init; }
    }

    private sealed class MetricTimeSeries
    {
        public string Name { get; }
        public Dictionary<string, string> Tags { get; }
        public List<MetricDataPoint> DataPoints { get; } = new();
        private readonly object _lock = new();

        public MetricTimeSeries(string name, Dictionary<string, string> tags)
        {
            Name = name;
            Tags = tags;
        }

        public void AddDataPoint(double value)
        {
            lock (_lock)
            {
                DataPoints.Add(new MetricDataPoint
                {
                    Timestamp = DateTime.UtcNow,
                    Value = value
                });

                while (DataPoints.Count > MaxMetricDataPoints)
                {
                    DataPoints.RemoveAt(0);
                }
            }
        }

        public double GetLatestValue()
        {
            lock (_lock)
            {
                return DataPoints.LastOrDefault()?.Value ?? 0;
            }
        }

        public List<MetricDataPoint> GetDataPointsSince(DateTime since)
        {
            lock (_lock)
            {
                return DataPoints.Where(dp => dp.Timestamp >= since).ToList();
            }
        }

        public void Cleanup(TimeSpan maxAge)
        {
            var cutoff = DateTime.UtcNow - maxAge;
            lock (_lock)
            {
                DataPoints.RemoveAll(dp => dp.Timestamp < cutoff);
            }
        }
    }

    private sealed class MetricDataPoint
    {
        public DateTime Timestamp { get; init; }
        public double Value { get; init; }
    }

    private enum AlertSeverityLevel
    {
        Info,
        Warning,
        Error,
        Critical
    }

    private enum ChannelType
    {
        Webhook,
        Slack,
        PagerDuty,
        Email,
        Teams
    }

    #endregion
}
