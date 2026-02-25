using System;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;

namespace DataWarehouse.SDK.Infrastructure.Policy
{
    /// <summary>
    /// Represents a single regulatory requirement that a deployment's policies must satisfy.
    /// Each requirement maps to a specific feature and specifies minimum thresholds for
    /// intensity, cascade strategy, AI autonomy, and custom parameters.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 70: Compliance scoring (PADV-03)")]
    public sealed record RegulatoryRequirement
    {
        /// <summary>
        /// Unique identifier for this requirement (e.g., "HIPAA-ENC-01").
        /// </summary>
        public required string RequirementId { get; init; }

        /// <summary>
        /// Human-readable description of what this requirement mandates.
        /// </summary>
        public required string Description { get; init; }

        /// <summary>
        /// Requirement category (e.g., "encryption", "access_control", "audit").
        /// </summary>
        public required string Category { get; init; }

        /// <summary>
        /// The policy feature identifier this requirement maps to (e.g., "encryption", "audit").
        /// Must match a key in the evaluated policy dictionary.
        /// </summary>
        public required string FeatureId { get; init; }

        /// <summary>
        /// Minimum acceptable intensity level (0-100) for the feature. A policy with
        /// <see cref="FeaturePolicy.IntensityLevel"/> below this value fails this requirement.
        /// </summary>
        public int MinimumIntensity { get; init; }

        /// <summary>
        /// If non-null, the framework mandates this specific cascade strategy.
        /// A policy using a different cascade strategy fails this requirement.
        /// </summary>
        public CascadeStrategy? RequiredCascade { get; init; }

        /// <summary>
        /// If non-null, the maximum AI autonomy level permitted. A policy with
        /// <see cref="FeaturePolicy.AiAutonomy"/> exceeding this value (numerically) fails this requirement.
        /// </summary>
        public AiAutonomyLevel? MaxAiAutonomy { get; init; }

        /// <summary>
        /// Custom parameters that must be present in the policy's <see cref="FeaturePolicy.CustomParameters"/>
        /// with matching values. Null when no custom parameter checks are needed.
        /// </summary>
        public Dictionary<string, string>? RequiredParameters { get; init; }

        /// <summary>
        /// Relative importance of this requirement for scoring (0.0 to 1.0).
        /// Higher weights contribute more to the overall compliance score.
        /// </summary>
        public double Weight { get; init; } = 1.0;

        /// <summary>
        /// Actionable remediation guidance describing what to do if this requirement fails.
        /// </summary>
        public required string Remediation { get; init; }
    }

    /// <summary>
    /// Defines a regulatory compliance template containing all requirements for a specific
    /// framework (HIPAA, GDPR, SOC2, FedRAMP). Templates are evaluated against a set of
    /// feature policies to produce a scored gap analysis report.
    /// <para>
    /// Built-in factory methods provide production-ready requirement sets for the four major
    /// compliance frameworks. Custom templates can be constructed for organization-specific
    /// or industry-specific requirements.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <b>Usage example:</b>
    /// <code>
    /// var hipaa = RegulatoryTemplate.Hipaa();
    /// var scorer = new PolicyComplianceScorer(profile.FeaturePolicies);
    /// var report = scorer.ScoreAgainst(hipaa);
    /// Console.WriteLine($"HIPAA Score: {report.Score} ({report.Grade})");
    /// </code>
    /// </remarks>
    [SdkCompatibility("6.0.0", Notes = "Phase 70: Compliance scoring (PADV-03)")]
    public sealed record RegulatoryTemplate
    {
        /// <summary>
        /// Name of the regulatory framework (e.g., "HIPAA", "GDPR", "SOC2", "FedRAMP").
        /// </summary>
        public required string FrameworkName { get; init; }

        /// <summary>
        /// Version of the framework template (e.g., "2024"). Allows tracking template updates
        /// as regulatory requirements evolve.
        /// </summary>
        public required string FrameworkVersion { get; init; }

        /// <summary>
        /// Human-readable description of the framework and its scope.
        /// </summary>
        public required string Description { get; init; }

        /// <summary>
        /// The ordered list of requirements that constitute this framework's compliance checks.
        /// </summary>
        public required IReadOnlyList<RegulatoryRequirement> Requirements { get; init; }

