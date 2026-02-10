using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.USFederal
{
    /// <summary>
    /// ITAR (International Traffic in Arms Regulations) compliance strategy.
    /// Validates export control requirements for defense articles and technical data.
    /// </summary>
    public sealed class ItarStrategy : ComplianceStrategyBase
    {
        private readonly HashSet<string> _restrictedCountries = new(StringComparer.OrdinalIgnoreCase)
        {
            "CN", "RU", "IR", "KP", "SY", "CU", "VE"
        };

        /// <inheritdoc/>
        public override string StrategyId => "itar";

        /// <inheritdoc/>
        public override string StrategyName => "ITAR Compliance";

        /// <inheritdoc/>
        public override string Framework => "ITAR";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            CheckExportLicense(context, violations, recommendations);
            CheckTechnicalData(context, violations, recommendations);
            CheckForeignPersonAccess(context, violations, recommendations);
            CheckRegistrationRequirement(context, violations, recommendations);

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

        private void CheckExportLicense(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.DataClassification.Equals("itar", StringComparison.OrdinalIgnoreCase) ||
                context.DataSubjectCategories.Contains("defense-article", StringComparer.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(context.DestinationLocation) &&
                    !context.DestinationLocation.Equals("US", StringComparison.OrdinalIgnoreCase))
                {
                    if (!context.Attributes.TryGetValue("ExportLicense", out var licenseObj) || licenseObj is not true)
                    {
                        violations.Add(new ComplianceViolation
                        {
                            Code = "ITAR-001",
                            Description = $"ITAR-controlled data export to {context.DestinationLocation} without license",
                            Severity = ViolationSeverity.Critical,
                            Remediation = "Obtain State Department export license or TAA before transfer",
                            RegulatoryReference = "ITAR 22 CFR 120-130"
                        });
                    }

                    if (_restrictedCountries.Contains(context.DestinationLocation))
                    {
                        violations.Add(new ComplianceViolation
                        {
                            Code = "ITAR-002",
                            Description = $"Transfer to embargoed/restricted country: {context.DestinationLocation}",
                            Severity = ViolationSeverity.Critical,
                            Remediation = "ITAR prohibits export to embargoed countries",
                            RegulatoryReference = "ITAR 22 CFR 126.1"
                        });
                    }
                }
            }
        }

        private void CheckTechnicalData(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.DataSubjectCategories.Contains("technical-data", StringComparer.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("UsmlCategory", out var categoryObj) ||
                    categoryObj is not string category ||
                    string.IsNullOrEmpty(category))
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "ITAR-003",
                        Description = "Technical data not classified under USML category",
                        Severity = ViolationSeverity.High,
                        Remediation = "Classify technical data under appropriate USML category",
                        RegulatoryReference = "ITAR 22 CFR 121"
                    });
                }

                if (!context.Attributes.TryGetValue("DataMarked", out var markedObj) || markedObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "ITAR-004",
                        Description = "ITAR technical data not properly marked",
                        Severity = ViolationSeverity.High,
                        Remediation = "Mark all ITAR technical data with export control warnings",
                        RegulatoryReference = "ITAR 22 CFR 120.10"
                    });
                }
            }
        }

        private void CheckForeignPersonAccess(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.Attributes.TryGetValue("ForeignPersonAccess", out var foreignObj) && foreignObj is true)
            {
                if (!context.Attributes.TryGetValue("TechnicalAssistanceAgreement", out var taaObj) || taaObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "ITAR-005",
                        Description = "Foreign person access to ITAR data without TAA",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Obtain Technical Assistance Agreement (TAA) before foreign person access",
                        RegulatoryReference = "ITAR 22 CFR 124"
                    });
                }

                if (context.Attributes.TryGetValue("ForeignNationality", out var nationalityObj) &&
                    nationalityObj is string nationality &&
                    _restrictedCountries.Contains(nationality))
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "ITAR-006",
                        Description = $"Access granted to foreign national from restricted country: {nationality}",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Deny access to nationals from embargoed countries",
                        RegulatoryReference = "ITAR 22 CFR 126.1"
                    });
                }
            }
        }

        private void CheckRegistrationRequirement(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.Attributes.TryGetValue("DefenseArticleManufacturer", out var mfgObj) && mfgObj is true)
            {
                if (!context.Attributes.TryGetValue("DdtcRegistered", out var regObj) || regObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "ITAR-007",
                        Description = "Entity not registered with DDTC",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Register with Directorate of Defense Trade Controls (DDTC)",
                        RegulatoryReference = "ITAR 22 CFR 122"
                    });
                }
            }
        }
    }
}
