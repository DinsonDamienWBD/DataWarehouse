using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Regulations
{
    /// <summary>
    /// HIPAA (Health Insurance Portability and Accountability Act) compliance strategy.
    /// Validates operations against US healthcare data protection requirements.
    /// </summary>
    public sealed class HipaaStrategy : ComplianceStrategyBase
    {
        private readonly HashSet<string> _phiIdentifiers = new(StringComparer.OrdinalIgnoreCase)
        {
            "name", "address", "dates", "phone", "fax", "email", "ssn",
            "medical-record-number", "health-plan-id", "account-number",
            "certificate-license", "vehicle-id", "device-id", "url",
            "ip-address", "biometric-id", "photo", "other-unique-id"
        };

        /// <inheritdoc/>
        public override string StrategyId => "hipaa";

        /// <inheritdoc/>
        public override string StrategyName => "HIPAA Compliance";

        /// <inheritdoc/>
        public override string Framework => "HIPAA";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
        IncrementCounter("hipaa.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check if PHI is involved
            bool containsPhi = CheckPhiPresence(context);

            if (containsPhi)
            {
                // Check minimum necessary standard
                CheckMinimumNecessary(context, violations, recommendations);

                // Check authorization
                CheckAuthorization(context, violations, recommendations);

                // Check safeguards
                CheckSafeguards(context, violations, recommendations);

                // Check access controls
                CheckAccessControls(context, violations, recommendations);

                // Check audit controls
                CheckAuditControls(context, violations, recommendations);

                // Check breach notification readiness
                CheckBreachNotification(context, violations, recommendations);

                // Check BAA for third parties
                CheckBusinessAssociates(context, violations, recommendations);
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
                    ["ContainsPHI"] = containsPhi,
                    ["OperationType"] = context.OperationType,
                    ["DataClassification"] = context.DataClassification
                }
            });
        }

        private bool CheckPhiPresence(ComplianceContext context)
        {
            // Check data classification
            if (context.DataClassification.Contains("phi", StringComparison.OrdinalIgnoreCase) ||
                context.DataClassification.Contains("health", StringComparison.OrdinalIgnoreCase) ||
                context.DataClassification.Contains("medical", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Check data subject categories
            if (context.DataSubjectCategories.Any(c =>
                _phiIdentifiers.Contains(c) ||
                c.Contains("health", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            // Check explicit PHI flag
            if (context.Attributes.TryGetValue("ContainsPHI", out var phiObj) && phiObj is true)
            {
                return true;
            }

            return false;
        }

        private void CheckMinimumNecessary(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("MinimumNecessaryReview", out var reviewObj) || reviewObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "HIPAA-001",
                    Description = "Minimum necessary standard review not documented",
                    Severity = ViolationSeverity.High,
                    Remediation = "Document review ensuring only minimum necessary PHI is used/disclosed",
                    RegulatoryReference = "45 CFR 164.502(b), 164.514(d)"
                });
            }

            // Check for treatment, payment, health care operations exception
            if (context.ProcessingPurposes.Any(p =>
                p.Equals("treatment", StringComparison.OrdinalIgnoreCase) ||
                p.Equals("payment", StringComparison.OrdinalIgnoreCase) ||
                p.Equals("healthcare-operations", StringComparison.OrdinalIgnoreCase)))
            {
                recommendations.Add("TPO exception may apply - document treatment, payment, or healthcare operations purpose");
            }
        }

        private void CheckAuthorization(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            // Check if authorization is required and present
            var purposes = context.ProcessingPurposes;
            bool requiresAuth = purposes.Any(p =>
                p.Contains("marketing", StringComparison.OrdinalIgnoreCase) ||
                p.Contains("research", StringComparison.OrdinalIgnoreCase) ||
                p.Contains("sale", StringComparison.OrdinalIgnoreCase));

            if (requiresAuth)
            {
                if (!context.Attributes.TryGetValue("PatientAuthorization", out var authObj) || authObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "HIPAA-002",
                        Description = "Patient authorization required but not documented",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Obtain valid written patient authorization before use/disclosure",
                        RegulatoryReference = "45 CFR 164.508"
                    });
                }
            }

            // Check for psychotherapy notes
            if (context.DataSubjectCategories.Any(c => c.Contains("psychotherapy", StringComparison.OrdinalIgnoreCase)))
            {
                if (!context.Attributes.TryGetValue("PsychotherapyNotesAuthorization", out var ptAuthObj) || ptAuthObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "HIPAA-003",
                        Description = "Psychotherapy notes require specific authorization",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Obtain separate authorization specifically for psychotherapy notes",
                        RegulatoryReference = "45 CFR 164.508(a)(2)"
                    });
                }
            }
        }

        private void CheckSafeguards(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            // Administrative safeguards
            if (!context.Attributes.TryGetValue("SecurityOfficerAssigned", out var soObj) || soObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "HIPAA-004",
                    Description = "Security official not designated",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Designate a security official responsible for HIPAA security policies",
                    RegulatoryReference = "45 CFR 164.308(a)(2)"
                });
            }

            // Physical safeguards
            if (context.OperationType.Contains("physical", StringComparison.OrdinalIgnoreCase) ||
                context.OperationType.Contains("access", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("FacilityAccessControlled", out var facObj) || facObj is not true)
                {
                    recommendations.Add("Document facility access controls and physical safeguards");
                }
            }

            // Technical safeguards
            if (!context.Attributes.TryGetValue("EncryptionEnabled", out var encObj) || encObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "HIPAA-005",
                    Description = "PHI encryption not confirmed",
                    Severity = ViolationSeverity.High,
                    Remediation = "Enable encryption for PHI at rest and in transit",
                    RegulatoryReference = "45 CFR 164.312(a)(2)(iv), 164.312(e)(2)(ii)"
                });
            }
        }

        private void CheckAccessControls(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            // Unique user identification
            if (!context.Attributes.TryGetValue("UniqueUserIdentification", out var uuidObj) || uuidObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "HIPAA-006",
                    Description = "Unique user identification not implemented",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement unique user identification for PHI access",
                    RegulatoryReference = "45 CFR 164.312(a)(2)(i)"
                });
            }

            // Emergency access procedure
            if (!context.Attributes.TryGetValue("EmergencyAccessProcedure", out var eapObj) || eapObj is not true)
            {
                recommendations.Add("Document emergency access procedures for PHI access during emergencies");
            }

            // Automatic logoff
            if (!context.Attributes.TryGetValue("AutomaticLogoff", out var alObj) || alObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "HIPAA-007",
                    Description = "Automatic logoff not implemented",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Implement automatic logoff for sessions accessing PHI",
                    RegulatoryReference = "45 CFR 164.312(a)(2)(iii)"
                });
            }
        }

        private void CheckAuditControls(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("AuditLoggingEnabled", out var auditObj) || auditObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "HIPAA-008",
                    Description = "Audit logging not enabled for PHI access",
                    Severity = ViolationSeverity.High,
                    Remediation = "Enable audit logging for all PHI access and modifications",
                    RegulatoryReference = "45 CFR 164.312(b)"
                });
            }

            if (!context.Attributes.TryGetValue("AuditLogReviewSchedule", out var scheduleObj) || scheduleObj is not true)
            {
                recommendations.Add("Implement regular audit log review schedule");
            }
        }

        private void CheckBreachNotification(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.OperationType.Contains("breach", StringComparison.OrdinalIgnoreCase) ||
                context.OperationType.Contains("incident", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("BreachAssessmentCompleted", out var assessObj) || assessObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "HIPAA-009",
                        Description = "Breach risk assessment not documented",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Complete breach risk assessment to determine notification requirements",
                        RegulatoryReference = "45 CFR 164.402"
                    });
                }
            }

            if (!context.Attributes.TryGetValue("BreachNotificationPlan", out var planObj) || planObj is not true)
            {
                recommendations.Add("Ensure breach notification plan is documented and tested");
            }
        }

        private void CheckBusinessAssociates(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.Attributes.TryGetValue("ThirdPartyInvolved", out var tpObj) && tpObj is true)
            {
                if (!context.Attributes.TryGetValue("BusinessAssociateAgreement", out var baaObj) || baaObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "HIPAA-010",
                        Description = "Business Associate Agreement (BAA) not in place for third party handling PHI",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Execute BAA with business associate before sharing PHI",
                        RegulatoryReference = "45 CFR 164.502(e), 164.504(e)"
                    });
                }
            }
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("hipaa.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("hipaa.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
