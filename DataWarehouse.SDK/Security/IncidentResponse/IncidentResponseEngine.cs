using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;
using Microsoft.Extensions.Logging;
using ObsAuditEntry = DataWarehouse.SDK.Contracts.Observability.AuditEntry;
using AuditOutcome = DataWarehouse.SDK.Contracts.Observability.AuditOutcome;
using IAuditTrail = DataWarehouse.SDK.Contracts.Observability.IAuditTrail;

namespace DataWarehouse.SDK.Security.IncidentResponse
{
    /// <summary>
    /// Automated incident response engine that manages security incidents, executes containment
    /// playbooks, and supports auto-response rules triggered by event patterns.
    /// All containment actions communicate via the message bus and are fully audited.
    /// </summary>
    public sealed class IncidentResponseEngine : IDisposable
    {
        private readonly IMessageBus _messageBus;
        private readonly IAuditTrail _auditTrail;
        private readonly ILogger _logger;
        private readonly BoundedDictionary<string, IContainmentAction> _actions = new BoundedDictionary<string, IContainmentAction>(1000);
        private readonly BoundedDictionary<string, Incident> _incidents = new BoundedDictionary<string, Incident>(1000);
        private readonly BoundedDictionary<string, AutoResponseRule> _autoResponseRules = new BoundedDictionary<string, AutoResponseRule>(1000);
        private readonly BoundedDictionary<string, ConcurrentQueue<DateTimeOffset>> _eventWindows = new BoundedDictionary<string, ConcurrentQueue<DateTimeOffset>>(1000);
        private readonly List<IDisposable> _subscriptions = new();
        private bool _disposed;

