using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.USFederal
{
    /// <summary>
    /// TX-RAMP (Texas Risk and Authorization Management Program) compliance strategy.
    /// Validates Texas state cloud service authorization requirements.
    /// </summary>
    public sealed class TxRampStrategy : ComplianceStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "txramp";

        /// <inheritdoc/>
        public override string StrategyName => "TX-RAMP Compliance";

        /// <inheritdoc/>
        public override string Framework => "TX-RAMP";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("tx_ramp.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            CheckTexasAuthorization(context, violations, recommendations);
            CheckSecurityRequirements(context, violations, recommendations);
            CheckDataResidency(context, violations, recommendations);
            CheckBreachNotification(context, violations, recommendations);

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

        private void CheckTexasAuthorization(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.Attributes.TryGetValue("TexasStateData", out var texasObj) && texasObj is true)
            {
                if (!context.Attributes.TryGetValue("TxRampAuthorization", out var authObj) ||
                    authObj is not string authStatus ||
                    !authStatus.Equals("authorized", StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "TXRAMP-001",
                        Description = "Cloud service not TX-RAMP authorized for Texas state data",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Obtain TX-RAMP certification from Texas Department of Information Resources",
                        RegulatoryReference = "Texas TAC 202.76"
                    });
                }

                if (context.Attributes.TryGetValue("TxRampLevel", out var levelObj) &&
                    levelObj is string level &&
                    level.Equals("level-2", StringComparison.OrdinalIgnoreCase))
                {
                    if (!context.Attributes.TryGetValue("PenetrationTest", out var pentestObj) || pentestObj is not true)
                    {
                        violations.Add(new ComplianceViolation
                        {
                            Code = "TXRAMP-002",
                            Description = "Level 2 certification requires annual penetration testing",
                            Severity = ViolationSeverity.High,
                            Remediation = "Complete annual penetration test by approved assessor",
                            RegulatoryReference = "TX-RAMP Level 2 Requirements"
                        });
                    }
                }
            }
        }

        private void CheckSecurityRequirements(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("Nist800171Compliant", out var nistObj) || nistObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "TXRAMP-003",
                    Description = "NIST 800-171 compliance not demonstrated",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement NIST 800-171 security requirements",
                    RegulatoryReference = "TX-RAMP Security Framework"
                });
            }

            if (!context.Attributes.TryGetValue("MultiFactorAuth", out var mfaObj) || mfaObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "TXRAMP-004",
                    Description = "Multi-factor authentication not enabled",
                    Severity = ViolationSeverity.High,
                    Remediation = "Enable MFA for all administrative and user access",
                    RegulatoryReference = "TX-RAMP Security Controls"
                });
            }
        }

        private void CheckDataResidency(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!string.IsNullOrEmpty(context.DestinationLocation) &&
                !context.DestinationLocation.Equals("US", StringComparison.OrdinalIgnoreCase) &&
                !context.DestinationLocation.StartsWith("US-", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("DataResidencyApproval", out var approvalObj) || approvalObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "TXRAMP-005",
                        Description = $"Texas state data transfer to {context.DestinationLocation} without approval",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Ensure data remains within US or obtain specific approval",
                        RegulatoryReference = "Texas Government Code 2054"
                    });
                }
            }
        }

        private void CheckBreachNotification(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (string.Equals(context.OperationType, "security-incident", StringComparison.OrdinalIgnoreCase))
            {
                if (context.Attributes.TryGetValue("TexasStateData", out var texasObj) && texasObj is true)
                {
                    if (!context.Attributes.TryGetValue("DirNotified", out var notifyObj) || notifyObj is not true)
                    {
                        violations.Add(new ComplianceViolation
                        {
                            Code = "TXRAMP-006",
                            Description = "Security incident involving state data not reported to DIR",
                            Severity = ViolationSeverity.Critical,
                            Remediation = "Notify Texas DIR of security incidents within 48 hours",
                            RegulatoryReference = "Texas Government Code 2054.5192"
                        });
                    }
                }
            }
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("tx_ramp.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("tx_ramp.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