        /// <summary>
        /// Returns all four built-in regulatory templates (HIPAA, GDPR, SOC2, FedRAMP).
        /// </summary>
        /// <returns>An array containing all built-in templates.</returns>
        public static RegulatoryTemplate[] All() => new[]
        {
            Hipaa(),
            Gdpr(),
            Soc2(),
            FedRamp()
        };

        /// <summary>
        /// Creates the HIPAA (Health Insurance Portability and Accountability Act) compliance template.
        /// Focuses on encryption, audit trails, access control, and data protection requirements
        /// for protected health information (PHI).
        /// </summary>
        /// <returns>A <see cref="RegulatoryTemplate"/> with 10 HIPAA requirements.</returns>
        public static RegulatoryTemplate Hipaa() => new()
        {
            FrameworkName = "HIPAA",
            FrameworkVersion = "2024",
            Description = "Health Insurance Portability and Accountability Act requirements for protected health information (PHI).",
            Requirements = new RegulatoryRequirement[]
            {
                new()
                {
                    RequirementId = "HIPAA-ENC-01",
                    Description = "PHI must be encrypted at rest with strong encryption (intensity >= 80).",
                    Category = "encryption",
                    FeatureId = "encryption",
                    MinimumIntensity = 80,
                    RequiredCascade = CascadeStrategy.MostRestrictive,
                    Weight = 1.0,
                    Remediation = "Set encryption intensity to at least 80 and cascade to MostRestrictive to ensure all hierarchy levels enforce encryption."
                },
                new()
                {
                    RequirementId = "HIPAA-ENC-02",
                    Description = "Encryption configuration changes must not be made by AI without human review.",
                    Category = "encryption",
                    FeatureId = "encryption",
                    MaxAiAutonomy = AiAutonomyLevel.Suggest,
                    Weight = 0.9,
                    Remediation = "Set encryption AiAutonomy to Suggest or ManualOnly to require human approval for encryption changes."
                },
                new()
                {
                    RequirementId = "HIPAA-AUD-01",
                    Description = "Comprehensive audit logging must be enabled at high intensity with enforced cascade.",
                    Category = "audit",
                    FeatureId = "audit",
                    MinimumIntensity = 90,
                    RequiredCascade = CascadeStrategy.Enforce,
                    Weight = 1.0,
                    Remediation = "Set audit intensity to at least 90 with Enforce cascade to guarantee audit coverage at all hierarchy levels."
                },
                new()
                {
                    RequirementId = "HIPAA-ACC-01",
                    Description = "Access control must be configured with high intensity to protect PHI access.",
                    Category = "access_control",
                    FeatureId = "access_control",
                    MinimumIntensity = 80,
                    Weight = 1.0,
                    Remediation = "Set access_control intensity to at least 80 to enforce strong access restrictions on PHI."
                },
                new()
                {
                    RequirementId = "HIPAA-ACC-02",
                    Description = "Access control must cascade using MostRestrictive to prevent privilege escalation.",
                    Category = "access_control",
                    FeatureId = "access_control",
                    RequiredCascade = CascadeStrategy.MostRestrictive,
                    Weight = 0.9,
                    Remediation = "Set access_control cascade to MostRestrictive to ensure child levels cannot weaken parent access restrictions."
                },
                new()
                {
                    RequirementId = "HIPAA-REP-01",
                    Description = "Data replication must be configured to ensure PHI availability and disaster recovery.",
                    Category = "replication",
                    FeatureId = "replication",
                    MinimumIntensity = 60,
                    Weight = 0.7,
                    Remediation = "Set replication intensity to at least 60 to maintain adequate PHI backup and availability."
                },
                new()
                {
                    RequirementId = "HIPAA-GOV-01",
                    Description = "Data governance policies must be active to enforce PHI handling rules.",
                    Category = "governance",
                    FeatureId = "governance",
                    MinimumIntensity = 70,
                    RequiredCascade = CascadeStrategy.Merge,
                    Weight = 0.8,
                    Remediation = "Set governance intensity to at least 70 with Merge cascade to combine organizational and departmental governance rules."
                },
                new()
                {
                    RequirementId = "HIPAA-RET-01",
                    Description = "Retention policies must enforce minimum 6-year PHI retention period.",
                    Category = "retention",
                    FeatureId = "retention",
                    MinimumIntensity = 80,
                    Weight = 0.8,
                    Remediation = "Set retention intensity to at least 80 to enforce HIPAA's 6-year minimum retention requirement."
                },
                new()
                {
                    RequirementId = "HIPAA-BAK-01",
                    Description = "Backup policies must ensure PHI recoverability within required timeframes.",
                    Category = "backup",
                    FeatureId = "backup",
                    MinimumIntensity = 70,
                    Weight = 0.7,
                    Remediation = "Set backup intensity to at least 70 to meet PHI recoverability requirements."
                },
                new()
                {
                    RequirementId = "HIPAA-MON-01",
                    Description = "Monitoring must detect unauthorized PHI access attempts in near-real-time.",
                    Category = "monitoring",
                    FeatureId = "monitoring",
                    MinimumIntensity = 60,
                    Weight = 0.6,
                    Remediation = "Set monitoring intensity to at least 60 to enable near-real-time PHI access monitoring."
                }
            }
        };

