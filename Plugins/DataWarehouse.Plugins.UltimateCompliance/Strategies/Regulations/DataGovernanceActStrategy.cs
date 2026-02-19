using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Regulations
{
    /// <summary>
    /// EU Data Governance Act compliance strategy.
    /// Validates data intermediary services, data altruism, and governance requirements.
    /// </summary>
    public sealed class DataGovernanceActStrategy : ComplianceStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "eu-data-governance-act";

        /// <inheritdoc/>
        public override string StrategyName => "Data Governance Act Compliance";

        /// <inheritdoc/>
        public override string Framework => "EU-DGA";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
        IncrementCounter("data_governance_act.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            CheckDataIntermediaryRequirements(context, violations, recommendations);
            CheckDataAltruismOrganization(context, violations, recommendations);
            CheckPublicSectorDataReuse(context, violations, recommendations);
            CheckNeutralityRequirements(context, violations, recommendations);

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

        private void CheckDataIntermediaryRequirements(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.Attributes.TryGetValue("DataIntermediaryService", out var intermediaryObj) && intermediaryObj is true)
            {
                if (!context.Attributes.TryGetValue("NotificationAuthority", out var notificationObj) || notificationObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "DGA-001",
                        Description = "Data intermediary service not notified to competent authority",
                        Severity = ViolationSeverity.High,
                        Remediation = "Notify competent authority before providing data intermediary services",
                        RegulatoryReference = "DGA Article 11"
                    });
                }

                if (!context.Attributes.TryGetValue("CommercialUseProhibited", out var commercialObj) || commercialObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "DGA-002",
                        Description = "Data intermediary using data for commercial purposes",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Data intermediaries cannot use data for purposes other than intermediation",
                        RegulatoryReference = "DGA Article 12(c)"
                    });
                }

                if (!context.Attributes.TryGetValue("NeutralityCommitment", out var neutralityObj) || neutralityObj is not true)
                {
                    recommendations.Add("Document commitment to neutrality and transparent fee structure");
                }
            }
        }

        private void CheckDataAltruismOrganization(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.Attributes.TryGetValue("DataAltruismOrganization", out var altruismObj) && altruismObj is true)
            {
                if (!context.Attributes.TryGetValue("RegisteredDaoa", out var registeredObj) || registeredObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "DGA-003",
                        Description = "Data altruism organization not registered",
                        Severity = ViolationSeverity.High,
                        Remediation = "Register as recognized data altruism organization",
                        RegulatoryReference = "DGA Article 18"
                    });
                }

                if (!context.Attributes.TryGetValue("NonProfitPurpose", out var nonprofitObj) || nonprofitObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "DGA-004",
                        Description = "Data altruism organization operating for profit",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Data altruism organizations must operate on not-for-profit basis",
                        RegulatoryReference = "DGA Article 16"
                    });
                }

                if (!context.Attributes.TryGetValue("TransparencyReport", out var reportObj) || reportObj is not true)
                {
                    recommendations.Add("Publish annual transparency report on data altruism activities");
                }
            }
        }

        private void CheckPublicSectorDataReuse(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.Attributes.TryGetValue("PublicSectorData", out var publicObj) && publicObj is true)
            {
                if (context.DataClassification.Equals("confidential", StringComparison.OrdinalIgnoreCase) ||
                    context.DataClassification.Equals("sensitive", StringComparison.OrdinalIgnoreCase))
                {
                    if (!context.Attributes.TryGetValue("CompetentBodyAuthorization", out var authObj) || authObj is not true)
                    {
                        violations.Add(new ComplianceViolation
                        {
                            Code = "DGA-005",
                            Description = "Protected public sector data reuse without authorization",
                            Severity = ViolationSeverity.High,
                            Remediation = "Obtain authorization from competent body for protected data reuse",
                            RegulatoryReference = "DGA Article 5"
                        });
                    }

                    if (!context.Attributes.TryGetValue("TechnicalMeasures", out var measuresObj) || measuresObj is not true)
                    {
                        violations.Add(new ComplianceViolation
                        {
                            Code = "DGA-006",
                            Description = "Insufficient technical measures for protected data",
                            Severity = ViolationSeverity.Medium,
                            Remediation = "Implement technical measures to preserve confidentiality",
                            RegulatoryReference = "DGA Article 5"
                        });
                    }
                }
            }
        }

        private void CheckNeutralityRequirements(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.Attributes.TryGetValue("DataIntermediaryService", out var intermediaryObj) && intermediaryObj is true)
            {
                if (context.Attributes.TryGetValue("ConflictOfInterest", out var conflictObj) && conflictObj is true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "DGA-007",
                        Description = "Conflict of interest in data intermediary operations",
                        Severity = ViolationSeverity.High,
                        Remediation = "Ensure organizational separation from conflicting business activities",
                        RegulatoryReference = "DGA Article 12(b)"
                    });
                }
            }
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("data_governance_act.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("data_governance_act.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
