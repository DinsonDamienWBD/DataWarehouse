namespace DataWarehouse.SDK.Compliance;

/// <summary>
/// Compliance frameworks supported by the automation system.
/// Covers major regulatory standards across industries and regions.
/// </summary>
public enum ComplianceFramework
{
    /// <summary>EU General Data Protection Regulation - Personal data protection</summary>
    GDPR,

    /// <summary>US Health Insurance Portability and Accountability Act - Healthcare data</summary>
    HIPAA,

    /// <summary>Payment Card Industry Data Security Standard - Payment card data</summary>
    PciDss,

    /// <summary>Sarbanes-Oxley Act - Financial reporting and auditing</summary>
    Sox,

    /// <summary>US Federal Risk and Authorization Management Program - Cloud services</summary>
    FedRamp,

    /// <summary>California Consumer Privacy Act - California consumer data</summary>
    Ccpa,

    /// <summary>Brazil Lei Geral de Proteção de Dados - Brazil data protection</summary>
    Lgpd,

    /// <summary>Canada Personal Information Protection and Electronic Documents Act</summary>
    Pipeda,

    /// <summary>Singapore Personal Data Protection Act</summary>
    Pdpa,

    /// <summary>EU Digital Operational Resilience Act - Financial sector ICT risk</summary>
    Dora,

    /// <summary>EU Network and Information Security Directive 2 - Critical infrastructure</summary>
    Nis2,

    /// <summary>Trusted Information Security Assessment Exchange - German automotive</summary>
    Tisax,

    /// <summary>ISO/IEC 27001 - Information Security Management System</summary>
    Iso27001,

    /// <summary>Service Organization Control 2 Type II - Service organization controls</summary>
    Soc2TypeII
}

/// <summary>
/// Compliance status indicating level of conformance to requirements.
/// </summary>
public enum AutomationComplianceStatus
{
    /// <summary>Fully compliant with all requirements</summary>
    Compliant,

    /// <summary>One or more requirements not met</summary>
    NonCompliant,

    /// <summary>Some requirements met, others not met</summary>
    PartiallyCompliant,

    /// <summary>Compliance status cannot be determined</summary>
    Unknown,

    /// <summary>Requirement does not apply to this system</summary>
    NotApplicable
}

/// <summary>
/// Result of a single compliance control check.
/// Contains findings, recommendations, and remediation guidance.
/// </summary>
/// <param name="CheckId">Unique identifier for this check execution</param>
/// <param name="ControlId">Identifier of the control being checked</param>
/// <param name="Framework">Framework this control belongs to</param>
/// <param name="Status">Compliance status result</param>
/// <param name="Description">Human-readable description of the check</param>
/// <param name="Findings">List of specific issues found</param>
/// <param name="Recommendations">Actionable recommendations to achieve compliance</param>
/// <param name="CheckedAt">Timestamp when check was performed</param>
public record AutomationCheckResult(
    string CheckId,
    string ControlId,
    ComplianceFramework Framework,
    AutomationComplianceStatus Status,
    string Description,
    string[] Findings,
    string[] Recommendations,
    DateTimeOffset CheckedAt
);

/// <summary>
/// Comprehensive compliance report for a framework.
/// Aggregates individual control check results.
/// </summary>
/// <param name="ReportId">Unique identifier for this report</param>
/// <param name="Framework">Framework assessed in this report</param>
/// <param name="OverallStatus">Overall compliance status</param>
/// <param name="TotalControls">Total number of controls assessed</param>
/// <param name="CompliantControls">Number of controls that passed</param>
/// <param name="NonCompliantControls">Number of controls that failed</param>
/// <param name="Results">Individual control check results</param>
/// <param name="GeneratedAt">Report generation timestamp</param>
public record ComplianceAutomationReport(
    string ReportId,
    ComplianceFramework Framework,
    AutomationComplianceStatus OverallStatus,
    int TotalControls,
    int CompliantControls,
    int NonCompliantControls,
    IReadOnlyList<AutomationCheckResult> Results,
    DateTimeOffset GeneratedAt
);

/// <summary>
/// Interface for compliance automation systems.
/// Provides automated compliance checking, control validation, and remediation.
/// </summary>
public interface IComplianceAutomation
{
    /// <summary>
    /// Runs all compliance checks for a specific framework.
    /// Generates a comprehensive report with all control results.
    /// </summary>
    /// <param name="framework">Framework to check compliance against</param>
    /// <param name="options">Optional configuration for the check run</param>
    /// <returns>Compliance report with all check results</returns>
    Task<ComplianceAutomationReport> RunComplianceCheckAsync(ComplianceFramework framework, AutomationCheckOptions? options = null);

