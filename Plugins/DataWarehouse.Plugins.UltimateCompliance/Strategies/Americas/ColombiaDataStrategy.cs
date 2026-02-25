using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Americas
{
    /// <summary>
    /// Colombia Habeas Data Law compliance strategy.
    /// </summary>
    public sealed class ColombiaDataStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "colombia-data";
        public override string StrategyName => "Colombia Habeas Data Compliance";
        public override string Framework => "COLOMBIA-HABEASDATA";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
        IncrementCounter("colombia_data.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("PriorConsent", out var consentObj) || consentObj is not true)
            {
                violations.Add(new ComplianceViolation { Code = "CO-001", Description = "Prior and informed consent not obtained", Severity = ViolationSeverity.Critical, Remediation = "Obtain prior and informed authorization", RegulatoryReference = "Law 1581 of 2012 Art. 4" });
            }

            if (!context.Attributes.TryGetValue("DataRegistered", out var registeredObj) || registeredObj is not true)
            {
                violations.Add(new ComplianceViolation { Code = "CO-002", Description = "Database not registered with SIC", Severity = ViolationSeverity.High, Remediation = "Register database with Superintendencia de Industria y Comercio", RegulatoryReference = "Law 1581 Art. 25" });
            }

            if (!string.IsNullOrEmpty(context.DestinationLocation) && !context.DestinationLocation.Equals("CO", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("TransferAuthorization", out var transferObj) || transferObj is not true)
                {
                    violations.Add(new ComplianceViolation { Code = "CO-003", Description = $"International transfer to {context.DestinationLocation} without authorization", Severity = ViolationSeverity.High, Remediation = "Obtain authorization for international transfers", RegulatoryReference = "Law 1581 Art. 26" });
                }
            }

            var isCompliant = !violations.Any(v => v.Severity >= ViolationSeverity.High);
            var status = violations.Count == 0 ? ComplianceStatus.Compliant : violations.Any(v => v.Severity >= ViolationSeverity.High) ? ComplianceStatus.NonCompliant : ComplianceStatus.PartiallyCompliant;
            return Task.FromResult(new ComplianceResult { IsCompliant = isCompliant, Framework = Framework, Status = status, Violations = violations, Recommendations = recommendations });
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("colombia_data.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("colombia_data.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
