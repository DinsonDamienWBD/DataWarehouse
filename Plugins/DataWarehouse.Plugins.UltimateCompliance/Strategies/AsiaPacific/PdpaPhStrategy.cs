using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.AsiaPacific
{
    /// <summary>
    /// Philippines Data Privacy Act (RA 10173) compliance strategy.
    /// </summary>
    /// <remarks>
    /// The class name <c>PdpaPhStrategy</c> uses the informal regional abbreviation "PDPA-PH"
    /// for discoverability alongside other PDPA strategies. The official regulation is the
    /// Data Privacy Act (DPA) and <see cref="Framework"/> returns <c>"DPA-PH"</c>.
    /// <see cref="StrategyId"/> uses <c>"pdpa-ph"</c> for backward-compatible keying.
    /// </remarks>
    public sealed class PdpaPhStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "pdpa-ph";
        public override string StrategyName => "Philippines DPA";
        public override string Framework => "DPA-PH";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("pdpa_ph.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("LawfulBasis", out var basisObj) || basisObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "DPA-PH-011",
                    Description = "Lawful criteria for processing not established",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Establish consent or other lawful basis for processing (Sec. 11-12)",
                    RegulatoryReference = "DPA Philippines Section 11-12"
                });
            }

            if (context.Attributes.TryGetValue("IsSensitivePersonalInfo", out var spiObj) && spiObj is true)
            {
                if (!context.Attributes.TryGetValue("SensitiveDataConsent", out var scdObj) || scdObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "DPA-PH-013",
                        Description = "Consent not obtained for sensitive personal information",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Obtain consent for processing race, health, genetic, sexual orientation data (Sec. 13)",
                        RegulatoryReference = "DPA Philippines Section 13"
                    });
                }
            }

            if (!context.Attributes.TryGetValue("SecurityMeasures", out var secObj) || secObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "DPA-PH-020",
                    Description = "Organizational, physical, and technical security measures not implemented",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement security measures to protect personal data (Sec. 20)",
                    RegulatoryReference = "DPA Philippines Section 20"
                });
            }

            if (context.Attributes.TryGetValue("DataBreachOccurred", out var breachObj) && breachObj is true)
            {
                if (!context.Attributes.TryGetValue("BreachNotified72Hours", out var notifyObj) || notifyObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "DPA-PH-20D",
                        Description = "Data breach not notified within 72 hours",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Notify NPC within 72 hours of breach discovery (Sec. 20(d))",
                        RegulatoryReference = "DPA Philippines Section 20(d)"
                    });
                }
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
            IncrementCounter("pdpa_ph.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("pdpa_ph.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