    /// <summary>
    /// Checks a specific control within a framework.
    /// Useful for targeted validation or continuous compliance monitoring.
    /// </summary>
    /// <param name="framework">Framework containing the control</param>
    /// <param name="controlId">Unique identifier of the control to check</param>
    /// <returns>Check result for the specified control</returns>
    Task<AutomationCheckResult> CheckControlAsync(ComplianceFramework framework, string controlId);

    /// <summary>
    /// Retrieves all controls defined for a framework.
    /// Provides metadata about control requirements and automation support.
    /// </summary>
    /// <param name="framework">Framework to get controls for</param>
    /// <returns>List of compliance controls</returns>
    Task<IReadOnlyList<AutomationControl>> GetControlsAsync(ComplianceFramework framework);

    /// <summary>
    /// Attempts to automatically remediate compliance findings.
    /// May require approval based on remediation options.
    /// </summary>
    /// <param name="checkId">Identifier of the check with findings to remediate</param>
    /// <param name="options">Remediation behavior configuration</param>
    /// <returns>Result of remediation attempt</returns>
    Task<RemediationResult> RemediateAsync(string checkId, RemediationOptions options);
}

/// <summary>
/// Options for configuring compliance check execution.
/// </summary>
/// <param name="SpecificControls">Optional list of control IDs to check (null = all controls)</param>
/// <param name="AutoRemediate">Whether to automatically remediate non-compliant findings</param>
/// <param name="GenerateEvidence">Whether to collect and attach evidence artifacts</param>
public record AutomationCheckOptions(
    string[]? SpecificControls = null,
    bool AutoRemediate = false,
    bool GenerateEvidence = true
);

/// <summary>
/// Definition of a compliance control requirement.
/// Maps to specific regulatory or standard requirements.
/// </summary>
/// <param name="ControlId">Unique identifier for this control</param>
/// <param name="Title">Short title of the control</param>
/// <param name="Description">Detailed description of control requirements</param>
/// <param name="Category">Category this control belongs to</param>
/// <param name="Severity">Impact severity if control is not met</param>
/// <param name="IsAutomatable">Whether this control can be checked automatically</param>
public record AutomationControl(
    string ControlId,
    string Title,
    string Description,
    ControlCategory Category,
    ControlSeverity Severity,
    bool IsAutomatable
);

/// <summary>
/// Categories of compliance controls.
/// </summary>
public enum ControlCategory
{
    /// <summary>Access control and authentication</summary>
    AccessControl,

    /// <summary>Data protection and privacy</summary>
    DataProtection,

    /// <summary>Encryption and key management</summary>
    Encryption,

    /// <summary>Audit logging and monitoring</summary>
    Audit,

    /// <summary>Network security and segmentation</summary>
    NetworkSecurity,

    /// <summary>Incident response and management</summary>
    Incident,

    /// <summary>Business continuity and disaster recovery</summary>
    BusinessContinuity
}

/// <summary>
/// Severity levels for control compliance failures.
/// </summary>
public enum ControlSeverity
{
    /// <summary>Critical - immediate action required</summary>
    Critical,

    /// <summary>High - urgent attention needed</summary>
    High,

    /// <summary>Medium - should be addressed soon</summary>
    Medium,

    /// <summary>Low - minor issue</summary>
    Low,

    /// <summary>Informational - no action required</summary>
    Informational
}

/// <summary>
/// Options for controlling remediation behavior.
/// </summary>
/// <param name="DryRun">If true, simulate remediation without making changes</param>
/// <param name="RequireApproval">If true, require manual approval before executing</param>
public record RemediationOptions(bool DryRun = false, bool RequireApproval = true);

/// <summary>
/// Result of a remediation attempt.
/// </summary>
/// <param name="Success">Whether remediation was successful</param>
/// <param name="ActionsPerformed">List of actions taken during remediation</param>
/// <param name="Errors">Any errors encountered during remediation</param>
/// <param name="RequiresManualAction">Whether manual intervention is required</param>
public record RemediationResult(bool Success, string[] ActionsPerformed, string[] Errors, bool RequiresManualAction);

/// <summary>
/// Interface for Data Subject Rights automation (GDPR, CCPA, LGPD, PIPEDA, PDPA).
/// Implements rights to access, erasure, rectification, portability, and restriction.
/// </summary>
public interface IDataSubjectRights
{
    /// <summary>
    /// Right to Access - Exports all data related to a data subject.
    /// Required by GDPR Article 15, CCPA Section 1798.110, LGPD Article 18.
    /// </summary>
    /// <param name="subjectId">Unique identifier of the data subject</param>
    /// <param name="options">Export configuration options</param>
    /// <returns>Data export package</returns>
    Task<DataExport> ExportDataAsync(string subjectId, DataExportOptions options);

