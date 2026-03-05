using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Compliance;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Passport;

/// <summary>
/// Manages the full lifecycle of <see cref="CompliancePassport"/> instances including
/// registration, revocation, suspension, reinstatement, and expiration scanning.
/// <para>
/// Every status transition is recorded in an immutable audit trail, providing full
/// traceability for compliance investigations and regulatory audits.
/// </para>
/// </summary>
public sealed class PassportLifecycleStrategy : ComplianceStrategyBase
{
    private readonly BoundedDictionary<string, PassportRegistryEntry> _registry = new BoundedDictionary<string, PassportRegistryEntry>(1000);

    /// <inheritdoc/>
    public override string StrategyId => "passport-lifecycle";

    /// <inheritdoc/>
    public override string StrategyName => "Compliance Passport Lifecycle Management";

    /// <inheritdoc/>
    public override string Framework => "CompliancePassport";

    /// <summary>
    /// Registers a newly issued passport in the lifecycle registry.
    /// </summary>
    /// <param name="passport">The passport to register.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The registry entry created for the passport.</returns>
    public Task<PassportRegistryEntry> RegisterPassportAsync(CompliancePassport passport, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(passport);
        ct.ThrowIfCancellationRequested();
            IncrementCounter("passport_lifecycle.registered");

        var now = DateTimeOffset.UtcNow;
        var initialChange = new PassportStatusChange
        {
            OldStatus = null,
            NewStatus = passport.Status,
            Reason = "Initial passport registration",
            ChangedBy = passport.IssuerId,
            Timestamp = now
        };

        var entry = new PassportRegistryEntry
        {
            PassportId = passport.PassportId,
            ObjectId = passport.ObjectId,
            Status = passport.Status,
            IssuedAt = passport.IssuedAt,
            ExpiresAt = passport.ExpiresAt,
            RevocationReason = null,
            SuspensionReason = null,
            History = new List<PassportStatusChange> { initialChange }
        };

        _registry.AddOrUpdate(passport.PassportId, entry, (_, existing) =>
        {
            // Re-registration replaces the entry but preserves prior history
            var mergedHistory = new List<PassportStatusChange>(existing.History) { initialChange };
            return entry with { History = mergedHistory };
        });

        return Task.FromResult(entry);
    }

    /// <summary>
    /// Revokes a passport permanently. Revoked passports cannot be reinstated.
    /// </summary>
    /// <param name="passportId">The passport identifier to revoke.</param>
    /// <param name="reason">The reason for revocation.</param>
    /// <param name="revokedBy">The entity performing the revocation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated registry entry, or <c>null</c> if the passport was not found.</returns>
    public Task<PassportRegistryEntry?> RevokePassportAsync(
        string passportId,
        PassportRevocationReason reason,
        string revokedBy,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passportId);
        ArgumentException.ThrowIfNullOrWhiteSpace(revokedBy);
        ct.ThrowIfCancellationRequested();

        if (!_registry.TryGetValue(passportId, out var existing))
            return Task.FromResult<PassportRegistryEntry?>(null);

            IncrementCounter("passport_lifecycle.revoked");

        var change = new PassportStatusChange
        {
            OldStatus = existing.Status,
            NewStatus = PassportStatus.Revoked,
            Reason = $"Revoked: {reason}",
            ChangedBy = revokedBy,
            Timestamp = DateTimeOffset.UtcNow
        };

        var updated = existing with
        {
            Status = PassportStatus.Revoked,
            RevocationReason = reason,
            History = new List<PassportStatusChange>(existing.History) { change }
        };

