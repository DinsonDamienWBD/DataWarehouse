namespace DataWarehouse.Plugins.UltimateDataProtection.Versioning.Policies
{
    /// <summary>
    /// Manual versioning policy - versions are only created when explicitly requested.
    /// Users must manually trigger version creation; no automatic versioning occurs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This policy is suitable for:
    /// </para>
    /// <list type="bullet">
    ///   <item>Milestone-based versioning (releases, deployments)</item>
    ///   <item>User-controlled checkpoint creation</item>
    ///   <item>Minimal storage overhead scenarios</item>
    ///   <item>Integration with external version control systems</item>
    /// </list>
    /// </remarks>
    public sealed class ManualVersioningPolicy : VersioningPolicyBase
    {
        /// <inheritdoc/>
        public override string PolicyId => "manual-versioning";

        /// <inheritdoc/>
        public override string PolicyName => "Manual Versioning";

        /// <inheritdoc/>
        public override string Description =>
            "Versions are created only when explicitly requested by users. " +
            "No automatic version creation occurs based on changes or schedules.";

        /// <inheritdoc/>
        public override VersioningMode Mode => VersioningMode.Manual;

        /// <summary>
        /// For manual versioning, this always returns false as versions are only created explicitly.
        /// The actual version creation bypasses policy check.
        /// </summary>
        /// <param name="context">Version context.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Always false - manual versions bypass this check.</returns>
        public override Task<bool> ShouldCreateVersionAsync(VersionContext context, CancellationToken ct = default)
        {
            // Manual policy - automatic version creation is always denied
            // Explicit CreateVersion calls bypass this check
            return Task.FromResult(false);
        }

        /// <inheritdoc/>
        public override Task<VersionRetentionDecision> EvaluateRetentionAsync(VersionInfo version, CancellationToken ct = default)
        {
            // Manual versions are typically kept longer as they represent explicit checkpoints
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

            // For manual versions, default to keeping them unless explicitly expired
            // or retention period exceeded
            if (Settings.RetentionPeriod.HasValue)
            {
                var age = DateTimeOffset.UtcNow - version.CreatedAt;
                if (age > Settings.RetentionPeriod.Value)
                {
                    return Task.FromResult(new VersionRetentionDecision
                    {
                        ShouldRetain = false,
                        Reason = $"Version exceeds retention period of {Settings.RetentionPeriod.Value.TotalDays} days",
                        SuggestedAction = RetentionAction.Delete
                    });
                }
            }

            // Consider tier transitions for older manual versions
            StorageTier? suggestedTier = null;
            var action = RetentionAction.Keep;
            var reason = "Manual version within retention policy";

            var versionAge = DateTimeOffset.UtcNow - version.CreatedAt;
            foreach (var rule in Settings.TierTransitions.OrderBy(r => r.AgeThreshold))
            {
                if (versionAge >= rule.AgeThreshold && version.StorageTier < rule.TargetTier)
                {
                    suggestedTier = rule.TargetTier;
                    action = RetentionAction.Archive;
                    reason = $"Manual version eligible for {rule.TargetTier} storage transition";
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
        /// Manual versioning has no minimum interval.
        /// </summary>
        public override TimeSpan? GetMinimumInterval() => null;
    }
}
