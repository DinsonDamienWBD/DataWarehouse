using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataGovernance.Strategies.PolicyManagement;

/// <summary>
/// Governance policy dashboard data layer providing CRUD, version history,
/// approval workflows, testing sandbox, compliance gap visualization, and effectiveness metrics.
/// </summary>
public sealed class PolicyDashboardDataLayer : DataGovernanceStrategyBase
{
    private readonly BoundedDictionary<string, PolicyDashboardItem> _policyItems = new BoundedDictionary<string, PolicyDashboardItem>(1000);
    private readonly BoundedDictionary<string, List<PolicyVersionRecord>> _versionHistory = new BoundedDictionary<string, List<PolicyVersionRecord>>(1000);
    private readonly BoundedDictionary<string, PolicyApprovalWorkflow> _workflows = new BoundedDictionary<string, PolicyApprovalWorkflow>(1000);
    private readonly BoundedDictionary<string, PolicyTestResult> _testResults = new BoundedDictionary<string, PolicyTestResult>(1000);
    private readonly BoundedDictionary<string, PolicyEffectivenessMetric> _effectivenessMetrics = new BoundedDictionary<string, PolicyEffectivenessMetric>(1000);
    private readonly BoundedDictionary<string, ComplianceGap> _complianceGaps = new BoundedDictionary<string, ComplianceGap>(1000);

    public override string StrategyId => "policy-dashboard-data-layer";
    public override string DisplayName => "Policy Dashboard Data Layer";
    public override GovernanceCategory Category => GovernanceCategory.PolicyManagement;
    public override DataGovernanceCapabilities Capabilities => new()
    {
        SupportsAsync = true, SupportsBatch = true,
        SupportsRealTime = true, SupportsAudit = true,
        SupportsVersioning = true
    };
    public override string SemanticDescription =>
        "Dashboard data layer for governance policies with CRUD, versioning, approval workflows, " +
        "testing sandbox, compliance gap visualization, and effectiveness metrics";
    public override string[] Tags => ["dashboard", "policy", "governance", "crud", "metrics"];

    #region Policy CRUD

    /// <summary>
    /// Creates a new policy dashboard item.
    /// </summary>
    public PolicyDashboardItem CreatePolicy(string name, string description, string category,
        Dictionary<string, object> rules, string createdBy)
    {
        // P2-2329: Validate required fields — empty strings silently produce unusable policies.
        ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(name));
        ArgumentException.ThrowIfNullOrWhiteSpace(description, nameof(description));
        ArgumentException.ThrowIfNullOrWhiteSpace(category, nameof(category));
        ArgumentNullException.ThrowIfNull(rules, nameof(rules));
        ArgumentException.ThrowIfNullOrWhiteSpace(createdBy, nameof(createdBy));

