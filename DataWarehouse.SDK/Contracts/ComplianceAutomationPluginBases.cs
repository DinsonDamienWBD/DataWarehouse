using DataWarehouse.SDK.Compliance;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts;

/// <summary>
/// Base class for compliance automation plugin implementations.
/// Provides framework orchestration, control execution, and remediation coordination.
/// Implementations must specify supported framework and provide control definitions.
/// </summary>
public abstract class ComplianceAutomationPluginBase : FeaturePluginBase, IComplianceAutomation
{
    /// <summary>
    /// The compliance framework this plugin supports.
    /// Each plugin implementation handles one framework (GDPR, HIPAA, PCI-DSS, etc.).
    /// </summary>
    public abstract ComplianceFramework SupportedFramework { get; }

    /// <summary>
    /// Loads all control definitions for the supported framework.
    /// Called during compliance check execution to enumerate controls.
    /// </summary>
    /// <returns>Complete list of controls for the framework</returns>
    protected abstract Task<IReadOnlyList<AutomationControl>> LoadControlsAsync();

    /// <summary>
    /// Executes validation logic for a specific control.
    /// Implementations should check system state against control requirements.
    /// </summary>
    /// <param name="control">Control to validate</param>
    /// <returns>Check result with status and findings</returns>
    protected abstract Task<AutomationCheckResult> ExecuteControlCheckAsync(AutomationControl control);

    /// <summary>
    /// Executes remediation actions for a failed control check.
    /// Implementations should apply fixes to bring system into compliance.
    /// </summary>
    /// <param name="checkId">Identifier of the check that failed</param>
    /// <param name="options">Remediation behavior options</param>
    /// <returns>Result of remediation attempt</returns>
    protected abstract Task<RemediationResult> ExecuteRemediationAsync(string checkId, RemediationOptions options);

    /// <summary>
    /// Runs comprehensive compliance check for the framework.
    /// Orchestrates control loading, execution, and optional auto-remediation.
    /// </summary>
    /// <param name="framework">Framework to check (must match SupportedFramework)</param>
    /// <param name="options">Check execution options</param>
    /// <returns>Compliance report with all control results</returns>
    /// <exception cref="ArgumentException">Thrown if framework doesn't match SupportedFramework</exception>
    public async Task<ComplianceAutomationReport> RunComplianceCheckAsync(ComplianceFramework framework, AutomationCheckOptions? options = null)
    {
        if (framework != SupportedFramework)
            throw new ArgumentException($"This plugin supports {SupportedFramework}, not {framework}");

        // Load control definitions
        var controls = await LoadControlsAsync();

        // Filter to specific controls if requested
        var targetControls = options?.SpecificControls != null
            ? controls.Where(c => options.SpecificControls.Contains(c.ControlId)).ToList()
            : controls.ToList();

        // Execute each control check
        var results = new List<AutomationCheckResult>();
        foreach (var control in targetControls)
        {
            var result = await ExecuteControlCheckAsync(control);
            results.Add(result);

            // Auto-remediate if enabled and control failed
            if (options?.AutoRemediate == true && result.Status == AutomationComplianceStatus.NonCompliant)
            {
                await ExecuteRemediationAsync(result.CheckId, new RemediationOptions());
            }
        }

        // Calculate overall compliance status
        var compliant = results.Count(r => r.Status == AutomationComplianceStatus.Compliant);
        var nonCompliant = results.Count(r => r.Status == AutomationComplianceStatus.NonCompliant);
        var overall = nonCompliant == 0 ? AutomationComplianceStatus.Compliant :
                      compliant == 0 ? AutomationComplianceStatus.NonCompliant : AutomationComplianceStatus.PartiallyCompliant;

        // Generate report
        return new ComplianceAutomationReport(
            $"CR-{Guid.NewGuid():N}",
            framework,
            overall,
            results.Count,
            compliant,
            nonCompliant,
            results,
            DateTimeOffset.UtcNow
        );
    }

    /// <summary>
    /// Checks a specific control within the framework.
    /// </summary>
    /// <param name="framework">Framework containing the control</param>
    /// <param name="controlId">Control identifier to check</param>
    /// <returns>Check result for the control</returns>
    /// <exception cref="ArgumentException">Thrown if control not found</exception>
    public Task<AutomationCheckResult> CheckControlAsync(ComplianceFramework framework, string controlId)
        => Task.FromResult(GetControls().FirstOrDefault(c => c.ControlId == controlId))
            .ContinueWith(t => t.Result != null
                ? ExecuteControlCheckAsync(t.Result)
                : throw new ArgumentException($"Control {controlId} not found"))
            .Unwrap();

    /// <summary>
    /// Gets all controls for the framework.
    /// </summary>
    /// <param name="framework">Framework to get controls for</param>
    /// <returns>List of compliance controls</returns>
    public Task<IReadOnlyList<AutomationControl>> GetControlsAsync(ComplianceFramework framework)
        => LoadControlsAsync();

