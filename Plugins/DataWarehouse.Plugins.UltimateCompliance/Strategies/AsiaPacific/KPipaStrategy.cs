using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.AsiaPacific
{
    /// <summary>
    /// South Korea Personal Information Protection Act (K-PIPA) compliance strategy.
    /// </summary>
    public sealed class KPipaStrategy : ComplianceStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "k-pipa";

        /// <inheritdoc/>
        public override string StrategyName => "South Korea PIPA";

        /// <inheritdoc/>
        public override string Framework => "K-PIPA";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("k_pipa.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check consent (Article 15)
            if (!context.Attributes.TryGetValue("ConsentObtained", out var consentObj) || consentObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "K-PIPA-001",
                    Description = "Consent not obtained for personal information collection and use",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Obtain consent separately for each purpose (Art. 15)",
                    RegulatoryReference = "K-PIPA Article 15"
                });
            }

            // Check sensitive information (Article 23)
            if (context.Attributes.TryGetValue("IsSensitiveInfo", out var sensitiveObj) && sensitiveObj is true)
            {
                if (!context.Attributes.TryGetValue("SeparateConsent", out var separateObj) || separateObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "K-PIPA-002",
                        Description = "Separate consent not obtained for sensitive personal information",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Obtain separate consent for ideology, health, genetic, criminal record data (Art. 23)",
                        RegulatoryReference = "K-PIPA Article 23"
                    });
                }
            }

            // Check unique identifiers (Article 24)
            if (context.Attributes.TryGetValue("UsesUniqueIdentifier", out var uniqueObj) && uniqueObj is true)
            {
                if (!context.Attributes.TryGetValue("LegalBasisForIdentifier", out var legalObj) || legalObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "K-PIPA-003",
                        Description = "Resident registration number used without legal basis",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Use resident registration numbers only when required by law (Art. 24)",
                        RegulatoryReference = "K-PIPA Article 24"
                    });
                }
            }

            // Check cross-border transfer (Article 17)
            if (!string.IsNullOrEmpty(context.DestinationLocation) &&
                !context.DestinationLocation.StartsWith("KR", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("OverseasTransferConsent", out var cbObj) || cbObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "K-PIPA-004",
                        Description = "Overseas transfer consent not obtained",
                        Severity = ViolationSeverity.High,
                        Remediation = "Obtain consent after informing recipient, country, and purpose (Art. 17)",
                        RegulatoryReference = "K-PIPA Article 17"
                    });
                }
            }

            // Check security measures (Article 29)
            if (!context.Attributes.TryGetValue("SecurityMeasuresImplemented", out var securityObj) || securityObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "K-PIPA-005",
                    Description = "Technical and administrative security measures not implemented",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement encryption, access control, and audit logging (Art. 29)",
                    RegulatoryReference = "K-PIPA Article 29"
                });
            }

            recommendations.Add("Appoint Chief Privacy Officer (CPO) if processing personal information of 1M+ individuals");

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
            IncrementCounter("k_pipa.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("k_pipa.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
