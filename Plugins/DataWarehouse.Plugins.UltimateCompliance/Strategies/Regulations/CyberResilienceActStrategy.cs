using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Regulations
{
    /// <summary>
    /// EU Cyber Resilience Act compliance strategy.
    /// Validates vulnerability handling, incident reporting, and security updates.
    /// </summary>
    public sealed class CyberResilienceActStrategy : ComplianceStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "eu-cyber-resilience-act";

        /// <inheritdoc/>
        public override string StrategyName => "Cyber Resilience Act Compliance";

        /// <inheritdoc/>
        public override string Framework => "EU-CRA";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("cyber_resilience_act.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            CheckVulnerabilityHandling(context, violations, recommendations);
            CheckSecurityByDesign(context, violations, recommendations);
            CheckIncidentReporting(context, violations, recommendations);
            CheckSecurityUpdates(context, violations, recommendations);

            var isCompliant = !violations.Any(v => v.Severity >= ViolationSeverity.High);
            var status = violations.Count == 0 ? ComplianceStatus.Compliant :
                        violations.Any(v => v.Severity >= ViolationSeverity.High) ? ComplianceStatus.NonCompliant :
                        ComplianceStatus.PartiallyCompliant;

            return Task.FromResult(new ComplianceResult
            {
                IsCompliant = isCompliant,
                Framework = Framework,
                Status = status,
                Violations = violations,
                Recommendations = recommendations
            });
        }

        private void CheckVulnerabilityHandling(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("VulnerabilityHandlingProcess", out var processObj) || processObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CRA-001",
                    Description = "No vulnerability handling process documented",
                    Severity = ViolationSeverity.High,
                    Remediation = "Establish vulnerability identification, documentation, and remediation process",
                    RegulatoryReference = "CRA Article 11"
                });
            }

            if (context.Attributes.TryGetValue("ActiveVulnerabilities", out var vulnObj) &&
                vulnObj is int vulnCount && vulnCount > 0)
            {
                if (!context.Attributes.TryGetValue("RemediationTimeline", out var timelineObj) || timelineObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "CRA-002",
                        Description = $"{vulnCount} active vulnerabilities without remediation timeline",
                        Severity = ViolationSeverity.High,
                        Remediation = "Define and implement remediation timeline for all vulnerabilities",
                        RegulatoryReference = "CRA Article 11"
                    });
                }
            }
        }

        private void CheckSecurityByDesign(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("SecurityByDesign", out var designObj) || designObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CRA-003",
                    Description = "Security-by-design principles not documented",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Document security-by-design approach throughout product lifecycle",
                    RegulatoryReference = "CRA Article 10"
                });
            }

            if (!string.IsNullOrEmpty(context.DataClassification) && context.DataClassification.Equals("sensitive", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(context.DataClassification, "critical", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("EncryptionAtRest", out var encryptObj) || encryptObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "CRA-004",
                        Description = "Sensitive data not encrypted at rest",
                        Severity = ViolationSeverity.High,
                        Remediation = "Implement encryption for sensitive data storage",
                        RegulatoryReference = "CRA Annex I"
                    });
                }
            }
        }

        private void CheckIncidentReporting(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!string.IsNullOrEmpty(context.OperationType) && context.OperationType.Equals("security-incident", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("IncidentSeverity", out var severityObj) ||
                    severityObj is not string severity)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "CRA-005",
                        Description = "Security incident severity not classified",
                        Severity = ViolationSeverity.High,
                        Remediation = "Classify incident severity and determine reporting requirements",
                        RegulatoryReference = "CRA Article 14"
                    });
                }

                if (!context.Attributes.TryGetValue("IncidentReported", out var reportedObj) || reportedObj is not true)
                {
                    recommendations.Add("Consider reporting requirement: actively exploited vulnerabilities and severe incidents must be reported within 24 hours");
                }
            }
        }

        private void CheckSecurityUpdates(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.Attributes.TryGetValue("SupportPeriod", out var supportObj) &&
                supportObj is TimeSpan supportPeriod)
            {
                if (supportPeriod.TotalDays < 1825) // 5 years minimum
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "CRA-006",
                        Description = $"Security update support period ({supportPeriod.TotalDays / 365:F1} years) below required minimum",
                        Severity = ViolationSeverity.Medium,
                        Remediation = "Provide security updates for minimum 5 years or expected product lifetime",
                        RegulatoryReference = "CRA Article 10(9)"
                    });
                }
            }
            else
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CRA-007",
                    Description = "No security update support period defined",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Define and communicate security update support period",
                    RegulatoryReference = "CRA Article 10(9)"
                });
            }
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("cyber_resilience_act.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("cyber_resilience_act.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
