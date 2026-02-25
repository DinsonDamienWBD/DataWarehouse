using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.MiddleEastAfrica
{
    /// <summary>
    /// Qatar Personal Data Protection Law compliance strategy.
    /// </summary>
    public sealed class QatarPdplStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "qatar-pdpl";
        public override string StrategyName => "Qatar PDPL Compliance";
        public override string Framework => "QATAR-PDPL";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("qatar_pdpl.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("ProcessingLawful", out var lawfulObj) || lawfulObj is not true)
            {
                violations.Add(new ComplianceViolation { Code = "QAPD-001", Description = "Lawful processing basis not established", Severity = ViolationSeverity.High, Remediation = "Establish lawful basis", RegulatoryReference = "Qatar Law No. 13 of 2016" });
            }

            if (!string.IsNullOrEmpty(context.DestinationLocation) && !context.DestinationLocation.Equals("QA", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("TransferApproval", out var transferObj) || transferObj is not true)
                {
                    violations.Add(new ComplianceViolation { Code = "QAPD-002", Description = $"International transfer to {context.DestinationLocation} without approval", Severity = ViolationSeverity.High, Remediation = "Obtain approval for transfers", RegulatoryReference = "Qatar PDPL Art. 15" });
                }
            }

            if (context.DataClassification.Equals("sensitive", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("SensitiveDataConsent", out var consentObj) || consentObj is not true)
                {
                    violations.Add(new ComplianceViolation { Code = "QAPD-003", Description = "Sensitive data without consent", Severity = ViolationSeverity.Critical, Remediation = "Obtain consent for sensitive data", RegulatoryReference = "Qatar PDPL Art. 6" });
                }
            }

            var isCompliant = !violations.Any(v => v.Severity >= ViolationSeverity.High);
            var status = violations.Count == 0 ? ComplianceStatus.Compliant : violations.Any(v => v.Severity >= ViolationSeverity.High) ? ComplianceStatus.NonCompliant : ComplianceStatus.PartiallyCompliant;
            return Task.FromResult(new ComplianceResult { IsCompliant = isCompliant, Framework = Framework, Status = status, Violations = violations, Recommendations = recommendations });
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("qatar_pdpl.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("qatar_pdpl.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
