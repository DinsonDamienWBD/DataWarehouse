using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.SecurityFrameworks
{
    /// <summary>
    /// ISO/IEC 15408 Common Criteria compliance strategy.
    /// </summary>
    public sealed class IsoIec15408Strategy : ComplianceStrategyBase
    {
        public override string StrategyId => "iso15408";
        public override string StrategyName => "ISO/IEC 15408 (Common Criteria)";
        public override string Framework => "ISO15408";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
        IncrementCounter("iso_iec15408.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("SecurityTarget", out var stObj) || stObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CC-ST",
                    Description = "Security Target (ST) not documented",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Document Security Target defining TOE, security objectives, and requirements",
                    RegulatoryReference = "ISO/IEC 15408 Security Target"
                });
            }

            if (!context.Attributes.TryGetValue("EalLevel", out var ealObj))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CC-EAL",
                    Description = "Evaluation Assurance Level (EAL) not determined",
                    Severity = ViolationSeverity.High,
                    Remediation = "Determine target EAL (EAL1-EAL7) based on assurance requirements",
                    RegulatoryReference = "ISO/IEC 15408 EAL"
                });
            }

            if (!context.Attributes.TryGetValue("ToeDefinition", out var toeObj) || toeObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CC-TOE",
                    Description = "Target of Evaluation (TOE) not clearly defined",
                    Severity = ViolationSeverity.High,
                    Remediation = "Define TOE boundary, components, and operational environment",
                    RegulatoryReference = "ISO/IEC 15408 TOE"
                });
            }

            if (!context.Attributes.TryGetValue("SecurityFunctionalRequirements", out var sfrObj) || sfrObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CC-SFR",
                    Description = "Security Functional Requirements (SFR) not specified",
                    Severity = ViolationSeverity.High,
                    Remediation = "Specify SFRs from Part 2 or define extended requirements",
                    RegulatoryReference = "ISO/IEC 15408 Part 2"
                });
            }

            recommendations.Add("Engage Common Criteria accredited lab for formal evaluation");

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
        IncrementCounter("iso_iec15408.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("iso_iec15408.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
