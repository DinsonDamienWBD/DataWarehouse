using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.USFederal
{
    /// <summary>
    /// FISMA (Federal Information Security Management Act) compliance strategy.
    /// Validates security categorization, continuous monitoring, and authorization requirements.
    /// </summary>
    public sealed class FismaStrategy : ComplianceStrategyBase
    {
        private readonly HashSet<string> _validImpactLevels = new(StringComparer.OrdinalIgnoreCase)
        {
            "low", "moderate", "high"
        };

        /// <inheritdoc/>
        public override string StrategyId => "fisma";

        /// <inheritdoc/>
        public override string StrategyName => "FISMA Compliance";

        /// <inheritdoc/>
        public override string Framework => "FISMA";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            CheckSecurityCategorization(context, violations, recommendations);
            CheckAuthorizationToOperate(context, violations, recommendations);
            CheckContinuousMonitoring(context, violations, recommendations);
            CheckIncidentResponse(context, violations, recommendations);
            CheckRiskAssessment(context, violations, recommendations);

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

        private void CheckSecurityCategorization(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("FipsImpactLevel", out var impactObj) ||
                impactObj is not string impactLevel ||
                string.IsNullOrEmpty(impactLevel))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "FISMA-001",
                    Description = "No FIPS 199 security categorization assigned",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Categorize system using FIPS 199 (low/moderate/high impact)",
                    RegulatoryReference = "FISMA 44 USC 3544(a)(1)"
                });
                return;
            }

            if (!_validImpactLevels.Contains(impactLevel))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "FISMA-002",
                    Description = $"Invalid FIPS 199 impact level: {impactLevel}",
                    Severity = ViolationSeverity.High,
                    Remediation = "Use valid impact level: low, moderate, or high",
                    RegulatoryReference = "FIPS 199"
                });
            }
        }

        private void CheckAuthorizationToOperate(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("AtoStatus", out var atoObj) ||
                atoObj is not string atoStatus ||
                !atoStatus.Equals("active", StringComparison.OrdinalIgnoreCase))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "FISMA-003",
                    Description = "No active Authorization to Operate (ATO)",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Obtain ATO from Authorizing Official before system operation",
                    RegulatoryReference = "NIST SP 800-37"
                });
            }

            if (context.Attributes.TryGetValue("AtoExpirationDate", out var expiryObj) &&
                expiryObj is DateTime expiryDate)
            {
                var daysUntilExpiry = (expiryDate - DateTime.UtcNow).TotalDays;
                if (daysUntilExpiry < 0)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "FISMA-004",
                        Description = "Authorization to Operate has expired",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Renew ATO through reauthorization process",
                        RegulatoryReference = "NIST SP 800-37"
                    });
                }
                else if (daysUntilExpiry < 90)
                {
                    recommendations.Add($"ATO expires in {daysUntilExpiry:F0} days - begin reauthorization process");
                }
            }
        }

        private void CheckContinuousMonitoring(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("ContinuousMonitoring", out var monitoringObj) || monitoringObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "FISMA-005",
                    Description = "Continuous monitoring program not implemented",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement continuous monitoring of security controls",
                    RegulatoryReference = "FISMA 44 USC 3554"
                });
            }

            if (context.Attributes.TryGetValue("LastControlAssessment", out var lastAssessObj) &&
                lastAssessObj is DateTime lastAssess)
            {
                var daysSinceAssessment = (DateTime.UtcNow - lastAssess).TotalDays;
                if (daysSinceAssessment > 365)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "FISMA-006",
                        Description = $"Security control assessment overdue ({daysSinceAssessment:F0} days since last assessment)",
                        Severity = ViolationSeverity.High,
                        Remediation = "Conduct annual security control assessment",
                        RegulatoryReference = "NIST SP 800-37"
                    });
                }
            }
        }

        private void CheckIncidentResponse(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("IncidentResponsePlan", out var planObj) || planObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "FISMA-007",
                    Description = "No incident response plan documented",
                    Severity = ViolationSeverity.High,
                    Remediation = "Develop and maintain incident response plan",
                    RegulatoryReference = "FISMA 44 USC 3554(b)"
                });
            }

            if (context.OperationType.Equals("security-incident", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("UsCertNotified", out var certObj) || certObj is not true)
                {
                    recommendations.Add("Major incidents must be reported to US-CERT within required timeframe");
                }
            }
        }

        private void CheckRiskAssessment(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.Attributes.TryGetValue("LastRiskAssessment", out var riskDateObj) &&
                riskDateObj is DateTime riskDate)
            {
                var daysSinceRisk = (DateTime.UtcNow - riskDate).TotalDays;
                if (daysSinceRisk > 365)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "FISMA-008",
                        Description = "Risk assessment not conducted within required timeframe",
                        Severity = ViolationSeverity.High,
                        Remediation = "Conduct annual risk assessment of information systems",
                        RegulatoryReference = "FISMA 44 USC 3554(a)(1)"
                    });
                }
            }
        }
    }
}
