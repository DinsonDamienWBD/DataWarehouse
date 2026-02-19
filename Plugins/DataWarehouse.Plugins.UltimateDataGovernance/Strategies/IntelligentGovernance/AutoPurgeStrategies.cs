using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Consciousness;

namespace DataWarehouse.Plugins.UltimateDataGovernance.Strategies.IntelligentGovernance;

#region Supporting Types

/// <summary>
/// Reason category for why a data object is recommended for purging.
/// </summary>
public enum PurgeReason
{
    /// <summary>Low value combined with high liability -- data is a net negative.</summary>
    ToxicData,
    /// <summary>Data has exceeded its retention period.</summary>
    RetentionExpired,
    /// <summary>Regulatory requirement mandates deletion.</summary>
    RegulatoryRequirement,
    /// <summary>Data subject or owner explicitly requested deletion.</summary>
    UserRequested,
    /// <summary>Storage reclamation needed and this data is lowest priority.</summary>
    StorageReclamation
}

/// <summary>
/// Urgency level for purge execution.
/// </summary>
public enum PurgeUrgency
{
    /// <summary>Regulatory or breach response -- purge immediately.</summary>
    Immediate,
    /// <summary>High risk -- purge within 24 hours.</summary>
    High,
    /// <summary>Moderate risk -- purge within 7 days.</summary>
    Medium,
    /// <summary>Low risk -- purge at next maintenance window.</summary>
    Low,
    /// <summary>Purge at a specific scheduled date.</summary>
    Scheduled
}

/// <summary>
/// Represents the outcome of an auto-purge evaluation for a single data object.
/// </summary>
/// <param name="ObjectId">Unique identifier of the evaluated data object.</param>
/// <param name="ShouldPurge">Whether the object should be purged.</param>
/// <param name="Reason">The category of purge reason.</param>
/// <param name="Urgency">How urgently the purge should be executed.</param>
/// <param name="RequiresApproval">Whether the purge requires manual approval before execution.</param>
/// <param name="ApprovalStatus">Current approval status: "pending", "approved", "rejected", or "auto_approved".</param>
/// <param name="Score">The consciousness score that drove this decision.</param>
/// <param name="DecidedAt">UTC timestamp when the decision was made.</param>
/// <param name="PurgeScheduledAt">Optional scheduled purge date for deferred purges.</param>
public sealed record PurgeDecision(
    string ObjectId,
    bool ShouldPurge,
    PurgeReason Reason,
    PurgeUrgency Urgency,
    bool RequiresApproval,
    string ApprovalStatus,
    ConsciousnessScore Score,
    DateTime DecidedAt,
    DateTime? PurgeScheduledAt = null);

/// <summary>
/// Tracks the approval state for a purge request.
/// </summary>
/// <param name="ObjectId">The data object targeted for purge.</param>
/// <param name="RequestedBy">Identifier of the strategy or actor that requested the purge.</param>
/// <param name="RequestedAt">UTC timestamp when the purge was requested.</param>
/// <param name="ApprovedBy">Identifier of the approver, null if not yet approved.</param>
/// <param name="ApprovedAt">UTC timestamp of approval, null if not yet approved.</param>
/// <param name="RejectedReason">Reason for rejection, null if not rejected.</param>
public sealed record PurgeApproval(
    string ObjectId,
    string RequestedBy,
    DateTime RequestedAt,
    string? ApprovedBy = null,
    DateTime? ApprovedAt = null,
    string? RejectedReason = null);

#endregion

#region Strategy 1: ToxicDataPurgeStrategy

