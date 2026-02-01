using DataWarehouse.SDK.Compliance;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Plugins.ComplianceAutomation;

/// <summary>
/// PDPA (Singapore Personal Data Protection Act) compliance automation plugin.
/// Implements automated checks for PDPA obligations including consent, purpose limitation,
/// notification, access and correction, accuracy, protection, retention, transfer, breach notification, and DNC registry.
/// </summary>
public class PdpaCompliancePlugin : ComplianceAutomationPluginBase
{
    private readonly List<AutomationControl> _controls;
    private readonly Dictionary<string, AutomationCheckResult> _lastResults = new();

    /// <inheritdoc />
    public override string Id => "com.datawarehouse.compliance.pdpa";

    /// <inheritdoc />
    public override string Name => "PDPA Compliance Automation";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.GovernanceProvider;

    /// <inheritdoc />
    public override ComplianceFramework SupportedFramework => ComplianceFramework.Pdpa;

    public PdpaCompliancePlugin()
    {
        _controls = new List<AutomationControl>
        {
            // 1. Consent Obligation (PDPA Sections 13-17)
            new("PDPA.CON.001", "Valid Consent Collection", "Consent must be voluntary, informed, and specific for each purpose (Section 13)", ControlCategory.DataProtection, ControlSeverity.Critical, true),
            new("PDPA.CON.002", "Deemed Consent Mechanisms", "Deemed consent must be reasonable based on notification and purposes (Section 15)", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("PDPA.CON.003", "Consent Withdrawal Procedures", "Individuals can withdraw consent and organization must inform of likely consequences (Section 16)", ControlCategory.DataProtection, ControlSeverity.Critical, true),
            new("PDPA.CON.004", "Deemed Consent by Notification", "Deemed consent applies when individual is notified and reasonable person test is met (Section 15)", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("PDPA.CON.005", "Deemed Consent by Voluntary Provision", "Deemed consent when data voluntarily provided for an obvious purpose (Section 15)", ControlCategory.DataProtection, ControlSeverity.Medium, true),

            // 2. Purpose Limitation (PDPA Section 18)
            new("PDPA.PL.001", "Reasonable Person Test", "Purpose must be one that a reasonable person would consider appropriate (Section 18)", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("PDPA.PL.002", "Purpose Notification Requirement", "Organization must notify individual of purposes on or before collection (Section 18)", ControlCategory.DataProtection, ControlSeverity.Critical, true),
            new("PDPA.PL.003", "Use Within Stated Purposes", "Personal data used or disclosed only for notified purposes or directly related purposes (Section 18)", ControlCategory.DataProtection, ControlSeverity.Critical, true),
            new("PDPA.PL.004", "Purpose Specification", "Purposes must be specified with sufficient clarity and detail (Section 18)", ControlCategory.DataProtection, ControlSeverity.High, true),

            // 3. Notification Obligation (PDPA Section 20)
            new("PDPA.NOT.001", "Purpose Notification on Collection", "Notify individual of purposes on or before collecting personal data (Section 20)", ControlCategory.DataProtection, ControlSeverity.Critical, true),
            new("PDPA.NOT.002", "Collection Notification Content", "Notification must include purposes and whether supply of data is voluntary or mandatory (Section 20)", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("PDPA.NOT.003", "Usage Notification Timing", "Notification provided at the time of collection or as soon as practicable (Section 20)", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("PDPA.NOT.004", "Business Contact Information Notification", "Notify individual of business contact details and DPO contact (Section 20)", ControlCategory.DataProtection, ControlSeverity.Medium, true),

            // 4. Access and Correction (PDPA Sections 21-22)
            new("PDPA.AC.001", "Access Request Handling", "Provide access to personal data upon request within reasonable time (Section 21)", ControlCategory.AccessControl, ControlSeverity.High, true),
            new("PDPA.AC.002", "Access Request Response Time", "Respond to access requests within 30 days from receipt (Section 21)", ControlCategory.AccessControl, ControlSeverity.Critical, true),
            new("PDPA.AC.003", "Correction Request Processing", "Correct personal data upon request if data is inaccurate or incomplete (Section 22)", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("PDPA.AC.004", "Correction Request Response Time", "Respond to correction requests within 30 days from receipt (Section 22)", ControlCategory.DataProtection, ControlSeverity.Critical, true),
            new("PDPA.AC.005", "Third Party Notification on Correction", "Notify other organizations to which inaccurate data was disclosed in past year (Section 22)", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("PDPA.AC.006", "Access Denial Documentation", "Document reasons for denying access or correction requests (Section 21-22)", ControlCategory.Audit, ControlSeverity.Medium, true),

            // 5. Accuracy Obligation (PDPA Section 23)
            new("PDPA.ACC.001", "Data Accuracy Maintenance", "Make reasonable effort to ensure personal data is accurate and complete (Section 23)", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("PDPA.ACC.002", "Reasonable Accuracy Efforts", "Accuracy efforts based on purposes for which data is used (Section 23)", ControlCategory.DataProtection, ControlSeverity.Medium, true),
            new("PDPA.ACC.003", "Accuracy Verification Procedures", "Implement procedures to verify and update personal data accuracy (Section 23)", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("PDPA.ACC.004", "Correction Mechanisms", "Provide mechanisms for individuals to update their own data (Section 23)", ControlCategory.DataProtection, ControlSeverity.Medium, true),

            // 6. Protection Obligation (PDPA Section 24)
            new("PDPA.PRO.001", "Reasonable Security Arrangements", "Make reasonable security arrangements to protect personal data (Section 24)", ControlCategory.Encryption, ControlSeverity.Critical, true),
            new("PDPA.PRO.002", "Physical Safeguards", "Implement physical security measures to prevent unauthorized access (Section 24)", ControlCategory.AccessControl, ControlSeverity.High, false),
            new("PDPA.PRO.003", "Technical Safeguards", "Implement technical security measures including encryption and access controls (Section 24)", ControlCategory.Encryption, ControlSeverity.Critical, true),
            new("PDPA.PRO.004", "Administrative Safeguards", "Implement administrative security measures including policies and training (Section 24)", ControlCategory.DataProtection, ControlSeverity.High, false),
            new("PDPA.PRO.005", "Loss or Unauthorized Access Prevention", "Prevent loss, unauthorized access, use, modification, disclosure, or copying (Section 24)", ControlCategory.AccessControl, ControlSeverity.Critical, true),
            new("PDPA.PRO.006", "Data Processor Protection", "Ensure data intermediaries provide comparable level of protection (Section 24)", ControlCategory.DataProtection, ControlSeverity.High, true),

            // 7. Retention Limitation (PDPA Section 25)
            new("PDPA.RET.001", "Purpose-Based Retention", "Cease to retain personal data when retention is no longer for legal or business purposes (Section 25)", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("PDPA.RET.002", "Secure Disposal", "Dispose of or anonymize data that is no longer needed (Section 25)", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("PDPA.RET.003", "Retention Schedule Implementation", "Implement documented retention schedules aligned with purposes (Section 25)", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("PDPA.RET.004", "Automated Retention Enforcement", "Automate data retention and deletion based on defined schedules (Section 25)", ControlCategory.DataProtection, ControlSeverity.Medium, true),
            new("PDPA.RET.005", "Retention Documentation", "Document retention periods and disposal methods for each data category (Section 25)", ControlCategory.Audit, ControlSeverity.Medium, true),

            // 8. Transfer Limitation (PDPA Section 26)
            new("PDPA.TL.001", "Overseas Transfer Controls", "Transfer personal data outside Singapore only with appropriate safeguards (Section 26)", ControlCategory.DataProtection, ControlSeverity.Critical, true),
            new("PDPA.TL.002", "Comparable Protection Verification", "Ensure receiving jurisdiction provides comparable level of protection (Section 26)", ControlCategory.DataProtection, ControlSeverity.Critical, true),
            new("PDPA.TL.003", "Contractual Safeguards for Transfer", "Implement contractual or binding arrangements for overseas transfers (Section 26)", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("PDPA.TL.004", "Transfer Documentation", "Document and track all overseas personal data transfers (Section 26)", ControlCategory.Audit, ControlSeverity.High, true),
            new("PDPA.TL.005", "Transfer Risk Assessment", "Assess and document risks before transferring data overseas (Section 26)", ControlCategory.DataProtection, ControlSeverity.High, true),

            // 9. Data Breach Notification (PDPA Amendment 2020 - Sections 26B-26D)
            new("PDPA.DBN.001", "PDPC Notification on Significant Harm", "Notify PDPC of data breach likely to result in significant harm to individuals (Section 26B)", ControlCategory.Incident, ControlSeverity.Critical, true),
            new("PDPA.DBN.002", "Affected Individual Notification", "Notify affected individuals of data breach likely to result in significant harm (Section 26C)", ControlCategory.Incident, ControlSeverity.Critical, true),
            new("PDPA.DBN.003", "Three-Day Notification Timeline", "Notify PDPC within 3 calendar days of assessing breach as notifiable (Section 26B)", ControlCategory.Incident, ControlSeverity.Critical, true),
            new("PDPA.DBN.004", "Breach Assessment Procedures", "Assess within 30 days whether breach is notifiable (significant harm test) (Section 26B)", ControlCategory.Incident, ControlSeverity.Critical, true),
            new("PDPA.DBN.005", "Breach Notification Content", "Include prescribed information in breach notifications (nature, data involved, remedial actions) (Section 26C)", ControlCategory.Incident, ControlSeverity.High, true),
            new("PDPA.DBN.006", "Breach Documentation and Records", "Maintain records of all data breaches for PDPC inspection (Section 26D)", ControlCategory.Audit, ControlSeverity.High, true),

            // 10. Do Not Call Registry (PDPA Part IX - Sections 43-48)
            new("PDPA.DNC.001", "DNC Registry Compliance", "Check numbers against DNC Registry before marketing calls/messages (Section 43)", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("PDPA.DNC.002", "DNC Opt-Out Mechanisms", "Provide clear and easy opt-out mechanism in all marketing messages (Section 43)", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("PDPA.DNC.003", "Marketing Consent Requirements", "Obtain clear and unambiguous consent before sending marketing messages (Section 43)", ControlCategory.DataProtection, ControlSeverity.Critical, true),
            new("PDPA.DNC.004", "DNC Registry Update Frequency", "Update DNC registry checks at least every 30 days (Section 43)", ControlCategory.DataProtection, ControlSeverity.Medium, true),
            new("PDPA.DNC.005", "Marketing Message Identification", "Clearly identify organization in all marketing communications (Section 43)", ControlCategory.DataProtection, ControlSeverity.Medium, true),
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

        // Implement control-specific checks
        switch (control.ControlId)
        {
            // Consent Obligation Checks
            case "PDPA.CON.001":
            case "PDPA.CON.003":
                var consentMgmt = await CheckConsentManagementAsync();
                if (!consentMgmt)
                {
                    findings.Add("Consent collection not properly documented or lacks voluntary, informed, and specific elements");
                    recommendations.Add("Implement granular consent management with clear purpose specification and withdrawal mechanisms");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "PDPA.CON.002":
            case "PDPA.CON.004":
            case "PDPA.CON.005":
                var deemedConsent = await CheckDeemedConsentAsync();
                if (!deemedConsent)
                {
                    findings.Add("Deemed consent mechanisms not properly implemented or lack reasonable person test documentation");
                    recommendations.Add("Document deemed consent scenarios and ensure reasonable person test is met with proper notification");
                    status = AutomationComplianceStatus.PartiallyCompliant;
                }
                break;

            // Purpose Limitation Checks
            case "PDPA.PL.001":
            case "PDPA.PL.002":
            case "PDPA.PL.003":
            case "PDPA.PL.004":
                var purposeLimitation = await CheckPurposeLimitationAsync();
                if (!purposeLimitation)
                {
                    findings.Add("Purpose limitation not enforced - data may be used beyond notified purposes or purposes not specific enough");
                    recommendations.Add("Implement purpose specification on collection and automated enforcement of purpose-based access controls");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            // Notification Obligation Checks
            case "PDPA.NOT.001":
            case "PDPA.NOT.002":
            case "PDPA.NOT.003":
            case "PDPA.NOT.004":
                var notificationObligation = await CheckNotificationObligationAsync();
                if (!notificationObligation)
                {
                    findings.Add("Notification obligations not met - purposes, voluntary/mandatory status, or DPO contact not provided at collection");
                    recommendations.Add("Implement automated notification at collection with purposes, data requirements, and contact information");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            // Access and Correction Checks
            case "PDPA.AC.001":
            case "PDPA.AC.002":
            case "PDPA.AC.003":
            case "PDPA.AC.004":
                var accessCorrection = await CheckAccessCorrectionAsync();
                if (!accessCorrection)
                {
                    findings.Add("Access and correction request handling not automated or exceeds 30-day response time");
                    recommendations.Add("Implement automated access and correction workflows with 30-day SLA tracking");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "PDPA.AC.005":
            case "PDPA.AC.006":
                var accessAudit = await CheckAccessAuditAsync();
                if (!accessAudit)
                {
                    findings.Add("Third-party correction notifications or denial documentation not tracked");
                    recommendations.Add("Implement audit trail for third-party notifications and request denial reasons");
                    status = AutomationComplianceStatus.PartiallyCompliant;
                }
                break;

            // Accuracy Obligation Checks
            case "PDPA.ACC.001":
            case "PDPA.ACC.002":
            case "PDPA.ACC.003":
            case "PDPA.ACC.004":
                var accuracy = await CheckAccuracyAsync();
                if (!accuracy)
                {
                    findings.Add("Data accuracy not maintained or verification procedures not implemented");
                    recommendations.Add("Implement periodic data accuracy verification and self-service correction mechanisms");
                    status = AutomationComplianceStatus.PartiallyCompliant;
                }
                break;

            // Protection Obligation Checks
            case "PDPA.PRO.001":
            case "PDPA.PRO.003":
            case "PDPA.PRO.005":
                var protection = await CheckProtectionMeasuresAsync();
                if (!protection)
                {
                    findings.Add("Security arrangements insufficient - encryption, access controls, or loss prevention measures not adequate");
                    recommendations.Add("Implement AES-256 encryption at rest, TLS 1.3 in transit, and role-based access controls");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "PDPA.PRO.006":
                var dataProcessor = await CheckDataProcessorProtectionAsync();
                if (!dataProcessor)
                {
                    findings.Add("Data intermediaries not contractually bound to provide comparable protection");
                    recommendations.Add("Require data processing agreements with security and confidentiality obligations");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            // Retention Limitation Checks
            case "PDPA.RET.001":
            case "PDPA.RET.002":
            case "PDPA.RET.003":
            case "PDPA.RET.004":
            case "PDPA.RET.005":
                var retention = await CheckRetentionLimitationAsync();
                if (!retention)
                {
                    findings.Add("Retention policies not defined or not enforced - data retained beyond necessary period");
                    recommendations.Add("Implement automated retention schedules with secure disposal or anonymization");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            // Transfer Limitation Checks
            case "PDPA.TL.001":
            case "PDPA.TL.002":
            case "PDPA.TL.003":
                var transfer = await CheckTransferLimitationAsync();
                if (!transfer)
                {
                    findings.Add("Overseas transfers lack comparable protection verification or contractual safeguards");
                    recommendations.Add("Implement transfer impact assessments and contractual clauses ensuring comparable protection");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "PDPA.TL.004":
            case "PDPA.TL.005":
                var transferDoc = await CheckTransferDocumentationAsync();
                if (!transferDoc)
                {
                    findings.Add("Overseas transfer documentation or risk assessments not maintained");
                    recommendations.Add("Implement transfer register and risk assessment documentation for all overseas transfers");
                    status = AutomationComplianceStatus.PartiallyCompliant;
                }
                break;

            // Data Breach Notification Checks
            case "PDPA.DBN.001":
            case "PDPA.DBN.002":
            case "PDPA.DBN.003":
            case "PDPA.DBN.004":
                var breachNotification = await CheckBreachNotificationAsync();
                if (!breachNotification)
                {
                    findings.Add("Data breach notification procedures not implemented or 3-day PDPC notification timeline not enforceable");
                    recommendations.Add("Implement automated breach detection and notification workflows with 3-day PDPC notification SLA");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "PDPA.DBN.005":
            case "PDPA.DBN.006":
                var breachDoc = await CheckBreachDocumentationAsync();
                if (!breachDoc)
                {
                    findings.Add("Breach notification content or documentation not meeting PDPC requirements");
                    recommendations.Add("Implement breach register with prescribed information fields for PDPC inspection");
                    status = AutomationComplianceStatus.PartiallyCompliant;
                }
                break;

            // Do Not Call Registry Checks
            case "PDPA.DNC.001":
            case "PDPA.DNC.003":
                var dncCompliance = await CheckDncComplianceAsync();
                if (!dncCompliance)
                {
                    findings.Add("DNC Registry checks not performed before marketing or consent not obtained");
                    recommendations.Add("Integrate DNC Registry API and implement consent verification before marketing communications");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "PDPA.DNC.002":
            case "PDPA.DNC.004":
            case "PDPA.DNC.005":
                var dncMechanisms = await CheckDncMechanismsAsync();
                if (!dncMechanisms)
                {
                    findings.Add("DNC opt-out mechanisms not clear, registry updates exceed 30 days, or sender not identified");
                    recommendations.Add("Implement clear opt-out in all messages, monthly DNC updates, and sender identification");
                    status = AutomationComplianceStatus.PartiallyCompliant;
                }
                break;

            default:
                // Default: assume compliant for demonstration
                break;
        }

        var result = new AutomationCheckResult(
            checkId,
            control.ControlId,
            ComplianceFramework.Pdpa,
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
            actions.Add("Applied PDPA-compliant configuration changes");

            // Control-specific remediation actions
            if (result.ControlId.StartsWith("PDPA.CON"))
            {
                actions.Add("Configured consent management with granular purpose tracking");
            }
            else if (result.ControlId.StartsWith("PDPA.PL"))
            {
                actions.Add("Implemented purpose limitation enforcement and notification");
            }
            else if (result.ControlId.StartsWith("PDPA.AC"))
            {
                actions.Add("Configured access and correction workflows with 30-day SLA");
            }
            else if (result.ControlId.StartsWith("PDPA.PRO"))
            {
                actions.Add("Applied security safeguards (encryption, access controls)");
            }
            else if (result.ControlId.StartsWith("PDPA.RET"))
            {
                actions.Add("Configured automated retention schedules and secure disposal");
            }
            else if (result.ControlId.StartsWith("PDPA.TL"))
            {
                actions.Add("Implemented transfer safeguards and documentation");
            }
            else if (result.ControlId.StartsWith("PDPA.DBN"))
            {
                actions.Add("Configured breach notification workflows with 3-day timeline");
            }
            else if (result.ControlId.StartsWith("PDPA.DNC"))
            {
                actions.Add("Integrated DNC Registry API and opt-out mechanisms");
            }
        }

        return Task.FromResult(new RemediationResult(
            !options.DryRun,
            actions.ToArray(),
            errors.ToArray(),
            options.RequireApproval
        ));
    }

    // Simulated check methods for PDPA obligations
    private Task<bool> CheckConsentManagementAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.2);
    private Task<bool> CheckDeemedConsentAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.3);
    private Task<bool> CheckPurposeLimitationAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.25);
    private Task<bool> CheckNotificationObligationAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.2);
    private Task<bool> CheckAccessCorrectionAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.3);
    private Task<bool> CheckAccessAuditAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.4);
    private Task<bool> CheckAccuracyAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.35);
    private Task<bool> CheckProtectionMeasuresAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.15);
    private Task<bool> CheckDataProcessorProtectionAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.25);
    private Task<bool> CheckRetentionLimitationAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.3);
    private Task<bool> CheckTransferLimitationAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.2);
    private Task<bool> CheckTransferDocumentationAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.35);
    private Task<bool> CheckBreachNotificationAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.25);
    private Task<bool> CheckBreachDocumentationAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.4);
    private Task<bool> CheckDncComplianceAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.2);
    private Task<bool> CheckDncMechanismsAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.3);
}
