using System.Collections.Concurrent;
using System.Diagnostics;
using DataWarehouse.SDK.Contracts.IntelligenceAware;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.AiEnhanced;

/// <summary>
/// Compliance framework.
/// </summary>
public enum ComplianceFramework
{
    /// <summary>
    /// General Data Protection Regulation.
    /// </summary>
    GDPR,

    /// <summary>
    /// Health Insurance Portability and Accountability Act.
    /// </summary>
    HIPAA,

    /// <summary>
    /// Payment Card Industry Data Security Standard.
    /// </summary>
    PCI_DSS,

    /// <summary>
    /// Sarbanes-Oxley Act.
    /// </summary>
    SOX,

    /// <summary>
    /// California Consumer Privacy Act.
    /// </summary>
    CCPA,

    /// <summary>
    /// Federal Information Security Management Act.
    /// </summary>
    FISMA,

    /// <summary>
    /// ISO 27001 Information Security.
    /// </summary>
    ISO27001,

    /// <summary>
    /// Custom or internal policy.
    /// </summary>
    Custom
}

/// <summary>
/// Sensitivity level of data.
/// </summary>
public enum SensitivityLevel
{
    /// <summary>
    /// Public data.
    /// </summary>
    Public = 0,

    /// <summary>
    /// Internal use only.
    /// </summary>
    Internal = 1,

    /// <summary>
    /// Confidential data.
    /// </summary>
    Confidential = 2,

    /// <summary>
    /// Highly sensitive/restricted.
    /// </summary>
    Restricted = 3,

    /// <summary>
    /// Top secret/critical.
    /// </summary>
    Critical = 4
}

/// <summary>
/// Result of compliance analysis.
/// </summary>
public sealed class ComplianceAnalysis
{
    /// <summary>
    /// Object identifier.
    /// </summary>
    public required string ObjectId { get; init; }

    /// <summary>
    /// Detected sensitivity level.
    /// </summary>
    public SensitivityLevel SensitivityLevel { get; init; }

    /// <summary>
    /// Applicable compliance frameworks.
    /// </summary>
    public IReadOnlyList<ComplianceFramework> ApplicableFrameworks { get; init; } = Array.Empty<ComplianceFramework>();

    /// <summary>
    /// Whether PII was detected.
    /// </summary>
    public bool ContainsPII { get; init; }

