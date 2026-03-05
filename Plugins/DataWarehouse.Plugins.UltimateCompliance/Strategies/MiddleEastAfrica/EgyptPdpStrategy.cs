using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.MiddleEastAfrica
{
    /// <summary>
    /// Egypt Personal Data Protection Law compliance strategy.
    /// </summary>
    public sealed class EgyptPdpStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "egypt-pdp";
        public override string StrategyName => "Egypt PDP Law Compliance";
        public override string Framework => "EGYPT-PDP";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("egypt_pdp.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("ConsentObtained", out var consentObj) || consentObj is not true)
            {
                violations.Add(new ComplianceViolation { Code = "EG-001", Description = "Consent not obtained", Severity = ViolationSeverity.High, Remediation = "Obtain informed consent", RegulatoryReference = "Egypt PDPL Law No. 151 of 2020" });
            }

            if (!string.IsNullOrEmpty(context.DestinationLocation) && !context.DestinationLocation.Equals("EG", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("CrossBorderApproval", out var approvalObj) || approvalObj is not true)
                {
                    violations.Add(new ComplianceViolation { Code = "EG-002", Description = $"Cross-border transfer to {context.DestinationLocation} without approval", Severity = ViolationSeverity.High, Remediation = "Obtain DPA approval", RegulatoryReference = "Egypt PDPL Art. 12" });
                }
            }

            if (!string.IsNullOrEmpty(context.DataClassification) && context.DataClassification.Equals("sensitive", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("SensitiveDataConsent", out var sensitiveObj) || sensitiveObj is not true)
                {
                    violations.Add(new ComplianceViolation { Code = "EG-003", Description = "Sensitive data without explicit consent", Severity = ViolationSeverity.Critical, Remediation = "Obtain explicit consent", RegulatoryReference = "Egypt PDPL Art. 4" });
                }
            }

            var hasHighViolations = violations.Any(v => v.Severity >= ViolationSeverity.High);
            var isCompliant = !hasHighViolations;
            var status = violations.Count == 0 ? ComplianceStatus.Compliant : hasHighViolations ? ComplianceStatus.NonCompliant : ComplianceStatus.PartiallyCompliant;
            return Task.FromResult(new ComplianceResult { IsCompliant = isCompliant, Framework = Framework, Status = status, Violations = violations, Recommendations = recommendations });
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("egypt_pdp.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("egypt_pdp.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
