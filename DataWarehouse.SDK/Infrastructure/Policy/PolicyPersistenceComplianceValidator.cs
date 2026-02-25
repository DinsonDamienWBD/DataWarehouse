using System;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Infrastructure.Policy
{
    /// <summary>
    /// Validates a <see cref="PolicyPersistenceConfiguration"/> against compliance framework
    /// requirements (HIPAA, SOC2, GDPR, FedRAMP). Returns actionable violations with remediation
    /// steps when the configured persistence backend does not meet a framework's requirements.
    /// <para>
    /// This validator is intended to run at startup or during configuration changes to prevent
    /// deployment with inadequate persistence for regulated environments. All rules are hardcoded
    /// and production-ready -- no external rule engine is needed.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <b>Usage example:</b>
    /// <code>
    /// var config = new PolicyPersistenceConfiguration
    /// {
    ///     Backend = PolicyPersistenceBackend.InMemory,
    ///     ActiveComplianceFrameworks = new[] { "HIPAA", "SOC2" }
    /// };
    ///
    /// var result = PolicyPersistenceComplianceValidator.Validate(config);
    /// if (!result.IsValid)
    /// {
    ///     foreach (var v in result.Violations)
    ///         Console.WriteLine($"[{v.Rule}] {v.Message} -- Remediation: {v.Remediation}");
    /// }
    /// </code>
    ///
    /// <b>Supported compliance frameworks:</b>
    /// <list type="bullet">
    ///   <item><description>HIPAA: Requires immutable, durable audit trails</description></item>
    ///   <item><description>SOC2: Requires durable, auditable policy storage</description></item>
    ///   <item><description>GDPR: Requires demonstrable policy enforcement records</description></item>
    ///   <item><description>FedRAMP: Requires tamper-proof audit records</description></item>
    /// </list>
    /// </remarks>
    [SdkCompatibility("6.0.0", Notes = "Phase 69: Compliance validation (PERS-07)")]
    public sealed class PolicyPersistenceComplianceValidator
    {
        /// <summary>
        /// Validates the specified persistence configuration against all active compliance
        /// frameworks and returns a result containing any violations found.
        /// </summary>
        /// <param name="config">The persistence configuration to validate. Must not be null.</param>
        /// <returns>
        /// A <see cref="ComplianceValidationResult"/> that is valid when no violations are found,
        /// or contains one or more <see cref="ComplianceViolation"/> entries with actionable
        /// remediation steps.
        /// </returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="config"/> is null.</exception>
        public static ComplianceValidationResult Validate(PolicyPersistenceConfiguration config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            var violations = new List<ComplianceViolation>();

            ValidateHipaa(config, violations);
            ValidateSoc2(config, violations);
            ValidateGdpr(config, violations);
            ValidateFedRamp(config, violations);

            return new ComplianceValidationResult
            {
                IsValid = violations.Count == 0,
                Violations = violations
            };
        }

        /// <summary>
        /// Validates HIPAA compliance requirements against the persistence configuration.
        /// HIPAA requires immutable, durable audit trails that survive process restarts.
        /// </summary>
        private static void ValidateHipaa(PolicyPersistenceConfiguration config, List<ComplianceViolation> violations)
        {
            if (!HasFramework(config, "HIPAA"))
                return;

            // HIPAA-AUDIT-001: File-based audit is not tamper-proof
            if (config.Backend == PolicyPersistenceBackend.File ||
                (config.Backend == PolicyPersistenceBackend.Hybrid && config.AuditBackend == PolicyPersistenceBackend.File))
            {
                violations.Add(new ComplianceViolation
                {
                    Framework = "HIPAA",
                    Rule = "HIPAA-AUDIT-001",
                    Message = "HIPAA requires immutable audit trails. File-based audit storage does not provide tamper-proof guarantees.",
                    Remediation = "Change auditStore to TamperProof or use Hybrid backend with AuditBackend=TamperProof."
                });
            }

            // HIPAA-AUDIT-002: In-memory audit does not survive restarts
            if (config.Backend == PolicyPersistenceBackend.InMemory ||
                (config.Backend == PolicyPersistenceBackend.Hybrid && config.AuditBackend == PolicyPersistenceBackend.InMemory))
            {
                violations.Add(new ComplianceViolation
                {
                    Framework = "HIPAA",
                    Rule = "HIPAA-AUDIT-002",
                    Message = "HIPAA requires durable audit trails. In-memory storage does not survive restarts.",
                    Remediation = "Use Database, TamperProof, or Hybrid backend for production HIPAA deployments."
                });
            }
        }

        /// <summary>
        /// Validates SOC2 compliance requirements against the persistence configuration.
        /// SOC2 requires durable, auditable policy storage and audit trails.
        /// </summary>
        private static void ValidateSoc2(PolicyPersistenceConfiguration config, List<ComplianceViolation> violations)
        {
            if (!HasFramework(config, "SOC2"))
                return;

            // SOC2-AUDIT-001: In-memory backend is not durable
            if (config.Backend == PolicyPersistenceBackend.InMemory)
            {
                violations.Add(new ComplianceViolation
                {
                    Framework = "SOC2",
                    Rule = "SOC2-AUDIT-001",
                    Message = "SOC2 requires durable, auditable policy storage.",
                    Remediation = "Use Database, File, TamperProof, or Hybrid backend."
                });
            }

            // SOC2-AUDIT-002: Hybrid with in-memory audit is not durable
            if (config.Backend == PolicyPersistenceBackend.Hybrid && config.AuditBackend == PolicyPersistenceBackend.InMemory)
            {
                violations.Add(new ComplianceViolation
                {
                    Framework = "SOC2",
                    Rule = "SOC2-AUDIT-002",
                    Message = "SOC2 requires durable audit trails.",
                    Remediation = "Change AuditBackend to TamperProof or Database."
                });
            }
        }

        /// <summary>
        /// Validates GDPR compliance requirements against the persistence configuration.
        /// GDPR requires demonstrable policy enforcement records that survive process restarts.
        /// </summary>
        private static void ValidateGdpr(PolicyPersistenceConfiguration config, List<ComplianceViolation> violations)
        {
            if (!HasFramework(config, "GDPR"))
                return;

            // GDPR-POLICY-001: In-memory backend cannot demonstrate policy enforcement
            if (config.Backend == PolicyPersistenceBackend.InMemory)
            {
                violations.Add(new ComplianceViolation
                {
                    Framework = "GDPR",
                    Rule = "GDPR-POLICY-001",
                    Message = "GDPR requires demonstrable policy enforcement records.",
                    Remediation = "Use a durable backend (Database, File, TamperProof, or Hybrid)."
                });
            }
        }

        /// <summary>
        /// Validates FedRAMP compliance requirements against the persistence configuration.
        /// FedRAMP requires tamper-proof audit records, either directly or via Hybrid with
        /// TamperProof audit backend.
        /// </summary>
        private static void ValidateFedRamp(PolicyPersistenceConfiguration config, List<ComplianceViolation> violations)
        {
            if (!HasFramework(config, "FedRAMP"))
                return;

            // FEDRAMP-AUDIT-001: Audit must be tamper-proof
            if (!AuditIsTamperProof(config))
            {
                violations.Add(new ComplianceViolation
                {
                    Framework = "FedRAMP",
                    Rule = "FEDRAMP-AUDIT-001",
                    Message = "FedRAMP requires tamper-proof audit records.",
                    Remediation = "Use TamperProof backend or Hybrid with AuditBackend=TamperProof."
                });
            }
        }

        /// <summary>
        /// Checks whether the specified compliance framework is active in the configuration.
        /// Uses case-insensitive comparison.
        /// </summary>
        /// <param name="config">The persistence configuration.</param>
        /// <param name="framework">The framework name to check (e.g., "HIPAA", "SOC2").</param>
        /// <returns><see langword="true"/> if the framework is in <see cref="PolicyPersistenceConfiguration.ActiveComplianceFrameworks"/>.</returns>
        private static bool HasFramework(PolicyPersistenceConfiguration config, string framework)
        {
            if (config.ActiveComplianceFrameworks == null || config.ActiveComplianceFrameworks.Length == 0)
                return false;

            for (int i = 0; i < config.ActiveComplianceFrameworks.Length; i++)
            {
                if (string.Equals(config.ActiveComplianceFrameworks[i], framework, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Determines whether the audit trail uses tamper-proof storage, either via direct
        /// <see cref="PolicyPersistenceBackend.TamperProof"/> backend or via
        /// <see cref="PolicyPersistenceBackend.Hybrid"/> with <see cref="PolicyPersistenceConfiguration.AuditBackend"/>
        /// set to <see cref="PolicyPersistenceBackend.TamperProof"/>.
        /// </summary>
        /// <param name="config">The persistence configuration.</param>
        /// <returns><see langword="true"/> if the audit trail is tamper-proof.</returns>
        private static bool AuditIsTamperProof(PolicyPersistenceConfiguration config)
        {
            if (config.Backend == PolicyPersistenceBackend.TamperProof)
                return true;

            if (config.Backend == PolicyPersistenceBackend.Hybrid && config.AuditBackend == PolicyPersistenceBackend.TamperProof)
                return true;

            return false;
        }
    }

    /// <summary>
    /// Result of a compliance validation check against a <see cref="PolicyPersistenceConfiguration"/>.
    /// Contains a validity flag and any violations found.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 69: Compliance validation (PERS-07)")]
    public sealed record ComplianceValidationResult
    {
        /// <summary>
        /// Whether the configuration passes all active compliance framework requirements.
        /// </summary>
        public bool IsValid { get; init; }

        /// <summary>
        /// List of compliance violations found. Empty when <see cref="IsValid"/> is <see langword="true"/>.
        /// </summary>
        public IReadOnlyList<ComplianceViolation> Violations { get; init; } = Array.Empty<ComplianceViolation>();
    }

    /// <summary>
    /// Represents a single compliance violation found during validation. Contains the framework,
    /// rule identifier, human-readable message, and actionable remediation steps.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 69: Compliance validation (PERS-07)")]
    public sealed record ComplianceViolation
    {
        /// <summary>
        /// The compliance framework that was violated (e.g., "HIPAA", "SOC2", "GDPR", "FedRAMP").
        /// </summary>
        public required string Framework { get; init; }

        /// <summary>
        /// The specific rule identifier that was violated (e.g., "HIPAA-AUDIT-001").
        /// </summary>
        public required string Rule { get; init; }

        /// <summary>
        /// Human-readable description of what the violation is and why it matters.
        /// </summary>
        public required string Message { get; init; }

        /// <summary>
        /// Actionable steps the operator should take to resolve the violation.
        /// </summary>
        public required string Remediation { get; init; }
    }
}
