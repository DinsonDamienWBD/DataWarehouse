using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.MiddleEastAfrica
{
    /// <summary>
    /// POPIA (Protection of Personal Information Act - South Africa) compliance strategy.
    /// </summary>
    public sealed class PopiaStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "popia";
        public override string StrategyName => "POPIA Compliance";
        public override string Framework => "POPIA";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
        IncrementCounter("popia.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("ConditionsForProcessing", out var conditionsObj) || conditionsObj is not true)
            {
                violations.Add(new ComplianceViolation { Code = "POPIA-001", Description = "Conditions for lawful processing not met", Severity = ViolationSeverity.Critical, Remediation = "Ensure all eight conditions for lawful processing", RegulatoryReference = "POPIA Section 4" });
            }

            if (context.OperationType.Equals("direct-marketing", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("OptInConsent", out var consentObj) || consentObj is not true)
                {
                    violations.Add(new ComplianceViolation { Code = "POPIA-002", Description = "Direct marketing without opt-in consent", Severity = ViolationSeverity.High, Remediation = "Obtain prior consent for direct marketing", RegulatoryReference = "POPIA Section 69" });
                }
            }

            if (!context.Attributes.TryGetValue("PriorAuthorization", out var authObj))
            {
                if (context.DataClassification.Equals("special-personal", StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add(new ComplianceViolation { Code = "POPIA-003", Description = "Special personal information requires prior authorization", Severity = ViolationSeverity.High, Remediation = "Obtain Information Regulator authorization", RegulatoryReference = "POPIA Section 26-32" });
                }
            }

            if (!context.Attributes.TryGetValue("InfoOfficerAppointed", out var officerObj) || officerObj is not true)
            {
                violations.Add(new ComplianceViolation { Code = "POPIA-004", Description = "Information Officer not appointed", Severity = ViolationSeverity.High, Remediation = "Appoint and register Information Officer", RegulatoryReference = "POPIA Section 55" });
            }

            var isCompliant = !violations.Any(v => v.Severity >= ViolationSeverity.High);
            var status = violations.Count == 0 ? ComplianceStatus.Compliant : violations.Any(v => v.Severity >= ViolationSeverity.High) ? ComplianceStatus.NonCompliant : ComplianceStatus.PartiallyCompliant;
            return Task.FromResult(new ComplianceResult { IsCompliant = isCompliant, Framework = Framework, Status = status, Violations = violations, Recommendations = recommendations });
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("popia.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("popia.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
