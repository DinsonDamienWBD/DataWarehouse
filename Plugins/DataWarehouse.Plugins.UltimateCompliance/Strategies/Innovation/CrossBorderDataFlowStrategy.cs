using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Innovation
{
    /// <summary>
    /// Cross-border data flow compliance strategy with transfer mechanism validation.
    /// </summary>
    public sealed class CrossBorderDataFlowStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "cross-border-flow";
        public override string StrategyName => "Cross-Border Data Flow";
        public override string Framework => "CROSS-BORDER-FLOW";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("cross_border_data_flow.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            bool isCrossBorder = !string.IsNullOrEmpty(context.SourceLocation) &&
                                !string.IsNullOrEmpty(context.DestinationLocation) &&
                                !context.SourceLocation.Equals(context.DestinationLocation, StringComparison.OrdinalIgnoreCase);

            if (!isCrossBorder)
            {
                return Task.FromResult(new ComplianceResult
                {
                    IsCompliant = true,
                    Framework = Framework,
                    Status = ComplianceStatus.NotApplicable,
                    Violations = violations,
                    Recommendations = new List<string> { "No cross-border transfer detected" }
                });
            }

            if (!context.Attributes.TryGetValue("TransferMechanismValidated", out var mechObj) || mechObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CBDF-001",
                    Description = "Cross-border transfer mechanism not validated",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Validate transfer mechanism: adequacy decision, SCCs, BCRs, or derogation",
                    RegulatoryReference = "GDPR Chapter V, PIPL Art 38-40"
                });
            }

            if (!context.Attributes.TryGetValue("AdequacyCheckPerformed", out var adequacyObj) || adequacyObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CBDF-002",
                    Description = "Destination country adequacy not assessed",
                    Severity = ViolationSeverity.High,
                    Remediation = "Check if destination has adequacy determination from source jurisdiction",
                    RegulatoryReference = "Adequacy Decisions Database"
                });
            }

            if (!context.Attributes.TryGetValue("DataLocalizationCheck", out var localObj) || localObj is not true)
            {
                recommendations.Add("Review data localization requirements for source and destination jurisdictions");
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
            IncrementCounter("cross_border_data_flow.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("cross_border_data_flow.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