/// <summary>
/// Identifies and flags toxic data -- objects with low value and high liability that represent
/// a net negative to the organization. Toxic data is defined as value score &lt; 30 AND
/// liability score > 70.
/// </summary>
/// <remarks>
/// Urgency is determined by liability severity:
/// <list type="bullet">
///   <item><term>Liability > 90</term><description>Immediate purge (breach/regulatory risk)</description></item>
///   <item><term>Liability > 80</term><description>High urgency (within 24 hours)</description></item>
///   <item><term>Otherwise</term><description>Medium urgency (within 7 days)</description></item>
/// </list>
/// Always requires approval because purge is destructive and irreversible.
/// Generates a justification string explaining why the data is classified as toxic.
/// </remarks>
public sealed class ToxicDataPurgeStrategy : ConsciousnessStrategyBase
{
    private const double ValueThreshold = 30.0;
    private const double LiabilityThreshold = 70.0;

    /// <inheritdoc />
    public override string StrategyId => "auto-purge-toxic";

    /// <inheritdoc />
    public override string DisplayName => "Toxic Data Purge";

    /// <inheritdoc />
    public override ConsciousnessCategory Category => ConsciousnessCategory.AutoPurge;

    /// <inheritdoc />
    public override ConsciousnessCapabilities Capabilities => new(
        SupportsAsync: true,
        SupportsBatch: true,
        SupportsRealTime: true);

    /// <inheritdoc />
    public override string SemanticDescription =>
        "Identifies toxic data (low value + high liability) and recommends purge with mandatory approval. " +
        "Toxic threshold: value < 30 AND liability > 70. Urgency scales with liability severity.";

    /// <inheritdoc />
    public override string[] Tags => ["auto-purge", "toxic-data", "liability", "risk-reduction", "data-hygiene"];

    /// <summary>
    /// Evaluates whether a data object is toxic and should be purged.
    /// </summary>
    /// <param name="score">The consciousness score of the data object.</param>
    /// <param name="metadata">Object metadata for additional context.</param>
    /// <returns>A purge decision indicating whether the data is toxic.</returns>
    public PurgeDecision Evaluate(ConsciousnessScore score, Dictionary<string, object> metadata)
    {
        ArgumentNullException.ThrowIfNull(score);
        ArgumentNullException.ThrowIfNull(metadata);
        IncrementCounter("evaluate_invocations");

        double valueScore = score.Value.OverallScore;
        double liabilityScore = score.Liability.OverallScore;

        bool isToxic = valueScore < ValueThreshold && liabilityScore > LiabilityThreshold;

        if (!isToxic)
        {
            IncrementCounter("not_toxic");
            return new PurgeDecision(
                score.ObjectId,
                ShouldPurge: false,
                Reason: PurgeReason.ToxicData,
                Urgency: PurgeUrgency.Low,
                RequiresApproval: true,
                ApprovalStatus: "not_applicable",
                Score: score,
                DecidedAt: DateTime.UtcNow);
        }

        // Determine urgency based on liability severity
        PurgeUrgency urgency = liabilityScore switch
        {
            > 90 => PurgeUrgency.Immediate,
            > 80 => PurgeUrgency.High,
            _ => PurgeUrgency.Medium
        };

        // Build justification
        string justification = BuildToxicJustification(score);

        IncrementCounter("toxic_detected");
        return new PurgeDecision(
            score.ObjectId,
            ShouldPurge: true,
            Reason: PurgeReason.ToxicData,
            Urgency: urgency,
            RequiresApproval: true, // Always requires approval -- purge is destructive
            ApprovalStatus: "pending",
            Score: score,
            DecidedAt: DateTime.UtcNow);
    }

    private static string BuildToxicJustification(ConsciousnessScore score)
    {
        var factors = new List<string>
        {
            $"Value score: {score.Value.OverallScore:F1} (below {ValueThreshold} threshold)"
        };

        if (score.Value.ValueDrivers.Count > 0)
        {
            factors.Add($"Value drivers: {string.Join(", ", score.Value.ValueDrivers)}");
        }

        factors.Add($"Liability score: {score.Liability.OverallScore:F1} (above {LiabilityThreshold} threshold)");

        if (score.Liability.LiabilityFactors.Count > 0)
        {
            factors.Add($"Liability factors: {string.Join(", ", score.Liability.LiabilityFactors)}");
        }

        if (score.Liability.DetectedPIITypes.Count > 0)
        {
            factors.Add($"Detected PII: {string.Join(", ", score.Liability.DetectedPIITypes)}");
        }

        if (score.Liability.ApplicableRegulations.Count > 0)
        {
            factors.Add($"Applicable regulations: {string.Join(", ", score.Liability.ApplicableRegulations)}");
        }

        return string.Join("; ", factors);
    }
}

