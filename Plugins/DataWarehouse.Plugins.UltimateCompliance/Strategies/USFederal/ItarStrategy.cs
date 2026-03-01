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
        // LOW-1557: ITAR restricted country lists change via regulation; the default set covers
        // the 22 CFR Part 126.1 embargoed countries as of the strategy's last review date.
        // Override via configuration key "RestrictedCountries" (comma-separated ISO-2 codes).
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
            IncrementCounter("itar.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            CheckExportLicense(context, violations, recommendations);
            CheckTechnicalData(context, violations, recommendations);
            CheckForeignPersonAccess(context, violations, recommendations);
            CheckRegistrationRequirement(context, violations, recommendations);

            var hasHighViolations = violations.Any(v => v.Severity >= ViolationSeverity.High);
            var isCompliant = !hasHighViolations;
            var status = violations.Count == 0 ? ComplianceStatus.Compliant :
                        hasHighViolations ? ComplianceStatus.NonCompliant :
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
            if (!string.IsNullOrEmpty(context.DataClassification) && context.DataClassification.Equals("itar", StringComparison.OrdinalIgnoreCase) ||
                (context.DataSubjectCategories != null && context.DataSubjectCategories.Contains("defense-article", StringComparer.OrdinalIgnoreCase)))
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
            if (context.DataSubjectCategories != null && context.DataSubjectCategories.Contains("technical-data", StringComparer.OrdinalIgnoreCase))
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
    
    /// <inheritdoc/>
    public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
    {
        // LOW-1557: Allow operators to supply an up-to-date restricted country list.
        if (configuration.TryGetValue("RestrictedCountries", out var listObj) && listObj is string codes && !string.IsNullOrEmpty(codes))
        {
            _restrictedCountries.Clear();
            foreach (var code in codes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                _restrictedCountries.Add(code);
        }
        return base.InitializeAsync(configuration, cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("itar.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("itar.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
