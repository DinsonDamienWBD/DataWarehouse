using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.USState
{
    /// <summary>
    /// VCDPA (Virginia Consumer Data Protection Act) compliance strategy.
    /// </summary>
    public sealed class VcdpaStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "vcdpa";
        public override string StrategyName => "VCDPA Compliance";
        public override string Framework => "VCDPA";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("vcdpa.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check consumer rights
            if (!string.IsNullOrEmpty(context.OperationType) && context.OperationType.Equals("access-request", StringComparison.OrdinalIgnoreCase))
            {
                if (context.Attributes.TryGetValue("ResponseDays", out var daysObj) && daysObj is int days && days > 45)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "VCDPA-001",
                        Description = $"Request response exceeded 45 days ({days} days)",
                        Severity = ViolationSeverity.High,
                        Remediation = "Respond to consumer requests within 45 days",
                        RegulatoryReference = "VCDPA § 59.1-577(A)"
                    });
                }
            }

            // Check appeal rights
            if (context.Attributes.TryGetValue("RequestDenied", out var deniedObj) && deniedObj is true)
            {
                if (!context.Attributes.TryGetValue("AppealProcessProvided", out var appealObj) || appealObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "VCDPA-002",
                        Description = "Appeal process not provided after request denial",
                        Severity = ViolationSeverity.High,
                        Remediation = "Inform consumers of appeal rights within 60 days",
                        RegulatoryReference = "VCDPA § 59.1-577(A)(5)"
                    });
                }
            }

            // Check data protection assessment
            if (!string.IsNullOrEmpty(context.OperationType) && context.OperationType.Equals("targeted-advertising", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(context.OperationType, "profiling", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("DataProtectionAssessment", out var dpaObj) || dpaObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "VCDPA-003",
                        Description = "Data protection assessment not conducted for profiling/targeting",
                        Severity = ViolationSeverity.Medium,
                        Remediation = "Conduct and document data protection assessment",
                        RegulatoryReference = "VCDPA § 59.1-580"
                    });
                }
            }

            // Check sensitive data consent
            if (!string.IsNullOrEmpty(context.DataClassification) && context.DataClassification.Equals("sensitive", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("ConsentObtained", out var consentObj) || consentObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "VCDPA-004",
                        Description = "Sensitive data processing without consent",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Obtain consent for sensitive data processing",
                        RegulatoryReference = "VCDPA § 59.1-578(A)(5)"
                    });
                }
            }

            var isCompliant = !violations.Any(v => v.Severity >= ViolationSeverity.High);
            var status = violations.Count == 0 ? ComplianceStatus.Compliant :
                        violations.Any(v => v.Severity >= ViolationSeverity.High) ? ComplianceStatus.NonCompliant :
                        ComplianceStatus.PartiallyCompliant;

            return Task.FromResult(new ComplianceResult
            {
                IsCompliant = isCompliant,
                Framework = Framework,
                Status = status,
                Violations = violations,
                Recommendations = recommendations
            });
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("vcdpa.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("vcdpa.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
