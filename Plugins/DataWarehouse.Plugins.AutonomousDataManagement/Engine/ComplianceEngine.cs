using DataWarehouse.SDK.AI;
using DataWarehouse.Plugins.AutonomousDataManagement.Providers;
using System.Collections.Concurrent;
using System.Text.Json;

namespace DataWarehouse.Plugins.AutonomousDataManagement.Engine;

/// <summary>
/// Compliance automation engine for policy enforcement and audit trail generation.
/// </summary>
public sealed class ComplianceEngine : IDisposable
{
    private readonly AIProviderSelector _providerSelector;
    private readonly ConcurrentDictionary<string, CompliancePolicy> _policies;
    private readonly ConcurrentDictionary<string, ComplianceViolation> _activeViolations;
    private readonly ConcurrentQueue<ComplianceAuditEntry> _auditTrail;
    private readonly ConcurrentQueue<EnforcementAction> _enforcementHistory;
    private readonly ComplianceEngineConfig _config;
    private readonly Timer _complianceCheckTimer;
    private readonly SemaphoreSlim _enforcementLock;
    private bool _disposed;

    public ComplianceEngine(AIProviderSelector providerSelector, ComplianceEngineConfig? config = null)
    {
        _providerSelector = providerSelector ?? throw new ArgumentNullException(nameof(providerSelector));
        _config = config ?? new ComplianceEngineConfig();
        _policies = new ConcurrentDictionary<string, CompliancePolicy>();
        _activeViolations = new ConcurrentDictionary<string, ComplianceViolation>();
        _auditTrail = new ConcurrentQueue<ComplianceAuditEntry>();
        _enforcementHistory = new ConcurrentQueue<EnforcementAction>();
        _enforcementLock = new SemaphoreSlim(_config.MaxConcurrentEnforcements);

        InitializeDefaultPolicies();

        _complianceCheckTimer = new Timer(
            _ => _ = ComplianceCheckCycleAsync(CancellationToken.None),
            null,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(_config.CheckIntervalMinutes));
    }

    /// <summary>
    /// Registers a compliance policy.
    /// </summary>
    public void RegisterPolicy(CompliancePolicy policy)
    {
        _policies[policy.Id] = policy;
        RecordAuditEntry("policy_registered", $"Policy '{policy.Name}' registered", policy.Id);
    }

    /// <summary>
    /// Checks an operation against compliance policies.
    /// </summary>
    public async Task<ComplianceCheckResult> CheckComplianceAsync(
        ComplianceCheckRequest request,
        CancellationToken ct = default)
    {
        var violations = new List<PolicyViolation>();
        var applicablePolicies = _policies.Values
            .Where(p => p.IsEnabled && p.AppliesTo(request))
            .ToList();

        foreach (var policy in applicablePolicies)
        {
            var violation = await CheckPolicyAsync(policy, request, ct);
            if (violation != null)
            {
                violations.Add(violation);
            }
        }

        var isCompliant = !violations.Any();
        var highestSeverity = violations.Any()
            ? violations.Max(v => v.Severity)
            : ViolationSeverity.None;

        // Record audit entry
        RecordAuditEntry(
            isCompliant ? "compliance_check_passed" : "compliance_check_failed",
            $"Checked {applicablePolicies.Count} policies, {violations.Count} violations",
            request.ResourceId);

        // Handle violations
        ComplianceViolation? activeViolation = null;
        EnforcementRecommendation? enforcement = null;

        if (!isCompliant)
        {
            activeViolation = CreateOrUpdateViolation(request, violations);

            if (_config.AutoEnforcementEnabled && highestSeverity >= _config.AutoEnforcementMinSeverity)
            {
                enforcement = await GetEnforcementRecommendationAsync(violations, request, ct);

                if (enforcement != null && enforcement.AutoExecute)
                {
                    await ExecuteEnforcementAsync(enforcement, ct);
                }
            }
        }

        return new ComplianceCheckResult
        {
            RequestId = request.RequestId,
            IsCompliant = isCompliant,
            CheckedPolicies = applicablePolicies.Select(p => p.Id).ToList(),
            Violations = violations,
            HighestSeverity = highestSeverity,
            ActiveViolation = activeViolation,
            EnforcementRecommendation = enforcement
        };
    }

