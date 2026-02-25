using System;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;

namespace DataWarehouse.SDK.Infrastructure.Policy
{
    /// <summary>
    /// Static methods implementing each <see cref="CascadeStrategy"/> algorithm.
    /// Every method accepts a resolution chain ordered most-specific first (e.g., Block before VDE)
    /// and returns the resolved intensity, AI autonomy, merged custom parameters, and the level
    /// at which the decision was made.
    /// <para>
    /// <list type="bullet">
    ///   <item><description>Inherit: most-specific entry wins; custom parameters merged from all levels (parent first, child overwrites).</description></item>
    ///   <item><description>Override: most-specific entry wins; parent parameters discarded entirely.</description></item>
    ///   <item><description>MostRestrictive: lowest intensity, most restrictive AI autonomy across entire chain (CASC-02).</description></item>
    ///   <item><description>Enforce: highest-level entry with Cascade=Enforce wins unconditionally (CASC-05).</description></item>
    ///   <item><description>Merge: custom parameters combined from all levels (parent first, child overwrites); intensity/autonomy from most-specific.</description></item>
    /// </list>
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 70: Cascade strategy implementations (CASC-02, CASC-05)")]
    public static class CascadeStrategies
    {
        /// <summary>
        /// Inherit strategy: uses the most-specific (chain[0]) entry for intensity and AI autonomy.
        /// Custom parameters are merged from all levels, with more-specific entries overwriting
        /// less-specific keys (parent values provide defaults, child values override).
        /// </summary>
        /// <param name="chain">Resolution chain ordered most-specific first. Must contain at least one entry.</param>
        /// <returns>Resolved tuple of (intensity, aiAutonomy, mergedParameters, decidedAtLevel).</returns>
        public static (int Intensity, AiAutonomyLevel AiAutonomy, Dictionary<string, string> MergedParams, PolicyLevel DecidedAt) Inherit(
            IReadOnlyList<FeaturePolicy> chain)
        {
            var winner = chain[0];
            var mergedParams = new Dictionary<string, string>(StringComparer.Ordinal);

            // Walk from least-specific (last) to most-specific (first): child keys overwrite parent keys
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                if (chain[i].CustomParameters is { } customParams)
                {
                    foreach (var kvp in customParams)
                    {
                        mergedParams[kvp.Key] = kvp.Value;
                    }
                }
            }

            return (winner.IntensityLevel, winner.AiAutonomy, mergedParams, winner.Level);
        }

        /// <summary>
        /// Override strategy: most-specific entry takes full precedence. All parent values
        /// (including custom parameters) are completely discarded.
        /// </summary>
        /// <param name="chain">Resolution chain ordered most-specific first. Must contain at least one entry.</param>
        /// <returns>Resolved tuple of (intensity, aiAutonomy, mergedParameters, decidedAtLevel).</returns>
        public static (int Intensity, AiAutonomyLevel AiAutonomy, Dictionary<string, string> MergedParams, PolicyLevel DecidedAt) Override(
            IReadOnlyList<FeaturePolicy> chain)
        {
            var winner = chain[0];
            var mergedParams = new Dictionary<string, string>(StringComparer.Ordinal);

            if (winner.CustomParameters is { } winnerParams)
            {
                foreach (var kvp in winnerParams)
                {
                    mergedParams[kvp.Key] = kvp.Value;
                }
            }

            return (winner.IntensityLevel, winner.AiAutonomy, mergedParams, winner.Level);
        }

        /// <summary>
        /// MostRestrictive strategy: walks the entire chain and selects the lowest intensity
        /// and most restrictive (lowest enum ordinal) AI autonomy level. Custom parameters
        /// are intersected: only keys present in ALL entries are kept, using the most restrictive
        /// value (lowest numeric or first alphabetically).
        /// <para>
        /// DecidedAt is the level of the entry that contributed the minimum intensity value.
        /// </para>
        /// </summary>
        /// <param name="chain">Resolution chain ordered most-specific first. Must contain at least one entry.</param>
        /// <returns>Resolved tuple of (intensity, aiAutonomy, mergedParameters, decidedAtLevel).</returns>
        public static (int Intensity, AiAutonomyLevel AiAutonomy, Dictionary<string, string> MergedParams, PolicyLevel DecidedAt) MostRestrictive(
            IReadOnlyList<FeaturePolicy> chain)
        {
            var minIntensity = chain[0].IntensityLevel;
            var decidedAt = chain[0].Level;
            var minAiAutonomy = chain[0].AiAutonomy;

            for (var i = 1; i < chain.Count; i++)
            {
                var entry = chain[i];

                if (entry.IntensityLevel < minIntensity)
                {
                    minIntensity = entry.IntensityLevel;
                    decidedAt = entry.Level;
                }

                if ((int)entry.AiAutonomy < (int)minAiAutonomy)
                {
                    minAiAutonomy = entry.AiAutonomy;
                }
            }

            // Intersect custom parameters: keep only keys present in ALL entries with non-null parameters.
            // For each surviving key, pick the most restrictive value (lowest numeric, or first alphabetically).
            var mergedParams = IntersectMostRestrictiveParams(chain);

            return (minIntensity, minAiAutonomy, mergedParams, decidedAt);
        }

