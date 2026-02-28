using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Regulations
{
    /// <summary>
    /// NIS2 (Network and Information Security Directive 2) compliance strategy.
    /// Implements EU-wide cybersecurity requirements for critical infrastructure and digital services.
    /// </summary>
    /// <remarks>
    /// NIS2 Directive (EU) 2022/2555 establishes measures for high common level of cybersecurity across the EU.
    /// Applies to essential and important entities across 18 sectors.
    /// </remarks>
    public sealed class Nis2Strategy : ComplianceStrategyBase
    {
        private readonly HashSet<string> _coveredSectors = new(StringComparer.OrdinalIgnoreCase)
        {
            "energy", "transport", "banking", "financial-market", "health", "drinking-water",
            "wastewater", "digital-infrastructure", "ict-service-management", "public-administration",
            "space", "postal-courier", "waste-management", "chemicals", "food", "manufacturing",
            "digital-providers", "research"
        };

        private readonly HashSet<string> _requiredControls = new(StringComparer.OrdinalIgnoreCase)
        {
            "risk-analysis", "incident-handling", "business-continuity", "supply-chain-security",
            "security-acquisition", "effectiveness-assessment", "cryptography", "human-resources",
            "access-control", "asset-management"
        };

        /// <inheritdoc/>
        public override string StrategyId => "nis2";

        /// <inheritdoc/>
        public override string StrategyName => "NIS2 Directive Compliance";

        /// <inheritdoc/>
        public override string Framework => "NIS2";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("nis2.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check if entity is in covered sector
            CheckSectorApplicability(context, violations, recommendations);

            // Check incident reporting capabilities
            CheckIncidentReporting(context, violations, recommendations);

            // Check supply chain security
            CheckSupplyChainSecurity(context, violations, recommendations);

            // Check security controls implementation
            CheckSecurityControls(context, violations, recommendations);

            // Check risk management procedures
            CheckRiskManagement(context, violations, recommendations);

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

        // NIS2 Annex I sectors are "essential entities" (higher tier, stricter supervision)
        // NIS2 Annex II sectors are "important entities" (lower tier, lighter requirements)
        private static readonly HashSet<string> _essentialSectors = new(StringComparer.OrdinalIgnoreCase)
        {
            "energy", "transport", "banking", "financial-market", "health", "drinking-water",
            "wastewater", "digital-infrastructure", "ict-service-management", "public-administration", "space"
        };

        private void CheckSectorApplicability(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("Sector", out var sectorObj) || sectorObj is not string sector)
                return;

            if (!_coveredSectors.Contains(sector))
                return;

            // NIS2: essential entities (Annex I) face stricter requirements and proactive supervision
            // Important entities (Annex II) have lighter obligations — opposite of what was previously coded
            var isEssential = _essentialSectors.Contains(sector);
            var entityType = isEssential ? "Essential Entity (Annex I)" : "Important Entity (Annex II)";
            recommendations.Add($"NIS2 applies to {sector} sector as {entityType} - ensure full compliance with all Article 21 measures");

            if (isEssential)
            {
                // Essential entities require proactive ex-ante supervision
                if (!context.Attributes.TryGetValue("ProactiveSupervisionCompliance", out var psObj) || psObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "NIS2-ESS-001",
                        Description = $"Essential entity in {sector} sector must comply with proactive ex-ante supervision (NIS2 Article 32)",
                        Severity = ViolationSeverity.High,
                        Remediation = "Register with national competent authority and submit to proactive supervision",
                        RegulatoryReference = "NIS2 Article 32-33"
                    });
                }
            }
        }

        private void CheckIncidentReporting(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("IncidentReportingCapability", out var reportingObj) || reportingObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NIS2-001",
                    Description = "No incident reporting capability implemented",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Implement 24-hour incident reporting to national CSIRT",
                    RegulatoryReference = "NIS2 Article 23"
                });
            }

            // Check for early warning (significant incidents within 24 hours)
            if (!context.Attributes.TryGetValue("EarlyWarningSystem", out var earlyWarningObj) || earlyWarningObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NIS2-002",
                    Description = "No early warning system for significant incidents",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement early warning within 24 hours of incident detection",
                    RegulatoryReference = "NIS2 Article 23(3)"
                });
            }
        }

        private void CheckSupplyChainSecurity(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("SupplyChainRiskAssessment", out var assessmentObj) || assessmentObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NIS2-003",
                    Description = "No supply chain security risk assessment",
                    Severity = ViolationSeverity.High,
                    Remediation = "Conduct supply chain security risk assessment including supplier relationships",
                    RegulatoryReference = "NIS2 Article 21(2)(d)"
                });
            }

            if (context.Attributes.TryGetValue("SupplierCount", out var supplierCountObj) && supplierCountObj is int supplierCount && supplierCount > 0)
            {
                if (!context.Attributes.TryGetValue("SupplierSecurityAgreements", out var agreementsObj) || agreementsObj is not true)
                {
                    recommendations.Add("Establish security agreements with all suppliers in the supply chain");
                }
            }
        }

        private void CheckSecurityControls(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.Attributes.TryGetValue("ImplementedControls", out var controlsObj) &&
                controlsObj is IEnumerable<string> implementedControls)
            {
                var implemented = new HashSet<string>(implementedControls, StringComparer.OrdinalIgnoreCase);
                var missing = _requiredControls.Except(implemented).ToList();

                if (missing.Any())
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "NIS2-004",
                        Description = $"Missing required security controls: {string.Join(", ", missing)}",
                        Severity = ViolationSeverity.High,
                        Remediation = "Implement all required NIS2 cybersecurity measures",
                        RegulatoryReference = "NIS2 Article 21"
                    });
                }
            }
            else
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NIS2-005",
                    Description = "No security controls inventory documented",
                    Severity = ViolationSeverity.High,
                    Remediation = "Document all implemented security controls",
                    RegulatoryReference = "NIS2 Article 21"
                });
            }
        }

        private void CheckRiskManagement(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("RiskManagementFramework", out var frameworkObj) || frameworkObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NIS2-006",
                    Description = "No risk management framework established",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Establish comprehensive risk management framework covering all network and information systems",
                    RegulatoryReference = "NIS2 Article 21(2)(a)"
                });
            }

            if (context.Attributes.TryGetValue("ManagementApproval", out var approvalObj) && approvalObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NIS2-007",
                    Description = "Management body has not approved cybersecurity measures",
                    Severity = ViolationSeverity.High,
                    Remediation = "Obtain management body approval and oversight of cybersecurity measures",
                    RegulatoryReference = "NIS2 Article 20"
                });
            }
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("nis2.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("nis2.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
