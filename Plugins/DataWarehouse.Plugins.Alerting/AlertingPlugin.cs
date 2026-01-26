using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.Alerting
{
    /// <summary>
    /// Advanced alerting plugin providing comprehensive alert management capabilities.
    /// Implements threshold, rate-of-change, and anomaly-based alerting with multi-channel
    /// notification routing, deduplication, grouping, and escalation policies.
    ///
    /// Features:
    /// - Threshold alerts: Trigger when metrics exceed defined thresholds
    /// - Rate-of-change alerts: Detect sudden spikes or drops in metrics
    /// - Anomaly alerts: Statistical anomaly detection using moving averages and standard deviation
    /// - Multi-channel routing: Email, Webhook, PagerDuty, Slack, Microsoft Teams
    /// - Alert deduplication: Prevent notification storms
    /// - Alert grouping: Group related alerts to reduce noise
    /// - Silencing windows: Suppress alerts during maintenance
    /// - Escalation policies: Automatic escalation based on severity and duration
    /// - Alert history: Full audit trail and analytics
    ///
    /// Message Commands:
    /// - alert.rule.create: Create a new alert rule
    /// - alert.rule.update: Update an existing rule
    /// - alert.rule.delete: Delete a rule
    /// - alert.rule.list: List all rules
    /// - alert.evaluate: Manually evaluate rules against current metrics
    /// - alert.silence.create: Create a silence window
    /// - alert.silence.delete: Remove a silence
    /// - alert.acknowledge: Acknowledge an active alert
    /// - alert.resolve: Manually resolve an alert
    /// - alert.history: Query alert history
    /// - alert.channel.configure: Configure notification channels
    /// - alert.escalation.configure: Configure escalation policies
    /// </summary>
    public sealed class AlertingPlugin : TelemetryPluginBase
    {
        /// <inheritdoc />
        public override string Id => "com.datawarehouse.telemetry.alerting";

        /// <inheritdoc />
        public override string Name => "Alerting Plugin";

        /// <inheritdoc />
        public override string Version => "1.0.0";

        /// <inheritdoc />
        public override TelemetryCapabilities Capabilities => TelemetryCapabilities.Metrics | TelemetryCapabilities.Logging;

        #region Private Fields

        // Alert rules storage
        private readonly ConcurrentDictionary<string, AlertRule> _rules = new();

        // Active alert instances
        private readonly ConcurrentDictionary<string, AlertInstance> _activeAlerts = new();

        // Alert history
        private readonly ConcurrentQueue<AlertHistoryEntry> _alertHistory = new();
        private readonly int _maxHistoryEntries = 10000;

        // Notification channels
        private readonly ConcurrentDictionary<string, NotificationChannel> _channels = new();

        // Escalation policies
        private readonly ConcurrentDictionary<string, EscalationPolicy> _escalationPolicies = new();

        // Silence windows
        private readonly ConcurrentDictionary<string, SilenceWindow> _silences = new();

        // Alert groups for deduplication
        private readonly ConcurrentDictionary<string, AlertGroup> _alertGroups = new();

        // Metric data storage for evaluation
        private readonly ConcurrentDictionary<string, MetricTimeSeries> _metricData = new();

        // Counters and histograms for telemetry
        private readonly ConcurrentDictionary<string, long> _counters = new();
        private readonly ConcurrentDictionary<string, List<double>> _histograms = new();

        // Log entries
        private readonly ConcurrentQueue<LogEntry> _logs = new();

        // Background tasks
        private CancellationTokenSource? _cts;
        private Task? _evaluationTask;
        private Task? _escalationTask;
        private Task? _cleanupTask;
        private HttpClient? _httpClient;
        private bool _isRunning;

        // Configuration
        private TimeSpan _evaluationInterval = TimeSpan.FromSeconds(30);
        private TimeSpan _escalationCheckInterval = TimeSpan.FromMinutes(1);
        private TimeSpan _groupingWindow = TimeSpan.FromMinutes(5);
        private int _deduplicationWindowSeconds = 300;

        // Trace spans
        private readonly AsyncLocal<ITraceSpan?> _currentSpan = new();

        #endregion

        #region Lifecycle

        /// <inheritdoc />
        public override async Task StartAsync(CancellationToken ct)
        {
            if (_isRunning) return;

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            _isRunning = true;

            // Initialize default notification channels
            InitializeDefaultChannels();

            // Start background evaluation loop
            _evaluationTask = RunEvaluationLoopAsync(_cts.Token);
            _escalationTask = RunEscalationLoopAsync(_cts.Token);
            _cleanupTask = RunCleanupLoopAsync(_cts.Token);

            LogEvent(LogLevel.Information, "Alerting plugin started");
            await Task.CompletedTask;
        }

        /// <inheritdoc />
        public override async Task StopAsync()
        {
            if (!_isRunning) return;

            _isRunning = false;
            _cts?.Cancel();

            // Wait for background tasks
            var tasks = new List<Task>();
            if (_evaluationTask != null) tasks.Add(_evaluationTask);
            if (_escalationTask != null) tasks.Add(_escalationTask);
            if (_cleanupTask != null) tasks.Add(_cleanupTask);

            await Task.WhenAll(tasks.Select(t => t.ContinueWith(_ => { })));

            _httpClient?.Dispose();
            _httpClient = null;
            _cts?.Dispose();
            _cts = null;

            LogEvent(LogLevel.Information, "Alerting plugin stopped");
        }

        #endregion

        #region ITelemetryProvider Implementation

        /// <inheritdoc />
        public override void RecordMetric(string name, double value, Dictionary<string, string>? tags = null)
        {
            var key = GetMetricKey(name, tags);
            var series = _metricData.GetOrAdd(key, _ => new MetricTimeSeries(name, tags ?? new Dictionary<string, string>()));
            series.AddDataPoint(value, DateTime.UtcNow);
        }

        /// <inheritdoc />
        public override void IncrementCounter(string name, long delta = 1, Dictionary<string, string>? tags = null)
        {
            var key = GetMetricKey(name, tags);
            _counters.AddOrUpdate(key, delta, (_, current) => current + delta);

            // Also record as metric for alerting
            var newValue = _counters[key];
            RecordMetric(name, newValue, tags);
        }

        /// <inheritdoc />
        public override void RecordHistogram(string name, double value, Dictionary<string, string>? tags = null)
        {
            var key = GetMetricKey(name, tags);
            var histogram = _histograms.GetOrAdd(key, _ => new List<double>());
            lock (histogram)
            {
                histogram.Add(value);
                if (histogram.Count > 1000)
                {
                    histogram.RemoveRange(0, histogram.Count - 1000);
                }
            }

            // Also record as metric for alerting
            RecordMetric(name, value, tags);
        }

        /// <inheritdoc />
        public override void LogEvent(LogLevel level, string message, Dictionary<string, object>? properties = null, Exception? exception = null)
        {
            var entry = new LogEntry
            {
                Timestamp = DateTime.UtcNow,
                Level = level,
                Message = message,
                Properties = properties ?? new Dictionary<string, object>(),
                ExceptionMessage = exception?.Message,
                ExceptionStackTrace = exception?.StackTrace,
                TraceId = _currentSpan.Value?.TraceId,
                SpanId = _currentSpan.Value?.SpanId
            };

            _logs.Enqueue(entry);

            // Keep log queue bounded
            while (_logs.Count > 10000)
            {
                _logs.TryDequeue(out _);
            }
        }

        /// <inheritdoc />
        public override async Task FlushAsync(CancellationToken ct = default)
        {
            // Process any pending alerts
            await EvaluateAllRulesAsync();
        }

        /// <inheritdoc />
        public override async Task<IReadOnlyDictionary<string, MetricSnapshot>> GetMetricsAsync(CancellationToken ct = default)
        {
            var result = new Dictionary<string, MetricSnapshot>();

            foreach (var (key, series) in _metricData)
            {
                var stats = series.GetStatistics();
                result[key] = new MetricSnapshot
                {
                    Name = series.Name,
                    Type = "gauge",
                    Value = stats.Current,
                    Min = stats.Min,
                    Max = stats.Max,
                    Mean = stats.Mean,
                    Count = stats.Count,
                    Timestamp = DateTime.UtcNow,
                    Tags = series.Tags
                };
            }

            return await Task.FromResult(result);
        }

        /// <inheritdoc />
        protected override ITraceSpan CreateSpan(string operationName, SpanKind kind, ITraceSpan? parent)
        {
            var span = new AlertingTraceSpan(operationName, kind, parent);
            _currentSpan.Value = span;
            return span;
        }

        #endregion

        #region Message Handling

        /// <inheritdoc />
        public override async Task OnMessageAsync(PluginMessage message)
        {
            if (message.Payload == null) return;

            var response = message.Type switch
            {
                "alert.rule.create" => HandleRuleCreate(message.Payload),
                "alert.rule.update" => HandleRuleUpdate(message.Payload),
                "alert.rule.delete" => HandleRuleDelete(message.Payload),
                "alert.rule.list" => HandleRuleList(),
                "alert.rule.get" => HandleRuleGet(message.Payload),
                "alert.evaluate" => await HandleEvaluateAsync(),
                "alert.silence.create" => HandleSilenceCreate(message.Payload),
                "alert.silence.delete" => HandleSilenceDelete(message.Payload),
                "alert.silence.list" => HandleSilenceList(),
                "alert.acknowledge" => HandleAcknowledge(message.Payload),
                "alert.resolve" => HandleResolve(message.Payload),
                "alert.active" => HandleGetActiveAlerts(),
                "alert.history" => HandleGetHistory(message.Payload),
                "alert.channel.configure" => HandleChannelConfigure(message.Payload),
                "alert.channel.list" => HandleChannelList(),
                "alert.channel.test" => await HandleChannelTestAsync(message.Payload),
                "alert.escalation.configure" => HandleEscalationConfigure(message.Payload),
                "alert.escalation.list" => HandleEscalationList(),
                "alert.analytics" => HandleGetAnalytics(message.Payload),
                "alert.configure" => HandleConfigure(message.Payload),
                _ => new Dictionary<string, object> { ["error"] = $"Unknown command: {message.Type}" }
            };

            message.Payload["_response"] = response;
        }

        #endregion

        #region Rule Management

        private Dictionary<string, object> HandleRuleCreate(Dictionary<string, object> payload)
        {
            try
            {
                var rule = DeserializeAlertRule(payload);

                if (string.IsNullOrEmpty(rule.Id))
                {
                    rule.Id = $"rule-{Guid.NewGuid():N}";
                }

                if (_rules.ContainsKey(rule.Id))
                {
                    return new Dictionary<string, object> { ["error"] = $"Rule with ID {rule.Id} already exists" };
                }

                rule.CreatedAt = DateTime.UtcNow;
                rule.UpdatedAt = DateTime.UtcNow;
                _rules[rule.Id] = rule;

                LogEvent(LogLevel.Information, $"Created alert rule: {rule.Name}", new Dictionary<string, object>
                {
                    ["ruleId"] = rule.Id,
                    ["type"] = rule.Type.ToString()
                });

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["ruleId"] = rule.Id,
                    ["rule"] = SerializeAlertRule(rule)
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { ["error"] = ex.Message };
            }
        }

        private Dictionary<string, object> HandleRuleUpdate(Dictionary<string, object> payload)
        {
            try
            {
                var ruleId = payload.GetValueOrDefault("id")?.ToString();
                if (string.IsNullOrEmpty(ruleId))
                {
                    return new Dictionary<string, object> { ["error"] = "Rule ID is required" };
                }

                if (!_rules.TryGetValue(ruleId, out var existingRule))
                {
                    return new Dictionary<string, object> { ["error"] = $"Rule not found: {ruleId}" };
                }

                var updatedRule = DeserializeAlertRule(payload);
                updatedRule.Id = ruleId;
                updatedRule.CreatedAt = existingRule.CreatedAt;
                updatedRule.UpdatedAt = DateTime.UtcNow;

                _rules[ruleId] = updatedRule;

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["ruleId"] = ruleId,
                    ["rule"] = SerializeAlertRule(updatedRule)
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { ["error"] = ex.Message };
            }
        }

        private Dictionary<string, object> HandleRuleDelete(Dictionary<string, object> payload)
        {
            var ruleId = payload.GetValueOrDefault("id")?.ToString();
            if (string.IsNullOrEmpty(ruleId))
            {
                return new Dictionary<string, object> { ["error"] = "Rule ID is required" };
            }

            if (_rules.TryRemove(ruleId, out _))
            {
                // Also remove any active alerts for this rule
                var alertsToRemove = _activeAlerts.Where(kv => kv.Value.RuleId == ruleId).Select(kv => kv.Key).ToList();
                foreach (var alertId in alertsToRemove)
                {
                    _activeAlerts.TryRemove(alertId, out _);
                }

                return new Dictionary<string, object> { ["success"] = true, ["ruleId"] = ruleId };
            }

            return new Dictionary<string, object> { ["error"] = $"Rule not found: {ruleId}" };
        }

        private Dictionary<string, object> HandleRuleList()
        {
            var rules = _rules.Values.Select(SerializeAlertRule).ToList();
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["count"] = rules.Count,
                ["rules"] = rules
            };
        }

        private Dictionary<string, object> HandleRuleGet(Dictionary<string, object> payload)
        {
            var ruleId = payload.GetValueOrDefault("id")?.ToString();
            if (string.IsNullOrEmpty(ruleId))
            {
                return new Dictionary<string, object> { ["error"] = "Rule ID is required" };
            }

            if (_rules.TryGetValue(ruleId, out var rule))
            {
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["rule"] = SerializeAlertRule(rule)
                };
            }

            return new Dictionary<string, object> { ["error"] = $"Rule not found: {ruleId}" };
        }

        #endregion

        #region Alert Evaluation

        private async Task RunEvaluationLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_evaluationInterval, ct);
                    await EvaluateAllRulesAsync();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogEvent(LogLevel.Error, "Error in evaluation loop", exception: ex);
                }
            }
        }

        private async Task<Dictionary<string, object>> HandleEvaluateAsync()
        {
            var results = await EvaluateAllRulesAsync();
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["rulesEvaluated"] = results.RulesEvaluated,
                ["alertsFiring"] = results.AlertsFiring,
                ["alertsResolved"] = results.AlertsResolved
            };
        }

        private async Task<EvaluationResult> EvaluateAllRulesAsync()
        {
            var result = new EvaluationResult();

            foreach (var rule in _rules.Values.Where(r => r.Enabled))
            {
                try
                {
                    result.RulesEvaluated++;
                    var evaluation = EvaluateRule(rule);

                    if (evaluation.IsFiring)
                    {
                        await HandleFiringAlertAsync(rule, evaluation);
                        result.AlertsFiring++;
                    }
                    else
                    {
                        var resolved = await TryResolveAlertAsync(rule);
                        if (resolved) result.AlertsResolved++;
                    }
                }
                catch (Exception ex)
                {
                    LogEvent(LogLevel.Error, $"Error evaluating rule {rule.Id}", exception: ex);
                }
            }

            return result;
        }

        /// <summary>
        /// Evaluates a single alert rule against current metric data.
        /// </summary>
        /// <param name="rule">The rule to evaluate.</param>
        /// <returns>The evaluation result.</returns>
        private RuleEvaluation EvaluateRule(AlertRule rule)
        {
            var metricKey = GetMetricKey(rule.Metric, rule.Labels);

            if (!_metricData.TryGetValue(metricKey, out var series))
            {
                return new RuleEvaluation { IsFiring = false, Reason = "No data available" };
            }

            var stats = series.GetStatistics(rule.EvaluationWindow);

            if (stats.Count == 0)
            {
                return new RuleEvaluation { IsFiring = false, Reason = "No data in evaluation window" };
            }

            return rule.Type switch
            {
                AlertRuleType.Threshold => EvaluateThresholdRule(rule, stats),
                AlertRuleType.RateOfChange => EvaluateRateOfChangeRule(rule, series),
                AlertRuleType.Anomaly => EvaluateAnomalyRule(rule, series, stats),
                AlertRuleType.Absence => EvaluateAbsenceRule(rule, series),
                AlertRuleType.Composite => EvaluateCompositeRule(rule),
                _ => new RuleEvaluation { IsFiring = false, Reason = "Unknown rule type" }
            };
        }

        private RuleEvaluation EvaluateThresholdRule(AlertRule rule, MetricStatistics stats)
        {
            var value = rule.AggregationType switch
            {
                AggregationType.Average => stats.Mean,
                AggregationType.Min => stats.Min,
                AggregationType.Max => stats.Max,
                AggregationType.Sum => stats.Sum,
                AggregationType.Count => stats.Count,
                AggregationType.Last => stats.Current,
                AggregationType.Percentile95 => stats.Percentile95,
                AggregationType.Percentile99 => stats.Percentile99,
                _ => stats.Current
            };

            var isFiring = rule.Condition switch
            {
                AlertCondition.GreaterThan => value > rule.Threshold,
                AlertCondition.GreaterThanOrEqual => value >= rule.Threshold,
                AlertCondition.LessThan => value < rule.Threshold,
                AlertCondition.LessThanOrEqual => value <= rule.Threshold,
                AlertCondition.Equal => Math.Abs(value - rule.Threshold) < 0.0001,
                AlertCondition.NotEqual => Math.Abs(value - rule.Threshold) >= 0.0001,
                _ => false
            };

            return new RuleEvaluation
            {
                IsFiring = isFiring,
                CurrentValue = value,
                ThresholdValue = rule.Threshold,
                Reason = isFiring
                    ? $"{rule.AggregationType}({rule.Metric}) = {value:F2} {rule.Condition} {rule.Threshold}"
                    : "Threshold not exceeded"
            };
        }

        private RuleEvaluation EvaluateRateOfChangeRule(AlertRule rule, MetricTimeSeries series)
        {
            var rateOfChange = series.GetRateOfChange(rule.EvaluationWindow);

            if (!rateOfChange.HasValue)
            {
                return new RuleEvaluation { IsFiring = false, Reason = "Insufficient data for rate calculation" };
            }

            var threshold = rule.RateOfChangeThreshold ?? rule.Threshold;
            var isFiring = rule.Condition switch
            {
                AlertCondition.GreaterThan => rateOfChange.Value > threshold,
                AlertCondition.LessThan => rateOfChange.Value < -threshold,
                _ => Math.Abs(rateOfChange.Value) > threshold
            };

            return new RuleEvaluation
            {
                IsFiring = isFiring,
                CurrentValue = rateOfChange.Value,
                ThresholdValue = threshold,
                Reason = isFiring
                    ? $"Rate of change: {rateOfChange.Value:F2}%/min exceeds threshold {threshold}%"
                    : "Rate of change within limits"
            };
        }

        private RuleEvaluation EvaluateAnomalyRule(AlertRule rule, MetricTimeSeries series, MetricStatistics stats)
        {
            // Use Z-score based anomaly detection
            var stdDevMultiplier = rule.AnomalyStdDevMultiplier ?? 3.0;
            var zScore = stats.StdDev > 0 ? (stats.Current - stats.Mean) / stats.StdDev : 0;

            var isAnomaly = Math.Abs(zScore) > stdDevMultiplier;

            return new RuleEvaluation
            {
                IsFiring = isAnomaly,
                CurrentValue = stats.Current,
                ThresholdValue = stats.Mean + (stdDevMultiplier * stats.StdDev),
                Reason = isAnomaly
                    ? $"Anomaly detected: Z-score = {zScore:F2} (threshold: {stdDevMultiplier})"
                    : "No anomaly detected"
            };
        }

        private RuleEvaluation EvaluateAbsenceRule(AlertRule rule, MetricTimeSeries series)
        {
            var lastDataPoint = series.GetLastDataPoint();
            if (lastDataPoint == null)
            {
                return new RuleEvaluation
                {
                    IsFiring = true,
                    Reason = "No data points found"
                };
            }

            var absenceThreshold = rule.AbsenceThreshold ?? TimeSpan.FromMinutes(5);
            var timeSinceLastData = DateTime.UtcNow - lastDataPoint.Timestamp;
            var isFiring = timeSinceLastData > absenceThreshold;

            return new RuleEvaluation
            {
                IsFiring = isFiring,
                Reason = isFiring
                    ? $"No data for {timeSinceLastData.TotalMinutes:F1} minutes (threshold: {absenceThreshold.TotalMinutes} min)"
                    : "Data is being received"
            };
        }

        private RuleEvaluation EvaluateCompositeRule(AlertRule rule)
        {
            if (rule.CompositeRules == null || rule.CompositeRules.Count == 0)
            {
                return new RuleEvaluation { IsFiring = false, Reason = "No composite rules defined" };
            }

            var results = new List<bool>();
            var reasons = new List<string>();

            foreach (var subRuleId in rule.CompositeRules)
            {
                if (_rules.TryGetValue(subRuleId, out var subRule))
                {
                    var eval = EvaluateRule(subRule);
                    results.Add(eval.IsFiring);
                    reasons.Add($"{subRule.Name}: {(eval.IsFiring ? "FIRING" : "OK")}");
                }
            }

            var isFiring = rule.CompositeOperator switch
            {
                CompositeOperator.And => results.All(r => r),
                CompositeOperator.Or => results.Any(r => r),
                _ => false
            };

            return new RuleEvaluation
            {
                IsFiring = isFiring,
                Reason = string.Join(", ", reasons)
            };
        }

        #endregion

        #region Alert Lifecycle

        private async Task HandleFiringAlertAsync(AlertRule rule, RuleEvaluation evaluation)
        {
            var alertKey = GenerateAlertKey(rule);

            // Check for silencing
            if (IsAlertSilenced(rule))
            {
                LogEvent(LogLevel.Debug, $"Alert {rule.Name} is silenced");
                return;
            }

            // Check for deduplication
            if (_activeAlerts.TryGetValue(alertKey, out var existingAlert))
            {
                // Update existing alert
                existingAlert.LastEvaluatedAt = DateTime.UtcNow;
                existingAlert.CurrentValue = evaluation.CurrentValue;
                existingAlert.EvaluationCount++;

                // Check if we need to re-notify (based on repeat interval)
                if (rule.RepeatInterval.HasValue &&
                    DateTime.UtcNow - existingAlert.LastNotifiedAt > rule.RepeatInterval.Value)
                {
                    await SendNotificationsAsync(existingAlert, NotificationEventType.Reminder);
                    existingAlert.LastNotifiedAt = DateTime.UtcNow;
                }

                return;
            }

            // Create new alert instance
            var alert = new AlertInstance
            {
                Id = $"alert-{Guid.NewGuid():N}",
                RuleId = rule.Id,
                RuleName = rule.Name,
                Metric = rule.Metric,
                Labels = rule.Labels ?? new Dictionary<string, string>(),
                Severity = rule.Severity,
                State = AlertState.Pending,
                CurrentValue = evaluation.CurrentValue,
                ThresholdValue = evaluation.ThresholdValue,
                Message = evaluation.Reason,
                FiredAt = DateTime.UtcNow,
                LastEvaluatedAt = DateTime.UtcNow,
                EvaluationCount = 1
            };

            // Check if pending duration is required
            if (rule.PendingDuration.HasValue && rule.PendingDuration.Value > TimeSpan.Zero)
            {
                _activeAlerts[alertKey] = alert;
                return;
            }

            // Transition to firing
            alert.State = AlertState.Firing;
            _activeAlerts[alertKey] = alert;

            // Group alert if grouping is enabled
            GroupAlert(alert, rule);

            // Send notifications
            await SendNotificationsAsync(alert, NotificationEventType.Firing);
            alert.LastNotifiedAt = DateTime.UtcNow;

            // Record in history
            RecordAlertHistory(alert, AlertHistoryEventType.Fired);

            LogEvent(LogLevel.Warning, $"Alert fired: {rule.Name}", new Dictionary<string, object>
            {
                ["alertId"] = alert.Id,
                ["ruleId"] = rule.Id,
                ["severity"] = alert.Severity.ToString(),
                ["value"] = evaluation.CurrentValue
            });
        }

        private async Task<bool> TryResolveAlertAsync(AlertRule rule)
        {
            var alertKey = GenerateAlertKey(rule);

            if (!_activeAlerts.TryRemove(alertKey, out var alert))
            {
                return false;
            }

            alert.State = AlertState.Resolved;
            alert.ResolvedAt = DateTime.UtcNow;

            // Send resolution notification
            await SendNotificationsAsync(alert, NotificationEventType.Resolved);

            // Record in history
            RecordAlertHistory(alert, AlertHistoryEventType.Resolved);

            LogEvent(LogLevel.Information, $"Alert resolved: {rule.Name}", new Dictionary<string, object>
            {
                ["alertId"] = alert.Id,
                ["ruleId"] = rule.Id,
                ["duration"] = (alert.ResolvedAt.Value - alert.FiredAt).TotalMinutes
            });

            return true;
        }

        private string GenerateAlertKey(AlertRule rule)
        {
            var labelString = rule.Labels != null
                ? string.Join(",", rule.Labels.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"))
                : "";
            return $"{rule.Id}:{rule.Metric}:{labelString}";
        }

        #endregion

        #region Notification System

        private void InitializeDefaultChannels()
        {
            // Console channel for development
            _channels["console"] = new NotificationChannel
            {
                Id = "console",
                Name = "Console Output",
                Type = ChannelType.Console,
                Enabled = true,
                CreatedAt = DateTime.UtcNow
            };
        }

        private async Task SendNotificationsAsync(AlertInstance alert, NotificationEventType eventType)
        {
            if (!_rules.TryGetValue(alert.RuleId, out var rule))
            {
                return;
            }

            var channels = rule.NotificationChannels ?? new List<string> { "console" };

            foreach (var channelId in channels)
            {
                if (_channels.TryGetValue(channelId, out var channel) && channel.Enabled)
                {
                    try
                    {
                        await SendToChannelAsync(channel, alert, eventType);
                    }
                    catch (Exception ex)
                    {
                        LogEvent(LogLevel.Error, $"Failed to send notification to channel {channelId}", exception: ex);
                    }
                }
            }
        }

        private async Task SendToChannelAsync(NotificationChannel channel, AlertInstance alert, NotificationEventType eventType)
        {
            var message = FormatAlertMessage(alert, eventType);

            switch (channel.Type)
            {
                case ChannelType.Console:
                    await SendToConsoleAsync(alert, eventType);
                    break;

                case ChannelType.Email:
                    await SendEmailAsync(channel, alert, message);
                    break;

                case ChannelType.Webhook:
                    await SendWebhookAsync(channel, alert, eventType);
                    break;

                case ChannelType.PagerDuty:
                    await SendPagerDutyAsync(channel, alert, eventType);
                    break;

                case ChannelType.Slack:
                    await SendSlackAsync(channel, alert, eventType);
                    break;

                case ChannelType.MicrosoftTeams:
                    await SendTeamsAsync(channel, alert, eventType);
                    break;
            }
        }

        private Task SendToConsoleAsync(AlertInstance alert, NotificationEventType eventType)
        {
            var severity = alert.Severity.ToString().ToUpperInvariant();
            var state = eventType.ToString().ToUpperInvariant();
            var message = $"[ALERT:{severity}] [{state}] {alert.RuleName} - {alert.Message} (value: {alert.CurrentValue:F2})";

#if DEBUG
            Console.WriteLine(message);
#else
            System.Diagnostics.Debug.WriteLine(message);
#endif
            return Task.CompletedTask;
        }

        private async Task SendEmailAsync(NotificationChannel channel, AlertInstance alert, string message)
        {
            // Validate required SMTP configuration
            if (string.IsNullOrEmpty(channel.SmtpHost))
            {
                LogEvent(LogLevel.Warning, "Cannot send email alert: SMTP host not configured",
                    new Dictionary<string, object> { ["channelId"] = channel.Id, ["alertId"] = alert.Id });
                return;
            }

            if (string.IsNullOrEmpty(channel.EmailTo))
            {
                LogEvent(LogLevel.Warning, "Cannot send email alert: recipient email not configured",
                    new Dictionary<string, object> { ["channelId"] = channel.Id, ["alertId"] = alert.Id });
                return;
            }

            if (string.IsNullOrEmpty(channel.EmailFrom))
            {
                LogEvent(LogLevel.Warning, "Cannot send email alert: sender email not configured",
                    new Dictionary<string, object> { ["channelId"] = channel.Id, ["alertId"] = alert.Id });
                return;
            }

            // Build email subject
            var subjectPrefix = channel.EmailSubjectPrefix ?? "[Alert]";
            var severityText = alert.Severity.ToString().ToUpperInvariant();
            var subject = $"{subjectPrefix} [{severityText}] {alert.RuleName}";

            // Build email body with HTML template
            var body = BuildEmailBody(alert, message);

            // Retry logic for transient failures
            var lastException = (Exception?)null;
            for (int attempt = 1; attempt <= channel.MaxRetryAttempts; attempt++)
            {
                try
                {
                    using var smtpClient = new SmtpClient(channel.SmtpHost, channel.SmtpPort)
                    {
                        EnableSsl = channel.SmtpEnableSsl,
                        DeliveryMethod = SmtpDeliveryMethod.Network,
                        Timeout = 30000 // 30 second timeout
                    };

                    // Configure authentication if credentials provided
                    if (!string.IsNullOrEmpty(channel.SmtpUsername))
                    {
                        smtpClient.Credentials = new NetworkCredential(
                            channel.SmtpUsername,
                            channel.SmtpPassword ?? string.Empty);
                    }

                    using var mailMessage = new MailMessage
                    {
                        From = new MailAddress(channel.EmailFrom),
                        Subject = subject,
                        Body = body,
                        IsBodyHtml = true,
                        Priority = alert.Severity == AlertSeverity.Critical ? MailPriority.High :
                                   alert.Severity == AlertSeverity.Error ? MailPriority.High :
                                   MailPriority.Normal
                    };

                    // Add recipients (supports comma-separated list)
                    foreach (var recipient in channel.EmailTo.Split(',', ';'))
                    {
                        var email = recipient.Trim();
                        if (!string.IsNullOrEmpty(email))
                        {
                            mailMessage.To.Add(email);
                        }
                    }

                    await smtpClient.SendMailAsync(mailMessage);

                    LogEvent(LogLevel.Information, "Email alert sent successfully",
                        new Dictionary<string, object>
                        {
                            ["channelId"] = channel.Id,
                            ["alertId"] = alert.Id,
                            ["recipients"] = channel.EmailTo,
                            ["attempt"] = attempt
                        });

                    return; // Success - exit retry loop
                }
                catch (SmtpException ex)
                {
                    lastException = ex;
                    LogEvent(LogLevel.Warning, $"SMTP error sending email alert (attempt {attempt}/{channel.MaxRetryAttempts})",
                        new Dictionary<string, object>
                        {
                            ["channelId"] = channel.Id,
                            ["alertId"] = alert.Id,
                            ["smtpStatusCode"] = ex.StatusCode.ToString(),
                            ["error"] = ex.Message
                        });
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    LogEvent(LogLevel.Warning, $"Error sending email alert (attempt {attempt}/{channel.MaxRetryAttempts})",
                        new Dictionary<string, object>
                        {
                            ["channelId"] = channel.Id,
                            ["alertId"] = alert.Id,
                            ["error"] = ex.Message
                        });
                }

                // Wait before retry (except on last attempt)
                if (attempt < channel.MaxRetryAttempts)
                {
                    await Task.Delay(channel.RetryDelayMs * attempt); // Exponential backoff
                }
            }

            // All retries exhausted
            LogEvent(LogLevel.Error, "Failed to send email alert after all retry attempts",
                new Dictionary<string, object>
                {
                    ["channelId"] = channel.Id,
                    ["alertId"] = alert.Id,
                    ["recipients"] = channel.EmailTo,
                    ["lastError"] = lastException?.Message ?? "Unknown error"
                }, lastException);
        }

        private static string BuildEmailBody(AlertInstance alert, string message)
        {
            var severityColor = alert.Severity switch
            {
                AlertSeverity.Critical => "#dc3545",
                AlertSeverity.Error => "#fd7e14",
                AlertSeverity.Warning => "#ffc107",
                AlertSeverity.Info => "#17a2b8",
                _ => "#6c757d"
            };

            var labelsHtml = string.Empty;
            if (alert.Labels != null && alert.Labels.Count > 0)
            {
                var labelItems = string.Join("", alert.Labels.Select(kv =>
                    $"<li><strong>{WebUtility.HtmlEncode(kv.Key)}:</strong> {WebUtility.HtmlEncode(kv.Value)}</li>"));
                labelsHtml = $"<h3>Labels</h3><ul>{labelItems}</ul>";
            }

            return $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset=""utf-8"">
    <style>
        body {{ font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; margin: 0; padding: 20px; background: #f5f5f5; }}
        .container {{ max-width: 600px; margin: 0 auto; background: white; border-radius: 8px; overflow: hidden; box-shadow: 0 2px 4px rgba(0,0,0,0.1); }}
        .header {{ background: {severityColor}; color: white; padding: 20px; }}
        .header h1 {{ margin: 0; font-size: 24px; }}
        .header .severity {{ opacity: 0.9; font-size: 14px; text-transform: uppercase; }}
        .content {{ padding: 20px; }}
        .metric-box {{ background: #f8f9fa; border-radius: 4px; padding: 15px; margin: 15px 0; }}
        .metric-label {{ color: #6c757d; font-size: 12px; text-transform: uppercase; }}
        .metric-value {{ font-size: 24px; font-weight: bold; color: #212529; }}
        .threshold {{ color: #6c757d; font-size: 14px; }}
        .message {{ background: #e9ecef; padding: 15px; border-radius: 4px; margin: 15px 0; }}
        .footer {{ padding: 20px; border-top: 1px solid #dee2e6; color: #6c757d; font-size: 12px; }}
        ul {{ padding-left: 20px; }}
        li {{ margin: 5px 0; }}
    </style>
</head>
<body>
    <div class=""container"">
        <div class=""header"">
            <div class=""severity"">{alert.Severity} Alert</div>
            <h1>{WebUtility.HtmlEncode(alert.RuleName)}</h1>
        </div>
        <div class=""content"">
            <div class=""message"">{WebUtility.HtmlEncode(message)}</div>

            <div class=""metric-box"">
                <div class=""metric-label"">Metric: {WebUtility.HtmlEncode(alert.Metric)}</div>
                <div class=""metric-value"">{alert.CurrentValue:F2}</div>
                <div class=""threshold"">Threshold: {alert.ThresholdValue:F2}</div>
            </div>

            {labelsHtml}

            <h3>Alert Details</h3>
            <ul>
                <li><strong>Alert ID:</strong> {alert.Id}</li>
                <li><strong>Rule ID:</strong> {alert.RuleId}</li>
                <li><strong>State:</strong> {alert.State}</li>
                <li><strong>Fired At:</strong> {alert.FiredAt:yyyy-MM-dd HH:mm:ss} UTC</li>
                {(alert.ResolvedAt.HasValue ? $"<li><strong>Resolved At:</strong> {alert.ResolvedAt:yyyy-MM-dd HH:mm:ss} UTC</li>" : "")}
            </ul>
        </div>
        <div class=""footer"">
            This alert was generated by DataWarehouse Alerting Plugin.<br>
            Generated at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC
        </div>
    </div>
</body>
</html>";
        }

        private async Task SendWebhookAsync(NotificationChannel channel, AlertInstance alert, NotificationEventType eventType)
        {
            if (_httpClient == null || string.IsNullOrEmpty(channel.WebhookUrl))
            {
                return;
            }

            var payload = new
            {
                alertId = alert.Id,
                ruleId = alert.RuleId,
                ruleName = alert.RuleName,
                metric = alert.Metric,
                labels = alert.Labels,
                severity = alert.Severity.ToString(),
                state = alert.State.ToString(),
                eventType = eventType.ToString(),
                currentValue = alert.CurrentValue,
                thresholdValue = alert.ThresholdValue,
                message = alert.Message,
                firedAt = alert.FiredAt,
                resolvedAt = alert.ResolvedAt,
                timestamp = DateTime.UtcNow
            };

            var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"
            );

            // Add custom headers if configured
            if (channel.Headers != null)
            {
                foreach (var header in channel.Headers)
                {
                    content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            await _httpClient.PostAsync(channel.WebhookUrl, content);
        }

        private async Task SendPagerDutyAsync(NotificationChannel channel, AlertInstance alert, NotificationEventType eventType)
        {
            if (_httpClient == null || string.IsNullOrEmpty(channel.ApiKey))
            {
                return;
            }

            var pdEventType = eventType switch
            {
                NotificationEventType.Firing => "trigger",
                NotificationEventType.Resolved => "resolve",
                NotificationEventType.Reminder => "trigger",
                NotificationEventType.Acknowledged => "acknowledge",
                _ => "trigger"
            };

            var payload = new
            {
                routing_key = channel.ApiKey,
                event_action = pdEventType,
                dedup_key = alert.Id,
                payload = new
                {
                    summary = $"[{alert.Severity}] {alert.RuleName}: {alert.Message}",
                    severity = alert.Severity switch
                    {
                        AlertSeverity.Critical => "critical",
                        AlertSeverity.Error => "error",
                        AlertSeverity.Warning => "warning",
                        _ => "info"
                    },
                    source = "DataWarehouse",
                    timestamp = alert.FiredAt.ToString("O"),
                    custom_details = new
                    {
                        metric = alert.Metric,
                        current_value = alert.CurrentValue,
                        threshold = alert.ThresholdValue,
                        labels = alert.Labels
                    }
                }
            };

            var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"
            );

            await _httpClient.PostAsync("https://events.pagerduty.com/v2/enqueue", content);
        }

        private async Task SendSlackAsync(NotificationChannel channel, AlertInstance alert, NotificationEventType eventType)
        {
            if (_httpClient == null || string.IsNullOrEmpty(channel.WebhookUrl))
            {
                return;
            }

            var color = alert.Severity switch
            {
                AlertSeverity.Critical => "#FF0000",
                AlertSeverity.Error => "#FF6600",
                AlertSeverity.Warning => "#FFCC00",
                AlertSeverity.Info => "#0099FF",
                _ => "#808080"
            };

            if (eventType == NotificationEventType.Resolved)
            {
                color = "#00FF00";
            }

            var payload = new
            {
                attachments = new[]
                {
                    new
                    {
                        color,
                        title = $"[{alert.Severity}] {alert.RuleName}",
                        text = alert.Message,
                        fields = new object[]
                        {
                            new { title = "Metric", value = alert.Metric, @short = true },
                            new { title = "Value", value = alert.CurrentValue.ToString("F2"), @short = true },
                            new { title = "Threshold", value = alert.ThresholdValue.ToString("F2"), @short = true },
                            new { title = "State", value = eventType.ToString(), @short = true }
                        },
                        footer = "DataWarehouse Alerting",
                        ts = new DateTimeOffset(alert.FiredAt).ToUnixTimeSeconds()
                    }
                }
            };

            var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"
            );

            await _httpClient.PostAsync(channel.WebhookUrl, content);
        }

        private async Task SendTeamsAsync(NotificationChannel channel, AlertInstance alert, NotificationEventType eventType)
        {
            if (_httpClient == null || string.IsNullOrEmpty(channel.WebhookUrl))
            {
                return;
            }

            var themeColor = alert.Severity switch
            {
                AlertSeverity.Critical => "FF0000",
                AlertSeverity.Error => "FF6600",
                AlertSeverity.Warning => "FFCC00",
                AlertSeverity.Info => "0099FF",
                _ => "808080"
            };

            if (eventType == NotificationEventType.Resolved)
            {
                themeColor = "00FF00";
            }

            var payload = new
            {
                @type = "MessageCard",
                themeColor,
                title = $"[{alert.Severity}] {alert.RuleName}",
                text = alert.Message,
                sections = new[]
                {
                    new
                    {
                        facts = new object[]
                        {
                            new { name = "Metric", value = alert.Metric },
                            new { name = "Current Value", value = alert.CurrentValue.ToString("F2") },
                            new { name = "Threshold", value = alert.ThresholdValue.ToString("F2") },
                            new { name = "State", value = eventType.ToString() },
                            new { name = "Fired At", value = alert.FiredAt.ToString("O") }
                        }
                    }
                }
            };

            var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"
            );

            await _httpClient.PostAsync(channel.WebhookUrl, content);
        }

        private string FormatAlertMessage(AlertInstance alert, NotificationEventType eventType)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[{alert.Severity}] {alert.RuleName}");
            sb.AppendLine($"State: {eventType}");
            sb.AppendLine($"Metric: {alert.Metric}");
            sb.AppendLine($"Value: {alert.CurrentValue:F2}");
            sb.AppendLine($"Threshold: {alert.ThresholdValue:F2}");
            sb.AppendLine($"Message: {alert.Message}");
            sb.AppendLine($"Time: {alert.FiredAt:O}");

            if (alert.Labels.Count > 0)
            {
                sb.AppendLine($"Labels: {string.Join(", ", alert.Labels.Select(kv => $"{kv.Key}={kv.Value}"))}");
            }

            return sb.ToString();
        }

        #endregion

        #region Silencing

        private Dictionary<string, object> HandleSilenceCreate(Dictionary<string, object> payload)
        {
            var silence = new SilenceWindow
            {
                Id = $"silence-{Guid.NewGuid():N}",
                CreatedBy = payload.GetValueOrDefault("createdBy")?.ToString() ?? "unknown",
                Comment = payload.GetValueOrDefault("comment")?.ToString() ?? "",
                CreatedAt = DateTime.UtcNow
            };

            // Parse matchers
            if (payload.TryGetValue("matchers", out var matchersObj) && matchersObj is JsonElement matchersEl)
            {
                silence.Matchers = JsonSerializer.Deserialize<List<SilenceMatcher>>(matchersEl.GetRawText())
                    ?? new List<SilenceMatcher>();
            }

            // Parse time window
            if (payload.TryGetValue("startsAt", out var startsAt))
            {
                silence.StartsAt = DateTime.Parse(startsAt.ToString()!);
            }
            else
            {
                silence.StartsAt = DateTime.UtcNow;
            }

            if (payload.TryGetValue("endsAt", out var endsAt))
            {
                silence.EndsAt = DateTime.Parse(endsAt.ToString()!);
            }
            else if (payload.TryGetValue("durationMinutes", out var duration))
            {
                silence.EndsAt = silence.StartsAt.AddMinutes(Convert.ToDouble(duration));
            }
            else
            {
                silence.EndsAt = silence.StartsAt.AddHours(1);
            }

            _silences[silence.Id] = silence;

            LogEvent(LogLevel.Information, $"Created silence window: {silence.Id}", new Dictionary<string, object>
            {
                ["silenceId"] = silence.Id,
                ["endsAt"] = silence.EndsAt
            });

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["silenceId"] = silence.Id,
                ["startsAt"] = silence.StartsAt,
                ["endsAt"] = silence.EndsAt
            };
        }

        private Dictionary<string, object> HandleSilenceDelete(Dictionary<string, object> payload)
        {
            var silenceId = payload.GetValueOrDefault("id")?.ToString();
            if (string.IsNullOrEmpty(silenceId))
            {
                return new Dictionary<string, object> { ["error"] = "Silence ID is required" };
            }

            if (_silences.TryRemove(silenceId, out _))
            {
                return new Dictionary<string, object> { ["success"] = true, ["silenceId"] = silenceId };
            }

            return new Dictionary<string, object> { ["error"] = $"Silence not found: {silenceId}" };
        }

        private Dictionary<string, object> HandleSilenceList()
        {
            var activeSilences = _silences.Values
                .Where(s => s.EndsAt > DateTime.UtcNow)
                .Select(s => new Dictionary<string, object>
                {
                    ["id"] = s.Id,
                    ["matchers"] = s.Matchers,
                    ["startsAt"] = s.StartsAt,
                    ["endsAt"] = s.EndsAt,
                    ["createdBy"] = s.CreatedBy,
                    ["comment"] = s.Comment
                })
                .ToList();

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["count"] = activeSilences.Count,
                ["silences"] = activeSilences
            };
        }

        private bool IsAlertSilenced(AlertRule rule)
        {
            var now = DateTime.UtcNow;

            foreach (var silence in _silences.Values)
            {
                if (now < silence.StartsAt || now > silence.EndsAt)
                {
                    continue;
                }

                var matches = true;
                foreach (var matcher in silence.Matchers)
                {
                    var labelValue = matcher.Name == "__name__"
                        ? rule.Metric
                        : rule.Labels?.GetValueOrDefault(matcher.Name);

                    matches = matcher.Type switch
                    {
                        MatcherType.Equal => labelValue == matcher.Value,
                        MatcherType.NotEqual => labelValue != matcher.Value,
                        MatcherType.Regex => labelValue != null && System.Text.RegularExpressions.Regex.IsMatch(labelValue, matcher.Value),
                        MatcherType.NotRegex => labelValue == null || !System.Text.RegularExpressions.Regex.IsMatch(labelValue, matcher.Value),
                        _ => false
                    };

                    if (!matches) break;
                }

                if (matches) return true;
            }

            return false;
        }

        #endregion

        #region Alert Grouping and Deduplication

        private void GroupAlert(AlertInstance alert, AlertRule rule)
        {
            if (rule.GroupBy == null || rule.GroupBy.Count == 0)
            {
                return;
            }

            var groupKey = string.Join(":", rule.GroupBy.Select(g =>
                alert.Labels.TryGetValue(g, out var v) ? v : ""));

            var group = _alertGroups.GetOrAdd(groupKey, _ => new AlertGroup
            {
                Id = $"group-{Guid.NewGuid():N}",
                GroupKey = groupKey,
                GroupBy = rule.GroupBy,
                CreatedAt = DateTime.UtcNow
            });

            group.AlertIds.Add(alert.Id);
            group.LastUpdatedAt = DateTime.UtcNow;
            alert.GroupId = group.Id;
        }

        #endregion

        #region Escalation

        private async Task RunEscalationLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_escalationCheckInterval, ct);
                    await CheckEscalationsAsync();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogEvent(LogLevel.Error, "Error in escalation loop", exception: ex);
                }
            }
        }

        private async Task CheckEscalationsAsync()
        {
            foreach (var alert in _activeAlerts.Values.Where(a => a.State == AlertState.Firing))
            {
                if (!_rules.TryGetValue(alert.RuleId, out var rule))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(rule.EscalationPolicyId))
                {
                    continue;
                }

                if (!_escalationPolicies.TryGetValue(rule.EscalationPolicyId, out var policy))
                {
                    continue;
                }

                var alertDuration = DateTime.UtcNow - alert.FiredAt;

                foreach (var step in policy.Steps.OrderBy(s => s.DelayMinutes))
                {
                    if (alertDuration.TotalMinutes >= step.DelayMinutes &&
                        alert.EscalationLevel < step.Level)
                    {
                        alert.EscalationLevel = step.Level;
                        await SendEscalationNotificationAsync(alert, step);

                        RecordAlertHistory(alert, AlertHistoryEventType.Escalated, new Dictionary<string, object>
                        {
                            ["level"] = step.Level,
                            ["channels"] = step.NotificationChannels
                        });
                    }
                }
            }
        }

        private async Task SendEscalationNotificationAsync(AlertInstance alert, EscalationStep step)
        {
            foreach (var channelId in step.NotificationChannels)
            {
                if (_channels.TryGetValue(channelId, out var channel) && channel.Enabled)
                {
                    try
                    {
                        await SendToChannelAsync(channel, alert, NotificationEventType.Escalated);
                    }
                    catch (Exception ex)
                    {
                        LogEvent(LogLevel.Error, $"Failed escalation notification to {channelId}", exception: ex);
                    }
                }
            }
        }

        private Dictionary<string, object> HandleEscalationConfigure(Dictionary<string, object> payload)
        {
            var policy = new EscalationPolicy
            {
                Id = payload.GetValueOrDefault("id")?.ToString() ?? $"policy-{Guid.NewGuid():N}",
                Name = payload.GetValueOrDefault("name")?.ToString() ?? "Unnamed Policy",
                CreatedAt = DateTime.UtcNow
            };

            if (payload.TryGetValue("steps", out var stepsObj) && stepsObj is JsonElement stepsEl)
            {
                policy.Steps = JsonSerializer.Deserialize<List<EscalationStep>>(stepsEl.GetRawText())
                    ?? new List<EscalationStep>();
            }

            _escalationPolicies[policy.Id] = policy;

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["policyId"] = policy.Id,
                ["name"] = policy.Name,
                ["stepsCount"] = policy.Steps.Count
            };
        }

        private Dictionary<string, object> HandleEscalationList()
        {
            var policies = _escalationPolicies.Values.Select(p => new Dictionary<string, object>
            {
                ["id"] = p.Id,
                ["name"] = p.Name,
                ["stepsCount"] = p.Steps.Count,
                ["createdAt"] = p.CreatedAt
            }).ToList();

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["count"] = policies.Count,
                ["policies"] = policies
            };
        }

        #endregion

        #region Alert Management Handlers

        private Dictionary<string, object> HandleAcknowledge(Dictionary<string, object> payload)
        {
            var alertId = payload.GetValueOrDefault("alertId")?.ToString();
            var acknowledgedBy = payload.GetValueOrDefault("acknowledgedBy")?.ToString() ?? "unknown";
            var comment = payload.GetValueOrDefault("comment")?.ToString();

            if (string.IsNullOrEmpty(alertId))
            {
                return new Dictionary<string, object> { ["error"] = "Alert ID is required" };
            }

            var alert = _activeAlerts.Values.FirstOrDefault(a => a.Id == alertId);
            if (alert == null)
            {
                return new Dictionary<string, object> { ["error"] = $"Alert not found: {alertId}" };
            }

            alert.State = AlertState.Acknowledged;
            alert.AcknowledgedAt = DateTime.UtcNow;
            alert.AcknowledgedBy = acknowledgedBy;
            alert.AcknowledgeComment = comment;

            RecordAlertHistory(alert, AlertHistoryEventType.Acknowledged, new Dictionary<string, object>
            {
                ["acknowledgedBy"] = acknowledgedBy,
                ["comment"] = comment ?? ""
            });

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["alertId"] = alertId,
                ["acknowledgedBy"] = acknowledgedBy
            };
        }

        private Dictionary<string, object> HandleResolve(Dictionary<string, object> payload)
        {
            var alertId = payload.GetValueOrDefault("alertId")?.ToString();
            var resolvedBy = payload.GetValueOrDefault("resolvedBy")?.ToString() ?? "manual";
            var comment = payload.GetValueOrDefault("comment")?.ToString();

            if (string.IsNullOrEmpty(alertId))
            {
                return new Dictionary<string, object> { ["error"] = "Alert ID is required" };
            }

            // Find the alert key
            var alertEntry = _activeAlerts.FirstOrDefault(kv => kv.Value.Id == alertId);
            if (alertEntry.Value == null)
            {
                return new Dictionary<string, object> { ["error"] = $"Alert not found: {alertId}" };
            }

            if (_activeAlerts.TryRemove(alertEntry.Key, out var alert))
            {
                alert.State = AlertState.Resolved;
                alert.ResolvedAt = DateTime.UtcNow;
                alert.ResolvedBy = resolvedBy;
                alert.ResolveComment = comment;

                RecordAlertHistory(alert, AlertHistoryEventType.ManuallyResolved, new Dictionary<string, object>
                {
                    ["resolvedBy"] = resolvedBy,
                    ["comment"] = comment ?? ""
                });

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["alertId"] = alertId,
                    ["resolvedBy"] = resolvedBy
                };
            }

            return new Dictionary<string, object> { ["error"] = $"Failed to resolve alert: {alertId}" };
        }

        private Dictionary<string, object> HandleGetActiveAlerts()
        {
            var alerts = _activeAlerts.Values.Select(a => new Dictionary<string, object>
            {
                ["id"] = a.Id,
                ["ruleId"] = a.RuleId,
                ["ruleName"] = a.RuleName,
                ["metric"] = a.Metric,
                ["labels"] = a.Labels,
                ["severity"] = a.Severity.ToString(),
                ["state"] = a.State.ToString(),
                ["currentValue"] = a.CurrentValue,
                ["thresholdValue"] = a.ThresholdValue,
                ["message"] = a.Message,
                ["firedAt"] = a.FiredAt,
                ["acknowledgedAt"] = (object?)a.AcknowledgedAt ?? DBNull.Value,
                ["acknowledgedBy"] = (object?)a.AcknowledgedBy ?? DBNull.Value,
                ["escalationLevel"] = a.EscalationLevel
            }).ToList();

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["count"] = alerts.Count,
                ["alerts"] = alerts
            };
        }

        private Dictionary<string, object> HandleGetHistory(Dictionary<string, object> payload)
        {
            var limit = payload.TryGetValue("limit", out var l) ? Convert.ToInt32(l) : 100;
            var ruleId = payload.GetValueOrDefault("ruleId")?.ToString();
            var alertId = payload.GetValueOrDefault("alertId")?.ToString();
            var startTime = payload.TryGetValue("startTime", out var st)
                ? DateTime.Parse(st.ToString()!)
                : DateTime.UtcNow.AddDays(-7);
            var endTime = payload.TryGetValue("endTime", out var et)
                ? DateTime.Parse(et.ToString()!)
                : DateTime.UtcNow;

            var history = _alertHistory
                .Where(h => h.Timestamp >= startTime && h.Timestamp <= endTime)
                .Where(h => string.IsNullOrEmpty(ruleId) || h.RuleId == ruleId)
                .Where(h => string.IsNullOrEmpty(alertId) || h.AlertId == alertId)
                .OrderByDescending(h => h.Timestamp)
                .Take(limit)
                .Select(h => new Dictionary<string, object>
                {
                    ["alertId"] = h.AlertId,
                    ["ruleId"] = h.RuleId,
                    ["ruleName"] = h.RuleName,
                    ["eventType"] = h.EventType.ToString(),
                    ["severity"] = h.Severity.ToString(),
                    ["message"] = h.Message,
                    ["timestamp"] = h.Timestamp,
                    ["metadata"] = h.Metadata
                })
                .ToList();

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["count"] = history.Count,
                ["history"] = history
            };
        }

        #endregion

        #region Channel Configuration

        private Dictionary<string, object> HandleChannelConfigure(Dictionary<string, object> payload)
        {
            var channel = new NotificationChannel
            {
                Id = payload.GetValueOrDefault("id")?.ToString() ?? $"channel-{Guid.NewGuid():N}",
                Name = payload.GetValueOrDefault("name")?.ToString() ?? "Unnamed Channel",
                Enabled = payload.TryGetValue("enabled", out var e) && Convert.ToBoolean(e),
                CreatedAt = DateTime.UtcNow
            };

            if (payload.TryGetValue("type", out var typeStr))
            {
                channel.Type = Enum.TryParse<ChannelType>(typeStr.ToString(), true, out var ct)
                    ? ct : ChannelType.Webhook;
            }

            channel.WebhookUrl = payload.GetValueOrDefault("webhookUrl")?.ToString();
            channel.ApiKey = payload.GetValueOrDefault("apiKey")?.ToString();
            channel.EmailTo = payload.GetValueOrDefault("emailTo")?.ToString();
            channel.EmailSubjectPrefix = payload.GetValueOrDefault("emailSubjectPrefix")?.ToString();

            if (payload.TryGetValue("headers", out var headersObj) && headersObj is JsonElement headersEl)
            {
                channel.Headers = JsonSerializer.Deserialize<Dictionary<string, string>>(headersEl.GetRawText());
            }

            _channels[channel.Id] = channel;

            LogEvent(LogLevel.Information, $"Configured notification channel: {channel.Name}", new Dictionary<string, object>
            {
                ["channelId"] = channel.Id,
                ["type"] = channel.Type.ToString()
            });

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["channelId"] = channel.Id,
                ["name"] = channel.Name,
                ["type"] = channel.Type.ToString()
            };
        }

        private Dictionary<string, object> HandleChannelList()
        {
            var channels = _channels.Values.Select(c => new Dictionary<string, object>
            {
                ["id"] = c.Id,
                ["name"] = c.Name,
                ["type"] = c.Type.ToString(),
                ["enabled"] = c.Enabled,
                ["createdAt"] = c.CreatedAt
            }).ToList();

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["count"] = channels.Count,
                ["channels"] = channels
            };
        }

        private async Task<Dictionary<string, object>> HandleChannelTestAsync(Dictionary<string, object> payload)
        {
            var channelId = payload.GetValueOrDefault("channelId")?.ToString();
            if (string.IsNullOrEmpty(channelId))
            {
                return new Dictionary<string, object> { ["error"] = "Channel ID is required" };
            }

            if (!_channels.TryGetValue(channelId, out var channel))
            {
                return new Dictionary<string, object> { ["error"] = $"Channel not found: {channelId}" };
            }

            var testAlert = new AlertInstance
            {
                Id = "test-alert",
                RuleId = "test-rule",
                RuleName = "Test Alert",
                Metric = "test.metric",
                Labels = new Dictionary<string, string> { ["env"] = "test" },
                Severity = AlertSeverity.Info,
                State = AlertState.Firing,
                CurrentValue = 42.0,
                ThresholdValue = 40.0,
                Message = "This is a test alert notification",
                FiredAt = DateTime.UtcNow
            };

            try
            {
                await SendToChannelAsync(channel, testAlert, NotificationEventType.Firing);
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
                    ["channelId"] = channelId,
                    ["error"] = ex.Message
                };
            }
        }

        #endregion

        #region Analytics

        private Dictionary<string, object> HandleGetAnalytics(Dictionary<string, object> payload)
        {
            var startTime = payload.TryGetValue("startTime", out var st)
                ? DateTime.Parse(st.ToString()!)
                : DateTime.UtcNow.AddDays(-7);
            var endTime = payload.TryGetValue("endTime", out var et)
                ? DateTime.Parse(et.ToString()!)
                : DateTime.UtcNow;

            var historyInRange = _alertHistory
                .Where(h => h.Timestamp >= startTime && h.Timestamp <= endTime)
                .ToList();

            var alertsByRule = historyInRange
                .Where(h => h.EventType == AlertHistoryEventType.Fired)
                .GroupBy(h => h.RuleId)
                .ToDictionary(g => g.Key, g => g.Count());

            var alertsBySeverity = historyInRange
                .Where(h => h.EventType == AlertHistoryEventType.Fired)
                .GroupBy(h => h.Severity)
                .ToDictionary(g => g.Key.ToString(), g => g.Count());

            var mttr = CalculateMTTR(historyInRange);

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["period"] = new { startTime, endTime },
                ["totalAlertsFired"] = historyInRange.Count(h => h.EventType == AlertHistoryEventType.Fired),
                ["totalAlertsResolved"] = historyInRange.Count(h => h.EventType == AlertHistoryEventType.Resolved || h.EventType == AlertHistoryEventType.ManuallyResolved),
                ["totalAlertsEscalated"] = historyInRange.Count(h => h.EventType == AlertHistoryEventType.Escalated),
                ["alertsByRule"] = alertsByRule,
                ["alertsBySeverity"] = alertsBySeverity,
                ["meanTimeToResolve"] = (object?)mttr?.TotalMinutes ?? DBNull.Value,
                ["currentActiveAlerts"] = _activeAlerts.Count,
                ["totalRules"] = _rules.Count,
                ["enabledRules"] = _rules.Values.Count(r => r.Enabled),
                ["activeSilences"] = _silences.Values.Count(s => s.EndsAt > DateTime.UtcNow)
            };
        }

        private TimeSpan? CalculateMTTR(List<AlertHistoryEntry> history)
        {
            var firedEvents = history
                .Where(h => h.EventType == AlertHistoryEventType.Fired)
                .ToDictionary(h => h.AlertId, h => h.Timestamp);

            var resolvedEvents = history
                .Where(h => h.EventType == AlertHistoryEventType.Resolved || h.EventType == AlertHistoryEventType.ManuallyResolved)
                .ToDictionary(h => h.AlertId, h => h.Timestamp);

            var resolutionTimes = new List<TimeSpan>();

            foreach (var (alertId, firedAt) in firedEvents)
            {
                if (resolvedEvents.TryGetValue(alertId, out var resolvedAt))
                {
                    resolutionTimes.Add(resolvedAt - firedAt);
                }
            }

            if (resolutionTimes.Count == 0)
            {
                return null;
            }

            return TimeSpan.FromTicks((long)resolutionTimes.Average(t => t.Ticks));
        }

        #endregion

        #region Configuration

        private Dictionary<string, object> HandleConfigure(Dictionary<string, object> payload)
        {
            if (payload.TryGetValue("evaluationIntervalSeconds", out var eis))
            {
                _evaluationInterval = TimeSpan.FromSeconds(Convert.ToDouble(eis));
            }

            if (payload.TryGetValue("escalationCheckIntervalSeconds", out var ecis))
            {
                _escalationCheckInterval = TimeSpan.FromSeconds(Convert.ToDouble(ecis));
            }

            if (payload.TryGetValue("groupingWindowMinutes", out var gwm))
            {
                _groupingWindow = TimeSpan.FromMinutes(Convert.ToDouble(gwm));
            }

            if (payload.TryGetValue("deduplicationWindowSeconds", out var dws))
            {
                _deduplicationWindowSeconds = Convert.ToInt32(dws);
            }

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["evaluationIntervalSeconds"] = _evaluationInterval.TotalSeconds,
                ["escalationCheckIntervalSeconds"] = _escalationCheckInterval.TotalSeconds,
                ["groupingWindowMinutes"] = _groupingWindow.TotalMinutes,
                ["deduplicationWindowSeconds"] = _deduplicationWindowSeconds
            };
        }

        #endregion

        #region Cleanup

        private async Task RunCleanupLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(10), ct);
                    CleanupExpiredData();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogEvent(LogLevel.Error, "Error in cleanup loop", exception: ex);
                }
            }
        }

        private void CleanupExpiredData()
        {
            // Clean expired silences
            var expiredSilences = _silences
                .Where(kv => kv.Value.EndsAt < DateTime.UtcNow)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var id in expiredSilences)
            {
                _silences.TryRemove(id, out _);
            }

            // Clean old metric data
            foreach (var series in _metricData.Values)
            {
                series.PruneOldData(TimeSpan.FromHours(24));
            }

            // Limit history size
            while (_alertHistory.Count > _maxHistoryEntries)
            {
                _alertHistory.TryDequeue(out _);
            }

            // Clean empty alert groups
            var emptyGroups = _alertGroups
                .Where(kv => kv.Value.AlertIds.Count == 0 ||
                             DateTime.UtcNow - kv.Value.LastUpdatedAt > TimeSpan.FromHours(1))
                .Select(kv => kv.Key)
                .ToList();

            foreach (var id in emptyGroups)
            {
                _alertGroups.TryRemove(id, out _);
            }
        }

        #endregion

        #region History Recording

        private void RecordAlertHistory(AlertInstance alert, AlertHistoryEventType eventType, Dictionary<string, object>? metadata = null)
        {
            var entry = new AlertHistoryEntry
            {
                Id = $"history-{Guid.NewGuid():N}",
                AlertId = alert.Id,
                RuleId = alert.RuleId,
                RuleName = alert.RuleName,
                EventType = eventType,
                Severity = alert.Severity,
                Message = alert.Message,
                Timestamp = DateTime.UtcNow,
                Metadata = metadata ?? new Dictionary<string, object>()
            };

            entry.Metadata["currentValue"] = alert.CurrentValue;
            entry.Metadata["thresholdValue"] = alert.ThresholdValue;
            entry.Metadata["labels"] = alert.Labels;

            _alertHistory.Enqueue(entry);
        }

        #endregion

        #region Helpers

        private static string GetMetricKey(string name, Dictionary<string, string>? labels)
        {
            if (labels == null || labels.Count == 0) return name;
            var labelStr = string.Join(",", labels.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
            return $"{name}{{{labelStr}}}";
        }

        private AlertRule DeserializeAlertRule(Dictionary<string, object> payload)
        {
            var rule = new AlertRule
            {
                Id = payload.GetValueOrDefault("id")?.ToString() ?? "",
                Name = payload.GetValueOrDefault("name")?.ToString() ?? "Unnamed Rule",
                Description = payload.GetValueOrDefault("description")?.ToString(),
                Metric = payload.GetValueOrDefault("metric")?.ToString() ?? "",
                Enabled = !payload.TryGetValue("enabled", out var en) || Convert.ToBoolean(en)
            };

            // Parse type
            if (payload.TryGetValue("type", out var typeStr))
            {
                rule.Type = Enum.TryParse<AlertRuleType>(typeStr.ToString(), true, out var t)
                    ? t : AlertRuleType.Threshold;
            }

            // Parse condition
            if (payload.TryGetValue("condition", out var condStr))
            {
                rule.Condition = Enum.TryParse<AlertCondition>(condStr.ToString()?.Replace(" ", ""), true, out var c)
                    ? c : AlertCondition.GreaterThan;
            }

            // Parse severity
            if (payload.TryGetValue("severity", out var sevStr))
            {
                rule.Severity = Enum.TryParse<AlertSeverity>(sevStr.ToString(), true, out var s)
                    ? s : AlertSeverity.Warning;
            }

            // Parse aggregation type
            if (payload.TryGetValue("aggregationType", out var aggStr))
            {
                rule.AggregationType = Enum.TryParse<AggregationType>(aggStr.ToString(), true, out var a)
                    ? a : AggregationType.Average;
            }

            // Parse threshold
            if (payload.TryGetValue("threshold", out var thresh))
            {
                rule.Threshold = Convert.ToDouble(thresh);
            }

            // Parse labels
            if (payload.TryGetValue("labels", out var labelsObj) && labelsObj is JsonElement labelsEl)
            {
                rule.Labels = JsonSerializer.Deserialize<Dictionary<string, string>>(labelsEl.GetRawText());
            }

            // Parse notification channels
            if (payload.TryGetValue("notificationChannels", out var ncObj))
            {
                if (ncObj is JsonElement ncEl)
                {
                    rule.NotificationChannels = JsonSerializer.Deserialize<List<string>>(ncEl.GetRawText());
                }
                else if (ncObj is List<object> ncList)
                {
                    rule.NotificationChannels = ncList.Select(x => x.ToString()!).ToList();
                }
            }

            // Parse evaluation window
            if (payload.TryGetValue("evaluationWindowMinutes", out var ewm))
            {
                rule.EvaluationWindow = TimeSpan.FromMinutes(Convert.ToDouble(ewm));
            }

            // Parse pending duration
            if (payload.TryGetValue("pendingDurationMinutes", out var pdm))
            {
                rule.PendingDuration = TimeSpan.FromMinutes(Convert.ToDouble(pdm));
            }

            // Parse repeat interval
            if (payload.TryGetValue("repeatIntervalMinutes", out var rim))
            {
                rule.RepeatInterval = TimeSpan.FromMinutes(Convert.ToDouble(rim));
            }

            // Parse rate of change threshold
            if (payload.TryGetValue("rateOfChangeThreshold", out var roc))
            {
                rule.RateOfChangeThreshold = Convert.ToDouble(roc);
            }

            // Parse anomaly std dev multiplier
            if (payload.TryGetValue("anomalyStdDevMultiplier", out var asdm))
            {
                rule.AnomalyStdDevMultiplier = Convert.ToDouble(asdm);
            }

            // Parse absence threshold
            if (payload.TryGetValue("absenceThresholdMinutes", out var atm))
            {
                rule.AbsenceThreshold = TimeSpan.FromMinutes(Convert.ToDouble(atm));
            }

            // Parse composite rules
            if (payload.TryGetValue("compositeRules", out var crObj) && crObj is JsonElement crEl)
            {
                rule.CompositeRules = JsonSerializer.Deserialize<List<string>>(crEl.GetRawText());
            }

            // Parse composite operator
            if (payload.TryGetValue("compositeOperator", out var coStr))
            {
                rule.CompositeOperator = Enum.TryParse<CompositeOperator>(coStr.ToString(), true, out var co)
                    ? co : CompositeOperator.And;
            }

            // Parse group by
            if (payload.TryGetValue("groupBy", out var gbObj) && gbObj is JsonElement gbEl)
            {
                rule.GroupBy = JsonSerializer.Deserialize<List<string>>(gbEl.GetRawText());
            }

            // Parse escalation policy
            rule.EscalationPolicyId = payload.GetValueOrDefault("escalationPolicyId")?.ToString();

            // Parse runbook URL
            rule.RunbookUrl = payload.GetValueOrDefault("runbookUrl")?.ToString();

            return rule;
        }

        private Dictionary<string, object> SerializeAlertRule(AlertRule rule)
        {
            return new Dictionary<string, object>
            {
                ["id"] = rule.Id,
                ["name"] = rule.Name,
                ["description"] = rule.Description ?? "",
                ["metric"] = rule.Metric,
                ["type"] = rule.Type.ToString(),
                ["condition"] = rule.Condition.ToString(),
                ["threshold"] = rule.Threshold,
                ["severity"] = rule.Severity.ToString(),
                ["aggregationType"] = rule.AggregationType.ToString(),
                ["labels"] = rule.Labels ?? new Dictionary<string, string>(),
                ["notificationChannels"] = rule.NotificationChannels ?? new List<string>(),
                ["evaluationWindowMinutes"] = rule.EvaluationWindow.TotalMinutes,
                ["pendingDurationMinutes"] = (object?)rule.PendingDuration?.TotalMinutes ?? DBNull.Value,
                ["repeatIntervalMinutes"] = (object?)rule.RepeatInterval?.TotalMinutes ?? DBNull.Value,
                ["enabled"] = rule.Enabled,
                ["createdAt"] = rule.CreatedAt,
                ["updatedAt"] = rule.UpdatedAt,
                ["escalationPolicyId"] = rule.EscalationPolicyId ?? "",
                ["runbookUrl"] = rule.RunbookUrl ?? ""
            };
        }

        #endregion

        #region Plugin Metadata

        /// <inheritdoc />
        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return new List<PluginCapabilityDescriptor>
            {
                new()
                {
                    Name = "alert.rule.create",
                    Description = "Create a new alert rule",
                    Parameters = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["name"] = new { type = "string", description = "Rule name" },
                            ["metric"] = new { type = "string", description = "Metric to monitor" },
                            ["type"] = new { type = "string", description = "Rule type (threshold, rateOfChange, anomaly, absence, composite)" },
                            ["condition"] = new { type = "string", description = "Comparison condition" },
                            ["threshold"] = new { type = "number", description = "Threshold value" },
                            ["severity"] = new { type = "string", description = "Alert severity (info, warning, error, critical)" }
                        },
                        ["required"] = new[] { "name", "metric", "threshold" }
                    }
                },
                new()
                {
                    Name = "alert.silence.create",
                    Description = "Create a silence window to suppress alerts",
                    Parameters = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["matchers"] = new { type = "array", description = "Label matchers for silencing" },
                            ["durationMinutes"] = new { type = "number", description = "Silence duration in minutes" },
                            ["comment"] = new { type = "string", description = "Reason for silence" }
                        }
                    }
                },
                new()
                {
                    Name = "alert.acknowledge",
                    Description = "Acknowledge an active alert",
                    Parameters = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["alertId"] = new { type = "string", description = "Alert ID to acknowledge" },
                            ["acknowledgedBy"] = new { type = "string", description = "User acknowledging the alert" },
                            ["comment"] = new { type = "string", description = "Acknowledgement comment" }
                        },
                        ["required"] = new[] { "alertId" }
                    }
                }
            };
        }

        /// <inheritdoc />
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["Description"] = "Advanced alerting with rules engine, multi-channel notifications, and escalation";
            metadata["FeatureType"] = "Alerting";
            metadata["SupportsThresholdAlerts"] = true;
            metadata["SupportsRateOfChangeAlerts"] = true;
            metadata["SupportsAnomalyAlerts"] = true;
            metadata["SupportsAbsenceAlerts"] = true;
            metadata["SupportsCompositeAlerts"] = true;
            metadata["SupportsSilencing"] = true;
            metadata["SupportsEscalation"] = true;
            metadata["SupportsDeduplication"] = true;
            metadata["SupportsGrouping"] = true;
            metadata["NotificationChannels"] = new[] { "email", "webhook", "pagerduty", "slack", "teams" };
            metadata["ActiveRules"] = _rules.Count;
            metadata["ActiveAlerts"] = _activeAlerts.Count;
            metadata["ConfiguredChannels"] = _channels.Count;
            return metadata;
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// Defines an alert rule with evaluation criteria and notification settings.
    /// </summary>
    public class AlertRule
    {
        /// <summary>
        /// Unique identifier for the rule.
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Human-readable name for the rule.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Optional description of what this rule monitors.
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// The metric name to evaluate.
        /// </summary>
        public string Metric { get; set; } = string.Empty;

        /// <summary>
        /// Label filters for the metric.
        /// </summary>
        public Dictionary<string, string>? Labels { get; set; }

        /// <summary>
        /// Type of alert rule.
        /// </summary>
        public AlertRuleType Type { get; set; } = AlertRuleType.Threshold;

        /// <summary>
        /// Comparison condition for threshold rules.
        /// </summary>
        public AlertCondition Condition { get; set; } = AlertCondition.GreaterThan;

        /// <summary>
        /// Threshold value for comparison.
        /// </summary>
        public double Threshold { get; set; }

        /// <summary>
        /// Alert severity level.
        /// </summary>
        public AlertSeverity Severity { get; set; } = AlertSeverity.Warning;

        /// <summary>
        /// How to aggregate metric values over the evaluation window.
        /// </summary>
        public AggregationType AggregationType { get; set; } = AggregationType.Average;

        /// <summary>
        /// Time window for metric evaluation.
        /// </summary>
        public TimeSpan EvaluationWindow { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Duration to wait before firing (to avoid flapping).
        /// </summary>
        public TimeSpan? PendingDuration { get; set; }

        /// <summary>
        /// How often to repeat notifications for ongoing alerts.
        /// </summary>
        public TimeSpan? RepeatInterval { get; set; }

        /// <summary>
        /// Threshold for rate-of-change rules (percentage per minute).
        /// </summary>
        public double? RateOfChangeThreshold { get; set; }

        /// <summary>
        /// Standard deviation multiplier for anomaly detection.
        /// </summary>
        public double? AnomalyStdDevMultiplier { get; set; }

        /// <summary>
        /// Time threshold for absence detection rules.
        /// </summary>
        public TimeSpan? AbsenceThreshold { get; set; }

        /// <summary>
        /// IDs of rules to combine for composite rules.
        /// </summary>
        public List<string>? CompositeRules { get; set; }

        /// <summary>
        /// Operator for combining composite rules.
        /// </summary>
        public CompositeOperator CompositeOperator { get; set; } = CompositeOperator.And;

        /// <summary>
        /// List of notification channel IDs.
        /// </summary>
        public List<string>? NotificationChannels { get; set; }

        /// <summary>
        /// Labels to group alerts by.
        /// </summary>
        public List<string>? GroupBy { get; set; }

        /// <summary>
        /// ID of escalation policy to use.
        /// </summary>
        public string? EscalationPolicyId { get; set; }

        /// <summary>
        /// URL to runbook for this alert.
        /// </summary>
        public string? RunbookUrl { get; set; }

        /// <summary>
        /// Whether this rule is enabled.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// When the rule was created.
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// When the rule was last updated.
        /// </summary>
        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>
    /// Represents an active or resolved alert instance.
    /// </summary>
    public class AlertInstance
    {
        /// <summary>
        /// Unique identifier for this alert instance.
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// ID of the rule that fired this alert.
        /// </summary>
        public string RuleId { get; set; } = string.Empty;

        /// <summary>
        /// Name of the rule that fired this alert.
        /// </summary>
        public string RuleName { get; set; } = string.Empty;

        /// <summary>
        /// The metric that triggered this alert.
        /// </summary>
        public string Metric { get; set; } = string.Empty;

        /// <summary>
        /// Labels associated with the alert.
        /// </summary>
        public Dictionary<string, string> Labels { get; set; } = new();

        /// <summary>
        /// Alert severity level.
        /// </summary>
        public AlertSeverity Severity { get; set; }

        /// <summary>
        /// Current alert state.
        /// </summary>
        public AlertState State { get; set; }

        /// <summary>
        /// Current metric value.
        /// </summary>
        public double CurrentValue { get; set; }

        /// <summary>
        /// Threshold value from the rule.
        /// </summary>
        public double ThresholdValue { get; set; }

        /// <summary>
        /// Human-readable alert message.
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// When the alert first fired.
        /// </summary>
        public DateTime FiredAt { get; set; }

        /// <summary>
        /// When the alert was last evaluated.
        /// </summary>
        public DateTime LastEvaluatedAt { get; set; }

        /// <summary>
        /// When the last notification was sent.
        /// </summary>
        public DateTime? LastNotifiedAt { get; set; }

        /// <summary>
        /// When the alert was acknowledged.
        /// </summary>
        public DateTime? AcknowledgedAt { get; set; }

        /// <summary>
        /// Who acknowledged the alert.
        /// </summary>
        public string? AcknowledgedBy { get; set; }

        /// <summary>
        /// Comment added during acknowledgement.
        /// </summary>
        public string? AcknowledgeComment { get; set; }

        /// <summary>
        /// When the alert was resolved.
        /// </summary>
        public DateTime? ResolvedAt { get; set; }

        /// <summary>
        /// Who resolved the alert.
        /// </summary>
        public string? ResolvedBy { get; set; }

        /// <summary>
        /// Comment added during resolution.
        /// </summary>
        public string? ResolveComment { get; set; }

        /// <summary>
        /// ID of the alert group this belongs to.
        /// </summary>
        public string? GroupId { get; set; }

        /// <summary>
        /// Current escalation level.
        /// </summary>
        public int EscalationLevel { get; set; }

        /// <summary>
        /// Number of times this alert has been evaluated as firing.
        /// </summary>
        public int EvaluationCount { get; set; }
    }

    /// <summary>
    /// Defines a notification channel configuration.
    /// </summary>
    public class NotificationChannel
    {
        /// <summary>
        /// Unique identifier for the channel.
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Human-readable channel name.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Type of notification channel.
        /// </summary>
        public ChannelType Type { get; set; }

        /// <summary>
        /// Whether this channel is enabled.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Webhook URL for webhook-based channels.
        /// </summary>
        public string? WebhookUrl { get; set; }

        /// <summary>
        /// API key for services like PagerDuty.
        /// </summary>
        public string? ApiKey { get; set; }

        /// <summary>
        /// Email addresses for email notifications.
        /// </summary>
        public string? EmailTo { get; set; }

        /// <summary>
        /// Subject prefix for email notifications.
        /// </summary>
        public string? EmailSubjectPrefix { get; set; }

        /// <summary>
        /// Custom headers for webhook requests.
        /// </summary>
        public Dictionary<string, string>? Headers { get; set; }

        /// <summary>
        /// When the channel was created.
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// SMTP server hostname for email notifications.
        /// </summary>
        public string? SmtpHost { get; set; }

        /// <summary>
        /// SMTP server port (default: 587 for TLS, 465 for SSL, 25 for plain).
        /// </summary>
        public int SmtpPort { get; set; } = 587;

        /// <summary>
        /// SMTP username for authentication.
        /// </summary>
        public string? SmtpUsername { get; set; }

        /// <summary>
        /// SMTP password for authentication.
        /// </summary>
        public string? SmtpPassword { get; set; }

        /// <summary>
        /// Enable TLS/SSL for SMTP connection.
        /// </summary>
        public bool SmtpEnableSsl { get; set; } = true;

        /// <summary>
        /// Sender email address (From field).
        /// </summary>
        public string? EmailFrom { get; set; }

        /// <summary>
        /// Maximum retry attempts for failed email sends.
        /// </summary>
        public int MaxRetryAttempts { get; set; } = 3;

        /// <summary>
        /// Delay between retry attempts in milliseconds.
        /// </summary>
        public int RetryDelayMs { get; set; } = 1000;
    }

    /// <summary>
    /// Defines an escalation policy with multiple steps.
    /// </summary>
    public class EscalationPolicy
    {
        /// <summary>
        /// Unique identifier for the policy.
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Human-readable policy name.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Escalation steps in order.
        /// </summary>
        public List<EscalationStep> Steps { get; set; } = new();

        /// <summary>
        /// When the policy was created.
        /// </summary>
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// Defines a single escalation step.
    /// </summary>
    public class EscalationStep
    {
        /// <summary>
        /// Escalation level (1, 2, 3, etc.).
        /// </summary>
        [JsonPropertyName("level")]
        public int Level { get; set; }

        /// <summary>
        /// Minutes to wait before this escalation step.
        /// </summary>
        [JsonPropertyName("delayMinutes")]
        public int DelayMinutes { get; set; }

        /// <summary>
        /// Notification channels to use at this step.
        /// </summary>
        [JsonPropertyName("notificationChannels")]
        public List<string> NotificationChannels { get; set; } = new();
    }

    /// <summary>
    /// Defines a silence window for suppressing alerts.
    /// </summary>
    public class SilenceWindow
    {
        /// <summary>
        /// Unique identifier for the silence.
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Label matchers for this silence.
        /// </summary>
        public List<SilenceMatcher> Matchers { get; set; } = new();

        /// <summary>
        /// When the silence starts.
        /// </summary>
        public DateTime StartsAt { get; set; }

        /// <summary>
        /// When the silence ends.
        /// </summary>
        public DateTime EndsAt { get; set; }

        /// <summary>
        /// Who created the silence.
        /// </summary>
        public string CreatedBy { get; set; } = string.Empty;

        /// <summary>
        /// Reason for the silence.
        /// </summary>
        public string Comment { get; set; } = string.Empty;

        /// <summary>
        /// When the silence was created.
        /// </summary>
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// Defines a matcher for silence windows.
    /// </summary>
    public class SilenceMatcher
    {
        /// <summary>
        /// Label name to match.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Value to match against.
        /// </summary>
        [JsonPropertyName("value")]
        public string Value { get; set; } = string.Empty;

        /// <summary>
        /// Type of matching to perform.
        /// </summary>
        [JsonPropertyName("type")]
        public MatcherType Type { get; set; } = MatcherType.Equal;
    }

    /// <summary>
    /// Groups related alerts together.
    /// </summary>
    public class AlertGroup
    {
        /// <summary>
        /// Unique identifier for the group.
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Key identifying this group.
        /// </summary>
        public string GroupKey { get; set; } = string.Empty;

        /// <summary>
        /// Labels used for grouping.
        /// </summary>
        public List<string> GroupBy { get; set; } = new();

        /// <summary>
        /// IDs of alerts in this group.
        /// </summary>
        public HashSet<string> AlertIds { get; set; } = new();

        /// <summary>
        /// When the group was created.
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// When the group was last updated.
        /// </summary>
        public DateTime LastUpdatedAt { get; set; }
    }

    /// <summary>
    /// Records an entry in the alert history.
    /// </summary>
    public class AlertHistoryEntry
    {
        /// <summary>
        /// Unique identifier for the history entry.
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// ID of the alert.
        /// </summary>
        public string AlertId { get; set; } = string.Empty;

        /// <summary>
        /// ID of the rule.
        /// </summary>
        public string RuleId { get; set; } = string.Empty;

        /// <summary>
        /// Name of the rule.
        /// </summary>
        public string RuleName { get; set; } = string.Empty;

        /// <summary>
        /// Type of history event.
        /// </summary>
        public AlertHistoryEventType EventType { get; set; }

        /// <summary>
        /// Alert severity at the time of the event.
        /// </summary>
        public AlertSeverity Severity { get; set; }

        /// <summary>
        /// Message at the time of the event.
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// When the event occurred.
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Additional metadata for the event.
        /// </summary>
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    /// <summary>
    /// Stores time series data for a metric.
    /// </summary>
    internal class MetricTimeSeries
    {
        public string Name { get; }
        public Dictionary<string, string> Tags { get; }

        private readonly List<DataPoint> _dataPoints = new();
        private readonly object _lock = new();

        public MetricTimeSeries(string name, Dictionary<string, string> tags)
        {
            Name = name;
            Tags = tags;
        }

        public void AddDataPoint(double value, DateTime timestamp)
        {
            lock (_lock)
            {
                _dataPoints.Add(new DataPoint { Value = value, Timestamp = timestamp });

                // Keep only last 24 hours of data
                if (_dataPoints.Count > 10000)
                {
                    _dataPoints.RemoveRange(0, _dataPoints.Count - 10000);
                }
            }
        }

        public MetricStatistics GetStatistics(TimeSpan? window = null)
        {
            lock (_lock)
            {
                var cutoff = window.HasValue
                    ? DateTime.UtcNow - window.Value
                    : DateTime.MinValue;

                var points = _dataPoints.Where(p => p.Timestamp >= cutoff).ToList();

                if (points.Count == 0)
                {
                    return new MetricStatistics();
                }

                var values = points.Select(p => p.Value).ToList();
                values.Sort();

                var mean = values.Average();
                var variance = values.Sum(v => Math.Pow(v - mean, 2)) / values.Count;

                return new MetricStatistics
                {
                    Current = points[^1].Value,
                    Min = values[0],
                    Max = values[^1],
                    Mean = mean,
                    Sum = values.Sum(),
                    Count = values.Count,
                    StdDev = Math.Sqrt(variance),
                    Percentile95 = values[(int)(values.Count * 0.95)],
                    Percentile99 = values[(int)(values.Count * 0.99)]
                };
            }
        }

        public double? GetRateOfChange(TimeSpan window)
        {
            lock (_lock)
            {
                var cutoff = DateTime.UtcNow - window;
                var points = _dataPoints.Where(p => p.Timestamp >= cutoff).OrderBy(p => p.Timestamp).ToList();

                if (points.Count < 2)
                {
                    return null;
                }

                var first = points[0];
                var last = points[^1];
                var timeDiff = (last.Timestamp - first.Timestamp).TotalMinutes;

                if (timeDiff <= 0 || first.Value == 0)
                {
                    return 0;
                }

                return ((last.Value - first.Value) / first.Value) * 100 / timeDiff;
            }
        }

        public DataPoint? GetLastDataPoint()
        {
            lock (_lock)
            {
                return _dataPoints.Count > 0 ? _dataPoints[^1] : null;
            }
        }

        public void PruneOldData(TimeSpan maxAge)
        {
            lock (_lock)
            {
                var cutoff = DateTime.UtcNow - maxAge;
                _dataPoints.RemoveAll(p => p.Timestamp < cutoff);
            }
        }
    }

    /// <summary>
    /// A single data point in a time series.
    /// </summary>
    internal class DataPoint
    {
        public double Value { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Statistics calculated from metric data.
    /// </summary>
    internal class MetricStatistics
    {
        public double Current { get; set; }
        public double Min { get; set; }
        public double Max { get; set; }
        public double Mean { get; set; }
        public double Sum { get; set; }
        public int Count { get; set; }
        public double StdDev { get; set; }
        public double Percentile95 { get; set; }
        public double Percentile99 { get; set; }
    }

    /// <summary>
    /// Result of evaluating a single rule.
    /// </summary>
    internal class RuleEvaluation
    {
        public bool IsFiring { get; set; }
        public double CurrentValue { get; set; }
        public double ThresholdValue { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>
    /// Result of evaluating all rules.
    /// </summary>
    internal class EvaluationResult
    {
        public int RulesEvaluated { get; set; }
        public int AlertsFiring { get; set; }
        public int AlertsResolved { get; set; }
    }

    /// <summary>
    /// A log entry with trace correlation.
    /// </summary>
    internal class LogEntry
    {
        public DateTime Timestamp { get; set; }
        public LogLevel Level { get; set; }
        public string Message { get; set; } = string.Empty;
        public Dictionary<string, object> Properties { get; set; } = new();
        public string? ExceptionMessage { get; set; }
        public string? ExceptionStackTrace { get; set; }
        public string? TraceId { get; set; }
        public string? SpanId { get; set; }
    }

    /// <summary>
    /// Simple trace span implementation for the alerting plugin.
    /// </summary>
    internal class AlertingTraceSpan : ITraceSpan
    {
        public string TraceId { get; }
        public string SpanId { get; }
        public string OperationName { get; }
        public SpanKind Kind { get; }
        public DateTime StartTime { get; }
        public TimeSpan? Duration { get; private set; }
        public bool IsRecording => _isRecording;

        private readonly Dictionary<string, object> _attributes = new();
        private readonly List<(string Name, Dictionary<string, object>? Attributes)> _events = new();
        private SpanStatus _status = SpanStatus.Unset;
        private string? _statusDescription;
        private bool _isRecording = true;

        public AlertingTraceSpan(string operationName, SpanKind kind, ITraceSpan? parent)
        {
            TraceId = parent?.TraceId ?? Guid.NewGuid().ToString("N");
            SpanId = Guid.NewGuid().ToString("N")[..16];
            OperationName = operationName;
            Kind = kind;
            StartTime = DateTime.UtcNow;
        }

        public void SetAttribute(string key, object value)
        {
            if (_isRecording)
            {
                _attributes[key] = value;
            }
        }

        public void AddEvent(string name, Dictionary<string, object>? attributes = null)
        {
            if (_isRecording)
            {
                _events.Add((name, attributes));
            }
        }

        public void SetStatus(SpanStatus status, string? description = null)
        {
            if (_isRecording)
            {
                _status = status;
                _statusDescription = description;
            }
        }

        public void RecordException(Exception exception)
        {
            if (_isRecording)
            {
                AddEvent("exception", new Dictionary<string, object>
                {
                    ["exception.type"] = exception.GetType().FullName ?? "Unknown",
                    ["exception.message"] = exception.Message,
                    ["exception.stacktrace"] = exception.StackTrace ?? ""
                });
                SetStatus(SpanStatus.Error, exception.Message);
            }
        }

        public void End()
        {
            if (_isRecording)
            {
                Duration = DateTime.UtcNow - StartTime;
                _isRecording = false;
            }
        }

        public void Dispose()
        {
            End();
        }
    }

    #endregion

    #region Enums

    /// <summary>
    /// Types of alert rules.
    /// </summary>
    public enum AlertRuleType
    {
        /// <summary>
        /// Threshold-based alerts.
        /// </summary>
        Threshold,

        /// <summary>
        /// Rate-of-change based alerts.
        /// </summary>
        RateOfChange,

        /// <summary>
        /// Statistical anomaly detection.
        /// </summary>
        Anomaly,

        /// <summary>
        /// Absence of data detection.
        /// </summary>
        Absence,

        /// <summary>
        /// Composite rule combining multiple rules.
        /// </summary>
        Composite
    }

    /// <summary>
    /// Comparison conditions for threshold rules.
    /// </summary>
    public enum AlertCondition
    {
        /// <summary>
        /// Value greater than threshold.
        /// </summary>
        GreaterThan,

        /// <summary>
        /// Value greater than or equal to threshold.
        /// </summary>
        GreaterThanOrEqual,

        /// <summary>
        /// Value less than threshold.
        /// </summary>
        LessThan,

        /// <summary>
        /// Value less than or equal to threshold.
        /// </summary>
        LessThanOrEqual,

        /// <summary>
        /// Value equal to threshold.
        /// </summary>
        Equal,

        /// <summary>
        /// Value not equal to threshold.
        /// </summary>
        NotEqual
    }

    /// <summary>
    /// Alert severity levels.
    /// </summary>
    public enum AlertSeverity
    {
        /// <summary>
        /// Informational alert.
        /// </summary>
        Info,

        /// <summary>
        /// Warning level alert.
        /// </summary>
        Warning,

        /// <summary>
        /// Error level alert.
        /// </summary>
        Error,

        /// <summary>
        /// Critical alert requiring immediate attention.
        /// </summary>
        Critical
    }

    /// <summary>
    /// Alert lifecycle states.
    /// </summary>
    public enum AlertState
    {
        /// <summary>
        /// Alert is pending (within pending duration).
        /// </summary>
        Pending,

        /// <summary>
        /// Alert is actively firing.
        /// </summary>
        Firing,

        /// <summary>
        /// Alert has been acknowledged.
        /// </summary>
        Acknowledged,

        /// <summary>
        /// Alert has been resolved.
        /// </summary>
        Resolved
    }

    /// <summary>
    /// Types of notification channels.
    /// </summary>
    public enum ChannelType
    {
        /// <summary>
        /// Console output (for development).
        /// </summary>
        Console,

        /// <summary>
        /// Email notifications.
        /// </summary>
        Email,

        /// <summary>
        /// Generic webhook.
        /// </summary>
        Webhook,

        /// <summary>
        /// PagerDuty integration.
        /// </summary>
        PagerDuty,

        /// <summary>
        /// Slack integration.
        /// </summary>
        Slack,

        /// <summary>
        /// Microsoft Teams integration.
        /// </summary>
        MicrosoftTeams
    }

    /// <summary>
    /// Types of notification events.
    /// </summary>
    public enum NotificationEventType
    {
        /// <summary>
        /// Alert started firing.
        /// </summary>
        Firing,

        /// <summary>
        /// Alert was resolved.
        /// </summary>
        Resolved,

        /// <summary>
        /// Reminder for ongoing alert.
        /// </summary>
        Reminder,

        /// <summary>
        /// Alert was acknowledged.
        /// </summary>
        Acknowledged,

        /// <summary>
        /// Alert was escalated.
        /// </summary>
        Escalated
    }

    /// <summary>
    /// Types of aggregation for metrics.
    /// </summary>
    public enum AggregationType
    {
        /// <summary>
        /// Average of values.
        /// </summary>
        Average,

        /// <summary>
        /// Minimum value.
        /// </summary>
        Min,

        /// <summary>
        /// Maximum value.
        /// </summary>
        Max,

        /// <summary>
        /// Sum of values.
        /// </summary>
        Sum,

        /// <summary>
        /// Count of values.
        /// </summary>
        Count,

        /// <summary>
        /// Most recent value.
        /// </summary>
        Last,

        /// <summary>
        /// 95th percentile.
        /// </summary>
        Percentile95,

        /// <summary>
        /// 99th percentile.
        /// </summary>
        Percentile99
    }

    /// <summary>
    /// Operators for composite rules.
    /// </summary>
    public enum CompositeOperator
    {
        /// <summary>
        /// All rules must fire.
        /// </summary>
        And,

        /// <summary>
        /// Any rule fires.
        /// </summary>
        Or
    }

    /// <summary>
    /// Types of matchers for silencing.
    /// </summary>
    public enum MatcherType
    {
        /// <summary>
        /// Exact match.
        /// </summary>
        Equal,

        /// <summary>
        /// Not equal.
        /// </summary>
        NotEqual,

        /// <summary>
        /// Regular expression match.
        /// </summary>
        Regex,

        /// <summary>
        /// Regular expression not match.
        /// </summary>
        NotRegex
    }

    /// <summary>
    /// Types of alert history events.
    /// </summary>
    public enum AlertHistoryEventType
    {
        /// <summary>
        /// Alert fired.
        /// </summary>
        Fired,

        /// <summary>
        /// Alert automatically resolved.
        /// </summary>
        Resolved,

        /// <summary>
        /// Alert manually resolved.
        /// </summary>
        ManuallyResolved,

        /// <summary>
        /// Alert acknowledged.
        /// </summary>
        Acknowledged,

        /// <summary>
        /// Alert escalated.
        /// </summary>
        Escalated,

        /// <summary>
        /// Alert silenced.
        /// </summary>
        Silenced
    }

    #endregion
}