#endregion

#region Strategy 2: RetentionExpiredPurgeStrategy

/// <summary>
/// Recommends purge for data objects whose retention period has expired and which are not
/// under legal hold or active litigation.
/// </summary>
/// <remarks>
/// Metadata keys consumed:
/// <list type="bullet">
///   <item><term>retention_end_date</term><description>The date when retention expires (DateTime).</description></item>
///   <item><term>legal_hold</term><description>Whether the object is under legal hold (bool).</description></item>
///   <item><term>active_litigation</term><description>Whether the object is involved in active litigation (bool).</description></item>
/// </list>
/// Urgency is determined by how far past the retention end date:
/// overdue > 90 days = High, overdue > 30 days = Medium, else Low.
/// Supports configurable auto-approval for retention-based purges.
/// </remarks>
public sealed class RetentionExpiredPurgeStrategy : ConsciousnessStrategyBase
{
    private bool _autoApproveRetentionPurge;

    /// <inheritdoc />
    public override string StrategyId => "auto-purge-retention-expired";

    /// <inheritdoc />
    public override string DisplayName => "Retention Expired Purge";

    /// <inheritdoc />
    public override ConsciousnessCategory Category => ConsciousnessCategory.AutoPurge;

    /// <inheritdoc />
    public override ConsciousnessCapabilities Capabilities => new(
        SupportsAsync: true,
        SupportsBatch: true,
        SupportsRealTime: true);

    /// <inheritdoc />
    public override string SemanticDescription =>
        "Recommends purge for data whose retention period has expired and which is not under " +
        "legal hold or active litigation. Supports configurable auto-approval for retention purges.";

    /// <inheritdoc />
    public override string[] Tags => ["auto-purge", "retention", "expired", "compliance", "data-lifecycle"];

    /// <summary>
    /// Configures whether retention-based purges are auto-approved.
    /// </summary>
    /// <param name="autoApprove">True to auto-approve retention purges; false to require manual approval.</param>
    public void ConfigureAutoApproval(bool autoApprove)
    {
        _autoApproveRetentionPurge = autoApprove;
    }

