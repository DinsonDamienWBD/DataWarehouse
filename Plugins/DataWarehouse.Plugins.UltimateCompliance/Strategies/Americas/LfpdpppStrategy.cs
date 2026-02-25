using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Americas
{
    /// <summary>
    /// LFPDPPP (Ley Federal de Protección de Datos Personales en Posesión de los Particulares - Mexico) compliance strategy.
    /// </summary>
    public sealed class LfpdpppStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "lfpdppp";
        public override string StrategyName => "LFPDPPP Compliance";
        public override string Framework => "LFPDPPP";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("lfpdppp.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("PrivacyNotice", out var noticeObj) || noticeObj is not true)
            {
                violations.Add(new ComplianceViolation { Code = "LFPDPPP-001", Description = "Privacy notice (Aviso de Privacidad) not provided", Severity = ViolationSeverity.Critical, Remediation = "Provide comprehensive privacy notice", RegulatoryReference = "LFPDPPP Art. 15-16" });
            }

            if (!context.Attributes.TryGetValue("ConsentObtained", out var consentObj) || consentObj is not true)
            {
                violations.Add(new ComplianceViolation { Code = "LFPDPPP-002", Description = "Consent not obtained", Severity = ViolationSeverity.High, Remediation = "Obtain consent for personal data processing", RegulatoryReference = "LFPDPPP Art. 8" });
            }

            if (!context.ProcessingPurposes.Any())
            {
                violations.Add(new ComplianceViolation { Code = "LFPDPPP-003", Description = "Processing purpose not specified", Severity = ViolationSeverity.High, Remediation = "Define specific, lawful, and legitimate purposes", RegulatoryReference = "LFPDPPP Art. 11" });
            }

            if (!string.IsNullOrEmpty(context.DestinationLocation) && !context.DestinationLocation.Equals("MX", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("TransferConsent", out var transferObj) || transferObj is not true)
                {
                    violations.Add(new ComplianceViolation { Code = "LFPDPPP-004", Description = $"International transfer to {context.DestinationLocation} without consent", Severity = ViolationSeverity.High, Remediation = "Obtain consent for international transfers", RegulatoryReference = "LFPDPPP Art. 36-37" });
                }
            }

            var isCompliant = !violations.Any(v => v.Severity >= ViolationSeverity.High);
            var status = violations.Count == 0 ? ComplianceStatus.Compliant : violations.Any(v => v.Severity >= ViolationSeverity.High) ? ComplianceStatus.NonCompliant : ComplianceStatus.PartiallyCompliant;
            return Task.FromResult(new ComplianceResult { IsCompliant = isCompliant, Framework = Framework, Status = status, Violations = violations, Recommendations = recommendations });
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("lfpdppp.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("lfpdppp.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