        /// <summary>
        /// Creates a new incident response engine.
        /// </summary>
        /// <param name="messageBus">Message bus for containment action enforcement and event monitoring.</param>
        /// <param name="auditTrail">Audit trail for logging all incident response actions.</param>
        /// <param name="logger">Logger instance.</param>
        public IncidentResponseEngine(IMessageBus messageBus, IAuditTrail auditTrail, ILogger logger)
        {
            _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
            _auditTrail = auditTrail ?? throw new ArgumentNullException(nameof(auditTrail));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Number of registered containment actions.
        /// </summary>
        public int ActionCount => _actions.Count;

        /// <summary>
        /// Number of active incidents.
        /// </summary>
        public int ActiveIncidentCount => _incidents.Values.Count(i => i.Status == IncidentStatus.Open || i.Status == IncidentStatus.Contained);

        /// <summary>
        /// Number of registered auto-response rules.
        /// </summary>
        public int AutoResponseRuleCount => _autoResponseRules.Count;

        /// <summary>
        /// Registers a containment action that can be used in playbooks.
        /// </summary>
        public void RegisterAction(IContainmentAction action)
        {
            ArgumentNullException.ThrowIfNull(action);
            if (!_actions.TryAdd(action.ActionId, action))
            {
                throw new InvalidOperationException($"Containment action '{action.ActionId}' is already registered.");
            }
            _logger.LogInformation("Registered containment action: {ActionId} - {Description}", action.ActionId, action.Description);
        }

        /// <summary>
        /// Registers all built-in containment actions.
        /// </summary>
        public void RegisterBuiltInActions()
        {
            RegisterAction(new IsolateNodeAction(_messageBus));
            RegisterAction(new RevokeCredentialsAction(_messageBus));
            RegisterAction(new BlockIpAction(_messageBus));
            RegisterAction(new QuarantineDataAction(_messageBus));
            RegisterAction(new ReadOnlyModeAction(_messageBus));
            RegisterAction(new AuditSnapshotAction(_messageBus));
        }

        /// <summary>
        /// Creates a new security incident.
        /// </summary>
        public Incident CreateIncident(IncidentSeverity severity, string description, Dictionary<string, string>? metadata = null)
        {
            var incident = new Incident
            {
                Id = Guid.NewGuid(),
                CreatedAt = DateTimeOffset.UtcNow,
                Severity = severity,
                Description = description,
                Status = IncidentStatus.Open,
                Metadata = metadata ?? new Dictionary<string, string>()
            };

            _incidents[incident.Id.ToString()] = incident;

            _logger.LogWarning("Security incident created: {IncidentId} [{Severity}] {Description}",
                incident.Id, severity, description);

            // Fire-and-forget audit entry
            _ = _auditTrail.RecordAsync(ObsAuditEntry.Create(
                actor: "IncidentResponseEngine",
                action: "CreateIncident",
                resourceType: "Incident",
                resourceId: incident.Id.ToString(),
                outcome: AuditOutcome.Success,
                detail: $"[{severity}] {description}"));

            return incident;
        }

        /// <summary>
        /// Retrieves an incident by ID.
        /// </summary>
        public Incident? GetIncident(string incidentId)
        {
            _incidents.TryGetValue(incidentId, out var incident);
            return incident;
        }

        /// <summary>
        /// Gets all incidents, optionally filtered by status.
        /// </summary>
        public IReadOnlyList<Incident> GetIncidents(IncidentStatus? status = null)
        {
            var query = _incidents.Values.AsEnumerable();
            if (status.HasValue)
            {
                query = query.Where(i => i.Status == status.Value);
            }
            return query.OrderByDescending(i => i.CreatedAt).ToList();
        }

        /// <summary>
        /// Executes a containment playbook against an incident.
        /// Actions are executed sequentially; each is audited before and after execution.
        /// </summary>
        public async Task<PlaybookResult> ExecutePlaybook(Incident incident, IReadOnlyList<string> actionIds, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(incident);
            ArgumentNullException.ThrowIfNull(actionIds);

            var results = new List<ContainmentResult>();

            _logger.LogWarning("Executing playbook for incident {IncidentId} with {Count} actions: [{Actions}]",
                incident.Id, actionIds.Count, string.Join(", ", actionIds));

            foreach (var actionId in actionIds)
            {
                if (!_actions.TryGetValue(actionId, out var action))
                {
                    results.Add(new ContainmentResult
                    {
                        Success = false,
                        ActionId = actionId,
                        Details = $"Action '{actionId}' not found in registry",
                        ErrorMessage = "Action not registered"
                    });
                    continue;
                }

                // Audit BEFORE execution (intent)
                await _auditTrail.RecordAsync(ObsAuditEntry.Create(
                    actor: "IncidentResponseEngine",
                    action: $"ExecuteAction:Intent:{actionId}",
                    resourceType: "Incident",
                    resourceId: incident.Id.ToString(),
                    outcome: AuditOutcome.Success,
                    detail: $"About to execute: {action.Description}"));

                var ctx = new ContainmentContext
                {
                    IncidentId = incident.Id.ToString(),
                    Severity = incident.Severity,
                    TargetNodeId = incident.Metadata.GetValueOrDefault("targetNodeId"),
                    TargetUserId = incident.Metadata.GetValueOrDefault("targetUserId"),
                    TargetIpAddress = incident.Metadata.GetValueOrDefault("targetIpAddress"),
                    TargetDataKey = incident.Metadata.GetValueOrDefault("targetDataKey"),
                    Metadata = new Dictionary<string, string>(incident.Metadata)
                };

                ContainmentResult result;
                try
                {
                    result = await action.ExecuteAsync(ctx, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Containment action {ActionId} failed for incident {IncidentId}", actionId, incident.Id);
                    result = new ContainmentResult
                    {
                        Success = false,
                        ActionId = actionId,
                        Details = $"Action failed with exception: {ex.Message}",
                        ErrorMessage = ex.Message
                    };
                }

                // Audit AFTER execution (result)
                await _auditTrail.RecordAsync(ObsAuditEntry.Create(
                    actor: "IncidentResponseEngine",
                    action: $"ExecuteAction:Result:{actionId}",
                    resourceType: "Incident",
                    resourceId: incident.Id.ToString(),
                    outcome: result.Success ? AuditOutcome.Success : AuditOutcome.Failure,
                    detail: result.Details));

                results.Add(result);
            }

            // Update incident status
            var allSucceeded = results.All(r => r.Success);
            incident.Status = allSucceeded ? IncidentStatus.Contained : IncidentStatus.Open;
            incident.Actions.AddRange(results);

            var playbookResult = new PlaybookResult
            {
                Incident = incident,
                AllSucceeded = allSucceeded,
                Results = results
            };

            _logger.LogInformation("Playbook completed for incident {IncidentId}: {Status} ({Succeeded}/{Total} actions succeeded)",
                incident.Id, allSucceeded ? "ALL SUCCEEDED" : "PARTIAL FAILURE", results.Count(r => r.Success), results.Count);

            return playbookResult;
        }

        /// <summary>
        /// Resolves an incident (marks as resolved after containment is verified).
        /// </summary>
        public async Task ResolveIncident(string incidentId, string resolution, CancellationToken ct = default)
        {
            if (!_incidents.TryGetValue(incidentId, out var incident))
            {
                throw new KeyNotFoundException($"Incident '{incidentId}' not found");
            }

            incident.Status = IncidentStatus.Resolved;
            incident.ResolvedAt = DateTimeOffset.UtcNow;
            incident.Resolution = resolution;

            await _auditTrail.RecordAsync(ObsAuditEntry.Create(
                actor: "IncidentResponseEngine",
                action: "ResolveIncident",
                resourceType: "Incident",
                resourceId: incidentId,
                outcome: AuditOutcome.Success,
                detail: resolution), ct);

            _logger.LogInformation("Incident {IncidentId} resolved: {Resolution}", incidentId, resolution);
        }

        /// <summary>
        /// Registers an auto-response rule that triggers a playbook when event patterns match.
        /// </summary>
        public void RegisterAutoResponseRule(AutoResponseRule rule)
        {
            ArgumentNullException.ThrowIfNull(rule);
            if (!_autoResponseRules.TryAdd(rule.Name, rule))
            {
                throw new InvalidOperationException($"Auto-response rule '{rule.Name}' is already registered.");
            }

            _logger.LogInformation("Registered auto-response rule: {Name} (pattern: {Pattern}, threshold: {Threshold}/{Window}s)",
                rule.Name, rule.EventPattern, rule.Threshold, rule.WindowSeconds);
        }

        /// <summary>
        /// Starts monitoring the message bus for auto-response rule triggers
        /// and critical security alerts.
        /// </summary>
        public void StartMonitoring()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(IncidentResponseEngine));

            // Subscribe to critical alerts for auto-incident creation
            var alertSub = _messageBus.SubscribePattern("security.alert.critical", async msg =>
            {
                var incident = CreateIncident(
                    IncidentSeverity.Critical,
                    $"Auto-created from critical alert: {msg.Type}",
                    new Dictionary<string, string>
                    {
                        ["triggerTopic"] = msg.Type,
                        ["triggerMessageId"] = msg.MessageId,
                        ["triggerSource"] = msg.Source ?? "unknown"
                    });

                _logger.LogWarning("Auto-created incident {IncidentId} from critical alert on topic '{Topic}'",
                    incident.Id, msg.Type);
            });
            _subscriptions.Add(alertSub);

            // Subscribe to all security events for auto-response rule evaluation
            var eventSub = _messageBus.SubscribePattern("security.*", msg =>
            {
                _ = EvaluateAutoResponseRulesAsync(msg);
                return Task.CompletedTask;
            });
            _subscriptions.Add(eventSub);

            var authSub = _messageBus.SubscribePattern("auth.*", msg =>
            {
                _ = EvaluateAutoResponseRulesAsync(msg);
                return Task.CompletedTask;
            });
            _subscriptions.Add(authSub);

            var accessSub = _messageBus.Subscribe("access.denied", msg =>
            {
                _ = EvaluateAutoResponseRulesAsync(msg);
                return Task.CompletedTask;
            });
            _subscriptions.Add(accessSub);

            _logger.LogInformation("Incident response monitoring started with {RuleCount} auto-response rules", _autoResponseRules.Count);
        }