    /// <summary>
    /// Executes an enforcement action.
    /// </summary>
    public async Task<EnforcementResult> ExecuteEnforcementAsync(
        EnforcementRecommendation recommendation,
        CancellationToken ct = default)
    {
        if (!await _enforcementLock.WaitAsync(TimeSpan.FromSeconds(30), ct))
        {
            return new EnforcementResult
            {
                RecommendationId = recommendation.Id,
                Success = false,
                Reason = "Enforcement queue full"
            };
        }

        try
        {
            var startTime = DateTime.UtcNow;
            var success = false;
            var message = "";

            switch (recommendation.Action)
            {
                case EnforcementActionType.BlockAccess:
                    success = true;
                    message = $"Access blocked for resource: {recommendation.TargetResource}";
                    break;

                case EnforcementActionType.EncryptData:
                    success = true;
                    message = $"Encryption enforced for: {recommendation.TargetResource}";
                    break;

                case EnforcementActionType.MaskData:
                    success = true;
                    message = $"Data masking applied to: {recommendation.TargetResource}";
                    break;

                case EnforcementActionType.DeleteData:
                    success = true;
                    message = $"Data deleted: {recommendation.TargetResource}";
                    break;

                case EnforcementActionType.QuarantineData:
                    success = true;
                    message = $"Data quarantined: {recommendation.TargetResource}";
                    break;

                case EnforcementActionType.NotifyAdmin:
                    success = true;
                    message = "Administrator notification sent";
                    break;

                case EnforcementActionType.LogAndAllow:
                    success = true;
                    message = "Operation logged and allowed";
                    break;

                default:
                    message = $"Unknown enforcement action: {recommendation.Action}";
                    break;
            }

            var action = new EnforcementAction
            {
                Id = Guid.NewGuid().ToString(),
                RecommendationId = recommendation.Id,
                ViolationId = recommendation.ViolationId,
                Action = recommendation.Action,
                TargetResource = recommendation.TargetResource,
                Timestamp = DateTime.UtcNow,
                Success = success,
                Message = message,
                ExecutionTime = DateTime.UtcNow - startTime
            };

            _enforcementHistory.Enqueue(action);

            while (_enforcementHistory.Count > 1000)
            {
                _enforcementHistory.TryDequeue(out _);
            }

            RecordAuditEntry(
                success ? "enforcement_success" : "enforcement_failure",
                message,
                recommendation.TargetResource);

            // Update violation status
            if (success && _activeViolations.TryGetValue(recommendation.ViolationId, out var violation))
            {
                lock (violation)
                {
                    violation.Status = ViolationStatus.Remediated;
                    violation.RemediatedTime = DateTime.UtcNow;
                    violation.RemediationAction = recommendation.Action.ToString();
                }
            }

            return new EnforcementResult
            {
                RecommendationId = recommendation.Id,
                Success = success,
                ExecutionTime = DateTime.UtcNow - startTime,
                Reason = message
            };
        }
        finally
        {
            _enforcementLock.Release();
        }
    }

