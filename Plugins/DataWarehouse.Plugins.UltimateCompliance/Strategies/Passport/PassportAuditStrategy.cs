using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Compliance;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Passport;

// ==================================================================================
// Passport Audit Strategy: Immutable audit trail for all passport and sovereignty
// operations. Captures issuance, verification, renewal, revocation, zone enforcement,
// cross-border transfers, agreements, and ZK proof events. Bounded storage with
// report generation.
// ==================================================================================

/// <summary>
/// Provides an immutable audit trail for all compliance passport operations and
/// sovereignty mesh decisions.
/// <para>
/// Tracks 14 distinct event types covering the full passport lifecycle, zone enforcement,
/// cross-border transfers, agreement management, and zero-knowledge proof operations.
/// Events are stored per-passport and in a global time-ordered queue bounded at 100,000
/// entries. Once logged, events cannot be modified or deleted (append-only).
/// </para>
/// </summary>
public sealed class PassportAuditStrategy : ComplianceStrategyBase
{
    // ==================================================================================
    // Audit event type constants
    // ==================================================================================

    /// <summary>Passport was issued for a data object.</summary>
    public const string PassportIssued = "PassportIssued";

    /// <summary>Passport was verified against compliance requirements.</summary>
    public const string PassportVerified = "PassportVerified";

    /// <summary>Passport was renewed with updated compliance data.</summary>
    public const string PassportRenewed = "PassportRenewed";

    /// <summary>Passport was permanently revoked.</summary>
    public const string PassportRevoked = "PassportRevoked";

    /// <summary>Passport was temporarily suspended.</summary>
    public const string PassportSuspended = "PassportSuspended";

    /// <summary>Passport was reinstated after suspension.</summary>
    public const string PassportReinstated = "PassportReinstated";

    /// <summary>Passport expired without renewal.</summary>
    public const string PassportExpired = "PassportExpired";

    /// <summary>Zone enforcement decision was made for a data operation.</summary>
    public const string ZoneEnforcementDecision = "ZoneEnforcementDecision";

    /// <summary>Cross-border transfer was requested.</summary>
    public const string CrossBorderTransferRequested = "CrossBorderTransferRequested";

    /// <summary>Cross-border transfer was approved.</summary>
    public const string CrossBorderTransferApproved = "CrossBorderTransferApproved";

    /// <summary>Cross-border transfer was denied.</summary>
    public const string CrossBorderTransferDenied = "CrossBorderTransferDenied";

    /// <summary>Data transfer agreement was created between jurisdictions.</summary>
    public const string AgreementCreated = "AgreementCreated";

    /// <summary>Data transfer agreement expired.</summary>
    public const string AgreementExpired = "AgreementExpired";

    /// <summary>Data transfer agreement was revoked.</summary>
    public const string AgreementRevoked = "AgreementRevoked";

    /// <summary>Zero-knowledge proof was generated for privacy-preserving verification.</summary>
    public const string ZkProofGenerated = "ZkProofGenerated";

    /// <summary>Zero-knowledge proof was verified.</summary>
    public const string ZkProofVerified = "ZkProofVerified";