    /// <summary>
    /// Right to Erasure (Right to be Forgotten) - Deletes all data for a subject.
    /// Required by GDPR Article 17, CCPA Section 1798.105, LGPD Article 18.
    /// </summary>
    /// <param name="subjectId">Unique identifier of the data subject</param>
    /// <param name="options">Deletion configuration options</param>
    /// <returns>Deletion result with certificate</returns>
    Task<DeletionResult> DeleteDataAsync(string subjectId, DeletionOptions options);

    /// <summary>
    /// Right to Rectification - Corrects inaccurate personal data.
    /// Required by GDPR Article 16, LGPD Article 18.
    /// </summary>
    /// <param name="subjectId">Unique identifier of the data subject</param>
    /// <param name="corrections">Dictionary of field names to corrected values</param>
    /// <returns>Rectification result</returns>
    Task<RectificationResult> RectifyDataAsync(string subjectId, Dictionary<string, object> corrections);

    /// <summary>
    /// Right to Data Portability - Exports data in portable, machine-readable format.
    /// Required by GDPR Article 20, LGPD Article 18.
    /// </summary>
    /// <param name="subjectId">Unique identifier of the data subject</param>
    /// <param name="format">Export format (json, xml, csv)</param>
    /// <returns>Portable data export</returns>
    Task<DataExport> ExportPortableAsync(string subjectId, string format = "json");

    /// <summary>
    /// Right to Restriction of Processing - Pauses processing for a data subject.
    /// Required by GDPR Article 18, LGPD Article 18.
    /// </summary>
    /// <param name="subjectId">Unique identifier of the data subject</param>
    /// <param name="reason">Reason for restriction</param>
    Task RestrictProcessingAsync(string subjectId, string reason);

    /// <summary>
    /// Records consent for data processing.
    /// Required for GDPR lawful basis, LGPD Article 7.
    /// </summary>
    /// <param name="subjectId">Unique identifier of the data subject</param>
    /// <param name="consent">Consent details and scope</param>
    /// <returns>Consent record with unique ID</returns>
    Task<ConsentRecord> RecordConsentAsync(string subjectId, ConsentDetails consent);
}

/// <summary>
/// Options for configuring data export.
/// </summary>
/// <param name="Categories">Specific data categories to export (empty = all)</param>
/// <param name="Format">Export format (json, xml, csv, pdf)</param>
/// <param name="IncludeMetadata">Whether to include metadata in export</param>
public record DataExportOptions(string[] Categories, string Format = "json", bool IncludeMetadata = true);

/// <summary>
/// Data export package for a data subject.
/// </summary>
/// <param name="ExportId">Unique identifier for this export</param>
/// <param name="SubjectId">Data subject this export is for</param>
/// <param name="Data">Exported data as bytes</param>
/// <param name="Format">Format of the exported data</param>
/// <param name="GeneratedAt">Export generation timestamp</param>
public record DataExport(string ExportId, string SubjectId, byte[] Data, string Format, DateTimeOffset GeneratedAt);

/// <summary>
/// Options for configuring data deletion.
/// </summary>
/// <param name="Cascade">Whether to cascade delete to related records</param>
/// <param name="CreateCertificate">Whether to generate deletion certificate</param>
/// <param name="RetentionPeriod">Grace period before permanent deletion</param>
public record DeletionOptions(bool Cascade = true, bool CreateCertificate = true, TimeSpan RetentionPeriod = default);

/// <summary>
/// Result of data deletion operation.
/// </summary>
/// <param name="Success">Whether deletion was successful</param>
/// <param name="RecordsDeleted">Number of records deleted</param>
/// <param name="CertificateId">Deletion certificate ID if generated</param>
/// <param name="Errors">Any errors encountered</param>
public record DeletionResult(bool Success, int RecordsDeleted, string? CertificateId, string[] Errors);

/// <summary>
/// Result of data rectification operation.
/// </summary>
/// <param name="Success">Whether rectification was successful</param>
/// <param name="FieldsCorrected">Number of fields corrected</param>
/// <param name="Errors">Any errors encountered</param>
public record RectificationResult(bool Success, int FieldsCorrected, string[] Errors);

