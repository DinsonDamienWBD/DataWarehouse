using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.MiddleEastAfrica
{
    /// <summary>
    /// PDPL (Personal Data Protection Law - Saudi Arabia) compliance strategy.
    /// </summary>
    public sealed class PdplSaStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "pdpl-sa";
        public override string StrategyName => "Saudi Arabia PDPL Compliance";
        public override string Framework => "PDPL-SA";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("pdpl_sa.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("ConsentObtained", out var consentObj) || consentObj is not true)
            {
                violations.Add(new ComplianceViolation { Code = "PDPLSA-001", Description = "Consent not obtained", Severity = ViolationSeverity.Critical, Remediation = "Obtain explicit consent", RegulatoryReference = "Saudi PDPL Art. 5" });
            }

            if (!string.IsNullOrEmpty(context.DestinationLocation) && !context.DestinationLocation.Equals("SA", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("CrossBorderApproval", out var approvalObj) || approvalObj is not true)
                {
                    violations.Add(new ComplianceViolation { Code = "PDPLSA-002", Description = $"Cross-border transfer to {context.DestinationLocation} without approval", Severity = ViolationSeverity.High, Remediation = "Obtain SDAIA approval for transfers", RegulatoryReference = "Saudi PDPL Art. 22" });
                }
            }

            if (context.DataSubjectCategories.Contains("health-data", StringComparer.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("SpecialCategoryConsent", out var specialObj) || specialObj is not true)
                {
                    violations.Add(new ComplianceViolation { Code = "PDPLSA-003", Description = "Health data without special consent", Severity = ViolationSeverity.Critical, Remediation = "Obtain explicit consent for health data", RegulatoryReference = "Saudi PDPL Art. 7" });
                }
            }

            var isCompliant = !violations.Any(v => v.Severity >= ViolationSeverity.High);
            var status = violations.Count == 0 ? ComplianceStatus.Compliant : violations.Any(v => v.Severity >= ViolationSeverity.High) ? ComplianceStatus.NonCompliant : ComplianceStatus.PartiallyCompliant;
            return Task.FromResult(new ComplianceResult { IsCompliant = isCompliant, Framework = Framework, Status = status, Violations = violations, Recommendations = recommendations });
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("pdpl_sa.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("pdpl_sa.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
