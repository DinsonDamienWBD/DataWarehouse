using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.USFederal
{
    /// <summary>
    /// FERPA (Family Educational Rights and Privacy Act) compliance strategy.
    /// Validates student education records privacy requirements.
    /// </summary>
    public sealed class FerpaStrategy : ComplianceStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "ferpa";

        /// <inheritdoc/>
        public override string StrategyName => "FERPA Compliance";

        /// <inheritdoc/>
        public override string Framework => "FERPA";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("ferpa.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            CheckConsentRequirements(context, violations, recommendations);
            CheckAccessRights(context, violations, recommendations);
            CheckDirectoryInformation(context, violations, recommendations);
            CheckDisclosureLogging(context, violations, recommendations);

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

        private void CheckConsentRequirements(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.DataSubjectCategories.Contains("student-education-record", StringComparer.OrdinalIgnoreCase))
            {
                if (context.OperationType.Equals("disclosure", StringComparison.OrdinalIgnoreCase) ||
                    context.OperationType.Equals("third-party-sharing", StringComparison.OrdinalIgnoreCase))
                {
                    if (!context.Attributes.TryGetValue("ParentalConsent", out var consentObj) || consentObj is not true)
                    {
                        if (!context.Attributes.TryGetValue("FerpaException", out var exceptionObj) || exceptionObj is not true)
                        {
                            violations.Add(new ComplianceViolation
                            {
                                Code = "FERPA-001",
                                Description = "Education record disclosure without parental consent or valid exception",
                                Severity = ViolationSeverity.Critical,
                                Remediation = "Obtain written parental consent or identify applicable FERPA exception",
                                RegulatoryReference = "20 USC 1232g(b)"
                            });
                        }
                    }
                }

                if (context.Attributes.TryGetValue("StudentAge", out var ageObj) &&
                    ageObj is int age && age >= 18)
                {
                    if (!context.Attributes.TryGetValue("StudentConsent", out var studentConsentObj) || studentConsentObj is not true)
                    {
                        recommendations.Add("Student is 18 or older - consent rights transfer from parents to eligible student");
                    }
                }
            }
        }

        private void CheckAccessRights(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.OperationType.Equals("access-request", StringComparison.OrdinalIgnoreCase))
            {
                if (context.Attributes.TryGetValue("RequestResponseDays", out var daysObj) &&
                    daysObj is int days && days > 45)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "FERPA-002",
                        Description = $"Education record access request not fulfilled within 45 days ({days} days elapsed)",
                        Severity = ViolationSeverity.High,
                        Remediation = "Provide access to education records within 45 days of request",
                        RegulatoryReference = "34 CFR 99.10"
                    });
                }
            }

            if (context.OperationType.Equals("amendment-request", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("HearingOffered", out var hearingObj) || hearingObj is not true)
                {
                    recommendations.Add("If amendment request denied, offer hearing opportunity per FERPA");
                }
            }
        }

        private void CheckDirectoryInformation(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.Attributes.TryGetValue("DirectoryInformation", out var dirObj) && dirObj is true)
            {
                if (!context.Attributes.TryGetValue("AnnualNoticeProvided", out var noticeObj) || noticeObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "FERPA-003",
                        Description = "Directory information disclosed without annual notice to parents/students",
                        Severity = ViolationSeverity.High,
                        Remediation = "Provide annual notice of directory information and opt-out rights",
                        RegulatoryReference = "34 CFR 99.37"
                    });
                }

                if (!context.Attributes.TryGetValue("OptOutHonored", out var optOutObj) || optOutObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "FERPA-004",
                        Description = "Directory information opt-out request not honored",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Honor all opt-out requests for directory information disclosure",
                        RegulatoryReference = "34 CFR 99.37"
                    });
                }
            }
        }

        private void CheckDisclosureLogging(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.OperationType.Equals("disclosure", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("DisclosureRecorded", out var recordedObj) || recordedObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "FERPA-005",
                        Description = "Education record disclosure not logged",
                        Severity = ViolationSeverity.Medium,
                        Remediation = "Maintain record of all disclosures including party, interest, and date",
                        RegulatoryReference = "34 CFR 99.32"
                    });
                }
            }
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("ferpa.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("ferpa.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
