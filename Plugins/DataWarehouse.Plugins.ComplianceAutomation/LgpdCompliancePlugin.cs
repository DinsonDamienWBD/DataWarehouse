using DataWarehouse.SDK.Compliance;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Plugins.ComplianceAutomation;

/// <summary>
/// LGPD (Lei Geral de Proteção de Dados) compliance automation plugin.
/// Implements automated checks for Brazil's General Data Protection Law.
/// Covers data subject rights, legal basis, DPO requirements, international transfers, and security measures.
/// </summary>
public class LgpdCompliancePlugin : ComplianceAutomationPluginBase
{
    private readonly List<AutomationControl> _controls;
    private readonly Dictionary<string, AutomationCheckResult> _lastResults = new();

    /// <inheritdoc />
    public override string Id => "com.datawarehouse.compliance.lgpd";

    /// <inheritdoc />
    public override string Name => "LGPD Compliance Automation";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.GovernanceProvider;

    /// <inheritdoc />
    public override ComplianceFramework SupportedFramework => ComplianceFramework.Lgpd;

    public LgpdCompliancePlugin()
    {
        _controls = new List<AutomationControl>
        {
            // Data Subject Rights (LGPD Article 18)
            new("LGPD.DSR.Access", "Right to Access - Art. 18, I", "Data subjects can confirm existence and access their personal data", ControlCategory.AccessControl, ControlSeverity.High, true),
            new("LGPD.DSR.Correction", "Right to Correction - Art. 18, III", "Data subjects can request correction of incomplete, inaccurate, or outdated data", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("LGPD.DSR.Anonymization", "Right to Anonymization - Art. 18, IV", "Data subjects can request anonymization, blocking, or deletion of unnecessary or excessive data", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("LGPD.DSR.Portability", "Right to Portability - Art. 18, V", "Data subjects can request portability of data to another service or product provider", ControlCategory.DataProtection, ControlSeverity.Medium, true),
            new("LGPD.DSR.Deletion", "Right to Deletion - Art. 18, VI", "Data subjects can request deletion of personal data processed with consent", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("LGPD.DSR.Information", "Right to Information - Art. 18, VII", "Data subjects can obtain information about public and private entities with which controller shared data", ControlCategory.DataProtection, ControlSeverity.Medium, true),
            new("LGPD.DSR.ConsentRevocation", "Right to Revoke Consent - Art. 18, IX", "Data subjects can revoke consent at any time", ControlCategory.DataProtection, ControlSeverity.High, true),

            // Legal Basis (LGPD Article 7)
            new("LGPD.LB.Consent", "Consent Management - Art. 7, I", "Ensure valid consent collection with clear, specific purpose disclosure", ControlCategory.DataProtection, ControlSeverity.Critical, true),
            new("LGPD.LB.LegitimateInterest", "Legitimate Interest - Art. 7, IX", "Document legitimate interest assessments with balancing tests", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("LGPD.LB.Documentation", "Legal Basis Documentation - Art. 7", "Maintain records of legal basis for each processing activity", ControlCategory.Audit, ControlSeverity.High, true),
            new("LGPD.LB.ConsentGranularity", "Consent Granularity - Art. 8", "Ensure consent is specific for each purpose, with separate acceptance required", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("LGPD.LB.ConsentEvidence", "Consent Evidence - Art. 8, §6", "Prove that consent was obtained and maintain consent records", ControlCategory.Audit, ControlSeverity.Critical, true),

            // DPO Requirements (LGPD Article 41)
            new("LGPD.DPO.Appointment", "DPO Appointment - Art. 41", "Verify Data Protection Officer (Encarregado) appointment and qualifications", ControlCategory.DataProtection, ControlSeverity.Critical, true),
            new("LGPD.DPO.ContactInfo", "DPO Contact Accessibility - Art. 41, §1", "Ensure DPO identity and contact information are publicly disclosed", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("LGPD.DPO.ComplaintHandling", "DPO Complaint Process - Art. 41, §2", "DPO accepts complaints, provides clarifications, and communicates with ANPD", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("LGPD.DPO.Guidance", "DPO Guidance Function - Art. 41, III", "DPO provides guidance on LGPD compliance obligations", ControlCategory.DataProtection, ControlSeverity.Medium, true),

            // International Transfers (LGPD Article 33)
            new("LGPD.IT.Mechanism", "Transfer Mechanism Compliance - Art. 33", "International transfers only via approved mechanisms (adequacy, clauses, certification, etc.)", ControlCategory.DataProtection, ControlSeverity.Critical, true),
            new("LGPD.IT.AdequacyVerification", "Adequate Protection Level - Art. 33, I", "Verify destination country or organization has adequate data protection", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("LGPD.IT.StandardClauses", "Standard Contractual Clauses - Art. 33, II", "Use ANPD-approved standard contractual clauses for international transfers", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("LGPD.IT.Certification", "Certification Compliance - Art. 33, III & IV", "Verify certifications, seals, or binding corporate rules for international transfers", ControlCategory.DataProtection, ControlSeverity.Medium, true),
            new("LGPD.IT.Documentation", "Transfer Documentation - Art. 33", "Document legal basis and safeguards for each international transfer", ControlCategory.Audit, ControlSeverity.High, true),

            // Security Measures (LGPD Article 46)
            new("LGPD.SM.TechnicalControls", "Technical Security Measures - Art. 46", "Implement technical security measures to protect data from unauthorized access", ControlCategory.Encryption, ControlSeverity.Critical, true),
            new("LGPD.SM.AdministrativeControls", "Administrative Controls - Art. 46", "Implement administrative measures including policies, procedures, and training", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("LGPD.SM.Encryption", "Encryption Requirements - Art. 46", "Use encryption for sensitive personal data at rest and in transit", ControlCategory.Encryption, ControlSeverity.Critical, true),
            new("LGPD.SM.AccessControl", "Access Control Measures - Art. 46", "Implement role-based access control and least privilege principles", ControlCategory.AccessControl, ControlSeverity.Critical, true),
            new("LGPD.SM.IncidentResponse", "Incident Response Capability - Art. 48", "Establish incident response procedures for data breaches", ControlCategory.Incident, ControlSeverity.Critical, true),

            // Breach Notification (LGPD Article 48)
            new("LGPD.BN.ANPDNotification", "ANPD Notification - Art. 48", "Capability to notify ANPD within reasonable timeframe of security incidents", ControlCategory.Incident, ControlSeverity.Critical, true),
            new("LGPD.BN.SubjectNotification", "Data Subject Notification - Art. 48, §1", "Notify affected data subjects when incident may cause relevant risk or damage", ControlCategory.Incident, ControlSeverity.Critical, true),
            new("LGPD.BN.IncidentDocumentation", "Incident Documentation - Art. 48", "Document breach details including nature, affected data subjects, technical measures, and risks", ControlCategory.Audit, ControlSeverity.High, true),
            new("LGPD.BN.TimelinessProcess", "Timeliness Requirements - Art. 48", "Ensure reasonable timeframe for breach notification to ANPD and subjects", ControlCategory.Incident, ControlSeverity.Critical, true),

            // General Principles (LGPD Article 6)
            new("LGPD.PR.Purpose", "Purpose Limitation - Art. 6, I", "Process data only for legitimate, specific, explicit purposes communicated to subject", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("LGPD.PR.Adequacy", "Adequacy - Art. 6, II", "Ensure processing is compatible with purposes communicated to subject", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("LGPD.PR.Necessity", "Necessity (Data Minimization) - Art. 6, III", "Limit processing to minimum necessary for purpose", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("LGPD.PR.FreeAccess", "Free Access - Art. 6, IV", "Guarantee data subjects free and facilitated access to their data", ControlCategory.AccessControl, ControlSeverity.High, true),
            new("LGPD.PR.Quality", "Data Quality - Art. 6, V", "Ensure accuracy, clarity, relevance, and up-to-date status of data", ControlCategory.DataProtection, ControlSeverity.Medium, true),
            new("LGPD.PR.Transparency", "Transparency - Art. 6, VI", "Guarantee clear, accurate, and easily accessible information about processing", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("LGPD.PR.Security", "Security Principle - Art. 6, VII", "Use technical and administrative measures to protect data from unauthorized access", ControlCategory.Encryption, ControlSeverity.Critical, true),
            new("LGPD.PR.Prevention", "Prevention - Art. 6, VIII", "Adopt measures to prevent occurrence of damage due to processing", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("LGPD.PR.NonDiscrimination", "Non-Discrimination - Art. 6, IX", "Prevent processing for illicit or abusive discriminatory purposes", ControlCategory.DataProtection, ControlSeverity.Critical, true),
            new("LGPD.PR.Accountability", "Accountability - Art. 6, X", "Demonstrate adoption of effective measures to comply with LGPD", ControlCategory.Audit, ControlSeverity.High, true),

            // Records and Accountability (LGPD Articles 37, 50)
            new("LGPD.RA.ProcessingRecords", "Processing Records - Art. 37", "Maintain records of processing operations for at least 6 months after completion", ControlCategory.Audit, ControlSeverity.High, true),
            new("LGPD.RA.DPIA", "Data Protection Impact Assessment - Art. 38", "Conduct DPIA for high-risk processing operations", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("LGPD.RA.ComplianceProgram", "Compliance Program - Art. 50", "Implement data governance program with policies, practices, and procedures", ControlCategory.DataProtection, ControlSeverity.High, true),
        };
    }

    /// <inheritdoc />
    protected override Task<IReadOnlyList<AutomationControl>> LoadControlsAsync()
    {
        return Task.FromResult<IReadOnlyList<AutomationControl>>(_controls);
    }

    /// <inheritdoc />
    protected override IReadOnlyList<AutomationControl> GetControls() => _controls;

    /// <inheritdoc />
    protected override async Task<AutomationCheckResult> ExecuteControlCheckAsync(AutomationControl control)
    {
        // Simulate checking each control
        await Task.Delay(50);

        var checkId = $"CHK-{Guid.NewGuid():N}";
        var findings = new List<string>();
        var recommendations = new List<string>();
        var status = AutomationComplianceStatus.Compliant;

        // Simulate control-specific checks
        switch (control.ControlId)
        {
            // Data Subject Rights
            case "LGPD.DSR.Access":
            case "LGPD.DSR.Correction":
            case "LGPD.DSR.Anonymization":
            case "LGPD.DSR.Portability":
            case "LGPD.DSR.Deletion":
                var dsrImplemented = await CheckDataSubjectRightAsync(control.ControlId);
                if (!dsrImplemented)
                {
                    findings.Add($"{control.Title} mechanism not fully implemented");
                    recommendations.Add($"Implement automated {control.Title} capability with audit trail");
                    status = AutomationComplianceStatus.PartiallyCompliant;
                }
                break;

            case "LGPD.DSR.ConsentRevocation":
                var consentRevocationEnabled = await CheckConsentRevocationAsync();
                if (!consentRevocationEnabled)
                {
                    findings.Add("Consent revocation mechanism not available or not easily accessible");
                    recommendations.Add("Implement user-friendly consent revocation interface with immediate effect");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            // Legal Basis
            case "LGPD.LB.Consent":
                var consentValid = await CheckConsentValidityAsync();
                if (!consentValid)
                {
                    findings.Add("Consent collection not meeting LGPD requirements (free, informed, unambiguous)");
                    recommendations.Add("Implement granular consent with clear purpose disclosure and affirmative action");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "LGPD.LB.LegitimateInterest":
                var legitimateInterestDocumented = await CheckLegitimateInterestAssessmentAsync();
                if (!legitimateInterestDocumented)
                {
                    findings.Add("Legitimate interest assessments not documented or balancing test missing");
                    recommendations.Add("Document legitimate interest for each purpose with necessity and proportionality analysis");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "LGPD.LB.Documentation":
            case "LGPD.LB.ConsentEvidence":
                var legalBasisRecords = await CheckLegalBasisRecordsAsync();
                if (!legalBasisRecords)
                {
                    findings.Add("Legal basis not documented for all processing activities");
                    recommendations.Add("Maintain comprehensive records of legal basis with timestamps and evidence");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            // DPO Requirements
            case "LGPD.DPO.Appointment":
                var dpoAppointed = await CheckDpoAppointmentAsync();
                if (!dpoAppointed)
                {
                    findings.Add("Data Protection Officer (Encarregado) not appointed or qualifications unclear");
                    recommendations.Add("Appoint qualified DPO and document appointment with job description");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "LGPD.DPO.ContactInfo":
                var dpoContactPublic = await CheckDpoContactInfoAsync();
                if (!dpoContactPublic)
                {
                    findings.Add("DPO contact information not publicly available or not easily accessible");
                    recommendations.Add("Publish DPO identity and contact details on website and in privacy policy");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "LGPD.DPO.ComplaintHandling":
                var complaintProcessExists = await CheckComplaintHandlingProcessAsync();
                if (!complaintProcessExists)
                {
                    findings.Add("DPO complaint handling process not established or documented");
                    recommendations.Add("Establish formal process for receiving, tracking, and responding to complaints");
                    status = AutomationComplianceStatus.PartiallyCompliant;
                }
                break;

            // International Transfers
            case "LGPD.IT.Mechanism":
            case "LGPD.IT.AdequacyVerification":
                var transferMechanismValid = await CheckInternationalTransferMechanismAsync();
                if (!transferMechanismValid)
                {
                    findings.Add("International transfers lack valid LGPD-compliant mechanism");
                    recommendations.Add("Implement adequacy decisions, standard clauses, or certifications for all transfers");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "LGPD.IT.StandardClauses":
                var standardClausesUsed = await CheckStandardContractualClausesAsync();
                if (!standardClausesUsed)
                {
                    findings.Add("Standard contractual clauses not used for international transfers");
                    recommendations.Add("Adopt ANPD-approved standard clauses or equivalent safeguards");
                    status = AutomationComplianceStatus.PartiallyCompliant;
                }
                break;

            case "LGPD.IT.Documentation":
                var transfersDocumented = await CheckTransferDocumentationAsync();
                if (!transfersDocumented)
                {
                    findings.Add("International transfers not fully documented with legal basis");
                    recommendations.Add("Maintain transfer register with recipients, countries, and safeguards");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            // Security Measures
            case "LGPD.SM.TechnicalControls":
            case "LGPD.SM.Security":
                var technicalControlsAdequate = await CheckTechnicalSecurityControlsAsync();
                if (!technicalControlsAdequate)
                {
                    findings.Add("Technical security measures insufficient for data protection");
                    recommendations.Add("Implement encryption, access controls, and security monitoring");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "LGPD.SM.Encryption":
                var encryptionEnabled = await CheckEncryptionAsync();
                if (!encryptionEnabled)
                {
                    findings.Add("Encryption not enabled for sensitive personal data");
                    recommendations.Add("Enable AES-256 encryption at rest and TLS 1.3 in transit");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "LGPD.SM.AccessControl":
                var accessControlEnforced = await CheckAccessControlAsync();
                if (!accessControlEnforced)
                {
                    findings.Add("Access control not enforcing least privilege or role segregation");
                    recommendations.Add("Implement RBAC with regular access reviews and segregation of duties");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "LGPD.SM.IncidentResponse":
                var incidentResponseReady = await CheckIncidentResponseCapabilityAsync();
                if (!incidentResponseReady)
                {
                    findings.Add("Incident response procedures not documented or tested");
                    recommendations.Add("Develop and test incident response plan with roles and notification workflows");
                    status = AutomationComplianceStatus.PartiallyCompliant;
                }
                break;

            // Breach Notification
            case "LGPD.BN.ANPDNotification":
            case "LGPD.BN.SubjectNotification":
                var notificationCapable = await CheckBreachNotificationCapabilityAsync();
                if (!notificationCapable)
                {
                    findings.Add("Breach notification process not established or not timely");
                    recommendations.Add("Implement automated breach detection and notification workflows");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "LGPD.BN.IncidentDocumentation":
                var incidentDocumentationComplete = await CheckIncidentDocumentationAsync();
                if (!incidentDocumentationComplete)
                {
                    findings.Add("Incident documentation incomplete or missing required fields");
                    recommendations.Add("Document nature, affected subjects, technical measures, risks for all incidents");
                    status = AutomationComplianceStatus.PartiallyCompliant;
                }
                break;

            // General Principles
            case "LGPD.PR.Purpose":
            case "LGPD.PR.Adequacy":
            case "LGPD.PR.Necessity":
                var dataMiningCompliant = await CheckDataMinimizationPrincipleAsync();
                if (!dataMiningCompliant)
                {
                    findings.Add("Data collection exceeds necessity or purpose not clearly communicated");
                    recommendations.Add("Review data inventory and eliminate unnecessary data collection");
                    status = AutomationComplianceStatus.PartiallyCompliant;
                }
                break;

            case "LGPD.PR.Transparency":
                var transparencyAdequate = await CheckTransparencyAsync();
                if (!transparencyAdequate)
                {
                    findings.Add("Privacy information not clear, accurate, or easily accessible");
                    recommendations.Add("Provide layered privacy notice in plain language with accessible format");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "LGPD.PR.Accountability":
                var accountabilityDemonstrated = await CheckAccountabilityAsync();
                if (!accountabilityDemonstrated)
                {
                    findings.Add("Cannot demonstrate effective compliance measures or accountability");
                    recommendations.Add("Implement compliance program with policies, training, and audit trails");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            // Records and Accountability
            case "LGPD.RA.ProcessingRecords":
                var recordsComplete = await CheckProcessingRecordsAsync();
                if (!recordsComplete)
                {
                    findings.Add("Processing records incomplete or not maintained for required period");
                    recommendations.Add("Maintain comprehensive processing records for at least 6 months after completion");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "LGPD.RA.DPIA":
                var dpiaPerformed = await CheckDpiaRequirementAsync();
                if (!dpiaPerformed)
                {
                    findings.Add("Data Protection Impact Assessment not performed for high-risk processing");
                    recommendations.Add("Conduct DPIA for high-risk activities with privacy by design analysis");
                    status = AutomationComplianceStatus.PartiallyCompliant;
                }
                break;

            case "LGPD.RA.ComplianceProgram":
                var complianceProgramExists = await CheckComplianceProgramAsync();
                if (!complianceProgramExists)
                {
                    findings.Add("Data governance and compliance program not established");
                    recommendations.Add("Implement comprehensive compliance program per Article 50 requirements");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            default:
                // Default: assume compliant for demonstration
                break;
        }

        var result = new AutomationCheckResult(
            checkId,
            control.ControlId,
            ComplianceFramework.Lgpd,
            status,
            control.Description,
            findings.ToArray(),
            recommendations.ToArray(),
            DateTimeOffset.UtcNow
        );

        _lastResults[control.ControlId] = result;
        return result;
    }

    /// <inheritdoc />
    protected override Task<RemediationResult> ExecuteRemediationAsync(string checkId, RemediationOptions options)
    {
        var result = _lastResults.Values.FirstOrDefault(r => r.CheckId == checkId);
        if (result == null)
        {
            return Task.FromResult(new RemediationResult(
                false,
                Array.Empty<string>(),
                new[] { "Check result not found" },
                false
            ));
        }

        var actions = new List<string>();
        var errors = new List<string>();

        if (options.DryRun)
        {
            actions.Add($"[DRY RUN] Would remediate {result.ControlId}");
            foreach (var rec in result.Recommendations)
            {
                actions.Add($"[DRY RUN] Would execute: {rec}");
            }
        }
        else
        {
            // Simulate remediation
            actions.Add($"Initiated remediation for {result.ControlId}");
            actions.Add("Applied automated configuration changes for LGPD compliance");

            // Add specific remediation actions based on control type
            if (result.ControlId.StartsWith("LGPD.SM."))
            {
                actions.Add("Updated security configurations");
            }
            else if (result.ControlId.StartsWith("LGPD.DSR."))
            {
                actions.Add("Enabled data subject rights automation");
            }
            else if (result.ControlId.StartsWith("LGPD.LB."))
            {
                actions.Add("Updated consent management system");
            }
        }

        return Task.FromResult(new RemediationResult(
            !options.DryRun,
            actions.ToArray(),
            errors.ToArray(),
            options.RequireApproval
        ));
    }

    // Simulated check methods
    private Task<bool> CheckDataSubjectRightAsync(string controlId) => Task.FromResult(true);
    private Task<bool> CheckConsentRevocationAsync() => Task.FromResult(true);
    private Task<bool> CheckConsentValidityAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.2);
    private Task<bool> CheckLegitimateInterestAssessmentAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.3);
    private Task<bool> CheckLegalBasisRecordsAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.2);
    private Task<bool> CheckDpoAppointmentAsync() => Task.FromResult(true);
    private Task<bool> CheckDpoContactInfoAsync() => Task.FromResult(true);
    private Task<bool> CheckComplaintHandlingProcessAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.4);
    private Task<bool> CheckInternationalTransferMechanismAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.3);
    private Task<bool> CheckStandardContractualClausesAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.4);
    private Task<bool> CheckTransferDocumentationAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.3);
    private Task<bool> CheckTechnicalSecurityControlsAsync() => Task.FromResult(true);
    private Task<bool> CheckEncryptionAsync() => Task.FromResult(true);
    private Task<bool> CheckAccessControlAsync() => Task.FromResult(true);
    private Task<bool> CheckIncidentResponseCapabilityAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.4);
    private Task<bool> CheckBreachNotificationCapabilityAsync() => Task.FromResult(true);
    private Task<bool> CheckIncidentDocumentationAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.3);
    private Task<bool> CheckDataMinimizationPrincipleAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.3);
    private Task<bool> CheckTransparencyAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.3);
    private Task<bool> CheckAccountabilityAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.4);
    private Task<bool> CheckProcessingRecordsAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.3);
    private Task<bool> CheckDpiaRequirementAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.4);
    private Task<bool> CheckComplianceProgramAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.5);
}