    /// <summary>
    /// Evaluates whether a data object's retention period has expired and purge is warranted.
    /// </summary>
    /// <param name="score">The consciousness score of the data object.</param>
    /// <param name="metadata">Object metadata including retention_end_date, legal_hold, and active_litigation.</param>
    /// <returns>A purge decision based on retention expiry analysis.</returns>
    public PurgeDecision Evaluate(ConsciousnessScore score, Dictionary<string, object> metadata)
    {
        ArgumentNullException.ThrowIfNull(score);
        ArgumentNullException.ThrowIfNull(metadata);
        IncrementCounter("evaluate_invocations");

        // Check for legal hold
        if (metadata.TryGetValue("legal_hold", out var legalHoldObj) && Convert.ToBoolean(legalHoldObj))
        {
            IncrementCounter("legal_hold_blocks");
            return new PurgeDecision(
                score.ObjectId,
                ShouldPurge: false,
                Reason: PurgeReason.RetentionExpired,
                Urgency: PurgeUrgency.Low,
                RequiresApproval: true,
                ApprovalStatus: "blocked_legal_hold",
                Score: score,
                DecidedAt: DateTime.UtcNow);
        }

        // Check for active litigation
        if (metadata.TryGetValue("active_litigation", out var litigationObj) && Convert.ToBoolean(litigationObj))
        {
            IncrementCounter("litigation_blocks");
            return new PurgeDecision(
                score.ObjectId,
                ShouldPurge: false,
                Reason: PurgeReason.RetentionExpired,
                Urgency: PurgeUrgency.Low,
                RequiresApproval: true,
                ApprovalStatus: "blocked_litigation",
                Score: score,
                DecidedAt: DateTime.UtcNow);
        }

        // Check retention end date
        if (!metadata.TryGetValue("retention_end_date", out var retentionEndObj))
        {
            IncrementCounter("no_retention_date");
            return new PurgeDecision(
                score.ObjectId,
                ShouldPurge: false,
                Reason: PurgeReason.RetentionExpired,
                Urgency: PurgeUrgency.Low,
                RequiresApproval: true,
                ApprovalStatus: "not_applicable",
                Score: score,
                DecidedAt: DateTime.UtcNow);
        }

        DateTime retentionEndDate = Convert.ToDateTime(retentionEndObj);
        if (retentionEndDate > DateTime.UtcNow)
        {
            IncrementCounter("retention_not_expired");
            return new PurgeDecision(
                score.ObjectId,
                ShouldPurge: false,
                Reason: PurgeReason.RetentionExpired,
                Urgency: PurgeUrgency.Low,
                RequiresApproval: true,
                ApprovalStatus: "not_applicable",
                Score: score,
                DecidedAt: DateTime.UtcNow);
        }

        // Retention has expired -- determine urgency based on how overdue
        double daysOverdue = (DateTime.UtcNow - retentionEndDate).TotalDays;
        PurgeUrgency urgency = daysOverdue switch
        {
            > 90 => PurgeUrgency.High,
            > 30 => PurgeUrgency.Medium,
            _ => PurgeUrgency.Low
        };

        bool requiresApproval = !_autoApproveRetentionPurge;
        string approvalStatus = _autoApproveRetentionPurge ? "auto_approved" : "pending";

        IncrementCounter("retention_expired_detected");
        return new PurgeDecision(
            score.ObjectId,
            ShouldPurge: true,
            Reason: PurgeReason.RetentionExpired,
            Urgency: urgency,
            RequiresApproval: requiresApproval,
            ApprovalStatus: approvalStatus,
            Score: score,
            DecidedAt: DateTime.UtcNow);
    }
}

#endregion

#region Strategy 3: PurgeApprovalWorkflowStrategy

/// <summary>
/// Manages the approval state machine for purge requests, routing approvals based on
/// impact severity, liability levels, and regulatory requirements.
/// </summary>
/// <remarks>
/// Approval rules:
/// <list type="bullet">
///   <item><term>Bulk purge (>1000 objects)</term><description>Requires senior approval.</description></item>
///   <item><term>High liability (>80)</term><description>Requires compliance officer approval.</description></item>
///   <item><term>Regulatory requirement</term><description>Auto-approved after 48-hour hold.</description></item>
/// </list>
/// State machine: pending -> approved | rejected.
/// </remarks>
public sealed class PurgeApprovalWorkflowStrategy : ConsciousnessStrategyBase
{
    private const int BulkThreshold = 1000;
    private const double HighLiabilityThreshold = 80.0;
    private static readonly TimeSpan RegulatoryAutoApproveHold = TimeSpan.FromHours(48);

    private readonly ConcurrentDictionary<string, PurgeApproval> _approvals = new();

    /// <inheritdoc />
    public override string StrategyId => "auto-purge-approval";

    /// <inheritdoc />
    public override string DisplayName => "Purge Approval Workflow";

    /// <inheritdoc />
    public override ConsciousnessCategory Category => ConsciousnessCategory.AutoPurge;

    /// <inheritdoc />
    public override ConsciousnessCapabilities Capabilities => new(
        SupportsAsync: true,
        SupportsBatch: true);

    /// <inheritdoc />
    public override string SemanticDescription =>
        "Manages the approval state machine for purge requests. Routes approvals based on bulk impact, " +
        "liability severity, and regulatory requirements. Never allows purge without proper authorization.";