    private static readonly HashSet<string> ValidEventTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        PassportIssued, PassportVerified, PassportRenewed, PassportRevoked,
        PassportSuspended, PassportReinstated, PassportExpired,
        ZoneEnforcementDecision,
        CrossBorderTransferRequested, CrossBorderTransferApproved, CrossBorderTransferDenied,
        AgreementCreated, AgreementExpired, AgreementRevoked,
        ZkProofGenerated, ZkProofVerified
    };

    // ==================================================================================
    // Storage: per-passport trail + global bounded queue
    // ==================================================================================

    private const int MaxGlobalQueueSize = 100_000;

    private readonly ConcurrentDictionary<string, List<PassportAuditEvent>> _passportTrails = new();
    private readonly ConcurrentQueue<PassportAuditEvent> _globalQueue = new();
    private long _globalQueueCount;

    // ==================================================================================
    // Strategy identity
    // ==================================================================================

    /// <inheritdoc/>
    public override string StrategyId => "passport-audit";

    /// <inheritdoc/>
    public override string StrategyName => "Compliance Passport Audit Trail";

    /// <inheritdoc/>
    public override string Framework => "CompliancePassport";

    // ==================================================================================
    // Core audit operations
    // ==================================================================================

    /// <summary>
    /// Logs an immutable audit event. Once logged, the event cannot be modified or removed.
    /// </summary>
    /// <param name="evt">The audit event to log.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="evt"/> is null.</exception>
    /// <exception cref="ArgumentException">If required fields are missing or event type is invalid.</exception>
    public Task LogEventAsync(PassportAuditEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ct.ThrowIfCancellationRequested();

        ValidateEvent(evt);

        // Add to passport-specific trail (thread-safe via lock on the list)
        var trail = _passportTrails.GetOrAdd(evt.PassportId, _ => new List<PassportAuditEvent>());
        lock (trail)
        {
            trail.Add(evt);
        }

        // Add to global queue with bounded eviction
        _globalQueue.Enqueue(evt);
        var count = Interlocked.Increment(ref _globalQueueCount);
        while (count > MaxGlobalQueueSize && _globalQueue.TryDequeue(out _))
        {
            count = Interlocked.Decrement(ref _globalQueueCount);
        }

        IncrementCounter("passport_audit.event_logged");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns all audit events for a specific passport, ordered by timestamp.
    /// </summary>
    /// <param name="passportId">The passport identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Ordered list of audit events for the passport.</returns>
    public Task<IReadOnlyList<PassportAuditEvent>> GetPassportAuditTrailAsync(
        string passportId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passportId);
        ct.ThrowIfCancellationRequested();

        if (_passportTrails.TryGetValue(passportId, out var trail))
        {
            List<PassportAuditEvent> snapshot;
            lock (trail)
            {
                snapshot = trail.OrderBy(e => e.Timestamp).ToList();
            }
            return Task.FromResult<IReadOnlyList<PassportAuditEvent>>(snapshot);
        }

        return Task.FromResult<IReadOnlyList<PassportAuditEvent>>(Array.Empty<PassportAuditEvent>());
    }

    /// <summary>
    /// Returns audit events within a time range from the global queue.
    /// </summary>
    /// <param name="start">Start of the time range (inclusive).</param>
    /// <param name="end">End of the time range (inclusive).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Events within the specified time range.</returns>
    public Task<IReadOnlyList<PassportAuditEvent>> GetAuditTrailByTimeRangeAsync(
        DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var results = _globalQueue
            .Where(e => e.Timestamp >= start && e.Timestamp <= end)
            .OrderBy(e => e.Timestamp)
            .ToList();

        return Task.FromResult<IReadOnlyList<PassportAuditEvent>>(results);
    }

    /// <summary>
    /// Returns audit events filtered by event type with an optional limit.
    /// </summary>
    /// <param name="eventType">The event type to filter by.</param>
    /// <param name="limit">Maximum number of events to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Events matching the specified type.</returns>
    public Task<IReadOnlyList<PassportAuditEvent>> GetAuditTrailByTypeAsync(
        string eventType, int limit = 100, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ct.ThrowIfCancellationRequested();

        var results = _globalQueue
            .Where(e => string.Equals(e.EventType, eventType, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Timestamp)
            .Take(limit)
            .ToList();

        return Task.FromResult<IReadOnlyList<PassportAuditEvent>>(results);
    }

    /// <summary>
    /// Generates an aggregate audit report for a time range.
    /// </summary>
    /// <param name="start">Start of the reporting period.</param>
    /// <param name="end">End of the reporting period.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An aggregated audit report.</returns>
    public Task<PassportAuditReport> GenerateAuditReportAsync(
        DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var events = _globalQueue
            .Where(e => e.Timestamp >= start && e.Timestamp <= end)
            .ToList();

        var eventsByType = events
            .GroupBy(e => e.EventType)
            .ToDictionary(g => g.Key, g => g.Count());

        var enforcementDecisions = events
            .Where(e => !string.IsNullOrEmpty(e.Decision))
            .GroupBy(e => e.Decision!)
            .ToDictionary(g => g.Key, g => g.Count());

        var topObjects = events
            .GroupBy(e => e.ObjectId)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .ToDictionary(g => g.Key, g => g.Count());

        var report = new PassportAuditReport
        {
            ReportId = Guid.NewGuid().ToString("N"),
            StartDate = start,
            EndDate = end,
            TotalEvents = events.Count,
            EventsByType = eventsByType,
            PassportsIssued = eventsByType.GetValueOrDefault(PassportIssued),
            PassportsRevoked = eventsByType.GetValueOrDefault(PassportRevoked),
            PassportsExpired = eventsByType.GetValueOrDefault(PassportExpired),
            TransfersApproved = eventsByType.GetValueOrDefault(CrossBorderTransferApproved),
            TransfersDenied = eventsByType.GetValueOrDefault(CrossBorderTransferDenied),
            EnforcementDecisions = enforcementDecisions,
            TopObjectsByEvents = topObjects
        };

        IncrementCounter("passport_audit.report_generated");

        return Task.FromResult(report);
    }

    // ==================================================================================
    // Convenience logging methods
    // ==================================================================================

    /// <summary>
    /// Logs a passport issuance event.
    /// </summary>
    public Task LogPassportIssuedAsync(CompliancePassport passport, string actorId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(passport);

        return LogEventAsync(new PassportAuditEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            EventType = PassportIssued,
            PassportId = passport.PassportId,
            ObjectId = passport.ObjectId,
            ActorId = actorId,
            Timestamp = DateTimeOffset.UtcNow,
            Details = new Dictionary<string, object>
            {
                ["scope"] = passport.Scope.ToString(),
                ["status"] = passport.Status.ToString(),
                ["entryCount"] = passport.Entries.Count,
                ["expiresAt"] = passport.ExpiresAt.ToString("O")
            }
        }, ct);
    }

    /// <summary>
    /// Logs a passport verification event.
    /// </summary>
    public Task LogPassportVerifiedAsync(
        string passportId, string objectId, bool isValid, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passportId);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        return LogEventAsync(new PassportAuditEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            EventType = PassportVerified,
            PassportId = passportId,
            ObjectId = objectId,
            Timestamp = DateTimeOffset.UtcNow,
            Details = new Dictionary<string, object>
            {
                ["isValid"] = isValid
            }
        }, ct);
    }

    /// <summary>
    /// Logs a zone enforcement decision event.
    /// </summary>
    public Task LogEnforcementDecisionAsync(
        string passportId, string objectId,
        string sourceZone, string destZone,
        ZoneAction decision, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passportId);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        return LogEventAsync(new PassportAuditEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            EventType = ZoneEnforcementDecision,
            PassportId = passportId,
            ObjectId = objectId,
            Timestamp = DateTimeOffset.UtcNow,
            SourceJurisdiction = sourceZone,
            DestinationJurisdiction = destZone,
            Decision = decision.ToString(),
            Details = new Dictionary<string, object>
            {
                ["sourceZone"] = sourceZone,
                ["destZone"] = destZone,
                ["action"] = decision.ToString()
            }
        }, ct);
    }

    /// <summary>
    /// Logs a cross-border transfer decision event.
    /// </summary>
    public Task LogCrossBorderTransferAsync(
        string passportId, string objectId,
        string sourceJurisdiction, string destJurisdiction,
        TransferDecision decision, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passportId);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        var eventType = decision switch
        {
            TransferDecision.Approved or TransferDecision.ConditionalApproval => CrossBorderTransferApproved,
            TransferDecision.Denied => CrossBorderTransferDenied,
            _ => CrossBorderTransferRequested
        };

        return LogEventAsync(new PassportAuditEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            EventType = eventType,
            PassportId = passportId,
            ObjectId = objectId,
            Timestamp = DateTimeOffset.UtcNow,
            SourceJurisdiction = sourceJurisdiction,
            DestinationJurisdiction = destJurisdiction,
            Decision = decision.ToString(),
            Details = new Dictionary<string, object>
            {
                ["sourceJurisdiction"] = sourceJurisdiction,
                ["destJurisdiction"] = destJurisdiction,
                ["transferDecision"] = decision.ToString()
            }
        }, ct);
    }

    // ==================================================================================
    // Compliance check: verifies audit trail exists for the object's passport
    // ==================================================================================

    /// <inheritdoc/>
    protected override Task<ComplianceResult> CheckComplianceCoreAsync(
        ComplianceContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IncrementCounter("passport_audit.compliance_check");

        var resourceId = context.ResourceId ?? string.Empty;
        var hasTrail = !string.IsNullOrEmpty(resourceId) &&
                       _passportTrails.TryGetValue(resourceId, out var trail) &&
                       trail.Count > 0;

        if (hasTrail)
        {
            return Task.FromResult(new ComplianceResult
            {
                IsCompliant = true,
                Framework = Framework,
                Status = ComplianceStatus.Compliant,
                Recommendations = Array.Empty<string>(),
                Metadata = new Dictionary<string, object>
                {
                    ["auditTrailExists"] = true,
                    ["passportId"] = resourceId
                }
            });
        }

        return Task.FromResult(new ComplianceResult
        {
            IsCompliant = false,
            Framework = Framework,
            Status = ComplianceStatus.NonCompliant,
            Violations = new[]
            {
                new ComplianceViolation
                {
                    Code = "AUDIT-001",
                    Description = $"No audit trail found for passport '{resourceId}'",
                    Severity = ViolationSeverity.High,
                    AffectedResource = resourceId,
                    Remediation = "Ensure all passport operations are logged via the audit strategy"
                }
            },
            Recommendations = new[] { "Enable audit logging for all passport operations" },
            Metadata = new Dictionary<string, object>
            {
                ["auditTrailExists"] = false,
                ["passportId"] = resourceId
            }
        });
    }

    // ==================================================================================
    // Validation
    // ==================================================================================

    private static void ValidateEvent(PassportAuditEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.EventId))
            throw new ArgumentException("EventId is required.", nameof(evt));
        if (string.IsNullOrWhiteSpace(evt.EventType))
            throw new ArgumentException("EventType is required.", nameof(evt));
        if (string.IsNullOrWhiteSpace(evt.PassportId))
            throw new ArgumentException("PassportId is required.", nameof(evt));
        if (string.IsNullOrWhiteSpace(evt.ObjectId))
            throw new ArgumentException("ObjectId is required.", nameof(evt));
        if (!ValidEventTypes.Contains(evt.EventType))
            throw new ArgumentException($"Unknown event type: '{evt.EventType}'. Must be one of: {string.Join(", ", ValidEventTypes)}", nameof(evt));
    }
}

