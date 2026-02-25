using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Regulations
{
    /// <summary>
    /// SOX (Sarbanes-Oxley Act) compliance strategy.
    /// Validates operations against financial reporting and internal control requirements.
    /// </summary>
    public sealed class Sox2Strategy : ComplianceStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "sox";

        /// <inheritdoc/>
        public override string StrategyName => "SOX Compliance";

        /// <inheritdoc/>
        public override string Framework => "SOX";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
        IncrementCounter("sox2.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            bool isFinancialData = IsFinancialData(context);

            if (isFinancialData)
            {
                // Section 302: Corporate Responsibility
                CheckCorporateResponsibility(context, violations, recommendations);

                // Section 404: Internal Controls
                CheckInternalControls(context, violations, recommendations);

                // Section 409: Real-Time Disclosure
                CheckRealTimeDisclosure(context, violations, recommendations);

                // Audit Trail Requirements
                CheckAuditTrail(context, violations, recommendations);

                // Access Controls
                CheckAccessControls(context, violations, recommendations);

                // Change Management
                CheckChangeManagement(context, violations, recommendations);

                // Data Integrity
                CheckDataIntegrity(context, violations, recommendations);

                // Record Retention
                CheckRecordRetention(context, violations, recommendations);
            }

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
                Recommendations = recommendations,
                Metadata = new Dictionary<string, object>
                {
                    ["IsFinancialData"] = isFinancialData,
                    ["OperationType"] = context.OperationType,
                    ["DataClassification"] = context.DataClassification
                }
            });
        }

        private bool IsFinancialData(ComplianceContext context)
        {
            var financialKeywords = new[] { "financial", "accounting", "revenue", "expense",
                "balance", "ledger", "transaction", "audit", "sox", "fiscal" };

            if (financialKeywords.Any(k => context.DataClassification.Contains(k, StringComparison.OrdinalIgnoreCase)))
                return true;

            if (context.Attributes.TryGetValue("IsFinancialData", out var finObj) && finObj is true)
                return true;

            if (context.ProcessingPurposes.Any(p =>
                financialKeywords.Any(k => p.Contains(k, StringComparison.OrdinalIgnoreCase))))
                return true;

            return false;
        }

        private void CheckCorporateResponsibility(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            // CEO/CFO certification requirements
            if (context.OperationType.Contains("report", StringComparison.OrdinalIgnoreCase) ||
                context.OperationType.Contains("disclosure", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("ExecutiveCertification", out var certObj) || certObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "SOX-001",
                        Description = "Financial reports require executive certification",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Obtain CEO and CFO certification for financial reports",
                        RegulatoryReference = "SOX Section 302"
                    });
                }
            }

            // Disclosure controls
            if (!context.Attributes.TryGetValue("DisclosureControlsEvaluated", out var dceObj) || dceObj is not true)
            {
                recommendations.Add("Evaluate disclosure controls and procedures effectiveness (Section 302(a)(4))");
            }
        }

        private void CheckInternalControls(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            // Internal control over financial reporting (ICFR)
            if (!context.Attributes.TryGetValue("InternalControlsDocumented", out var icObj) || icObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "SOX-002",
                    Description = "Internal controls over financial reporting not documented",
                    Severity = ViolationSeverity.High,
                    Remediation = "Document and maintain internal controls over financial reporting",
                    RegulatoryReference = "SOX Section 404(a)"
                });
            }

            // Management assessment
            if (!context.Attributes.TryGetValue("ManagementAssessmentCompleted", out var maObj) || maObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "SOX-003",
                    Description = "Management assessment of internal controls not completed",
                    Severity = ViolationSeverity.High,
                    Remediation = "Complete annual management assessment of ICFR effectiveness",
                    RegulatoryReference = "SOX Section 404(a)"
                });
            }

            // Attestation by external auditor (for accelerated filers)
            if (context.Attributes.TryGetValue("IsAcceleratedFiler", out var afObj) && afObj is true)
            {
                if (!context.Attributes.TryGetValue("AuditorAttestation", out var aaObj) || aaObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "SOX-004",
                        Description = "External auditor attestation required for accelerated filers",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Obtain external auditor attestation on ICFR effectiveness",
                        RegulatoryReference = "SOX Section 404(b)"
                    });
                }
            }

            // Material weakness disclosure
            if (context.Attributes.TryGetValue("MaterialWeaknessIdentified", out var mwObj) && mwObj is true)
            {
                if (!context.Attributes.TryGetValue("MaterialWeaknessDisclosed", out var mwdObj) || mwdObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "SOX-005",
                        Description = "Material weakness identified but not disclosed",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Disclose all material weaknesses in internal controls",
                        RegulatoryReference = "SOX Section 404"
                    });
                }
            }
        }

        private void CheckRealTimeDisclosure(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.Attributes.TryGetValue("IsMaterialEvent", out var meObj) && meObj is true)
            {
                if (!context.Attributes.TryGetValue("TimelyDisclosure", out var tdObj) || tdObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "SOX-006",
                        Description = "Material event requires timely public disclosure",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Disclose material events on rapid and current basis",
                        RegulatoryReference = "SOX Section 409"
                    });
                }
            }
        }

        private void CheckAuditTrail(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("AuditTrailEnabled", out var atObj) || atObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "SOX-007",
                    Description = "Audit trail not enabled for financial data",
                    Severity = ViolationSeverity.High,
                    Remediation = "Enable comprehensive audit trails for all financial data access and changes",
                    RegulatoryReference = "SOX Section 802"
                });
            }

            if (!context.Attributes.TryGetValue("AuditTrailTamperProof", out var atpObj) || atpObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "SOX-008",
                    Description = "Audit trail is not tamper-proof",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement tamper-proof audit logging with integrity verification",
                    RegulatoryReference = "SOX Section 802"
                });
            }
        }

        private void CheckAccessControls(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            // Segregation of duties
            if (!context.Attributes.TryGetValue("SegregationOfDuties", out var sodObj) || sodObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "SOX-009",
                    Description = "Segregation of duties not enforced",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement segregation of duties for financial processes",
                    RegulatoryReference = "SOX Section 404"
                });
            }

            // Role-based access control
            if (!context.Attributes.TryGetValue("RoleBasedAccessControl", out var rbacObj) || rbacObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "SOX-010",
                    Description = "Role-based access control not implemented",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Implement RBAC for financial systems and data",
                    RegulatoryReference = "SOX Section 404"
                });
            }

            // Periodic access review
            if (!context.Attributes.TryGetValue("AccessReviewCompleted", out var arObj) || arObj is not true)
            {
                recommendations.Add("Conduct periodic access reviews for financial systems");
            }
        }

        private void CheckChangeManagement(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.OperationType.Contains("modify", StringComparison.OrdinalIgnoreCase) ||
                context.OperationType.Contains("change", StringComparison.OrdinalIgnoreCase) ||
                context.OperationType.Contains("update", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("ChangeApproved", out var caObj) || caObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "SOX-011",
                        Description = "Change to financial data/system not approved",
                        Severity = ViolationSeverity.High,
                        Remediation = "Obtain appropriate approval before making changes to financial systems",
                        RegulatoryReference = "SOX Section 404"
                    });
                }

                if (!context.Attributes.TryGetValue("ChangeDocumented", out var cdObj) || cdObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "SOX-012",
                        Description = "Change not documented",
                        Severity = ViolationSeverity.Medium,
                        Remediation = "Document all changes to financial systems with justification",
                        RegulatoryReference = "SOX Section 404"
                    });
                }
            }
        }

        private void CheckDataIntegrity(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("DataIntegrityControls", out var dicObj) || dicObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "SOX-013",
                    Description = "Data integrity controls not implemented",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement controls to ensure accuracy and completeness of financial data",
                    RegulatoryReference = "SOX Section 404"
                });
            }

            // Reconciliation controls
            if (!context.Attributes.TryGetValue("ReconciliationPerformed", out var rpObj) || rpObj is not true)
            {
                recommendations.Add("Implement regular reconciliation of financial data");
            }
        }

        private void CheckRecordRetention(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("RetentionPolicyApplied", out var rpObj) || rpObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "SOX-014",
                    Description = "Record retention policy not applied to financial records",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Apply 7-year minimum retention for audit work papers and financial records",
                    RegulatoryReference = "SOX Section 802"
                });
            }

            // Check for premature destruction
            if (context.OperationType.Contains("delete", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("RetentionPeriodExpired", out var rpeObj) || rpeObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "SOX-015",
                        Description = "Deletion of financial records before retention period expires",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Do not delete financial records until retention period (minimum 7 years) expires",
                        RegulatoryReference = "SOX Section 802"
                    });
                }
            }
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("sox2.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("sox2.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
