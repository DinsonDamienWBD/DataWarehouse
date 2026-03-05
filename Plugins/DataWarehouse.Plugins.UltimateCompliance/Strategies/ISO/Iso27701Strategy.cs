using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.ISO
{
    /// <summary>
    /// ISO/IEC 27701 Privacy Information Management System (PIMS) compliance strategy.
    /// </summary>
    public sealed class Iso27701Strategy : ComplianceStrategyBase
    {
        /// <summary>
        /// Configurable data-field count threshold for the data-minimization recommendation (ISO 27701 §7.2.2).
        /// Default of 30 is a practical heuristic; override via configuration key "DataMinimizationFieldThreshold".
        /// </summary>
        public int DataMinimizationFieldThreshold { get; set; } = 30;

        /// <inheritdoc/>
        public override string StrategyId => "iso27701";

        /// <inheritdoc/>
        public override string StrategyName => "ISO/IEC 27701";

        /// <inheritdoc/>
        public override string Framework => "ISO27701";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("iso27701.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check PIMS scope definition (5.2.1)
            if (!context.Attributes.TryGetValue("PimsScopeDefined", out var scopeObj) || scopeObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ISO27701-001",
                    Description = "Privacy Information Management System scope not defined",
                    Severity = ViolationSeverity.High,
                    Remediation = "Define PIMS scope including boundaries and applicability",
                    RegulatoryReference = "ISO/IEC 27701:2019 5.2.1"
                });
            }

            // Check privacy governance (6.2.1)
            if (!context.Attributes.TryGetValue("PrivacyGovernanceEstablished", out var governanceObj) || governanceObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ISO27701-002",
                    Description = "Privacy governance structure not established",
                    Severity = ViolationSeverity.High,
                    Remediation = "Establish privacy governance with defined roles and responsibilities",
                    RegulatoryReference = "ISO/IEC 27701:2019 6.2.1"
                });
            }

            // Check Data Protection Officer (7.4.8)
            if (context.DataSubjectCategories.Count > 0)
            {
                if (!context.Attributes.TryGetValue("DpoDesignated", out var dpoObj) || dpoObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "ISO27701-003",
                        Description = "Data Protection Officer not designated for PII processing",
                        Severity = ViolationSeverity.Medium,
                        Remediation = "Designate DPO or privacy officer with appropriate authority",
                        RegulatoryReference = "ISO/IEC 27701:2019 7.4.8"
                    });
                }
            }

            // Check PII processing records (7.3.1)
            if (!context.Attributes.TryGetValue("ProcessingRecordMaintained", out var recordObj) || recordObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ISO27701-004",
                    Description = "Record of PII processing activities not maintained",
                    Severity = ViolationSeverity.High,
                    Remediation = "Maintain comprehensive records of all PII processing activities",
                    RegulatoryReference = "ISO/IEC 27701:2019 7.3.1"
                });
            }

            // Check privacy by design (6.4.2)
            if (!string.IsNullOrEmpty(context.OperationType) && context.OperationType.Equals("create", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("PrivacyByDesignApplied", out var pbdObj) || pbdObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "ISO27701-005",
                        Description = "Privacy by design principles not applied to new processing",
                        Severity = ViolationSeverity.Medium,
                        Remediation = "Integrate privacy considerations into system design and development",
                        RegulatoryReference = "ISO/IEC 27701:2019 6.4.2"
                    });
                }
            }

            // Check data minimization (7.2.2) — threshold is configurable via DataMinimizationFieldThreshold
            if (context.Attributes.TryGetValue("DataFields", out var fieldsObj) &&
                fieldsObj is IEnumerable<string> fields &&
                fields.Count() > DataMinimizationFieldThreshold)
            {
                recommendations.Add("Review data collection scope for compliance with data minimization principles");
            }

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
            IncrementCounter("iso27701.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("iso27701.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