// ==================================================================================
// Audit event and report records
// ==================================================================================

/// <summary>
/// Immutable audit event for a passport or sovereignty operation.
/// Once created, instances should not be modified (sealed record enforces this).
/// </summary>
public sealed record PassportAuditEvent
{
    /// <summary>Unique identifier for this event (GUID).</summary>
    public required string EventId { get; init; }

    /// <summary>Type of audit event (one of the PassportAuditStrategy constants).</summary>
    public required string EventType { get; init; }

    /// <summary>Identifier of the passport this event relates to.</summary>
    public required string PassportId { get; init; }

    /// <summary>Identifier of the data object involved.</summary>
    public required string ObjectId { get; init; }

    /// <summary>Identifier of the actor who triggered the event, if applicable.</summary>
    public string? ActorId { get; init; }

    /// <summary>Timestamp when the event occurred.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Event-specific details as key-value pairs.</summary>
    public IReadOnlyDictionary<string, object>? Details { get; init; }

    /// <summary>Source jurisdiction for cross-border or zone events.</summary>
    public string? SourceJurisdiction { get; init; }

    /// <summary>Destination jurisdiction for cross-border or zone events.</summary>
    public string? DestinationJurisdiction { get; init; }

    /// <summary>Decision outcome for enforcement or transfer events.</summary>
    public string? Decision { get; init; }