    /// <summary>
    /// Remediates findings from a compliance check.
    /// </summary>
    /// <param name="checkId">Check identifier with findings</param>
    /// <param name="options">Remediation options</param>
    /// <returns>Remediation result</returns>
    public Task<RemediationResult> RemediateAsync(string checkId, RemediationOptions options)
        => ExecuteRemediationAsync(checkId, options);

    /// <summary>
    /// Gets cached control definitions.
    /// Override to provide framework-specific control catalog.
    /// </summary>
    /// <returns>Control definitions</returns>
    protected abstract IReadOnlyList<AutomationControl> GetControls();

    /// <summary>
    /// Starts the compliance automation plugin.
    /// Override to provide custom initialization logic.
    /// </summary>
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Stops the compliance automation plugin.
    /// Override to provide custom cleanup logic.
    /// </summary>
    public override Task StopAsync() => Task.CompletedTask;

    /// <summary>
    /// Provides plugin metadata for discovery and reporting.
    /// </summary>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "ComplianceAutomation";
        metadata["Framework"] = SupportedFramework.ToString();
        metadata["SupportsAutoRemediation"] = true;
        metadata["SupportsEvidenceGeneration"] = true;
        return metadata;
    }
}

/// <summary>
/// Base class for Data Subject Rights plugin implementations.
/// Provides GDPR/CCPA/LGPD/PIPEDA/PDPA rights automation.
/// Implementations must provide data collection, deletion, and consent management.
/// </summary>
public abstract class DataSubjectRightsPluginBase : FeaturePluginBase, IDataSubjectRights
{
    /// <summary>
    /// Collects all personal data for a data subject.
    /// Implementations should query all data stores and aggregate subject data.
    /// </summary>
    /// <param name="subjectId">Data subject identifier</param>
    /// <param name="options">Export configuration</param>
    /// <returns>Collected data as bytes</returns>
    protected abstract Task<byte[]> CollectSubjectDataAsync(string subjectId, DataExportOptions options);

    /// <summary>
    /// Deletes all personal data for a data subject.
    /// Implementations should cascade delete across all data stores.
    /// </summary>
    /// <param name="subjectId">Data subject identifier</param>
    /// <param name="options">Deletion configuration</param>
    /// <returns>Number of records deleted</returns>
    protected abstract Task<int> DeleteSubjectDataAsync(string subjectId, DeletionOptions options);

    /// <summary>
    /// Updates personal data for a data subject.
    /// Implementations should apply corrections to all instances of subject data.
    /// </summary>
    /// <param name="subjectId">Data subject identifier</param>
    /// <param name="corrections">Fields to update with new values</param>
    /// <returns>Number of fields corrected</returns>
    protected abstract Task<int> UpdateSubjectDataAsync(string subjectId, Dictionary<string, object> corrections);

    /// <summary>
    /// Sets or removes processing restriction for a data subject.
    /// Implementations should mark subject data as restricted and prevent processing.
    /// </summary>
    /// <param name="subjectId">Data subject identifier</param>
    /// <param name="restricted">True to restrict, false to remove restriction</param>
    /// <param name="reason">Reason for restriction</param>
    protected abstract Task SetProcessingRestrictionAsync(string subjectId, bool restricted, string reason);

    /// <summary>
    /// Stores a consent record.
    /// Implementations should persist consent with immutable audit trail.
    /// </summary>
    /// <param name="record">Consent record to store</param>
    protected abstract Task StoreConsentAsync(ConsentRecord record);