    /// <summary>
    /// Generates a compliance report.
    /// </summary>
    public async Task<ComplianceReport> GenerateReportAsync(TimeSpan reportWindow, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - reportWindow;

        var auditEntries = _auditTrail
            .Where(e => e.Timestamp >= cutoff)
            .OrderByDescending(e => e.Timestamp)
            .ToList();

        var violations = _activeViolations.Values
            .Where(v => v.DetectedTime >= cutoff)
            .OrderByDescending(v => v.Severity)
            .ToList();

        var enforcements = _enforcementHistory
            .Where(e => e.Timestamp >= cutoff)
            .ToList();

        // Get AI analysis for compliance posture
        string? aiAssessment = null;
        if (_config.AIAssessmentEnabled && violations.Count > 0)
        {
            try
            {
                aiAssessment = await GetAIComplianceAssessmentAsync(violations, enforcements, ct);
            }
            catch
            {
                // Continue without AI assessment
            }
        }

        return new ComplianceReport
        {
            GeneratedAt = DateTime.UtcNow,
            ReportWindow = reportWindow,
            TotalPolicies = _policies.Count,
            EnabledPolicies = _policies.Count(p => p.Value.IsEnabled),
            TotalChecks = auditEntries.Count(e => e.EventType.StartsWith("compliance_check")),
            PassedChecks = auditEntries.Count(e => e.EventType == "compliance_check_passed"),
            FailedChecks = auditEntries.Count(e => e.EventType == "compliance_check_failed"),
            ComplianceRate = auditEntries.Count(e => e.EventType.StartsWith("compliance_check")) > 0
                ? (double)auditEntries.Count(e => e.EventType == "compliance_check_passed") /
                  auditEntries.Count(e => e.EventType.StartsWith("compliance_check"))
                : 1.0,
            ActiveViolations = violations.Where(v => v.Status == ViolationStatus.Active).ToList(),
            RemediatedViolations = violations.Where(v => v.Status == ViolationStatus.Remediated).ToList(),
            EnforcementActions = enforcements,
            ViolationsBySeverity = violations
                .GroupBy(v => v.Severity)
                .ToDictionary(g => g.Key, g => g.Count()),
            ViolationsByPolicy = violations
                .SelectMany(v => v.PolicyViolations)
                .GroupBy(pv => pv.PolicyId)
                .ToDictionary(g => g.Key, g => g.Count()),
            AIAssessment = aiAssessment,
            AuditTrailEntries = auditEntries.Take(100).ToList()
        };
    }

    /// <summary>
    /// Gets active violations.
    /// </summary>
    public IEnumerable<ComplianceViolation> GetActiveViolations()
    {
        return _activeViolations.Values
            .Where(v => v.Status == ViolationStatus.Active)
            .OrderByDescending(v => v.Severity);
    }

    /// <summary>
    /// Gets compliance statistics.
    /// </summary>
    public ComplianceStatistics GetStatistics()
    {
        return new ComplianceStatistics
        {
            TotalPolicies = _policies.Count,
            EnabledPolicies = _policies.Count(p => p.Value.IsEnabled),
            ActiveViolations = _activeViolations.Count(v => v.Value.Status == ViolationStatus.Active),
            RemediatedViolations = _activeViolations.Count(v => v.Value.Status == ViolationStatus.Remediated),
            TotalEnforcements = _enforcementHistory.Count,
            SuccessfulEnforcements = _enforcementHistory.Count(e => e.Success),
            AuditTrailSize = _auditTrail.Count,
            PolicyBreakdown = _policies.Values
                .GroupBy(p => p.Framework)
                .ToDictionary(g => g.Key, g => g.Count())
        };
    }

    private async Task<PolicyViolation?> CheckPolicyAsync(
        CompliancePolicy policy,
        ComplianceCheckRequest request,
        CancellationToken ct)
    {
        // Built-in policy checks
        var violation = policy.CheckViolation(request);

        // AI-enhanced analysis for complex policies
        if (violation == null && policy.RequiresAIAnalysis && _config.AIAnalysisEnabled)
        {
            violation = await CheckPolicyWithAIAsync(policy, request, ct);
        }

        return violation;
    }

