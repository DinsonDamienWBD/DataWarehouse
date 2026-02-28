using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.AsiaPacific
{
    /// <summary>
    /// China Data Security Law (DSL) compliance strategy.
    /// </summary>
    public sealed class DslStrategy : ComplianceStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "dsl";

        /// <inheritdoc/>
        public override string StrategyName => "China DSL";

        /// <inheritdoc/>
        public override string Framework => "DSL";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("dsl.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check data classification (Article 21)
            if (string.IsNullOrEmpty(context.DataClassification))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "DSL-001",
                    Description = "Data not classified according to DSL requirements",
                    Severity = ViolationSeverity.High,
                    Remediation = "Classify data and implement tiered protection measures (Art. 21)",
                    RegulatoryReference = "DSL Article 21"
                });
            }

            // Check important data handling (Article 27)
            if (context.Attributes.TryGetValue("IsImportantData", out var importantObj) && importantObj is true)
            {
                if (!context.Attributes.TryGetValue("SecurityReviewConducted", out var reviewObj) || reviewObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "DSL-002",
                        Description = "Security review not conducted for important data",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Conduct data security review and obtain approval (Art. 27)",
                        RegulatoryReference = "DSL Article 27"
                    });
                }
            }

            // Check cross-border data transfer (Article 31)
            if (!string.IsNullOrEmpty(context.DestinationLocation) &&
                !context.DestinationLocation.StartsWith("CN", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("CrossBorderApproved", out var cbObj) || cbObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "DSL-003",
                        Description = "Cross-border data transfer not approved",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Obtain security assessment or certification for data export (Art. 31)",
                        RegulatoryReference = "DSL Article 31"
                    });
                }
            }

            // Check data lifecycle management (Article 27)
            if (!context.Attributes.TryGetValue("DataLifecycleManaged", out var lifecycleObj) || lifecycleObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "DSL-004",
                    Description = "Data lifecycle security management not established",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Implement security management for data collection, storage, use, processing, transmission, and deletion (Art. 27)",
                    RegulatoryReference = "DSL Article 27"
                });
            }

            // Check data security risk assessment (Article 29)
            if (!context.Attributes.TryGetValue("RiskAssessmentCompleted", out var riskObj) || riskObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "DSL-005",
                    Description = "Data security risk assessment not conducted",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Conduct regular data security risk assessments (Art. 29)",
                    RegulatoryReference = "DSL Article 29"
                });
            }

            recommendations.Add("Establish data security responsibility system with designated personnel");

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
            IncrementCounter("dsl.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("dsl.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
