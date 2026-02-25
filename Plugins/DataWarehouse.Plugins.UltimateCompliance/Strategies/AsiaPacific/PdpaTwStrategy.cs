using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.AsiaPacific
{
    /// <summary>
    /// Taiwan Personal Data Protection Act compliance strategy.
    /// </summary>
    public sealed class PdpaTwStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "pdpa-tw";
        public override string StrategyName => "Taiwan PDPA";
        public override string Framework => "PDPA-TW";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("pdpa_tw.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("LegalBasisEstablished", out var basisObj) || basisObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "PDPA-TW-019",
                    Description = "Legal basis not established for personal data collection",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Establish legal basis: consent, contract, legal obligation, or legitimate interest (Art. 19)",
                    RegulatoryReference = "PDPA Taiwan Article 19"
                });
            }

            if (!string.IsNullOrEmpty(context.DestinationLocation) && !context.DestinationLocation.StartsWith("TW", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("InternationalTransferRestrictions", out var cbObj) || cbObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "PDPA-TW-021",
                        Description = "International transfer without checking government restrictions",
                        Severity = ViolationSeverity.High,
                        Remediation = "Comply with international transfer restrictions imposed by government (Art. 21)",
                        RegulatoryReference = "PDPA Taiwan Article 21"
                    });
                }
            }

            if (context.OperationType.Contains("access-request", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("AccessRightProvided", out var accessObj) || accessObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "PDPA-TW-003",
                        Description = "Data subject access and correction rights not honored",
                        Severity = ViolationSeverity.Medium,
                        Remediation = "Provide access, correction, and deletion mechanisms (Art. 3)",
                        RegulatoryReference = "PDPA Taiwan Article 3"
                    });
                }
            }

            if (!context.Attributes.TryGetValue("SecurityMaintained", out var secObj) || secObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "PDPA-TW-027",
                    Description = "Security maintenance measures not implemented",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement appropriate security measures to prevent data breach (Art. 27)",
                    RegulatoryReference = "PDPA Taiwan Article 27"
                });
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
            IncrementCounter("pdpa_tw.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("pdpa_tw.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
