using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.ThreatDetection
{
    /// <summary>
    /// Security Orchestration, Automation and Response (SOAR) strategy.
    /// Implements playbook-based incident response, automated containment, and ticketing integration.
    /// </summary>
    public sealed class SoarStrategy : AccessControlStrategyBase
    {
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<string, SecurityPlaybook> _playbooks = new();
        private readonly ConcurrentDictionary<string, IncidentResponse> _activeIncidents = new();
        private readonly ConcurrentQueue<ContainmentAction> _containmentQueue = new();

        public SoarStrategy(ILogger? logger = null)
        {
            _logger = logger ?? NullLogger.Instance;
        }

        /// <inheritdoc/>
        public override string StrategyId => "soar";

        /// <inheritdoc/>
        public override string StrategyName => "Security Orchestration and Response";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = false,
            SupportsTemporalAccess = true,
            SupportsGeographicRestrictions = true,
            MaxConcurrentEvaluations = 1000
        };

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            // Initialize default playbooks
            InitializeDefaultPlaybooks();

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <inheritdoc/>
        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            // Assess threat level based on context
            var threatLevel = AssessThreatLevel(context);

            // If threat detected, execute playbook
            if (threatLevel >= ThreatSeverity.Medium)
            {
                var playbook = SelectPlaybook(context, threatLevel);
                if (playbook != null)
                {
                    var incident = await ExecutePlaybookAsync(playbook, context, threatLevel, cancellationToken);

                    // Store active incident
                    _activeIncidents[incident.Id] = incident;

                    // Deny access for high/critical threats
                    if (threatLevel >= ThreatSeverity.High)
                    {
                        return new AccessDecision
                        {
                            IsGranted = false,
                            Reason = $"Access denied - {threatLevel} threat detected. Incident {incident.Id} created.",
                            ApplicablePolicies = new[] { "SoarAutoBlock", playbook.Name },
                            Metadata = new Dictionary<string, object>
                            {
                                ["IncidentId"] = incident.Id,
                                ["PlaybookExecuted"] = playbook.Name,
                                ["ThreatSeverity"] = threatLevel.ToString(),
                                ["ContainmentActions"] = incident.ExecutedActions.Count
                            }
                        };
                    }
                }
            }

            return new AccessDecision
            {
                IsGranted = true,
                Reason = "Access granted - no significant threat detected",
                ApplicablePolicies = new[] { "SoarMonitoring" },
                Metadata = new Dictionary<string, object>
                {
                    ["ThreatSeverity"] = threatLevel.ToString()
                }
            };
        }

        private ThreatSeverity AssessThreatLevel(AccessContext context)
        {
            var score = 0;

            // Check for suspicious patterns
            var action = context.Action.ToLowerInvariant();
            if (action == "delete" || action == "destroy")
                score += 30;

            // Check IP reputation (simplified)
            if (context.ClientIpAddress != null && IsHighRiskIp(context.ClientIpAddress))
                score += 40;

            // Check geographic location
            if (context.Location != null && IsHighRiskLocation(context.Location))
                score += 35;

            // Check time anomaly
            var hour = context.RequestTime.Hour;
            if (hour < 6 || hour > 22)
                score += 15;

            // Check resource sensitivity
            if (context.ResourceId.Contains("admin", StringComparison.OrdinalIgnoreCase) ||
                context.ResourceId.Contains("credential", StringComparison.OrdinalIgnoreCase))
                score += 25;

            // Classify severity
            if (score >= 80)
                return ThreatSeverity.Critical;
            if (score >= 60)
                return ThreatSeverity.High;
            if (score >= 40)
                return ThreatSeverity.Medium;
            return ThreatSeverity.Low;
        }

        private SecurityPlaybook? SelectPlaybook(AccessContext context, ThreatSeverity severity)
        {
            // Select appropriate playbook based on threat type and severity
            var candidatePlaybooks = _playbooks.Values
                .Where(p => p.MinimumSeverity <= severity && p.IsEnabled)
                .OrderByDescending(p => p.Priority)
                .ToList();

            foreach (var playbook in candidatePlaybooks)
            {
                if (playbook.TriggerCondition(context, severity))
                    return playbook;
            }

            return null;
        }

        private async Task<IncidentResponse> ExecutePlaybookAsync(
            SecurityPlaybook playbook,
            AccessContext context,
            ThreatSeverity severity,
            CancellationToken cancellationToken)
        {
            var incident = new IncidentResponse
            {
                Id = Guid.NewGuid().ToString("N"),
                PlaybookName = playbook.Name,
                SubjectId = context.SubjectId,
                ResourceId = context.ResourceId,
                Severity = severity,
                CreatedAt = DateTime.UtcNow,
                Status = IncidentStatus.InProgress
            };

            _logger.LogInformation("Executing playbook {Playbook} for incident {IncidentId}", playbook.Name, incident.Id);

            try
            {
                // Execute playbook steps in sequence
                foreach (var step in playbook.Steps)
                {
                    try
                    {
                        await ExecutePlaybookStepAsync(step, context, incident, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to execute playbook step {Step}", step.Name);
                        incident.Errors.Add($"Step '{step.Name}' failed: {ex.Message}");

                        if (step.IsCritical)
                        {
                            incident.Status = IncidentStatus.Failed;
                            return incident;
                        }
                    }
                }

                incident.Status = IncidentStatus.Completed;
                incident.CompletedAt = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Playbook execution failed for incident {IncidentId}", incident.Id);
                incident.Status = IncidentStatus.Failed;
                incident.Errors.Add($"Playbook execution failed: {ex.Message}");
            }

            return incident;
        }

        private async Task ExecutePlaybookStepAsync(
            PlaybookStep step,
            AccessContext context,
            IncidentResponse incident,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("Executing step: {Step}", step.Name);

            var action = new ContainmentAction
            {
                Id = Guid.NewGuid().ToString("N"),
                IncidentId = incident.Id,
                ActionType = step.ActionType,
                ExecutedAt = DateTime.UtcNow,
                Status = ActionStatus.InProgress
            };

            try
            {
                switch (step.ActionType)
                {
                    case ContainmentActionType.IsolateSubject:
                        await IsolateSubjectAsync(context.SubjectId, cancellationToken);
                        action.Details = $"Subject {context.SubjectId} isolated";
                        break;

                    case ContainmentActionType.BlockIp:
                        if (context.ClientIpAddress != null)
                        {
                            await BlockIpAddressAsync(context.ClientIpAddress, cancellationToken);
                            action.Details = $"IP {context.ClientIpAddress} blocked";
                        }
                        break;

                    case ContainmentActionType.RevokeSession:
                        await RevokeSessionAsync(context.SubjectId, cancellationToken);
                        action.Details = $"Session for {context.SubjectId} revoked";
                        break;

                    case ContainmentActionType.AlertSecurityTeam:
                        await AlertSecurityTeamAsync(incident, context, cancellationToken);
                        action.Details = "Security team alerted";
                        break;

                    case ContainmentActionType.CreateTicket:
                        var ticketId = await CreateTicketAsync(incident, context, cancellationToken);
                        action.Details = $"Ticket {ticketId} created";
                        break;

                    case ContainmentActionType.CaptureForensics:
                        await CaptureForensicsAsync(context, cancellationToken);
                        action.Details = "Forensic data captured";
                        break;

                    case ContainmentActionType.QuarantineResource:
                        await QuarantineResourceAsync(context.ResourceId, cancellationToken);
                        action.Details = $"Resource {context.ResourceId} quarantined";
                        break;
                }

                action.Status = ActionStatus.Completed;
                incident.ExecutedActions.Add(action);
                _containmentQueue.Enqueue(action);
            }
            catch (Exception ex)
            {
                action.Status = ActionStatus.Failed;
                action.Details = $"Failed: {ex.Message}";
                throw;
            }
        }

        private Task IsolateSubjectAsync(string subjectId, CancellationToken cancellationToken)
        {
            // In production: Disable account, revoke tokens, terminate sessions
            _logger.LogWarning("Subject {Subject} isolated due to security incident", subjectId);
            return Task.CompletedTask;
        }

        private Task BlockIpAddressAsync(string ipAddress, CancellationToken cancellationToken)
        {
            // In production: Add to firewall blocklist, update WAF rules
            _logger.LogWarning("IP address {IP} blocked due to security incident", ipAddress);
            return Task.CompletedTask;
        }

        private Task RevokeSessionAsync(string subjectId, CancellationToken cancellationToken)
        {
            // In production: Invalidate JWT tokens, clear session cache
            _logger.LogWarning("Sessions revoked for subject {Subject}", subjectId);
            return Task.CompletedTask;
        }

        private Task AlertSecurityTeamAsync(IncidentResponse incident, AccessContext context, CancellationToken cancellationToken)
        {
            // In production: Send email/SMS/Slack notification to SOC
            _logger.LogWarning("Security team alerted for incident {IncidentId}", incident.Id);
            return Task.CompletedTask;
        }

        private Task<string> CreateTicketAsync(IncidentResponse incident, AccessContext context, CancellationToken cancellationToken)
        {
            // In production: Create ticket in Jira/ServiceNow
            var ticketId = $"SEC-{DateTime.UtcNow:yyyyMMdd}-{incident.Id[..8]}";
            _logger.LogInformation("Created security ticket {TicketId} for incident {IncidentId}", ticketId, incident.Id);
            return Task.FromResult(ticketId);
        }

        private Task CaptureForensicsAsync(AccessContext context, CancellationToken cancellationToken)
        {
            // In production: Capture memory dumps, logs, network traffic
            _logger.LogInformation("Forensic data captured for subject {Subject}", context.SubjectId);
            return Task.CompletedTask;
        }

        private Task QuarantineResourceAsync(string resourceId, CancellationToken cancellationToken)
        {
            // In production: Move resource to quarantine, revoke access
            _logger.LogWarning("Resource {Resource} quarantined", resourceId);
            return Task.CompletedTask;
        }

        private void InitializeDefaultPlaybooks()
        {
            // Brute force playbook
            _playbooks["brute-force"] = new SecurityPlaybook
            {
                Name = "Brute Force Response",
                Description = "Response to brute force attempts",
                MinimumSeverity = ThreatSeverity.Medium,
                Priority = 80,
                IsEnabled = true,
                TriggerCondition = (ctx, sev) => sev >= ThreatSeverity.Medium,
                Steps = new List<PlaybookStep>
                {
                    new() { Name = "Revoke Session", ActionType = ContainmentActionType.RevokeSession, IsCritical = true },
                    new() { Name = "Block IP", ActionType = ContainmentActionType.BlockIp, IsCritical = false },
                    new() { Name = "Alert Security", ActionType = ContainmentActionType.AlertSecurityTeam, IsCritical = false }
                }
            };

            // Data exfiltration playbook
            _playbooks["data-exfiltration"] = new SecurityPlaybook
            {
                Name = "Data Exfiltration Response",
                Description = "Response to suspected data exfiltration",
                MinimumSeverity = ThreatSeverity.High,
                Priority = 90,
                IsEnabled = true,
                TriggerCondition = (ctx, sev) => sev >= ThreatSeverity.High,
                Steps = new List<PlaybookStep>
                {
                    new() { Name = "Isolate Subject", ActionType = ContainmentActionType.IsolateSubject, IsCritical = true },
                    new() { Name = "Quarantine Resource", ActionType = ContainmentActionType.QuarantineResource, IsCritical = true },
                    new() { Name = "Capture Forensics", ActionType = ContainmentActionType.CaptureForensics, IsCritical = false },
                    new() { Name = "Create Ticket", ActionType = ContainmentActionType.CreateTicket, IsCritical = false },
                    new() { Name = "Alert Security", ActionType = ContainmentActionType.AlertSecurityTeam, IsCritical = true }
                }
            };

            // Insider threat playbook
            _playbooks["insider-threat"] = new SecurityPlaybook
            {
                Name = "Insider Threat Response",
                Description = "Response to insider threat indicators",
                MinimumSeverity = ThreatSeverity.Critical,
                Priority = 95,
                IsEnabled = true,
                TriggerCondition = (ctx, sev) => sev == ThreatSeverity.Critical,
                Steps = new List<PlaybookStep>
                {
                    new() { Name = "Capture Forensics", ActionType = ContainmentActionType.CaptureForensics, IsCritical = true },
                    new() { Name = "Isolate Subject", ActionType = ContainmentActionType.IsolateSubject, IsCritical = true },
                    new() { Name = "Create High-Priority Ticket", ActionType = ContainmentActionType.CreateTicket, IsCritical = true },
                    new() { Name = "Alert Security", ActionType = ContainmentActionType.AlertSecurityTeam, IsCritical = true }
                }
            };
        }

        private bool IsHighRiskIp(string ipAddress)
        {
            // Simplified check - in production: check against threat intelligence feeds
            return false;
        }

        private bool IsHighRiskLocation(GeoLocation location)
        {
            var highRiskCountries = new[] { "CN", "RU", "KP", "IR" };
            return highRiskCountries.Contains(location.Country, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Gets active incidents.
        /// </summary>
        public IReadOnlyCollection<IncidentResponse> GetActiveIncidents()
        {
            return _activeIncidents.Values.Where(i => i.Status == IncidentStatus.InProgress).ToList().AsReadOnly();
        }

        /// <summary>
        /// Gets containment actions for an incident.
        /// </summary>
        public IReadOnlyCollection<ContainmentAction> GetContainmentActions(string incidentId)
        {
            return _containmentQueue.Where(a => a.IncidentId == incidentId).ToList().AsReadOnly();
        }
    }

    public enum ThreatSeverity
    {
        Low = 0,
        Medium = 1,
        High = 2,
        Critical = 3
    }

    public enum IncidentStatus
    {
        InProgress,
        Completed,
        Failed,
        Cancelled
    }

    public enum ContainmentActionType
    {
        IsolateSubject,
        BlockIp,
        RevokeSession,
        AlertSecurityTeam,
        CreateTicket,
        CaptureForensics,
        QuarantineResource
    }

    public enum ActionStatus
    {
        InProgress,
        Completed,
        Failed
    }

    public sealed class SecurityPlaybook
    {
        public required string Name { get; init; }
        public required string Description { get; init; }
        public required ThreatSeverity MinimumSeverity { get; init; }
        public required int Priority { get; init; }
        public required bool IsEnabled { get; set; }
        public required Func<AccessContext, ThreatSeverity, bool> TriggerCondition { get; init; }
        public required List<PlaybookStep> Steps { get; init; }
    }

    public sealed class PlaybookStep
    {
        public required string Name { get; init; }
        public required ContainmentActionType ActionType { get; init; }
        public required bool IsCritical { get; init; }
    }

    public sealed class IncidentResponse
    {
        public required string Id { get; init; }
        public required string PlaybookName { get; init; }
        public required string SubjectId { get; init; }
        public required string ResourceId { get; init; }
        public required ThreatSeverity Severity { get; init; }
        public required DateTime CreatedAt { get; init; }
        public DateTime? CompletedAt { get; set; }
        public required IncidentStatus Status { get; set; }
        public List<ContainmentAction> ExecutedActions { get; init; } = new();
        public List<string> Errors { get; init; } = new();
    }

    public sealed class ContainmentAction
    {
        public required string Id { get; init; }
        public required string IncidentId { get; init; }
        public required ContainmentActionType ActionType { get; init; }
        public required DateTime ExecutedAt { get; init; }
        public required ActionStatus Status { get; set; }
        public string? Details { get; set; }
    }
}