    /// <summary>
    /// Types of PII detected.
    /// </summary>
    public IReadOnlyList<string> PIITypes { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Required retention period.
    /// </summary>
    public TimeSpan? RequiredRetention { get; init; }

    /// <summary>
    /// Required disposal method.
    /// </summary>
    public string? DisposalMethod { get; init; }

    /// <summary>
    /// Required access controls.
    /// </summary>
    public IReadOnlyList<string> RequiredControls { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Compliance violations found.
    /// </summary>
    public IReadOnlyList<ComplianceViolation> Violations { get; init; } = Array.Empty<ComplianceViolation>();

    /// <summary>
    /// Overall compliance score (0.0-1.0).
    /// </summary>
    public double ComplianceScore { get; init; }

    /// <summary>
    /// Confidence in the analysis (0.0-1.0).
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// Whether AI was used for analysis.
    /// </summary>
    public bool UsedAi { get; init; }

    /// <summary>
    /// When analysis was performed.
    /// </summary>
    public DateTime AnalyzedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// A compliance violation.
/// </summary>
public sealed class ComplianceViolation
{
    /// <summary>
    /// Violation type.
    /// </summary>
    public required string ViolationType { get; init; }

    /// <summary>
    /// Description of the violation.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Severity (0.0-1.0).
    /// </summary>
    public double Severity { get; init; }

    /// <summary>
    /// Applicable framework.
    /// </summary>
    public ComplianceFramework? Framework { get; init; }

    /// <summary>
    /// Remediation action.
    /// </summary>
    public string? Remediation { get; init; }
}

/// <summary>
/// Retention policy for data.
/// </summary>
public sealed class RetentionPolicy
{
    /// <summary>
    /// Policy identifier.
    /// </summary>
    public required string PolicyId { get; init; }

    /// <summary>
    /// Policy name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Applicable frameworks.
    /// </summary>
    public ComplianceFramework[] Frameworks { get; init; } = Array.Empty<ComplianceFramework>();

    /// <summary>
    /// Minimum retention period.
    /// </summary>
    public TimeSpan MinRetention { get; init; }

    /// <summary>
    /// Maximum retention period (if applicable).
    /// </summary>
    public TimeSpan? MaxRetention { get; init; }

    /// <summary>
    /// Required disposal method.
    /// </summary>
    public string DisposalMethod { get; init; } = "secure-delete";

    /// <summary>
    /// Whether legal hold can override.
    /// </summary>
    public bool AllowsLegalHold { get; init; } = true;

    /// <summary>
    /// Content types this policy applies to.
    /// </summary>
    public string[]? ApplicableContentTypes { get; init; }

    /// <summary>
    /// Data classifications this policy applies to.
    /// </summary>
    public SensitivityLevel[]? ApplicableSensitivityLevels { get; init; }
}

/// <summary>
/// Compliance-aware lifecycle strategy that automatically detects PII,
/// applies retention policies, and generates compliance reports.
/// </summary>
/// <remarks>
/// Features:
/// - AI-powered PII detection
/// - Auto-detection of applicable compliance frameworks
/// - Automatic retention policy application
/// - Compliance violation detection
/// - Audit-ready reporting
/// </remarks>
public sealed class ComplianceAwareLifecycleStrategy : AiEnhancedStrategyBase
{
    private readonly ConcurrentDictionary<string, ComplianceAnalysis> _analysisCache = new();
    private readonly ConcurrentDictionary<string, RetentionPolicy> _policies = new();
    private readonly TimeSpan _cacheTtl;

    /// <summary>
    /// Initializes a new ComplianceAwareLifecycleStrategy.
    /// </summary>
    public ComplianceAwareLifecycleStrategy() : this(TimeSpan.FromHours(24)) { }

    /// <summary>
    /// Initializes with custom cache TTL.
    /// </summary>
    public ComplianceAwareLifecycleStrategy(TimeSpan cacheTtl)
    {
        _cacheTtl = cacheTtl;
        InitializeDefaultPolicies();
    }

    /// <inheritdoc/>
    public override string StrategyId => "ai.compliance-lifecycle";

    /// <inheritdoc/>
    public override string DisplayName => "Compliance-Aware Lifecycle";

    /// <inheritdoc/>
    public override AiEnhancedCategory AiCategory => AiEnhancedCategory.Compliance;

    /// <inheritdoc/>
    public override IntelligenceCapabilities RequiredCapabilities =>
        IntelligenceCapabilities.PIIDetection | IntelligenceCapabilities.ComplianceClassification;

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = false,
        SupportsTTL = true,
        MaxThroughput = 500,
        TypicalLatencyMs = 100.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Compliance-aware lifecycle strategy using AI to detect PII, " +
        "classify data sensitivity, apply retention policies, and ensure regulatory compliance.";

    /// <inheritdoc/>
    public override string[] Tags => ["ai", "compliance", "gdpr", "hipaa", "pii", "retention", "lifecycle"];

    /// <summary>
    /// Registers a retention policy.
    /// </summary>
    public void RegisterPolicy(RetentionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(policy.PolicyId);

        _policies[policy.PolicyId] = policy;
    }

    /// <summary>
    /// Gets all registered policies.
    /// </summary>
    public IReadOnlyList<RetentionPolicy> GetPolicies() => _policies.Values.ToList();

    /// <summary>
    /// Analyzes content for compliance requirements.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="textContent">Text content to analyze.</param>
    /// <param name="contentType">Content type.</param>
    /// <param name="metadata">Additional metadata.</param>
    /// <param name="forceRefresh">Force new analysis.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Compliance analysis result.</returns>
    public async Task<ComplianceAnalysis> AnalyzeAsync(
        string objectId,
        string? textContent,
        string? contentType = null,
        Dictionary<string, object>? metadata = null,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        // Check cache
        if (!forceRefresh && _analysisCache.TryGetValue(objectId, out var cached))
        {
            if (DateTime.UtcNow - cached.AnalyzedAt < _cacheTtl)
            {
                return cached;
            }
        }

        var sw = Stopwatch.StartNew();
        ComplianceAnalysis analysis;

        if (IsAiAvailable && !string.IsNullOrWhiteSpace(textContent))
        {
            analysis = await AnalyzeWithAiAsync(objectId, textContent, contentType, metadata, ct);
            RecordAiOperation(analysis.UsedAi, false, analysis.Confidence, sw.Elapsed.TotalMilliseconds);
        }
        else
        {
            analysis = AnalyzeWithFallback(objectId, textContent, contentType, metadata);
            RecordAiOperation(false, false, 0, sw.Elapsed.TotalMilliseconds);
        }

        _analysisCache[objectId] = analysis;
        return analysis;
    }

    /// <summary>
    /// Gets the applicable retention policy for analyzed content.
    /// </summary>
    /// <param name="analysis">Compliance analysis result.</param>
    /// <returns>Most restrictive applicable policy.</returns>
    public RetentionPolicy? GetApplicablePolicy(ComplianceAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        var applicablePolicies = _policies.Values
            .Where(p => IsPolicyApplicable(p, analysis))
            .OrderByDescending(p => p.MinRetention)
            .ToList();

        return applicablePolicies.FirstOrDefault();
    }

    /// <summary>
    /// Checks if an object can be deleted based on compliance requirements.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="createdAt">Object creation date.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Whether deletion is allowed and reason.</returns>
    public async Task<(bool CanDelete, string Reason)> CanDeleteAsync(
        string objectId,
        DateTime createdAt,
        CancellationToken ct = default)
    {
        if (!_analysisCache.TryGetValue(objectId, out var analysis))
        {
            return (true, "No compliance analysis on record - deletion allowed");
        }

        var policy = GetApplicablePolicy(analysis);
        if (policy == null)
        {
            return (true, "No applicable retention policy - deletion allowed");
        }

        var age = DateTime.UtcNow - createdAt;

        if (age < policy.MinRetention)
        {
            var remaining = policy.MinRetention - age;
            return (false, $"Retention period not met. Must retain for {remaining.TotalDays:F0} more days per {policy.Name}");
        }

        if (policy.MaxRetention.HasValue && age > policy.MaxRetention.Value)
        {
            return (true, $"Maximum retention exceeded - deletion required per {policy.Name}");
        }

        return (true, $"Retention period satisfied - deletion allowed per {policy.Name}");
    }

    /// <summary>
    /// Gets objects that must be deleted based on max retention.
    /// </summary>
    /// <param name="objectCreationDates">Dictionary of object IDs to creation dates.</param>
    /// <returns>Objects requiring deletion.</returns>
    public IReadOnlyList<(string ObjectId, string Reason)> GetObjectsRequiringDeletion(
        IReadOnlyDictionary<string, DateTime> objectCreationDates)
    {
        var results = new List<(string ObjectId, string Reason)>();

        foreach (var (objectId, createdAt) in objectCreationDates)
        {
            if (_analysisCache.TryGetValue(objectId, out var analysis))
            {
                var policy = GetApplicablePolicy(analysis);
                if (policy?.MaxRetention != null)
                {
                    var age = DateTime.UtcNow - createdAt;
                    if (age > policy.MaxRetention.Value)
                    {
                        results.Add((objectId, $"Exceeds max retention of {policy.MaxRetention.Value.TotalDays} days per {policy.Name}"));
                    }
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Generates a compliance report.
    /// </summary>
    /// <returns>Compliance report data.</returns>
    public Dictionary<string, object> GenerateComplianceReport()
    {
        var analyses = _analysisCache.Values.ToList();

        var byFramework = analyses
            .SelectMany(a => a.ApplicableFrameworks.Select(f => new { Framework = f, Analysis = a }))
            .GroupBy(x => x.Framework)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());

        var bySensitivity = analyses
            .GroupBy(a => a.SensitivityLevel)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());

        var piiCount = analyses.Count(a => a.ContainsPII);
        var violationCount = analyses.Sum(a => a.Violations.Count);
        var avgComplianceScore = analyses.Count > 0 ? analyses.Average(a => a.ComplianceScore) : 1.0;

        return new Dictionary<string, object>
        {
            ["GeneratedAt"] = DateTime.UtcNow,
            ["TotalObjectsAnalyzed"] = analyses.Count,
            ["ObjectsWithPII"] = piiCount,
            ["PIIPercentage"] = analyses.Count > 0 ? (double)piiCount / analyses.Count * 100 : 0,
            ["TotalViolations"] = violationCount,
            ["AverageComplianceScore"] = avgComplianceScore,
            ["ByFramework"] = byFramework,
            ["BySensitivity"] = bySensitivity,
            ["HighRiskObjects"] = analyses.Where(a => a.Violations.Any(v => v.Severity > 0.7)).Select(a => a.ObjectId).ToList(),
            ["PolicyCount"] = _policies.Count
        };
    }

    private async Task<ComplianceAnalysis> AnalyzeWithAiAsync(
        string objectId,
        string textContent,
        string? contentType,
        Dictionary<string, object>? metadata,
        CancellationToken ct)
    {
        var piiResult = await RequestPIIDetectionAsync(textContent, DefaultContext, ct);
        var containsPII = piiResult?.ContainsPII ?? false;
        var piiTypes = piiResult?.Items.Select(i => i.Type).Distinct().ToList() ?? new List<string>();

        // Classify sensitivity
        var sensitivityLevels = new[] { "Public", "Internal", "Confidential", "Restricted", "Critical" };
        var classification = await RequestClassificationAsync(textContent, sensitivityLevels, false, DefaultContext, ct);

        var sensitivity = SensitivityLevel.Internal;
        if (classification != null && classification.Length > 0)
        {
            var top = classification.OrderByDescending(c => c.Confidence).First();
            sensitivity = Enum.TryParse<SensitivityLevel>(top.Category, out var parsed) ? parsed : SensitivityLevel.Internal;
        }

        // Determine frameworks based on PII types
        var frameworks = DetermineFrameworks(piiTypes, metadata);
        var policy = GetMostRestrictivePolicy(frameworks, sensitivity);

        var violations = DetectViolations(containsPII, sensitivity, frameworks, metadata);

        return new ComplianceAnalysis
        {
            ObjectId = objectId,
            SensitivityLevel = sensitivity,
            ApplicableFrameworks = frameworks,
            ContainsPII = containsPII,
            PIITypes = piiTypes,
            RequiredRetention = policy?.MinRetention,
            DisposalMethod = policy?.DisposalMethod ?? "standard",
            RequiredControls = GetRequiredControls(sensitivity, frameworks),
            Violations = violations,
            ComplianceScore = CalculateComplianceScore(violations),
            Confidence = classification?.FirstOrDefault().Confidence ?? 0.5,
            UsedAi = true
        };
    }

    private ComplianceAnalysis AnalyzeWithFallback(
        string objectId,
        string? textContent,
        string? contentType,
        Dictionary<string, object>? metadata)
    {
        var containsPII = false;
        var piiTypes = new List<string>();

        if (!string.IsNullOrWhiteSpace(textContent))
        {
            // Simple pattern matching for common PII
            var lower = textContent.ToLowerInvariant();

            if (System.Text.RegularExpressions.Regex.IsMatch(textContent, @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Z|a-z]{2,}\b"))
            {
                containsPII = true;
                piiTypes.Add("EMAIL");
            }
            if (System.Text.RegularExpressions.Regex.IsMatch(textContent, @"\b\d{3}[-.]?\d{2}[-.]?\d{4}\b"))
            {
                containsPII = true;
                piiTypes.Add("SSN");
            }
            if (System.Text.RegularExpressions.Regex.IsMatch(textContent, @"\b\d{4}[\s-]?\d{4}[\s-]?\d{4}[\s-]?\d{4}\b"))
            {
                containsPII = true;
                piiTypes.Add("CREDIT_CARD");
            }
            if (lower.Contains("patient") || lower.Contains("diagnosis") || lower.Contains("medical"))
            {
                containsPII = true;
                piiTypes.Add("HEALTH_INFO");
            }
        }

        var sensitivity = containsPII ? SensitivityLevel.Confidential : SensitivityLevel.Internal;
        var frameworks = DetermineFrameworks(piiTypes, metadata);

        return new ComplianceAnalysis
        {
            ObjectId = objectId,
            SensitivityLevel = sensitivity,
            ApplicableFrameworks = frameworks,
            ContainsPII = containsPII,
            PIITypes = piiTypes,
            RequiredRetention = GetDefaultRetention(frameworks),
            DisposalMethod = containsPII ? "secure-delete" : "standard",
            RequiredControls = GetRequiredControls(sensitivity, frameworks),
            Violations = Array.Empty<ComplianceViolation>(),
            ComplianceScore = 1.0,
            Confidence = 0.5,
            UsedAi = false
        };
    }

    private static List<ComplianceFramework> DetermineFrameworks(List<string> piiTypes, Dictionary<string, object>? metadata)
    {
        var frameworks = new List<ComplianceFramework>();

        if (piiTypes.Contains("SSN") || piiTypes.Contains("EMAIL") || piiTypes.Contains("NAME"))
        {
            frameworks.Add(ComplianceFramework.GDPR);
            frameworks.Add(ComplianceFramework.CCPA);
        }

        if (piiTypes.Contains("HEALTH_INFO") || piiTypes.Contains("PATIENT_ID"))
        {
            frameworks.Add(ComplianceFramework.HIPAA);
        }

        if (piiTypes.Contains("CREDIT_CARD") || piiTypes.Contains("CVV"))
        {
            frameworks.Add(ComplianceFramework.PCI_DSS);
        }

        if (metadata?.ContainsKey("financial") == true)
        {
            frameworks.Add(ComplianceFramework.SOX);
        }

        return frameworks.Distinct().ToList();
    }

    private RetentionPolicy? GetMostRestrictivePolicy(List<ComplianceFramework> frameworks, SensitivityLevel sensitivity)
    {
        return _policies.Values
            .Where(p => p.Frameworks.Any(f => frameworks.Contains(f)) ||
                       p.ApplicableSensitivityLevels?.Contains(sensitivity) == true)
            .OrderByDescending(p => p.MinRetention)
            .FirstOrDefault();
    }

    private static List<ComplianceViolation> DetectViolations(
        bool containsPII,
        SensitivityLevel sensitivity,
        List<ComplianceFramework> frameworks,
        Dictionary<string, object>? metadata)
    {
        var violations = new List<ComplianceViolation>();

        if (containsPII && metadata?.ContainsKey("encrypted") != true)
        {
            violations.Add(new ComplianceViolation
            {
                ViolationType = "UNENCRYPTED_PII",
                Description = "PII detected in unencrypted content",
                Severity = 0.8,
                Framework = frameworks.FirstOrDefault(),
                Remediation = "Enable encryption at rest"
            });
        }

        if (sensitivity >= SensitivityLevel.Restricted && metadata?.ContainsKey("accessControlled") != true)
        {
            violations.Add(new ComplianceViolation
            {
                ViolationType = "INSUFFICIENT_ACCESS_CONTROL",
                Description = "Restricted data without access controls",
                Severity = 0.7,
                Remediation = "Implement role-based access control"
            });
        }

        return violations;
    }

    private static double CalculateComplianceScore(IReadOnlyList<ComplianceViolation> violations)
    {
        if (violations.Count == 0)
            return 1.0;

        var totalSeverity = violations.Sum(v => v.Severity);
        return Math.Max(0, 1 - totalSeverity / violations.Count);
    }

    private static List<string> GetRequiredControls(SensitivityLevel sensitivity, List<ComplianceFramework> frameworks)
    {
        var controls = new List<string>();

        if (sensitivity >= SensitivityLevel.Confidential)
        {
            controls.Add("encryption-at-rest");
            controls.Add("encryption-in-transit");
        }

        if (sensitivity >= SensitivityLevel.Restricted)
        {
            controls.Add("access-logging");
            controls.Add("role-based-access");
        }

        if (frameworks.Contains(ComplianceFramework.HIPAA))
        {
            controls.Add("audit-trail");
            controls.Add("access-controls");
        }

        if (frameworks.Contains(ComplianceFramework.PCI_DSS))
        {
            controls.Add("strong-encryption");
            controls.Add("key-management");
        }

        return controls.Distinct().ToList();
    }

    private static TimeSpan GetDefaultRetention(List<ComplianceFramework> frameworks)
    {
        if (frameworks.Contains(ComplianceFramework.SOX))
            return TimeSpan.FromDays(7 * 365); // 7 years
        if (frameworks.Contains(ComplianceFramework.HIPAA))
            return TimeSpan.FromDays(6 * 365); // 6 years
        if (frameworks.Contains(ComplianceFramework.GDPR))
            return TimeSpan.FromDays(365); // 1 year default
        if (frameworks.Contains(ComplianceFramework.PCI_DSS))
            return TimeSpan.FromDays(365); // 1 year

        return TimeSpan.FromDays(90); // Default 90 days
    }

    private bool IsPolicyApplicable(RetentionPolicy policy, ComplianceAnalysis analysis)
    {
        if (policy.Frameworks.Length > 0 && !policy.Frameworks.Any(f => analysis.ApplicableFrameworks.Contains(f)))
            return false;

        if (policy.ApplicableSensitivityLevels != null && !policy.ApplicableSensitivityLevels.Contains(analysis.SensitivityLevel))
            return false;

        return true;
    }

    private void InitializeDefaultPolicies()
    {
        RegisterPolicy(new RetentionPolicy
        {
            PolicyId = "gdpr-default",
            Name = "GDPR Default Retention",
            Frameworks = new[] { ComplianceFramework.GDPR },
            MinRetention = TimeSpan.FromDays(365),
            MaxRetention = TimeSpan.FromDays(3 * 365),
            DisposalMethod = "secure-delete"
        });

        RegisterPolicy(new RetentionPolicy
        {
            PolicyId = "hipaa-default",
            Name = "HIPAA Retention",
            Frameworks = new[] { ComplianceFramework.HIPAA },
            MinRetention = TimeSpan.FromDays(6 * 365),
            DisposalMethod = "certified-destruction"
        });

        RegisterPolicy(new RetentionPolicy
        {
            PolicyId = "pci-default",
            Name = "PCI-DSS Retention",
            Frameworks = new[] { ComplianceFramework.PCI_DSS },
            MinRetention = TimeSpan.FromDays(365),
            DisposalMethod = "secure-delete"
        });

        RegisterPolicy(new RetentionPolicy
        {
            PolicyId = "sox-default",
            Name = "SOX Retention",
            Frameworks = new[] { ComplianceFramework.SOX },
            MinRetention = TimeSpan.FromDays(7 * 365),
            DisposalMethod = "certified-destruction"
        });
    }
}
