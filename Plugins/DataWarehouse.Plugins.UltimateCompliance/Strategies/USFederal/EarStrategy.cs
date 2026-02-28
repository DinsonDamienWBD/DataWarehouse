using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.USFederal
{
    /// <summary>
    /// EAR (Export Administration Regulations) compliance strategy.
    /// Validates export control requirements for dual-use items and technology.
    /// </summary>
    public sealed class EarStrategy : ComplianceStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "ear";

        /// <inheritdoc/>
        public override string StrategyName => "EAR Compliance";

        /// <inheritdoc/>
        public override string Framework => "EAR";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("ear.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            CheckEcccClassification(context, violations, recommendations);
            CheckLicenseRequirements(context, violations, recommendations);
            CheckScreeningRequirements(context, violations, recommendations);
            CheckRecordkeeping(context, violations, recommendations);

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

        private void CheckEcccClassification(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!string.IsNullOrEmpty(context.DataClassification) && context.DataClassification.Equals("ear", StringComparison.OrdinalIgnoreCase) ||
                (context.DataSubjectCategories != null && context.DataSubjectCategories.Contains("dual-use", StringComparer.OrdinalIgnoreCase)))
            {
                if (!context.Attributes.TryGetValue("EcccNumber", out var ecccObj) ||
                    ecccObj is not string eccn ||
                    string.IsNullOrEmpty(eccn))
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "EAR-001",
                        Description = "Export Controlled Classification Number (ECCN) not assigned",
                        Severity = ViolationSeverity.High,
                        Remediation = "Classify item under appropriate ECCN or determine EAR99",
                        RegulatoryReference = "EAR 15 CFR 774"
                    });
                }

                if (!context.Attributes.TryGetValue("JurisdictionDetermined", out var jurisdictionObj) || jurisdictionObj is not true)
                {
                    recommendations.Add("Confirm export control jurisdiction (EAR vs ITAR)");
                }
            }
        }

        private void CheckLicenseRequirements(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!string.IsNullOrEmpty(context.DestinationLocation) &&
                !context.DestinationLocation.Equals("US", StringComparison.OrdinalIgnoreCase))
            {
                if (context.Attributes.TryGetValue("LicenseRequired", out var licReqObj) && licReqObj is true)
                {
                    if (!context.Attributes.TryGetValue("BisLicense", out var licenseObj) || licenseObj is not true)
                    {
                        violations.Add(new ComplianceViolation
                        {
                            Code = "EAR-002",
                            Description = $"EAR-controlled item export to {context.DestinationLocation} requires BIS license",
                            Severity = ViolationSeverity.Critical,
                            Remediation = "Obtain Commerce Department (BIS) export license",
                            RegulatoryReference = "EAR 15 CFR 740-774"
                        });
                    }
                }

                if (!context.Attributes.TryGetValue("LicenseExceptionApplied", out var exceptionObj))
                {
                    if (!context.Attributes.TryGetValue("LicenseExceptionAnalyzed", out var analyzedObj) || analyzedObj is not true)
                    {
                        recommendations.Add("Analyze potential license exception eligibility (e.g., ENC, TSU, BAG)");
                    }
                }
            }
        }

        private void CheckScreeningRequirements(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!string.IsNullOrEmpty(context.OperationType) && context.OperationType.Equals("export", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(context.OperationType, "deemed-export", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("DeniedPartiesScreening", out var screeningObj) || screeningObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "EAR-003",
                        Description = "Denied parties screening not conducted",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Screen against Denied Persons List, Entity List, and other restricted parties",
                        RegulatoryReference = "EAR 15 CFR 744"
                    });
                }

                if (context.Attributes.TryGetValue("EndUserRestricted", out var restrictedObj) && restrictedObj is true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "EAR-004",
                        Description = "End user appears on restricted parties list",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Deny export to restricted parties unless licensed exception applies",
                        RegulatoryReference = "EAR 15 CFR 744.11"
                    });
                }

                if (context.Attributes.TryGetValue("EndUseKnownProhibited", out var endUseObj) && endUseObj is true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "EAR-005",
                        Description = "Known prohibited end use (weapons, nuclear, etc.)",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Deny transaction with prohibited end use",
                        RegulatoryReference = "EAR 15 CFR 744.6"
                    });
                }
            }
        }

        private void CheckRecordkeeping(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!string.IsNullOrEmpty(context.OperationType) && context.OperationType.Equals("export", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("ExportRecordMaintained", out var recordObj) || recordObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "EAR-006",
                        Description = "Export transaction records not maintained",
                        Severity = ViolationSeverity.Medium,
                        Remediation = "Maintain export records for 5 years from export date",
                        RegulatoryReference = "EAR 15 CFR 762"
                    });
                }
            }
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("ear.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("ear.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