        var id = Guid.NewGuid().ToString("N")[..12];
        var item = new PolicyDashboardItem
        {
            PolicyId = id,
            Name = name,
            Description = description,
            Category = category,
            Rules = rules,
            Status = PolicyStatus.Draft,
            CreatedBy = createdBy,
            CreatedAt = DateTimeOffset.UtcNow,
            Version = 1,
            EffectivenessScore = 0
        };
        _policyItems[id] = item;
        RecordVersion(id, item, createdBy, "Created");
        return item;
    }

    /// <summary>
    /// Updates an existing policy.
    /// </summary>
    public PolicyDashboardItem? UpdatePolicy(string policyId, string? name = null,
        string? description = null, Dictionary<string, object>? rules = null, string? updatedBy = null)
    {
        if (!_policyItems.TryGetValue(policyId, out var existing)) return null;

        var updated = existing with
        {
            Name = name ?? existing.Name,
            Description = description ?? existing.Description,
            Rules = rules ?? existing.Rules,
            Version = existing.Version + 1,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = updatedBy
        };
        _policyItems[policyId] = updated;
        RecordVersion(policyId, updated, updatedBy ?? "system", "Updated");
        return updated;
    }

    /// <summary>
    /// Gets a policy by ID.
    /// </summary>
    public PolicyDashboardItem? GetPolicy(string policyId) =>
        _policyItems.TryGetValue(policyId, out var item) ? item : null;

    /// <summary>
    /// Lists all policies with optional filtering.
    /// </summary>
    public IReadOnlyList<PolicyDashboardItem> ListPolicies(
        PolicyStatus? status = null, string? category = null,
        string? createdBy = null, int skip = 0, int take = 100)
    {
        // LOW-3019: Removed redundant AsEnumerable() — _policyItems.Values already implements
        // IEnumerable<PolicyDashboardItem>; the extra call forced a premature evaluation.
        IEnumerable<PolicyDashboardItem> query = _policyItems.Values;
        if (status.HasValue) query = query.Where(p => p.Status == status.Value);
        if (category != null) query = query.Where(p => p.Category == category);
        if (createdBy != null) query = query.Where(p => p.CreatedBy == createdBy);
        return query.OrderByDescending(p => p.UpdatedAt ?? p.CreatedAt).Skip(skip).Take(take).ToList().AsReadOnly();
    }

    /// <summary>
    /// Deletes a policy (soft delete by setting status to Archived).
    /// </summary>
    public bool DeletePolicy(string policyId)
    {
        if (!_policyItems.TryGetValue(policyId, out var item)) return false;
        _policyItems[policyId] = item with { Status = PolicyStatus.Archived };
        return true;
    }

    #endregion

    #region Version History

    /// <summary>
    /// Gets version history for a policy.
    /// </summary>
    public IReadOnlyList<PolicyVersionRecord> GetVersionHistory(string policyId)
    {
        if (!_versionHistory.TryGetValue(policyId, out var history))
            return Array.Empty<PolicyVersionRecord>();
        // Lock the list to synchronize with concurrent RecordVersion writers (finding 2307).
        lock (history) { return history.ToArray(); }
    }

    /// <summary>
    /// Restores a policy to a previous version.
    /// </summary>
    public PolicyDashboardItem? RestoreVersion(string policyId, int targetVersion, string restoredBy)
    {
        if (!_versionHistory.TryGetValue(policyId, out var history)) return null;
        var target = history.FirstOrDefault(v => v.Version == targetVersion);
        if (target == null) return null;

        return UpdatePolicy(policyId, target.Name, target.Description, target.Rules, restoredBy);
    }

    private void RecordVersion(string policyId, PolicyDashboardItem item, string author, string action)
    {
        var record = new PolicyVersionRecord
        {
            PolicyId = policyId,
            Version = item.Version,
            Name = item.Name,
            Description = item.Description,
            Rules = new Dictionary<string, object>(item.Rules),
            Status = item.Status,
            Author = author,
            Action = action,
            Timestamp = DateTimeOffset.UtcNow
        };

        _versionHistory.AddOrUpdate(
            policyId,
            _ => new List<PolicyVersionRecord> { record },
            (_, list) => { lock (list) { list.Add(record); } return list; });
    }

    #endregion

    #region Approval Workflows

    /// <summary>
    /// Submits a policy for approval.
    /// </summary>
    public PolicyApprovalWorkflow SubmitForApproval(string policyId, string submittedBy, string[] approverIds)
    {
        if (!_policyItems.TryGetValue(policyId, out var item))
            throw new KeyNotFoundException($"Policy {policyId} not found");

        var workflow = new PolicyApprovalWorkflow
        {
            WorkflowId = Guid.NewGuid().ToString("N")[..12],
            PolicyId = policyId,
            PolicyVersion = item.Version,
            SubmittedBy = submittedBy,
            SubmittedAt = DateTimeOffset.UtcNow,
            Approvers = approverIds.Select(id => new ApproverStatus
            {
                ApproverId = id,
                Status = ApprovalDecision.Pending
            }).ToList(),
            Status = WorkflowStatus.Pending
        };
        _workflows[workflow.WorkflowId] = workflow;
        _policyItems[policyId] = item with { Status = PolicyStatus.PendingApproval };
        return workflow;
    }

    /// <summary>
    /// Records an approval decision.
    /// </summary>
    public PolicyApprovalWorkflow? RecordApproval(string workflowId, string approverId, ApprovalDecision decision, string? comment = null)
    {
        if (!_workflows.TryGetValue(workflowId, out var workflow)) return null;

        var approver = workflow.Approvers.FirstOrDefault(a => a.ApproverId == approverId);
        if (approver == null) return null;

        approver = approver with { Status = decision, Comment = comment, DecidedAt = DateTimeOffset.UtcNow };
        var updatedApprovers = workflow.Approvers
            .Select(a => a.ApproverId == approverId ? approver : a).ToList();

        var allDecided = updatedApprovers.All(a => a.Status != ApprovalDecision.Pending);
        var anyRejected = updatedApprovers.Any(a => a.Status == ApprovalDecision.Rejected);

        var newStatus = allDecided
            ? (anyRejected ? WorkflowStatus.Rejected : WorkflowStatus.Approved)
            : WorkflowStatus.Pending;

        var updated = workflow with { Approvers = updatedApprovers, Status = newStatus };
        _workflows[workflowId] = updated;

        // Update policy status based on workflow outcome
        if (newStatus == WorkflowStatus.Approved && _policyItems.TryGetValue(workflow.PolicyId, out var policy))
        {
            _policyItems[workflow.PolicyId] = policy with { Status = PolicyStatus.Active };
        }
        else if (newStatus == WorkflowStatus.Rejected && _policyItems.TryGetValue(workflow.PolicyId, out var rejectedPolicy))
        {
            _policyItems[workflow.PolicyId] = rejectedPolicy with { Status = PolicyStatus.Draft };
        }

        return updated;
    }

    /// <summary>
    /// Gets workflows for a policy.
    /// </summary>
    public IReadOnlyList<PolicyApprovalWorkflow> GetWorkflows(string? policyId = null) =>
        (policyId != null
            ? _workflows.Values.Where(w => w.PolicyId == policyId)
            : _workflows.Values)
        .OrderByDescending(w => w.SubmittedAt).ToList().AsReadOnly();

    #endregion

    #region Policy Testing Sandbox

    /// <summary>
    /// Tests a policy against sample data in a sandbox environment.
    /// </summary>
    public PolicyTestResult TestPolicy(string policyId, Dictionary<string, object> testData)
    {
        if (!_policyItems.TryGetValue(policyId, out var policy))
            return new PolicyTestResult { PolicyId = policyId, Passed = false, Error = "Policy not found" };

        var violations = new List<string>();
        var passes = new List<string>();

        foreach (var (ruleName, ruleValue) in policy.Rules)
        {
            if (testData.TryGetValue(ruleName, out var testValue))
            {
                if (Equals(ruleValue, testValue))
                    passes.Add($"Rule '{ruleName}': PASS (expected={ruleValue}, actual={testValue})");
                else
                    violations.Add($"Rule '{ruleName}': FAIL (expected={ruleValue}, actual={testValue})");
            }
            else
            {
                violations.Add($"Rule '{ruleName}': MISSING (required field not in test data)");
            }
        }

        var result = new PolicyTestResult
        {
            PolicyId = policyId,
            Passed = violations.Count == 0,
            Violations = violations,
            Passes = passes,
            TestedAt = DateTimeOffset.UtcNow,
            TestDataSize = testData.Count
        };

        _testResults[policyId] = result;
        return result;
    }

    #endregion

    #region Compliance Gap Visualization

    /// <summary>
    /// Records a compliance gap.
    /// </summary>
    public ComplianceGap RecordComplianceGap(string policyId, string resourceId,
        string gapDescription, ComplianceGapSeverity severity)
    {
        var gap = new ComplianceGap
        {
            GapId = Guid.NewGuid().ToString("N")[..12],
            PolicyId = policyId,
            ResourceId = resourceId,
            Description = gapDescription,
            Severity = severity,
            DetectedAt = DateTimeOffset.UtcNow,
            Status = ComplianceGapStatus.Open
        };
        _complianceGaps[gap.GapId] = gap;
        return gap;
    }

    /// <summary>
    /// Gets compliance gaps with optional filtering.
    /// </summary>
    public IReadOnlyList<ComplianceGap> GetComplianceGaps(
        string? policyId = null, ComplianceGapSeverity? severity = null,
        ComplianceGapStatus? status = null)
    {
        var query = _complianceGaps.Values.AsEnumerable();
        if (policyId != null) query = query.Where(g => g.PolicyId == policyId);
        if (severity.HasValue) query = query.Where(g => g.Severity == severity.Value);
        if (status.HasValue) query = query.Where(g => g.Status == status.Value);
        return query.OrderByDescending(g => g.DetectedAt).ToList().AsReadOnly();
    }

    /// <summary>
    /// Gets compliance gap summary for dashboard visualization.
    /// </summary>
    public ComplianceGapSummary GetComplianceGapSummary()
    {
        var gaps = _complianceGaps.Values.ToList();
        return new ComplianceGapSummary
        {
            TotalGaps = gaps.Count,
            OpenGaps = gaps.Count(g => g.Status == ComplianceGapStatus.Open),
            CriticalGaps = gaps.Count(g => g.Severity == ComplianceGapSeverity.Critical),
            HighGaps = gaps.Count(g => g.Severity == ComplianceGapSeverity.High),
            MediumGaps = gaps.Count(g => g.Severity == ComplianceGapSeverity.Medium),
            LowGaps = gaps.Count(g => g.Severity == ComplianceGapSeverity.Low),
            GapsByPolicy = gaps.GroupBy(g => g.PolicyId).ToDictionary(g => g.Key, g => g.Count()),
            GeneratedAt = DateTimeOffset.UtcNow
        };
    }

    #endregion

    #region Effectiveness Metrics

    /// <summary>
    /// Records policy effectiveness metric.
    /// </summary>
    public void RecordEffectiveness(string policyId, double complianceRate,
        int totalEvaluations, int violations, int exceptions)
    {
        _effectivenessMetrics[policyId] = new PolicyEffectivenessMetric
        {
            PolicyId = policyId,
            ComplianceRate = complianceRate,
            TotalEvaluations = totalEvaluations,
            Violations = violations,
            Exceptions = exceptions,
            EffectivenessScore = CalculateEffectivenessScore(complianceRate, violations, totalEvaluations),
            MeasuredAt = DateTimeOffset.UtcNow
        };

        // Update policy with effectiveness score
        if (_policyItems.TryGetValue(policyId, out var policy))
        {
            _policyItems[policyId] = policy with
            {
                EffectivenessScore = CalculateEffectivenessScore(complianceRate, violations, totalEvaluations)
            };
        }
    }

    /// <summary>
    /// Gets effectiveness metrics for a policy.
    /// </summary>
    public PolicyEffectivenessMetric? GetEffectiveness(string policyId) =>
        _effectivenessMetrics.TryGetValue(policyId, out var metric) ? metric : null;

    /// <summary>
    /// Gets aggregated effectiveness metrics for all policies.
    /// </summary>
    public EffectivenessAggregation GetAggregatedEffectiveness()
    {
        var metrics = _effectivenessMetrics.Values.ToList();
        if (metrics.Count == 0)
            return new EffectivenessAggregation { GeneratedAt = DateTimeOffset.UtcNow };

        return new EffectivenessAggregation
        {
            TotalPolicies = metrics.Count,
            AverageComplianceRate = metrics.Average(m => m.ComplianceRate),
            AverageEffectivenessScore = metrics.Average(m => m.EffectivenessScore),
            TotalViolations = metrics.Sum(m => m.Violations),
            TotalEvaluations = metrics.Sum(m => m.TotalEvaluations),
            TopPerformers = metrics.OrderByDescending(m => m.EffectivenessScore).Take(5).ToList(),
            BottomPerformers = metrics.OrderBy(m => m.EffectivenessScore).Take(5).ToList(),
            GeneratedAt = DateTimeOffset.UtcNow
        };
    }

    private static double CalculateEffectivenessScore(double complianceRate, int violations, int totalEvaluations)
    {
        if (totalEvaluations == 0) return 0;
        var violationRate = (double)violations / totalEvaluations;
        return complianceRate * 0.6 + (1.0 - violationRate) * 0.4;
    }

    #endregion
}

