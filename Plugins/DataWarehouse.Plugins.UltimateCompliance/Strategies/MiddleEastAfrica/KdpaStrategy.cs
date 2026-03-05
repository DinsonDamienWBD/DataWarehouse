using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.MiddleEastAfrica
{
    /// <summary>
    /// KDPA (Kenya Data Protection Act) compliance strategy.
    /// </summary>
    public sealed class KdpaStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "kdpa";
        public override string StrategyName => "KDPA Compliance";
        public override string Framework => "KDPA";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("kdpa.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("RegisteredWithCommissioner", out var regObj) || regObj is not true)
            {
                violations.Add(new ComplianceViolation { Code = "KDPA-001", Description = "Not registered with Data Protection Commissioner", Severity = ViolationSeverity.Critical, Remediation = "Register with ODPC Kenya", RegulatoryReference = "KDPA Section 28" });
            }

            if (!context.Attributes.TryGetValue("ConsentObtained", out var consentObj) || consentObj is not true)
            {
                violations.Add(new ComplianceViolation { Code = "KDPA-002", Description = "Consent not obtained", Severity = ViolationSeverity.High, Remediation = "Obtain valid consent", RegulatoryReference = "KDPA Section 30" });
            }

            if (!string.IsNullOrEmpty(context.OperationType) && context.OperationType.Equals("access-request", StringComparison.OrdinalIgnoreCase))
            {
                // Accept int, long, double, or string to avoid silent miss when stored as non-int (finding 1466)
                if (context.Attributes.TryGetValue("ResponseDays", out var daysObj) &&
                    TryGetDays(daysObj, out var days) && days > 30)
                {
                    violations.Add(new ComplianceViolation { Code = "KDPA-003", Description = $"Access request exceeded 30 days ({days} days)", Severity = ViolationSeverity.High, Remediation = "Respond within 30 days", RegulatoryReference = "KDPA Section 38" });
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
            IncrementCounter("kdpa.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("kdpa.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
