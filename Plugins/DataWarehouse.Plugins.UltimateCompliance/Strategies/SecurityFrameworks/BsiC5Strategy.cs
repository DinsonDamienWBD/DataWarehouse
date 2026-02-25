using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.SecurityFrameworks
{
    /// <summary>
    /// BSI C5 (Cloud Computing Compliance Criteria Catalogue) compliance strategy for Germany.
    /// </summary>
    public sealed class BsiC5Strategy : ComplianceStrategyBase
    {
        public override string StrategyId => "bsi-c5";
        public override string StrategyName => "BSI C5";
        public override string Framework => "BSI-C5";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("bsi_c5.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("OrganizationAndProcesses", out var orgObj) || orgObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "C5-ORP",
                    Description = "Organization and processes criteria not met",
                    Severity = ViolationSeverity.High,
                    Remediation = "Establish organizational security processes per BSI C5 ORP criteria",
                    RegulatoryReference = "BSI C5:2020 ORP"
                });
            }

            if (!context.Attributes.TryGetValue("ComplianceRequirements", out var compObj) || compObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "C5-CCM",
                    Description = "Compliance management criteria not implemented",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement compliance management system (CCM criteria)",
                    RegulatoryReference = "BSI C5:2020 CCM"
                });
            }

            if (!context.Attributes.TryGetValue("IdentityAndAccessManagement", out var iamObj) || iamObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "C5-IAM",
                    Description = "Identity and access management criteria not met",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Implement IAM controls per BSI C5 requirements",
                    RegulatoryReference = "BSI C5:2020 IAM"
                });
            }

            if (!context.Attributes.TryGetValue("Cryptography", out var cryptoObj) || cryptoObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "C5-CRY",
                    Description = "Cryptography criteria not implemented",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement cryptographic controls using approved algorithms",
                    RegulatoryReference = "BSI C5:2020 CRY"
                });
            }

            recommendations.Add("Obtain BSI C5 Type 2 audit report for German cloud service compliance");

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
            IncrementCounter("bsi_c5.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("bsi_c5.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
