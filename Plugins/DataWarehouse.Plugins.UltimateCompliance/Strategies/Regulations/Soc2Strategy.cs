using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Regulations
{
    /// <summary>
    /// SOC 2 (Service Organization Control 2) compliance strategy.
    /// Validates operations against the five Trust Services Criteria (TSC).
    /// </summary>
    /// <remarks>
    /// Covers all five Trust Services Criteria:
    /// - Security (Common Criteria): Protection against unauthorized access
    /// - Availability: System availability for operation and use
    /// - Processing Integrity: System processing is complete, valid, accurate, timely, authorized
    /// - Confidentiality: Information designated as confidential is protected
    /// - Privacy: Personal information is collected, used, retained, disclosed, and disposed properly
    /// </remarks>
    public sealed class Soc2Strategy : ComplianceStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "soc2";

        /// <inheritdoc/>
        public override string StrategyName => "SOC 2 Type II Compliance";

        /// <inheritdoc/>
        public override string Framework => "SOC2";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
        IncrementCounter("soc2.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // CC: Common Criteria (Security)
            CheckSecurityCriteria(context, violations, recommendations);

            // A: Availability
            CheckAvailabilityCriteria(context, violations, recommendations);

            // PI: Processing Integrity
            CheckProcessingIntegrityCriteria(context, violations, recommendations);

            // C: Confidentiality
            CheckConfidentialityCriteria(context, violations, recommendations);

            // P: Privacy
            CheckPrivacyCriteria(context, violations, recommendations);

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
                    ["OperationType"] = context.OperationType,
                    ["DataClassification"] = context.DataClassification,
                    ["TrustCriteriaEvaluated"] = new[] { "Security", "Availability", "ProcessingIntegrity", "Confidentiality", "Privacy" }
                }
            });
        }

        #region CC: Common Criteria (Security)

        private void CheckSecurityCriteria(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            // CC1: Control Environment
            CheckControlEnvironment(context, violations, recommendations);

            // CC2: Communication and Information
            CheckCommunicationAndInformation(context, violations, recommendations);

            // CC3: Risk Assessment
            CheckRiskAssessment(context, violations, recommendations);

            // CC4: Monitoring Activities
            CheckMonitoringActivities(context, violations, recommendations);

            // CC5: Control Activities
            CheckControlActivities(context, violations, recommendations);

            // CC6: Logical and Physical Access Controls
            CheckAccessControls(context, violations, recommendations);

            // CC7: System Operations
            CheckSystemOperations(context, violations, recommendations);

            // CC8: Change Management
            CheckChangeManagement(context, violations, recommendations);

            // CC9: Risk Mitigation
            CheckRiskMitigation(context, violations, recommendations);
        }

        private void CheckControlEnvironment(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            // CC1.1: Demonstrate commitment to integrity and ethical values
            if (!context.Attributes.TryGetValue("EthicalPolicyExists", out var epeObj) || epeObj is not true)
            {
                recommendations.Add("CC1.1: Establish and communicate code of conduct and ethical policies");
            }

            // CC1.2: Board oversight
            if (!context.Attributes.TryGetValue("BoardOversightEnabled", out var boeObj) || boeObj is not true)
            {
                recommendations.Add("CC1.2: Ensure board/management oversight of control environment");
            }

            // CC1.3: Establish structure, authority, and responsibility
            if (!context.Attributes.TryGetValue("OrganizationalStructureDefined", out var osdObj) || osdObj is not true)
            {
                recommendations.Add("CC1.3: Define organizational structure and reporting lines");
            }
        }

        private void CheckCommunicationAndInformation(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            // CC2.1: Relevant quality information
            if (!context.Attributes.TryGetValue("InformationQualityControls", out var iqcObj) || iqcObj is not true)
            {
                recommendations.Add("CC2.1: Implement controls to ensure information quality");
            }

            // CC2.2: Internal communication
            if (!context.Attributes.TryGetValue("InternalCommunicationChannels", out var iccObj) || iccObj is not true)
            {
                recommendations.Add("CC2.2: Establish internal communication channels for control information");
            }

            // CC2.3: External communication
            if (context.OperationType.Contains("external", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("ExternalCommunicationControls", out var eccObj) || eccObj is not true)
                {
                    recommendations.Add("CC2.3: Implement controls for external communication");
                }
            }
        }

        private void CheckRiskAssessment(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            // CC3.1: Specify suitable objectives
            if (!context.Attributes.TryGetValue("SecurityObjectivesDefined", out var sodObj) || sodObj is not true)
            {
                recommendations.Add("CC3.1: Define and document security objectives");
            }

            // CC3.2: Identify and analyze risks
            if (!context.Attributes.TryGetValue("RiskAssessmentCompleted", out var racObj) || racObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "SOC2-CC3.2",
                    Description = "Risk assessment not completed for this operation",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Perform risk assessment to identify and analyze risks",
                    RegulatoryReference = "SOC 2 CC3.2"
                });
            }

            // CC3.3: Consider potential for fraud
            if (context.DataClassification.Contains("financial", StringComparison.OrdinalIgnoreCase) ||
                context.DataClassification.Contains("payment", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("FraudRiskAssessed", out var fraObj) || fraObj is not true)
                {
                    recommendations.Add("CC3.3: Assess fraud risk for financial operations");
                }
            }

            // CC3.4: Identify and assess changes
            if (context.OperationType.Contains("change", StringComparison.OrdinalIgnoreCase) ||
                context.OperationType.Contains("modify", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("ChangeRiskAssessed", out var craObj) || craObj is not true)
                {
                    recommendations.Add("CC3.4: Assess risks associated with changes");
                }
            }
        }

        private void CheckMonitoringActivities(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            // CC4.1: Select and develop ongoing and/or separate evaluations
            if (!context.Attributes.TryGetValue("ContinuousMonitoringEnabled", out var cmeObj) || cmeObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "SOC2-CC4.1",
                    Description = "Continuous monitoring not enabled",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Implement ongoing monitoring of control effectiveness",
                    RegulatoryReference = "SOC 2 CC4.1"
                });
            }

            // CC4.2: Evaluate and communicate deficiencies
            if (!context.Attributes.TryGetValue("DeficiencyReportingProcess", out var drpObj) || drpObj is not true)
            {
                recommendations.Add("CC4.2: Establish process for evaluating and reporting control deficiencies");
            }
        }

        private void CheckControlActivities(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            // CC5.1: Select and develop control activities
            if (!context.Attributes.TryGetValue("ControlActivitiesImplemented", out var caiObj) || caiObj is not true)
            {
                recommendations.Add("CC5.1: Implement control activities that mitigate risks");
            }

            // CC5.2: Select and develop general controls over technology
            if (!context.Attributes.TryGetValue("TechnologyControlsImplemented", out var tciObj) || tciObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "SOC2-CC5.2",
                    Description = "Technology controls not implemented",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement general IT controls over technology infrastructure",
                    RegulatoryReference = "SOC 2 CC5.2"
                });
            }

            // CC5.3: Deploy through policies and procedures
            if (!context.Attributes.TryGetValue("PoliciesAndProceduresDocumented", out var ppdObj) || ppdObj is not true)
            {
                recommendations.Add("CC5.3: Document and deploy policies and procedures");
            }
        }

        private void CheckAccessControls(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            // CC6.1: Logical access security software, infrastructure, architectures
            if (!context.Attributes.TryGetValue("LogicalAccessControls", out var lacObj) || lacObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "SOC2-CC6.1",
                    Description = "Logical access controls not implemented",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement logical access security for infrastructure and applications",
                    RegulatoryReference = "SOC 2 CC6.1"
                });
            }

            // CC6.2: Prior to issuing system credentials
            if (string.IsNullOrEmpty(context.UserId))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "SOC2-CC6.2",
                    Description = "User identification required for system access",
                    Severity = ViolationSeverity.High,
                    Remediation = "Register and authorize users before issuing credentials",
                    RegulatoryReference = "SOC 2 CC6.2"
                });
            }

            // CC6.3: Remove access when no longer required
            if (context.OperationType.Equals("termination", StringComparison.OrdinalIgnoreCase) ||
                context.OperationType.Equals("offboarding", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("AccessRevokedTimely", out var artObj) || artObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "SOC2-CC6.3",
                        Description = "Access not revoked timely upon termination",
                        Severity = ViolationSeverity.High,
                        Remediation = "Remove access promptly when no longer required",
                        RegulatoryReference = "SOC 2 CC6.3"
                    });
                }
            }

            // CC6.6: Restrict access through system boundaries
            if (!context.Attributes.TryGetValue("NetworkBoundaryControlsEnabled", out var nbceObj) || nbceObj is not true)
            {
                recommendations.Add("CC6.6: Implement controls at system boundaries (firewalls, segmentation)");
            }

            // CC6.7: Restrict transmission, movement, removal
            if (context.OperationType.Contains("transfer", StringComparison.OrdinalIgnoreCase) ||
                context.OperationType.Contains("export", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("DataTransferControlled", out var dtcObj) || dtcObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "SOC2-CC6.7",
                        Description = "Data transfer not controlled",
                        Severity = ViolationSeverity.High,
                        Remediation = "Implement controls for data transmission and movement",
                        RegulatoryReference = "SOC 2 CC6.7"
                    });
                }
            }

            // CC6.8: Prevent unauthorized software
            if (!context.Attributes.TryGetValue("SoftwareControlsEnabled", out var sceObj) || sceObj is not true)
            {
                recommendations.Add("CC6.8: Implement controls to prevent unauthorized software installation");
            }
        }

        private void CheckSystemOperations(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            // CC7.1: Detect and monitor security events
            if (!context.Attributes.TryGetValue("SecurityEventMonitoring", out var semObj) || semObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "SOC2-CC7.1",
                    Description = "Security event monitoring not enabled",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement security event detection and monitoring",
                    RegulatoryReference = "SOC 2 CC7.1"
                });
            }

            // CC7.2: Monitor system components for anomalies
            if (!context.Attributes.TryGetValue("AnomalyDetectionEnabled", out var adeObj) || adeObj is not true)
            {
                recommendations.Add("CC7.2: Implement anomaly detection for system components");
            }

            // CC7.3: Evaluate security events
            if (!context.Attributes.TryGetValue("SecurityEventEvaluation", out var seeObj) || seeObj is not true)
            {
                recommendations.Add("CC7.3: Establish process for evaluating security events");
            }

            // CC7.4: Respond to security incidents
            if (!context.Attributes.TryGetValue("IncidentResponsePlan", out var irpObj) || irpObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "SOC2-CC7.4",
                    Description = "Incident response plan not documented",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Develop and maintain incident response procedures",
                    RegulatoryReference = "SOC 2 CC7.4"
                });
            }

            // CC7.5: Identify and assess recovery activities
            if (!context.Attributes.TryGetValue("RecoveryPlanDefined", out var rpdObj) || rpdObj is not true)
            {
                recommendations.Add("CC7.5: Define recovery objectives and procedures");
            }
        }

        private void CheckChangeManagement(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            // CC8.1: Changes to infrastructure and software
            if (context.OperationType.Contains("deploy", StringComparison.OrdinalIgnoreCase) ||
                context.OperationType.Contains("release", StringComparison.OrdinalIgnoreCase) ||
                context.OperationType.Contains("change", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("ChangeApproved", out var caObj) || caObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "SOC2-CC8.1",
                        Description = "Change not approved through change management process",
                        Severity = ViolationSeverity.High,
                        Remediation = "Submit changes through formal change management process",
                        RegulatoryReference = "SOC 2 CC8.1"
                    });
                }

                if (!context.Attributes.TryGetValue("ChangeTested", out var ctObj) || ctObj is not true)
                {
                    recommendations.Add("CC8.1: Test changes before deployment to production");
                }
            }
        }

        private void CheckRiskMitigation(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            // CC9.1: Identify and manage vendor risks
            if (context.Attributes.TryGetValue("ThirdPartyInvolved", out var tpiObj) && tpiObj is true)
            {
                if (!context.Attributes.TryGetValue("VendorRiskAssessed", out var vraObj) || vraObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "SOC2-CC9.1",
                        Description = "Third-party/vendor risk not assessed",
                        Severity = ViolationSeverity.Medium,
                        Remediation = "Perform vendor risk assessment and due diligence",
                        RegulatoryReference = "SOC 2 CC9.1"
                    });
                }
            }

            // CC9.2: Implement risk mitigation activities
            if (!context.Attributes.TryGetValue("RiskMitigationInPlace", out var rmiObj) || rmiObj is not true)
            {
                recommendations.Add("CC9.2: Implement risk mitigation activities");
            }
        }

        #endregion

        #region A: Availability

        private void CheckAvailabilityCriteria(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            // A1.1: Availability commitment
            if (context.Attributes.TryGetValue("SLADefined", out var slaObj) && slaObj is false)
            {
                recommendations.Add("A1.1: Define and communicate availability commitments (SLA)");
            }

            // A1.2: Capacity management
            if (!context.Attributes.TryGetValue("CapacityMonitored", out var cmObj) || cmObj is not true)
            {
                recommendations.Add("A1.2: Monitor and manage system capacity");
            }

            // A1.3: Recovery procedures
            if (!context.Attributes.TryGetValue("DisasterRecoveryPlan", out var drpObj) || drpObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "SOC2-A1.3",
                    Description = "Disaster recovery procedures not documented",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Develop and test disaster recovery procedures",
                    RegulatoryReference = "SOC 2 A1.3"
                });
            }
        }

        #endregion

        #region PI: Processing Integrity

        private void CheckProcessingIntegrityCriteria(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            // PI1.1: Processing integrity objectives
            if (!context.Attributes.TryGetValue("ProcessingIntegrityControlsEnabled", out var piceObj) || piceObj is not true)
            {
                recommendations.Add("PI1.1: Define processing integrity objectives and controls");
            }

            // PI1.2: Complete, accurate, and timely processing
            if (context.OperationType.Contains("process", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("InputValidated", out var ivObj) || ivObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "SOC2-PI1.2",
                        Description = "Input validation not performed",
                        Severity = ViolationSeverity.High,
                        Remediation = "Implement input validation to ensure data accuracy",
                        RegulatoryReference = "SOC 2 PI1.2"
                    });
                }
            }

            // PI1.3: Detect and address errors
            if (!context.Attributes.TryGetValue("ErrorHandlingEnabled", out var eheObj) || eheObj is not true)
            {
                recommendations.Add("PI1.3: Implement error detection and handling mechanisms");
            }

            // PI1.4: Processing outputs
            if (!context.Attributes.TryGetValue("OutputValidated", out var ovObj) || ovObj is not true)
            {
                recommendations.Add("PI1.4: Validate processing outputs for completeness and accuracy");
            }

            // PI1.5: Store inputs and outputs
            if (!context.Attributes.TryGetValue("DataRetentionEnabled", out var dreObj) || dreObj is not true)
            {
                recommendations.Add("PI1.5: Retain inputs and outputs as required");
            }
        }

        #endregion

        #region C: Confidentiality

        private void CheckConfidentialityCriteria(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            // C1.1: Identify confidential information
            if (context.DataClassification.Contains("confidential", StringComparison.OrdinalIgnoreCase) ||
                context.DataClassification.Contains("sensitive", StringComparison.OrdinalIgnoreCase) ||
                context.DataClassification.Contains("restricted", StringComparison.OrdinalIgnoreCase))
            {
                // C1.2: Protect confidential information
                if (!context.Attributes.TryGetValue("ConfidentialityControlsEnabled", out var cceObj) || cceObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "SOC2-C1.2",
                        Description = "Confidentiality controls not enabled for sensitive data",
                        Severity = ViolationSeverity.High,
                        Remediation = "Implement controls to protect confidential information",
                        RegulatoryReference = "SOC 2 C1.2"
                    });
                }

                // Check encryption
                if (!context.Attributes.TryGetValue("Encrypted", out var encObj) || encObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "SOC2-C1.2-ENC",
                        Description = "Confidential data not encrypted",
                        Severity = ViolationSeverity.High,
                        Remediation = "Encrypt confidential data at rest and in transit",
                        RegulatoryReference = "SOC 2 C1.2"
                    });
                }
            }

            // C1.3: Disposal of confidential information
            if (context.OperationType.Equals("delete", StringComparison.OrdinalIgnoreCase) ||
                context.OperationType.Equals("dispose", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("SecureDisposal", out var sdObj) || sdObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "SOC2-C1.3",
                        Description = "Secure disposal not confirmed for confidential data",
                        Severity = ViolationSeverity.High,
                        Remediation = "Use secure disposal methods (cryptographic erasure, physical destruction)",
                        RegulatoryReference = "SOC 2 C1.3"
                    });
                }
            }
        }

        #endregion

        #region P: Privacy

        private void CheckPrivacyCriteria(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            // Only apply privacy criteria if personal information is involved
            bool containsPersonalInfo = context.DataClassification.Contains("personal", StringComparison.OrdinalIgnoreCase) ||
                                        context.DataClassification.Contains("pii", StringComparison.OrdinalIgnoreCase) ||
                                        context.DataSubjectCategories.Any();

            if (!containsPersonalInfo)
                return;

            // P1.1: Privacy notice
            if (!context.Attributes.TryGetValue("PrivacyNoticeProvided", out var pnpObj) || pnpObj is not true)
            {
                recommendations.Add("P1.1: Provide privacy notice to data subjects");
            }

            // P2.1: Consent/choice
            if (!context.Attributes.TryGetValue("ConsentObtained", out var coObj) || coObj is not true)
            {
                if (!context.Attributes.TryGetValue("LawfulBasis", out var lbObj) || lbObj is null)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "SOC2-P2.1",
                        Description = "Consent or lawful basis not documented for personal data processing",
                        Severity = ViolationSeverity.High,
                        Remediation = "Obtain consent or document lawful basis for processing",
                        RegulatoryReference = "SOC 2 P2.1"
                    });
                }
            }

            // P3.1: Collection limited to stated purposes
            if (!context.ProcessingPurposes.Any())
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "SOC2-P3.1",
                    Description = "Processing purpose not specified for personal data",
                    Severity = ViolationSeverity.High,
                    Remediation = "Specify and document processing purposes",
                    RegulatoryReference = "SOC 2 P3.1"
                });
            }

            // P4.1: Use limitation
            if (context.Attributes.TryGetValue("UseBeyondPurpose", out var ubpObj) && ubpObj is true)
            {
                if (!context.Attributes.TryGetValue("AdditionalConsentObtained", out var acoObj) || acoObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "SOC2-P4.1",
                        Description = "Personal data used beyond original purpose without consent",
                        Severity = ViolationSeverity.High,
                        Remediation = "Obtain consent for use beyond original purpose",
                        RegulatoryReference = "SOC 2 P4.1"
                    });
                }
            }

            // P5.1: Retention and disposal
            if (!context.Attributes.TryGetValue("RetentionPeriodDefined", out var rpdObj) || rpdObj is not true)
            {
                recommendations.Add("P5.1: Define retention period for personal data");
            }

            // P6.1: Access rights
            if (context.OperationType.Equals("access-request", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("IdentityVerified", out var ivObj) || ivObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "SOC2-P6.1",
                        Description = "Identity not verified before providing access to personal data",
                        Severity = ViolationSeverity.High,
                        Remediation = "Verify identity before fulfilling access requests",
                        RegulatoryReference = "SOC 2 P6.1"
                    });
                }
            }

            // P6.7: Correction rights
            if (context.OperationType.Equals("correction-request", StringComparison.OrdinalIgnoreCase))
            {
                recommendations.Add("P6.7: Process correction requests and update inaccurate data");
            }

            // P7.1: Disclosure to third parties
            if (context.Attributes.TryGetValue("ThirdPartyDisclosure", out var tpdObj) && tpdObj is true)
            {
                if (!context.Attributes.TryGetValue("DisclosureAuthorized", out var daObj) || daObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "SOC2-P7.1",
                        Description = "Third-party disclosure not authorized",
                        Severity = ViolationSeverity.High,
                        Remediation = "Obtain authorization before disclosing personal data to third parties",
                        RegulatoryReference = "SOC 2 P7.1"
                    });
                }
            }

            // P8.1: Quality of personal information
            if (!context.Attributes.TryGetValue("DataQualityVerified", out var dqvObj) || dqvObj is not true)
            {
                recommendations.Add("P8.1: Verify accuracy and completeness of personal information");
            }
        }

        #endregion
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("soc2.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("soc2.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
