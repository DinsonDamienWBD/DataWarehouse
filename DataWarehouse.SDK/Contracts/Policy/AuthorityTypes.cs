using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Policy
{
    /// <summary>
    /// Represents a decision made at a specific authority level within the authority chain.
    /// Each decision records who decided what, at what authority level, and when.
    /// Lower <see cref="AuthorityPriority"/> numbers indicate higher authority.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 75: Authority Chain (AUTH-09)")]
    public sealed record AuthorityDecision
    {
        /// <summary>
        /// Unique identifier for this decision (GUID string).
        /// </summary>
        public required string DecisionId { get; init; }

        /// <summary>
        /// Name of the authority level that made this decision (e.g., "Quorum", "AiEmergency", "Admin", "SystemDefaults").
        /// Must correspond to an <see cref="AuthorityLevel.Name"/> in the configured <see cref="AuthorityChain"/>.
        /// </summary>
        public required string AuthorityLevelName { get; init; }

        /// <summary>
        /// Numeric priority of the authority level (from <see cref="AuthorityLevel.Priority"/>).
        /// 0 = highest authority (Quorum), 3 = lowest (SystemDefaults).
        /// </summary>
        public required int AuthorityPriority { get; init; }

        /// <summary>
        /// The action that was decided upon (e.g., "OverrideEncryptionPolicy", "DisableCompression").
        /// </summary>
        public required string Action { get; init; }

        /// <summary>
        /// UTC timestamp when this decision was recorded.
        /// </summary>
        public required DateTimeOffset Timestamp { get; init; }

        /// <summary>
        /// Human-readable reason for the decision, or null if not provided.
        /// </summary>
        public string? DecisionReason { get; init; }

        /// <summary>
        /// Identity of the actor who made the decision (user ID, service principal, etc.), or null for system-initiated decisions.
        /// </summary>
        public string? ActorId { get; init; }

        /// <summary>
        /// Whether this decision is currently active. A decision can be superseded (deactivated)
        /// when a higher-authority decision overrides it.
        /// </summary>
        public bool IsActive { get; init; } = true;
    }

    /// <summary>
    /// Result of resolving competing authority decisions for a given action.
    /// Contains the winning decision, all overridden decisions, and the chain used for resolution.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 75: Authority Chain (AUTH-09)")]
    public sealed record AuthorityResolution
    {
        /// <summary>
        /// The decision that won authority resolution (lowest <see cref="AuthorityDecision.AuthorityPriority"/>).
        /// </summary>
        public required AuthorityDecision WinningDecision { get; init; }

        /// <summary>
        /// Decisions that were suppressed because they came from lower-authority levels.
        /// Ordered by priority ascending (closest competitor first).
        /// </summary>
        public IReadOnlyList<AuthorityDecision> OverriddenDecisions { get; init; } = Array.Empty<AuthorityDecision>();

        /// <summary>
        /// The authority chain that was used to determine resolution ordering.
        /// </summary>
        public required AuthorityChain Chain { get; init; }

        /// <summary>
        /// UTC timestamp when the resolution was computed.
        /// </summary>
        public required DateTimeOffset ResolvedAt { get; init; }
    }

    /// <summary>
    /// Defines the contract for resolving competing authority decisions.
    /// Enforces the strict priority ordering: Quorum > AiEmergency > Admin > SystemDefaults.
    /// Lower-authority levels cannot override decisions made at a higher authority level.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 75: Authority Chain (AUTH-09)")]
    public interface IAuthorityResolver
    {
        /// <summary>
        /// Resolves competing decisions for the same action, returning the winning decision
        /// based on authority ordering (lowest priority number wins).
        /// </summary>
        /// <param name="action">The action being resolved (e.g., "OverrideEncryptionPolicy").</param>
        /// <param name="decisions">Competing decisions from different authority levels.</param>
        /// <param name="chain">
        /// The authority chain to use for resolution. If null, the configured default chain is used.
        /// </param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>An <see cref="AuthorityResolution"/> containing the winner and all overridden decisions.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="decisions"/> is empty.</exception>
        Task<AuthorityResolution> ResolveAsync(
            string action,
            IReadOnlyList<AuthorityDecision> decisions,
            AuthorityChain? chain = null,
            CancellationToken ct = default);

        /// <summary>
        /// Determines whether a proposed decision can override an existing decision.
        /// Returns true only if the proposed decision comes from a strictly higher authority
        /// (lower priority number) than the existing decision.
        /// </summary>
        /// <param name="proposed">The new decision attempting to override.</param>
        /// <param name="existing">The current decision that would be overridden.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>
        /// <c>true</c> if <paramref name="proposed"/> has strictly higher authority than <paramref name="existing"/>;
        /// <c>false</c> otherwise (including equal priority).
        /// </returns>
        Task<bool> CanOverrideAsync(
            AuthorityDecision proposed,
            AuthorityDecision existing,
            CancellationToken ct = default);

        /// <summary>
        /// Creates and stores a new authority decision for the specified action.
        /// Validates that the authority level name exists in the configured chain.
        /// </summary>
        /// <param name="authorityLevelName">
        /// Name of the authority level making the decision (e.g., "Quorum", "Admin").
        /// Must match an <see cref="AuthorityLevel.Name"/> in the configured chain.
        /// </param>
        /// <param name="action">The action being decided upon.</param>
        /// <param name="actorId">Optional identity of the actor making the decision.</param>
        /// <param name="reason">Optional human-readable reason for the decision.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>The newly created <see cref="AuthorityDecision"/>.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="authorityLevelName"/> does not exist in the configured chain.
        /// </exception>
        Task<AuthorityDecision> RecordDecisionAsync(
            string authorityLevelName,
            string action,
            string? actorId = null,
            string? reason = null,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Configurable settings for authority resolution behavior.
    /// Controls the authority chain used, enforcement strictness, and decision retention.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 75: Authority Chain (AUTH-09)")]
    public sealed record AuthorityConfiguration
    {
        /// <summary>
        /// The authority chain defining the hierarchy of authority levels.
        /// Defaults to <see cref="AuthorityChain.Default()"/> (Quorum > AiEmergency > Admin > SystemDefaults).
        /// </summary>
        public AuthorityChain Chain { get; init; } = AuthorityChain.Default();

        /// <summary>
        /// When true, strictly enforces that lower-authority levels cannot override higher-authority decisions.
        /// Equal-priority decisions also cannot override each other.
        /// When false, override checks still return false but no exceptions are thrown.
        /// </summary>
        public bool StrictEnforcement { get; init; } = true;

        /// <summary>
        /// How long to retain decision history before purging. Decisions older than this are
        /// removed by the authority resolution engine's PurgeExpiredDecisions method.
        /// Default: 90 days.
        /// </summary>
        public TimeSpan DecisionRetentionPeriod { get; init; } = TimeSpan.FromDays(90);
    }
}