        /// <summary>
        /// Enforce strategy: finds the HIGHEST-level entry (largest <see cref="PolicyLevel"/> enum value,
        /// closest to VDE) with <see cref="CascadeStrategy.Enforce"/>. That entry's values win
        /// unconditionally, even if a lower-level entry has <see cref="CascadeStrategy.Override"/>.
        /// If no Enforce entry exists in the chain, falls back to Override behavior.
        /// <para>
        /// This implements the CASC-05 critical requirement: "Enforce at higher level overrides
        /// Override at lower level."
        /// </para>
        /// </summary>
        /// <param name="chain">Resolution chain ordered most-specific first. Must contain at least one entry.</param>
        /// <returns>Resolved tuple of (intensity, aiAutonomy, mergedParameters, decidedAtLevel).</returns>
        public static (int Intensity, AiAutonomyLevel AiAutonomy, Dictionary<string, string> MergedParams, PolicyLevel DecidedAt) Enforce(
            IReadOnlyList<FeaturePolicy> chain)
        {
            // Find highest-level (largest PolicyLevel value) entry with Cascade=Enforce
            FeaturePolicy? enforcer = null;

            for (var i = 0; i < chain.Count; i++)
            {
                var entry = chain[i];
                if (entry.Cascade == CascadeStrategy.Enforce)
                {
                    if (enforcer is null || (int)entry.Level > (int)enforcer.Level)
                    {
                        enforcer = entry;
                    }
                }
            }

            // If no Enforce entry found, fall back to Override behavior
            if (enforcer is null)
            {
                return Override(chain);
            }

            // Enforcer wins unconditionally
            var mergedParams = new Dictionary<string, string>(StringComparer.Ordinal);
            if (enforcer.CustomParameters is { } enforcerParams)
            {
                foreach (var kvp in enforcerParams)
                {
                    mergedParams[kvp.Key] = kvp.Value;
                }
            }

            return (enforcer.IntensityLevel, enforcer.AiAutonomy, mergedParams, enforcer.Level);
        }

        /// <summary>
        /// Merge strategy: combines custom parameters from all entries in the chain.
        /// Starts from least-specific (last in chain), overlays more-specific keys on top,
        /// so child keys overwrite parent keys. Intensity and AI autonomy come from the
        /// most-specific entry (chain[0]).
        /// </summary>
        /// <param name="chain">Resolution chain ordered most-specific first. Must contain at least one entry.</param>
        /// <returns>Resolved tuple of (intensity, aiAutonomy, mergedParameters, decidedAtLevel).</returns>
        public static (int Intensity, AiAutonomyLevel AiAutonomy, Dictionary<string, string> MergedParams, PolicyLevel DecidedAt) Merge(
            IReadOnlyList<FeaturePolicy> chain)
        {
            var winner = chain[0];
            var mergedParams = new Dictionary<string, string>(StringComparer.Ordinal);

            // Walk from least-specific (last) to most-specific (first): child keys overwrite parent keys
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                if (chain[i].CustomParameters is { } customParams)
                {
                    foreach (var kvp in customParams)
                    {
                        mergedParams[kvp.Key] = kvp.Value;
                    }
                }
            }

            return (winner.IntensityLevel, winner.AiAutonomy, mergedParams, winner.Level);
        }

        /// <summary>
        /// Intersects custom parameters across the chain, keeping only keys present in ALL
        /// entries that have non-null custom parameters. For each surviving key, selects the
        /// most restrictive value: if parseable as a number, the lowest value wins; otherwise
        /// the alphabetically-first string wins.
        /// </summary>
        private static Dictionary<string, string> IntersectMostRestrictiveParams(IReadOnlyList<FeaturePolicy> chain)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);

            // Collect entries that have non-null custom parameters
            var entriesWithParams = new List<Dictionary<string, string>>();
            for (var i = 0; i < chain.Count; i++)
            {
                if (chain[i].CustomParameters is { Count: > 0 } cp)
                {
                    entriesWithParams.Add(cp);
                }
            }

            if (entriesWithParams.Count == 0)
                return result;

            // Start with keys from the first entry with params
            var candidateKeys = new HashSet<string>(entriesWithParams[0].Keys, StringComparer.Ordinal);

            // Intersect with all other entries
            for (var i = 1; i < entriesWithParams.Count; i++)
            {
                candidateKeys.IntersectWith(entriesWithParams[i].Keys);
            }

            // For each surviving key, pick the most restrictive value
            foreach (var key in candidateKeys)
            {
                string? mostRestrictive = null;

                for (var i = 0; i < entriesWithParams.Count; i++)
                {
                    var value = entriesWithParams[i][key];

                    if (mostRestrictive is null)
                    {
                        mostRestrictive = value;
                        continue;
                    }

                    // Try numeric comparison first
                    if (double.TryParse(value, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var numValue) &&
                        double.TryParse(mostRestrictive, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var numCurrent))
                    {
                        if (numValue < numCurrent)
                        {
                            mostRestrictive = value;
                        }
                    }
                    else
                    {
                        // Alphabetically first is most restrictive
                        if (string.Compare(value, mostRestrictive, StringComparison.Ordinal) < 0)
                        {
                            mostRestrictive = value;
                        }
                    }
                }

                if (mostRestrictive is not null)
                {
                    result[key] = mostRestrictive;
                }
            }

            return result;
        }
    }
}
