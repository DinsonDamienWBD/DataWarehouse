using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.AsiaPacific
{
    /// <summary>
    /// Vietnam Personal Data Protection Decree compliance strategy.
    /// </summary>
    public sealed class PdpaVnStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "pdpa-vn";
        public override string StrategyName => "Vietnam PDPA";
        public override string Framework => "PDPA-VN";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("ConsentObtained", out var consentObj) || consentObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "PDPA-VN-011",
                    Description = "Consent not obtained for personal data processing",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Obtain consent or establish legal basis for processing (Decree 13 Art. 11)",
                    RegulatoryReference = "Decree 13/2023 Article 11"
                });
            }

            if (!string.IsNullOrEmpty(context.DestinationLocation) && !context.DestinationLocation.StartsWith("VN", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("CrossBorderTransferApproved", out var cbObj) || cbObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "PDPA-VN-026",
                        Description = "Cross-border transfer without adequate measures",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Implement adequate safeguards for international transfer (Decree 13 Art. 26)",
                        RegulatoryReference = "Decree 13/2023 Article 26"
                    });
                }
            }

            bool requiresLocalization = context.Attributes.TryGetValue("RequiresDataLocalization", out var localObj) && localObj is true;
            if (requiresLocalization && (!context.Attributes.TryGetValue("DataStoredInVietnam", out var storedObj) || storedObj is not true))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "PDPA-VN-026",
                    Description = "Data localization requirement not met",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Store copy of personal data in Vietnam for specified categories (Decree 13 Art. 26)",
                    RegulatoryReference = "Decree 13/2023 Article 26"
                });
            }

            if (!context.Attributes.TryGetValue("SecurityMeasures", out var secObj) || secObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "PDPA-VN-019",
                    Description = "Security measures not implemented",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement technical and organizational security measures (Decree 13 Art. 19)",
                    RegulatoryReference = "Decree 13/2023 Article 19"
                });
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
                Recommendations = recommendations
            });
        }
    }
}
