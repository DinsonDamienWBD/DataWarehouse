using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.MiddleEastAfrica
{
    /// <summary>
    /// Bahrain Personal Data Protection Law compliance strategy.
    /// </summary>
    public sealed class BahrainPdpStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "bahrain-pdp";
        public override string StrategyName => "Bahrain PDP Law Compliance";
        public override string Framework => "BAHRAIN-PDP";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("bahrain_pdp.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("ConsentObtained", out var consentObj) || consentObj is not true)
            {
                violations.Add(new ComplianceViolation { Code = "BH-001", Description = "Consent not obtained", Severity = ViolationSeverity.High, Remediation = "Obtain valid consent", RegulatoryReference = "Bahrain PDPL Law No. 30 of 2018" });
            }

            if (context.DataSubjectCategories.Contains("special-category", StringComparer.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("SpecialConsent", out var specialObj) || specialObj is not true)
                {
                    violations.Add(new ComplianceViolation { Code = "BH-002", Description = "Special category data without explicit consent", Severity = ViolationSeverity.Critical, Remediation = "Obtain explicit consent for special categories", RegulatoryReference = "Bahrain PDPL Art. 11" });
                }
            }

            if (!context.Attributes.TryGetValue("DpoAppointed", out var dpoObj) || dpoObj is not true)
            {
                recommendations.Add("Consider appointing Data Protection Officer");
            }

            var isCompliant = !violations.Any(v => v.Severity >= ViolationSeverity.High);
            var status = violations.Count == 0 ? ComplianceStatus.Compliant : violations.Any(v => v.Severity >= ViolationSeverity.High) ? ComplianceStatus.NonCompliant : ComplianceStatus.PartiallyCompliant;
            return Task.FromResult(new ComplianceResult { IsCompliant = isCompliant, Framework = Framework, Status = status, Violations = violations, Recommendations = recommendations });
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("bahrain_pdp.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("bahrain_pdp.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
