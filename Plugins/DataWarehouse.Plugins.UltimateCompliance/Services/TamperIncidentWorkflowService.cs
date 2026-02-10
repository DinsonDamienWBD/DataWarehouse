using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateCompliance.Services
{
    /// <summary>
    /// Automated tamper incident workflow service. Creates incident tickets when compliance
    /// violations or tamper events are detected. Manages incident lifecycle from Open through
    /// Investigating, Remediated, to Closed. Attaches relevant compliance evidence and notifies
    /// responsible parties via ComplianceAlertService. Publishes to "compliance.incident.created".
    /// </summary>
    public sealed class TamperIncidentWorkflowService
    {
        private readonly ComplianceAlertService _alertService;
        private readonly IMessageBus? _messageBus;
        private readonly string _pluginId;
        private readonly ConcurrentDictionary<string, IncidentTicket> _incidents = new();
        private long _incidentSequence;

        public TamperIncidentWorkflowService(
            ComplianceAlertService alertService,
            IMessageBus? messageBus,
            string pluginId)
        {
            _alertService = alertService ?? throw new ArgumentNullException(nameof(alertService));
            _messageBus = messageBus;
            _pluginId = pluginId;
        }

        /// <summary>
        /// Creates an incident ticket from a tamper event. Automatically assigns severity,
        /// attaches evidence, notifies responsible parties, and publishes the incident.
        /// </summary>
        public async Task<IncidentTicket> CreateIncidentAsync(
            TamperEvent tamperEvent,
            CancellationToken cancellationToken = default)
        {
            if (tamperEvent == null) throw new ArgumentNullException(nameof(tamperEvent));

            var sequence = Interlocked.Increment(ref _incidentSequence);
            var incidentId = $"INC-{DateTime.UtcNow:yyyyMMdd}-{sequence:D6}";

            var ticket = new IncidentTicket
            {
                IncidentId = incidentId,
                Title = $"Tamper Incident: {tamperEvent.Description}",
                Description = BuildIncidentDescription(tamperEvent),
                Status = IncidentStatus.Open,
                Priority = MapSeverityToPriority(tamperEvent.Severity),
                Severity = tamperEvent.Severity,
                CreatedAtUtc = DateTime.UtcNow,
                DetectedAtUtc = tamperEvent.DetectedAtUtc,
                AffectedResource = tamperEvent.AffectedResource,
                DetectedBy = tamperEvent.DetectedBy,
                SourceEventId = tamperEvent.EventId,
                EvidenceAttachments = new List<IncidentEvidence>(),
                StatusHistory = new List<StatusTransition>
                {
                    new()
                    {
                        FromStatus = IncidentStatus.Open,
                        ToStatus = IncidentStatus.Open,
                        TransitionedAtUtc = DateTime.UtcNow,
                        TransitionedBy = "System",
                        Reason = "Incident auto-created from tamper event detection"
                    }
                },
                NotificationsSent = new List<string>()
            };

            // Attach initial evidence from the tamper event
            ticket.EvidenceAttachments.Add(new IncidentEvidence
            {
                EvidenceId = $"EV-{incidentId}-001",
                Type = EvidenceType.TamperEvent,
                Description = tamperEvent.Description,
                CollectedAtUtc = tamperEvent.DetectedAtUtc,
                SourceSystem = tamperEvent.DetectedBy,
                Metadata = new Dictionary<string, string>
                {
                    ["eventId"] = tamperEvent.EventId,
                    ["severity"] = tamperEvent.Severity.ToString(),
                    ["resource"] = tamperEvent.AffectedResource ?? "N/A",
                    ["hashBefore"] = tamperEvent.HashBefore ?? "N/A",
                    ["hashAfter"] = tamperEvent.HashAfter ?? "N/A"
                }
            });

            // If hash values are present, add hash comparison evidence
            if (!string.IsNullOrEmpty(tamperEvent.HashBefore) && !string.IsNullOrEmpty(tamperEvent.HashAfter))
            {
                ticket.EvidenceAttachments.Add(new IncidentEvidence
                {
                    EvidenceId = $"EV-{incidentId}-002",
                    Type = EvidenceType.HashComparison,
                    Description = $"Hash mismatch detected. Before: {tamperEvent.HashBefore[..Math.Min(16, tamperEvent.HashBefore.Length)]}... After: {tamperEvent.HashAfter[..Math.Min(16, tamperEvent.HashAfter.Length)]}...",
                    CollectedAtUtc = tamperEvent.DetectedAtUtc,
                    SourceSystem = "Integrity Verification",
                    Metadata = new Dictionary<string, string>
                    {
                        ["hashBefore"] = tamperEvent.HashBefore,
                        ["hashAfter"] = tamperEvent.HashAfter,
                        ["algorithm"] = "SHA-256"
                    }
                });
            }

            _incidents[incidentId] = ticket;

            // Notify responsible parties via alert service
            var alertResult = await _alertService.SendAlertAsync(new ComplianceAlert
            {
                AlertId = $"ALERT-{incidentId}",
                Title = $"Tamper Incident Created: {incidentId}",
                Message = $"Severity: {tamperEvent.Severity}. {tamperEvent.Description}. " +
                          $"Affected: {tamperEvent.AffectedResource ?? "Unknown"}. " +
                          $"Detected by: {tamperEvent.DetectedBy}.",
                Severity = tamperEvent.Severity,
                Framework = "Integrity",
                Source = _pluginId,
                CreatedAtUtc = DateTime.UtcNow
            }, cancellationToken);

            ticket.NotificationsSent.AddRange(alertResult.ChannelsNotified);

            // Publish incident created event
            if (_messageBus != null)
            {
                await _messageBus.PublishAsync("compliance.incident.created", new PluginMessage
                {
                    Type = "compliance.incident.created",
                    Source = _pluginId,
                    Payload = new Dictionary<string, object>
                    {
                        ["incidentId"] = ticket.IncidentId,
                        ["title"] = ticket.Title,
                        ["severity"] = ticket.Severity.ToString(),
                        ["priority"] = ticket.Priority.ToString(),
                        ["status"] = ticket.Status.ToString(),
                        ["affectedResource"] = ticket.AffectedResource ?? "",
                        ["evidenceCount"] = ticket.EvidenceAttachments.Count,
                        ["createdAt"] = ticket.CreatedAtUtc.ToString("O")
                    }
                }, cancellationToken);
            }

            return ticket;
        }

        /// <summary>
        /// Transitions an incident to a new status with audit trail.
        /// Valid transitions: Open -> Investigating -> Remediated -> Closed.
        /// </summary>
        public async Task<bool> TransitionStatusAsync(
            string incidentId,
            IncidentStatus newStatus,
            string transitionedBy,
            string reason,
            CancellationToken cancellationToken = default)
        {
            if (!_incidents.TryGetValue(incidentId, out var ticket))
                return false;

            if (!IsValidTransition(ticket.Status, newStatus))
                return false;

            var transition = new StatusTransition
            {
                FromStatus = ticket.Status,
                ToStatus = newStatus,
                TransitionedAtUtc = DateTime.UtcNow,
                TransitionedBy = transitionedBy,
                Reason = reason
            };

            ticket.StatusHistory.Add(transition);
            ticket.Status = newStatus;

            if (newStatus == IncidentStatus.Closed)
            {
                ticket.ClosedAtUtc = DateTime.UtcNow;
                ticket.ClosedBy = transitionedBy;
            }

            // Publish status change
            if (_messageBus != null)
            {
                await _messageBus.PublishAsync("compliance.incident.updated", new PluginMessage
                {
                    Type = "compliance.incident.updated",
                    Source = _pluginId,
                    Payload = new Dictionary<string, object>
                    {
                        ["incidentId"] = incidentId,
                        ["previousStatus"] = transition.FromStatus.ToString(),
                        ["newStatus"] = newStatus.ToString(),
                        ["transitionedBy"] = transitionedBy,
                        ["reason"] = reason
                    }
                }, cancellationToken);
            }

            return true;
        }

        /// <summary>
        /// Attaches additional evidence to an open incident.
        /// </summary>
        public bool AttachEvidence(string incidentId, IncidentEvidence evidence)
        {
            if (!_incidents.TryGetValue(incidentId, out var ticket))
                return false;

            if (ticket.Status == IncidentStatus.Closed)
                return false;

            ticket.EvidenceAttachments.Add(evidence);
            return true;
        }

        /// <summary>
        /// Returns an incident by ID.
        /// </summary>
        public IncidentTicket? GetIncident(string incidentId)
        {
            return _incidents.TryGetValue(incidentId, out var ticket) ? ticket : null;
        }

        /// <summary>
        /// Returns all open incidents ordered by priority.
        /// </summary>
        public IReadOnlyList<IncidentTicket> GetOpenIncidents()
        {
            return _incidents.Values
                .Where(t => t.Status != IncidentStatus.Closed)
                .OrderByDescending(t => t.Priority)
                .ThenBy(t => t.CreatedAtUtc)
                .ToList()
                .AsReadOnly();
        }

        /// <summary>
        /// Returns incident metrics.
        /// </summary>
        public IncidentMetrics GetMetrics()
        {
            var all = _incidents.Values.ToList();
            return new IncidentMetrics
            {
                TotalIncidents = all.Count,
                OpenIncidents = all.Count(i => i.Status == IncidentStatus.Open),
                InvestigatingIncidents = all.Count(i => i.Status == IncidentStatus.Investigating),
                RemediatedIncidents = all.Count(i => i.Status == IncidentStatus.Remediated),
                ClosedIncidents = all.Count(i => i.Status == IncidentStatus.Closed),
                AverageResolutionTime = all
                    .Where(i => i.ClosedAtUtc.HasValue)
                    .Select(i => (i.ClosedAtUtc!.Value - i.CreatedAtUtc).TotalHours)
                    .DefaultIfEmpty(0)
                    .Average(),
                IncidentsByPriority = all.GroupBy(i => i.Priority).ToDictionary(g => g.Key, g => g.Count()),
                IncidentsBySeverity = all.GroupBy(i => i.Severity).ToDictionary(g => g.Key, g => g.Count())
            };
        }

        private bool IsValidTransition(IncidentStatus current, IncidentStatus target)
        {
            return (current, target) switch
            {
                (IncidentStatus.Open, IncidentStatus.Investigating) => true,
                (IncidentStatus.Open, IncidentStatus.Closed) => true,  // Direct close (false positive)
                (IncidentStatus.Investigating, IncidentStatus.Remediated) => true,
                (IncidentStatus.Investigating, IncidentStatus.Closed) => true,
                (IncidentStatus.Remediated, IncidentStatus.Closed) => true,
                (IncidentStatus.Remediated, IncidentStatus.Investigating) => true, // Re-investigate
                _ => false
            };
        }

        private IncidentPriority MapSeverityToPriority(ComplianceAlertSeverity severity)
        {
            return severity switch
            {
                ComplianceAlertSeverity.Emergency => IncidentPriority.P1,
                ComplianceAlertSeverity.Critical => IncidentPriority.P2,
                ComplianceAlertSeverity.Warning => IncidentPriority.P3,
                ComplianceAlertSeverity.Info => IncidentPriority.P4,
                _ => IncidentPriority.P4
            };
        }

        private string BuildIncidentDescription(TamperEvent tamperEvent)
        {
            var parts = new List<string>
            {
                $"Tamper/compliance violation detected at {tamperEvent.DetectedAtUtc:yyyy-MM-dd HH:mm:ss} UTC.",
                $"Severity: {tamperEvent.Severity}.",
                $"Description: {tamperEvent.Description}."
            };

            if (!string.IsNullOrEmpty(tamperEvent.AffectedResource))
                parts.Add($"Affected Resource: {tamperEvent.AffectedResource}.");

            if (!string.IsNullOrEmpty(tamperEvent.DetectedBy))
                parts.Add($"Detected By: {tamperEvent.DetectedBy}.");

            if (!string.IsNullOrEmpty(tamperEvent.HashBefore) && !string.IsNullOrEmpty(tamperEvent.HashAfter))
                parts.Add($"Hash mismatch: expected {tamperEvent.HashBefore}, found {tamperEvent.HashAfter}.");

            parts.Add("Automated incident ticket created for investigation and remediation.");

            return string.Join(" ", parts);
        }
    }

    #region Incident Types

    /// <summary>
    /// Tamper event triggering incident creation.
    /// </summary>
    public sealed class TamperEvent
    {
        public required string EventId { get; init; }
        public required DateTime DetectedAtUtc { get; init; }
        public required ComplianceAlertSeverity Severity { get; init; }
        public required string Description { get; init; }
        public string? AffectedResource { get; init; }
        public required string DetectedBy { get; init; }
        public string? HashBefore { get; init; }
        public string? HashAfter { get; init; }
    }

    /// <summary>
    /// Incident ticket with full lifecycle tracking.
    /// </summary>
    public sealed class IncidentTicket
    {
        public required string IncidentId { get; init; }
        public required string Title { get; init; }
        public required string Description { get; init; }
        public IncidentStatus Status { get; set; }
        public required IncidentPriority Priority { get; init; }
        public required ComplianceAlertSeverity Severity { get; init; }
        public required DateTime CreatedAtUtc { get; init; }
        public required DateTime DetectedAtUtc { get; init; }
        public string? AffectedResource { get; init; }
        public required string DetectedBy { get; init; }
        public required string SourceEventId { get; init; }
        public required List<IncidentEvidence> EvidenceAttachments { get; init; }
        public required List<StatusTransition> StatusHistory { get; init; }
        public required List<string> NotificationsSent { get; init; }
        public DateTime? ClosedAtUtc { get; set; }
        public string? ClosedBy { get; set; }
    }

    /// <summary>
    /// Evidence attached to an incident.
    /// </summary>
    public sealed class IncidentEvidence
    {
        public required string EvidenceId { get; init; }
        public required EvidenceType Type { get; init; }
        public required string Description { get; init; }
        public required DateTime CollectedAtUtc { get; init; }
        public required string SourceSystem { get; init; }
        public required Dictionary<string, string> Metadata { get; init; }
    }

    /// <summary>
    /// Status transition record for audit trail.
    /// </summary>
    public sealed class StatusTransition
    {
        public required IncidentStatus FromStatus { get; init; }
        public required IncidentStatus ToStatus { get; init; }
        public required DateTime TransitionedAtUtc { get; init; }
        public required string TransitionedBy { get; init; }
        public required string Reason { get; init; }
    }

    /// <summary>
    /// Incident metrics summary.
    /// </summary>
    public sealed class IncidentMetrics
    {
        public required int TotalIncidents { get; init; }
        public required int OpenIncidents { get; init; }
        public required int InvestigatingIncidents { get; init; }
        public required int RemediatedIncidents { get; init; }
        public required int ClosedIncidents { get; init; }
        public required double AverageResolutionTime { get; init; }
        public required Dictionary<IncidentPriority, int> IncidentsByPriority { get; init; }
        public required Dictionary<ComplianceAlertSeverity, int> IncidentsBySeverity { get; init; }
    }

    /// <summary>
    /// Incident lifecycle status.
    /// </summary>
    public enum IncidentStatus
    {
        Open,
        Investigating,
        Remediated,
        Closed
    }

    /// <summary>
    /// Incident priority level.
    /// </summary>
    public enum IncidentPriority
    {
        P1,
        P2,
        P3,
        P4
    }

    /// <summary>
    /// Type of evidence attached to an incident.
    /// </summary>
    public enum EvidenceType
    {
        TamperEvent,
        HashComparison,
        AuditLog,
        ComplianceReport,
        SystemSnapshot,
        UserActivity
    }

    #endregion
}
