namespace DataWarehouse.Plugins.UltimateDataProtection.Versioning
{
    /// <summary>
    /// Interface for versioning policies that control when and how versions are created and retained.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Versioning policies determine:
    /// </para>
    /// <list type="bullet">
    ///   <item>When to create new versions based on context</item>
    ///   <item>How long to retain versions</item>
    ///   <item>Minimum intervals between versions</item>
    ///   <item>Storage tier transitions</item>
    /// </list>
    /// </remarks>
    public interface IVersioningPolicy
    {
        /// <summary>
        /// Gets the unique identifier for this policy.
        /// </summary>
        string PolicyId { get; }

        /// <summary>
        /// Gets the human-readable name of this policy.
        /// </summary>
        string PolicyName { get; }

        /// <summary>
        /// Gets the description of this policy.
        /// </summary>
        string Description { get; }

        /// <summary>
        /// Gets the versioning mode this policy implements.
        /// </summary>
        VersioningMode Mode { get; }

        /// <summary>
        /// Determines whether a new version should be created based on the context.
        /// </summary>
        /// <param name="context">Version context with change information.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if a version should be created; otherwise false.</returns>
        Task<bool> ShouldCreateVersionAsync(VersionContext context, CancellationToken ct = default);

        /// <summary>
        /// Evaluates retention for a version.
        /// </summary>
        /// <param name="version">Version to evaluate.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Retention decision.</returns>
        Task<VersionRetentionDecision> EvaluateRetentionAsync(VersionInfo version, CancellationToken ct = default);

        /// <summary>
        /// Gets the minimum interval between version creations.
        /// </summary>
        /// <returns>Minimum interval, or null if no minimum.</returns>
        TimeSpan? GetMinimumInterval();

        /// <summary>
        /// Gets the maximum number of versions to retain per item.
        /// </summary>
        /// <returns>Maximum versions, or null if unlimited.</returns>
        int? GetMaxVersionsPerItem();

        /// <summary>
        /// Gets the retention period for versions.
        /// </summary>
        /// <returns>Retention period, or null if indefinite.</returns>
        TimeSpan? GetRetentionPeriod();

        /// <summary>
        /// Configures the policy with specific settings.
        /// </summary>
        /// <param name="settings">Policy settings.</param>
        void Configure(PolicySettings settings);

        /// <summary>
        /// Gets the current policy settings.
        /// </summary>
        PolicySettings GetSettings();
    }

    /// <summary>
    /// Settings for configuring versioning policies.
    /// </summary>
    public sealed record PolicySettings
    {
        /// <summary>Minimum interval between versions.</summary>
        public TimeSpan? MinimumInterval { get; init; }

        /// <summary>Maximum versions per item.</summary>
        public int? MaxVersionsPerItem { get; init; }

        /// <summary>Retention period.</summary>
        public TimeSpan? RetentionPeriod { get; init; }

        /// <summary>Minimum change size to trigger versioning (bytes).</summary>
        public long? MinimumChangeSize { get; init; }

        /// <summary>Minimum change percentage to trigger versioning.</summary>
        public double? MinimumChangePercentage { get; init; }

        /// <summary>Enable compression for version storage.</summary>
        public bool EnableCompression { get; init; } = true;

        /// <summary>Enable deduplication for version storage.</summary>
        public bool EnableDeduplication { get; init; } = true;

        /// <summary>Events that trigger versioning (for event-based mode).</summary>
        public IReadOnlyList<string> TriggerEvents { get; init; } = Array.Empty<string>();

        /// <summary>Schedule expression (for scheduled mode).</summary>
        public string? ScheduleExpression { get; init; }

        /// <summary>Intelligence threshold for intelligent mode (0.0 - 1.0).</summary>
        public double? IntelligenceThreshold { get; init; }

        /// <summary>Storage tier transition rules.</summary>
        public IReadOnlyList<TierTransitionRule> TierTransitions { get; init; } = Array.Empty<TierTransitionRule>();

