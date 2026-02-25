using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.AsiaPacific
{
    /// <summary>
    /// New Zealand Privacy Act (Information Privacy Principles) compliance strategy.
    /// </summary>
    public sealed class NzPrivacyStrategy : ComplianceStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "nz-privacy";

        /// <inheritdoc/>
        public override string StrategyName => "New Zealand Privacy Act";

        /// <inheritdoc/>
        public override string Framework => "NZ-PRIVACY";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("nz_privacy.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check IPP 3 - collection from individual
            if (!context.Attributes.TryGetValue("CollectedFromIndividual", out var collectedObj) || collectedObj is not true)
            {
                if (!context.Attributes.TryGetValue("ExceptionApplies", out var exceptionObj) || exceptionObj is not true)
                {
                    recommendations.Add("Ensure personal information is collected directly from individual unless exception applies (IPP 3)");
                }
            }

            // Check IPP 4 - manner of collection
            if (!context.Attributes.TryGetValue("LawfulCollection", out var lawfulObj) || lawfulObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "IPP-004",
                    Description = "Personal information not collected by lawful and fair means",
                    Severity = ViolationSeverity.High,
                    Remediation = "Collect information lawfully, fairly, and without intrusion upon personal affairs (IPP 4)",
                    RegulatoryReference = "Privacy Act 2020 IPP 4"
                });
            }

            // Check IPP 5 - security safeguards
            if (!context.Attributes.TryGetValue("SecuritySafeguards", out var securityObj) || securityObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "IPP-005",
                    Description = "Security safeguards not implemented for personal information",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Implement reasonable security safeguards against loss, unauthorized access, use, modification, or disclosure (IPP 5)",
                    RegulatoryReference = "Privacy Act 2020 IPP 5"
                });
            }

            // Check IPP 8 - access to personal information
            if (context.OperationType.Contains("access-request", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("AccessProvided", out var accessObj) || accessObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "IPP-008",
                        Description = "Access to personal information not provided",
                        Severity = ViolationSeverity.Medium,
                        Remediation = "Provide individuals access to their personal information unless exception applies (IPP 8)",
                        RegulatoryReference = "Privacy Act 2020 IPP 8"
                    });
                }
            }

            // Check IPP 12 - cross-border disclosure
            if (!string.IsNullOrEmpty(context.DestinationLocation) &&
                !context.DestinationLocation.StartsWith("NZ", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("CrossBorderSafeguards", out var cbObj) || cbObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "IPP-012",
                        Description = "Cross-border disclosure without ensuring comparable safeguards",
                        Severity = ViolationSeverity.High,
                        Remediation = "Ensure overseas recipient provides comparable privacy protection (IPP 12)",
                        RegulatoryReference = "Privacy Act 2020 IPP 12"
                    });
                }
            }

            // Check notifiable privacy breach
            if (context.Attributes.TryGetValue("PrivacyBreachOccurred", out var breachObj) && breachObj is true)
            {
                if (!context.Attributes.TryGetValue("BreachNotifiedToCommissioner", out var notifiedObj) || notifiedObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "NPB-001",
                        Description = "Notifiable privacy breach not reported to Privacy Commissioner",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Notify Privacy Commissioner of breaches causing serious harm as soon as practicable",
                        RegulatoryReference = "Privacy Act 2020 Part 6A"
                    });
                }
            }

            recommendations.Add("Conduct privacy impact assessments for projects involving new technologies or significant privacy risks");

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
            IncrementCounter("nz_privacy.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("nz_privacy.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
