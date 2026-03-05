using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SdkStrategyBase = DataWarehouse.SDK.Contracts.StrategyBase;

namespace DataWarehouse.Plugins.UltimateCompliance
{
    /// <summary>
    /// Compliance check result.
    /// </summary>
    public record ComplianceResult
    {
        /// <summary>
        /// Whether the operation is compliant.
        /// </summary>
        public required bool IsCompliant { get; init; }

        /// <summary>
        /// Compliance framework (GDPR, HIPAA, SOX, etc.).
        /// </summary>
        public required string Framework { get; init; }

        /// <summary>
        /// Overall compliance status.
        /// </summary>
        public required ComplianceStatus Status { get; init; }

        /// <summary>
        /// List of violations found.
        /// </summary>
        public IReadOnlyList<ComplianceViolation> Violations { get; init; } = Array.Empty<ComplianceViolation>();

        /// <summary>
        /// List of recommendations.
        /// </summary>
        public IReadOnlyList<string> Recommendations { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Timestamp of the check.
        /// </summary>
        public DateTime Timestamp { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Additional metadata.
        /// </summary>
        public IReadOnlyDictionary<string, object> Metadata { get; init; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Compliance status levels.
    /// </summary>
    public enum ComplianceStatus
    {
        Compliant,
        PartiallyCompliant,
        NonCompliant,
        NotApplicable,
        RequiresReview
    }

    /// <summary>
    /// Represents a compliance violation.
    /// </summary>
    public record ComplianceViolation
    {
        /// <summary>
        /// Violation code/identifier.
        /// </summary>
        public required string Code { get; init; }

        /// <summary>
        /// Description of the violation.
        /// </summary>
        public required string Description { get; init; }

        /// <summary>
        /// Severity of the violation.
        /// </summary>
        public required ViolationSeverity Severity { get; init; }

        /// <summary>
        /// Affected data or resource.
        /// </summary>
        public string? AffectedResource { get; init; }

        /// <summary>
        /// Recommended remediation.
        /// </summary>
        public string? Remediation { get; init; }

        /// <summary>
        /// Regulatory reference (article, section).
        /// </summary>
        public string? RegulatoryReference { get; init; }
    }

    /// <summary>
    /// Violation severity levels.
    /// </summary>
    public enum ViolationSeverity
    {
        Low,
        Medium,
        High,
        Critical
    }

    /// <summary>
    /// Context for compliance evaluation.
    /// </summary>
    public record ComplianceContext
    {
        /// <summary>
        /// Type of operation being checked.
        /// </summary>
        public required string OperationType { get; init; }

        /// <summary>
        /// Data classification.
        /// </summary>
        public required string DataClassification { get; init; }

        /// <summary>
        /// Source location/region.
        /// </summary>
        public string? SourceLocation { get; init; }

        /// <summary>
        /// Destination location/region.
        /// </summary>
        public string? DestinationLocation { get; init; }

        /// <summary>
        /// User performing the operation.
        /// </summary>
        public string? UserId { get; init; }

        /// <summary>
        /// Resource being accessed.
        /// </summary>
        public string? ResourceId { get; init; }

        /// <summary>
        /// Data subject categories.
        /// </summary>
        public IReadOnlyList<string> DataSubjectCategories { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Processing purposes.
        /// </summary>
        public IReadOnlyList<string> ProcessingPurposes { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Additional context attributes.
        /// </summary>
        public IReadOnlyDictionary<string, object> Attributes { get; init; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Interface for compliance checking strategies.
    /// </summary>
    public interface IComplianceStrategy
    {
        /// <summary>
        /// Gets the strategy identifier.
        /// </summary>
        string StrategyId { get; }

        /// <summary>
        /// Gets the strategy display name.
        /// </summary>
        string StrategyName { get; }

        /// <summary>
        /// Gets the compliance framework this strategy implements.
        /// </summary>
        string Framework { get; }

        /// <summary>
        /// Checks compliance for the given context.
        /// </summary>
        Task<ComplianceResult> CheckComplianceAsync(ComplianceContext context, CancellationToken cancellationToken = default);

        /// <summary>
        /// Initializes the strategy with configuration.
        /// </summary>
        Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets compliance statistics.
        /// </summary>
        ComplianceStatistics GetStatistics();
    }

    /// <summary>
    /// Compliance statistics.
    /// </summary>
    public sealed class ComplianceStatistics
    {
        public long TotalChecks { get; set; }
        public long CompliantCount { get; set; }
        public long NonCompliantCount { get; set; }
        public long ViolationsFound { get; set; }
        public DateTime StartTime { get; set; } = DateTime.UtcNow;
        public DateTime LastCheckTime { get; set; }
    }

    /// <summary>
    /// Base class for compliance strategies.
    /// Inherits from <see cref="SdkStrategyBase"/> for unified strategy hierarchy (AD-05).
    /// Uses alias to resolve name collision with SDK's ComplianceStrategyBase.
    /// </summary>
    public abstract class ComplianceStrategyBase : SdkStrategyBase, IComplianceStrategy
    {
        private long _totalChecks;
        private long _compliantCount;
        private long _nonCompliantCount;
        private long _violationsFound;
        private readonly DateTime _startTime = DateTime.UtcNow;
        private DateTime _lastCheckTime;

        protected Dictionary<string, object> Configuration { get; private set; } = new();

        public abstract override string StrategyId { get; }
        public abstract string StrategyName { get; }

        /// <summary>
        /// Bridges StrategyBase.Name to the domain-specific StrategyName property.
        /// </summary>
        public override string Name => StrategyName;

        public abstract string Framework { get; }

        public virtual Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            Configuration = configuration ?? new Dictionary<string, object>();
            return Task.CompletedTask;
        }

        public async Task<ComplianceResult> CheckComplianceAsync(ComplianceContext context, CancellationToken cancellationToken = default)
        {
            var result = await CheckComplianceCoreAsync(context, cancellationToken);

            // Update statistics
            Interlocked.Increment(ref _totalChecks);
            if (result.IsCompliant)
                Interlocked.Increment(ref _compliantCount);
            else
                Interlocked.Increment(ref _nonCompliantCount);

            Interlocked.Add(ref _violationsFound, result.Violations.Count);
            _lastCheckTime = DateTime.UtcNow;

            return result;
        }

        protected abstract Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken);

        public ComplianceStatistics GetStatistics()
        {
            return new ComplianceStatistics
            {
                TotalChecks = Interlocked.Read(ref _totalChecks),
                CompliantCount = Interlocked.Read(ref _compliantCount),
                NonCompliantCount = Interlocked.Read(ref _nonCompliantCount),
                ViolationsFound = Interlocked.Read(ref _violationsFound),
                StartTime = _startTime,
                LastCheckTime = _lastCheckTime
            };
        }

        /// <summary>
        /// Retrieves a typed configuration value by key, returning <paramref name="defaultValue"/> when the key
        /// is absent or the stored value cannot be cast to <typeparamref name="T"/>.
        /// </summary>
        /// <remarks>
        /// LOW-1459: Centralised on base class to avoid copy-paste across derived strategies.
        /// </remarks>
        protected T GetConfigValue<T>(string key, T defaultValue)
        {
            if (Configuration.TryGetValue(key, out var value) && value is T typedValue)
                return typedValue;
            return defaultValue;
        }
    }
}