        /// <summary>Custom policy-specific settings.</summary>
        public IReadOnlyDictionary<string, object> CustomSettings { get; init; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Rule for transitioning versions between storage tiers.
    /// </summary>
    public sealed record TierTransitionRule
    {
        /// <summary>Age threshold for transition.</summary>
        public TimeSpan AgeThreshold { get; init; }

        /// <summary>Target storage tier.</summary>
        public StorageTier TargetTier { get; init; }

        /// <summary>Whether to skip immutable versions.</summary>
        public bool SkipImmutable { get; init; }

        /// <summary>Whether to skip versions on legal hold.</summary>
        public bool SkipLegalHold { get; init; } = true;
    }

    /// <summary>
    /// Base class for versioning policies providing common functionality.
    /// </summary>
    public abstract class VersioningPolicyBase : IVersioningPolicy
    {
        /// <summary>
        /// Policy settings.
        /// </summary>
        protected PolicySettings Settings { get; private set; } = new();

        /// <inheritdoc/>
        public abstract string PolicyId { get; }

        /// <inheritdoc/>
        public abstract string PolicyName { get; }

        /// <inheritdoc/>
        public abstract string Description { get; }

        /// <inheritdoc/>
        public abstract VersioningMode Mode { get; }

        /// <inheritdoc/>
        public abstract Task<bool> ShouldCreateVersionAsync(VersionContext context, CancellationToken ct = default);

        /// <inheritdoc/>
        public virtual Task<VersionRetentionDecision> EvaluateRetentionAsync(VersionInfo version, CancellationToken ct = default)
        {
            // Default retention logic based on settings
            var shouldRetain = true;
            var reason = "Version within retention policy";
            var action = RetentionAction.Keep;

            // Check legal hold
            if (version.IsOnLegalHold)
            {
                return Task.FromResult(new VersionRetentionDecision
                {
                    ShouldRetain = true,
                    Reason = "Version is on legal hold",
                    SuggestedAction = RetentionAction.Keep
                });
            }

            // Check immutability
            if (version.IsImmutable)
            {
                return Task.FromResult(new VersionRetentionDecision
                {
                    ShouldRetain = true,
                    Reason = "Version is immutable",
                    SuggestedAction = RetentionAction.Keep
                });
            }

            // Check expiration
            if (version.ExpiresAt.HasValue && version.ExpiresAt.Value <= DateTimeOffset.UtcNow)
            {
                shouldRetain = false;
                reason = "Version has expired";
                action = RetentionAction.Delete;
            }

            // Check retention period
            if (Settings.RetentionPeriod.HasValue)
            {
                var age = DateTimeOffset.UtcNow - version.CreatedAt;
                if (age > Settings.RetentionPeriod.Value)
                {
                    shouldRetain = false;
                    reason = $"Version exceeds retention period of {Settings.RetentionPeriod.Value.TotalDays} days";
                    action = RetentionAction.Delete;
                }
            }

            // Check tier transition
            StorageTier? suggestedTier = null;
            if (shouldRetain && Settings.TierTransitions.Count > 0)
            {
                var age = DateTimeOffset.UtcNow - version.CreatedAt;
                foreach (var rule in Settings.TierTransitions.OrderBy(r => r.AgeThreshold))
                {
                    if (age >= rule.AgeThreshold && version.StorageTier < rule.TargetTier)
                    {
                        suggestedTier = rule.TargetTier;
                        action = RetentionAction.Archive;
                        reason = $"Version should transition to {rule.TargetTier} storage tier";
                    }
                }
            }

            return Task.FromResult(new VersionRetentionDecision
            {
                ShouldRetain = shouldRetain,
                Reason = reason,
                SuggestedAction = action,
                SuggestedTierTransition = suggestedTier
            });
        }

        /// <inheritdoc/>
        public virtual TimeSpan? GetMinimumInterval() => Settings.MinimumInterval;

        /// <inheritdoc/>
        public virtual int? GetMaxVersionsPerItem() => Settings.MaxVersionsPerItem;

        /// <inheritdoc/>
        public virtual TimeSpan? GetRetentionPeriod() => Settings.RetentionPeriod;

        /// <inheritdoc/>
        public virtual void Configure(PolicySettings settings)
        {
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <inheritdoc/>
        public PolicySettings GetSettings() => Settings;

        /// <summary>
        /// Checks if enough time has passed since the last version.
        /// </summary>
        /// <param name="context">Version context.</param>
        /// <returns>True if minimum interval has passed.</returns>
        protected bool HasMinimumIntervalPassed(VersionContext context)
        {
            if (!Settings.MinimumInterval.HasValue || !context.LastVersionTime.HasValue)
                return true;

            var elapsed = DateTimeOffset.UtcNow - context.LastVersionTime.Value;
            return elapsed >= Settings.MinimumInterval.Value;
        }

        /// <summary>
        /// Checks if the change is significant enough for versioning.
        /// </summary>
        /// <param name="context">Version context.</param>
        /// <returns>True if change is significant.</returns>
        protected bool IsChangeSignificant(VersionContext context)
        {
            // Check minimum change size
            if (Settings.MinimumChangeSize.HasValue && context.ChangeSize < Settings.MinimumChangeSize.Value)
                return false;

            // Check minimum change percentage
            if (Settings.MinimumChangePercentage.HasValue && context.PreviousSize.HasValue && context.PreviousSize.Value > 0)
            {
                var changePercent = (double)context.ChangeSize / context.PreviousSize.Value * 100;
                if (changePercent < Settings.MinimumChangePercentage.Value)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Checks if the version count has reached the maximum.
        /// </summary>
        /// <param name="context">Version context.</param>
        /// <returns>True if at or over maximum.</returns>
        protected bool IsAtMaxVersions(VersionContext context)
        {
            if (!Settings.MaxVersionsPerItem.HasValue)
                return false;

            return context.ExistingVersionCount >= Settings.MaxVersionsPerItem.Value;
        }
    }
}
