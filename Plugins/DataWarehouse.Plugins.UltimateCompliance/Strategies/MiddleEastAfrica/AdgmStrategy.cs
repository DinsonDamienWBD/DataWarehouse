using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.MiddleEastAfrica
{
    /// <summary>
    /// ADGM Data Protection Regulations (Abu Dhabi Global Market) compliance strategy.
    /// </summary>
    public sealed class AdgmStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "adgm";
        public override string StrategyName => "ADGM Data Protection Regulations Compliance";
        public override string Framework => "ADGM";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("adgm.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("LawfulBasis", out var basisObj) || basisObj is not string)
            {
                violations.Add(new ComplianceViolation { Code = "ADGM-001", Description = "No lawful basis for processing", Severity = ViolationSeverity.Critical, Remediation = "Establish lawful basis for data processing", RegulatoryReference = "ADGM DPR 2021" });
            }

            if (context.DataClassification.Equals("sensitive", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("ExplicitConsent", out var consentObj) || consentObj is not true)
                {
                    violations.Add(new ComplianceViolation { Code = "ADGM-002", Description = "Sensitive data without explicit consent", Severity = ViolationSeverity.Critical, Remediation = "Obtain explicit consent for sensitive data", RegulatoryReference = "ADGM DPR Art. 8" });
                }
            }

            if (!string.IsNullOrEmpty(context.DestinationLocation))
            {
                if (!context.Attributes.TryGetValue("TransferSafeguards", out var safeguardsObj) || safeguardsObj is not true)
                {
                    violations.Add(new ComplianceViolation { Code = "ADGM-003", Description = $"International transfer to {context.DestinationLocation} without safeguards", Severity = ViolationSeverity.High, Remediation = "Implement appropriate transfer safeguards", RegulatoryReference = "ADGM DPR Chapter 5" });
                }
            }

            var isCompliant = !violations.Any(v => v.Severity >= ViolationSeverity.High);
            var status = violations.Count == 0 ? ComplianceStatus.Compliant : violations.Any(v => v.Severity >= ViolationSeverity.High) ? ComplianceStatus.NonCompliant : ComplianceStatus.PartiallyCompliant;
            return Task.FromResult(new ComplianceResult { IsCompliant = isCompliant, Framework = Framework, Status = status, Violations = violations, Recommendations = recommendations });
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("adgm.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("adgm.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
