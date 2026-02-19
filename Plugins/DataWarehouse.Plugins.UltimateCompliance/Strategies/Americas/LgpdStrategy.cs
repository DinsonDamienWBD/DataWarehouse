using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Americas
{
    /// <summary>
    /// LGPD (Lei Geral de Proteção de Dados - Brazil) compliance strategy.
    /// </summary>
    public sealed class LgpdStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "lgpd";
        public override string StrategyName => "LGPD Compliance";
        public override string Framework => "LGPD";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
        IncrementCounter("lgpd.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("LawfulBasis", out var basisObj) || basisObj is not string basis)
            {
                violations.Add(new ComplianceViolation { Code = "LGPD-001", Description = "No lawful basis for processing", Severity = ViolationSeverity.Critical, Remediation = "Define lawful basis (consent, legal obligation, etc.)", RegulatoryReference = "LGPD Art. 7" });
            }

            if (!context.Attributes.TryGetValue("DpoAppointed", out var dpoObj) || dpoObj is not true)
            {
                violations.Add(new ComplianceViolation { Code = "LGPD-002", Description = "Data Protection Officer not appointed", Severity = ViolationSeverity.High, Remediation = "Appoint DPO and publish contact information", RegulatoryReference = "LGPD Art. 41" });
            }

            if (!string.IsNullOrEmpty(context.DestinationLocation) && !context.DestinationLocation.Equals("BR", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("InternationalTransferMechanism", out var transferObj) || transferObj is not true)
                {
                    violations.Add(new ComplianceViolation { Code = "LGPD-003", Description = $"International transfer to {context.DestinationLocation} without adequate mechanism", Severity = ViolationSeverity.High, Remediation = "Ensure adequate protection level or use approved mechanisms", RegulatoryReference = "LGPD Art. 33" });
                }
            }

            if (context.DataClassification.Equals("sensitive", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("SpecificConsent", out var consentObj) || consentObj is not true)
                {
                    violations.Add(new ComplianceViolation { Code = "LGPD-004", Description = "Sensitive data processing without specific consent", Severity = ViolationSeverity.Critical, Remediation = "Obtain specific and highlighted consent", RegulatoryReference = "LGPD Art. 11" });
                }
            }

            var isCompliant = !violations.Any(v => v.Severity >= ViolationSeverity.High);
            var status = violations.Count == 0 ? ComplianceStatus.Compliant : violations.Any(v => v.Severity >= ViolationSeverity.High) ? ComplianceStatus.NonCompliant : ComplianceStatus.PartiallyCompliant;
            return Task.FromResult(new ComplianceResult { IsCompliant = isCompliant, Framework = Framework, Status = status, Violations = violations, Recommendations = recommendations });
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("lgpd.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("lgpd.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
