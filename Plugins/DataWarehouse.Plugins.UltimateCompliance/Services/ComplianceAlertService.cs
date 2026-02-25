using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateCompliance.Services
{
    /// <summary>
    /// Routes compliance alerts to configured notification channels via message bus.
    /// Supports Email, Slack, PagerDuty, and OpsGenie routing. Includes deduplication
    /// with configurable suppression window and automatic escalation for unacknowledged alerts.
    /// </summary>
    public sealed class ComplianceAlertService
    {
        private readonly IMessageBus? _messageBus;
        private readonly string _pluginId;
        private readonly BoundedDictionary<string, AlertRecord> _alertHistory = new BoundedDictionary<string, AlertRecord>(1000);
        private readonly BoundedDictionary<string, DateTime> _deduplicationCache = new BoundedDictionary<string, DateTime>(1000);
        private readonly TimeSpan _deduplicationWindow;
        private readonly TimeSpan _escalationTimeout;
        private long _alertSequence;

        /// <summary>
        /// Message bus topics for notification channels.
        /// </summary>
        private static class NotificationTopics
        {
            public const string Email = "notification.email.send";
            public const string Slack = "notification.slack.send";
            public const string PagerDuty = "notification.pagerduty.send";
            public const string OpsGenie = "notification.opsgenie.send";
        }

        public ComplianceAlertService(
            IMessageBus? messageBus,
            string pluginId,
            TimeSpan? deduplicationWindow = null,
            TimeSpan? escalationTimeout = null)
        {
            _messageBus = messageBus;
            _pluginId = pluginId;
            _deduplicationWindow = deduplicationWindow ?? TimeSpan.FromMinutes(15);
            _escalationTimeout = escalationTimeout ?? TimeSpan.FromMinutes(30);
        }

        /// <summary>
        /// Sends a compliance alert to all configured channels based on severity.
        /// Applies deduplication and schedules escalation for critical alerts.
        /// </summary>
        public async Task<AlertResult> SendAlertAsync(
            ComplianceAlert alert,
            CancellationToken cancellationToken = default)
        {
            if (alert == null) throw new ArgumentNullException(nameof(alert));

            // Deduplication check
            var deduplicationKey = ComputeDeduplicationKey(alert);
            if (IsDuplicate(deduplicationKey))
            {
                return new AlertResult
                {
                    AlertId = alert.AlertId,
                    Status = AlertDeliveryStatus.Suppressed,
                    Message = $"Duplicate alert suppressed. Window: {_deduplicationWindow.TotalMinutes} minutes.",
                    ChannelsNotified = new List<string>(),
                    SentAtUtc = DateTime.UtcNow
                };
            }

            // Record in deduplication cache
            _deduplicationCache[deduplicationKey] = DateTime.UtcNow;
            CleanupDeduplicationCache();

            var sequence = Interlocked.Increment(ref _alertSequence);
            var channelsNotified = new List<string>();
            var failures = new List<string>();

            // Route to channels based on severity
            var channels = DetermineChannels(alert.Severity);

            foreach (var channel in channels)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await RouteToChannelAsync(channel, alert, sequence, cancellationToken);
                    channelsNotified.Add(channel);
                }
                catch (Exception ex)
                {
                    failures.Add($"{channel}: {ex.Message}");
                }
            }

            // Record alert
            var record = new AlertRecord
            {
                Alert = alert,
                SequenceNumber = sequence,
                SentAtUtc = DateTime.UtcNow,
                ChannelsNotified = channelsNotified,
                Acknowledged = false,
                EscalationScheduled = alert.Severity >= ComplianceAlertSeverity.Critical
            };
            _alertHistory[alert.AlertId] = record;

            // Schedule escalation for critical/emergency alerts
            if (alert.Severity >= ComplianceAlertSeverity.Critical)
            {
                _ = ScheduleEscalationAsync(alert, cancellationToken);
            }

            // Publish alert event
            if (_messageBus != null)
            {
                await _messageBus.PublishAsync("compliance.alert.sent", new PluginMessage
                {
                    Type = "compliance.alert.sent",
                    Source = _pluginId,
                    Payload = new Dictionary<string, object>
                    {
                        ["alertId"] = alert.AlertId,
                        ["severity"] = alert.Severity.ToString(),
                        ["title"] = alert.Title,
                        ["channels"] = channelsNotified,
                        ["channelCount"] = channelsNotified.Count,
                        ["failures"] = failures.Count,
                        ["sentAt"] = DateTime.UtcNow.ToString("O")
                    }
                }, cancellationToken);
            }

            return new AlertResult
            {
                AlertId = alert.AlertId,
                Status = failures.Count == 0
                    ? AlertDeliveryStatus.Delivered
                    : channelsNotified.Count > 0
                        ? AlertDeliveryStatus.PartialDelivery
                        : AlertDeliveryStatus.Failed,
                Message = failures.Count == 0
                    ? $"Alert delivered to {channelsNotified.Count} channels"
                    : $"Delivered to {channelsNotified.Count}, failed: {string.Join("; ", failures)}",
                ChannelsNotified = channelsNotified,
                SentAtUtc = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Acknowledges an alert, cancelling any pending escalation.
        /// </summary>
        public bool AcknowledgeAlert(string alertId, string acknowledgedBy)
        {
            if (_alertHistory.TryGetValue(alertId, out var record))
            {
                record.Acknowledged = true;
                record.AcknowledgedBy = acknowledgedBy;
                record.AcknowledgedAtUtc = DateTime.UtcNow;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Returns recent alert history.
        /// </summary>
        public IReadOnlyList<AlertRecord> GetRecentAlerts(int count = 50)
        {
            return _alertHistory.Values
                .OrderByDescending(r => r.SentAtUtc)
                .Take(count)
                .ToList()
                .AsReadOnly();
        }

        private List<string> DetermineChannels(ComplianceAlertSeverity severity)
        {
            return severity switch
            {
                ComplianceAlertSeverity.Emergency => new List<string>
                {
                    NotificationTopics.PagerDuty,
                    NotificationTopics.OpsGenie,
                    NotificationTopics.Slack,
                    NotificationTopics.Email
                },
                ComplianceAlertSeverity.Critical => new List<string>
                {
                    NotificationTopics.PagerDuty,
                    NotificationTopics.Slack,
                    NotificationTopics.Email
                },
                ComplianceAlertSeverity.Warning => new List<string>
                {
                    NotificationTopics.Slack,
                    NotificationTopics.Email
                },
                ComplianceAlertSeverity.Info => new List<string>
                {
                    NotificationTopics.Email
                },
                _ => new List<string> { NotificationTopics.Email }
            };
        }

        private async Task RouteToChannelAsync(
            string channel,
            ComplianceAlert alert,
            long sequence,
            CancellationToken cancellationToken)
        {
            if (_messageBus == null) return;

            var payload = new Dictionary<string, object>
            {
                ["alertId"] = alert.AlertId,
                ["title"] = alert.Title,
                ["message"] = alert.Message,
                ["severity"] = alert.Severity.ToString(),
                ["framework"] = alert.Framework ?? "General",
                ["source"] = alert.Source,
                ["timestamp"] = alert.CreatedAtUtc.ToString("O"),
                ["sequence"] = sequence
            };

            // Channel-specific formatting
            switch (channel)
            {
                case NotificationTopics.Email:
                    payload["subject"] = $"[{alert.Severity}] Compliance Alert: {alert.Title}";
                    payload["body"] = FormatEmailBody(alert);
                    payload["priority"] = alert.Severity >= ComplianceAlertSeverity.Critical ? "high" : "normal";
                    break;

                case NotificationTopics.Slack:
                    payload["channel"] = alert.Severity >= ComplianceAlertSeverity.Critical ? "#compliance-critical" : "#compliance-alerts";
                    payload["text"] = FormatSlackMessage(alert);
                    payload["color"] = GetSeverityColor(alert.Severity);
                    break;

                case NotificationTopics.PagerDuty:
                    payload["routingKey"] = "compliance-alerts";
                    payload["eventAction"] = "trigger";
                    payload["dedupKey"] = $"compliance-{alert.AlertId}";
                    payload["summary"] = $"{alert.Severity}: {alert.Title}";
                    payload["pdSeverity"] = MapToPagerDutySeverity(alert.Severity);
                    payload["component"] = alert.Framework ?? "compliance";
                    break;

                case NotificationTopics.OpsGenie:
                    payload["ogPriority"] = MapToOpsGeniePriority(alert.Severity);
                    payload["alias"] = $"compliance-{alert.AlertId}";
                    payload["tags"] = new[] { "compliance", alert.Framework ?? "general", alert.Severity.ToString().ToLowerInvariant() };
                    payload["entity"] = alert.Source;
                    break;
            }

            await _messageBus.PublishAsync(channel, new PluginMessage
            {
                Type = channel,
                Source = _pluginId,
                Payload = payload
            }, cancellationToken);
        }

        private async Task ScheduleEscalationAsync(ComplianceAlert alert, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(_escalationTimeout, cancellationToken);

                // Check if alert was acknowledged
                if (_alertHistory.TryGetValue(alert.AlertId, out var record) && !record.Acknowledged)
                {
                    // Escalate: upgrade severity and re-send
                    var escalatedAlert = new ComplianceAlert
                    {
                        AlertId = $"{alert.AlertId}-ESC",
                        Title = $"[ESCALATED] {alert.Title}",
                        Message = $"ESCALATION: Alert {alert.AlertId} was not acknowledged within {_escalationTimeout.TotalMinutes} minutes. Original: {alert.Message}",
                        Severity = ComplianceAlertSeverity.Emergency,
                        Framework = alert.Framework,
                        Source = _pluginId,
                        CreatedAtUtc = DateTime.UtcNow,
                        EscalatedFromAlertId = alert.AlertId
                    };

                    await SendAlertAsync(escalatedAlert, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Escalation cancelled - expected during shutdown
            }
        }

        private string ComputeDeduplicationKey(ComplianceAlert alert)
        {
            return $"{alert.Severity}|{alert.Title}|{alert.Framework}|{alert.Source}";
        }

        private bool IsDuplicate(string key)
        {
            if (_deduplicationCache.TryGetValue(key, out var lastSent))
            {
                return DateTime.UtcNow - lastSent < _deduplicationWindow;
            }
            return false;
        }

        private void CleanupDeduplicationCache()
        {
            var expiredKeys = _deduplicationCache
                .Where(kvp => DateTime.UtcNow - kvp.Value > _deduplicationWindow)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                _deduplicationCache.TryRemove(key, out _);
            }
        }

        private string FormatEmailBody(ComplianceAlert alert)
        {
            return $"Compliance Alert\n" +
                   $"================\n" +
                   $"Severity: {alert.Severity}\n" +
                   $"Framework: {alert.Framework ?? "N/A"}\n" +
                   $"Title: {alert.Title}\n" +
                   $"Time: {alert.CreatedAtUtc:yyyy-MM-dd HH:mm:ss} UTC\n" +
                   $"Source: {alert.Source}\n\n" +
                   $"Details:\n{alert.Message}\n\n" +
                   $"Alert ID: {alert.AlertId}";
        }

        private string FormatSlackMessage(ComplianceAlert alert)
        {
            var emoji = alert.Severity switch
            {
                ComplianceAlertSeverity.Emergency => ":rotating_light:",
                ComplianceAlertSeverity.Critical => ":red_circle:",
                ComplianceAlertSeverity.Warning => ":warning:",
                ComplianceAlertSeverity.Info => ":information_source:",
                _ => ":bell:"
            };

            return $"{emoji} *{alert.Title}*\n" +
                   $"Severity: `{alert.Severity}` | Framework: `{alert.Framework ?? "N/A"}`\n" +
                   $"{alert.Message}";
        }

        private string GetSeverityColor(ComplianceAlertSeverity severity)
        {
            return severity switch
            {
                ComplianceAlertSeverity.Emergency => "#FF0000",
                ComplianceAlertSeverity.Critical => "#CC0000",
                ComplianceAlertSeverity.Warning => "#FFA500",
                ComplianceAlertSeverity.Info => "#0066CC",
                _ => "#808080"
            };
        }

        private string MapToPagerDutySeverity(ComplianceAlertSeverity severity)
        {
            return severity switch
            {
                ComplianceAlertSeverity.Emergency => "critical",
                ComplianceAlertSeverity.Critical => "error",
                ComplianceAlertSeverity.Warning => "warning",
                ComplianceAlertSeverity.Info => "info",
                _ => "info"
            };
        }

        private string MapToOpsGeniePriority(ComplianceAlertSeverity severity)
        {
            return severity switch
            {
                ComplianceAlertSeverity.Emergency => "P1",
                ComplianceAlertSeverity.Critical => "P2",
                ComplianceAlertSeverity.Warning => "P3",
                ComplianceAlertSeverity.Info => "P5",
                _ => "P4"
            };
        }
    }

    #region Alert Types

    /// <summary>
    /// Compliance alert to be routed to notification channels.
    /// </summary>
    public sealed class ComplianceAlert
    {
        public required string AlertId { get; init; }
        public required string Title { get; init; }
        public required string Message { get; init; }
        public required ComplianceAlertSeverity Severity { get; init; }
        public string? Framework { get; init; }
        public required string Source { get; init; }
        public required DateTime CreatedAtUtc { get; init; }
        public string? EscalatedFromAlertId { get; init; }
    }

    /// <summary>
    /// Result of sending an alert.
    /// </summary>
    public sealed class AlertResult
    {
        public required string AlertId { get; init; }
        public required AlertDeliveryStatus Status { get; init; }
        public required string Message { get; init; }
        public required List<string> ChannelsNotified { get; init; }
        public required DateTime SentAtUtc { get; init; }
    }

    /// <summary>
    /// Internal record of a sent alert.
    /// </summary>
    public sealed class AlertRecord
    {
        public required ComplianceAlert Alert { get; init; }
        public required long SequenceNumber { get; init; }
        public required DateTime SentAtUtc { get; init; }
        public required List<string> ChannelsNotified { get; init; }
        public bool Acknowledged { get; set; }
        public string? AcknowledgedBy { get; set; }
        public DateTime? AcknowledgedAtUtc { get; set; }
        public bool EscalationScheduled { get; init; }
    }

    /// <summary>
    /// Compliance-specific alert severity levels.
    /// </summary>
    public enum ComplianceAlertSeverity
    {
        Info,
        Warning,
        Critical,
        Emergency
    }

    /// <summary>
    /// Alert delivery status.
    /// </summary>
    public enum AlertDeliveryStatus
    {
        Delivered,
        PartialDelivery,
        Failed,
        Suppressed
    }

    #endregion
}