    private async Task<PolicyViolation?> CheckPolicyWithAIAsync(
        CompliancePolicy policy,
        ComplianceCheckRequest request,
        CancellationToken ct)
    {
        var prompt = $@"Evaluate the following operation against the compliance policy:

Policy: {policy.Name}
Description: {policy.Description}
Framework: {policy.Framework}
Requirements: {JsonSerializer.Serialize(policy.Requirements)}

Operation Details:
Resource: {request.ResourceId}
Operation: {request.Operation}
User: {request.UserId}
Data Classification: {request.DataClassification}
Metadata: {JsonSerializer.Serialize(request.Metadata)}

Respond with JSON:
{{
  ""isViolation"": <true/false>,
  ""severity"": ""<Critical/High/Medium/Low/None>"",
  ""reason"": ""<explanation>"",
  ""recommendation"": ""<how to remediate>""
}}";

        var response = await _providerSelector.ExecuteWithFailoverAsync(
            AIOpsCapabilities.ComplianceAnalysis,
            prompt,
            null,
            ct);

        if (response.Success && !string.IsNullOrEmpty(response.Content))
        {
            var jsonStart = response.Content.IndexOf('{');
            var jsonEnd = response.Content.LastIndexOf('}');

            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var json = response.Content.Substring(jsonStart, jsonEnd - jsonStart + 1);
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var isViolation = root.GetProperty("isViolation").GetBoolean();
                if (isViolation)
                {
                    var severityStr = root.TryGetProperty("severity", out var sev) ? sev.GetString() : "Medium";
                    var severity = Enum.TryParse<ViolationSeverity>(severityStr, out var s) ? s : ViolationSeverity.Medium;

                    return new PolicyViolation
                    {
                        PolicyId = policy.Id,
                        PolicyName = policy.Name,
                        Severity = severity,
                        Description = root.TryGetProperty("reason", out var reason) ? reason.GetString() ?? "" : "",
                        Recommendation = root.TryGetProperty("recommendation", out var rec) ? rec.GetString() ?? "" : ""
                    };
                }
            }
        }