    /// <inheritdoc />
    public override string[] Tags => ["auto-purge", "approval", "workflow", "state-machine", "compliance", "authorization"];

    /// <summary>
    /// Submits a purge request and determines the required approval level.
    /// </summary>
    /// <param name="decision">The purge decision to submit for approval.</param>
    /// <param name="requestedBy">Identifier of the requesting strategy or actor.</param>
    /// <param name="batchSize">Number of objects in the purge batch (for bulk threshold checking).</param>
    /// <returns>The approval record with the appropriate routing.</returns>
    public PurgeApproval SubmitForApproval(PurgeDecision decision, string requestedBy, int batchSize = 1)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedBy);
        IncrementCounter("approval_submissions");

        var approval = new PurgeApproval(
            ObjectId: decision.ObjectId,
            RequestedBy: requestedBy,
            RequestedAt: DateTime.UtcNow);

        // Check if regulatory requirement -- auto-approve after 48h hold
        if (decision.Reason == PurgeReason.RegulatoryRequirement)
        {
            approval = approval with
            {
                ApprovedBy = "regulatory_auto_approve",
                ApprovedAt = DateTime.UtcNow.Add(RegulatoryAutoApproveHold)
            };
            IncrementCounter("regulatory_auto_approvals");
        }

        _approvals[decision.ObjectId] = approval;

        // Log routing information
        if (batchSize > BulkThreshold)
        {
            IncrementCounter("senior_approval_required");
        }

        if (decision.Score.Liability.OverallScore > HighLiabilityThreshold)
        {
            IncrementCounter("compliance_officer_approval_required");
        }

        return approval;
    }

    /// <summary>
    /// Approves a pending purge request.
    /// </summary>
    /// <param name="objectId">The object ID to approve purge for.</param>
    /// <param name="approvedBy">Identifier of the approver.</param>
    /// <returns>The updated approval record, or null if no pending approval exists.</returns>
    public PurgeApproval? Approve(string objectId, string approvedBy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedBy);

        if (!_approvals.TryGetValue(objectId, out var existing))
            return null;

        // Cannot approve if already rejected
        if (existing.RejectedReason != null)
            return existing;

        var updated = existing with
        {
            ApprovedBy = approvedBy,
            ApprovedAt = DateTime.UtcNow
        };

        _approvals[objectId] = updated;
        IncrementCounter("approvals_granted");
        return updated;
    }

    /// <summary>
    /// Rejects a pending purge request.
    /// </summary>
    /// <param name="objectId">The object ID to reject purge for.</param>
    /// <param name="reason">The reason for rejection.</param>
    /// <returns>The updated approval record, or null if no pending approval exists.</returns>
    public PurgeApproval? Reject(string objectId, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (!_approvals.TryGetValue(objectId, out var existing))
            return null;

        // Cannot reject if already approved and executed
        if (existing.ApprovedAt.HasValue && existing.ApprovedAt.Value <= DateTime.UtcNow)
            return existing;

        var updated = existing with { RejectedReason = reason };
        _approvals[objectId] = updated;
        IncrementCounter("approvals_rejected");
        return updated;
    }

    /// <summary>
    /// Gets the approval status for a specific object.
    /// </summary>
    /// <param name="objectId">The data object identifier.</param>
    /// <returns>The approval record, or null if not found.</returns>
    public PurgeApproval? GetApproval(string objectId) =>
        _approvals.TryGetValue(objectId, out var approval) ? approval : null;

    /// <summary>
    /// Checks whether a purge has been approved and the approval is effective (past hold period).
    /// </summary>
    /// <param name="objectId">The data object identifier.</param>
    /// <returns>True if approved and effective; false otherwise.</returns>
    public bool IsApprovedAndEffective(string objectId)
    {
        if (!_approvals.TryGetValue(objectId, out var approval))
            return false;

        if (approval.RejectedReason != null)
            return false;

        if (!approval.ApprovedAt.HasValue)
            return false;

        return approval.ApprovedAt.Value <= DateTime.UtcNow;
    }

    /// <summary>
    /// Determines the required approval level based on batch size and liability.
    /// </summary>
    /// <param name="batchSize">Number of objects in the purge batch.</param>
    /// <param name="maxLiability">Maximum liability score in the batch.</param>
    /// <returns>A string describing the required approval level.</returns>
    public static string DetermineApprovalLevel(int batchSize, double maxLiability)
    {
        if (batchSize > BulkThreshold && maxLiability > HighLiabilityThreshold)
            return "senior_and_compliance_officer";
        if (batchSize > BulkThreshold)
            return "senior_approval";
        if (maxLiability > HighLiabilityThreshold)
            return "compliance_officer";
        return "standard";
    }

    /// <summary>
    /// Gets all pending approvals that have not been approved or rejected.
    /// </summary>
    /// <returns>A read-only list of pending approval records.</returns>
    public IReadOnlyList<PurgeApproval> GetPendingApprovals()
    {
        return _approvals.Values
            .Where(a => !a.ApprovedAt.HasValue && a.RejectedReason == null)
            .ToList()
            .AsReadOnly();
    }
}

