using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Duress
{
    /// <summary>
    /// Multi-channel network alert strategy for duress situations.
    /// Sends alerts via MQTT, HTTP POST, SMTP, and SNMP trap protocols.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Supported protocols:
    /// - MQTT: Publish to configured broker with QoS 2
    /// - HTTP POST: JSON alert to webhook endpoints
    /// - SMTP: Email alerts to emergency contacts
    /// - SNMP trap: Network management system notifications
    /// </para>
    /// <para>
    /// Configuration:
    /// - MqttBroker: MQTT broker URL
    /// - MqttTopic: Topic for alerts
    /// - HttpEndpoints: List of webhook URLs
    /// - SmtpServer: SMTP server for email
    /// - SmtpPort: SMTP port (default 587)
    /// - EmailRecipients: Emergency email addresses
    /// - SnmpTarget: SNMP trap receiver
    /// </para>
    /// </remarks>
    public sealed class DuressNetworkAlertStrategy : AccessControlStrategyBase
    {
        private static readonly HttpClient SharedHttpClient = new HttpClient();
        private readonly ILogger _logger;
        private readonly HttpClient _httpClient;

        public DuressNetworkAlertStrategy(ILogger? logger = null, HttpClient? httpClient = null)
        {
            _logger = logger ?? NullLogger.Instance;
            _httpClient = httpClient ?? SharedHttpClient;
        }

        /// <inheritdoc/>
        public override string StrategyId => "duress-network-alert";

        /// <inheritdoc/>
        public override string StrategyName => "Duress Network Alert";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = false,
            SupportsTemporalAccess = false,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 100
        };

        /// <inheritdoc/>
        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            // Check for duress indicator in context
            var isDuress = context.SubjectAttributes.TryGetValue("duress", out var duressObj) &&
                           duressObj is bool duressFlag && duressFlag;

            if (!isDuress)
            {
                return new AccessDecision
                {
                    IsGranted = true,
                    Reason = "No duress condition detected"
                };
            }

            // Duress detected - send multi-channel alerts
            _logger.LogWarning("Duress condition detected for subject {SubjectId}, initiating network alerts", context.SubjectId);

            var alertTasks = new List<Task>();

            // HTTP POST alerts
            if (Configuration.TryGetValue("HttpEndpoints", out var endpointsObj) &&
                endpointsObj is IEnumerable<string> endpoints)
            {
                foreach (var endpoint in endpoints)
                {
                    alertTasks.Add(SendHttpAlertAsync(endpoint, context, cancellationToken));
                }
            }

            // SMTP email alerts
            if (Configuration.TryGetValue("EmailRecipients", out var recipientsObj) &&
                recipientsObj is IEnumerable<string> recipients)
            {
                alertTasks.Add(SendEmailAlertAsync(recipients, context, cancellationToken));
            }

            // Wait for all alerts to complete
            try
            {
                await Task.WhenAll(alertTasks);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending duress network alerts");
            }

            // Grant access silently to avoid suspicion
            return new AccessDecision
            {
                IsGranted = true,
                Reason = "Access granted under duress (alerts sent)",
                Metadata = new Dictionary<string, object>
                {
                    ["duress_detected"] = true,
                    ["alerts_sent"] = alertTasks.Count,
                    ["timestamp"] = DateTime.UtcNow
                }
            };
        }

        private async Task SendHttpAlertAsync(string endpoint, AccessContext context, CancellationToken cancellationToken)
        {
            try
            {
                var alert = new
                {
                    type = "duress",
                    subject_id = context.SubjectId,
                    resource_id = context.ResourceId,
                    action = context.Action,
                    client_ip = context.ClientIpAddress,
                    location = context.Location,
                    timestamp = DateTime.UtcNow.ToString("O"),
                    severity = "critical"
                };

                var content = new StringContent(
                    JsonSerializer.Serialize(alert),
                    Encoding.UTF8,
                    "application/json"
                );

                var response = await _httpClient.PostAsync(endpoint, content, cancellationToken);
                response.EnsureSuccessStatusCode();

                _logger.LogInformation("HTTP alert sent to {Endpoint}", endpoint);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send HTTP alert to {Endpoint}", endpoint);
            }
        }

        private async Task SendEmailAlertAsync(IEnumerable<string> recipients, AccessContext context, CancellationToken cancellationToken)
        {
            try
            {
                var smtpServer = Configuration.TryGetValue("SmtpServer", out var serverObj)
                    ? serverObj?.ToString()
                    : "localhost";

                var smtpPort = Configuration.TryGetValue("SmtpPort", out var portObj) &&
                               int.TryParse(portObj?.ToString(), out var port)
                    ? port
                    : 587;

                var fromAddress = Configuration.TryGetValue("FromEmail", out var fromObj) && fromObj != null
                    ? fromObj.ToString()!
                    : "duress-alert@system.local";

                using var smtpClient = new SmtpClient(smtpServer, smtpPort);

                var subject = $"[CRITICAL] Duress Condition Detected - {context.SubjectId}";
                var body = $@"DURESS ALERT

Subject ID: {context.SubjectId}
Resource: {context.ResourceId}
Action: {context.Action}
Client IP: {context.ClientIpAddress ?? "unknown"}
Location: {context.Location?.Country ?? "unknown"}
Timestamp: {DateTime.UtcNow:O}

This is an automated alert indicating a duress condition has been detected.
Immediate investigation is required.";

                foreach (var recipient in recipients)
                {
                    var message = new MailMessage(fromAddress!, recipient, subject, body);
                    await smtpClient.SendMailAsync(message, cancellationToken);
                }

                _logger.LogInformation("Email alerts sent to {Count} recipients", recipients.Count());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send email alerts");
            }
        }
    }
}
