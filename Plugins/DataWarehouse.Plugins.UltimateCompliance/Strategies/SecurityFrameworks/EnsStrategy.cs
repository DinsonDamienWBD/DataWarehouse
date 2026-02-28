using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.SecurityFrameworks
{
    /// <summary>
    /// Spanish ENS (Esquema Nacional de Seguridad) compliance strategy.
    /// </summary>
    public sealed class EnsStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "ens";
        public override string StrategyName => "Spanish ENS";
        public override string Framework => "ENS";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("ens.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("SecurityCategory", out var catObj))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ENS-CAT",
                    Description = "System security category not determined",
                    Severity = ViolationSeverity.High,
                    Remediation = "Categorize system as Basic, Medium, or High per ENS criteria",
                    RegulatoryReference = "ENS Security Categories"
                });
            }

            if (!context.Attributes.TryGetValue("SecurityMeasures", out var measuresObj) || measuresObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ENS-MEASURES",
                    Description = "ENS security measures not implemented",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Implement organizational, operational, and protective measures per ENS Annex II",
                    RegulatoryReference = "ENS Annex II"
                });
            }

            if (!context.Attributes.TryGetValue("SecurityDocumentation", out var docObj) || docObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ENS-DOC",
                    Description = "Mandatory security documentation not prepared",
                    Severity = ViolationSeverity.High,
                    Remediation = "Prepare security policy, procedures, and audit reports",
                    RegulatoryReference = "ENS Documentation Requirements"
                });
            }

            if (!context.Attributes.TryGetValue("CertificationObtained", out var certObj) || certObj is not true)
            {
                recommendations.Add("Obtain ENS certification from authorized certification body");
            }

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
            IncrementCounter("ens.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("ens.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
