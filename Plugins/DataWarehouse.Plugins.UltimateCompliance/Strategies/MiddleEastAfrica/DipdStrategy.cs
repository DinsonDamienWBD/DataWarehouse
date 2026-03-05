using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.MiddleEastAfrica
{
    /// <summary>
    /// DIFC Data Protection Law (UAE DIFC) compliance strategy.
    /// </summary>
    public sealed class DipdStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "difc-dpl";
        public override string StrategyName => "DIFC Data Protection Law Compliance";
        public override string Framework => "DIFC-DPL"; // Corrected: DIFC-DPL (Dubai International Financial Centre Data Protection Law)

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("dipd.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("ProcessingPrinciples", out var principlesObj) || principlesObj is not true)
            {
                violations.Add(new ComplianceViolation { Code = "DIPD-001", Description = "Data processing principles not followed", Severity = ViolationSeverity.High, Remediation = "Follow fair, lawful processing principles", RegulatoryReference = "DIFC Law No. 5 of 2020" });
            }

            if (!string.IsNullOrEmpty(context.DestinationLocation) && !context.DestinationLocation.StartsWith("AE", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("AdequacyDecision", out var adequacyObj) || adequacyObj is not true)
                {
                    violations.Add(new ComplianceViolation { Code = "DIPD-002", Description = $"Cross-border transfer to {context.DestinationLocation} without adequacy", Severity = ViolationSeverity.High, Remediation = "Ensure adequacy or use approved mechanisms", RegulatoryReference = "DIFC DPL Art. 32" });
                }
            }

            if (!string.IsNullOrEmpty(context.OperationType) && context.OperationType.Equals("access-request", StringComparison.OrdinalIgnoreCase))
            {
                // Accept int, long, double, or string to avoid silent miss when stored as non-int (finding 1466)
                if (context.Attributes.TryGetValue("ResponseDays", out var daysObj) &&
                    TryGetDays(daysObj, out var days) && days > 28)
                {
                    violations.Add(new ComplianceViolation { Code = "DIPD-003", Description = $"Access request exceeded 28 days ({days} days)", Severity = ViolationSeverity.High, Remediation = "Respond within 28 days", RegulatoryReference = "DIFC DPL Art. 21" });
                }
            }

            var hasHighViolations = violations.Any(v => v.Severity >= ViolationSeverity.High);
            var isCompliant = !hasHighViolations;
            var status = violations.Count == 0 ? ComplianceStatus.Compliant : hasHighViolations ? ComplianceStatus.NonCompliant : ComplianceStatus.PartiallyCompliant;
            return Task.FromResult(new ComplianceResult { IsCompliant = isCompliant, Framework = Framework, Status = status, Violations = violations, Recommendations = recommendations });
        }
    
    /// <summary>Converts ResponseDays attribute value to int regardless of stored type.</summary>
    private static bool TryGetDays(object? value, out int days)
    {
        days = 0;
        switch (value)
        {
            case int i:
                days = i;
                return days >= 0;
            case long l:
                days = (int)l;
                return days >= 0;
            case double d:
                days = (int)d;
                return days >= 0;
            case string s:
                return int.TryParse(s, out days);
            default:
                return false;
        }
    }

    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("dipd.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("dipd.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