/// <summary>
/// Details of consent granted by data subject.
/// </summary>
/// <param name="Purpose">Purpose for which consent is granted</param>
/// <param name="LegalBasis">Legal basis for processing (consent, contract, etc.)</param>
/// <param name="ValidUntil">Expiration date for consent</param>
/// <param name="AllowMarketing">Whether marketing communications are allowed</param>
public record ConsentDetails(string Purpose, string LegalBasis, DateTimeOffset ValidUntil, bool AllowMarketing);

/// <summary>
/// Stored consent record.
/// </summary>
/// <param name="ConsentId">Unique identifier for this consent</param>
/// <param name="SubjectId">Data subject who gave consent</param>
/// <param name="Details">Consent details</param>
/// <param name="RecordedAt">When consent was recorded</param>
public record ConsentRecord(string ConsentId, string SubjectId, ConsentDetails Details, DateTimeOffset RecordedAt);

/// <summary>
/// Interface for compliance audit trail management.
/// Provides immutable audit logging for compliance evidence.
/// </summary>
public interface IComplianceAudit
{
    /// <summary>
    /// Logs a compliance-relevant event to the audit trail.
    /// Creates immutable record for regulatory evidence.
    /// </summary>
    /// <param name="evt">Event to log</param>
    Task LogEventAsync(ComplianceAuditEvent evt);

    /// <summary>
    /// Queries the audit trail with filtering criteria.
    /// Used for compliance reporting and investigation.
    /// </summary>
    /// <param name="query">Query criteria</param>
    /// <returns>Matching audit events</returns>
    Task<IReadOnlyList<ComplianceAuditEvent>> GetAuditTrailAsync(AuditQuery query);

    /// <summary>
    /// Generates a compliance audit report for a time period.
    /// Provides evidence for regulatory audits.
    /// </summary>
    /// <param name="framework">Framework to generate report for</param>
    /// <param name="start">Start of reporting period</param>
    /// <param name="end">End of reporting period</param>
    /// <returns>Audit report with event summary</returns>
    Task<AuditReport> GenerateAuditReportAsync(ComplianceFramework framework, DateTimeOffset start, DateTimeOffset end);
}

/// <summary>
/// Immutable audit event for compliance tracking.
/// </summary>
/// <param name="EventId">Unique identifier for this event</param>
/// <param name="EventType">Type of event (access, modification, deletion, etc.)</param>
/// <param name="Actor">User or system that performed the action</param>
/// <param name="Resource">Resource that was accessed or modified</param>
/// <param name="Action">Specific action performed</param>
/// <param name="Framework">Related compliance framework (if applicable)</param>
/// <param name="Timestamp">When the event occurred</param>
/// <param name="Details">Additional event details</param>
public record ComplianceAuditEvent(
    string EventId,
    string EventType,
    string Actor,
    string Resource,
    string Action,
    ComplianceFramework? Framework,
    DateTimeOffset Timestamp,
    Dictionary<string, object>? Details
);

/// <summary>
/// Query criteria for audit trail searches.
/// </summary>
/// <param name="Start">Start of time range (null = no start limit)</param>
/// <param name="End">End of time range (null = no end limit)</param>
/// <param name="Actor">Filter by actor (null = all actors)</param>
/// <param name="Resource">Filter by resource (null = all resources)</param>
/// <param name="Framework">Filter by framework (null = all frameworks)</param>
/// <param name="Limit">Maximum number of results to return</param>
public record AuditQuery(
    DateTimeOffset? Start = null,
    DateTimeOffset? End = null,
    string? Actor = null,
    string? Resource = null,
    ComplianceFramework? Framework = null,
    int Limit = 1000
);

/// <summary>
/// Audit report for a compliance framework and time period.
/// </summary>
/// <param name="ReportId">Unique identifier for this report</param>
/// <param name="Framework">Framework this report covers</param>
/// <param name="Start">Start of reporting period</param>
/// <param name="End">End of reporting period</param>
/// <param name="TotalEvents">Total number of events in period</param>
/// <param name="Summary">Summary of events by type</param>
public record AuditReport(
    string ReportId,
    ComplianceFramework Framework,
    DateTimeOffset Start,
    DateTimeOffset End,
    int TotalEvents,
    IReadOnlyList<AuditSummaryEntry> Summary
);

/// <summary>
/// Summary entry for audit report.
/// </summary>
/// <param name="EventType">Type of events</param>
/// <param name="Count">Number of events of this type</param>
/// <param name="UniqueActors">Unique actors who performed this event type</param>
public record AuditSummaryEntry(string EventType, int Count, string[] UniqueActors);
