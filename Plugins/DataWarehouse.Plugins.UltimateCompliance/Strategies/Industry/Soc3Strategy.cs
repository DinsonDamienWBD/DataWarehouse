using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Industry
{
    /// <summary>
    /// SOC 3 Trust Services Criteria compliance strategy.
    /// </summary>
    public sealed class Soc3Strategy : ComplianceStrategyBase
    {
        public override string StrategyId => "soc3";
        public override string StrategyName => "SOC 3";
        public override string Framework => "SOC3";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
        IncrementCounter("soc3.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("SecurityCriteriaMet", out var secObj) || secObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "SOC3-TSC-SECURITY",
                    Description = "Security Trust Services Criteria not met",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Implement controls to protect against unauthorized access (CC6)",
                    RegulatoryReference = "TSC Security Criteria CC6"
                });
            }

            if (!context.Attributes.TryGetValue("AvailabilityCriteriaMet", out var availObj) || availObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "SOC3-TSC-AVAILABILITY",
                    Description = "Availability Trust Services Criteria not met",
                    Severity = ViolationSeverity.High,
                    Remediation = "Ensure system availability meets commitments (A1)",
                    RegulatoryReference = "TSC Availability Criteria A1"
                });
            }

            if (!context.Attributes.TryGetValue("ConfidentialityCriteriaMet", out var confObj) || confObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "SOC3-TSC-CONFIDENTIALITY",
                    Description = "Confidentiality Trust Services Criteria not met",
                    Severity = ViolationSeverity.High,
                    Remediation = "Protect confidential information as committed (C1)",
                    RegulatoryReference = "TSC Confidentiality Criteria C1"
                });
            }

            if (!context.Attributes.TryGetValue("Soc3SealObtained", out var sealObj) || sealObj is not true)
            {
                recommendations.Add("Obtain SOC 3 seal for public display of trust services compliance");
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
        IncrementCounter("soc3.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("soc3.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
