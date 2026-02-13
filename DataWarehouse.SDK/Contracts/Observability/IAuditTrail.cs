using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Observability
{
    /// <summary>
    /// Contract for an immutable, append-only audit trail (OBS-05).
    /// Records security-sensitive operations for compliance and forensics.
    /// Entries are immutable once recorded and cannot be modified or deleted.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Immutable audit trail")]
    public interface IAuditTrail
    {
        /// <summary>
        /// Records an audit entry. The entry is immutable once recorded.
        /// </summary>
        /// <param name="entry">The audit entry to record.</param>
        /// <param name="ct">Cancellation token for the record operation.</param>
        /// <returns>A task representing the record operation.</returns>
        Task RecordAsync(AuditEntry entry, CancellationToken ct = default);

        /// <summary>
        /// Queries audit entries matching the specified criteria.
        /// </summary>
        /// <param name="query">The query criteria.</param>
        /// <param name="ct">Cancellation token for the query operation.</param>
        /// <returns>A read-only list of matching audit entries.</returns>
        Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery query, CancellationToken ct = default);

        /// <summary>
        /// Gets the count of audit entries matching the specified criteria.
        /// If no query is provided, returns the total count.
        /// </summary>
        /// <param name="query">Optional query criteria.</param>
        /// <param name="ct">Cancellation token for the count operation.</param>
        /// <returns>The number of matching entries.</returns>
        Task<long> GetCountAsync(AuditQuery? query = null, CancellationToken ct = default);

        /// <summary>
        /// Raised when an audit entry is recorded.
        /// </summary>
        event Action<AuditEntry>? OnAuditRecorded;
    }

    /// <summary>
    /// An immutable audit trail entry.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Immutable audit trail")]
    public record AuditEntry
    {
        /// <summary>
        /// Unique identifier for this entry.
        /// </summary>
        public required string EntryId { get; init; }

        /// <summary>
        /// When this action occurred.
        /// </summary>
        public required DateTimeOffset Timestamp { get; init; }

        /// <summary>
        /// The actor who performed the action (user, plugin, or system).
        /// </summary>
        public required string Actor { get; init; }

        /// <summary>
        /// The action that was performed (e.g., "read", "write", "delete", "login").
        /// </summary>
        public required string Action { get; init; }

        /// <summary>
        /// The type of resource that was acted upon.
        /// </summary>
        public required string ResourceType { get; init; }

        /// <summary>
        /// The identifier of the resource that was acted upon.
        /// </summary>
        public required string ResourceId { get; init; }

        /// <summary>
        /// The outcome of the action.
        /// </summary>
        public required AuditOutcome Outcome { get; init; }

        /// <summary>
        /// Optional detail about the action.
        /// </summary>
        public string? Detail { get; init; }

        /// <summary>
        /// Optional correlation ID for distributed tracing.
        /// </summary>
        public string? CorrelationId { get; init; }

        /// <summary>
        /// Additional metadata about the audit entry.
        /// </summary>
        public IReadOnlyDictionary<string, string>? Metadata { get; init; }

        /// <summary>
        /// Creates a new audit entry with auto-generated ID and timestamp.
        /// </summary>
        /// <param name="actor">The actor performing the action.</param>
        /// <param name="action">The action being performed.</param>
        /// <param name="resourceType">The resource type.</param>
        /// <param name="resourceId">The resource identifier.</param>
        /// <param name="outcome">The outcome of the action.</param>
        /// <param name="detail">Optional detail.</param>
        /// <returns>A new audit entry.</returns>
        public static AuditEntry Create(
            string actor,
            string action,
            string resourceType,
            string resourceId,
            AuditOutcome outcome,
            string? detail = null) =>
            new()
            {
                EntryId = Guid.NewGuid().ToString("N"),
                Timestamp = DateTimeOffset.UtcNow,
                Actor = actor,
                Action = action,
                ResourceType = resourceType,
                ResourceId = resourceId,
                Outcome = outcome,
                Detail = detail
            };
    }

    /// <summary>
    /// Outcomes of an audited action.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Immutable audit trail")]
    public enum AuditOutcome
    {
        /// <summary>The action succeeded.</summary>
        Success,
        /// <summary>The action failed.</summary>
        Failure,
        /// <summary>The action was denied by policy.</summary>
        Denied
    }

    /// <summary>
    /// Query criteria for audit trail entries.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Immutable audit trail")]
    public record AuditQuery
    {
        /// <summary>
        /// Filter by actor.
        /// </summary>
        public string? Actor { get; init; }

        /// <summary>
        /// Filter by action.
        /// </summary>
        public string? Action { get; init; }

        /// <summary>
        /// Filter by resource type.
        /// </summary>
        public string? ResourceType { get; init; }

        /// <summary>
        /// Filter by resource ID.
        /// </summary>
        public string? ResourceId { get; init; }

        /// <summary>
        /// Filter by entries after this time.
        /// </summary>
        public DateTimeOffset? From { get; init; }

        /// <summary>
        /// Filter by entries before this time.
        /// </summary>
        public DateTimeOffset? To { get; init; }

        /// <summary>
        /// Maximum number of results to return. Default: 100.
        /// </summary>
        public int MaxResults { get; init; } = 100;

        /// <summary>
        /// Filter by outcome.
        /// </summary>
        public AuditOutcome? Outcome { get; init; }
    }
}
