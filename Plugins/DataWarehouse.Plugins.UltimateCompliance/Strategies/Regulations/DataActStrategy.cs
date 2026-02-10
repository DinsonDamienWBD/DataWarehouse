using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Regulations
{
    /// <summary>
    /// EU Data Act compliance strategy.
    /// Validates data sharing, interoperability, and cloud switching requirements.
    /// </summary>
    public sealed class DataActStrategy : ComplianceStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "eu-data-act";

        /// <inheritdoc/>
        public override string StrategyName => "EU Data Act Compliance";

        /// <inheritdoc/>
        public override string Framework => "EU-DATA-ACT";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            CheckDataAccessRights(context, violations, recommendations);
            CheckInteroperability(context, violations, recommendations);
            CheckCloudSwitching(context, violations, recommendations);
            CheckDataSharing(context, violations, recommendations);

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

        private void CheckDataAccessRights(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.OperationType.Equals("data-access-request", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("DataAccessMechanism", out var mechanismObj) || mechanismObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "DATAACT-001",
                        Description = "No mechanism for user data access in place",
                        Severity = ViolationSeverity.High,
                        Remediation = "Implement mechanism for users to access their IoT/connected product data",
                        RegulatoryReference = "Data Act Article 4"
                    });
                }

                if (!context.Attributes.TryGetValue("RealTimeAccess", out var realtimeObj) || realtimeObj is not true)
                {
                    recommendations.Add("Consider providing real-time data access where technically feasible");
                }
            }
        }

        private void CheckInteroperability(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("OpenStandards", out var standardsObj) || standardsObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "DATAACT-002",
                    Description = "Data not provided in open, interoperable format",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Use open standards and interoperable formats for data provision",
                    RegulatoryReference = "Data Act Article 3"
                });
            }

            if (context.Attributes.TryGetValue("ProprietaryFormat", out var proprietaryObj) && proprietaryObj is true)
            {
                if (!context.Attributes.TryGetValue("ConversionToolProvided", out var toolObj) || toolObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "DATAACT-003",
                        Description = "Proprietary format used without conversion tools",
                        Severity = ViolationSeverity.Medium,
                        Remediation = "Provide tools or documentation for converting proprietary formats",
                        RegulatoryReference = "Data Act Article 3"
                    });
                }
            }
        }

        private void CheckCloudSwitching(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.Attributes.TryGetValue("CloudService", out var cloudObj) && cloudObj is true)
            {
                if (!context.Attributes.TryGetValue("SwitchingTerms", out var termsObj) || termsObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "DATAACT-004",
                        Description = "Cloud service switching terms not clearly documented",
                        Severity = ViolationSeverity.Medium,
                        Remediation = "Document clear switching terms including timeline and data portability",
                        RegulatoryReference = "Data Act Article 23"
                    });
                }

                if (context.Attributes.TryGetValue("SwitchingFees", out var feesObj) && feesObj is true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "DATAACT-005",
                        Description = "Switching fees or penalties imposed on cloud customers",
                        Severity = ViolationSeverity.High,
                        Remediation = "Remove switching fees - Data Act prohibits financial penalties for switching",
                        RegulatoryReference = "Data Act Article 23"
                    });
                }

                if (!context.Attributes.TryGetValue("FunctionalEquivalence", out var equivObj) || equivObj is not true)
                {
                    recommendations.Add("Ensure functional equivalence during cloud switching transitions");
                }
            }
        }

        private void CheckDataSharing(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.OperationType.Equals("b2b-data-sharing", StringComparison.OrdinalIgnoreCase) ||
                context.OperationType.Equals("b2g-data-sharing", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("FairTerms", out var fairObj) || fairObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "DATAACT-006",
                        Description = "Data sharing terms not assessed for fairness",
                        Severity = ViolationSeverity.Medium,
                        Remediation = "Ensure data sharing terms are fair, reasonable, and non-discriminatory",
                        RegulatoryReference = "Data Act Article 6"
                    });
                }

                if (context.Attributes.TryGetValue("TradeSecretProtection", out var secretObj) && secretObj is true)
                {
                    if (!context.Attributes.TryGetValue("TechnicalMeasures", out var measuresObj) || measuresObj is not true)
                    {
                        recommendations.Add("Implement technical measures to protect trade secrets during data sharing");
                    }
                }
            }
        }
    }
}
