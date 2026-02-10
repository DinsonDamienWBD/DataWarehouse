using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.USState
{
    /// <summary>
    /// CPA (Colorado Privacy Act) compliance strategy.
    /// </summary>
    public sealed class CpaStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "cpa";
        public override string StrategyName => "Colorado Privacy Act Compliance";
        public override string Framework => "CPA";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("UniversalOptOutHonored", out var uooObj) || uooObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CPA-001",
                    Description = "Universal opt-out mechanism not honored",
                    Severity = ViolationSeverity.High,
                    Remediation = "Honor universal opt-out preference signals",
                    RegulatoryReference = "CPA § 6-1-1306(1)(a)(I)(D)"
                });
            }

            if (context.OperationType.Equals("profiling", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("ProfilingDisclosed", out var disclosedObj) || disclosedObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "CPA-002",
                        Description = "Profiling activity not disclosed",
                        Severity = ViolationSeverity.High,
                        Remediation = "Disclose profiling and provide opt-out",
                        RegulatoryReference = "CPA § 6-1-1306(1)(a)(II)"
                    });
                }
            }

            if (context.DataClassification.Equals("sensitive", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("ConsentObtained", out var consentObj) || consentObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "CPA-003",
                        Description = "Sensitive data processing without consent",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Obtain opt-in consent for sensitive data",
                        RegulatoryReference = "CPA § 6-1-1308(1)(a)"
                    });
                }
            }

            var isCompliant = !violations.Any(v => v.Severity >= ViolationSeverity.High);
            var status = violations.Count == 0 ? ComplianceStatus.Compliant :
                        violations.Any(v => v.Severity >= ViolationSeverity.High) ? ComplianceStatus.NonCompliant :
                        ComplianceStatus.PartiallyCompliant;

            return Task.FromResult(new ComplianceResult { IsCompliant = isCompliant, Framework = Framework, Status = status, Violations = violations, Recommendations = recommendations });
        }
    }
}
