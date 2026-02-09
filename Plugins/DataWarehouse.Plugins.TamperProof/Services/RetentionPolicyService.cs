// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Contracts.TamperProof;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.TamperProof.Services;

/// <summary>
/// Service interface for managing retention policies and legal holds on tamper-proof blocks.
/// Provides compliance-grade retention enforcement with audit trail support.
/// </summary>
public interface IRetentionPolicyService
{
    /// <summary>
    /// Sets a retention policy for a block.
    /// </summary>
    /// <param name="blockId">Block identifier.</param>
    /// <param name="policy">Retention policy to apply.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The applied retention policy.</returns>
    Task<RetentionPolicy> SetPolicyAsync(Guid blockId, RetentionPolicy policy, CancellationToken ct = default);

    /// <summary>
    /// Gets the retention policy for a block.
    /// </summary>
    /// <param name="blockId">Block identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The retention policy or null if none exists.</returns>
    Task<RetentionPolicy?> GetPolicyAsync(Guid blockId, CancellationToken ct = default);

    /// <summary>
    /// Checks if a block is currently under retention (cannot be deleted).
    /// </summary>
    /// <param name="blockId">Block identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if block is retained and cannot be deleted.</returns>
    Task<bool> IsRetainedAsync(Guid blockId, CancellationToken ct = default);

    /// <summary>
    /// Applies a legal hold to a block, preventing deletion regardless of retention policy.
    /// </summary>
    /// <param name="blockId">Block identifier.</param>
    /// <param name="request">Legal hold request with reason and applied by information.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the legal hold operation.</returns>
    Task<LegalHoldResult> ApplyLegalHoldAsync(Guid blockId, LegalHoldRequest request, CancellationToken ct = default);

    /// <summary>
    /// Releases a legal hold from a block.
    /// </summary>
    /// <param name="blockId">Block identifier.</param>
    /// <param name="holdId">Legal hold identifier to release.</param>
    /// <param name="authorizedBy">Principal authorized to release the hold.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the release operation.</returns>
    Task<LegalHoldResult> ReleaseLegalHoldAsync(Guid blockId, string holdId, string authorizedBy, CancellationToken ct = default);

    /// <summary>
    /// Gets all active legal holds for a block.
    /// </summary>
    /// <param name="blockId">Block identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of active legal holds.</returns>
    Task<IReadOnlyList<RetentionLegalHold>> GetActiveLegalHoldsAsync(Guid blockId, CancellationToken ct = default);

    /// <summary>
    /// Generates a retention report for all blocks under management.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Comprehensive retention report.</returns>
    Task<RetentionReport> GetRetentionReportAsync(CancellationToken ct = default);

    /// <summary>
    /// Validates whether a deletion operation is allowed for a block.
    /// </summary>
    /// <param name="blockId">Block identifier to validate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Validation result with details if deletion is blocked.</returns>
    Task<DeletionValidationResult> ValidateDeletionAsync(Guid blockId, CancellationToken ct = default);

    /// <summary>
    /// Extends the retention period for a block.
    /// Retention can only be extended, never shortened (compliance requirement).
    /// </summary>
    /// <param name="blockId">Block identifier.</param>
    /// <param name="newRetainUntil">New retention end date (must be later than current).</param>
    /// <param name="extendedBy">Principal extending the retention.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Updated retention policy.</returns>
    Task<RetentionPolicy> ExtendRetentionAsync(Guid blockId, DateTime newRetainUntil, string extendedBy, CancellationToken ct = default);
}

/// <summary>
/// Implementation of retention policy and legal hold management.
/// Provides in-memory storage with full audit trail for compliance requirements.
/// </summary>
public class RetentionPolicyService : IRetentionPolicyService
{
    private readonly ConcurrentDictionary<Guid, RetentionPolicy> _policies = new();
    private readonly ConcurrentDictionary<Guid, List<RetentionLegalHold>> _legalHolds = new();
    private readonly ConcurrentDictionary<string, LegalHoldAuditEntry> _auditLog = new();
    private readonly ILogger<RetentionPolicyService> _logger;