    /// <summary>
    /// Exports all data for a data subject (Right to Access - GDPR Art. 15).
    /// </summary>
    /// <param name="subjectId">Data subject identifier</param>
    /// <param name="options">Export options</param>
    /// <returns>Data export package</returns>
    public async Task<DataExport> ExportDataAsync(string subjectId, DataExportOptions options)
    {
        var data = await CollectSubjectDataAsync(subjectId, options);
        return new DataExport($"EXP-{Guid.NewGuid():N}", subjectId, data, options.Format, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Deletes all data for a data subject (Right to Erasure - GDPR Art. 17).
    /// </summary>
    /// <param name="subjectId">Data subject identifier</param>
    /// <param name="options">Deletion options</param>
    /// <returns>Deletion result with certificate</returns>
    public async Task<DeletionResult> DeleteDataAsync(string subjectId, DeletionOptions options)
    {
        var deleted = await DeleteSubjectDataAsync(subjectId, options);
        var certId = options.CreateCertificate ? $"DEL-{Guid.NewGuid():N}" : null;
        return new DeletionResult(deleted > 0, deleted, certId, Array.Empty<string>());
    }

    /// <summary>
    /// Corrects inaccurate data for a data subject (Right to Rectification - GDPR Art. 16).
    /// </summary>
    /// <param name="subjectId">Data subject identifier</param>
    /// <param name="corrections">Fields to correct</param>
    /// <returns>Rectification result</returns>
    public async Task<RectificationResult> RectifyDataAsync(string subjectId, Dictionary<string, object> corrections)
    {
        var corrected = await UpdateSubjectDataAsync(subjectId, corrections);
        return new RectificationResult(corrected > 0, corrected, Array.Empty<string>());
    }

    /// <summary>
    /// Exports data in portable format (Right to Portability - GDPR Art. 20).
    /// </summary>
    /// <param name="subjectId">Data subject identifier</param>
    /// <param name="format">Export format (json, xml, csv)</param>
    /// <returns>Portable data export</returns>
    public Task<DataExport> ExportPortableAsync(string subjectId, string format = "json")
        => ExportDataAsync(subjectId, new DataExportOptions(Array.Empty<string>(), format, true));

    /// <summary>
    /// Restricts processing for a data subject (Right to Restriction - GDPR Art. 18).
    /// </summary>
    /// <param name="subjectId">Data subject identifier</param>
    /// <param name="reason">Reason for restriction</param>
    public Task RestrictProcessingAsync(string subjectId, string reason)
        => SetProcessingRestrictionAsync(subjectId, true, reason);

    /// <summary>
    /// Records consent for data processing (GDPR lawful basis).
    /// </summary>
    /// <param name="subjectId">Data subject identifier</param>
    /// <param name="consent">Consent details</param>
    /// <returns>Stored consent record</returns>
    public async Task<ConsentRecord> RecordConsentAsync(string subjectId, ConsentDetails consent)
    {
        var record = new ConsentRecord($"CON-{Guid.NewGuid():N}", subjectId, consent, DateTimeOffset.UtcNow);
        await StoreConsentAsync(record);
        return record;
    }

    /// <summary>
    /// Starts the data subject rights plugin.
    /// Override to provide custom initialization logic.
    /// </summary>
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Stops the data subject rights plugin.
    /// Override to provide custom cleanup logic.
    /// </summary>
    public override Task StopAsync() => Task.CompletedTask;

    /// <summary>
    /// Provides plugin metadata for discovery and reporting.
    /// </summary>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "DataSubjectRights";
        metadata["SupportedFrameworks"] = "GDPR,CCPA,LGPD,PIPEDA,PDPA";
        metadata["SupportsRightToAccess"] = true;
        metadata["SupportsRightToErasure"] = true;
        metadata["SupportsRightToRectification"] = true;
        metadata["SupportsRightToPortability"] = true;
        metadata["SupportsRightToRestriction"] = true;
        return metadata;
    }
}

/// <summary>
/// Base class for compliance audit plugin implementations.
/// Provides immutable audit trail for regulatory evidence.
/// Implementations must provide audit storage and querying.
/// </summary>
public abstract class ComplianceAuditPluginBase : FeaturePluginBase, IComplianceAudit
{
    /// <summary>
    /// Stores an audit event.
    /// Implementations should ensure immutability and integrity protection.
    /// </summary>
    /// <param name="evt">Event to store</param>
    protected abstract Task StoreEventAsync(ComplianceAuditEvent evt);

    /// <summary>
    /// Queries audit events with filtering.
    /// Implementations should support efficient time-range and field queries.
    /// </summary>
    /// <param name="query">Query criteria</param>
    /// <returns>Matching audit events</returns>
    protected abstract Task<IReadOnlyList<ComplianceAuditEvent>> QueryEventsAsync(AuditQuery query);

    /// <summary>
    /// Generates an audit report for a framework and time period.
    /// Implementations should aggregate events and produce summary statistics.
    /// </summary>
    /// <param name="framework">Framework to report on</param>
    /// <param name="start">Start of reporting period</param>
    /// <param name="end">End of reporting period</param>
    /// <returns>Audit report with summary</returns>
    protected abstract Task<AuditReport> GenerateReportAsync(ComplianceFramework framework, DateTimeOffset start, DateTimeOffset end);

    /// <summary>
    /// Logs a compliance audit event.
    /// </summary>
    /// <param name="evt">Event to log</param>
    public Task LogEventAsync(ComplianceAuditEvent evt) => StoreEventAsync(evt);

    /// <summary>
    /// Retrieves audit trail with filtering.
    /// </summary>
    /// <param name="query">Query criteria</param>
    /// <returns>Matching events</returns>
    public Task<IReadOnlyList<ComplianceAuditEvent>> GetAuditTrailAsync(AuditQuery query)
        => QueryEventsAsync(query);

    /// <summary>
    /// Generates compliance audit report.
    /// </summary>
    /// <param name="framework">Framework to report on</param>
    /// <param name="start">Start date</param>
    /// <param name="end">End date</param>
    /// <returns>Audit report</returns>
    public Task<AuditReport> GenerateAuditReportAsync(ComplianceFramework framework, DateTimeOffset start, DateTimeOffset end)
        => GenerateReportAsync(framework, start, end);

    /// <summary>
    /// Starts the compliance audit plugin.
    /// Override to provide custom initialization logic.
    /// </summary>
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Stops the compliance audit plugin.
    /// Override to provide custom cleanup logic.
    /// </summary>
    public override Task StopAsync() => Task.CompletedTask;

    /// <summary>
    /// Provides plugin metadata for discovery and reporting.
    /// </summary>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "ComplianceAudit";
        metadata["SupportsImmutableAuditTrail"] = true;
        metadata["SupportsTimeRangeQueries"] = true;
        metadata["SupportsFrameworkReporting"] = true;
        return metadata;
    }
}
