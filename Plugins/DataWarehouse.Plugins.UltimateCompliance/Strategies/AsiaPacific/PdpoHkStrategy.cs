using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.AsiaPacific
{
    /// <summary>
    /// Hong Kong Personal Data (Privacy) Ordinance compliance strategy.
    /// </summary>
    public sealed class PdpoHkStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "pdpo-hk";
        public override string StrategyName => "Hong Kong PDPO";
        public override string Framework => "PDPO-HK";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("pdpo_hk.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("PurposeNotified", out var purposeObj) || purposeObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "DPP1-001",
                    Description = "Purpose and intended use not notified on or before collection",
                    Severity = ViolationSeverity.High,
                    Remediation = "Inform data subject of purpose and use on or before collection (DPP1)",
                    RegulatoryReference = "PDPO Schedule 1 DPP1"
                });
            }

            if (!string.IsNullOrEmpty(context.DestinationLocation) && !context.DestinationLocation.StartsWith("HK", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("CrossBorderSafeguards", out var cbObj) || cbObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "DPP33-001",
                        Description = "Cross-border transfer without adequate safeguards",
                        Severity = ViolationSeverity.High,
                        Remediation = "Implement contractual or other means to ensure comparable protection (DPP33)",
                        RegulatoryReference = "PDPO Schedule 1 DPP33"
                    });
                }
            }

            if (context.OperationType.Contains("marketing", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("DirectMarketingConsent", out var dmObj) || dmObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "PDPO-35C",
                        Description = "Consent not obtained for direct marketing use",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Obtain consent before using personal data for direct marketing (Section 35C)",
                        RegulatoryReference = "PDPO Section 35C"
                    });
                }
            }

            if (!context.Attributes.TryGetValue("SecurityMeasures", out var secObj) || secObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "DPP4-001",
                    Description = "Security safeguards not implemented",
                    Severity = ViolationSeverity.High,
                    Remediation = "Take practical steps to safeguard personal data (DPP4)",
                    RegulatoryReference = "PDPO Schedule 1 DPP4"
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
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("pdpo_hk.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("pdpo_hk.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
