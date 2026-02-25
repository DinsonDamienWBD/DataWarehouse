using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.USState
{
    /// <summary>
    /// UTCPA (Utah Consumer Privacy Act) compliance strategy.
    /// </summary>
    public sealed class UtcpaStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "utcpa";
        public override string StrategyName => "Utah Consumer Privacy Act Compliance";
        public override string Framework => "UTCPA";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
        IncrementCounter("utcpa.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("PrivacyNoticeProvided", out var noticeObj) || noticeObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "UTCPA-001",
                    Description = "Privacy notice not provided to consumers",
                    Severity = ViolationSeverity.High,
                    Remediation = "Provide clear privacy notice",
                    RegulatoryReference = "UTCPA § 13-61-302"
                });
            }

            if (context.OperationType.Equals("data-sale", StringComparison.OrdinalIgnoreCase))
            {
                if (context.Attributes.TryGetValue("OptOutRequested", out var optOutObj) && optOutObj is true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "UTCPA-002",
                        Description = "Data sold despite consumer opt-out",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Honor consumer opt-out requests",
                        RegulatoryReference = "UTCPA § 13-61-303(2)"
                    });
                }
            }

            if (context.DataClassification.Equals("sensitive", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("ConsentObtained", out var consentObj) || consentObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "UTCPA-003",
                        Description = "Sensitive data processing without consent",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Obtain consent before processing sensitive data",
                        RegulatoryReference = "UTCPA § 13-61-302(3)"
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
        IncrementCounter("utcpa.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("utcpa.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
