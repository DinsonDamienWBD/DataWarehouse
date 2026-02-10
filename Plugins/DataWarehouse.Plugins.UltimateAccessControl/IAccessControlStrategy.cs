using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl
{
    /// <summary>
    /// Defines capabilities of an access control strategy.
    /// </summary>
    public record AccessControlCapabilities
    {
        /// <summary>
        /// Indicates whether the strategy supports real-time access decisions.
        /// </summary>
        public bool SupportsRealTimeDecisions { get; init; }

        /// <summary>
        /// Indicates whether the strategy can generate audit trails.
        /// </summary>
        public bool SupportsAuditTrail { get; init; }

        /// <summary>
        /// Indicates whether the strategy supports policy-based configuration.
        /// </summary>
        public bool SupportsPolicyConfiguration { get; init; }

        /// <summary>
        /// Indicates whether the strategy supports integration with external identity providers.
        /// </summary>
        public bool SupportsExternalIdentity { get; init; }

        /// <summary>
        /// Indicates whether the strategy supports time-based access control.
        /// </summary>
        public bool SupportsTemporalAccess { get; init; }

        /// <summary>
        /// Indicates whether the strategy supports location-based access control.
        /// </summary>
        public bool SupportsGeographicRestrictions { get; init; }

        /// <summary>
        /// Maximum number of concurrent access evaluations supported.
        /// </summary>
        public int MaxConcurrentEvaluations { get; init; } = 1000;
    }

    /// <summary>
    /// Result of an access control decision.
    /// </summary>
    public record AccessDecision
    {
        /// <summary>
        /// Whether access is granted.
        /// </summary>
        public required bool IsGranted { get; init; }

        /// <summary>
        /// Reason for the decision.
        /// </summary>
        public required string Reason { get; init; }

        /// <summary>
        /// Unique identifier for this decision for audit purposes.
        /// </summary>
        public string DecisionId { get; init; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// Timestamp of the decision.
        /// </summary>
        public DateTime Timestamp { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Policies that contributed to this decision.
        /// </summary>
        public IReadOnlyList<string> ApplicablePolicies { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Additional metadata about the decision.
        /// </summary>
        public IReadOnlyDictionary<string, object> Metadata { get; init; } = new Dictionary<string, object>();

        /// <summary>
        /// Time in milliseconds to evaluate the decision.
        /// </summary>
        public double EvaluationTimeMs { get; init; }
    }

    /// <summary>
    /// Context for access control evaluation.
    /// </summary>
    public record AccessContext
    {
        /// <summary>
        /// Subject (user or service) requesting access.
        /// </summary>
        public required string SubjectId { get; init; }

        /// <summary>
        /// Resource being accessed.
        /// </summary>
        public required string ResourceId { get; init; }

        /// <summary>
        /// Action being performed.
        /// </summary>
        public required string Action { get; init; }

        /// <summary>
        /// Roles associated with the subject.
        /// </summary>
        public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Attributes of the subject.
        /// </summary>
        public IReadOnlyDictionary<string, object> SubjectAttributes { get; init; } = new Dictionary<string, object>();

        /// <summary>
        /// Attributes of the resource.
        /// </summary>
        public IReadOnlyDictionary<string, object> ResourceAttributes { get; init; } = new Dictionary<string, object>();

        /// <summary>
        /// Environmental attributes (time, location, etc.).
        /// </summary>
        public IReadOnlyDictionary<string, object> EnvironmentAttributes { get; init; } = new Dictionary<string, object>();

        /// <summary>
        /// Client IP address.
        /// </summary>
        public string? ClientIpAddress { get; init; }

        /// <summary>
        /// Geographic location if available.
        /// </summary>
        public GeoLocation? Location { get; init; }

        /// <summary>
        /// Request timestamp.
        /// </summary>
        public DateTime RequestTime { get; init; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Geographic location information.
    /// </summary>
    public record GeoLocation
    {
        public double Latitude { get; init; }
        public double Longitude { get; init; }
        public string? Country { get; init; }
        public string? Region { get; init; }
        public string? City { get; init; }
    }

    /// <summary>
    /// Interface for access control strategies.
    /// </summary>
    public interface IAccessControlStrategy
    {
        /// <summary>
        /// Gets the unique identifier for this strategy.
        /// </summary>
        string StrategyId { get; }

        /// <summary>
        /// Gets the display name for this strategy.
        /// </summary>
        string StrategyName { get; }

        /// <summary>
        /// Gets the capabilities of this strategy.
        /// </summary>
        AccessControlCapabilities Capabilities { get; }

        /// <summary>
        /// Evaluates an access request.
        /// </summary>
        Task<AccessDecision> EvaluateAccessAsync(AccessContext context, CancellationToken cancellationToken = default);

        /// <summary>
        /// Initializes the strategy with configuration.
        /// </summary>
        Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets statistics about strategy usage.
        /// </summary>
        AccessControlStatistics GetStatistics();

        /// <summary>
        /// Resets statistics counters.
        /// </summary>
        void ResetStatistics();
    }

    /// <summary>
    /// Statistics for access control operations.
    /// </summary>
    public sealed class AccessControlStatistics
    {
        public long TotalEvaluations { get; set; }
        public long GrantedCount { get; set; }
        public long DeniedCount { get; set; }
        public double AverageEvaluationTimeMs { get; set; }
        public DateTime StartTime { get; set; } = DateTime.UtcNow;
        public DateTime LastEvaluationTime { get; set; }
    }

    /// <summary>
    /// Base class for access control strategies.
    /// </summary>
    public abstract class AccessControlStrategyBase : IAccessControlStrategy
    {
        private long _totalEvaluations;
        private long _grantedCount;
        private long _deniedCount;
        private double _totalEvaluationTimeMs;
        private readonly DateTime _startTime = DateTime.UtcNow;
        private DateTime _lastEvaluationTime;
        protected Dictionary<string, object> Configuration { get; private set; } = new();

        public abstract string StrategyId { get; }
        public abstract string StrategyName { get; }
        public abstract AccessControlCapabilities Capabilities { get; }

        public virtual Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            Configuration = configuration ?? new Dictionary<string, object>();
            return Task.CompletedTask;
        }

        public async Task<AccessDecision> EvaluateAccessAsync(AccessContext context, CancellationToken cancellationToken = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var decision = await EvaluateAccessCoreAsync(context, cancellationToken);
                sw.Stop();

                // Update statistics
                Interlocked.Increment(ref _totalEvaluations);
                if (decision.IsGranted)
                    Interlocked.Increment(ref _grantedCount);
                else
                    Interlocked.Increment(ref _deniedCount);

                lock (this)
                {
                    _totalEvaluationTimeMs += sw.Elapsed.TotalMilliseconds;
                    _lastEvaluationTime = DateTime.UtcNow;
                }

                return decision with { EvaluationTimeMs = sw.Elapsed.TotalMilliseconds };
            }
            catch
            {
                sw.Stop();
                Interlocked.Increment(ref _deniedCount);
                throw;
            }
        }

        protected abstract Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken);

        public AccessControlStatistics GetStatistics()
        {
            var total = Interlocked.Read(ref _totalEvaluations);
            return new AccessControlStatistics
            {
                TotalEvaluations = total,
                GrantedCount = Interlocked.Read(ref _grantedCount),
                DeniedCount = Interlocked.Read(ref _deniedCount),
                AverageEvaluationTimeMs = total > 0 ? _totalEvaluationTimeMs / total : 0,
                StartTime = _startTime,
                LastEvaluationTime = _lastEvaluationTime
            };
        }

        public void ResetStatistics()
        {
            Interlocked.Exchange(ref _totalEvaluations, 0);
            Interlocked.Exchange(ref _grantedCount, 0);
            Interlocked.Exchange(ref _deniedCount, 0);
            lock (this)
            {
                _totalEvaluationTimeMs = 0;
            }
        }
    }

    /// <summary>
    /// Strategy decision detail for policy engine evaluation.
    /// </summary>
    public sealed record StrategyDecisionDetail
    {
        public required string StrategyId { get; init; }
        public required string StrategyName { get; init; }
        public required AccessDecision Decision { get; init; }
        public double Weight { get; init; } = 1.0;
        public string? Error { get; init; }
    }

    /// <summary>
    /// Policy access decision with multi-strategy evaluation details.
    /// </summary>
    public sealed record PolicyAccessDecision
    {
        public required bool IsGranted { get; init; }
        public required string Reason { get; init; }
        public required string DecisionId { get; init; }
        public required DateTime Timestamp { get; init; }
        public required double EvaluationTimeMs { get; init; }
        public required PolicyEvaluationMode EvaluationMode { get; init; }
        public required IReadOnlyList<StrategyDecisionDetail> StrategyDecisions { get; init; }
        public required AccessContext Context { get; init; }
    }

    /// <summary>
    /// Policy evaluation mode forward declaration (defined in plugin).
    /// </summary>
    public enum PolicyEvaluationMode
    {
        AllMustAllow,
        AnyMustAllow,
        FirstMatch,
        Weighted
    }
}
