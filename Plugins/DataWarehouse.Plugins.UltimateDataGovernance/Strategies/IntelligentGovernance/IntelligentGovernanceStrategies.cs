using System.Text.RegularExpressions;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataGovernance.Strategies.IntelligentGovernance;

#region Strategy 1: PolicyRecommendationStrategy

/// <summary>
/// AI-powered policy recommendation engine that analyzes data characteristics,
/// sensitivity classifications, and compliance requirements to automatically
/// recommend appropriate governance policies.
/// </summary>
/// <remarks>
/// T146.B5.1 — Registers data profiles and policy templates, then computes
/// scored recommendations based on sensitivity overlap, framework overlap,
/// and risk score weighting. Seeded with 5 default policy templates covering
/// encryption-at-rest, access-logging, data-masking, retention-limit, and
/// cross-border-restriction.
/// </remarks>
public sealed class PolicyRecommendationStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "intelligent-policy-recommendation";
    public override string DisplayName => "AI Policy Recommendation";
    public override GovernanceCategory Category => GovernanceCategory.PolicyManagement;
    public override DataGovernanceCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsRealTime = true,
        SupportsAudit = true,
        SupportsVersioning = true
    };
    public override string SemanticDescription =>
        "AI-powered policy recommendation engine that analyzes data characteristics, sensitivity classifications, and compliance requirements to automatically recommend appropriate governance policies.";
    public override string[] Tags => ["ai", "recommendation", "policy", "automated-governance", "industry-first"];

    private readonly BoundedDictionary<string, DataProfile> _dataProfiles = new BoundedDictionary<string, DataProfile>(1000);
    private readonly BoundedDictionary<string, PolicyTemplate> _policyTemplates = new BoundedDictionary<string, PolicyTemplate>(1000);

    /// <summary>
    /// Data profile describing a data asset's characteristics for policy matching.
    /// </summary>
    public sealed record DataProfile(
        string DataId,
        string DataType,
        IReadOnlyList<string> SensitivityLabels,
        IReadOnlyList<string> ComplianceFrameworks,
        double RiskScore,
        Dictionary<string, string> Attributes);

    /// <summary>
    /// Reusable policy template that can be recommended for matching data profiles.
    /// </summary>
    public sealed record PolicyTemplate(
        string TemplateId,
        string PolicyName,
        string Description,
        IReadOnlyList<string> ApplicableSensitivities,
        IReadOnlyList<string> ApplicableFrameworks,
        int Priority);

    /// <summary>
    /// A single policy recommendation with confidence score and rationale.
    /// </summary>
    public sealed record PolicyRecommendation(
        string TemplateId,
        string PolicyName,
        string Reason,
        double Confidence,
        int Priority);

    /// <summary>
    /// Report containing all policy recommendations for a data asset.
    /// </summary>
    public sealed record RecommendationReport(
        string DataId,
        IReadOnlyList<PolicyRecommendation> Recommendations,
        DateTimeOffset GeneratedAt);

    /// <summary>
    /// Initializes a new instance with seeded default policy templates.
    /// </summary>
    public PolicyRecommendationStrategy()
    {
        RegisterPolicyTemplate(
            "encryption-at-rest",
            "Encryption at Rest",
            "Encrypt data at rest using AES-256-GCM or equivalent cipher",
            new[] { "pii", "phi", "pci" },
            new[] { "gdpr", "hipaa", "pci-dss" },
            90);

        RegisterPolicyTemplate(
            "access-logging",
            "Access Logging",
            "Log all data access events with user identity, timestamp, and operation type",
            new[] { "pii", "phi", "pci", "confidential", "internal" },
            new[] { "gdpr", "hipaa", "pci-dss", "sox", "ccpa" },
            80);

        RegisterPolicyTemplate(
            "data-masking",
            "Data Masking",
            "Apply dynamic data masking to sensitive fields in non-production environments",
            new[] { "pii", "phi" },
            new string[0],
            70);

        RegisterPolicyTemplate(
            "retention-limit",
            "Retention Limit",
            "Enforce maximum retention periods with automated purge scheduling",
            new string[0],
            new[] { "gdpr", "ccpa" },
            60);

        RegisterPolicyTemplate(
            "cross-border-restriction",
            "Cross-Border Restriction",
            "Restrict data transfer across jurisdictional boundaries requiring adequacy decisions",
            new string[0],
            new[] { "gdpr", "pipl" },
            50);
    }

    /// <summary>
    /// Registers a data profile describing a data asset's characteristics.
    /// </summary>
    /// <param name="dataId">Unique identifier for the data asset.</param>
    /// <param name="dataType">Type of data (e.g., "customer-records", "financial-transactions").</param>
    /// <param name="sensitivityLabels">Sensitivity labels assigned to the data.</param>
    /// <param name="complianceFrameworks">Compliance frameworks applicable to the data.</param>
    /// <param name="riskScore">Risk score between 0.0 and 1.0.</param>
    /// <param name="attributes">Optional additional attributes.</param>
    public void RegisterDataProfile(
        string dataId,
        string dataType,
        IReadOnlyList<string> sensitivityLabels,
        IReadOnlyList<string> complianceFrameworks,
        double riskScore,
        Dictionary<string, string>? attributes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataId);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataType);
        ArgumentNullException.ThrowIfNull(sensitivityLabels);
        ArgumentNullException.ThrowIfNull(complianceFrameworks);

        _dataProfiles[dataId] = new DataProfile(
            dataId,
            dataType,
            sensitivityLabels,
            complianceFrameworks,
            Math.Clamp(riskScore, 0.0, 1.0),
            attributes ?? new Dictionary<string, string>());
    }

    /// <summary>
    /// Registers a reusable policy template for recommendation matching.
    /// </summary>
    /// <param name="templateId">Unique template identifier.</param>
    /// <param name="policyName">Human-readable policy name.</param>
    /// <param name="description">Description of what the policy enforces.</param>
    /// <param name="applicableSensitivities">Sensitivity levels this policy applies to.</param>
    /// <param name="applicableFrameworks">Compliance frameworks this policy supports.</param>
    /// <param name="priority">Priority for sorting (higher = more important).</param>
    public void RegisterPolicyTemplate(
        string templateId,
        string policyName,
        string description,
        IReadOnlyList<string> applicableSensitivities,
        IReadOnlyList<string> applicableFrameworks,
        int priority = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        ArgumentNullException.ThrowIfNull(applicableSensitivities);
        ArgumentNullException.ThrowIfNull(applicableFrameworks);

        _policyTemplates[templateId] = new PolicyTemplate(
            templateId,
            policyName,
            description,
            applicableSensitivities,
            applicableFrameworks,
            priority);
    }

    /// <summary>
    /// Recommends governance policies for a data asset based on its profile,
    /// matching against registered policy templates using sensitivity overlap,
    /// framework overlap, and risk score weighting.
    /// </summary>
    /// <param name="dataId">Identifier of the data asset to analyze.</param>
    /// <returns>Recommendation report sorted by priority descending then confidence descending.</returns>
    public RecommendationReport RecommendPolicies(string dataId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataId);

        if (!_dataProfiles.TryGetValue(dataId, out var profile))
            throw new InvalidOperationException($"Data profile not found for '{dataId}'. Register a profile first.");

        var recommendations = new List<PolicyRecommendation>();

        foreach (var template in _policyTemplates.Values)
        {
            int sensitivityOverlap = profile.SensitivityLabels
                .Count(s => template.ApplicableSensitivities.Contains(s, StringComparer.OrdinalIgnoreCase));
            int frameworkOverlap = profile.ComplianceFrameworks
                .Count(f => template.ApplicableFrameworks.Contains(f, StringComparer.OrdinalIgnoreCase));

            double sensitivityScore = (double)sensitivityOverlap / Math.Max(1, template.ApplicableSensitivities.Count) * 0.5;
            double frameworkScore = (double)frameworkOverlap / Math.Max(1, template.ApplicableFrameworks.Count) * 0.3;
            double riskBonus = profile.RiskScore > 0.7 ? 0.2 : profile.RiskScore > 0.4 ? 0.1 : 0.0;

            double matchScore = sensitivityScore + frameworkScore + riskBonus;

            if (matchScore > 0.3)
            {
                var matchedSensitivities = profile.SensitivityLabels
                    .Where(s => template.ApplicableSensitivities.Contains(s, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                var matchedFrameworks = profile.ComplianceFrameworks
                    .Where(f => template.ApplicableFrameworks.Contains(f, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                string reason = $"Matched sensitivities: [{string.Join(", ", matchedSensitivities)}]; " +
                                $"Matched frameworks: [{string.Join(", ", matchedFrameworks)}]; " +
                                $"Risk score: {profile.RiskScore:F2}";

                recommendations.Add(new PolicyRecommendation(
                    template.TemplateId,
                    template.PolicyName,
                    reason,
                    matchScore,
                    template.Priority));
            }
        }

        var sorted = recommendations
            .OrderByDescending(r => r.Priority)
            .ThenByDescending(r => r.Confidence)
            .ToList();

        return new RecommendationReport(dataId, sorted.AsReadOnly(), DateTimeOffset.UtcNow);
    }
}

#endregion

#region Strategy 2: ComplianceGapDetectorStrategy

/// <summary>
/// Automatically identifies compliance gaps by comparing actual data handling
/// practices against registered regulatory framework requirements, highlighting
/// areas needing remediation.
/// </summary>
/// <remarks>
/// T146.B5.2 — Registers compliance frameworks with typed requirements and
/// data handling practices with implemented controls, then compares to identify
/// gaps. Seeded with GDPR (5 requirements), HIPAA (5 requirements), and
/// PCI-DSS (4 requirements) for a total of 14 default requirements.
/// </remarks>
public sealed class ComplianceGapDetectorStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "intelligent-compliance-gap";
    public override string DisplayName => "Compliance Gap Detector";
    public override GovernanceCategory Category => GovernanceCategory.RegulatoryCompliance;
    public override DataGovernanceCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsRealTime = false,
        SupportsAudit = true,
        SupportsVersioning = true
    };
    public override string SemanticDescription =>
        "Automatically identifies compliance gaps by comparing actual data handling practices against registered regulatory framework requirements, highlighting areas needing remediation.";
    public override string[] Tags => ["compliance-gap", "regulatory", "remediation", "audit", "industry-first"];

    private readonly BoundedDictionary<string, FrameworkRequirements> _frameworks = new BoundedDictionary<string, FrameworkRequirements>(1000);
    private readonly BoundedDictionary<string, DataHandlingPractice> _practices = new BoundedDictionary<string, DataHandlingPractice>(1000);

    /// <summary>
    /// A single compliance requirement within a regulatory framework.
    /// </summary>
    public sealed record Requirement(
        string RequirementId,
        string Description,
        string Category,
        bool IsMandatory);

    /// <summary>
    /// A regulatory framework with its set of compliance requirements.
    /// </summary>
    public sealed record FrameworkRequirements(
        string FrameworkId,
        string Name,
        BoundedDictionary<string, Requirement> Requirements);

    /// <summary>
    /// The set of controls implemented for a specific data asset.
    /// </summary>
    public sealed record DataHandlingPractice(
        string DataId,
        HashSet<string> ImplementedControls);

    /// <summary>
    /// A single compliance gap where a requirement is not satisfied.
    /// </summary>
    public sealed record ComplianceGap(
        string FrameworkId,
        string RequirementId,
        string Description,
        string Category,
        bool IsMandatory,
        string Severity);

    /// <summary>
    /// Gap analysis report for a data asset against a specific framework.
    /// </summary>
    public sealed record GapReport(
        string DataId,
        IReadOnlyList<ComplianceGap> Gaps,
        int TotalRequirements,
        int SatisfiedRequirements,
        double ComplianceScore,
        DateTimeOffset AnalyzedAt);

    /// <summary>
    /// Initializes a new instance with seeded regulatory frameworks (GDPR, HIPAA, PCI-DSS).
    /// </summary>
    public ComplianceGapDetectorStrategy()
    {
        // GDPR - 5 requirements
        RegisterFramework("gdpr", "General Data Protection Regulation", new Dictionary<string, Requirement>
        {
            ["consent-management"] = new Requirement("consent-management", "Obtain and record lawful basis for processing personal data", "Lawful Basis", true),
            ["data-minimization"] = new Requirement("data-minimization", "Collect only data that is adequate, relevant, and limited to processing purpose", "Data Principles", true),
            ["right-to-erasure"] = new Requirement("right-to-erasure", "Enable data subjects to request deletion of their personal data", "Data Subject Rights", true),
            ["breach-notification"] = new Requirement("breach-notification", "Notify supervisory authority within 72 hours of discovering a personal data breach", "Breach Response", true),
            ["dpa-appointment"] = new Requirement("dpa-appointment", "Appoint a Data Protection Officer where required by Article 37", "Organizational", false)
        });

        // HIPAA - 5 requirements
        RegisterFramework("hipaa", "Health Insurance Portability and Accountability Act", new Dictionary<string, Requirement>
        {
            ["access-control"] = new Requirement("access-control", "Implement technical policies to allow access only to authorized persons", "Technical Safeguards", true),
            ["audit-logging"] = new Requirement("audit-logging", "Record and examine activity in information systems containing ePHI", "Technical Safeguards", true),
            ["encryption-transit"] = new Requirement("encryption-transit", "Encrypt ePHI during electronic transmission over open networks", "Technical Safeguards", true),
            ["encryption-rest"] = new Requirement("encryption-rest", "Encrypt ePHI at rest using addressable encryption specification", "Technical Safeguards", false),
            ["minimum-necessary"] = new Requirement("minimum-necessary", "Limit PHI use, disclosure, and requests to the minimum necessary", "Privacy Rule", true)
        });

        // PCI-DSS - 4 requirements
        RegisterFramework("pci-dss", "Payment Card Industry Data Security Standard", new Dictionary<string, Requirement>
        {
            ["encryption-cardholder"] = new Requirement("encryption-cardholder", "Render PAN unreadable anywhere it is stored using strong cryptography", "Protect Stored Data", true),
            ["access-restriction"] = new Requirement("access-restriction", "Restrict access to cardholder data by business need to know", "Access Control", true),
            ["logging-monitoring"] = new Requirement("logging-monitoring", "Track and monitor all access to network resources and cardholder data", "Monitoring", true),
            ["vulnerability-scanning"] = new Requirement("vulnerability-scanning", "Regularly test security systems and processes for vulnerabilities", "Testing", true)
        });
    }

    /// <summary>
    /// Registers a regulatory framework with its compliance requirements.
    /// </summary>
    /// <param name="frameworkId">Unique framework identifier (e.g., "gdpr").</param>
    /// <param name="name">Human-readable framework name.</param>
    /// <param name="requirements">Dictionary of requirement ID to requirement definition.</param>
    public void RegisterFramework(string frameworkId, string name, Dictionary<string, Requirement> requirements)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(frameworkId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(requirements);

        var boundedRequirements = new BoundedDictionary<string, Requirement>(1000);
        foreach (var kvp in requirements)
        {
            boundedRequirements[kvp.Key] = kvp.Value;
        }

        _frameworks[frameworkId] = new FrameworkRequirements(
            frameworkId,
            name,
            boundedRequirements);
    }

    /// <summary>
    /// Registers the set of compliance controls implemented for a data asset.
    /// </summary>
    /// <param name="dataId">Unique identifier for the data asset.</param>
    /// <param name="implementedControls">List of requirement IDs that are satisfied.</param>
    public void RegisterPractice(string dataId, IReadOnlyList<string> implementedControls)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataId);
        ArgumentNullException.ThrowIfNull(implementedControls);

        _practices[dataId] = new DataHandlingPractice(
            dataId,
            new HashSet<string>(implementedControls, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Detects compliance gaps for a data asset against a specific regulatory framework.
    /// </summary>
    /// <param name="dataId">Identifier of the data asset to analyze.</param>
    /// <param name="frameworkId">Identifier of the regulatory framework to check against.</param>
    /// <returns>Gap report with compliance score and list of unsatisfied requirements.</returns>
    public GapReport DetectGaps(string dataId, string frameworkId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataId);
        ArgumentException.ThrowIfNullOrWhiteSpace(frameworkId);

        if (!_frameworks.TryGetValue(frameworkId, out var framework))
            throw new InvalidOperationException($"Framework '{frameworkId}' not registered.");

        var implementedControls = _practices.TryGetValue(dataId, out var practice)
            ? practice.ImplementedControls
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var gaps = new List<ComplianceGap>();
        int satisfiedCount = 0;
        int totalCount = framework.Requirements.Count;

        foreach (var kvp in framework.Requirements)
        {
            if (implementedControls.Contains(kvp.Key))
            {
                satisfiedCount++;
            }
            else
            {
                string severity = kvp.Value.IsMandatory ? "Critical" : "Warning";
                gaps.Add(new ComplianceGap(
                    frameworkId,
                    kvp.Value.RequirementId,
                    kvp.Value.Description,
                    kvp.Value.Category,
                    kvp.Value.IsMandatory,
                    severity));
            }
        }

        double complianceScore = totalCount > 0 ? (double)satisfiedCount / totalCount : 1.0;

        return new GapReport(dataId, gaps.AsReadOnly(), totalCount, satisfiedCount, complianceScore, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Detects compliance gaps for a data asset against all registered regulatory frameworks.
    /// </summary>
    /// <param name="dataId">Identifier of the data asset to analyze.</param>
    /// <returns>List of gap reports, one per registered framework.</returns>
    public IReadOnlyList<GapReport> DetectAllGaps(string dataId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataId);

        var reports = new List<GapReport>();
        foreach (var frameworkId in _frameworks.Keys)
        {
            reports.Add(DetectGaps(dataId, frameworkId));
        }
        return reports.AsReadOnly();
    }
}

#endregion

#region Strategy 3: SensitivityClassifierStrategy

/// <summary>
/// Automatically classifies data sensitivity levels using regex pattern detection
/// for PII, PHI, PCI, and confidential data, with configurable classification
/// rules and confidence scoring.
/// </summary>
/// <remarks>
/// T146.B5.3 — Uses compiled regex patterns to scan content for sensitive data
/// indicators across multiple sensitivity levels (public, internal, confidential,
/// restricted, top-secret). Seeded with default rules for email, SSN, credit card,
/// phone, date of birth, medical codes, salary keywords, internal IDs, and IP
/// addresses. Supports both free-text and column-based classification.
/// </remarks>
public sealed class SensitivityClassifierStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "intelligent-sensitivity-classifier";
    public override string DisplayName => "Sensitivity Auto-Classifier";
    public override GovernanceCategory Category => GovernanceCategory.DataClassification;
    public override DataGovernanceCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsRealTime = true,
        SupportsAudit = true,
        SupportsVersioning = false
    };
    public override string SemanticDescription =>
        "Automatically classifies data sensitivity levels using regex pattern detection for PII, PHI, PCI, and confidential data, with configurable classification rules and confidence scoring.";
    public override string[] Tags => ["sensitivity", "classification", "pii-detection", "auto-classify", "industry-first"];

    private readonly BoundedDictionary<string, List<ClassificationRule>> _rules = new BoundedDictionary<string, List<ClassificationRule>>(1000);

    /// <summary>
    /// Classification rule with a compiled regex pattern for detecting sensitive data.
    /// </summary>
    public sealed record ClassificationRule(
        string RuleId,
        string SensitivityLevel,
        Regex CompiledPattern,
        string Description,
        double Weight);

    /// <summary>
    /// Result of classifying a data asset or content block.
    /// </summary>
    public sealed record ClassificationResult(
        string DataId,
        string OverallSensitivity,
        IReadOnlyList<DetectedPattern> Detections,
        double ConfidenceScore,
        DateTimeOffset ClassifiedAt);

    /// <summary>
    /// A single detected pattern match within classified content.
    /// </summary>
    public sealed record DetectedPattern(
        string RuleId,
        string SensitivityLevel,
        string MatchedValue,
        int MatchCount,
        string Description);

    // Sensitivity level ordering for comparison
    private static readonly Dictionary<string, int> SensitivityOrder = new(StringComparer.OrdinalIgnoreCase)
    {
        ["public"] = 0,
        ["internal"] = 1,
        ["confidential"] = 2,
        ["restricted"] = 3,
        ["top-secret"] = 4
    };

    // Pre-compiled regex patterns for default rules
    private static readonly Regex EmailPattern = new(
        @"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b",
        RegexOptions.Compiled);

    private static readonly Regex SsnPattern = new(
        @"\b\d{3}-\d{2}-\d{4}\b",
        RegexOptions.Compiled);

    private static readonly Regex CreditCardPattern = new(
        @"\b\d{4}[\s\-]?\d{4}[\s\-]?\d{4}[\s\-]?\d{4}\b",
        RegexOptions.Compiled);

    private static readonly Regex PhonePattern = new(
        @"\b\d{3}[\-.]?\d{3}[\-.]?\d{4}\b",
        RegexOptions.Compiled);

    private static readonly Regex DateOfBirthPattern = new(
        @"\b\d{4}-\d{2}-\d{2}\b",
        RegexOptions.Compiled);

    private static readonly Regex MedicalCodePattern = new(
        @"\b[A-Z]\d{2}\.?\d{0,2}\b",
        RegexOptions.Compiled);

    private static readonly Regex SalaryKeywordPattern = new(
        @"\b(?:salary|compensation|wage|bonus)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex InternalIdPattern = new(
        @"\b(?:emp|usr|cust)[\-_]?\d{4,}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex IpAddressPattern = new(
        @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b",
        RegexOptions.Compiled);

    /// <summary>
    /// Initializes a new instance with seeded default classification rules for
    /// restricted, confidential, and internal sensitivity levels.
    /// </summary>
    public SensitivityClassifierStrategy()
    {
        // Restricted level rules (highest non-top-secret)
        AddRuleInternal("email-detect", "restricted", EmailPattern,
            "Email address pattern (PII)", 2.0);
        AddRuleInternal("ssn-detect", "restricted", SsnPattern,
            "Social Security Number pattern (PII)", 3.0);
        AddRuleInternal("credit-card-detect", "restricted", CreditCardPattern,
            "Credit card number pattern (PCI)", 3.0);
        AddRuleInternal("phone-detect", "restricted", PhonePattern,
            "Phone number pattern (PII)", 1.5);

        // Confidential level rules
        AddRuleInternal("dob-detect", "confidential", DateOfBirthPattern,
            "Date of birth pattern (PHI)", 1.0);
        AddRuleInternal("medical-code-detect", "confidential", MedicalCodePattern,
            "ICD-10 medical code pattern (PHI)", 2.0);
        AddRuleInternal("salary-detect", "confidential", SalaryKeywordPattern,
            "Salary/compensation keyword (confidential business)", 1.5);

        // Internal level rules
        AddRuleInternal("internal-id-detect", "internal", InternalIdPattern,
            "Internal identifier pattern (employee/user/customer ID)", 0.5);
        AddRuleInternal("ip-address-detect", "internal", IpAddressPattern,
            "IP address pattern (infrastructure)", 1.0);
    }

    private void AddRuleInternal(string ruleId, string sensitivityLevel, Regex compiledPattern, string description, double weight)
    {
        var rule = new ClassificationRule(ruleId, sensitivityLevel, compiledPattern, description, weight);
        _rules.AddOrUpdate(
            sensitivityLevel,
            _ => new List<ClassificationRule> { rule },
            (_, existing) => { lock (existing) { existing.Add(rule); } return existing; });
    }

    /// <summary>
    /// Adds a custom classification rule with a regex pattern.
    /// </summary>
    /// <param name="ruleId">Unique rule identifier.</param>
    /// <param name="sensitivityLevel">Sensitivity level this rule classifies to.</param>
    /// <param name="regexPattern">Regex pattern string for matching.</param>
    /// <param name="description">Description of what the pattern detects.</param>
    /// <param name="weight">Weight for confidence scoring (default 1.0).</param>
    public void AddRule(string ruleId, string sensitivityLevel, string regexPattern, string description, double weight = 1.0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sensitivityLevel);
        ArgumentException.ThrowIfNullOrWhiteSpace(regexPattern);

        var compiled = new Regex(regexPattern, RegexOptions.Compiled);
        AddRuleInternal(ruleId, sensitivityLevel, compiled, description, weight);
    }

    /// <summary>
    /// Classifies content by scanning for sensitive data patterns across all registered rules.
    /// </summary>
    /// <param name="dataId">Identifier for the data being classified.</param>
    /// <param name="content">Text content to scan for sensitive patterns.</param>
    /// <returns>Classification result with overall sensitivity, detections, and confidence score.</returns>
    public ClassificationResult Classify(string dataId, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataId);
        ArgumentNullException.ThrowIfNull(content);

        var detections = new List<DetectedPattern>();
        double totalMatchWeight = 0.0;
        string highestSensitivity = "public";
        int highestOrder = -1;

        foreach (var kvp in _rules)
        {
            string sensitivityLevel = kvp.Key;
            foreach (var rule in kvp.Value)
            {
                var matches = rule.CompiledPattern.Matches(content);
                int matchCount = matches.Count;

                if (matchCount > 0)
                {
                    string firstMatch = matches[0].Value;
                    detections.Add(new DetectedPattern(
                        rule.RuleId,
                        rule.SensitivityLevel,
                        firstMatch,
                        matchCount,
                        rule.Description));

                    totalMatchWeight += matchCount * rule.Weight;

                    if (SensitivityOrder.TryGetValue(sensitivityLevel, out int order) && order > highestOrder)
                    {
                        highestOrder = order;
                        highestSensitivity = sensitivityLevel;
                    }
                }
            }
        }

        double confidenceScore = Math.Min(1.0, totalMatchWeight / 10.0);

        return new ClassificationResult(
            dataId,
            highestSensitivity,
            detections.AsReadOnly(),
            confidenceScore,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Classifies database columns by analyzing sample values from each column.
    /// </summary>
    /// <param name="dataId">Identifier for the dataset being classified.</param>
    /// <param name="columnSamples">Dictionary of column name to sample values.</param>
    /// <returns>Dictionary of column name to classification result.</returns>
    public Dictionary<string, ClassificationResult> ClassifyColumns(string dataId, Dictionary<string, string[]> columnSamples)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataId);
        ArgumentNullException.ThrowIfNull(columnSamples);

        var results = new Dictionary<string, ClassificationResult>();

        foreach (var kvp in columnSamples)
        {
            string columnName = kvp.Key;
            string concatenated = string.Join(" ", kvp.Value);
            string columnDataId = $"{dataId}.{columnName}";
            results[columnName] = Classify(columnDataId, concatenated);
        }

        return results;
    }
}