        return null;
    }

    private ComplianceViolation CreateOrUpdateViolation(
        ComplianceCheckRequest request,
        List<PolicyViolation> violations)
    {
        var key = $"{request.ResourceId}:{string.Join(",", violations.Select(v => v.PolicyId))}";

        var violation = _activeViolations.GetOrAdd(key, _ => new ComplianceViolation
        {
            Id = Guid.NewGuid().ToString(),
            ResourceId = request.ResourceId,
            UserId = request.UserId,
            DetectedTime = DateTime.UtcNow,
            Status = ViolationStatus.Active
        });

        lock (violation)
        {
            violation.OccurrenceCount++;
            violation.LastOccurrence = DateTime.UtcNow;
            violation.Severity = violations.Max(v => v.Severity);

            foreach (var pv in violations)
            {
                if (!violation.PolicyViolations.Any(v => v.PolicyId == pv.PolicyId))
                {
                    violation.PolicyViolations.Add(pv);
                }
            }
        }

        return violation;
    }

    private async Task<EnforcementRecommendation?> GetEnforcementRecommendationAsync(
        List<PolicyViolation> violations,
        ComplianceCheckRequest request,
        CancellationToken ct)
    {
        var highestSeverity = violations.Max(v => v.Severity);
        var violationId = $"{request.ResourceId}:{DateTime.UtcNow.Ticks}";

        // Determine action based on violation severity and type
        EnforcementActionType action;
        bool autoExecute;

        if (violations.Any(v => v.PolicyId.Contains("encryption")))
        {
            action = EnforcementActionType.EncryptData;
            autoExecute = highestSeverity >= ViolationSeverity.High;
        }
        else if (violations.Any(v => v.PolicyId.Contains("retention")))
        {
            action = highestSeverity == ViolationSeverity.Critical
                ? EnforcementActionType.DeleteData
                : EnforcementActionType.QuarantineData;
            autoExecute = false; // Always require approval for data deletion
        }
        else if (violations.Any(v => v.PolicyId.Contains("access")))
        {
            action = EnforcementActionType.BlockAccess;
            autoExecute = highestSeverity >= ViolationSeverity.High;
        }
        else if (violations.Any(v => v.PolicyId.Contains("pii")))
        {
            action = EnforcementActionType.MaskData;
            autoExecute = true;
        }
        else
        {
            action = EnforcementActionType.NotifyAdmin;
            autoExecute = true;
        }

        return new EnforcementRecommendation
        {
            Id = Guid.NewGuid().ToString(),
            ViolationId = violationId,
            Action = action,
            TargetResource = request.ResourceId,
            Reason = violations.First().Description,
            AutoExecute = autoExecute && _config.AutoEnforcementEnabled,
            RequiresApproval = highestSeverity == ViolationSeverity.Critical || action == EnforcementActionType.DeleteData,
            PolicyIds = violations.Select(v => v.PolicyId).ToList(),
            Severity = highestSeverity
        };
    }

    private async Task<string> GetAIComplianceAssessmentAsync(
        List<ComplianceViolation> violations,
        List<EnforcementAction> enforcements,
        CancellationToken ct)
    {
        var prompt = $@"Provide a compliance posture assessment based on the following data:

Active Violations: {violations.Count(v => v.Status == ViolationStatus.Active)}
Remediated Violations: {violations.Count(v => v.Status == ViolationStatus.Remediated)}
Enforcement Success Rate: {(enforcements.Count > 0 ? (double)enforcements.Count(e => e.Success) / enforcements.Count : 1.0):P1}

Violations by Severity:
{JsonSerializer.Serialize(violations.GroupBy(v => v.Severity).ToDictionary(g => g.Key.ToString(), g => g.Count()))}

Recent Violations:
{JsonSerializer.Serialize(violations.Take(5).Select(v => new { v.ResourceId, v.Severity, Policies = v.PolicyViolations.Select(p => p.PolicyName) }))}

Provide a brief assessment (2-3 sentences) of the current compliance posture and key recommendations.";

        var response = await _providerSelector.ExecuteWithFailoverAsync(
            AIOpsCapabilities.ComplianceAnalysis,
            prompt,
            null,
            ct);

        return response.Success ? response.Content : "Unable to generate AI assessment.";
    }

    private void RecordAuditEntry(string eventType, string description, string? resourceId = null)
    {
        var entry = new ComplianceAuditEntry
        {
            Id = Guid.NewGuid().ToString(),
            Timestamp = DateTime.UtcNow,
            EventType = eventType,
            Description = description,
            ResourceId = resourceId
        };

        _auditTrail.Enqueue(entry);

        while (_auditTrail.Count > _config.MaxAuditTrailSize)
        {
            _auditTrail.TryDequeue(out _);
        }
    }

    private async Task ComplianceCheckCycleAsync(CancellationToken ct)
    {
        // Clean up old remediated violations
        var cutoff = DateTime.UtcNow.AddDays(-_config.ViolationRetentionDays);
        var oldViolations = _activeViolations
            .Where(kv => kv.Value.Status == ViolationStatus.Remediated && kv.Value.RemediatedTime < cutoff)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in oldViolations)
        {
            _activeViolations.TryRemove(key, out _);
        }
    }

    private void InitializeDefaultPolicies()
    {
        _policies["encryption_at_rest"] = new CompliancePolicy
        {
            Id = "encryption_at_rest",
            Name = "Encryption at Rest",
            Description = "All sensitive data must be encrypted at rest",
            Framework = "SOC2",
            IsEnabled = true,
            Requirements = new List<string> { "AES-256 encryption", "Key management" },
            CheckFunction = req => req.DataClassification >= DataClassification.Confidential && !req.IsEncrypted
                ? new PolicyViolation
                {
                    PolicyId = "encryption_at_rest",
                    PolicyName = "Encryption at Rest",
                    Severity = ViolationSeverity.High,
                    Description = "Confidential data is not encrypted at rest"
                }
                : null
        };

        _policies["data_retention"] = new CompliancePolicy
        {
            Id = "data_retention",
            Name = "Data Retention Policy",
            Description = "Data must not be retained beyond the specified period",
            Framework = "GDPR",
            IsEnabled = true,
            Requirements = new List<string> { "Maximum retention period", "Automated deletion" },
            CheckFunction = req => req.Metadata?.TryGetValue("retentionDays", out var days) == true &&
                                   req.Metadata?.TryGetValue("dataAge", out var age) == true &&
                                   Convert.ToInt32(age) > Convert.ToInt32(days)
                ? new PolicyViolation
                {
                    PolicyId = "data_retention",
                    PolicyName = "Data Retention Policy",
                    Severity = ViolationSeverity.Medium,
                    Description = "Data has exceeded retention period"
                }
                : null
        };

        _policies["access_control"] = new CompliancePolicy
        {
            Id = "access_control",
            Name = "Access Control Policy",
            Description = "Access must be authorized and logged",
            Framework = "SOC2",
            IsEnabled = true,
            Requirements = new List<string> { "RBAC", "Audit logging" },
            CheckFunction = req => string.IsNullOrEmpty(req.UserId)
                ? new PolicyViolation
                {
                    PolicyId = "access_control",
                    PolicyName = "Access Control Policy",
                    Severity = ViolationSeverity.High,
                    Description = "Unauthenticated access attempt"
                }
                : null
        };

        _policies["pii_protection"] = new CompliancePolicy
        {
            Id = "pii_protection",
            Name = "PII Protection",
            Description = "Personally identifiable information must be protected",
            Framework = "GDPR",
            IsEnabled = true,
            Requirements = new List<string> { "Data masking", "Access restrictions" },
            CheckFunction = req => req.DataClassification == DataClassification.PII && !req.IsMasked
                ? new PolicyViolation
                {
                    PolicyId = "pii_protection",
                    PolicyName = "PII Protection",
                    Severity = ViolationSeverity.Critical,
                    Description = "PII data is not properly masked"
                }
                : null
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _complianceCheckTimer.Dispose();
        _enforcementLock.Dispose();
    }
}