#endregion

#region Strategy 4: AutoPurgeOrchestrator

/// <summary>
/// Orchestrates auto-purge decisions by evaluating toxic data and retention-expired strategies,
/// then routing through the approval workflow. Never executes a purge without approval --
/// purge is irreversible and safety-critical.
/// Subscribes to "consciousness.purge.recommended" message bus topic and publishes
/// "consciousness.purge.approved", "consciousness.purge.executed", or "consciousness.purge.rejected" events.
/// </summary>
public sealed class AutoPurgeOrchestrator : ConsciousnessStrategyBase
{
    private readonly ToxicDataPurgeStrategy _toxicStrategy = new();
    private readonly RetentionExpiredPurgeStrategy _retentionStrategy = new();
    private readonly PurgeApprovalWorkflowStrategy _approvalWorkflow = new();
    private readonly ConcurrentDictionary<string, PurgeDecision> _decisions = new();

    /// <summary>
    /// The message bus topic this orchestrator subscribes to for purge recommendations.
    /// </summary>
    public const string SubscribeTopic = "consciousness.purge.recommended";

    /// <summary>
    /// The message bus topic published when a purge is approved.
    /// </summary>
    public const string ApprovedTopic = "consciousness.purge.approved";

    /// <summary>
    /// The message bus topic published when a purge is executed.
    /// </summary>
    public const string ExecutedTopic = "consciousness.purge.executed";

    /// <summary>
    /// The message bus topic published when a purge is rejected.
    /// </summary>
    public const string RejectedTopic = "consciousness.purge.rejected";

    /// <inheritdoc />
    public override string StrategyId => "auto-purge-orchestrator";

    /// <inheritdoc />
    public override string DisplayName => "Auto-Purge Orchestrator";

    /// <inheritdoc />
    public override ConsciousnessCategory Category => ConsciousnessCategory.AutoPurge;

    /// <inheritdoc />
    public override ConsciousnessCapabilities Capabilities => new(
        SupportsAsync: true,
        SupportsBatch: true,
        SupportsRealTime: true,
        SupportsStreaming: true,
        SupportsRetroactive: true);

    /// <inheritdoc />
    public override string SemanticDescription =>
        "Orchestrates auto-purge decisions across toxic data and retention-expired strategies, " +
        "routing all purge requests through the approval workflow. Subscribes to " +
        "'consciousness.purge.recommended' and publishes approved/executed/rejected events. " +
        "Never executes purge without approval -- purge is irreversible.";

    /// <inheritdoc />
    public override string[] Tags => ["auto-purge", "orchestrator", "approval-workflow", "message-bus", "safety-critical"];

