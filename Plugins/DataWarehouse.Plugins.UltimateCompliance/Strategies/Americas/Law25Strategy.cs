using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Americas
{
    /// <summary>
    /// Quebec Law 25 (Loi 25) compliance strategy.
    /// </summary>
    public sealed class Law25Strategy : ComplianceStrategyBase
    {
        public override string StrategyId => "law25";
        public override string StrategyName => "Quebec Law 25 Compliance";
        public override string Framework => "LAW25";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("law25.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("ConsentObtained", out var consentObj) || consentObj is not true)
            {
                violations.Add(new ComplianceViolation { Code = "LAW25-001", Description = "Consent not obtained", Severity = ViolationSeverity.Critical, Remediation = "Obtain express and free consent", RegulatoryReference = "Law 25 Art. 14" });
            }

            if (!context.Attributes.TryGetValue("PiaCompleted", out var piaObj) || piaObj is not true)
            {
                violations.Add(new ComplianceViolation { Code = "LAW25-002", Description = "Privacy Impact Assessment not conducted", Severity = ViolationSeverity.High, Remediation = "Conduct PIA for new technologies/systems", RegulatoryReference = "Law 25 Art. 3.3" });
            }

            if (!context.Attributes.TryGetValue("GovernanceFramework", out var govObj) || govObj is not true)
            {
                violations.Add(new ComplianceViolation { Code = "LAW25-003", Description = "Privacy governance framework not established", Severity = ViolationSeverity.Medium, Remediation = "Establish governance policies and practices", RegulatoryReference = "Law 25 Art. 3.2" });
            }

            if (!string.IsNullOrEmpty(context.OperationType) && context.OperationType.Equals("data-breach", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("CaiNotified", out var caiObj) || caiObj is not true)
                {
                    violations.Add(new ComplianceViolation { Code = "LAW25-004", Description = "CAI not notified of privacy breach", Severity = ViolationSeverity.Critical, Remediation = "Notify Commission d'accès à l'information", RegulatoryReference = "Law 25 Art. 3.5" });
                }
            }

            var hasHighViolations = violations.Any(v => v.Severity >= ViolationSeverity.High);
            var isCompliant = !hasHighViolations;
            var status = violations.Count == 0 ? ComplianceStatus.Compliant : hasHighViolations ? ComplianceStatus.NonCompliant : ComplianceStatus.PartiallyCompliant;
            return Task.FromResult(new ComplianceResult { IsCompliant = isCompliant, Framework = Framework, Status = status, Violations = violations, Recommendations = recommendations });
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("law25.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("law25.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
