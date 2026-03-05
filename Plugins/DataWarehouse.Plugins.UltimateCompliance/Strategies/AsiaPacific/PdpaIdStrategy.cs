using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.AsiaPacific
{
    /// <summary>
    /// Indonesia Personal Data Protection Law compliance strategy.
    /// </summary>
    public sealed class PdpaIdStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "pdp-id";
        public override string StrategyName => "Indonesia PDP";
        public override string Framework => "PDP-ID";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("pdpa_id.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("ConsentObtained", out var consentObj) || consentObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "PDP-ID-020",
                    Description = "Explicit consent not obtained for personal data processing",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Obtain explicit and informed consent (Art. 20)",
                    RegulatoryReference = "PDP Law Indonesia Article 20"
                });
            }

            if (!context.Attributes.TryGetValue("DataControllerDuties", out var dutiesObj) || dutiesObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "PDP-ID-017",
                    Description = "Data controller obligations not fulfilled",
                    Severity = ViolationSeverity.High,
                    Remediation = "Fulfill data controller duties including transparency and accountability (Art. 17)",
                    RegulatoryReference = "PDP Law Indonesia Article 17"
                });
            }

            if (context.OperationType.Contains("request", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("DataSubjectRightsMechanism", out var rightsObj) || rightsObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "PDP-ID-028",
                        Description = "Data subject rights mechanism not established",
                        Severity = ViolationSeverity.Medium,
                        Remediation = "Provide access, correction, deletion, and portability rights (Art. 28-35)",
                        RegulatoryReference = "PDP Law Indonesia Articles 28-35"
                    });
                }
            }

            if (!context.Attributes.TryGetValue("SecurityImplemented", out var secObj) || secObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "PDP-ID-040",
                    Description = "Personal data security measures not implemented",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement appropriate technical and organizational security (Art. 40)",
                    RegulatoryReference = "PDP Law Indonesia Article 40"
                });
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
            IncrementCounter("pdpa_id.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("pdpa_id.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
