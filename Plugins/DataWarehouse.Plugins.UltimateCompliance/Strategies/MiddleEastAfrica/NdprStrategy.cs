using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.MiddleEastAfrica
{
    /// <summary>
    /// NDPR (Nigeria Data Protection Regulation) compliance strategy.
    /// </summary>
    public sealed class NdprStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "ndpr";
        public override string StrategyName => "NDPR Compliance";
        public override string Framework => "NDPR";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("ndpr.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("LawfulProcessing", out var lawfulObj) || lawfulObj is not true)
            {
                violations.Add(new ComplianceViolation { Code = "NDPR-001", Description = "Lawful processing basis not established", Severity = ViolationSeverity.High, Remediation = "Establish lawful basis for processing", RegulatoryReference = "NDPR Section 2.1" });
            }

            if (!context.Attributes.TryGetValue("DataAuditCompleted", out var auditObj) || auditObj is not true)
            {
                violations.Add(new ComplianceViolation { Code = "NDPR-002", Description = "Mandatory data audit not completed", Severity = ViolationSeverity.High, Remediation = "Conduct annual data audit", RegulatoryReference = "NDPR Section 3.1" });
            }

            if (!string.IsNullOrEmpty(context.OperationType) && context.OperationType.Equals("data-breach", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("NitdaNotified", out var notifyObj) || notifyObj is not true)
                {
                    violations.Add(new ComplianceViolation { Code = "NDPR-003", Description = "Data breach not reported to NITDA", Severity = ViolationSeverity.Critical, Remediation = "Report breach to NITDA within 72 hours", RegulatoryReference = "NDPR Section 2.6" });
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
            IncrementCounter("ndpr.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("ndpr.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
