using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Industry
{
    /// <summary>
    /// HITRUST CSF (Common Security Framework) compliance strategy for healthcare.
    /// </summary>
    public sealed class HitrustStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "hitrust";
        public override string StrategyName => "HITRUST CSF";
        public override string Framework => "HITRUST";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("hitrust.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("RiskAssessmentPerformed", out var riskObj) || riskObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "HITRUST-03.01",
                    Description = "Risk assessment not performed",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Conduct comprehensive risk assessment per HITRUST methodology (03.01)",
                    RegulatoryReference = "HITRUST CSF 03.01"
                });
            }

            if (!context.Attributes.TryGetValue("InformationSecurityPolicy", out var policyObj) || policyObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "HITRUST-02.01",
                    Description = "Information security policy not established",
                    Severity = ViolationSeverity.High,
                    Remediation = "Establish and maintain information security policy (02.01)",
                    RegulatoryReference = "HITRUST CSF 02.01"
                });
            }

            if (!context.Attributes.TryGetValue("AccessControlPolicy", out var accessObj) || accessObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "HITRUST-01.02",
                    Description = "Access control policy not implemented",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement role-based access control with least privilege (01.02)",
                    RegulatoryReference = "HITRUST CSF 01.02"
                });
            }

            if (context.DataClassification.Contains("PHI", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("EncryptionEnabled", out var encryptObj) || encryptObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "HITRUST-10.01",
                        Description = "Encryption not applied to PHI",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Encrypt PHI at rest and in transit (10.01)",
                        RegulatoryReference = "HITRUST CSF 10.01"
                    });
                }
            }

            recommendations.Add("Consider HITRUST r2 certification for comprehensive assurance");

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
            IncrementCounter("hitrust.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("hitrust.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