        /// <summary>
        /// Creates the GDPR (General Data Protection Regulation) compliance template.
        /// Focuses on data privacy, right to erasure, data portability, and governance
        /// requirements for personal data of EU residents.
        /// </summary>
        /// <returns>A <see cref="RegulatoryTemplate"/> with 8 GDPR requirements.</returns>
        public static RegulatoryTemplate Gdpr() => new()
        {
            FrameworkName = "GDPR",
            FrameworkVersion = "2024",
            Description = "General Data Protection Regulation requirements for personal data of EU residents.",
            Requirements = new RegulatoryRequirement[]
            {
                new()
                {
                    RequirementId = "GDPR-PRI-01",
                    Description = "Data privacy controls must be active at high intensity for personal data protection.",
                    Category = "data_privacy",
                    FeatureId = "data_privacy",
                    MinimumIntensity = 80,
                    Weight = 1.0,
                    Remediation = "Set data_privacy intensity to at least 80 to enforce GDPR personal data protection requirements."
                },
                new()
                {
                    RequirementId = "GDPR-ENC-01",
                    Description = "Personal data must be encrypted with adequate strength.",
                    Category = "encryption",
                    FeatureId = "encryption",
                    MinimumIntensity = 70,
                    Weight = 0.9,
                    Remediation = "Set encryption intensity to at least 70 to protect personal data at rest and in transit."
                },
                new()
                {
                    RequirementId = "GDPR-RET-01",
                    Description = "Retention must be configurable with an explicit retention policy parameter.",
                    Category = "retention",
                    FeatureId = "retention",
                    RequiredParameters = new Dictionary<string, string> { ["retention_policy"] = "" },
                    Weight = 0.9,
                    Remediation = "Add a 'retention_policy' custom parameter to the retention feature policy defining the data retention period."
                },
                new()
                {
                    RequirementId = "GDPR-AUD-01",
                    Description = "Audit logging must track all personal data processing activities.",
                    Category = "audit",
                    FeatureId = "audit",
                    MinimumIntensity = 70,
                    Weight = 0.8,
                    Remediation = "Set audit intensity to at least 70 to capture all personal data processing events."
                },
                new()
                {
                    RequirementId = "GDPR-ACC-01",
                    Description = "Access control must restrict personal data access to authorized personnel.",
                    Category = "access_control",
                    FeatureId = "access_control",
                    MinimumIntensity = 70,
                    Weight = 0.8,
                    Remediation = "Set access_control intensity to at least 70 to enforce least-privilege access to personal data."
                },
                new()
                {
                    RequirementId = "GDPR-GOV-01",
                    Description = "Governance must enforce data processing rules with merged organizational policies.",
                    Category = "governance",
                    FeatureId = "governance",
                    MinimumIntensity = 80,
                    RequiredCascade = CascadeStrategy.Merge,
                    Weight = 0.9,
                    Remediation = "Set governance intensity to at least 80 with Merge cascade to combine data processing governance rules."
                },
                new()
                {
                    RequirementId = "GDPR-DEL-01",
                    Description = "Deletion capability must support right to erasure (Article 17) with high intensity.",
                    Category = "deletion",
                    FeatureId = "deletion",
                    MinimumIntensity = 80,
                    Weight = 1.0,
                    Remediation = "Set deletion intensity to at least 80 to support GDPR right to be forgotten (Article 17) requirements."
                },
                new()
                {
                    RequirementId = "GDPR-PORT-01",
                    Description = "Data portability must support export in a structured, machine-readable format.",
                    Category = "portability",
                    FeatureId = "portability",
                    RequiredParameters = new Dictionary<string, string> { ["export_format"] = "" },
                    Weight = 0.8,
                    Remediation = "Add an 'export_format' custom parameter to the portability feature policy (e.g., 'JSON', 'CSV')."
                }
            }
        };

