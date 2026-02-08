namespace DataWarehouse.Plugins.UltimateDataProtection.Versioning.Policies
{
    /// <summary>
    /// Event-based versioning policy - versions are created when specific events occur.
    /// Supports configurable event triggers like save, commit, schema-change, deploy, publish.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This policy is suitable for:
    /// </para>
    /// <list type="bullet">
    ///   <item>Workflow-based versioning (approval, publication, deployment)</item>
    ///   <item>Integration with CI/CD pipelines</item>
    ///   <item>Change tracking at business milestones</item>
    ///   <item>Audit trail for specific operations</item>
    ///   <item>Schema evolution tracking</item>
    /// </list>
    /// <para>
    /// Event types supported:
    /// </para>
    /// <list type="bullet">
    ///   <item>save - Document/data save operations</item>
    ///   <item>commit - Version control commits</item>
    ///   <item>schema-change - Data schema modifications</item>
    ///   <item>deploy - Deployment events</item>
    ///   <item>publish - Publication to production</item>
    ///   <item>approval - Approval workflow completions</item>
    ///   <item>backup - Scheduled backup operations</item>
    ///   <item>migration - Data migration events</item>
    /// </list>
    /// </remarks>
    public sealed class EventVersioningPolicy : VersioningPolicyBase
    {
        /// <inheritdoc/>
        public override string PolicyId => "event-versioning";

        /// <inheritdoc/>
        public override string PolicyName => "Event-Based Versioning";

        /// <inheritdoc/>
        public override string Description =>
            "Versions are created when specific events occur (save, commit, schema-change, deploy, publish). " +
            "Configurable event triggers with minimum interval protection to prevent version flooding.";

        /// <inheritdoc/>
        public override VersioningMode Mode => VersioningMode.EventBased;

        /// <summary>
        /// Determines if a version should be created based on the trigger event.
        /// </summary>
        /// <param name="context">Version context with trigger event information.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if the event matches configured triggers and minimum interval has passed.</returns>
        public override Task<bool> ShouldCreateVersionAsync(VersionContext context, CancellationToken ct = default)
        {
            // Event-based policy requires a trigger event
            if (string.IsNullOrWhiteSpace(context.TriggerEvent))
                return Task.FromResult(false);

            // Check if this event is in the configured trigger list
            if (Settings.TriggerEvents == null || Settings.TriggerEvents.Count == 0)
            {
                // No events configured - default to common events
                var defaultEvents = new[] { "save", "commit", "schema-change", "deploy", "publish" };
                if (!defaultEvents.Contains(context.TriggerEvent.ToLowerInvariant()))
                    return Task.FromResult(false);
            }
            else
            {
                // Check against configured events (case-insensitive)
                var eventMatches = Settings.TriggerEvents.Any(e =>
                    string.Equals(e, context.TriggerEvent, StringComparison.OrdinalIgnoreCase));

                if (!eventMatches)
                    return Task.FromResult(false);
            }

            // Check minimum interval to prevent version flooding
            if (!HasMinimumIntervalPassed(context))
                return Task.FromResult(false);

            // Check if change is significant enough
            if (!IsChangeSignificant(context))
                return Task.FromResult(false);

            // Check if we've hit the maximum version count
            if (IsAtMaxVersions(context))
                return Task.FromResult(false);

            return Task.FromResult(true);
        }

        /// <inheritdoc/>
        public override Task<VersionRetentionDecision> EvaluateRetentionAsync(VersionInfo version, CancellationToken ct = default)
        {
            // Event-based versions are typically kept longer as they represent significant milestones
            if (version.IsOnLegalHold || version.IsImmutable)
            {
                return Task.FromResult(new VersionRetentionDecision
                {
                    ShouldRetain = true,
                    Reason = version.IsOnLegalHold ? "Legal hold active" : "Version is immutable",
                    SuggestedAction = RetentionAction.Keep
                });
            }

            // Check explicit expiration
            if (version.ExpiresAt.HasValue && version.ExpiresAt.Value <= DateTimeOffset.UtcNow)
            {
                return Task.FromResult(new VersionRetentionDecision
                {
                    ShouldRetain = false,
                    Reason = "Version has passed its explicit expiration date",
                    SuggestedAction = RetentionAction.Delete
                });
            }

            // Event-based versions respect retention periods
            if (Settings.RetentionPeriod.HasValue)
            {
                var age = DateTimeOffset.UtcNow - version.CreatedAt;
                if (age > Settings.RetentionPeriod.Value)
                {
                    return Task.FromResult(new VersionRetentionDecision
                    {
                        ShouldRetain = false,
                        Reason = $"Event version exceeds retention period of {Settings.RetentionPeriod.Value.TotalDays} days",
                        SuggestedAction = RetentionAction.Delete
                    });
                }
            }

            // Event versions from critical events (deploy, publish) should be kept longer
            var isCriticalEvent = version.Metadata.TriggerEvent != null &&
                (version.Metadata.TriggerEvent.Equals("deploy", StringComparison.OrdinalIgnoreCase) ||
                 version.Metadata.TriggerEvent.Equals("publish", StringComparison.OrdinalIgnoreCase) ||
                 version.Metadata.TriggerEvent.Equals("schema-change", StringComparison.OrdinalIgnoreCase));

            // Consider tier transitions
            StorageTier? suggestedTier = null;
            var action = RetentionAction.Keep;
            var reason = isCriticalEvent
                ? $"Critical event version ({version.Metadata.TriggerEvent}) within retention policy"
                : "Event version within retention policy";

            var versionAge = DateTimeOffset.UtcNow - version.CreatedAt;
            foreach (var rule in Settings.TierTransitions.OrderBy(r => r.AgeThreshold))
            {
                // For critical events, delay tier transition by 50%
                var threshold = isCriticalEvent
                    ? TimeSpan.FromTicks((long)(rule.AgeThreshold.Ticks * 1.5))
                    : rule.AgeThreshold;

                if (versionAge >= threshold && version.StorageTier < rule.TargetTier)
                {
                    suggestedTier = rule.TargetTier;
                    action = RetentionAction.Archive;
                    reason = $"Event version eligible for {rule.TargetTier} storage transition";
                }
            }

            return Task.FromResult(new VersionRetentionDecision
            {
                ShouldRetain = true,
                Reason = reason,
                SuggestedAction = action,
                SuggestedTierTransition = suggestedTier
            });
        }

        /// <summary>
        /// Gets the minimum interval between event-based versions.
        /// Default is 1 minute to prevent event storms from creating excessive versions.
        /// </summary>
        public override TimeSpan? GetMinimumInterval() =>
            Settings.MinimumInterval ?? TimeSpan.FromMinutes(1);
    }
}