        _registry.TryUpdate(passportId, updated, existing);
        return Task.FromResult<PassportRegistryEntry?>(updated);
    }

    /// <summary>
    /// Suspends a passport temporarily pending investigation or update.
    /// </summary>
    /// <param name="passportId">The passport identifier to suspend.</param>
    /// <param name="reason">The reason for suspension.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated registry entry, or <c>null</c> if the passport was not found.</returns>
    public Task<PassportRegistryEntry?> SuspendPassportAsync(
        string passportId,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passportId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ct.ThrowIfCancellationRequested();

        if (!_registry.TryGetValue(passportId, out var existing))
            return Task.FromResult<PassportRegistryEntry?>(null);

            IncrementCounter("passport_lifecycle.suspended");

        var change = new PassportStatusChange
        {
            OldStatus = existing.Status,
            NewStatus = PassportStatus.Suspended,
            Reason = $"Suspended: {reason}",
            ChangedBy = "lifecycle-manager",
            Timestamp = DateTimeOffset.UtcNow
        };

        var updated = existing with
        {
            Status = PassportStatus.Suspended,
            SuspensionReason = reason,
            History = new List<PassportStatusChange>(existing.History) { change }
        };

        _registry.TryUpdate(passportId, updated, existing);
        return Task.FromResult<PassportRegistryEntry?>(updated);
    }

    /// <summary>
    /// Reinstates a currently suspended passport back to Active status.
    /// Only passports with <see cref="PassportStatus.Suspended"/> status can be reinstated.
    /// </summary>
    /// <param name="passportId">The passport identifier to reinstate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated registry entry, or <c>null</c> if not found or not suspended.</returns>
    public Task<PassportRegistryEntry?> ReinstatePassportAsync(
        string passportId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passportId);
        ct.ThrowIfCancellationRequested();

        if (!_registry.TryGetValue(passportId, out var existing))
            return Task.FromResult<PassportRegistryEntry?>(null);

        if (existing.Status != PassportStatus.Suspended)
            return Task.FromResult<PassportRegistryEntry?>(null);

            IncrementCounter("passport_lifecycle.reinstated");

        var change = new PassportStatusChange
        {
            OldStatus = PassportStatus.Suspended,
            NewStatus = PassportStatus.Active,
            Reason = "Reinstated after suspension",
            ChangedBy = "lifecycle-manager",
            Timestamp = DateTimeOffset.UtcNow
        };

        var updated = existing with
        {
            Status = PassportStatus.Active,
            SuspensionReason = null,
            History = new List<PassportStatusChange>(existing.History) { change }
        };

        _registry.TryUpdate(passportId, updated, existing);
        return Task.FromResult<PassportRegistryEntry?>(updated);
    }

    /// <summary>
    /// Scans the registry for passports that have expired while still marked Active.
    /// Returns them for renewal processing.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of expired passport registry entries.</returns>
    public Task<IReadOnlyList<PassportRegistryEntry>> GetExpiredPassportsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
            IncrementCounter("passport_lifecycle.expiry_scan");

        var now = DateTimeOffset.UtcNow;
        var expired = _registry.Values
            .Where(e => e.Status == PassportStatus.Active && e.ExpiresAt < now)
            .ToList();

        return Task.FromResult<IReadOnlyList<PassportRegistryEntry>>(expired);
    }

    /// <summary>
    /// Returns the full status change history for a passport, providing
    /// a complete audit trail for regulatory investigations.
    /// </summary>
    /// <param name="passportId">The passport identifier to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of status changes, or empty if passport not found.</returns>
    public Task<IReadOnlyList<PassportStatusChange>> GetPassportHistoryAsync(
        string passportId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (_registry.TryGetValue(passportId, out var entry))
            return Task.FromResult<IReadOnlyList<PassportStatusChange>>(entry.History);

        return Task.FromResult<IReadOnlyList<PassportStatusChange>>(Array.Empty<PassportStatusChange>());
    }

    /// <summary>
    /// Retrieves a registry entry by passport identifier.
    /// </summary>
    /// <param name="passportId">The passport identifier to look up.</param>
    /// <returns>The registry entry, or <c>null</c> if not found.</returns>
    public PassportRegistryEntry? GetRegistryEntry(string passportId)
    {
        _registry.TryGetValue(passportId, out var entry);
        return entry;
    }

    /// <inheritdoc/>
    protected override Task<ComplianceResult> CheckComplianceCoreAsync(
        ComplianceContext context,
        CancellationToken cancellationToken)
    {
            IncrementCounter("passport_lifecycle.check");

        var objectId = context.ResourceId ?? string.Empty;

        // Find registry entry for this object
        var entry = _registry.Values.FirstOrDefault(
            e => string.Equals(e.ObjectId, objectId, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            return Task.FromResult(new ComplianceResult
            {
                IsCompliant = false,
                Framework = Framework,
                Status = ComplianceStatus.NonCompliant,
                Recommendations = new[] { "No passport registered for this object" }
            });
        }

        if (entry.Status == PassportStatus.Active && entry.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return Task.FromResult(new ComplianceResult
            {
                IsCompliant = true,
                Framework = Framework,
                Status = ComplianceStatus.Compliant,
                Metadata = new Dictionary<string, object>
                {
                    ["PassportId"] = entry.PassportId,
                    ["Status"] = entry.Status.ToString(),
                    ["ExpiresAt"] = entry.ExpiresAt.ToString("O")
                }
            });
        }

        var statusDescription = entry.Status switch
        {
            PassportStatus.Revoked => "Passport has been revoked",
            PassportStatus.Suspended => "Passport is currently suspended",
            PassportStatus.Expired => "Passport has expired",
            _ when entry.ExpiresAt <= DateTimeOffset.UtcNow => "Passport has expired",
            _ => $"Passport status is {entry.Status}"
        };

        return Task.FromResult(new ComplianceResult
        {
            IsCompliant = false,
            Framework = Framework,
            Status = entry.Status == PassportStatus.Suspended
                ? ComplianceStatus.RequiresReview
                : ComplianceStatus.NonCompliant,
            Recommendations = new[] { statusDescription },
            Metadata = new Dictionary<string, object>
            {
                ["PassportId"] = entry.PassportId,
                ["Status"] = entry.Status.ToString()
            }
        });
    }

    // ------------------------------------------------------------------
    // Internal types
    // ------------------------------------------------------------------

    /// <summary>
    /// Status change record providing a single entry in the passport audit trail.
    /// </summary>
    public sealed record PassportStatusChange
    {
        /// <summary>Previous status, or <c>null</c> for initial registration.</summary>
        public required PassportStatus? OldStatus { get; init; }

        /// <summary>New status after the change.</summary>
        public required PassportStatus NewStatus { get; init; }

        /// <summary>Human-readable reason for the status change.</summary>
        public required string Reason { get; init; }

        /// <summary>Identifier of the entity that performed the change.</summary>
        public required string ChangedBy { get; init; }

        /// <summary>Timestamp when the change occurred.</summary>
        public required DateTimeOffset Timestamp { get; init; }
    }

    /// <summary>
    /// Registry entry tracking a passport through its full lifecycle.
    /// </summary>
    public sealed record PassportRegistryEntry
    {
        /// <summary>Unique passport identifier.</summary>
        public required string PassportId { get; init; }

        /// <summary>Data object this passport certifies.</summary>
        public required string ObjectId { get; init; }

        /// <summary>Current lifecycle status.</summary>
        public required PassportStatus Status { get; init; }

        /// <summary>Timestamp when the passport was originally issued.</summary>
        public required DateTimeOffset IssuedAt { get; init; }

        /// <summary>Timestamp when the passport expires.</summary>
        public required DateTimeOffset ExpiresAt { get; init; }

        /// <summary>Reason for revocation, if revoked.</summary>
        public PassportRevocationReason? RevocationReason { get; init; }

        /// <summary>Reason for suspension, if suspended.</summary>
        public string? SuspensionReason { get; init; }

        /// <summary>Complete ordered history of status transitions.</summary>
        public required List<PassportStatusChange> History { get; init; }
    }
}
