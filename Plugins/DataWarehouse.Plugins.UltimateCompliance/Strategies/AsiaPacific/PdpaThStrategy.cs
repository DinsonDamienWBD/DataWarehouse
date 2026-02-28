using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.AsiaPacific
{
    /// <summary>
    /// Thailand Personal Data Protection Act (PDPA) compliance strategy.
    /// </summary>
    public sealed class PdpaThStrategy : ComplianceStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "pdpa-th";

        /// <inheritdoc/>
        public override string StrategyName => "Thailand PDPA";

        /// <inheritdoc/>
        public override string Framework => "PDPA-TH";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("pdpa_th.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check consent (Section 19)
            if (!context.Attributes.TryGetValue("ConsentObtained", out var consentObj) || consentObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "PDPA-TH-001",
                    Description = "Consent not obtained before personal data collection",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Obtain clear, informed consent prior to data processing (Sec. 19)",
                    RegulatoryReference = "PDPA Thailand Section 19"
                });
            }

            // Check sensitive data (Section 26)
            if (context.Attributes.TryGetValue("IsSensitiveData", out var sensitiveObj) && sensitiveObj is true)
            {
                if (!context.Attributes.TryGetValue("ExplicitConsent", out var explicitObj) || explicitObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "PDPA-TH-002",
                        Description = "Explicit consent not obtained for sensitive personal data",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Obtain explicit consent for race, religion, health, biometric, or criminal data (Sec. 26)",
                        RegulatoryReference = "PDPA Thailand Section 26"
                    });
                }
            }

            // Check data subject rights (Section 30-38)
            if (!context.Attributes.TryGetValue("RightsMechanismEstablished", out var rightsObj) || rightsObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "PDPA-TH-003",
                    Description = "Data subject rights mechanism not established",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement mechanisms for access, correction, deletion, and portability rights (Sec. 30-38)",
                    RegulatoryReference = "PDPA Thailand Sections 30-38"
                });
            }

            // Check DPO requirement (Section 41)
            if (context.Attributes.TryGetValue("LargeScaleProcessing", out var largeObj) && largeObj is true)
            {
                if (!context.Attributes.TryGetValue("DpoAppointed", out var dpoObj) || dpoObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "PDPA-TH-004",
                        Description = "Data Protection Officer not appointed for large-scale processing",
                        Severity = ViolationSeverity.Medium,
                        Remediation = "Appoint DPO for systematic monitoring or large-scale sensitive data processing (Sec. 41)",
                        RegulatoryReference = "PDPA Thailand Section 41"
                    });
                }
            }

            // Check cross-border transfer (Section 28)
            if (!string.IsNullOrEmpty(context.DestinationLocation) &&
                !context.DestinationLocation.StartsWith("TH", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("AdequacyOrSafeguards", out var adequacyObj) || adequacyObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "PDPA-TH-005",
                        Description = "Cross-border transfer without adequacy or appropriate safeguards",
                        Severity = ViolationSeverity.High,
                        Remediation = "Ensure destination has adequate protection or implement safeguards (Sec. 28)",
                        RegulatoryReference = "PDPA Thailand Section 28"
                    });
                }
            }

            recommendations.Add("Maintain records of processing activities and conduct DPIAs for high-risk processing");

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
            IncrementCounter("pdpa_th.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("pdpa_th.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