    /// <summary>
    /// Creates a new retention policy service instance.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    public RetentionPolicyService(ILogger<RetentionPolicyService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public Task<RetentionPolicy> SetPolicyAsync(Guid blockId, RetentionPolicy policy, CancellationToken ct = default)
    {
        if (blockId == Guid.Empty)
            throw new ArgumentException("Block ID cannot be empty.", nameof(blockId));

        if (policy == null)
            throw new ArgumentNullException(nameof(policy));

        _logger.LogInformation(
            "Setting retention policy for block {BlockId}: Mode={Mode}, RetainUntil={RetainUntil}",
            blockId, policy.Mode, policy.RetainUntil);

        // Check if we're trying to shorten an existing retention (not allowed for compliance modes)
        if (_policies.TryGetValue(blockId, out var existingPolicy))
        {
            if (existingPolicy.Mode == RetentionMode.Compliance)
            {
                throw new InvalidOperationException(
                    $"Cannot modify compliance retention policy for block {blockId}. " +
                    "Compliance retention can only be extended.");
            }

            if (existingPolicy.RetainUntil.HasValue && policy.RetainUntil.HasValue &&
                policy.RetainUntil.Value < existingPolicy.RetainUntil.Value)
            {
                throw new InvalidOperationException(
                    $"Cannot shorten retention period for block {blockId}. " +
                    $"Current: {existingPolicy.RetainUntil}, Requested: {policy.RetainUntil}");
            }
        }

        var appliedPolicy = policy with { BlockId = blockId };
        _policies[blockId] = appliedPolicy;

        _logger.LogDebug("Retention policy set for block {BlockId}", blockId);

        return Task.FromResult(appliedPolicy);
    }

    /// <inheritdoc/>
    public Task<RetentionPolicy?> GetPolicyAsync(Guid blockId, CancellationToken ct = default)
    {
        if (_policies.TryGetValue(blockId, out var policy))
        {
            return Task.FromResult<RetentionPolicy?>(policy);
        }

        return Task.FromResult<RetentionPolicy?>(null);
    }

    /// <inheritdoc/>
    public async Task<bool> IsRetainedAsync(Guid blockId, CancellationToken ct = default)
    {
        // Check for active legal holds first (they override retention policy)
        var legalHolds = await GetActiveLegalHoldsAsync(blockId, ct);
        if (legalHolds.Count > 0)
        {
            _logger.LogDebug(
                "Block {BlockId} is retained due to {Count} active legal hold(s)",
                blockId, legalHolds.Count);
            return true;
        }

        // Check time-based retention policy
        if (_policies.TryGetValue(blockId, out var policy))
        {
            return IsUnderRetention(policy);
        }

        // No policy = not retained
        return false;
    }