        private async Task EvaluateAutoResponseRulesAsync(PluginMessage message)
        {
            foreach (var rule in _autoResponseRules.Values)
            {
                try
                {
                    if (!Regex.IsMatch(message.Type, rule.EventPattern, RegexOptions.None, TimeSpan.FromMilliseconds(100)))
                    {
                        continue;
                    }

                    // Track event in sliding window
                    var windowKey = $"{rule.Name}:{ExtractGroupingKey(message, rule)}";
                    var window = _eventWindows.GetOrAdd(windowKey, _ => new ConcurrentQueue<DateTimeOffset>());
                    window.Enqueue(DateTimeOffset.UtcNow);

                    // Remove expired entries from window
                    var cutoff = DateTimeOffset.UtcNow.AddSeconds(-rule.WindowSeconds);
                    while (window.TryPeek(out var oldest) && oldest < cutoff)
                    {
                        window.TryDequeue(out _);
                    }

                    // Check if threshold exceeded
                    if (window.Count >= rule.Threshold)
                    {
                        // Clear the window to prevent repeated triggering
                        while (window.TryDequeue(out _)) { }

                        _logger.LogWarning("Auto-response rule '{RuleName}' triggered: {Count} events matching '{Pattern}' in {Window}s window",
                            rule.Name, rule.Threshold, rule.EventPattern, rule.WindowSeconds);

                        var metadata = new Dictionary<string, string>
                        {
                            ["autoResponseRule"] = rule.Name,
                            ["triggerPattern"] = rule.EventPattern,
                            ["triggerTopic"] = message.Type,
                            ["triggerCount"] = rule.Threshold.ToString()
                        };

                        // Extract targeting info from message payload
                        if (message.Payload is Dictionary<string, object> payload)
                        {
                            if (payload.TryGetValue("ipAddress", out var ip))
                                metadata["targetIpAddress"] = ip?.ToString() ?? string.Empty;
                            if (payload.TryGetValue("userId", out var userId))
                                metadata["targetUserId"] = userId?.ToString() ?? string.Empty;
                            if (payload.TryGetValue("nodeId", out var nodeId))
                                metadata["targetNodeId"] = nodeId?.ToString() ?? string.Empty;
                        }

                        var incident = CreateIncident(
                            rule.ResponseSeverity,
                            $"Auto-response: {rule.Name} triggered ({rule.Threshold} events in {rule.WindowSeconds}s)",
                            metadata);

                        await ExecutePlaybook(incident, rule.PlaybookActionIds);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error evaluating auto-response rule '{RuleName}'", rule.Name);
                }
            }
        }

        private static string ExtractGroupingKey(PluginMessage message, AutoResponseRule rule)
        {
            // Group events by source IP or user ID for targeted responses
            if (message.Payload is Dictionary<string, object> payload)
            {
                if (payload.TryGetValue("ipAddress", out var ip) && ip != null)
                    return $"ip:{ip}";
                if (payload.TryGetValue("userId", out var userId) && userId != null)
                    return $"user:{userId}";
            }
            return "global";
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var sub in _subscriptions)
            {
                sub.Dispose();
            }
            _subscriptions.Clear();
        }
    }

