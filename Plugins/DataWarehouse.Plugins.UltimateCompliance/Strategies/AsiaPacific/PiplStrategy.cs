using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.AsiaPacific
{
    /// <summary>
    /// China Personal Information Protection Law (PIPL) compliance strategy.
    /// </summary>
    public sealed class PiplStrategy : ComplianceStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "pipl";

        /// <inheritdoc/>
        public override string StrategyName => "China PIPL";

        /// <inheritdoc/>
        public override string Framework => "PIPL";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("pipl.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check consent requirements (Article 13)
            if (!context.Attributes.TryGetValue("InformedConsent", out var consentObj) || consentObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "PIPL-001",
                    Description = "Informed consent not obtained for personal information processing",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Obtain informed, voluntary consent before processing personal information (Art. 13)",
                    RegulatoryReference = "PIPL Article 13"
                });
            }

            // Check cross-border transfer (Article 38-40)
            if (!string.IsNullOrEmpty(context.DestinationLocation) &&
                context.DestinationLocation.StartsWith("CN", StringComparison.OrdinalIgnoreCase) == false)
            {
                if (!context.Attributes.TryGetValue("CrossBorderMechanism", out var cbObj) || cbObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "PIPL-002",
                        Description = "Cross-border transfer without approved mechanism",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Use CAC-approved mechanism: security assessment, certification, or SCC (Art. 38-40)",
                        RegulatoryReference = "PIPL Articles 38-40"
                    });
                }
            }

            // Check sensitive personal information (Article 28-29)
            if (context.Attributes.TryGetValue("IsSensitive", out var sensitiveObj) && sensitiveObj is true)
            {
                if (!context.Attributes.TryGetValue("SeparateConsent", out var separateObj) || separateObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "PIPL-003",
                        Description = "Separate consent not obtained for sensitive personal information",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Obtain separate consent for biometric, health, financial, or children's data (Art. 28-29)",
                        RegulatoryReference = "PIPL Articles 28-29"
                    });
                }
            }

            // Check automated decision-making (Article 24)
            if (!string.IsNullOrEmpty(context.OperationType) && context.OperationType.Equals("automated-decision", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("ExplanationProvided", out var explainObj) || explainObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "PIPL-004",
                        Description = "Automated decision explanation not provided to individual",
                        Severity = ViolationSeverity.High,
                        Remediation = "Provide transparency about automated decision logic and allow opt-out (Art. 24)",
                        RegulatoryReference = "PIPL Article 24"
                    });
                }
            }

            // Check individual rights (Article 44-50)
            if (context.OperationType.Contains("request", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("ResponseWithin15Days", out var responseObj) || responseObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "PIPL-005",
                        Description = "Individual rights request response mechanism not established",
                        Severity = ViolationSeverity.Medium,
                        Remediation = "Respond to access, correction, deletion requests within 15 days (Art. 50)",
                        RegulatoryReference = "PIPL Articles 44-50"
                    });
                }
            }

            recommendations.Add("Appoint China-based representative if processing large volumes of Chinese personal information");

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
            IncrementCounter("pipl.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("pipl.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