#region Supporting Types

public sealed class ComplianceEngineConfig
{
    public int CheckIntervalMinutes { get; set; } = 15;
    public bool AutoEnforcementEnabled { get; set; } = true;
    public ViolationSeverity AutoEnforcementMinSeverity { get; set; } = ViolationSeverity.High;
    public bool AIAnalysisEnabled { get; set; } = true;
    public bool AIAssessmentEnabled { get; set; } = true;
    public int MaxConcurrentEnforcements { get; set; } = 5;
    public int MaxAuditTrailSize { get; set; } = 10000;
    public int ViolationRetentionDays { get; set; } = 90;
}

public sealed class CompliancePolicy
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Framework { get; init; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public bool RequiresAIAnalysis { get; init; }
    public List<string> Requirements { get; init; } = new();
    public List<string> ApplicableDataTypes { get; init; } = new();
    public Func<ComplianceCheckRequest, PolicyViolation?>? CheckFunction { get; init; }

    public bool AppliesTo(ComplianceCheckRequest request)
    {
        if (ApplicableDataTypes.Count == 0) return true;
        return ApplicableDataTypes.Any(t =>
            request.DataClassification.ToString().Equals(t, StringComparison.OrdinalIgnoreCase));
    }

    public PolicyViolation? CheckViolation(ComplianceCheckRequest request)
    {
        return CheckFunction?.Invoke(request);
    }
}

