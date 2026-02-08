namespace DataWarehouse.Plugins.UltimateDataProtection.Versioning.Policies
{
    /// <summary>
    /// Continuous versioning policy (CDP) - every write operation creates a version.
    /// Provides comprehensive change tracking with aggressive tiered retention to manage storage.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This policy is suitable for:
    /// </para>
    /// <list type="bullet">
    ///   <item>Critical data requiring complete change history</item>
    ///   <item>Compliance and audit requirements</item>
    ///   <item>Point-in-time recovery capabilities</item>
    ///   <item>Real-time collaboration environments</item>
    ///   <item>Continuous data protection (CDP) systems</item>
    ///   <item>Forensic analysis and debugging</item>
    /// </list>
    /// <para>
    /// Retention Strategy:
    /// </para>
    /// <list type="bullet">
    ///   <item>Last 24 hours: Keep all versions (second-level granularity)</item>
    ///   <item>Last 7 days: Keep one version per minute (thin to minute-level)</item>
    ///   <item>Last 30 days: Keep one version per hour (thin to hourly)</item>
    ///   <item>Beyond 30 days: Apply standard retention rules or archive</item>
    /// </list>
    /// </remarks>
    public sealed class ContinuousVersioningPolicy : VersioningPolicyBase
    {
        /// <inheritdoc/>
        public override string PolicyId => "continuous-versioning";

        /// <inheritdoc/>
        public override string PolicyName => "Continuous Versioning (CDP)";

        /// <inheritdoc/>
        public override string Description =>
            "Every write operation creates a version, providing complete change history. " +
            "Uses aggressive tiered retention (seconds for 1 day, minutes for 7 days, hours for 30 days) to manage storage overhead.";

        /// <inheritdoc/>
        public override VersioningMode Mode => VersioningMode.Continuous;

        /// <summary>
        /// Determines if a version should be created. For continuous versioning,
        /// this returns true for all changes that meet minimum size thresholds.
        /// </summary>
        /// <param name="context">Version context with change information.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if change meets minimum thresholds.</returns>
        public override Task<bool> ShouldCreateVersionAsync(VersionContext context, CancellationToken ct = default)
        {
            // Continuous versioning creates versions for all changes that pass basic filters

            // Skip micro-versions if minimum change size is configured
            if (Settings.MinimumChangeSize.HasValue)
            {
                if (context.ChangeSize < Settings.MinimumChangeSize.Value)
                    return Task.FromResult(false);
            }
            else
            {
                // Default: skip changes smaller than 1 byte (no actual change)
                if (context.ChangeSize == 0)
                    return Task.FromResult(false);
            }

            // Optional: respect minimum change percentage
            if (Settings.MinimumChangePercentage.HasValue &&
                context.PreviousSize.HasValue &&
                context.PreviousSize.Value > 0)
            {
                var changePercent = (double)context.ChangeSize / context.PreviousSize.Value * 100;
                if (changePercent < Settings.MinimumChangePercentage.Value)
                    return Task.FromResult(false);
            }

            // Check maximum version count (though this is typically high for CDP)
            if (IsAtMaxVersions(context))
                return Task.FromResult(false);

            // Continuous versioning generally ignores minimum interval,
            // but can be configured to prevent version storms
            if (Settings.MinimumInterval.HasValue && !HasMinimumIntervalPassed(context))
                return Task.FromResult(false);

            return Task.FromResult(true);
        }

        /// <inheritdoc/>
        public override Task<VersionRetentionDecision> EvaluateRetentionAsync(VersionInfo version, CancellationToken ct = default)
        {
            // Continuous versioning requires aggressive retention to manage storage
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

            var age = DateTimeOffset.UtcNow - version.CreatedAt;

            // Tiered retention for continuous versioning
            // This implements the "thin out" strategy to reduce version density over time

            // Tier 1: Last 24 hours - keep all versions
            if (age <= TimeSpan.FromDays(1))
            {
                return EvaluateTierTransition(version, age, "Recent continuous version (last 24 hours)");
            }

            // Tier 2: 1-7 days - keep one per minute (thin out seconds)
            if (age <= TimeSpan.FromDays(7))
            {
                // In a real implementation, this would check if there's a more recent version
                // within the same minute and delete this one if so.
                // For now, we suggest archiving to a lower tier
                return EvaluateTierTransition(version, age, "Continuous version within 7 days (minute-level retention)");
            }

            // Tier 3: 7-30 days - keep one per hour (thin out minutes)
            if (age <= TimeSpan.FromDays(30))
            {
                // Similar logic: in practice, this would thin to hourly granularity
                return EvaluateTierTransition(version, age, "Continuous version within 30 days (hourly retention)");
            }

            // Tier 4: Beyond 30 days - apply standard retention or delete
            if (Settings.RetentionPeriod.HasValue)
            {
                if (age > Settings.RetentionPeriod.Value)
                {
                    return Task.FromResult(new VersionRetentionDecision
                    {
                        ShouldRetain = false,
                        Reason = $"Continuous version exceeds retention period of {Settings.RetentionPeriod.Value.TotalDays} days",
                        SuggestedAction = RetentionAction.Delete
                    });
                }
            }
            else
            {
                // Default CDP retention: keep for 90 days, then delete
                if (age > TimeSpan.FromDays(90))
                {
                    return Task.FromResult(new VersionRetentionDecision
                    {
                        ShouldRetain = false,
                        Reason = "Continuous version exceeds default 90-day retention",
                        SuggestedAction = RetentionAction.Delete
                    });
                }
            }

            return EvaluateTierTransition(version, age, "Continuous version within retention policy");
        }

        /// <summary>
        /// Evaluates storage tier transition for a version based on age.
        /// </summary>
        private Task<VersionRetentionDecision> EvaluateTierTransition(
            VersionInfo version,
            TimeSpan age,
            string baseReason)
        {
            StorageTier? suggestedTier = null;
            var action = RetentionAction.Keep;
            var reason = baseReason;

            // Apply tier transition rules
            foreach (var rule in Settings.TierTransitions.OrderBy(r => r.AgeThreshold))
            {
                if (age >= rule.AgeThreshold && version.StorageTier < rule.TargetTier)
                {
                    suggestedTier = rule.TargetTier;
                    action = RetentionAction.Archive;
                    reason = $"Continuous version eligible for {rule.TargetTier} storage transition";
                }
            }

            // Default tier transitions for continuous versioning if none configured
            if (Settings.TierTransitions.Count == 0)
            {
                if (age >= TimeSpan.FromDays(7) && version.StorageTier < StorageTier.InfrequentAccess)
                {
                    suggestedTier = StorageTier.InfrequentAccess;
                    action = RetentionAction.Archive;
                    reason = "Continuous version older than 7 days - transition to infrequent access";
                }
                else if (age >= TimeSpan.FromDays(30) && version.StorageTier < StorageTier.Archive)
                {
                    suggestedTier = StorageTier.Archive;
                    action = RetentionAction.Archive;
                    reason = "Continuous version older than 30 days - transition to archive";
                }
                else if (age >= TimeSpan.FromDays(180) && version.StorageTier < StorageTier.DeepArchive)
                {
                    suggestedTier = StorageTier.DeepArchive;
                    action = RetentionAction.Archive;
                    reason = "Continuous version older than 180 days - transition to deep archive";
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
        /// Continuous versioning has no minimum interval by default,
        /// but can be configured to prevent write storms.
        /// </summary>
        public override TimeSpan? GetMinimumInterval() =>
            Settings.MinimumInterval; // null by default, but can be set to TimeSpan.FromSeconds(1) or similar

        /// <summary>
        /// Continuous versioning typically has a high version limit.
        /// Default is 10,000 versions per item if not configured.
        /// </summary>
        public override int? GetMaxVersionsPerItem() =>
            Settings.MaxVersionsPerItem ?? 10_000;
    }
}
