using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Americas
{
    /// <summary>
    /// Chile Data Protection Law compliance strategy.
    /// </summary>
    public sealed class ChileDataStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "chile-data";
        public override string StrategyName => "Chile Data Protection Law Compliance";
        public override string Framework => "CHILE-DPL";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("chile_data.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("ConsentObtained", out var consentObj) || consentObj is not true)
            {
                violations.Add(new ComplianceViolation { Code = "CL-001", Description = "Consent not obtained", Severity = ViolationSeverity.High, Remediation = "Obtain consent for personal data processing", RegulatoryReference = "Law 19.628 Art. 4" });
            }

            if (!string.IsNullOrEmpty(context.OperationType) && context.OperationType.Equals("access-request", StringComparison.OrdinalIgnoreCase))
            {
                if (context.Attributes.TryGetValue("ResponseDays", out var daysObj) && daysObj is int days && days > 10)
                {
                    violations.Add(new ComplianceViolation { Code = "CL-002", Description = $"Access request not fulfilled within 10 days ({days} days)", Severity = ViolationSeverity.High, Remediation = "Respond to access requests within 10 days", RegulatoryReference = "Law 19.628 Art. 16" });
                }
            }

            if (!context.Attributes.TryGetValue("DpaRegistered", out var dpaObj) || dpaObj is not true)
            {
                recommendations.Add("Consider registering with Chilean Data Protection Agency");
            }

            var hasHighViolations = violations.Any(v => v.Severity >= ViolationSeverity.High);
            var isCompliant = !hasHighViolations;
            var status = violations.Count == 0 ? ComplianceStatus.Compliant : hasHighViolations ? ComplianceStatus.NonCompliant : ComplianceStatus.PartiallyCompliant;
            return Task.FromResult(new ComplianceResult { IsCompliant = isCompliant, Framework = Framework, Status = status, Violations = violations, Recommendations = recommendations });
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("chile_data.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("chile_data.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