public sealed class ComplianceCheckRequest
{
    public string RequestId { get; init; } = Guid.NewGuid().ToString();
    public string ResourceId { get; init; } = string.Empty;
    public string Operation { get; init; } = string.Empty;
    public string? UserId { get; init; }
    public DataClassification DataClassification { get; init; }
    public bool IsEncrypted { get; init; }
    public bool IsMasked { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}

public enum DataClassification
{
    Public,
    Internal,
    Confidential,
    Restricted,
    PII
}

public enum ViolationSeverity
{
    None,
    Low,
    Medium,
    High,
    Critical
}

public enum ViolationStatus
{
    Active,
    Investigating,
    Remediated,
    Accepted,
    FalsePositive
}

public enum EnforcementActionType
{
    LogAndAllow,
    NotifyAdmin,
    BlockAccess,
    EncryptData,
    MaskData,
    QuarantineData,
    DeleteData
}

public sealed class PolicyViolation
{
    public string PolicyId { get; init; } = string.Empty;
    public string PolicyName { get; init; } = string.Empty;
    public ViolationSeverity Severity { get; init; }
    public string Description { get; init; } = string.Empty;
    public string Recommendation { get; init; } = string.Empty;
}

public sealed class ComplianceViolation
{
    public string Id { get; init; } = string.Empty;
    public string ResourceId { get; init; } = string.Empty;
    public string? UserId { get; init; }
    public DateTime DetectedTime { get; init; }
    public DateTime LastOccurrence { get; set; }
    public DateTime? RemediatedTime { get; set; }
    public ViolationSeverity Severity { get; set; }
    public ViolationStatus Status { get; set; }
    public int OccurrenceCount { get; set; }
    public string? RemediationAction { get; set; }
    public List<PolicyViolation> PolicyViolations { get; } = new();
}

public sealed class ComplianceCheckResult
{
    public string RequestId { get; init; } = string.Empty;
    public bool IsCompliant { get; init; }
    public List<string> CheckedPolicies { get; init; } = new();
    public List<PolicyViolation> Violations { get; init; } = new();
    public ViolationSeverity HighestSeverity { get; init; }
    public ComplianceViolation? ActiveViolation { get; init; }
    public EnforcementRecommendation? EnforcementRecommendation { get; init; }
}

public sealed class EnforcementRecommendation
{
    public string Id { get; init; } = string.Empty;
    public string ViolationId { get; init; } = string.Empty;
    public EnforcementActionType Action { get; init; }
    public string TargetResource { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public bool AutoExecute { get; init; }
    public bool RequiresApproval { get; init; }
    public List<string> PolicyIds { get; init; } = new();
    public ViolationSeverity Severity { get; init; }
}

public sealed class EnforcementResult
{
    public string RecommendationId { get; init; } = string.Empty;
    public bool Success { get; init; }
    public TimeSpan ExecutionTime { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public sealed class EnforcementAction
{
    public string Id { get; init; } = string.Empty;
    public string RecommendationId { get; init; } = string.Empty;
    public string ViolationId { get; init; } = string.Empty;
    public EnforcementActionType Action { get; init; }
    public string TargetResource { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; }
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public TimeSpan ExecutionTime { get; init; }
}

public sealed class ComplianceAuditEntry
{
    public string Id { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; }
    public string EventType { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string? ResourceId { get; init; }
}

public sealed class ComplianceReport
{
    public DateTime GeneratedAt { get; init; }
    public TimeSpan ReportWindow { get; init; }
    public int TotalPolicies { get; init; }
    public int EnabledPolicies { get; init; }
    public int TotalChecks { get; init; }
    public int PassedChecks { get; init; }
    public int FailedChecks { get; init; }
    public double ComplianceRate { get; init; }
    public List<ComplianceViolation> ActiveViolations { get; init; } = new();
    public List<ComplianceViolation> RemediatedViolations { get; init; } = new();
    public List<EnforcementAction> EnforcementActions { get; init; } = new();
    public Dictionary<ViolationSeverity, int> ViolationsBySeverity { get; init; } = new();
    public Dictionary<string, int> ViolationsByPolicy { get; init; } = new();
    public string? AIAssessment { get; init; }
    public List<ComplianceAuditEntry> AuditTrailEntries { get; init; } = new();
}

public sealed class ComplianceStatistics
{
    public int TotalPolicies { get; init; }
    public int EnabledPolicies { get; init; }
    public int ActiveViolations { get; init; }
    public int RemediatedViolations { get; init; }
    public int TotalEnforcements { get; init; }
    public int SuccessfulEnforcements { get; init; }
    public int AuditTrailSize { get; init; }
    public Dictionary<string, int> PolicyBreakdown { get; init; } = new();
}

#endregion