    /// <summary>
    /// A security incident tracked by the incident response engine.
    /// </summary>
    public sealed class Incident
    {
        /// <summary>Unique incident identifier.</summary>
        public Guid Id { get; init; }

        /// <summary>When the incident was created.</summary>
        public DateTimeOffset CreatedAt { get; init; }

        /// <summary>Severity of the incident.</summary>
        public IncidentSeverity Severity { get; init; }

        /// <summary>Description of the incident.</summary>
        public required string Description { get; init; }

        /// <summary>Current status of the incident.</summary>
        public IncidentStatus Status { get; set; }

        /// <summary>When the incident was resolved (null if still open).</summary>
        public DateTimeOffset? ResolvedAt { get; set; }

        /// <summary>Resolution description.</summary>
        public string? Resolution { get; set; }

        /// <summary>Results of containment actions executed for this incident.</summary>
        public List<ContainmentResult> Actions { get; init; } = new();

        /// <summary>Incident metadata including targeting info and trigger context.</summary>
        public Dictionary<string, string> Metadata { get; init; } = new();
    }

    /// <summary>
    /// Status of a security incident.
    /// </summary>
    public enum IncidentStatus
    {
        /// <summary>Incident is open and awaiting response.</summary>
        Open,

        /// <summary>Containment actions have been executed successfully.</summary>
        Contained,

        /// <summary>Incident has been resolved and closed.</summary>
        Resolved
    }

    /// <summary>
    /// Result of executing a containment playbook against an incident.
    /// </summary>
    public sealed record PlaybookResult
    {
        /// <summary>The incident the playbook was executed against.</summary>
        public required Incident Incident { get; init; }

        /// <summary>Whether all actions in the playbook succeeded.</summary>
        public required bool AllSucceeded { get; init; }

        /// <summary>Individual results from each containment action.</summary>
        public required IReadOnlyList<ContainmentResult> Results { get; init; }
    }

    /// <summary>
    /// Rule for automatic incident response triggered by event patterns.
    /// When the threshold of matching events is reached within the time window,
    /// the specified playbook actions are automatically executed.
    /// </summary>
    public sealed record AutoResponseRule
    {
        /// <summary>Unique name for this rule.</summary>
        public required string Name { get; init; }

        /// <summary>Regex pattern to match against message bus topics.</summary>
        public required string EventPattern { get; init; }

        /// <summary>Number of matching events required to trigger the rule.</summary>
        public required int Threshold { get; init; }

        /// <summary>Time window (seconds) in which the threshold must be reached.</summary>
        public required int WindowSeconds { get; init; }

        /// <summary>Action IDs to execute when the rule triggers.</summary>
        public required IReadOnlyList<string> PlaybookActionIds { get; init; }

        /// <summary>Severity to assign to auto-created incidents. Default: High.</summary>
        public IncidentSeverity ResponseSeverity { get; init; } = IncidentSeverity.High;
    }
}
