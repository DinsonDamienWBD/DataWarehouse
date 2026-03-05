using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.SecurityFrameworks
{
    /// <summary>
    /// Israel National Cyber Security compliance strategy.
    /// </summary>
    public sealed class IsraelNcsStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "israel-ncs";
        public override string StrategyName => "Israel NCS";
        public override string Framework => "ISRAEL-NCS";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("israel_ncs.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            bool isCriticalInfra = context.Attributes.TryGetValue("IsCriticalInfrastructure", out var ciObj) && ciObj is true;

            if (!isCriticalInfra)
            {
                return Task.FromResult(new ComplianceResult
                {
                    IsCompliant = true,
                    Framework = Framework,
                    Status = ComplianceStatus.NotApplicable,
                    Violations = violations,
                    Recommendations = new List<string> { "Israel NCS applies primarily to critical infrastructure" }
                });
            }

            if (!context.Attributes.TryGetValue("CyberDefenseReady", out var defenseObj) || defenseObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NCS-DEFENSE",
                    Description = "Cyber defense capabilities not established",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Implement cyber defense measures for critical infrastructure protection",
                    RegulatoryReference = "Israel Cyber Defense Guidelines"
                });
            }

            if (!context.Attributes.TryGetValue("IncidentReportingToINCDC", out var reportObj) || reportObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NCS-REPORTING",
                    Description = "Incident reporting to INCDC not configured",
                    Severity = ViolationSeverity.High,
                    Remediation = "Establish incident reporting procedures to Israeli National Cyber Directorate",
                    RegulatoryReference = "INCDC Reporting Requirements"
                });
            }

            if (!context.Attributes.TryGetValue("NationalStandardsCompliance", out var standardsObj) || standardsObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NCS-STANDARDS",
                    Description = "National cyber security standards not adopted",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Adopt Israeli national cyber security standards for critical sectors",
                    RegulatoryReference = "Israel Cyber Security Standards"
                });
            }

            recommendations.Add("Coordinate with Israeli National Cyber Directorate for sector-specific guidance");

            var hasHighViolations = violations.Any(v => v.Severity >= ViolationSeverity.High);
            var isCompliant = !hasHighViolations;
            var status = violations.Count == 0 ? ComplianceStatus.Compliant :
                        hasHighViolations ? ComplianceStatus.NonCompliant :
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
            IncrementCounter("israel_ncs.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("israel_ncs.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