    /// <inheritdoc/>
    public Task<LegalHoldResult> ApplyLegalHoldAsync(Guid blockId, LegalHoldRequest request, CancellationToken ct = default)
    {
        if (blockId == Guid.Empty)
            throw new ArgumentException("Block ID cannot be empty.", nameof(blockId));

        if (request == null)
            throw new ArgumentNullException(nameof(request));

        if (string.IsNullOrWhiteSpace(request.Reason))
            throw new ArgumentException("Legal hold reason is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.AppliedBy))
            throw new ArgumentException("AppliedBy is required.", nameof(request));

        var holdId = $"LH-{Guid.NewGuid():N}".ToUpperInvariant()[..16];

        var legalHold = new RetentionLegalHold(
            HoldId: holdId,
            BlockId: blockId,
            Reason: request.Reason,
            AppliedBy: request.AppliedBy,
            AppliedAt: DateTime.UtcNow,
            CaseReference: request.CaseReference,
            IsActive: true);

        var holds = _legalHolds.GetOrAdd(blockId, _ => new List<RetentionLegalHold>());

        lock (holds)
        {
            holds.Add(legalHold);
        }

        // Record audit entry
        var auditEntry = new LegalHoldAuditEntry(
            AuditId: Guid.NewGuid().ToString(),
            HoldId: holdId,
            BlockId: blockId,
            Action: LegalHoldAction.Applied,
            PerformedBy: request.AppliedBy,
            PerformedAt: DateTime.UtcNow,
            Reason: request.Reason,
            CaseReference: request.CaseReference);

        _auditLog[auditEntry.AuditId] = auditEntry;

        _logger.LogWarning(
            "Legal hold {HoldId} applied to block {BlockId} by {AppliedBy}. Reason: {Reason}. Case: {CaseReference}",
            holdId, blockId, request.AppliedBy, request.Reason, request.CaseReference ?? "N/A");

        return Task.FromResult(new LegalHoldResult(
            Success: true,
            HoldId: holdId));
    }

    /// <inheritdoc/>
    public Task<LegalHoldResult> ReleaseLegalHoldAsync(Guid blockId, string holdId, string authorizedBy, CancellationToken ct = default)
    {
        if (blockId == Guid.Empty)
            throw new ArgumentException("Block ID cannot be empty.", nameof(blockId));

        if (string.IsNullOrWhiteSpace(holdId))
            throw new ArgumentException("Hold ID is required.", nameof(holdId));

        if (string.IsNullOrWhiteSpace(authorizedBy))
            throw new ArgumentException("AuthorizedBy is required.", nameof(authorizedBy));

        if (!_legalHolds.TryGetValue(blockId, out var holds))
        {
            return Task.FromResult(new LegalHoldResult(
                Success: false,
                HoldId: holdId,
                Error: $"No legal holds found for block {blockId}"));
        }

        RetentionLegalHold? holdToRelease = null;
        int holdIndex = -1;

        lock (holds)
        {
            for (int i = 0; i < holds.Count; i++)
            {
                if (holds[i].HoldId == holdId && holds[i].IsActive)
                {
                    holdToRelease = holds[i];
                    holdIndex = i;
                    break;
                }
            }

            if (holdToRelease == null)
            {
                return Task.FromResult(new LegalHoldResult(
                    Success: false,
                    HoldId: holdId,
                    Error: $"Legal hold {holdId} not found or already released"));
            }

            // Mark as inactive (preserve for audit trail)
            holds[holdIndex] = holdToRelease with { IsActive = false };
        }

        // Record audit entry
        var auditEntry = new LegalHoldAuditEntry(
            AuditId: Guid.NewGuid().ToString(),
            HoldId: holdId,
            BlockId: blockId,
            Action: LegalHoldAction.Released,
            PerformedBy: authorizedBy,
            PerformedAt: DateTime.UtcNow,
            Reason: $"Released by {authorizedBy}",
            CaseReference: holdToRelease.CaseReference);

        _auditLog[auditEntry.AuditId] = auditEntry;

        _logger.LogWarning(
            "Legal hold {HoldId} released from block {BlockId} by {AuthorizedBy}",
            holdId, blockId, authorizedBy);

        return Task.FromResult(new LegalHoldResult(
            Success: true,
            HoldId: holdId));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<RetentionLegalHold>> GetActiveLegalHoldsAsync(Guid blockId, CancellationToken ct = default)
    {
        if (!_legalHolds.TryGetValue(blockId, out var holds))
        {
            return Task.FromResult<IReadOnlyList<RetentionLegalHold>>(Array.Empty<RetentionLegalHold>());
        }

        List<RetentionLegalHold> activeHolds;
        lock (holds)
        {
            activeHolds = holds.Where(h => h.IsActive).ToList();
        }

        return Task.FromResult<IReadOnlyList<RetentionLegalHold>>(activeHolds);
    }

    /// <inheritdoc/>
    public async Task<RetentionReport> GetRetentionReportAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var thirtyDaysFromNow = now.AddDays(30);

        var totalRetained = 0;
        var totalWithLegalHold = 0;
        var expiringWithin30Days = 0;
        var expiringPolicies = new List<RetentionPolicy>();

        foreach (var kvp in _policies)
        {
            var policy = kvp.Value;
            var blockId = kvp.Key;

            if (await IsRetainedAsync(blockId, ct))
            {
                totalRetained++;
            }

            var holds = await GetActiveLegalHoldsAsync(blockId, ct);
            if (holds.Count > 0)
            {
                totalWithLegalHold++;
            }

            if (policy.RetainUntil.HasValue &&
                policy.RetainUntil.Value > now &&
                policy.RetainUntil.Value <= thirtyDaysFromNow)
            {
                expiringWithin30Days++;
                expiringPolicies.Add(policy);
            }
        }

        return new RetentionReport(
            TotalBlocksUnderRetention: totalRetained,
            BlocksWithLegalHold: totalWithLegalHold,
            BlocksExpiringWithin30Days: expiringWithin30Days,
            ExpiringPolicies: expiringPolicies.AsReadOnly());
    }

    /// <inheritdoc/>
    public async Task<DeletionValidationResult> ValidateDeletionAsync(Guid blockId, CancellationToken ct = default)
    {
        // Check for active legal holds first
        var legalHolds = await GetActiveLegalHoldsAsync(blockId, ct);
        if (legalHolds.Count > 0)
        {
            var holdReasons = string.Join(", ", legalHolds.Select(h => $"{h.HoldId}: {h.Reason}"));
            return new DeletionValidationResult(
                IsAllowed: false,
                BlockId: blockId,
                Reason: DeletionBlockedReason.LegalHold,
                Details: $"Block has {legalHolds.Count} active legal hold(s): {holdReasons}",
                ActiveLegalHolds: legalHolds);
        }

        // Check retention policy
        if (_policies.TryGetValue(blockId, out var policy))
        {
            if (IsUnderRetention(policy))
            {
                return new DeletionValidationResult(
                    IsAllowed: false,
                    BlockId: blockId,
                    Reason: DeletionBlockedReason.RetentionPolicy,
                    Details: $"Block is under {policy.Mode} retention until {policy.RetainUntil?.ToString("O") ?? "indefinitely"}",
                    RetentionPolicy: policy);
            }

            // Check if early deletion is explicitly blocked
            if (!policy.AllowEarlyDeletion && policy.Mode == RetentionMode.Compliance)
            {
                return new DeletionValidationResult(
                    IsAllowed: false,
                    BlockId: blockId,
                    Reason: DeletionBlockedReason.CompliancePolicy,
                    Details: "Compliance retention policy does not allow early deletion",
                    RetentionPolicy: policy);
            }
        }

        return new DeletionValidationResult(
            IsAllowed: true,
            BlockId: blockId,
            Reason: DeletionBlockedReason.None,
            Details: "Deletion is allowed");
    }

    /// <inheritdoc/>
    public Task<RetentionPolicy> ExtendRetentionAsync(Guid blockId, DateTime newRetainUntil, string extendedBy, CancellationToken ct = default)
    {
        if (blockId == Guid.Empty)
            throw new ArgumentException("Block ID cannot be empty.", nameof(blockId));

        if (string.IsNullOrWhiteSpace(extendedBy))
            throw new ArgumentException("ExtendedBy is required.", nameof(extendedBy));

        if (!_policies.TryGetValue(blockId, out var existingPolicy))
        {
            throw new KeyNotFoundException($"No retention policy found for block {blockId}");
        }

        if (existingPolicy.RetainUntil.HasValue && newRetainUntil <= existingPolicy.RetainUntil.Value)
        {
            throw new InvalidOperationException(
                $"Cannot shorten retention. Current: {existingPolicy.RetainUntil}, Requested: {newRetainUntil}. " +
                "Retention periods can only be extended.");
        }

        var extendedPolicy = existingPolicy with
        {
            RetainUntil = newRetainUntil,
            CreatedBy = $"{existingPolicy.CreatedBy} (extended by {extendedBy})"
        };

        _policies[blockId] = extendedPolicy;

        _logger.LogInformation(
            "Retention extended for block {BlockId} to {NewRetainUntil} by {ExtendedBy}",
            blockId, newRetainUntil, extendedBy);

        return Task.FromResult(extendedPolicy);
    }

    /// <summary>
    /// Checks if a retention policy is currently active.
    /// </summary>
    private bool IsUnderRetention(RetentionPolicy policy)
    {
        return policy.Mode switch
        {
            RetentionMode.None => false,
            RetentionMode.Indefinite => true,
            RetentionMode.Compliance => true,
            RetentionMode.TimeBased => policy.RetainUntil.HasValue && policy.RetainUntil.Value > DateTime.UtcNow,
            RetentionMode.EventBased => true, // Event-based stays until event occurs
            _ => false
        };
    }
}

/// <summary>
/// Retention policy configuration for a block.
/// </summary>
/// <param name="BlockId">Block identifier this policy applies to.</param>
/// <param name="Mode">Retention mode determining how retention is enforced.</param>
/// <param name="RetainUntil">Specific date until which data must be retained (for TimeBased mode).</param>
/// <param name="AllowEarlyDeletion">Whether early deletion is allowed before retention expires.</param>
/// <param name="PolicyName">Optional name for the policy for reference.</param>
/// <param name="CreatedAt">When the policy was created.</param>
/// <param name="CreatedBy">Principal who created the policy.</param>
public record RetentionPolicy(
    Guid BlockId,
    RetentionMode Mode,
    DateTime? RetainUntil,
    bool AllowEarlyDeletion,
    string? PolicyName,
    DateTime CreatedAt,
    string CreatedBy);

/// <summary>
/// Retention enforcement mode.
/// </summary>
public enum RetentionMode
{
    /// <summary>No retention policy - data can be deleted anytime.</summary>
    None,

