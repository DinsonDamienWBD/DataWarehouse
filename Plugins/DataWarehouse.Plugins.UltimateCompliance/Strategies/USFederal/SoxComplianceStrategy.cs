using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.USFederal
{
    /// <summary>
    /// SOX (Sarbanes-Oxley Act) compliance strategy for IT controls.
    /// Validates financial reporting internal controls, audit trails, and retention.
    /// </summary>
    public sealed class SoxComplianceStrategy : ComplianceStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "sox-it";

        /// <inheritdoc/>
        public override string StrategyName => "SOX IT Controls Compliance";

        /// <inheritdoc/>
        public override string Framework => "SOX";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("sox_compliance.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            CheckInternalControls(context, violations, recommendations);
            CheckAuditTrails(context, violations, recommendations);
            CheckDataRetention(context, violations, recommendations);
            CheckAccessControls(context, violations, recommendations);
            CheckChangeManagement(context, violations, recommendations);

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

        private void CheckInternalControls(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!string.IsNullOrEmpty(context.DataClassification) && context.DataClassification.Equals("financial", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("ItgcDocumented", out var itgcObj) || itgcObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "SOX-001",
                        Description = "IT General Controls (ITGC) not documented",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Document IT general controls supporting financial reporting",
                        RegulatoryReference = "SOX Section 404"
                    });
                }

                if (!context.Attributes.TryGetValue("ControlEffectivenessTested", out var testObj) || testObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "SOX-002",
                        Description = "Internal control effectiveness not tested",
                        Severity = ViolationSeverity.High,
                        Remediation = "Test and document effectiveness of internal controls annually",
                        RegulatoryReference = "SOX Section 404"
                    });
                }
            }
        }

        private void CheckAuditTrails(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("AuditTrailEnabled", out var auditObj) || auditObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "SOX-003",
                    Description = "Audit trail not enabled for financial systems",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Enable comprehensive audit logging of all financial transactions",
                    RegulatoryReference = "SOX Section 802"
                });
            }

            if (!context.Attributes.TryGetValue("AuditTrailImmutable", out var immutableObj) || immutableObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "SOX-004",
                    Description = "Audit trail can be modified or deleted",
                    Severity = ViolationSeverity.High,
                    Remediation = "Ensure audit logs are immutable and tamper-evident",
                    RegulatoryReference = "SOX Section 802"
                });
            }

            if (context.Attributes.TryGetValue("AuditLogReviewFrequency", out var reviewObj) &&
                reviewObj is string frequency &&
                !frequency.Equals("monthly", StringComparison.OrdinalIgnoreCase))
            {
                recommendations.Add("Consider monthly audit log reviews for SOX-relevant systems");
            }
        }

        private void CheckDataRetention(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!string.IsNullOrEmpty(context.DataClassification) && context.DataClassification.Equals("financial", StringComparison.OrdinalIgnoreCase))
            {
                if (context.Attributes.TryGetValue("RetentionPeriodYears", out var retentionObj) &&
                    retentionObj is int years && years < 7)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "SOX-005",
                        Description = $"Financial record retention period ({years} years) below minimum requirement",
                        Severity = ViolationSeverity.High,
                        Remediation = "Retain financial records and audit trails for minimum 7 years",
                        RegulatoryReference = "SOX Section 802"
                    });
                }

                if (!string.IsNullOrEmpty(context.OperationType) && context.OperationType.Equals("deletion", StringComparison.OrdinalIgnoreCase))
                {
                    if (!context.Attributes.TryGetValue("LitigationHoldChecked", out var holdObj) || holdObj is not true)
                    {
                        violations.Add(new ComplianceViolation
                        {
                            Code = "SOX-006",
                            Description = "Financial record deletion without litigation hold verification",
                            Severity = ViolationSeverity.Critical,
                            Remediation = "Verify no litigation hold before deleting financial records",
                            RegulatoryReference = "SOX Section 802"
                        });
                    }
                }
            }
        }

        private void CheckAccessControls(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("SegregationOfDuties", out var sodObj) || sodObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "SOX-007",
                    Description = "Segregation of duties not enforced",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement segregation of duties for financial system access",
                    RegulatoryReference = "SOX Section 404"
                });
            }

            if (context.Attributes.TryGetValue("PrivilegedAccessReviewed", out var reviewObj) &&
                reviewObj is DateTime lastReview)
            {
                var daysSinceReview = (DateTime.UtcNow - lastReview).TotalDays;
                if (daysSinceReview > 90)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "SOX-008",
                        Description = $"Privileged access review overdue ({daysSinceReview:F0} days)",
                        Severity = ViolationSeverity.Medium,
                        Remediation = "Review privileged access quarterly",
                        RegulatoryReference = "SOX Section 404"
                    });
                }
            }
        }

        private void CheckChangeManagement(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!string.IsNullOrEmpty(context.OperationType) && context.OperationType.Equals("system-change", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("ChangeApproved", out var approvedObj) || approvedObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "SOX-009",
                        Description = "Financial system change not approved through change management",
                        Severity = ViolationSeverity.High,
                        Remediation = "Require formal approval for all financial system changes",
                        RegulatoryReference = "SOX Section 404"
                    });
                }

                if (!context.Attributes.TryGetValue("ChangeDocumented", out var docObj) || docObj is not true)
                {
                    recommendations.Add("Document all changes to financial systems for audit purposes");
                }
            }
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("sox_compliance.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("sox_compliance.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
