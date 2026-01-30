using System.Collections.Concurrent;
using DataWarehouse.SDK.Compliance;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Plugins.ComplianceAutomation;

/// <summary>
/// In-memory compliance audit trail implementation.
/// Provides immutable audit logging for regulatory evidence and compliance reporting.
/// </summary>
public class InMemoryComplianceAuditPlugin : ComplianceAuditPluginBase
{
    private readonly ConcurrentBag<ComplianceAuditEvent> _events = new();
    private readonly object _reportLock = new();

    /// <inheritdoc />
    public override string Id => "datawarehouse.compliance.audit.inmemory";

    /// <inheritdoc />
    public override string Name => "In-Memory Compliance Audit";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.GovernanceProvider;

    /// <inheritdoc />
    protected override Task StoreEventAsync(ComplianceAuditEvent evt)
    {
        // Audit events are immutable once stored
        _events.Add(evt);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override Task<IReadOnlyList<ComplianceAuditEvent>> QueryEventsAsync(AuditQuery query)
    {
        var results = _events.AsEnumerable();

        // Apply time range filters
        if (query.Start.HasValue)
        {
            results = results.Where(e => e.Timestamp >= query.Start.Value);
        }

        if (query.End.HasValue)
        {
            results = results.Where(e => e.Timestamp <= query.End.Value);
        }

        // Apply actor filter
        if (!string.IsNullOrEmpty(query.Actor))
        {
            results = results.Where(e => e.Actor.Equals(query.Actor, StringComparison.OrdinalIgnoreCase));
        }

        // Apply resource filter
        if (!string.IsNullOrEmpty(query.Resource))
        {
            results = results.Where(e => e.Resource.Contains(query.Resource, StringComparison.OrdinalIgnoreCase));
        }

        // Apply framework filter
        if (query.Framework.HasValue)
        {
            results = results.Where(e => e.Framework == query.Framework.Value);
        }

        // Order by timestamp descending and apply limit
        var list = results
            .OrderByDescending(e => e.Timestamp)
            .Take(query.Limit)
            .ToList();

        return Task.FromResult<IReadOnlyList<ComplianceAuditEvent>>(list);
    }

    /// <inheritdoc />
    protected override Task<AuditReport> GenerateReportAsync(
        ComplianceFramework framework,
        DateTimeOffset start,
        DateTimeOffset end)
    {
        lock (_reportLock)
        {
            var events = _events
                .Where(e => e.Timestamp >= start && e.Timestamp <= end)
                .Where(e => e.Framework == framework || e.Framework == null)
                .ToList();

            var summary = events
                .GroupBy(e => e.EventType)
                .Select(g => new AuditSummaryEntry(
                    g.Key,
                    g.Count(),
                    g.Select(e => e.Actor).Distinct().ToArray()
                ))
                .OrderByDescending(s => s.Count)
                .ToList();

            return Task.FromResult(new AuditReport(
                $"RPT-{Guid.NewGuid():N}",
                framework,
                start,
                end,
                events.Count,
                summary
            ));
        }
    }

    /// <summary>
    /// Logs an access event to the audit trail.
    /// </summary>
    /// <param name="actor">User or system performing the access.</param>
    /// <param name="resource">Resource being accessed.</param>
    /// <param name="action">Access action (read, write, etc.).</param>
    /// <param name="framework">Related compliance framework.</param>
    /// <param name="details">Additional event details.</param>
    public Task LogAccessAsync(
        string actor,
        string resource,
        string action,
        ComplianceFramework? framework = null,
        Dictionary<string, object>? details = null)
    {
        return LogEventAsync(new ComplianceAuditEvent(
            $"EVT-{Guid.NewGuid():N}",
            "Access",
            actor,
            resource,
            action,
            framework,
            DateTimeOffset.UtcNow,
            details
        ));
    }

    /// <summary>
    /// Logs a data modification event.
    /// </summary>
    /// <param name="actor">User or system performing the modification.</param>
    /// <param name="resource">Resource being modified.</param>
    /// <param name="changeType">Type of change (create, update, delete).</param>
    /// <param name="framework">Related compliance framework.</param>
    /// <param name="details">Additional event details.</param>
    public Task LogModificationAsync(
        string actor,
        string resource,
        string changeType,
        ComplianceFramework? framework = null,
        Dictionary<string, object>? details = null)
    {
        return LogEventAsync(new ComplianceAuditEvent(
            $"EVT-{Guid.NewGuid():N}",
            "Modification",
            actor,
            resource,
            changeType,
            framework,
            DateTimeOffset.UtcNow,
            details
        ));
    }

    /// <summary>
    /// Logs a security event.
    /// </summary>
    /// <param name="actor">User or system involved.</param>
    /// <param name="resource">Affected resource.</param>
    /// <param name="securityAction">Security action (login, logout, auth failure, etc.).</param>
    /// <param name="details">Additional event details.</param>
    public Task LogSecurityEventAsync(
        string actor,
        string resource,
        string securityAction,
        Dictionary<string, object>? details = null)
    {
        return LogEventAsync(new ComplianceAuditEvent(
            $"EVT-{Guid.NewGuid():N}",
            "Security",
            actor,
            resource,
            securityAction,
            null,
            DateTimeOffset.UtcNow,
            details
        ));
    }

    /// <summary>
    /// Logs a compliance-specific event.
    /// </summary>
    /// <param name="actor">User or system performing the action.</param>
    /// <param name="resource">Affected resource.</param>
    /// <param name="action">Compliance action performed.</param>
    /// <param name="framework">Compliance framework.</param>
    /// <param name="details">Additional event details.</param>
    public Task LogComplianceActionAsync(
        string actor,
        string resource,
        string action,
        ComplianceFramework framework,
        Dictionary<string, object>? details = null)
    {
        return LogEventAsync(new ComplianceAuditEvent(
            $"EVT-{Guid.NewGuid():N}",
            "Compliance",
            actor,
            resource,
            action,
            framework,
            DateTimeOffset.UtcNow,
            details
        ));
    }

    /// <summary>
    /// Gets the total count of audit events.
    /// </summary>
    public int EventCount => _events.Count;

    /// <summary>
    /// Gets events by event type.
    /// </summary>
    /// <param name="eventType">Event type to filter by.</param>
    /// <returns>Events matching the type.</returns>
    public IReadOnlyList<ComplianceAuditEvent> GetEventsByType(string eventType)
    {
        return _events
            .Where(e => e.EventType.Equals(eventType, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Timestamp)
            .ToList();
    }

    /// <summary>
    /// Gets events by actor.
    /// </summary>
    /// <param name="actor">Actor to filter by.</param>
    /// <returns>Events for the actor.</returns>
    public IReadOnlyList<ComplianceAuditEvent> GetEventsByActor(string actor)
    {
        return _events
            .Where(e => e.Actor.Equals(actor, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Timestamp)
            .ToList();
    }

    /// <summary>
    /// Gets recent events.
    /// </summary>
    /// <param name="count">Number of events to return.</param>
    /// <returns>Most recent events.</returns>
    public IReadOnlyList<ComplianceAuditEvent> GetRecentEvents(int count = 100)
    {
        return _events
            .OrderByDescending(e => e.Timestamp)
            .Take(count)
            .ToList();
    }

    /// <summary>
    /// Gets event statistics by framework.
    /// </summary>
    /// <returns>Dictionary of framework name to event count.</returns>
    public Dictionary<string, int> GetEventsByFramework()
    {
        return _events
            .GroupBy(e => e.Framework?.ToString() ?? "Unspecified")
            .ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>
    /// Clears all audit events (use only for testing).
    /// </summary>
    public void ClearEvents()
    {
        _events.Clear();
    }

    /// <summary>
    /// Exports all events for archival.
    /// </summary>
    /// <returns>All audit events ordered by timestamp.</returns>
    public IReadOnlyList<ComplianceAuditEvent> ExportAllEvents()
    {
        return _events.OrderBy(e => e.Timestamp).ToList();
    }
}
