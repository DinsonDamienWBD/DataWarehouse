using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.AsiaPacific
{
    /// <summary>
    /// China Cybersecurity Law (CSL) compliance strategy.
    /// </summary>
    public sealed class CslStrategy : ComplianceStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "csl";

        /// <inheritdoc/>
        public override string StrategyName => "China CSL";

        /// <inheritdoc/>
        public override string Framework => "CSL";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
        IncrementCounter("csl.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check network security level protection (MLPS)
            if (!context.Attributes.TryGetValue("MlpsLevel", out var mlpsObj))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CSL-001",
                    Description = "Multi-Level Protection Scheme (MLPS) level not determined",
                    Severity = ViolationSeverity.High,
                    Remediation = "Classify system under MLPS 2.0 and implement corresponding controls (Art. 21)",
                    RegulatoryReference = "CSL Article 21"
                });
            }

            // Check data localization (Article 37)
            bool isCriticalInfo = context.Attributes.TryGetValue("IsCriticalInformation", out var criticalObj) && criticalObj is true;
            if (isCriticalInfo && !string.IsNullOrEmpty(context.DestinationLocation) &&
                !context.DestinationLocation.StartsWith("CN", StringComparison.OrdinalIgnoreCase))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CSL-002",
                    Description = "Critical information infrastructure data stored outside China",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Store personal information and important data within China (Art. 37)",
                    RegulatoryReference = "CSL Article 37"
                });
            }

            // Check security incident response (Article 25)
            if (!context.Attributes.TryGetValue("IncidentResponsePlan", out var irObj) || irObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CSL-003",
                    Description = "Network security incident response plan not established",
                    Severity = ViolationSeverity.High,
                    Remediation = "Develop emergency response plan and reporting procedures (Art. 25)",
                    RegulatoryReference = "CSL Article 25"
                });
            }

            // Check real-name verification (Article 24)
            if (context.OperationType.Equals("user-registration", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("RealNameVerified", out var nameObj) || nameObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "CSL-004",
                        Description = "Real-name verification not implemented for user registration",
                        Severity = ViolationSeverity.Medium,
                        Remediation = "Implement real-name verification for network services (Art. 24)",
                        RegulatoryReference = "CSL Article 24"
                    });
                }
            }

            // Check security technical measures (Article 21)
            if (!context.Attributes.TryGetValue("TechnicalMeasures", out var measuresObj) || measuresObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CSL-005",
                    Description = "Security technical measures not implemented",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Implement virus protection, intrusion detection, and data backup (Art. 21)",
                    RegulatoryReference = "CSL Article 21"
                });
            }

            recommendations.Add("Obtain MLPS certification for critical information infrastructure");

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
        IncrementCounter("csl.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("csl.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