    /// <summary>Reason for the action, if applicable.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Aggregated audit report for a time period covering passport and sovereignty operations.
/// </summary>
public sealed record PassportAuditReport
{
    /// <summary>Unique report identifier.</summary>
    public required string ReportId { get; init; }

    /// <summary>Start of the reporting period.</summary>
    public required DateTimeOffset StartDate { get; init; }

    /// <summary>End of the reporting period.</summary>
    public required DateTimeOffset EndDate { get; init; }

    /// <summary>Total number of events in the reporting period.</summary>
    public int TotalEvents { get; init; }

    /// <summary>Event counts grouped by event type.</summary>
    public IReadOnlyDictionary<string, int> EventsByType { get; init; } = new Dictionary<string, int>();

    /// <summary>Number of passports issued during the period.</summary>
    public int PassportsIssued { get; init; }

    /// <summary>Number of passports revoked during the period.</summary>
    public int PassportsRevoked { get; init; }

    /// <summary>Number of passports expired during the period.</summary>
    public int PassportsExpired { get; init; }

    /// <summary>Number of cross-border transfers approved.</summary>
    public int TransfersApproved { get; init; }

    /// <summary>Number of cross-border transfers denied.</summary>
    public int TransfersDenied { get; init; }

    /// <summary>Enforcement decisions grouped by decision type.</summary>
    public IReadOnlyDictionary<string, int> EnforcementDecisions { get; init; } = new Dictionary<string, int>();

    /// <summary>Top 10 objects by event count.</summary>
    public IReadOnlyDictionary<string, int> TopObjectsByEvents { get; init; } = new Dictionary<string, int>();
}
