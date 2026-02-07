using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Compliance
{
    /// <summary>
    /// Supported compliance frameworks and regulations.
    /// Each framework has specific controls, requirements, and certification standards.
    /// </summary>
    public enum ComplianceFramework
    {
        /// <summary>
        /// General Data Protection Regulation (European Union).
        /// Privacy and data protection for EU residents.
        /// </summary>
        GDPR = 0,

        /// <summary>
        /// Health Insurance Portability and Accountability Act (United States).
        /// Security and privacy for protected health information (PHI).
        /// </summary>
        HIPAA = 1,

        /// <summary>
        /// Sarbanes-Oxley Act (United States).
        /// Financial record retention and accuracy for public companies.
        /// </summary>
        SOX = 2,

        /// <summary>
        /// Payment Card Industry Data Security Standard.
        /// Security requirements for organizations handling credit card data.
        /// </summary>
        PCIDSS = 3,

        /// <summary>
        /// Federal Risk and Authorization Management Program (United States).
        /// Cloud security for federal government agencies.
        /// </summary>
        FedRAMP = 4,

        /// <summary>
        /// Service Organization Control 2.
        /// Security, availability, processing integrity, confidentiality, and privacy controls.
        /// </summary>
        SOC2 = 5,

        /// <summary>
        /// ISO/IEC 27001 Information Security Management System.
        /// International standard for information security.
        /// </summary>
        ISO27001 = 6,

        /// <summary>
        /// California Consumer Privacy Act (United States).
        /// Privacy rights for California residents.
        /// </summary>
        CCPA = 7
    }

    /// <summary>
    /// Control severity levels for compliance violations.
    /// Determines the urgency and impact of remediation.
    /// </summary>
    public enum ComplianceSeverity
    {
        /// <summary>
        /// Informational finding with no compliance impact.
        /// </summary>
        Info = 0,

        /// <summary>
        /// Low-severity issue with minimal compliance risk.
        /// Should be addressed in routine maintenance.
        /// </summary>
        Low = 1,

        /// <summary>
        /// Medium-severity issue with moderate compliance risk.
        /// Should be addressed within defined SLA.
        /// </summary>
        Medium = 2,

        /// <summary>
        /// High-severity issue with significant compliance risk.
        /// Requires prompt remediation to maintain certification.
        /// </summary>
        High = 3,

        /// <summary>
        /// Critical issue with severe compliance risk.
        /// Immediate remediation required, may block certification.
        /// </summary>
        Critical = 4
    }

    /// <summary>
    /// Control categories for compliance requirements.
    /// Groups related controls by functional area.
    /// </summary>
    public enum ComplianceControlCategory
    {
        /// <summary>
        /// Access control and authorization (who can access what).
        /// </summary>
        AccessControl = 0,

        /// <summary>
        /// Data encryption (at rest and in transit).
        /// </summary>
        Encryption = 1,

        /// <summary>
        /// Audit logging and monitoring.
        /// </summary>
        AuditLogging = 2,

        /// <summary>
        /// Data retention and disposal policies.
        /// </summary>
        DataRetention = 3,

        /// <summary>
        /// Incident response and breach notification.
        /// </summary>
        IncidentResponse = 4,

        /// <summary>
        /// Business continuity and disaster recovery.
        /// </summary>
        BusinessContinuity = 5,

        /// <summary>
        /// Data residency and sovereignty (geographic restrictions).
        /// </summary>
        DataResidency = 6,

        /// <summary>
        /// Privacy and consent management.
        /// </summary>
        Privacy = 7,

        /// <summary>
        /// Vulnerability management and patching.
        /// </summary>
        VulnerabilityManagement = 8,

        /// <summary>
        /// Physical and environmental security.
        /// </summary>
        PhysicalSecurity = 9
    }

    /// <summary>
    /// Defines a compliance control requirement.
    /// Immutable record for control specifications.
    /// </summary>
    public sealed record ComplianceControl
    {
        /// <summary>
        /// Gets the unique control identifier.
        /// Example: "GDPR-Art.32", "HIPAA-164.312(a)(1)", "PCI-DSS-3.4".
        /// </summary>
        public required string ControlId { get; init; }

        /// <summary>
        /// Gets the control description and requirements.
        /// </summary>
        public required string Description { get; init; }

        /// <summary>
        /// Gets the control category.
        /// </summary>
        public required ComplianceControlCategory Category { get; init; }

        /// <summary>
        /// Gets the severity level for violations of this control.
        /// </summary>
        public required ComplianceSeverity Severity { get; init; }

        /// <summary>
        /// Gets a value indicating whether this control can be automated.
        /// True = can be validated programmatically, False = requires manual review.
        /// </summary>
        public required bool IsAutomated { get; init; }

        /// <summary>
        /// Gets the compliance framework this control belongs to.
        /// </summary>
        public required ComplianceFramework Framework { get; init; }

        /// <summary>
        /// Gets validation criteria for automated assessment.
        /// Key-value pairs defining what to check.
        /// </summary>
        public IReadOnlyDictionary<string, object>? ValidationCriteria { get; init; }

        /// <summary>
        /// Gets evidence collection requirements.
        /// Specifies what evidence must be collected to demonstrate compliance.
        /// </summary>
        public IReadOnlyList<string>? EvidenceRequirements { get; init; }

        /// <summary>
        /// Gets remediation guidance for violations.
        /// </summary>
        public string? RemediationGuidance { get; init; }

        /// <summary>
        /// Gets related control identifiers (cross-references).
        /// </summary>
        public IReadOnlyList<string>? RelatedControls { get; init; }

        /// <summary>
        /// Gets additional metadata about this control.
        /// </summary>
        public IReadOnlyDictionary<string, string>? Metadata { get; init; }
    }

    /// <summary>
    /// Records a compliance violation found during assessment.
    /// Immutable record for audit trails and remediation tracking.
    /// </summary>
    public sealed record ComplianceViolation
    {
        /// <summary>
        /// Gets the control identifier that was violated.
        /// </summary>
        public required string ControlId { get; init; }

        /// <summary>
        /// Gets the severity of this violation.
        /// </summary>
        public required ComplianceSeverity Severity { get; init; }

        /// <summary>
        /// Gets the detailed description of the violation.
        /// </summary>
        public required string Details { get; init; }

        /// <summary>
        /// Gets the resource or component where the violation was found.
        /// </summary>
        public string? Resource { get; init; }

        /// <summary>
        /// Gets the recommended remediation steps.
        /// </summary>
        public required IReadOnlyList<string> RemediationSteps { get; init; }

        /// <summary>
        /// Gets supporting evidence for this violation.
        /// Can include configuration values, log entries, screenshots.
        /// </summary>
        public IReadOnlyDictionary<string, object>? Evidence { get; init; }

        /// <summary>
        /// Gets the timestamp when this violation was detected.
        /// </summary>
        public DateTime DetectedAt { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Gets the deadline for remediation based on severity.
        /// </summary>
        public DateTime? RemediationDeadline { get; init; }

        /// <summary>
        /// Gets additional metadata about this violation.
        /// </summary>
        public IReadOnlyDictionary<string, string>? Metadata { get; init; }
    }

    /// <summary>
    /// Compliance requirements for a specific framework.
    /// Defines what controls must be implemented and validated.
    /// </summary>
    public sealed record ComplianceRequirements
    {
        /// <summary>
        /// Gets the compliance framework these requirements apply to.
        /// </summary>
        public required ComplianceFramework Framework { get; init; }

        /// <summary>
        /// Gets the list of controls that must be implemented.
        /// </summary>
        public required IReadOnlyList<ComplianceControl> Controls { get; init; }

        /// <summary>
        /// Gets data residency requirements (geographic restrictions).
        /// Key = region/country code, Value = allowed/denied.
        /// Example: { "EU": true, "US": false } for GDPR compliance.
        /// </summary>
        public IReadOnlyDictionary<string, bool>? ResidencyRequirements { get; init; }

        /// <summary>
        /// Gets data retention requirements.
        /// Minimum and maximum retention periods for different data types.
        /// </summary>
        public IReadOnlyDictionary<string, TimeSpan>? RetentionRequirements { get; init; }

        /// <summary>
        /// Gets required encryption standards.
        /// Key = data state (at-rest, in-transit), Value = minimum encryption strength.
        /// </summary>
        public IReadOnlyDictionary<string, string>? EncryptionRequirements { get; init; }

        /// <summary>
        /// Gets audit log retention period.
        /// How long audit logs must be retained for compliance.
        /// </summary>
        public TimeSpan? AuditLogRetention { get; init; }

        /// <summary>
        /// Gets certification validity period.
        /// How often compliance must be re-assessed.
        /// </summary>
        public TimeSpan? CertificationValidity { get; init; }

        /// <summary>
        /// Gets additional framework-specific requirements.
        /// </summary>
        public IReadOnlyDictionary<string, object>? AdditionalRequirements { get; init; }
    }

    /// <summary>
    /// Result of a compliance assessment.
    /// Contains overall compliance status and detailed findings.
    /// </summary>
    public sealed record ComplianceAssessmentResult
    {
        /// <summary>
        /// Gets the compliance framework that was assessed.
        /// </summary>
        public required ComplianceFramework Framework { get; init; }

        /// <summary>
        /// Gets a value indicating whether the system is compliant.
        /// True if no violations were found, false otherwise.
        /// </summary>
        public required bool IsCompliant { get; init; }

        /// <summary>
        /// Gets the compliance score (0.0 to 1.0).
        /// 1.0 = fully compliant, 0.0 = no controls met.
        /// </summary>
        public required double ComplianceScore { get; init; }

        /// <summary>
        /// Gets the list of violations found during assessment.
        /// Empty if fully compliant.
        /// </summary>
        public required IReadOnlyList<ComplianceViolation> Violations { get; init; }

        /// <summary>
        /// Gets the list of controls that passed assessment.
        /// </summary>
        public IReadOnlyList<string>? PassedControls { get; init; }

        /// <summary>
        /// Gets the timestamp when this assessment was performed.
        /// </summary>
        public DateTime AssessmentTime { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Gets the next assessment due date.
        /// </summary>
        public DateTime? NextAssessmentDue { get; init; }

        /// <summary>
        /// Gets the assessment method used.
        /// Examples: "automated", "manual", "hybrid".
        /// </summary>
        public string? AssessmentMethod { get; init; }

        /// <summary>
        /// Gets the assessor or tool that performed the assessment.
        /// </summary>
        public string? AssessorId { get; init; }

        /// <summary>
        /// Gets collected evidence demonstrating compliance.
        /// Key = control ID, Value = evidence artifacts.
        /// </summary>
        public IReadOnlyDictionary<string, object>? Evidence { get; init; }

        /// <summary>
        /// Gets additional metadata about this assessment.
        /// </summary>
        public IReadOnlyDictionary<string, string>? Metadata { get; init; }

        /// <summary>
        /// Gets summary statistics by severity.
        /// </summary>
        public IReadOnlyDictionary<ComplianceSeverity, int> GetViolationsBySeverity()
        {
            return Violations
                .GroupBy(v => v.Severity)
                .ToDictionary(g => g.Key, g => g.Count());
        }

        /// <summary>
        /// Gets summary statistics by category.
        /// </summary>
        public IReadOnlyDictionary<ComplianceControlCategory, int> GetViolationsByCategory(
            IReadOnlyList<ComplianceControl> allControls)
        {
            var controlMap = allControls.ToDictionary(c => c.ControlId);
            return Violations
                .Where(v => controlMap.ContainsKey(v.ControlId))
                .GroupBy(v => controlMap[v.ControlId].Category)
                .ToDictionary(g => g.Key, g => g.Count());
        }
    }

    /// <summary>
    /// Common interface for compliance strategy implementations.
    /// Compliance strategies assess adherence to regulatory frameworks.
    /// </summary>
    public interface IComplianceStrategy
    {
        /// <summary>
        /// Gets the unique identifier for this compliance strategy.
        /// Example: "gdpr-assessor", "hipaa-validator", "multi-framework".
        /// </summary>
        string StrategyId { get; }

        /// <summary>
        /// Gets the human-readable name of this compliance strategy.
        /// </summary>
        string StrategyName { get; }

        /// <summary>
        /// Gets the compliance frameworks supported by this strategy.
        /// </summary>
        IReadOnlyList<ComplianceFramework> SupportedFrameworks { get; }

        /// <summary>
        /// Gets the compliance requirements for a specific framework.
        /// </summary>
        /// <param name="framework">The compliance framework.</param>
        /// <returns>The compliance requirements.</returns>
        /// <exception cref="ArgumentException">If framework is not supported.</exception>
        ComplianceRequirements GetRequirements(ComplianceFramework framework);

        /// <summary>
        /// Performs a compliance assessment for a specific framework.
        /// </summary>
        /// <param name="framework">The framework to assess against.</param>
        /// <param name="targetContext">Context about what is being assessed (system, tenant, resource).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Assessment results with violations and evidence.</returns>
        /// <exception cref="ArgumentException">If framework is not supported.</exception>
        /// <exception cref="ComplianceException">If assessment fails.</exception>
        Task<ComplianceAssessmentResult> AssessAsync(
            ComplianceFramework framework,
            IReadOnlyDictionary<string, object> targetContext,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Assesses a specific control within a framework.
        /// </summary>
        /// <param name="controlId">The control identifier to assess.</param>
        /// <param name="targetContext">Context about what is being assessed.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>True if control is satisfied, false if violated.</returns>
        /// <exception cref="ArgumentException">If control is not found.</exception>
        /// <exception cref="ComplianceException">If assessment fails.</exception>
        Task<bool> AssessControlAsync(
            string controlId,
            IReadOnlyDictionary<string, object> targetContext,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Collects evidence for compliance audits.
        /// </summary>
        /// <param name="framework">The compliance framework.</param>
        /// <param name="targetContext">Context about what is being assessed.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Collected evidence artifacts keyed by control ID.</returns>
        /// <exception cref="ComplianceException">If evidence collection fails.</exception>
        Task<IReadOnlyDictionary<string, object>> CollectEvidenceAsync(
            ComplianceFramework framework,
            IReadOnlyDictionary<string, object> targetContext,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets statistics about compliance assessments performed.
        /// </summary>
        /// <returns>Compliance assessment statistics.</returns>
        ComplianceStatistics GetStatistics();

        /// <summary>
        /// Resets statistics counters.
        /// </summary>
        void ResetStatistics();
    }

    /// <summary>
    /// Statistics tracking for compliance assessments.
    /// Used for monitoring, reporting, and trend analysis.
    /// </summary>
    public sealed class ComplianceStatistics
    {
        /// <summary>
        /// Gets or sets the total number of assessments performed.
        /// </summary>
        public long TotalAssessments { get; init; }

        /// <summary>
        /// Gets or sets the number of compliant assessments.
        /// </summary>
        public long CompliantCount { get; init; }

        /// <summary>
        /// Gets or sets the number of non-compliant assessments.
        /// </summary>
        public long NonCompliantCount { get; init; }

        /// <summary>
        /// Gets or sets assessment counts by framework.
        /// </summary>
        public IReadOnlyDictionary<ComplianceFramework, long>? AssessmentsByFramework { get; init; }

        /// <summary>
        /// Gets or sets total violations detected across all assessments.
        /// </summary>
        public long TotalViolations { get; init; }

        /// <summary>
        /// Gets or sets violation counts by severity.
        /// </summary>
        public IReadOnlyDictionary<ComplianceSeverity, long>? ViolationsBySeverity { get; init; }

        /// <summary>
        /// Gets or sets the number of assessment errors.
        /// </summary>
        public long ErrorCount { get; init; }

        /// <summary>
        /// Gets or sets the average assessment time in milliseconds.
        /// </summary>
        public double AverageAssessmentTimeMs { get; init; }

        /// <summary>
        /// Gets or sets the average compliance score (0.0 to 1.0).
        /// </summary>
        public double AverageComplianceScore { get; init; }

        /// <summary>
        /// Gets or sets the timestamp when statistics tracking started.
        /// </summary>
        public DateTime StartTime { get; init; }

        /// <summary>
        /// Gets or sets the timestamp of the last statistic update.
        /// </summary>
        public DateTime LastUpdateTime { get; init; }

        /// <summary>
        /// Gets the compliance rate (compliant / total assessments).
        /// </summary>
        public double ComplianceRate => TotalAssessments > 0 ? (double)CompliantCount / TotalAssessments : 0;

        /// <summary>
        /// Gets the error rate (errors / total assessments).
        /// </summary>
        public double ErrorRate => TotalAssessments > 0 ? (double)ErrorCount / TotalAssessments : 0;

        /// <summary>
        /// Creates an empty statistics object.
        /// </summary>
        public static ComplianceStatistics Empty => new()
        {
            StartTime = DateTime.UtcNow,
            LastUpdateTime = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Exception thrown when compliance assessment fails.
    /// </summary>
    public sealed class ComplianceException : Exception
    {
        /// <summary>
        /// Gets the compliance framework where the failure occurred.
        /// </summary>
        public ComplianceFramework? Framework { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="ComplianceException"/> class.
        /// </summary>
        /// <param name="message">The error message.</param>
        public ComplianceException(string message) : base(message) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="ComplianceException"/> class.
        /// </summary>
        /// <param name="message">The error message.</param>
        /// <param name="innerException">The inner exception.</param>
        public ComplianceException(string message, Exception innerException) : base(message, innerException) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="ComplianceException"/> class.
        /// </summary>
        /// <param name="framework">The framework where the failure occurred.</param>
        /// <param name="message">The error message.</param>
        public ComplianceException(ComplianceFramework framework, string message) : base(message)
        {
            Framework = framework;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ComplianceException"/> class.
        /// </summary>
        /// <param name="framework">The framework where the failure occurred.</param>
        /// <param name="message">The error message.</param>
        /// <param name="innerException">The inner exception.</param>
        public ComplianceException(ComplianceFramework framework, string message, Exception innerException)
            : base(message, innerException)
        {
            Framework = framework;
        }
    }

    /// <summary>
    /// Abstract base class for compliance strategy implementations.
    /// Provides common functionality for control assessment, evidence collection, and violation detection.
    /// Thread-safe for concurrent operations.
    /// </summary>
    public abstract class ComplianceStrategyBase : IComplianceStrategy
    {
        private long _totalAssessments;
        private long _compliantCount;
        private long _nonCompliantCount;
        private long _totalViolations;
        private long _errorCount;
        private double _totalAssessmentTimeMs;
        private double _totalComplianceScore;
        private readonly DateTime _startTime;
        private DateTime _lastUpdateTime;
        private readonly object _statsLock = new();
        private readonly Dictionary<ComplianceFramework, long> _assessmentsByFramework = new();
        private readonly Dictionary<ComplianceSeverity, long> _violationsBySeverity = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="ComplianceStrategyBase"/> class.
        /// </summary>
        protected ComplianceStrategyBase()
        {
            _startTime = DateTime.UtcNow;
            _lastUpdateTime = DateTime.UtcNow;

            // Initialize framework counters
            foreach (ComplianceFramework framework in Enum.GetValues(typeof(ComplianceFramework)))
            {
                _assessmentsByFramework[framework] = 0;
            }

            // Initialize severity counters
            foreach (ComplianceSeverity severity in Enum.GetValues(typeof(ComplianceSeverity)))
            {
                _violationsBySeverity[severity] = 0;
            }
        }

        /// <inheritdoc/>
        public abstract string StrategyId { get; }

        /// <inheritdoc/>
        public abstract string StrategyName { get; }

        /// <inheritdoc/>
        public abstract IReadOnlyList<ComplianceFramework> SupportedFrameworks { get; }

        /// <inheritdoc/>
        public abstract ComplianceRequirements GetRequirements(ComplianceFramework framework);

        /// <inheritdoc/>
        public async Task<ComplianceAssessmentResult> AssessAsync(
            ComplianceFramework framework,
            IReadOnlyDictionary<string, object> targetContext,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(targetContext);

            if (!SupportedFrameworks.Contains(framework))
            {
                throw new ArgumentException(
                    $"Framework {framework} is not supported by strategy {StrategyId}",
                    nameof(framework));
            }

            var startTime = DateTime.UtcNow;
            try
            {
                var result = await AssessCoreAsync(framework, targetContext, cancellationToken).ConfigureAwait(false);

                // Update statistics
                var assessmentTime = (DateTime.UtcNow - startTime).TotalMilliseconds;
                UpdateStatistics(framework, result, assessmentTime);

                return result;
            }
            catch (Exception ex)
            {
                IncrementErrorCount();
                throw new ComplianceException(framework, "Compliance assessment failed", ex);
            }
        }

        /// <inheritdoc/>
        public async Task<bool> AssessControlAsync(
            string controlId,
            IReadOnlyDictionary<string, object> targetContext,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(controlId);
            ArgumentNullException.ThrowIfNull(targetContext);

            try
            {
                return await AssessControlCoreAsync(controlId, targetContext, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                IncrementErrorCount();
                throw new ComplianceException($"Control assessment failed for {controlId}", ex);
            }
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyDictionary<string, object>> CollectEvidenceAsync(
            ComplianceFramework framework,
            IReadOnlyDictionary<string, object> targetContext,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(targetContext);

            if (!SupportedFrameworks.Contains(framework))
            {
                throw new ArgumentException(
                    $"Framework {framework} is not supported by strategy {StrategyId}",
                    nameof(framework));
            }

            try
            {
                return await CollectEvidenceCoreAsync(framework, targetContext, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new ComplianceException(framework, "Evidence collection failed", ex);
            }
        }

        /// <inheritdoc/>
        public ComplianceStatistics GetStatistics()
        {
            lock (_statsLock)
            {
                var totalAssessments = Interlocked.Read(ref _totalAssessments);

                return new ComplianceStatistics
                {
                    TotalAssessments = totalAssessments,
                    CompliantCount = Interlocked.Read(ref _compliantCount),
                    NonCompliantCount = Interlocked.Read(ref _nonCompliantCount),
                    TotalViolations = Interlocked.Read(ref _totalViolations),
                    ErrorCount = Interlocked.Read(ref _errorCount),
                    AverageAssessmentTimeMs = totalAssessments > 0 ? _totalAssessmentTimeMs / totalAssessments : 0,
                    AverageComplianceScore = totalAssessments > 0 ? _totalComplianceScore / totalAssessments : 0,
                    AssessmentsByFramework = new Dictionary<ComplianceFramework, long>(_assessmentsByFramework),
                    ViolationsBySeverity = new Dictionary<ComplianceSeverity, long>(_violationsBySeverity),
                    StartTime = _startTime,
                    LastUpdateTime = _lastUpdateTime
                };
            }
        }

        /// <inheritdoc/>
        public void ResetStatistics()
        {
            lock (_statsLock)
            {
                Interlocked.Exchange(ref _totalAssessments, 0);
                Interlocked.Exchange(ref _compliantCount, 0);
                Interlocked.Exchange(ref _nonCompliantCount, 0);
                Interlocked.Exchange(ref _totalViolations, 0);
                Interlocked.Exchange(ref _errorCount, 0);
                _totalAssessmentTimeMs = 0;
                _totalComplianceScore = 0;
                _lastUpdateTime = DateTime.UtcNow;

                foreach (var framework in _assessmentsByFramework.Keys.ToList())
                {
                    _assessmentsByFramework[framework] = 0;
                }

                foreach (var severity in _violationsBySeverity.Keys.ToList())
                {
                    _violationsBySeverity[severity] = 0;
                }
            }
        }

        /// <summary>
        /// Core assessment implementation to be provided by derived classes.
        /// Assesses all controls for the specified framework.
        /// </summary>
        /// <param name="framework">The compliance framework to assess.</param>
        /// <param name="targetContext">Context about what is being assessed.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Assessment results with violations and evidence.</returns>
        protected abstract Task<ComplianceAssessmentResult> AssessCoreAsync(
            ComplianceFramework framework,
            IReadOnlyDictionary<string, object> targetContext,
            CancellationToken cancellationToken);

        /// <summary>
        /// Core control assessment implementation.
        /// Default implementation is not provided - derived classes must implement.
        /// </summary>
        /// <param name="controlId">The control identifier to assess.</param>
        /// <param name="targetContext">Context about what is being assessed.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>True if control is satisfied, false if violated.</returns>
        protected abstract Task<bool> AssessControlCoreAsync(
            string controlId,
            IReadOnlyDictionary<string, object> targetContext,
            CancellationToken cancellationToken);

        /// <summary>
        /// Core evidence collection implementation.
        /// Default implementation returns empty dictionary - derived classes should override.
        /// </summary>
        /// <param name="framework">The compliance framework.</param>
        /// <param name="targetContext">Context about what is being assessed.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Collected evidence artifacts keyed by control ID.</returns>
        protected virtual Task<IReadOnlyDictionary<string, object>> CollectEvidenceCoreAsync(
            ComplianceFramework framework,
            IReadOnlyDictionary<string, object> targetContext,
            CancellationToken cancellationToken)
        {
            // Default implementation: no evidence collected
            // Derived classes should override to collect framework-specific evidence
            return Task.FromResult<IReadOnlyDictionary<string, object>>(
                new Dictionary<string, object>());
        }

        /// <summary>
        /// Creates a violation record with recommended remediation.
        /// Helper method for derived classes.
        /// </summary>
        /// <param name="control">The control that was violated.</param>
        /// <param name="details">Details about the violation.</param>
        /// <param name="resource">The resource where violation was found.</param>
        /// <param name="evidence">Supporting evidence.</param>
        /// <returns>A compliance violation record.</returns>
        protected static ComplianceViolation CreateViolation(
            ComplianceControl control,
            string details,
            string? resource = null,
            IReadOnlyDictionary<string, object>? evidence = null)
        {
            // Calculate remediation deadline based on severity
            var deadline = control.Severity switch
            {
                ComplianceSeverity.Critical => DateTime.UtcNow.AddHours(24),
                ComplianceSeverity.High => DateTime.UtcNow.AddDays(7),
                ComplianceSeverity.Medium => DateTime.UtcNow.AddDays(30),
                ComplianceSeverity.Low => DateTime.UtcNow.AddDays(90),
                _ => (DateTime?)null
            };

            var remediationSteps = new List<string>();
            if (!string.IsNullOrEmpty(control.RemediationGuidance))
            {
                remediationSteps.Add(control.RemediationGuidance);
            }
            else
            {
                remediationSteps.Add($"Review and implement control {control.ControlId}");
                remediationSteps.Add("Collect evidence demonstrating compliance");
                remediationSteps.Add("Re-assess to verify remediation");
            }

            return new ComplianceViolation
            {
                ControlId = control.ControlId,
                Severity = control.Severity,
                Details = details,
                Resource = resource,
                RemediationSteps = remediationSteps,
                Evidence = evidence,
                RemediationDeadline = deadline
            };
        }

        /// <summary>
        /// Validates that all required controls are defined for a framework.
        /// Helper method for derived classes to validate their control definitions.
        /// </summary>
        /// <param name="requirements">The requirements to validate.</param>
        /// <returns>True if valid, false otherwise.</returns>
        protected static bool ValidateRequirements(ComplianceRequirements requirements)
        {
            if (requirements.Controls == null || requirements.Controls.Count == 0)
                return false;

            // Validate each control
            foreach (var control in requirements.Controls)
            {
                if (string.IsNullOrWhiteSpace(control.ControlId))
                    return false;

                if (string.IsNullOrWhiteSpace(control.Description))
                    return false;

                if (control.Framework != requirements.Framework)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Updates statistics atomically.
        /// </summary>
        private void UpdateStatistics(
            ComplianceFramework framework,
            ComplianceAssessmentResult result,
            double assessmentTimeMs)
        {
            lock (_statsLock)
            {
                Interlocked.Increment(ref _totalAssessments);
                _totalAssessmentTimeMs += assessmentTimeMs;
                _totalComplianceScore += result.ComplianceScore;
                _lastUpdateTime = DateTime.UtcNow;

                if (result.IsCompliant)
                {
                    Interlocked.Increment(ref _compliantCount);
                }
                else
                {
                    Interlocked.Increment(ref _nonCompliantCount);
                }

                _assessmentsByFramework[framework]++;

                // Update violation counters
                Interlocked.Add(ref _totalViolations, result.Violations.Count);
                foreach (var violation in result.Violations)
                {
                    _violationsBySeverity[violation.Severity]++;
                }
            }
        }

        /// <summary>
        /// Increments error counter atomically.
        /// </summary>
        private void IncrementErrorCount()
        {
            Interlocked.Increment(ref _errorCount);
        }

        #region Intelligence Integration

        /// <summary>
        /// Message bus reference for Intelligence communication.
        /// </summary>
        protected IMessageBus? MessageBus { get; private set; }

        /// <summary>
        /// Configures Intelligence integration for this strategy.
        /// Called by the plugin to enable AI-enhanced features.
        /// </summary>
        public virtual void ConfigureIntelligence(IMessageBus? messageBus)
        {
            MessageBus = messageBus;
        }

        /// <summary>
        /// Whether Intelligence is available for this strategy.
        /// </summary>
        protected bool IsIntelligenceAvailable => MessageBus != null;

        /// <summary>
        /// Gets static knowledge about this strategy for AI discovery.
        /// Override to provide strategy-specific knowledge.
        /// </summary>
        public virtual KnowledgeObject GetStrategyKnowledge()
        {
            return new KnowledgeObject
            {
                Id = $"strategy.{StrategyId}",
                Topic = $"{GetKnowledgeTopic()}",
                SourcePluginId = "sdk.strategy",
                SourcePluginName = StrategyName,
                KnowledgeType = "capability",
                Description = GetStrategyDescription(),
                Payload = GetKnowledgePayload(),
                Tags = GetKnowledgeTags()
            };
        }

        /// <summary>
        /// Gets the registered capability for this strategy.
        /// </summary>
        public virtual RegisteredCapability GetStrategyCapability()
        {
            return new RegisteredCapability
            {
                CapabilityId = $"strategy.{StrategyId}",
                DisplayName = StrategyName,
                Description = GetStrategyDescription(),
                Category = GetCapabilityCategory(),
                PluginId = "sdk.strategy",
                PluginName = StrategyName,
                PluginVersion = "1.0.0",
                Tags = GetKnowledgeTags(),
                Metadata = GetCapabilityMetadata(),
                SemanticDescription = GetSemanticDescription()
            };
        }

        /// <summary>
        /// Gets the knowledge topic for this strategy type.
        /// </summary>
        protected virtual string GetKnowledgeTopic() => "compliance";

        /// <summary>
        /// Gets the capability category for this strategy type.
        /// </summary>
        protected virtual CapabilityCategory GetCapabilityCategory() => CapabilityCategory.Governance;

        /// <summary>
        /// Gets a description for this strategy.
        /// </summary>
        protected virtual string GetStrategyDescription() =>
            $"{StrategyName} compliance strategy supporting {string.Join(", ", SupportedFrameworks)}";

        /// <summary>
        /// Gets the knowledge payload for this strategy.
        /// </summary>
        protected virtual Dictionary<string, object> GetKnowledgePayload() => new()
        {
            ["supportedFrameworks"] = SupportedFrameworks.Select(f => f.ToString()).ToArray(),
            ["frameworkCount"] = SupportedFrameworks.Count
        };

        /// <summary>
        /// Gets tags for this strategy.
        /// </summary>
        protected virtual string[] GetKnowledgeTags()
        {
            var tags = new List<string> { "strategy", "compliance", "governance" };
            tags.AddRange(SupportedFrameworks.Select(f => f.ToString().ToLowerInvariant()));
            return tags.ToArray();
        }

        /// <summary>
        /// Gets capability metadata for this strategy.
        /// </summary>
        protected virtual Dictionary<string, object> GetCapabilityMetadata() => new()
        {
            ["supportedFrameworks"] = SupportedFrameworks.Select(f => f.ToString()).ToArray()
        };

        /// <summary>
        /// Gets the semantic description for AI-driven discovery.
        /// </summary>
        protected virtual string GetSemanticDescription() =>
            $"Use {StrategyName} for {string.Join(", ", SupportedFrameworks)} compliance assessment";

        /// <summary>
        /// Requests an AI recommendation for compliance controls.
        /// </summary>
        /// <param name="framework">Compliance framework.</param>
        /// <param name="dataClassification">Classification of data being protected.</param>
        /// <param name="industry">Industry vertical.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Recommended compliance controls, or null if Intelligence unavailable.</returns>
        protected async Task<Dictionary<string, object>?> RequestComplianceRecommendationAsync(
            ComplianceFramework framework,
            string dataClassification,
            string? industry = null,
            CancellationToken cancellationToken = default)
        {
            if (!IsIntelligenceAvailable || MessageBus == null)
                return null;

            var request = new Dictionary<string, object>
            {
                ["requestType"] = "compliance_recommendation",
                ["strategyId"] = StrategyId,
                ["framework"] = framework.ToString(),
                ["dataClassification"] = dataClassification,
                ["industry"] = industry ?? "general"
            };

            var message = new PluginMessage
            {
                MessageId = Guid.NewGuid().ToString(),
                SourcePluginId = StrategyId,
                MessageType = "intelligence.request",
                Payload = request
            };

            var response = await MessageBus.SendAsync(MessageTopics.AIQuery, message, cancellationToken);
            return response.Success ? response.Payload as Dictionary<string, object> : null;
        }

        #endregion
    }
}
