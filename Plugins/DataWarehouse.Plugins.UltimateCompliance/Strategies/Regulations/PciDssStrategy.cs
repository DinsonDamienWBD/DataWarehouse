using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Regulations
{
    /// <summary>
    /// PCI-DSS (Payment Card Industry Data Security Standard) compliance strategy.
    /// Validates operations against payment card data security requirements.
    /// </summary>
    /// <remarks>
    /// Covers all 12 PCI-DSS requirements:
    /// - Requirements 1-2: Build and maintain a secure network
    /// - Requirements 3-4: Protect cardholder data
    /// - Requirements 5-6: Maintain a vulnerability management program
    /// - Requirements 7-9: Implement strong access control measures
    /// - Requirements 10-11: Regularly monitor and test networks
    /// - Requirement 12: Maintain an information security policy
    /// </remarks>
    public sealed class PciDssStrategy : ComplianceStrategyBase
    {
        private readonly HashSet<string> _cardholderDataElements = new(StringComparer.OrdinalIgnoreCase)
        {
            "pan", "primary-account-number", "cardholder-name", "expiration-date",
            "service-code", "full-track-data", "cav2", "cvv2", "cvc2", "cid", "pin", "pin-block"
        };

        private readonly HashSet<string> _sensitiveAuthData = new(StringComparer.OrdinalIgnoreCase)
        {
            "full-track-data", "magnetic-stripe-data", "cav2", "cvv2", "cvc2", "cid",
            "pin", "pin-block", "card-verification-value", "card-verification-code"
        };

        /// <inheritdoc/>
        public override string StrategyId => "pci-dss";

        /// <inheritdoc/>
        public override string StrategyName => "PCI-DSS Compliance";

        /// <inheritdoc/>
        public override string Framework => "PCI-DSS";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            bool containsCardholderData = CheckCardholderDataPresence(context);

            if (containsCardholderData)
            {
                // Requirement 3: Protect stored cardholder data
                CheckDataProtection(context, violations, recommendations);

                // Requirement 4: Encrypt transmission of cardholder data
                CheckTransmissionSecurity(context, violations, recommendations);

                // Requirement 7: Restrict access to cardholder data
                CheckAccessControl(context, violations, recommendations);

                // Requirement 8: Identify and authenticate access
                CheckAuthentication(context, violations, recommendations);

                // Requirement 9: Restrict physical access
                CheckPhysicalSecurity(context, violations, recommendations);

                // Requirement 10: Track and monitor access
                CheckAuditLogging(context, violations, recommendations);

                // Requirement 11: Test security systems
                CheckSecurityTesting(context, violations, recommendations);

                // Requirement 12: Security policy
                CheckSecurityPolicy(context, violations, recommendations);
            }

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
                Recommendations = recommendations,
                Metadata = new Dictionary<string, object>
                {
                    ["ContainsCardholderData"] = containsCardholderData,
                    ["OperationType"] = context.OperationType,
                    ["DataClassification"] = context.DataClassification
                }
            });
        }

        private bool CheckCardholderDataPresence(ComplianceContext context)
        {
            // Check data classification
            if (context.DataClassification.Contains("card", StringComparison.OrdinalIgnoreCase) ||
                context.DataClassification.Contains("payment", StringComparison.OrdinalIgnoreCase) ||
                context.DataClassification.Contains("pan", StringComparison.OrdinalIgnoreCase) ||
                context.DataClassification.Contains("pci", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Check data subject categories
            if (context.DataSubjectCategories.Any(c => _cardholderDataElements.Contains(c)))
            {
                return true;
            }

            // Check explicit flag
            if (context.Attributes.TryGetValue("ContainsCardholderData", out var chdObj) && chdObj is true)
            {
                return true;
            }

            return false;
        }

        private void CheckDataProtection(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            // Requirement 3.2: Do not store sensitive authentication data after authorization
            var hasSensitiveAuthData = context.DataSubjectCategories
                .Any(c => _sensitiveAuthData.Contains(c));

            if (hasSensitiveAuthData)
            {
                if (context.OperationType.Equals("store", StringComparison.OrdinalIgnoreCase) ||
                    context.OperationType.Equals("persist", StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "PCI-3.2",
                        Description = "Sensitive authentication data cannot be stored after authorization",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Delete sensitive authentication data immediately after authorization",
                        RegulatoryReference = "PCI-DSS Requirement 3.2"
                    });
                }
            }

            // Requirement 3.3: Mask PAN when displayed
            if (context.OperationType.Equals("display", StringComparison.OrdinalIgnoreCase) ||
                context.OperationType.Equals("render", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("PANMasked", out var maskedObj) || maskedObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "PCI-3.3",
                        Description = "PAN must be masked when displayed (show only first 6 and last 4 digits)",
                        Severity = ViolationSeverity.High,
                        Remediation = "Mask PAN to show only first 6 and last 4 digits",
                        RegulatoryReference = "PCI-DSS Requirement 3.3"
                    });
                }
            }

            // Requirement 3.4: Render PAN unreadable when stored
            if (!context.Attributes.TryGetValue("PANEncrypted", out var encObj) || encObj is not true)
            {
                if (!context.Attributes.TryGetValue("PANTokenized", out var tokObj) || tokObj is not true)
                {
                    if (!context.Attributes.TryGetValue("PANTruncated", out var truncObj) || truncObj is not true)
                    {
                        if (!context.Attributes.TryGetValue("PANHashed", out var hashObj) || hashObj is not true)
                        {
                            violations.Add(new ComplianceViolation
                            {
                                Code = "PCI-3.4",
                                Description = "PAN must be rendered unreadable (encrypted, tokenized, truncated, or hashed)",
                                Severity = ViolationSeverity.Critical,
                                Remediation = "Implement one of: strong cryptography, truncation, index tokens, or one-way hashing",
                                RegulatoryReference = "PCI-DSS Requirement 3.4"
                            });
                        }
                    }
                }
            }

            // Requirement 3.5: Protect encryption keys
            if (context.Attributes.TryGetValue("PANEncrypted", out var encrypted) && encrypted is true)
            {
                if (!context.Attributes.TryGetValue("KeyManagementProcedures", out var kmpObj) || kmpObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "PCI-3.5",
                        Description = "Encryption keys must be protected with documented key management procedures",
                        Severity = ViolationSeverity.High,
                        Remediation = "Implement and document key management procedures",
                        RegulatoryReference = "PCI-DSS Requirement 3.5"
                    });
                }
            }

            // Requirement 3.6: Key management documentation
            if (!context.Attributes.TryGetValue("KeyRotationSchedule", out var krsObj) || krsObj is not true)
            {
                recommendations.Add("Document cryptographic key rotation schedule");
            }
        }

        private void CheckTransmissionSecurity(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            // Requirement 4.1: Use strong cryptography during transmission
            if (context.OperationType.Contains("transmit", StringComparison.OrdinalIgnoreCase) ||
                context.OperationType.Contains("transfer", StringComparison.OrdinalIgnoreCase) ||
                context.OperationType.Contains("send", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("TLSVersion", out var tlsObj))
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "PCI-4.1",
                        Description = "Cardholder data must be encrypted during transmission over open networks",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Use TLS 1.2 or higher for data transmission",
                        RegulatoryReference = "PCI-DSS Requirement 4.1"
                    });
                }
                else if (tlsObj is string tlsVersion)
                {
                    if (tlsVersion.Contains("1.0", StringComparison.OrdinalIgnoreCase) ||
                        tlsVersion.Contains("1.1", StringComparison.OrdinalIgnoreCase) ||
                        tlsVersion.Contains("SSL", StringComparison.OrdinalIgnoreCase))
                    {
                        violations.Add(new ComplianceViolation
                        {
                            Code = "PCI-4.1.1",
                            Description = $"TLS version {tlsVersion} is not acceptable. Minimum TLS 1.2 required",
                            Severity = ViolationSeverity.Critical,
                            Remediation = "Upgrade to TLS 1.2 or TLS 1.3",
                            RegulatoryReference = "PCI-DSS Requirement 4.1"
                        });
                    }
                }
            }

            // Requirement 4.2: Never send unprotected PAN via end-user messaging
            if (context.OperationType.Contains("email", StringComparison.OrdinalIgnoreCase) ||
                context.OperationType.Contains("message", StringComparison.OrdinalIgnoreCase) ||
                context.OperationType.Contains("chat", StringComparison.OrdinalIgnoreCase))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "PCI-4.2",
                    Description = "Never send unprotected PANs via end-user messaging technologies",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Use secure transmission methods, not email/chat/SMS for PANs",
                    RegulatoryReference = "PCI-DSS Requirement 4.2"
                });
            }
        }

        private void CheckAccessControl(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            // Requirement 7.1: Limit access to cardholder data by business need to know
            if (!context.Attributes.TryGetValue("BusinessNeedVerified", out var bnvObj) || bnvObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "PCI-7.1",
                    Description = "Access to cardholder data must be restricted to need-to-know basis",
                    Severity = ViolationSeverity.High,
                    Remediation = "Verify business need before granting access to cardholder data",
                    RegulatoryReference = "PCI-DSS Requirement 7.1"
                });
            }

            // Requirement 7.2: Access control system
            if (!context.Attributes.TryGetValue("AccessControlSystem", out var acsObj) || acsObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "PCI-7.2",
                    Description = "Access control system not configured for cardholder data environment",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement access control system covering all system components",
                    RegulatoryReference = "PCI-DSS Requirement 7.2"
                });
            }

            // Requirement 7.3: Default deny all
            if (!context.Attributes.TryGetValue("DefaultDenyAll", out var ddaObj) || ddaObj is not true)
            {
                recommendations.Add("Ensure access control policy defaults to deny all access");
            }
        }

        private void CheckAuthentication(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            // Requirement 8.1: Unique user identification
            if (string.IsNullOrEmpty(context.UserId))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "PCI-8.1",
                    Description = "Unique user identification required for all access to cardholder data",
                    Severity = ViolationSeverity.High,
                    Remediation = "Assign unique ID to each user with access to cardholder data",
                    RegulatoryReference = "PCI-DSS Requirement 8.1"
                });
            }

            // Requirement 8.2: Proper authentication
            if (!context.Attributes.TryGetValue("AuthenticationVerified", out var avObj) || avObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "PCI-8.2",
                    Description = "User authentication not verified for cardholder data access",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement and verify user authentication (password, token, biometric)",
                    RegulatoryReference = "PCI-DSS Requirement 8.2"
                });
            }

            // Requirement 8.3: Multi-factor authentication
            if (!context.Attributes.TryGetValue("MFAEnabled", out var mfaObj) || mfaObj is not true)
            {
                if (context.OperationType.Contains("admin", StringComparison.OrdinalIgnoreCase) ||
                    context.OperationType.Contains("remote", StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "PCI-8.3",
                        Description = "Multi-factor authentication required for administrative and remote access",
                        Severity = ViolationSeverity.High,
                        Remediation = "Implement MFA for all non-console administrative access and remote network access",
                        RegulatoryReference = "PCI-DSS Requirement 8.3"
                    });
                }
            }

            // Requirement 8.5: No group/shared accounts
            if (context.UserId != null &&
                (context.UserId.Contains("shared", StringComparison.OrdinalIgnoreCase) ||
                 context.UserId.Contains("group", StringComparison.OrdinalIgnoreCase) ||
                 context.UserId.Contains("generic", StringComparison.OrdinalIgnoreCase)))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "PCI-8.5",
                    Description = "Group, shared, or generic accounts not permitted for cardholder data access",
                    Severity = ViolationSeverity.High,
                    Remediation = "Use individual user accounts, not shared/group accounts",
                    RegulatoryReference = "PCI-DSS Requirement 8.5"
                });
            }
        }

        private void CheckPhysicalSecurity(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            // Requirement 9.1: Physical access controls
            if (context.OperationType.Contains("physical", StringComparison.OrdinalIgnoreCase) ||
                context.Attributes.TryGetValue("PhysicalAccess", out var paObj) && paObj is true)
            {
                if (!context.Attributes.TryGetValue("PhysicalAccessControlled", out var pacObj) || pacObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "PCI-9.1",
                        Description = "Physical access to cardholder data environment not controlled",
                        Severity = ViolationSeverity.High,
                        Remediation = "Implement appropriate facility entry controls",
                        RegulatoryReference = "PCI-DSS Requirement 9.1"
                    });
                }
            }

            // Requirement 9.5: Protect media containing cardholder data
            if (context.OperationType.Contains("backup", StringComparison.OrdinalIgnoreCase) ||
                context.OperationType.Contains("media", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("MediaSecured", out var msObj) || msObj is not true)
                {
                    recommendations.Add("Ensure physical security of media containing cardholder data");
                }
            }
        }

        private void CheckAuditLogging(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            // Requirement 10.1: Implement audit trails
            if (!context.Attributes.TryGetValue("AuditEnabled", out var aeObj) || aeObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "PCI-10.1",
                    Description = "Audit trails not enabled for cardholder data access",
                    Severity = ViolationSeverity.High,
                    Remediation = "Enable audit logging for all access to cardholder data",
                    RegulatoryReference = "PCI-DSS Requirement 10.1"
                });
            }

            // Requirement 10.2: Implement automated audit trails
            if (!context.Attributes.TryGetValue("AuditTrailAutomated", out var ataObj) || ataObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "PCI-10.2",
                    Description = "Automated audit trails required for cardholder data environment",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement automated audit trails for all system components",
                    RegulatoryReference = "PCI-DSS Requirement 10.2"
                });
            }

            // Requirement 10.3: Record required audit trail entries
            var requiredFields = new[] { "UserId", "EventType", "Timestamp", "SourceIp", "Success", "AffectedResource" };
            if (context.Attributes.TryGetValue("AuditFields", out var afObj) && afObj is IEnumerable<string> auditFields)
            {
                var missingFields = requiredFields.Where(f => !auditFields.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
                if (missingFields.Any())
                {
                    recommendations.Add($"Audit entries should include: {string.Join(", ", missingFields)}");
                }
            }

            // Requirement 10.5: Secure audit trails
            if (!context.Attributes.TryGetValue("AuditTrailsSecured", out var atsObj) || atsObj is not true)
            {
                recommendations.Add("Secure audit trails against modification");
            }

            // Requirement 10.7: Retain audit trail history
            if (context.Attributes.TryGetValue("AuditRetentionDays", out var ardObj) && ardObj is int retentionDays)
            {
                if (retentionDays < 365)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "PCI-10.7",
                        Description = $"Audit retention period ({retentionDays} days) below required 1 year",
                        Severity = ViolationSeverity.High,
                        Remediation = "Retain audit trail history for at least one year",
                        RegulatoryReference = "PCI-DSS Requirement 10.7"
                    });
                }
            }
        }

        private void CheckSecurityTesting(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            // Requirement 11.2: Vulnerability scans
            if (!context.Attributes.TryGetValue("VulnerabilityScansCompleted", out var vscObj) || vscObj is not true)
            {
                recommendations.Add("Perform quarterly internal and external vulnerability scans");
            }

            // Requirement 11.3: Penetration testing
            if (!context.Attributes.TryGetValue("PenetrationTestCompleted", out var ptcObj) || ptcObj is not true)
            {
                recommendations.Add("Perform annual penetration testing");
            }

            // Requirement 11.4: Intrusion detection
            if (!context.Attributes.TryGetValue("IntrusionDetectionEnabled", out var ideObj) || ideObj is not true)
            {
                recommendations.Add("Implement intrusion detection/prevention systems at CDE perimeter");
            }
        }

        private void CheckSecurityPolicy(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            // Requirement 12.1: Security policy
            if (!context.Attributes.TryGetValue("SecurityPolicyExists", out var speObj) || speObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "PCI-12.1",
                    Description = "Information security policy required",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Establish, publish, maintain, and disseminate a security policy",
                    RegulatoryReference = "PCI-DSS Requirement 12.1"
                });
            }

            // Requirement 12.6: Security awareness training
            if (!context.Attributes.TryGetValue("SecurityTrainingCompleted", out var stcObj) || stcObj is not true)
            {
                recommendations.Add("Implement security awareness program for personnel");
            }

            // Requirement 12.10: Incident response plan
            if (!context.Attributes.TryGetValue("IncidentResponsePlan", out var irpObj) || irpObj is not true)
            {
                recommendations.Add("Create and maintain incident response plan");
            }
        }
    }
}