#endregion

#region Strategy 4: RetentionOptimizerStrategy

/// <summary>
/// Optimizes data retention periods based on value scoring (access frequency,
/// business criticality), regulatory minimums, and storage cost analysis to
/// recommend when data should be archived or deleted.
/// </summary>
/// <remarks>
/// T146.B5.4 — Computes a value score from access frequency (30%), recency (30%),
/// and business criticality (40%), then determines optimal retention by combining
/// the value score with regulatory minimum requirements. Recommends retain, archive,
/// or delete actions with cost savings estimates. Seeded with GDPR (3 years),
/// HIPAA (6 years), SOX (7 years), and PCI-DSS (1 year) regulatory minimums.
/// </remarks>
public sealed class RetentionOptimizerStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "intelligent-retention-optimizer";
    public override string DisplayName => "Retention Optimizer";
    public override GovernanceCategory Category => GovernanceCategory.RetentionManagement;
    public override DataGovernanceCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsRealTime = false,
        SupportsAudit = true,
        SupportsVersioning = true
    };
    public override string SemanticDescription =>
        "Optimizes data retention periods based on value scoring (access frequency, business criticality), regulatory minimums, and storage cost analysis to recommend when data should be archived or deleted.";
    public override string[] Tags => ["retention", "optimization", "cost-analysis", "lifecycle", "industry-first"];

    private readonly BoundedDictionary<string, DataRetentionProfile> _profiles = new BoundedDictionary<string, DataRetentionProfile>(1000);
    private readonly BoundedDictionary<string, RegulatoryMinimum> _regulatoryMinimums = new BoundedDictionary<string, RegulatoryMinimum>(1000);

    /// <summary>
    /// Profile describing a data asset's retention-relevant characteristics.
    /// </summary>
    public sealed record DataRetentionProfile(
        string DataId,
        DateTimeOffset CreatedAt,
        DateTimeOffset LastAccessed,
        long AccessCount,
        double BusinessCriticalityScore,
        IReadOnlyList<string> ComplianceFrameworks,
        double StorageCostPerMonthUsd);

    /// <summary>
    /// Regulatory minimum retention period for a compliance framework.
    /// </summary>
    public sealed record RegulatoryMinimum(
        string FrameworkId,
        TimeSpan MinRetention,
        string Reason);

    /// <summary>
    /// Retention optimization recommendation for a data asset.
    /// </summary>
    public sealed record RetentionRecommendation(
        string DataId,
        TimeSpan RecommendedRetention,
        TimeSpan RegulatoryMinimum,
        string Action,
        double ValueScore,
        double CostSavingsPerMonthUsd,
        string Rationale);

    /// <summary>
    /// Initializes a new instance with seeded regulatory minimum retention periods.
    /// </summary>
    public RetentionOptimizerStrategy()
    {
        RegisterRegulatoryMinimum("gdpr", TimeSpan.FromDays(3 * 365), "GDPR data minimization principle");
        RegisterRegulatoryMinimum("hipaa", TimeSpan.FromDays(6 * 365), "HIPAA medical records retention");
        RegisterRegulatoryMinimum("sox", TimeSpan.FromDays(7 * 365), "SOX financial records");
        RegisterRegulatoryMinimum("pci-dss", TimeSpan.FromDays(1 * 365), "PCI DSS audit log retention");
    }

    /// <summary>
    /// Registers a data retention profile for analysis.
    /// </summary>
    /// <param name="dataId">Unique identifier for the data asset.</param>
    /// <param name="createdAt">When the data was created.</param>
    /// <param name="lastAccessed">When the data was last accessed.</param>
    /// <param name="accessCount">Total number of access events.</param>
    /// <param name="businessCriticality">Business criticality score between 0.0 and 1.0.</param>
    /// <param name="frameworks">Compliance frameworks applicable to this data.</param>
    /// <param name="storageCostPerMonth">Monthly storage cost in USD.</param>
    public void RegisterProfile(
        string dataId,
        DateTimeOffset createdAt,
        DateTimeOffset lastAccessed,
        long accessCount,
        double businessCriticality,
        IReadOnlyList<string> frameworks,
        double storageCostPerMonth)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataId);
        ArgumentNullException.ThrowIfNull(frameworks);

        _profiles[dataId] = new DataRetentionProfile(
            dataId,
            createdAt,
            lastAccessed,
            accessCount,
            Math.Clamp(businessCriticality, 0.0, 1.0),
            frameworks,
            Math.Max(0.0, storageCostPerMonth));
    }

    /// <summary>
    /// Registers a regulatory minimum retention period for a compliance framework.
    /// </summary>
    /// <param name="frameworkId">Framework identifier (e.g., "gdpr").</param>
    /// <param name="minRetention">Minimum retention duration.</param>
    /// <param name="reason">Regulatory reason for the minimum.</param>
    public void RegisterRegulatoryMinimum(string frameworkId, TimeSpan minRetention, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(frameworkId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        _regulatoryMinimums[frameworkId] = new RegulatoryMinimum(frameworkId, minRetention, reason);
    }

    /// <summary>
    /// Optimizes retention for a data asset by computing value score, applying
    /// regulatory minimums, and recommending retain/archive/delete actions with
    /// cost savings estimates.
    /// </summary>
    /// <param name="dataId">Identifier of the data asset to optimize.</param>
    /// <returns>Retention recommendation with action, rationale, and cost savings.</returns>
    public RetentionRecommendation OptimizeRetention(string dataId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataId);

        if (!_profiles.TryGetValue(dataId, out var profile))
            throw new InvalidOperationException($"Retention profile not found for '{dataId}'. Register a profile first.");

        // Compute value score
        double accessFrequencyScore = Math.Min(1.0, profile.AccessCount / 1000.0) * 0.3;
        double daysInactive = (DateTimeOffset.UtcNow - profile.LastAccessed).TotalDays;
        double recencyScore = Math.Max(0.0, 1.0 - (daysInactive / 365.0)) * 0.3;
        double criticalityScore = profile.BusinessCriticalityScore * 0.4;
        double valueScore = accessFrequencyScore + recencyScore + criticalityScore;

        // Compute regulatory minimum as max across all applicable frameworks
        TimeSpan regulatoryMin = TimeSpan.Zero;
        var applicableReasons = new List<string>();
        foreach (var framework in profile.ComplianceFrameworks)
        {
            if (_regulatoryMinimums.TryGetValue(framework, out var minimum) && minimum.MinRetention > regulatoryMin)
            {
                regulatoryMin = minimum.MinRetention;
                applicableReasons.Add($"{minimum.FrameworkId}: {minimum.Reason}");
            }
        }

        // Determine recommended retention based on value score
        TimeSpan baseRetention;
        if (valueScore > 0.7)
            baseRetention = TimeSpan.FromDays(7 * 365);
        else if (valueScore > 0.4)
            baseRetention = TimeSpan.FromDays(3 * 365);
        else if (valueScore > 0.1)
            baseRetention = TimeSpan.FromDays(1 * 365);
        else
            baseRetention = TimeSpan.Zero;

        TimeSpan recommendedRetention = baseRetention > regulatoryMin ? baseRetention : regulatoryMin;

        // Determine action based on current age vs recommended retention
        TimeSpan currentAge = DateTimeOffset.UtcNow - profile.CreatedAt;
        string action;
        double costSavings = 0.0;

        if (currentAge > recommendedRetention)
        {
            if (currentAge > regulatoryMin && regulatoryMin > TimeSpan.Zero)
            {
                action = "delete";
                double monthsSaved = (currentAge - recommendedRetention).TotalDays / 30.0;
                costSavings = profile.StorageCostPerMonthUsd * Math.Max(0, monthsSaved);
            }
            else
            {
                action = "archive";
                // Archiving typically saves 70% of storage cost
                double monthsRemaining = (recommendedRetention - currentAge).TotalDays / 30.0;
                if (monthsRemaining < 0) monthsRemaining = 0;
                costSavings = profile.StorageCostPerMonthUsd * 0.7;
            }
        }
        else
        {
            action = "retain";
        }

        string rationale = $"Value score: {valueScore:F2} (access frequency: {accessFrequencyScore:F2}, " +
                           $"recency: {recencyScore:F2}, criticality: {criticalityScore:F2}). " +
                           $"Regulatory minimum: {regulatoryMin.TotalDays / 365.0:F1} years" +
                           (applicableReasons.Count > 0
                               ? $" ({string.Join("; ", applicableReasons)})"
                               : string.Empty) +
                           $". Recommended action: {action}.";

        return new RetentionRecommendation(
            dataId,
            recommendedRetention,
            regulatoryMin,
            action,
            valueScore,
            costSavings,
            rationale);
    }
}

#endregion