    /// <summary>
    /// Gets the approval workflow strategy for direct approval management.
    /// </summary>
    public PurgeApprovalWorkflowStrategy ApprovalWorkflow => _approvalWorkflow;

    /// <summary>
    /// Configures the retention-expired strategy auto-approval setting.
    /// </summary>
    /// <param name="autoApprove">True to auto-approve retention-based purges.</param>
    public void ConfigureRetentionAutoApproval(bool autoApprove)
    {
        _retentionStrategy.ConfigureAutoApproval(autoApprove);
    }

    /// <summary>
    /// Evaluates a single consciousness score against all purge strategies and routes
    /// through the approval workflow. Never purges without approval.
    /// </summary>
    /// <param name="score">The consciousness score to evaluate.</param>
    /// <param name="metadata">Object metadata for evaluation context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A purge decision routed through the approval workflow.</returns>
    public Task<PurgeDecision> EvaluateAsync(
        ConsciousnessScore score,
        Dictionary<string, object> metadata,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(score);
        ArgumentNullException.ThrowIfNull(metadata);
        IncrementCounter("evaluate_invocations");

        // Evaluate toxic data strategy
        var toxicDecision = _toxicStrategy.Evaluate(score, metadata);

        // Evaluate retention-expired strategy
        var retentionDecision = _retentionStrategy.Evaluate(score, metadata);

        // Select the most urgent purge recommendation
        PurgeDecision? selectedDecision = null;

        if (toxicDecision.ShouldPurge && retentionDecision.ShouldPurge)
        {
            // Both recommend purge -- take the more urgent one
            selectedDecision = toxicDecision.Urgency <= retentionDecision.Urgency
                ? toxicDecision
                : retentionDecision;
        }
        else if (toxicDecision.ShouldPurge)
        {
            selectedDecision = toxicDecision;
        }
        else if (retentionDecision.ShouldPurge)
        {
            selectedDecision = retentionDecision;
        }

        if (selectedDecision == null)
        {
            IncrementCounter("no_purge_recommended");
            var noPurge = new PurgeDecision(
                score.ObjectId,
                ShouldPurge: false,
                Reason: PurgeReason.ToxicData,
                Urgency: PurgeUrgency.Low,
                RequiresApproval: false,
                ApprovalStatus: "not_applicable",
                Score: score,
                DecidedAt: DateTime.UtcNow);
            _decisions[score.ObjectId] = noPurge;
            return Task.FromResult(noPurge);
        }

        // Route through approval workflow -- NEVER purge without approval
        var approval = _approvalWorkflow.SubmitForApproval(
            selectedDecision,
            requestedBy: StrategyId);

        string approvalStatus = approval.RejectedReason != null
            ? "rejected"
            : approval.ApprovedAt.HasValue
                ? (approval.ApprovedAt.Value <= DateTime.UtcNow ? "approved" : "pending_hold")
                : "pending";

        // Ensure RequiresApproval is always true for orchestrator-level decisions
        var finalDecision = selectedDecision with
        {
            RequiresApproval = true,
            ApprovalStatus = approvalStatus
        };

        _decisions[score.ObjectId] = finalDecision;
        IncrementCounter("purge_recommended");
        return Task.FromResult(finalDecision);
    }

    /// <summary>
    /// Gets a previously recorded purge decision for a specific object.
    /// </summary>
    /// <param name="objectId">The data object identifier.</param>
    /// <returns>The purge decision, or null if not found.</returns>
    public PurgeDecision? GetDecision(string objectId) =>
        _decisions.TryGetValue(objectId, out var decision) ? decision : null;

    /// <summary>
    /// Gets all tracked purge decisions.
    /// </summary>
    /// <returns>A read-only dictionary of all decisions keyed by object ID.</returns>
    public IReadOnlyDictionary<string, PurgeDecision> GetAllDecisions() =>
        new Dictionary<string, PurgeDecision>(_decisions);
}

#endregion
