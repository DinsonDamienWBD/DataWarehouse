using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.USState
{
    /// <summary>
    /// CTDPA (Connecticut Data Privacy Act) compliance strategy.
    /// </summary>
    public sealed class CtdpaStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "ctdpa";
        public override string StrategyName => "Connecticut Data Privacy Act Compliance";
        public override string Framework => "CTDPA";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("ctdpa.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (context.DataClassification.Equals("sensitive", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("ConsentObtained", out var consentObj) || consentObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "CTDPA-001",
                        Description = "Sensitive data processing without consent",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Obtain opt-in consent for sensitive data",
                        RegulatoryReference = "CTDPA § 42-520(a)"
                    });
                }
            }

            if (context.OperationType.Equals("profiling", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("DataProtectionAssessment", out var dpaObj) || dpaObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "CTDPA-002",
                        Description = "Data protection assessment not conducted for profiling",
                        Severity = ViolationSeverity.Medium,
                        Remediation = "Conduct data protection assessment",
                        RegulatoryReference = "CTDPA § 42-523"
                    });
                }
            }

            if (context.OperationType.Equals("access-request", StringComparison.OrdinalIgnoreCase))
            {
                if (context.Attributes.TryGetValue("ResponseDays", out var daysObj) && daysObj is int days && days > 45)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "CTDPA-003",
                        Description = $"Request not responded within 45 days ({days} days)",
                        Severity = ViolationSeverity.High,
                        Remediation = "Respond to requests within 45 days",
                        RegulatoryReference = "CTDPA § 42-521(b)"
                    });
                }
            }

            var isCompliant = !violations.Any(v => v.Severity >= ViolationSeverity.High);
            var status = violations.Count == 0 ? ComplianceStatus.Compliant :
                        violations.Any(v => v.Severity >= ViolationSeverity.High) ? ComplianceStatus.NonCompliant :
                        ComplianceStatus.PartiallyCompliant;

            return Task.FromResult(new ComplianceResult { IsCompliant = isCompliant, Framework = Framework, Status = status, Violations = violations, Recommendations = recommendations });
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("ctdpa.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("ctdpa.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