#region Models

public sealed record PolicyDashboardItem
{
    public required string PolicyId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string Category { get; init; }
    public Dictionary<string, object> Rules { get; init; } = new();
    public PolicyStatus Status { get; init; }
    public required string CreatedBy { get; init; }
    public string? UpdatedBy { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public int Version { get; init; }
    public double EffectivenessScore { get; init; }
}

public enum PolicyStatus { Draft, PendingApproval, Active, Suspended, Archived, Deprecated }

public sealed record PolicyVersionRecord
{
    public required string PolicyId { get; init; }
    public int Version { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public Dictionary<string, object> Rules { get; init; } = new();
    public PolicyStatus Status { get; init; }
    public required string Author { get; init; }
    public required string Action { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}

public sealed record PolicyApprovalWorkflow
{
    public required string WorkflowId { get; init; }
    public required string PolicyId { get; init; }
    public int PolicyVersion { get; init; }
    public required string SubmittedBy { get; init; }
    public DateTimeOffset SubmittedAt { get; init; }
    public List<ApproverStatus> Approvers { get; init; } = new();
    public WorkflowStatus Status { get; init; }
}

public sealed record ApproverStatus
{
    public required string ApproverId { get; init; }
    public ApprovalDecision Status { get; init; }
    public string? Comment { get; init; }
    public DateTimeOffset? DecidedAt { get; init; }
}

public enum ApprovalDecision { Pending, Approved, Rejected, Abstained }
public enum WorkflowStatus { Pending, Approved, Rejected, Cancelled }

public sealed record PolicyTestResult
{
    public required string PolicyId { get; init; }
    public bool Passed { get; init; }
    public string? Error { get; init; }
    public List<string> Violations { get; init; } = new();
    public List<string> Passes { get; init; } = new();
    public DateTimeOffset TestedAt { get; init; }
    public int TestDataSize { get; init; }
}

public sealed record ComplianceGap
{
    public required string GapId { get; init; }
    public required string PolicyId { get; init; }
    public required string ResourceId { get; init; }
    public required string Description { get; init; }
    public ComplianceGapSeverity Severity { get; init; }
    public ComplianceGapStatus Status { get; init; }
    public DateTimeOffset DetectedAt { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }
}

public enum ComplianceGapSeverity { Low, Medium, High, Critical }
public enum ComplianceGapStatus { Open, InProgress, Resolved, Accepted }

public sealed record ComplianceGapSummary
{
    public int TotalGaps { get; init; }
    public int OpenGaps { get; init; }
    public int CriticalGaps { get; init; }
    public int HighGaps { get; init; }
    public int MediumGaps { get; init; }
    public int LowGaps { get; init; }
    public Dictionary<string, int> GapsByPolicy { get; init; } = new();
    public DateTimeOffset GeneratedAt { get; init; }
}

public sealed record PolicyEffectivenessMetric
{
    public required string PolicyId { get; init; }
    public double ComplianceRate { get; init; }
    public int TotalEvaluations { get; init; }
    public int Violations { get; init; }
    public int Exceptions { get; init; }
    public double EffectivenessScore { get; init; }
    public DateTimeOffset MeasuredAt { get; init; }
}

public sealed record EffectivenessAggregation
{
    public int TotalPolicies { get; init; }
    public double AverageComplianceRate { get; init; }
    public double AverageEffectivenessScore { get; init; }
    public int TotalViolations { get; init; }
    public int TotalEvaluations { get; init; }
    public List<PolicyEffectivenessMetric> TopPerformers { get; init; } = new();
    public List<PolicyEffectivenessMetric> BottomPerformers { get; init; } = new();
    public DateTimeOffset GeneratedAt { get; init; }
}

#endregion
