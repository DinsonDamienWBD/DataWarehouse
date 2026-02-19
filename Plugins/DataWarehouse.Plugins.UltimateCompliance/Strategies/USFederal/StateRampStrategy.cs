using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.USFederal
{
    /// <summary>
    /// StateRAMP (State Risk and Authorization Management Program) compliance strategy.
    /// Validates state-level cloud service authorization and control requirements.
    /// </summary>
    public sealed class StateRampStrategy : ComplianceStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "stateramp";

        /// <inheritdoc/>
        public override string StrategyName => "StateRAMP Compliance";

        /// <inheritdoc/>
        public override string Framework => "StateRAMP";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
        IncrementCounter("state_ramp.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            CheckAuthorizationStatus(context, violations, recommendations);
            CheckControlMapping(context, violations, recommendations);
            CheckContinuousMonitoring(context, violations, recommendations);
            CheckStateDataHandling(context, violations, recommendations);

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

        private void CheckAuthorizationStatus(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("StateRampAuthorization", out var authObj) ||
                authObj is not string authStatus ||
                !authStatus.Equals("authorized", StringComparison.OrdinalIgnoreCase))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "STATERAMP-001",
                    Description = "Cloud service not StateRAMP authorized",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Obtain StateRAMP authorization before state data processing",
                    RegulatoryReference = "StateRAMP Authorization Package"
                });
            }

            if (context.Attributes.TryGetValue("ImpactLevel", out var impactObj) &&
                impactObj is string impactLevel &&
                (impactLevel.Equals("moderate", StringComparison.OrdinalIgnoreCase) ||
                 impactLevel.Equals("high", StringComparison.OrdinalIgnoreCase)))
            {
                if (!context.Attributes.TryGetValue("IndependentAssessment", out var assessObj) || assessObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "STATERAMP-002",
                        Description = $"Moderate/high impact level requires independent 3PAO assessment",
                        Severity = ViolationSeverity.High,
                        Remediation = "Complete independent assessment by StateRAMP-recognized 3PAO",
                        RegulatoryReference = "StateRAMP PMO Guidance"
                    });
                }
            }
        }

        private void CheckControlMapping(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("Nist80053Mapped", out var mappedObj) || mappedObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "STATERAMP-003",
                    Description = "NIST 800-53 control mapping not documented",
                    Severity = ViolationSeverity.High,
                    Remediation = "Map security controls to NIST 800-53 baseline",
                    RegulatoryReference = "StateRAMP Control Baseline"
                });
            }

            if (context.Attributes.TryGetValue("ControlGaps", out var gapsObj) &&
                gapsObj is int gapCount && gapCount > 0)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "STATERAMP-004",
                    Description = $"{gapCount} control gaps identified",
                    Severity = ViolationSeverity.High,
                    Remediation = "Remediate all control gaps or document Plan of Action and Milestones (POA&M)",
                    RegulatoryReference = "StateRAMP Security Package"
                });
            }
        }

        private void CheckContinuousMonitoring(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("ConMonPlan", out var planObj) || planObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "STATERAMP-005",
                    Description = "Continuous monitoring plan not established",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Establish continuous monitoring strategy and reporting",
                    RegulatoryReference = "StateRAMP Continuous Monitoring Guide"
                });
            }

            if (context.Attributes.TryGetValue("LastVulnerabilityScan", out var scanObj) &&
                scanObj is DateTime lastScan)
            {
                var daysSinceScan = (DateTime.UtcNow - lastScan).TotalDays;
                if (daysSinceScan > 30)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "STATERAMP-006",
                        Description = $"Vulnerability scanning overdue ({daysSinceScan:F0} days since last scan)",
                        Severity = ViolationSeverity.High,
                        Remediation = "Conduct monthly vulnerability scanning",
                        RegulatoryReference = "StateRAMP ConMon Requirements"
                    });
                }
            }
        }

        private void CheckStateDataHandling(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.DataClassification.Equals("sensitive", StringComparison.OrdinalIgnoreCase) ||
                context.DataClassification.Equals("confidential", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("EncryptionInTransit", out var transitObj) || transitObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "STATERAMP-007",
                        Description = "State sensitive data not encrypted in transit",
                        Severity = ViolationSeverity.High,
                        Remediation = "Implement FIPS 140-2 validated encryption for data in transit",
                        RegulatoryReference = "StateRAMP Security Controls"
                    });
                }

                if (!context.Attributes.TryGetValue("EncryptionAtRest", out var restObj) || restObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "STATERAMP-008",
                        Description = "State sensitive data not encrypted at rest",
                        Severity = ViolationSeverity.High,
                        Remediation = "Implement FIPS 140-2 validated encryption for data at rest",
                        RegulatoryReference = "StateRAMP Security Controls"
                    });
                }
            }
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("state_ramp.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("state_ramp.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