    /// <summary>Time-based retention - retain until specific date.</summary>
    TimeBased,

    /// <summary>Event-based retention - retain until a specific event occurs.</summary>
    EventBased,

    /// <summary>Indefinite retention - retain forever until explicit release.</summary>
    Indefinite,

    /// <summary>Compliance mode - cannot be overridden or shortened.</summary>
    Compliance
}

/// <summary>
/// Legal hold on a block preventing deletion.
/// </summary>
/// <param name="HoldId">Unique identifier for this legal hold.</param>
/// <param name="BlockId">Block identifier this hold applies to.</param>
/// <param name="Reason">Reason for the legal hold.</param>
/// <param name="AppliedBy">Principal who applied the hold.</param>
/// <param name="AppliedAt">When the hold was applied.</param>
/// <param name="CaseReference">Optional case or matter reference.</param>
/// <param name="IsActive">Whether the hold is currently active.</param>
public record RetentionLegalHold(
    string HoldId,
    Guid BlockId,
    string Reason,
    string AppliedBy,
    DateTime AppliedAt,
    string? CaseReference,
    bool IsActive);

/// <summary>
/// Request to apply a legal hold.
/// </summary>
/// <param name="Reason">Reason for the legal hold.</param>
/// <param name="AppliedBy">Principal applying the hold.</param>
/// <param name="CaseReference">Optional case or matter reference.</param>
public record LegalHoldRequest(
    string Reason,
    string AppliedBy,
    string? CaseReference);

/// <summary>
/// Result of a legal hold operation.
/// </summary>
/// <param name="Success">Whether the operation succeeded.</param>
/// <param name="HoldId">The hold identifier (for apply) or affected hold (for release).</param>
/// <param name="Error">Error message if operation failed.</param>
public record LegalHoldResult(
    bool Success,
    string HoldId,
    string? Error = null);

/// <summary>
/// Comprehensive retention report.
/// </summary>
/// <param name="TotalBlocksUnderRetention">Total number of blocks currently under retention.</param>
/// <param name="BlocksWithLegalHold">Number of blocks with active legal holds.</param>
/// <param name="BlocksExpiringWithin30Days">Number of blocks with retention expiring soon.</param>
/// <param name="ExpiringPolicies">List of policies expiring within 30 days.</param>
public record RetentionReport(
    int TotalBlocksUnderRetention,
    int BlocksWithLegalHold,
    int BlocksExpiringWithin30Days,
    IReadOnlyList<RetentionPolicy> ExpiringPolicies);

/// <summary>
/// Result of deletion validation.
/// </summary>
/// <param name="IsAllowed">Whether deletion is allowed.</param>
/// <param name="BlockId">Block identifier that was validated.</param>
/// <param name="Reason">Reason if deletion is blocked.</param>
/// <param name="Details">Detailed explanation.</param>
/// <param name="ActiveLegalHolds">Active legal holds if any.</param>
/// <param name="RetentionPolicy">Retention policy if blocking deletion.</param>
public record DeletionValidationResult(
    bool IsAllowed,
    Guid BlockId,
    DeletionBlockedReason Reason,
    string Details,
    IReadOnlyList<RetentionLegalHold>? ActiveLegalHolds = null,
    RetentionPolicy? RetentionPolicy = null);

/// <summary>
/// Reason why deletion is blocked.
/// </summary>
public enum DeletionBlockedReason
{
    /// <summary>Deletion is not blocked.</summary>
    None,

