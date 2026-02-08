namespace DataWarehouse.Plugins.UltimateDataProtection.Versioning.Policies
{
    /// <summary>
    /// Intelligent versioning policy - AI-driven version creation based on change significance.
    /// Analyzes change patterns, criticality, and access frequency to determine optimal versioning.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This policy is suitable for:
    /// </para>
    /// <list type="bullet">
    ///   <item>Adaptive versioning based on data importance</item>
    ///   <item>Optimizing storage by versioning only significant changes</item>
    ///   <item>Machine learning model versioning</item>
    ///   <item>Dynamic content management</item>
    ///   <item>Intelligent backup strategies</item>
    ///   <item>Context-aware change tracking</item>
    /// </list>
    /// <para>
    /// Decision Factors:
    /// </para>
    /// <list type="bullet">
    ///   <item>Change Significance Score (0.0 - 1.0) - provided by AI analysis</item>
    ///   <item>Data Criticality Level - business importance of the item</item>
    ///   <item>Access Frequency - how often the item is accessed</item>
    ///   <item>Change Size - magnitude of the modification</item>
    ///   <item>Historical Patterns - learning from past versioning decisions</item>
    ///   <item>User/Process Context - who/what is making the change</item>
    /// </list>
    /// <para>
    /// The policy uses a configurable intelligence threshold (default 0.5) to determine
    /// whether a change is significant enough to warrant version creation.
    /// </para>
    /// </remarks>
    public sealed class IntelligentVersioningPolicy : VersioningPolicyBase
    {
        /// <inheritdoc/>
        public override string PolicyId => "intelligent-versioning";

        /// <inheritdoc/>
        public override string PolicyName => "Intelligent Versioning";

        /// <inheritdoc/>
        public override string Description =>
            "AI-driven version creation based on change significance analysis. " +
            "Considers criticality, access patterns, change size, and contextual factors to optimize versioning decisions.";

        /// <inheritdoc/>
        public override VersioningMode Mode => VersioningMode.Intelligent;

        /// <summary>
        /// Determines if a version should be created using intelligent analysis.
        /// Evaluates change significance score against the configured threshold.
        /// </summary>
        /// <param name="context">Version context with change and significance information.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if the change is significant enough based on AI analysis.</returns>
        public override Task<bool> ShouldCreateVersionAsync(VersionContext context, CancellationToken ct = default)
        {
            // Calculate overall significance score
            var significanceScore = CalculateSignificanceScore(context);

            // Get intelligence threshold from settings (default 0.5)
            var threshold = Settings.IntelligenceThreshold ?? 0.5;

            // Ensure threshold is in valid range
            threshold = Math.Clamp(threshold, 0.0, 1.0);

            // Compare significance against threshold
            if (significanceScore < threshold)
                return Task.FromResult(false);

            // Even with high significance, respect minimum interval if configured
            if (!HasMinimumIntervalPassed(context))
                return Task.FromResult(false);

            // Check if change meets minimum size requirements
            if (!IsChangeSignificant(context))
                return Task.FromResult(false);

            // Check maximum version count
            if (IsAtMaxVersions(context))
                return Task.FromResult(false);

            return Task.FromResult(true);
        }

        /// <summary>
        /// Calculates the overall significance score for a change.
        /// Combines multiple factors into a single score (0.0 - 1.0).
        /// </summary>
        /// <param name="context">Version context with change information.</param>
        /// <returns>Significance score from 0.0 (not significant) to 1.0 (highly significant).</returns>
        private double CalculateSignificanceScore(VersionContext context)
        {
            var scores = new List<(double Score, double Weight)>();

            // Factor 1: Explicit change significance from AI/external analysis
            if (context.AdditionalContext.TryGetValue("ChangeSignificance", out var sigObj) &&
                sigObj is double changeSignificance)
            {
                scores.Add((changeSignificance, 0.4)); // 40% weight
            }

            // Factor 2: Criticality level
            scores.Add((context.CriticalityLevel, 0.25)); // 25% weight

            // Factor 3: Access frequency (normalized)
            var accessScore = CalculateAccessFrequencyScore(context.RecentAccessCount);
            scores.Add((accessScore, 0.15)); // 15% weight

            // Factor 4: Change size significance (normalized)
            var sizeScore = CalculateChangeSizeScore(context);
            scores.Add((sizeScore, 0.2)); // 20% weight

            // If we have fewer factors, redistribute the weights
            if (scores.Count < 4)
            {
                var totalWeight = scores.Sum(s => s.Weight);
                if (totalWeight > 0)
                {
                    scores = scores.Select(s => (s.Score, s.Weight / totalWeight)).ToList();
                }
            }

            // Calculate weighted average
            var weightedScore = scores.Sum(s => s.Score * s.Weight);

            // Ensure score is in valid range
            return Math.Clamp(weightedScore, 0.0, 1.0);
        }

        /// <summary>
        /// Calculates significance score based on access frequency.
        /// Higher access frequency suggests more important data.
        /// </summary>
        /// <param name="accessCount">Recent access count.</param>
        /// <returns>Score from 0.0 to 1.0.</returns>
        private double CalculateAccessFrequencyScore(int accessCount)
        {
            if (accessCount <= 0)
                return 0.1; // Low baseline for unaccessed items

            // Logarithmic scale: 1-10 accesses = 0.3-0.5, 100+ = 0.8-1.0
            // This prevents very high access counts from dominating the score
            var score = Math.Log10(accessCount + 1) / Math.Log10(1000);
            return Math.Clamp(score, 0.1, 1.0);
        }

        /// <summary>
        /// Calculates significance score based on change size.
        /// Larger changes relative to total size suggest more significant modifications.
        /// </summary>
        /// <param name="context">Version context.</param>
        /// <returns>Score from 0.0 to 1.0.</returns>
        private double CalculateChangeSizeScore(VersionContext context)
        {
            if (context.ChangeSize == 0)
                return 0.0;

            // If we have previous size, calculate percentage change
            if (context.PreviousSize.HasValue && context.PreviousSize.Value > 0)
            {
                var changePercent = (double)context.ChangeSize / context.PreviousSize.Value;

                // Cap at 100% change (1.0 score)
                // Scale: 0-10% = 0.0-0.3, 10-50% = 0.3-0.7, 50%+ = 0.7-1.0
                if (changePercent < 0.1)
                    return changePercent * 3.0; // 0-10% -> 0-0.3
                else if (changePercent < 0.5)
                    return 0.3 + (changePercent - 0.1) * 1.0; // 10-50% -> 0.3-0.7
                else
                    return Math.Min(0.7 + (changePercent - 0.5) * 0.6, 1.0); // 50%+ -> 0.7-1.0
            }

            // If no previous size, use absolute change size
            // Logarithmic scale for absolute sizes
            var sizeInKB = context.ChangeSize / 1024.0;
            if (sizeInKB < 1)
                return 0.1; // < 1 KB
            else if (sizeInKB < 100)
                return 0.2 + (Math.Log10(sizeInKB) / 2.0) * 0.3; // 1-100 KB -> 0.2-0.5
            else if (sizeInKB < 10_000)
                return 0.5 + (Math.Log10(sizeInKB / 100.0) / 2.0) * 0.3; // 100 KB - 10 MB -> 0.5-0.8
            else
                return 0.8 + Math.Min((Math.Log10(sizeInKB / 10_000.0) / 2.0) * 0.2, 0.2); // 10+ MB -> 0.8-1.0
        }

        /// <inheritdoc/>
        public override Task<VersionRetentionDecision> EvaluateRetentionAsync(VersionInfo version, CancellationToken ct = default)
        {
            // Intelligent versioning keeps significant versions longer
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

            // Determine version significance from metadata
            var versionSignificance = version.Metadata.ChangeSignificance ?? 0.5;

            // Significant versions get longer retention
            TimeSpan? adjustedRetentionPeriod = Settings.RetentionPeriod;
            if (adjustedRetentionPeriod.HasValue)
            {
                // Highly significant versions (>0.8) get 2x retention
                // Moderately significant (0.5-0.8) get standard retention
                // Low significance (<0.5) get 0.5x retention
                if (versionSignificance > 0.8)
                    adjustedRetentionPeriod = TimeSpan.FromTicks((long)(adjustedRetentionPeriod.Value.Ticks * 2.0));
                else if (versionSignificance < 0.5)
                    adjustedRetentionPeriod = TimeSpan.FromTicks((long)(adjustedRetentionPeriod.Value.Ticks * 0.5));

                if (age > adjustedRetentionPeriod.Value)
                {
                    return Task.FromResult(new VersionRetentionDecision
                    {
                        ShouldRetain = false,
                        Reason = $"Intelligent version exceeds adjusted retention period " +
                                $"(significance: {versionSignificance:F2}, retention: {adjustedRetentionPeriod.Value.TotalDays:F1} days)",
                        SuggestedAction = RetentionAction.Delete
                    });
                }
            }

            // Consider tier transitions with significance-based delays
            StorageTier? suggestedTier = null;
            var action = RetentionAction.Keep;
            var reason = $"Intelligent version within retention policy (significance: {versionSignificance:F2})";

            foreach (var rule in Settings.TierTransitions.OrderBy(r => r.AgeThreshold))
            {
                // Delay tier transition for highly significant versions
                var adjustedThreshold = versionSignificance > 0.8
                    ? TimeSpan.FromTicks((long)(rule.AgeThreshold.Ticks * 1.5))
                    : rule.AgeThreshold;

                if (age >= adjustedThreshold && version.StorageTier < rule.TargetTier)
                {
                    suggestedTier = rule.TargetTier;
                    action = RetentionAction.Archive;
                    reason = $"Intelligent version eligible for {rule.TargetTier} storage transition " +
                            $"(significance: {versionSignificance:F2})";
                }
            }

            // Default tier transitions for intelligent versioning if none configured
            if (Settings.TierTransitions.Count == 0)
            {
                var standardThreshold = versionSignificance > 0.8 ? TimeSpan.FromDays(60) : TimeSpan.FromDays(30);
                var archiveThreshold = versionSignificance > 0.8 ? TimeSpan.FromDays(180) : TimeSpan.FromDays(90);

                if (age >= archiveThreshold && version.StorageTier < StorageTier.Archive)
                {
                    suggestedTier = StorageTier.Archive;
                    action = RetentionAction.Archive;
                    reason = $"Intelligent version ready for archive (significance: {versionSignificance:F2})";
                }
                else if (age >= standardThreshold && version.StorageTier < StorageTier.InfrequentAccess)
                {
                    suggestedTier = StorageTier.InfrequentAccess;
                    action = RetentionAction.Archive;
                    reason = $"Intelligent version ready for infrequent access (significance: {versionSignificance:F2})";
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
        /// Intelligent versioning respects configured minimum interval,
        /// defaulting to 30 seconds to allow rapid significant changes.
        /// </summary>
        public override TimeSpan? GetMinimumInterval() =>
            Settings.MinimumInterval ?? TimeSpan.FromSeconds(30);

        /// <summary>
        /// Intelligent versioning uses a moderate version limit,
        /// as it only versions significant changes.
        /// </summary>
        public override int? GetMaxVersionsPerItem() =>
            Settings.MaxVersionsPerItem ?? 1_000;
    }
}
