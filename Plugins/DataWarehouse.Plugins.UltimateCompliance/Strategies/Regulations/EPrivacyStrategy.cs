using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Regulations
{
    /// <summary>
    /// ePrivacy Directive compliance strategy.
    /// Implements EU rules on privacy and electronic communications.
    /// </summary>
    /// <remarks>
    /// Directive 2002/58/EC (ePrivacy Directive) covers confidentiality of communications,
    /// cookies, direct marketing, and traffic data processing.
    /// Complements GDPR for electronic communications.
    /// </remarks>
    public sealed class EPrivacyStrategy : ComplianceStrategyBase
    {
        private readonly HashSet<string> _allowedCookieTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "strictly-necessary", "functional", "performance", "targeting"
        };

        /// <inheritdoc/>
        public override string StrategyId => "eprivacy";

        /// <inheritdoc/>
        public override string StrategyName => "ePrivacy Directive";

        /// <inheritdoc/>
        public override string Framework => "ePrivacy";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check cookie consent
            CheckCookieConsent(context, violations, recommendations);

            // Check communication confidentiality
            CheckCommunicationConfidentiality(context, violations, recommendations);

            // Check direct marketing consent
            CheckDirectMarketingConsent(context, violations, recommendations);

            // Check traffic data processing
            CheckTrafficDataProcessing(context, violations, recommendations);

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

        private void CheckCookieConsent(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.OperationType.Equals("cookie-placement", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("CookieType", out var cookieTypeObj) ||
                    cookieTypeObj is not string cookieType)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "EPRIVACY-001",
                        Description = "Cookie type not specified",
                        Severity = ViolationSeverity.Medium,
                        Remediation = "Classify cookies as strictly-necessary, functional, performance, or targeting"
                    });
                    return;
                }

                if (!cookieType.Equals("strictly-necessary", StringComparison.OrdinalIgnoreCase))
                {
                    if (!context.Attributes.TryGetValue("ConsentObtained", out var consentObj) || consentObj is not true)
                    {
                        violations.Add(new ComplianceViolation
                        {
                            Code = "EPRIVACY-002",
                            Description = $"Non-essential cookie ({cookieType}) placed without consent",
                            Severity = ViolationSeverity.High,
                            Remediation = "Obtain prior informed consent before placing non-essential cookies",
                            RegulatoryReference = "ePrivacy Directive Article 5(3)"
                        });
                    }
                }
            }
        }

        private void CheckCommunicationConfidentiality(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.OperationType.Equals("communication-interception", StringComparison.OrdinalIgnoreCase) ||
                context.OperationType.Equals("communication-monitoring", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("LegalBasis", out var basisObj) ||
                    basisObj is not string basis ||
                    string.IsNullOrEmpty(basis))
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "EPRIVACY-003",
                        Description = "Communication interception without legal basis",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Ensure lawful authority or user consent for communication interception",
                        RegulatoryReference = "ePrivacy Directive Article 5(1)"
                    });
                }
            }

            if (context.DataClassification.Contains("communication-metadata", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("EncryptionEnabled", out var encObj) || encObj is not true)
                {
                    recommendations.Add("Enable encryption for communication metadata");
                }
            }
        }

        private void CheckDirectMarketingConsent(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.OperationType.Equals("direct-marketing", StringComparison.OrdinalIgnoreCase))
            {
                if (context.Attributes.TryGetValue("MarketingChannel", out var channelObj) &&
                    channelObj is string channel)
                {
                    if (channel.Equals("email", StringComparison.OrdinalIgnoreCase) ||
                        channel.Equals("sms", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!context.Attributes.TryGetValue("OptInConsent", out var optInObj) || optInObj is not true)
                        {
                            violations.Add(new ComplianceViolation
                            {
                                Code = "EPRIVACY-004",
                                Description = $"Electronic marketing via {channel} without opt-in consent",
                                Severity = ViolationSeverity.High,
                                Remediation = "Obtain prior opt-in consent for electronic direct marketing",
                                RegulatoryReference = "ePrivacy Directive Article 13"
                            });
                        }
                    }
                }

                if (!context.Attributes.TryGetValue("OptOutMechanism", out var optOutObj) || optOutObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "EPRIVACY-005",
                        Description = "No opt-out mechanism provided for marketing communications",
                        Severity = ViolationSeverity.Medium,
                        Remediation = "Provide clear and easy opt-out mechanism in every marketing message"
                    });
                }
            }
        }

        private void CheckTrafficDataProcessing(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.DataClassification.Contains("traffic-data", StringComparison.OrdinalIgnoreCase) ||
                context.DataClassification.Contains("location-data", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("DataAnonymized", out var anonObj) || anonObj is not true)
                {
                    if (!context.Attributes.TryGetValue("ConsentForProcessing", out var consentObj) || consentObj is not true)
                    {
                        violations.Add(new ComplianceViolation
                        {
                            Code = "EPRIVACY-006",
                            Description = "Traffic/location data processing without consent or anonymization",
                            Severity = ViolationSeverity.High,
                            Remediation = "Anonymize traffic data or obtain explicit consent for processing",
                            RegulatoryReference = "ePrivacy Directive Article 6, 9"
                        });
                    }
                }

                if (context.Attributes.TryGetValue("RetentionPeriod", out var retentionObj) &&
                    retentionObj is TimeSpan retention &&
                    retention > TimeSpan.FromDays(90))
                {
                    recommendations.Add("Consider reducing traffic data retention period to minimum necessary");
                }
            }
        }
    }
}