        /// <summary>
        /// Creates the SOC2 (Service Organization Control Type 2) compliance template.
        /// Covers the five trust service criteria: security, availability, processing integrity,
        /// confidentiality, and privacy.
        /// </summary>
        /// <returns>A <see cref="RegulatoryTemplate"/> with 8 SOC2 requirements.</returns>
        public static RegulatoryTemplate Soc2() => new()
        {
            FrameworkName = "SOC2",
            FrameworkVersion = "2024",
            Description = "Service Organization Control Type 2 requirements covering the five trust service criteria.",
            Requirements = new RegulatoryRequirement[]
            {
                new()
                {
                    RequirementId = "SOC2-SEC-01",
                    Description = "Security controls must be active at adequate intensity to protect against unauthorized access.",
                    Category = "security",
                    FeatureId = "security",
                    MinimumIntensity = 70,
                    Weight = 1.0,
                    Remediation = "Set security intensity to at least 70 to meet SOC2 Common Criteria security requirements."
                },
                new()
                {
                    RequirementId = "SOC2-AV-01",
                    Description = "System availability controls must ensure agreed-upon uptime targets.",
                    Category = "availability",
                    FeatureId = "availability",
                    MinimumIntensity = 70,
                    Weight = 0.9,
                    Remediation = "Set availability intensity to at least 70 to meet SOC2 availability trust criteria."
                },
                new()
                {
                    RequirementId = "SOC2-INT-01",
                    Description = "Data integrity controls must detect and prevent unauthorized modifications.",
                    Category = "integrity",
                    FeatureId = "integrity",
                    MinimumIntensity = 70,
                    Weight = 0.9,
                    Remediation = "Set integrity intensity to at least 70 to meet SOC2 processing integrity criteria."
                },
                new()
                {
                    RequirementId = "SOC2-CON-01",
                    Description = "Confidentiality controls must protect sensitive data from disclosure.",
                    Category = "confidentiality",
                    FeatureId = "confidentiality",
                    MinimumIntensity = 70,
                    Weight = 0.9,
                    Remediation = "Set confidentiality intensity to at least 70 to meet SOC2 confidentiality trust criteria."
                },
                new()
                {
                    RequirementId = "SOC2-PRI-01",
                    Description = "Privacy controls must govern collection, use, and retention of personal information.",
                    Category = "privacy",
                    FeatureId = "privacy",
                    MinimumIntensity = 70,
                    Weight = 0.8,
                    Remediation = "Set privacy intensity to at least 70 to meet SOC2 privacy trust criteria."
                },
                new()
                {
                    RequirementId = "SOC2-AUD-01",
                    Description = "Audit logging must provide comprehensive evidence of control effectiveness.",
                    Category = "audit",
                    FeatureId = "audit",
                    MinimumIntensity = 80,
                    Weight = 1.0,
                    Remediation = "Set audit intensity to at least 80 to support SOC2 audit evidence requirements."
                },
                new()
                {
                    RequirementId = "SOC2-CHG-01",
                    Description = "Change management controls must track and authorize all system modifications.",
                    Category = "change_management",
                    FeatureId = "change_management",
                    MinimumIntensity = 60,
                    Weight = 0.7,
                    Remediation = "Set change_management intensity to at least 60 to enforce SOC2 change control requirements."
                },
                new()
                {
                    RequirementId = "SOC2-MON-01",
                    Description = "Monitoring must detect anomalies and security events across all trust criteria.",
                    Category = "monitoring",
                    FeatureId = "monitoring",
                    MinimumIntensity = 70,
                    Weight = 0.8,
                    Remediation = "Set monitoring intensity to at least 70 to meet SOC2 continuous monitoring requirements."
                }
            }
        };

