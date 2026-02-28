using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.NIST
{
    /// <summary>
    /// NIST SP 800-172 Enhanced CUI Protection compliance strategy.
    /// </summary>
    public sealed class Nist800172Strategy : ComplianceStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "nist800172";

        /// <inheritdoc/>
        public override string StrategyName => "NIST SP 800-172";

        /// <inheritdoc/>
        public override string Framework => "NIST800172";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("nist800172.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            bool isCui = context.Attributes.TryGetValue("IsCUI", out var cuiObj) && cuiObj is true;
            bool isEnhanced = context.Attributes.TryGetValue("RequiresEnhancedProtection", out var enhancedObj) && enhancedObj is true;

            if (!isCui || !isEnhanced)
            {
                return Task.FromResult(new ComplianceResult
                {
                    IsCompliant = true,
                    Framework = Framework,
                    Status = ComplianceStatus.NotApplicable,
                    Violations = violations,
                    Recommendations = new List<string> { "NIST 800-172 applies only to CUI requiring enhanced protection" }
                });
            }

            // Check advanced access control (3.1e)
            if (!context.Attributes.TryGetValue("DynamicAccessControl", out var dacObj) || dacObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NIST800172-3.1e",
                    Description = "Dynamic access control not implemented for enhanced CUI",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement attribute-based or context-aware access control (3.1e)",
                    RegulatoryReference = "NIST SP 800-172 3.1e"
                });
            }

            // Check multifactor cryptographic authentication (3.5e)
            if (!context.Attributes.TryGetValue("CryptoMfa", out var cryptoMfaObj) || cryptoMfaObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NIST800172-3.5e",
                    Description = "Multifactor cryptographic authentication not used",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Implement PKI-based or hardware token MFA for enhanced CUI (3.5e)",
                    RegulatoryReference = "NIST SP 800-172 3.5e"
                });
            }

            // Check advanced threat protection (3.14e)
            if (!context.Attributes.TryGetValue("AdvancedThreatAnalytics", out var ataObj) || ataObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NIST800172-3.14e",
                    Description = "Advanced threat analytics not deployed",
                    Severity = ViolationSeverity.High,
                    Remediation = "Deploy behavioral analytics and threat intelligence integration (3.14e)",
                    RegulatoryReference = "NIST SP 800-172 3.14e"
                });
            }

            // Check insider threat program (3.11e)
            if (!context.Attributes.TryGetValue("InsiderThreatProgram", out var insiderObj) || insiderObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NIST800172-3.11e",
                    Description = "Insider threat program not established",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Implement insider threat detection and mitigation program (3.11e)",
                    RegulatoryReference = "NIST SP 800-172 3.11e"
                });
            }

            // Check supply chain risk management (3.13e)
            if (!context.Attributes.TryGetValue("SupplyChainRiskManaged", out var scrmObj) || scrmObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NIST800172-3.13e",
                    Description = "Supply chain risk management not implemented",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Assess and manage supply chain risks for system components (3.13e)",
                    RegulatoryReference = "NIST SP 800-172 3.13e"
                });
            }

            recommendations.Add("Enhanced controls require significant investment in advanced security technologies");

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
            IncrementCounter("nist800172.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("nist800172.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