    /// <summary>Blocked due to active legal hold.</summary>
    LegalHold,

    /// <summary>Blocked due to retention policy.</summary>
    RetentionPolicy,

    /// <summary>Blocked due to compliance policy that cannot be overridden.</summary>
    CompliancePolicy
}

/// <summary>
/// Audit entry for legal hold operations.
/// </summary>
/// <param name="AuditId">Unique audit entry identifier.</param>
/// <param name="HoldId">Legal hold identifier.</param>
/// <param name="BlockId">Block identifier.</param>
/// <param name="Action">Action performed.</param>
/// <param name="PerformedBy">Principal who performed the action.</param>
/// <param name="PerformedAt">When the action was performed.</param>
/// <param name="Reason">Reason for the action.</param>
/// <param name="CaseReference">Case reference if applicable.</param>
public record LegalHoldAuditEntry(
    string AuditId,
    string HoldId,
    Guid BlockId,
    LegalHoldAction Action,
    string PerformedBy,
    DateTime PerformedAt,
    string Reason,
    string? CaseReference);

/// <summary>
/// Legal hold action type.
/// </summary>
public enum LegalHoldAction
{
    /// <summary>Legal hold was applied.</summary>
    Applied,

    /// <summary>Legal hold was released.</summary>
    Released,

    /// <summary>Legal hold was modified.</summary>
    Modified
}