        /// <summary>
        /// Creates the FedRAMP (Federal Risk and Authorization Management Program) compliance template.
        /// Enforces the strictest requirements for federal government cloud services including
        /// mandatory tamper-proof audit, manual-only AI for encryption, and high-intensity access control.
        /// </summary>
        /// <returns>A <see cref="RegulatoryTemplate"/> with 8 FedRAMP requirements.</returns>
        public static RegulatoryTemplate FedRamp() => new()
        {
            FrameworkName = "FedRAMP",
            FrameworkVersion = "2024",
            Description = "Federal Risk and Authorization Management Program requirements for federal government cloud services.",
            Requirements = new RegulatoryRequirement[]
            {
                new()
                {
                    RequirementId = "FEDRAMP-ENC-01",
                    Description = "FIPS 140-2/3 compliant encryption at maximum intensity with enforced cascade.",
                    Category = "encryption",
                    FeatureId = "encryption",
                    MinimumIntensity = 90,
                    RequiredCascade = CascadeStrategy.Enforce,
                    Weight = 1.0,
                    Remediation = "Set encryption intensity to at least 90 with Enforce cascade for FIPS-compliant encryption at all hierarchy levels."
                },
                new()
                {
                    RequirementId = "FEDRAMP-ENC-02",
                    Description = "Encryption configuration must require manual human approval only (no AI automation).",
                    Category = "encryption",
                    FeatureId = "encryption",
                    MaxAiAutonomy = AiAutonomyLevel.ManualOnly,
                    Weight = 0.9,
                    Remediation = "Set encryption AiAutonomy to ManualOnly to prohibit any AI-driven encryption changes in federal environments."
                },
                new()
                {
                    RequirementId = "FEDRAMP-AUD-01",
                    Description = "Audit logging at maximum intensity with enforced cascade for tamper-proof federal audit trails.",
                    Category = "audit",
                    FeatureId = "audit",
                    MinimumIntensity = 95,
                    RequiredCascade = CascadeStrategy.Enforce,
                    Weight = 1.0,
                    Remediation = "Set audit intensity to at least 95 with Enforce cascade for comprehensive tamper-proof federal audit trails."
                },
                new()
                {
                    RequirementId = "FEDRAMP-ACC-01",
                    Description = "Access control at high intensity with MostRestrictive cascade for federal data protection.",
                    Category = "access_control",
                    FeatureId = "access_control",
                    MinimumIntensity = 90,
                    RequiredCascade = CascadeStrategy.MostRestrictive,
                    Weight = 1.0,
                    Remediation = "Set access_control intensity to at least 90 with MostRestrictive cascade for federal access requirements."
                },
                new()
                {
                    RequirementId = "FEDRAMP-ACC-02",
                    Description = "Access control changes must require human review (AI limited to suggestions).",
                    Category = "access_control",
                    FeatureId = "access_control",
                    MaxAiAutonomy = AiAutonomyLevel.Suggest,
                    Weight = 0.8,
                    Remediation = "Set access_control AiAutonomy to Suggest or ManualOnly to require human review of access changes."
                },
                new()
                {
                    RequirementId = "FEDRAMP-MON-01",
                    Description = "Continuous monitoring at high intensity for federal threat detection and response.",
                    Category = "monitoring",
                    FeatureId = "monitoring",
                    MinimumIntensity = 80,
                    Weight = 0.8,
                    Remediation = "Set monitoring intensity to at least 80 for FedRAMP continuous monitoring requirements (ConMon)."
                },
                new()
                {
                    RequirementId = "FEDRAMP-INC-01",
                    Description = "Incident response capability must meet federal reporting and containment requirements.",
                    Category = "incident_response",
                    FeatureId = "incident_response",
                    MinimumIntensity = 80,
                    Weight = 0.8,
                    Remediation = "Set incident_response intensity to at least 80 to meet FedRAMP US-CERT incident reporting timelines."
                },
                new()
                {
                    RequirementId = "FEDRAMP-BAK-01",
                    Description = "Backup and recovery must ensure federal data continuity and disaster recovery.",
                    Category = "backup",
                    FeatureId = "backup",
                    MinimumIntensity = 80,
                    Weight = 0.7,
                    Remediation = "Set backup intensity to at least 80 to meet FedRAMP contingency planning (CP) requirements."
                }
            }
        };
    }
}
