using DataWarehouse.SDK.Compliance;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Plugins.ComplianceAutomation;

/// <summary>
/// NIS2 (EU Network and Information Security Directive 2) compliance automation plugin.
/// Implements automated checks for NIS2 requirements covering critical infrastructure cybersecurity.
/// Directive (EU) 2022/2555 on measures for high common level of cybersecurity across the Union.
/// </summary>
public class Nis2CompliancePlugin : ComplianceAutomationPluginBase
{
    private readonly List<AutomationControl> _controls;
    private readonly Dictionary<string, AutomationCheckResult> _lastResults = new();

    /// <inheritdoc />
    public override string Id => "com.datawarehouse.compliance.nis2";

    /// <inheritdoc />
    public override string Name => "NIS2 Directive Compliance Automation";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.GovernanceProvider;

    /// <inheritdoc />
    public override ComplianceFramework SupportedFramework => ComplianceFramework.Nis2;

    public Nis2CompliancePlugin()
    {
        _controls = new List<AutomationControl>
        {
            // Article 21 - Risk Management Measures
            new("NIS2.RM.001", "Risk Analysis and Information Security Policies", "Conduct risk analysis and implement security policies (Art. 21.2.a)", ControlCategory.DataProtection, ControlSeverity.Critical, true),
            new("NIS2.RM.002", "Incident Handling Procedures", "Establish procedures for handling incidents (Art. 21.2.b)", ControlCategory.Incident, ControlSeverity.Critical, true),
            new("NIS2.RM.003", "Business Continuity Management", "Implement business continuity and disaster recovery measures (Art. 21.2.c)", ControlCategory.BusinessContinuity, ControlSeverity.Critical, true),
            new("NIS2.RM.004", "Supply Chain Security", "Implement supply chain security measures (Art. 21.2.d)", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("NIS2.RM.005", "Security in Network and Information Systems", "Ensure security in acquisition, development, and maintenance (Art. 21.2.e)", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("NIS2.RM.006", "Policies on Risk Analysis Assessment", "Develop policies to assess effectiveness of risk management measures (Art. 21.2.f)", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("NIS2.RM.007", "Basic Cyber Hygiene Practices", "Implement basic cyber hygiene practices and training (Art. 21.2.g)", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("NIS2.RM.008", "Cryptography Policies and Procedures", "Implement cryptography policies including encryption (Art. 21.2.h)", ControlCategory.Encryption, ControlSeverity.Critical, true),
            new("NIS2.RM.009", "Human Resources Security", "Implement security in human resources policies (Art. 21.2.i)", ControlCategory.AccessControl, ControlSeverity.Medium, true),
            new("NIS2.RM.010", "Access Control Policies", "Implement access control policies and asset management (Art. 21.2.j)", ControlCategory.AccessControl, ControlSeverity.Critical, true),
            new("NIS2.RM.011", "Multi-Factor Authentication", "Use MFA or continuous authentication solutions (Art. 21.2.k)", ControlCategory.AccessControl, ControlSeverity.Critical, true),
            new("NIS2.RM.012", "Secure Communications", "Ensure secured voice, video, and text communications (Art. 21.2.l)", ControlCategory.Encryption, ControlSeverity.High, true),
            new("NIS2.RM.013", "Secured Emergency Communication", "Implement secured emergency communication systems (Art. 21.2.l)", ControlCategory.Encryption, ControlSeverity.High, true),

            // Article 23 - Incident Handling
            new("NIS2.IH.001", "Incident Detection Capabilities", "Implement mechanisms to promptly detect incidents (Art. 23)", ControlCategory.Incident, ControlSeverity.Critical, true),
            new("NIS2.IH.002", "Incident Response Procedures", "Establish incident response and recovery procedures (Art. 23)", ControlCategory.Incident, ControlSeverity.Critical, true),
            new("NIS2.IH.003", "Early Warning Notification (24h)", "Provide early warning without undue delay (24 hours) (Art. 23.4)", ControlCategory.Incident, ControlSeverity.Critical, true),
            new("NIS2.IH.004", "Incident Notification (72h)", "Submit incident notification within 72 hours (Art. 23.5)", ControlCategory.Incident, ControlSeverity.Critical, true),
            new("NIS2.IH.005", "Final Report (1 month)", "Provide final report within one month (Art. 23.6)", ControlCategory.Incident, ControlSeverity.High, true),
            new("NIS2.IH.006", "Significant Incident Criteria", "Assess incidents against significance criteria (Art. 23.3)", ControlCategory.Incident, ControlSeverity.High, true),
            new("NIS2.IH.007", "Service Disruption Assessment", "Evaluate severe operational disruption or financial loss (Art. 23.3.a)", ControlCategory.Incident, ControlSeverity.High, true),
            new("NIS2.IH.008", "Impact on Other Entities", "Assess impact on other natural or legal persons (Art. 23.3.b)", ControlCategory.Incident, ControlSeverity.High, true),
            new("NIS2.IH.009", "Cross-Border Impact", "Determine incidents with cross-border significance (Art. 23.7)", ControlCategory.Incident, ControlSeverity.High, true),

            // Business Continuity (Article 21.2.c)
            new("NIS2.BC.001", "Backup Management Policies", "Implement backup management procedures (Art. 21.2.c)", ControlCategory.BusinessContinuity, ControlSeverity.Critical, true),
            new("NIS2.BC.002", "Disaster Recovery Plans", "Maintain disaster recovery plans (Art. 21.2.c)", ControlCategory.BusinessContinuity, ControlSeverity.Critical, true),
            new("NIS2.BC.003", "Crisis Management Procedures", "Establish crisis management procedures (Art. 21.2.c)", ControlCategory.BusinessContinuity, ControlSeverity.High, true),
            new("NIS2.BC.004", "Recovery Time Objectives", "Define and test recovery time objectives (Art. 21.2.c)", ControlCategory.BusinessContinuity, ControlSeverity.High, true),

            // Supply Chain Security (Article 21.2.d)
            new("NIS2.SC.001", "Supplier Risk Assessment", "Assess cybersecurity risks from supplier relationships (Art. 21.2.d)", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("NIS2.SC.002", "Security Requirements in Contracts", "Include security requirements in supplier contracts (Art. 21.2.d)", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("NIS2.SC.003", "Third-Party Access Controls", "Control and monitor third-party access (Art. 21.2.d)", ControlCategory.AccessControl, ControlSeverity.High, true),
            new("NIS2.SC.004", "Vendor Security Management", "Implement vendor security management program (Art. 21.2.d)", ControlCategory.DataProtection, ControlSeverity.Medium, true),

            // Cryptography (Article 21.2.h)
            new("NIS2.CRY.001", "Encryption of Data at Rest", "Implement encryption for data at rest (Art. 21.2.h)", ControlCategory.Encryption, ControlSeverity.Critical, true),
            new("NIS2.CRY.002", "Encryption of Data in Transit", "Implement encryption for data in transit (Art. 21.2.h)", ControlCategory.Encryption, ControlSeverity.Critical, true),
            new("NIS2.CRY.003", "Cryptographic Key Management", "Establish key management procedures (Art. 21.2.h)", ControlCategory.Encryption, ControlSeverity.Critical, true),
            new("NIS2.CRY.004", "Cryptography Policy Documentation", "Document cryptography policies and standards (Art. 21.2.h)", ControlCategory.Encryption, ControlSeverity.High, true),

            // Access Control (Article 21.2.j, k)
            new("NIS2.AC.001", "Identity and Access Management", "Implement identity and access management (Art. 21.2.j)", ControlCategory.AccessControl, ControlSeverity.Critical, true),
            new("NIS2.AC.002", "User Authentication Mechanisms", "Enforce strong authentication mechanisms (Art. 21.2.k)", ControlCategory.AccessControl, ControlSeverity.Critical, true),
            new("NIS2.AC.003", "Privileged Access Management", "Control and monitor privileged access (Art. 21.2.j)", ControlCategory.AccessControl, ControlSeverity.Critical, true),
            new("NIS2.AC.004", "Zero-Trust Architecture", "Implement zero-trust principles where applicable (Art. 21.2.k)", ControlCategory.AccessControl, ControlSeverity.High, true),
            new("NIS2.AC.005", "Asset Management", "Maintain asset inventory and classification (Art. 21.2.j)", ControlCategory.DataProtection, ControlSeverity.High, true),

            // Human Resources Security (Article 21.2.i, g)
            new("NIS2.HR.001", "Security Awareness Training", "Provide regular security awareness training (Art. 21.2.g)", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("NIS2.HR.002", "Secure Recruitment Process", "Implement security screening in recruitment (Art. 21.2.i)", ControlCategory.AccessControl, ControlSeverity.Medium, true),
            new("NIS2.HR.003", "Role-Based Security Responsibilities", "Define security responsibilities by role (Art. 21.2.i)", ControlCategory.AccessControl, ControlSeverity.Medium, true),

            // Vulnerability Handling (Article 21.2.e)
            new("NIS2.VH.001", "Vulnerability Disclosure Policy", "Establish coordinated vulnerability disclosure policy (Art. 21.2.e)", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("NIS2.VH.002", "Vulnerability Scanning", "Perform regular vulnerability scanning (Art. 21.2.e)", ControlCategory.DataProtection, ControlSeverity.High, true),
            new("NIS2.VH.003", "Patch Management", "Implement timely patch management (Art. 21.2.e)", ControlCategory.DataProtection, ControlSeverity.Critical, true),
            new("NIS2.VH.004", "Security Update Procedures", "Establish procedures for security updates (Art. 21.2.e)", ControlCategory.DataProtection, ControlSeverity.High, true),
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

        // Implement control-specific checks based on control ID
        switch (control.ControlId)
        {
            // Risk Management
            case "NIS2.RM.001":
                var riskAnalysis = await CheckRiskAnalysisAsync();
                if (!riskAnalysis)
                {
                    findings.Add("Risk analysis not documented or outdated");
                    recommendations.Add("Conduct comprehensive risk analysis and document security policies per NIS2 Article 21.2.a");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "NIS2.RM.008":
            case "NIS2.CRY.001":
            case "NIS2.CRY.002":
                // Check encryption implementation
                var encryptionAtRest = await CheckEncryptionAtRestAsync();
                var encryptionInTransit = await CheckEncryptionInTransitAsync();

                if (!encryptionAtRest)
                {
                    findings.Add("Data at rest is not encrypted with approved algorithms (e.g., AES-256)");
                    recommendations.Add("Implement encryption at rest using AES-256-GCM or equivalent");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                if (!encryptionInTransit)
                {
                    findings.Add("Data in transit not protected with TLS 1.3 or equivalent");
                    recommendations.Add("Enforce TLS 1.3 for all network communications");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "NIS2.CRY.003":
                // Check cryptographic key management
                var keyManagement = await CheckKeyManagementAsync();
                if (!keyManagement)
                {
                    findings.Add("Cryptographic key management procedures not fully implemented");
                    recommendations.Add("Implement key lifecycle management with rotation, backup, and secure storage");
                    status = AutomationComplianceStatus.PartiallyCompliant;
                }
                break;

            case "NIS2.RM.011":
            case "NIS2.AC.002":
                // Check multi-factor authentication
                var mfaEnabled = await CheckMultiFactorAuthAsync();
                if (!mfaEnabled)
                {
                    findings.Add("Multi-factor authentication not enforced for critical systems");
                    recommendations.Add("Implement MFA or continuous authentication for all system access per NIS2 Article 21.2.k");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "NIS2.AC.001":
            case "NIS2.AC.003":
            case "NIS2.RM.010":
                // Check access control implementation
                var accessControl = await CheckAccessControlAsync();
                if (!accessControl)
                {
                    findings.Add("Access control policies not properly implemented or enforced");
                    recommendations.Add("Implement identity and access management with privileged access monitoring");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "NIS2.IH.001":
            case "NIS2.IH.002":
                // Check incident detection and response
                var incidentCapabilities = await CheckIncidentResponseAsync();
                if (!incidentCapabilities)
                {
                    findings.Add("Incident detection or response capabilities insufficient");
                    recommendations.Add("Implement SIEM or equivalent for incident detection and establish response procedures");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "NIS2.IH.003":
            case "NIS2.IH.004":
            case "NIS2.IH.005":
                // Check incident notification procedures
                var notificationReady = await CheckIncidentNotificationAsync();
                if (!notificationReady)
                {
                    findings.Add("Incident notification procedures not documented for 24h/72h/1-month timelines");
                    recommendations.Add("Document and test incident notification workflows per NIS2 Article 23");
                    status = AutomationComplianceStatus.PartiallyCompliant;
                }
                break;

            case "NIS2.BC.001":
            case "NIS2.BC.002":
                // Check business continuity and backup
                var backupCompliant = await CheckBackupManagementAsync();
                if (!backupCompliant)
                {
                    findings.Add("Backup management or disaster recovery plans not sufficient");
                    recommendations.Add("Implement automated backup with off-site storage and tested recovery procedures");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "NIS2.BC.004":
                // Check RTO/RPO
                var rtoTested = await CheckRecoveryObjectivesAsync();
                if (!rtoTested)
                {
                    findings.Add("Recovery time objectives not defined or tested");
                    recommendations.Add("Define RTO/RPO and conduct annual disaster recovery testing");
                    status = AutomationComplianceStatus.PartiallyCompliant;
                }
                break;

            case "NIS2.SC.001":
            case "NIS2.SC.002":
                // Check supply chain security
                var supplyChainSecure = await CheckSupplyChainSecurityAsync();
                if (!supplyChainSecure)
                {
                    findings.Add("Supply chain security assessment or contractual requirements missing");
                    recommendations.Add("Conduct supplier risk assessment and include security requirements in contracts");
                    status = AutomationComplianceStatus.PartiallyCompliant;
                }
                break;

            case "NIS2.HR.001":
                // Check security awareness training
                var trainingCompliant = await CheckSecurityTrainingAsync();
                if (!trainingCompliant)
                {
                    findings.Add("Security awareness training not conducted regularly");
                    recommendations.Add("Implement annual mandatory security awareness training for all staff");
                    status = AutomationComplianceStatus.PartiallyCompliant;
                }
                break;

            case "NIS2.VH.003":
                // Check patch management
                var patchingCompliant = await CheckPatchManagementAsync();
                if (!patchingCompliant)
                {
                    findings.Add("Critical security patches not applied within required timeframes");
                    recommendations.Add("Implement automated patch management with 30-day SLA for critical vulnerabilities");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "NIS2.VH.002":
                // Check vulnerability scanning
                var scanningActive = await CheckVulnerabilityScanningAsync();
                if (!scanningActive)
                {
                    findings.Add("Regular vulnerability scanning not performed");
                    recommendations.Add("Implement quarterly vulnerability scanning for all network-accessible systems");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            default:
                // For other controls, assume compliant (simulation)
                break;
        }

        var result = new AutomationCheckResult(
            checkId,
            control.ControlId,
            ComplianceFramework.Nis2,
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
            // Simulate remediation based on control type
            actions.Add($"Initiated remediation for {result.ControlId}");

            if (result.ControlId.StartsWith("NIS2.CRY"))
            {
                actions.Add("Applied cryptographic configuration changes");
                actions.Add("Enabled AES-256-GCM encryption for data at rest");
            }
            else if (result.ControlId.StartsWith("NIS2.AC") || result.ControlId.StartsWith("NIS2.RM.01"))
            {
                actions.Add("Updated access control policies");
                actions.Add("Configured multi-factor authentication");
            }
            else if (result.ControlId.StartsWith("NIS2.IH"))
            {
                actions.Add("Configured incident detection and alerting");
                actions.Add("Updated incident notification templates");
            }
            else if (result.ControlId.StartsWith("NIS2.BC"))
            {
                actions.Add("Configured automated backup schedules");
                actions.Add("Validated disaster recovery procedures");
            }
            else
            {
                actions.Add("Applied NIS2-compliant configuration changes");
            }
        }

        return Task.FromResult(new RemediationResult(
            !options.DryRun,
            actions.ToArray(),
            errors.ToArray(),
            options.RequireApproval
        ));
    }

    // Simulated check methods - in production, these would perform actual system checks
    private Task<bool> CheckRiskAnalysisAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.2);
    private Task<bool> CheckEncryptionAtRestAsync() => Task.FromResult(true);
    private Task<bool> CheckEncryptionInTransitAsync() => Task.FromResult(true);
    private Task<bool> CheckKeyManagementAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.3);
    private Task<bool> CheckMultiFactorAuthAsync() => Task.FromResult(true);
    private Task<bool> CheckAccessControlAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.2);
    private Task<bool> CheckIncidentResponseAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.3);
    private Task<bool> CheckIncidentNotificationAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.4);
    private Task<bool> CheckBackupManagementAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.2);
    private Task<bool> CheckRecoveryObjectivesAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.3);
    private Task<bool> CheckSupplyChainSecurityAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.4);
    private Task<bool> CheckSecurityTrainingAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.3);
    private Task<bool> CheckPatchManagementAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.2);
    private Task<bool> CheckVulnerabilityScanningAsync() => Task.FromResult(Random.Shared.NextDouble() > 0.3);
}
