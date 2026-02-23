using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;

namespace DataWarehouse.SDK.Infrastructure.Policy
{
    /// <summary>
    /// Core cascade resolution engine implementing <see cref="IPolicyEngine"/>.
    /// Walks the VDE hierarchy from most-specific to least-specific level, applying
    /// <see cref="CascadeStrategy"/> rules to produce an <see cref="IEffectivePolicy"/> snapshot.
    /// <para>
    /// Path format: "/vdeName/containerName/objectName/chunkId/blockId".
    /// Segment count determines the deepest addressable level:
    /// 1 = VDE, 2 = Container, 3 = Object, 4 = Chunk, 5 = Block.
    /// </para>
    /// <para>
    /// Empty intermediate levels are transparently skipped during resolution (CASC-04).
    /// The cascade strategy application is a protected virtual method so that Plan 02
    /// can extend it with MostRestrictive, Enforce, and Merge without modifying this file.
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 70: Cascade engine (CASC-01, CASC-04)")]
    public class PolicyResolutionEngine : IPolicyEngine
    {
        private readonly IPolicyStore _store;
        private readonly IPolicyPersistence _persistence;
        private volatile OperationalProfile _activeProfile;

        /// <summary>
        /// Initializes a new instance of <see cref="PolicyResolutionEngine"/>.
        /// </summary>
        /// <param name="store">The policy store to query during resolution.</param>
        /// <param name="persistence">The persistence layer for hydration and profile storage.</param>
        /// <param name="defaultProfile">
        /// Optional default operational profile. If null, <see cref="OperationalProfile.Standard()"/> is used.
        /// </param>
        public PolicyResolutionEngine(
            IPolicyStore store,
            IPolicyPersistence persistence,
            OperationalProfile? defaultProfile = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
            _activeProfile = defaultProfile ?? OperationalProfile.Standard();
        }

        /// <inheritdoc />
        public async Task<IEffectivePolicy> ResolveAsync(string featureId, PolicyResolutionContext context, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(featureId))
                throw new ArgumentException("Feature ID must not be null or empty.", nameof(featureId));
            if (context is null)
                throw new ArgumentNullException(nameof(context));

            var levels = ParsePathToLevels(context.Path);
            var resolutionChain = new List<FeaturePolicy>();

            // Walk from most-specific (deepest) to least-specific (VDE)
            for (var i = levels.Count - 1; i >= 0; i--)
            {
                var (level, pathForLevel) = levels[i];
                var policy = await _store.GetAsync(featureId, level, pathForLevel, ct).ConfigureAwait(false);

                if (policy is not null)
                {
                    resolutionChain.Add(policy);
                }
            }

            // Fall back to active profile if no overrides found
            var profile = _activeProfile;
            if (resolutionChain.Count == 0)
            {
                if (profile.FeaturePolicies.TryGetValue(featureId, out var profilePolicy))
                {
                    resolutionChain.Add(profilePolicy);
                }
            }

            // If still empty, return default policy
            if (resolutionChain.Count == 0)
            {
                return BuildDefaultPolicy(featureId);
            }

            // Determine the cascade strategy from the most-specific policy
            var cascadeStrategy = resolutionChain[0].Cascade;

            // Apply cascade strategy
            var (intensity, aiAutonomy, mergedParams, decidedAt) = ApplyCascade(cascadeStrategy, resolutionChain);

            return new EffectivePolicy(
                featureId: featureId,
                effectiveIntensity: intensity,
                effectiveAiAutonomy: aiAutonomy,
                appliedCascade: cascadeStrategy,
                decidedAtLevel: decidedAt,
                resolutionChain: resolutionChain.AsReadOnly(),
                mergedParameters: mergedParams,
                snapshotTimestamp: DateTimeOffset.UtcNow);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyDictionary<string, IEffectivePolicy>> ResolveAllAsync(PolicyResolutionContext context, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            var profile = _activeProfile;
            var result = new Dictionary<string, IEffectivePolicy>();

            foreach (var featureId in profile.FeaturePolicies.Keys)
            {
                var effective = await ResolveAsync(featureId, context, ct).ConfigureAwait(false);
                result[featureId] = effective;
            }

            return result;
        }

        /// <inheritdoc />
        public Task<OperationalProfile> GetActiveProfileAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_activeProfile);
        }

        /// <inheritdoc />
        public async Task SetActiveProfileAsync(OperationalProfile profile, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            if (profile is null)
                throw new ArgumentNullException(nameof(profile));

            _activeProfile = profile;
            await _persistence.SaveProfileAsync(profile, ct).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<IEffectivePolicy> SimulateAsync(string featureId, PolicyResolutionContext context, FeaturePolicy hypotheticalPolicy, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(featureId))
                throw new ArgumentException("Feature ID must not be null or empty.", nameof(featureId));
            if (context is null)
                throw new ArgumentNullException(nameof(context));
            if (hypotheticalPolicy is null)
                throw new ArgumentNullException(nameof(hypotheticalPolicy));

            var levels = ParsePathToLevels(context.Path);
            var resolutionChain = new List<FeaturePolicy>();

            // Walk from most-specific (deepest) to least-specific (VDE)
            for (var i = levels.Count - 1; i >= 0; i--)
            {
                var (level, pathForLevel) = levels[i];

                // Inject hypothetical policy at the matching level and path
                if (level == hypotheticalPolicy.Level && string.Equals(pathForLevel, context.Path, StringComparison.Ordinal))
                {
                    resolutionChain.Add(hypotheticalPolicy);
                }
                else
                {
                    var policy = await _store.GetAsync(featureId, level, pathForLevel, ct).ConfigureAwait(false);
                    if (policy is not null)
                    {
                        resolutionChain.Add(policy);
                    }
                }
            }

            // Fall back to active profile if no overrides found
            var profile = _activeProfile;
            if (resolutionChain.Count == 0)
            {
                if (profile.FeaturePolicies.TryGetValue(featureId, out var profilePolicy))
                {
                    resolutionChain.Add(profilePolicy);
                }
            }

            if (resolutionChain.Count == 0)
            {
                return BuildDefaultPolicy(featureId);
            }

            var cascadeStrategy = resolutionChain[0].Cascade;
            var (intensity, aiAutonomy, mergedParams, decidedAt) = ApplyCascade(cascadeStrategy, resolutionChain);

            return new EffectivePolicy(
                featureId: featureId,
                effectiveIntensity: intensity,
                effectiveAiAutonomy: aiAutonomy,
                appliedCascade: cascadeStrategy,
                decidedAtLevel: decidedAt,
                resolutionChain: resolutionChain.AsReadOnly(),
                mergedParameters: mergedParams,
                snapshotTimestamp: DateTimeOffset.UtcNow);
        }

        /// <summary>
        /// Applies the cascade strategy to a resolution chain. Handles Inherit and Override
        /// as the baseline. Designed as a virtual method so Plan 02 can override to add
        /// MostRestrictive, Enforce, and Merge strategies.
        /// </summary>
        /// <param name="strategy">The cascade strategy to apply.</param>
        /// <param name="chain">The resolution chain, ordered most-specific first.</param>
        /// <returns>
        /// A tuple of (intensity, aiAutonomy, mergedParameters, decidedAtLevel).
        /// </returns>
        protected virtual (int Intensity, AiAutonomyLevel AiAutonomy, Dictionary<string, string> MergedParams, PolicyLevel DecidedAt) ApplyCascade(
            CascadeStrategy strategy,
            IReadOnlyList<FeaturePolicy> chain)
        {
            // Both Inherit and Override resolve to the first non-null in chain (most-specific)
            // since the chain already has only non-null entries ordered most-specific first.
            //
            // Inherit: use first non-null, walk up if null (already handled by skip logic in ResolveAsync)
            // Override: most-specific takes full precedence, ignoring parents
            //
            // For strategies not yet implemented (MostRestrictive, Enforce, Merge),
            // fall back to Override behavior. Plan 02 will override this method.

            var winner = chain[0];
            var mergedParams = new Dictionary<string, string>();

            switch (strategy)
            {
                case CascadeStrategy.Inherit:
                    // Inherit: use the most-specific non-null policy.
                    // Merge custom parameters from all levels (least-specific first, most-specific wins).
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

                case CascadeStrategy.Override:
                    // Override: most-specific only, discard all parent parameters
                    if (winner.CustomParameters is { } winnerParams)
                    {
                        foreach (var kvp in winnerParams)
                        {
                            mergedParams[kvp.Key] = kvp.Value;
                        }
                    }

                    return (winner.IntensityLevel, winner.AiAutonomy, mergedParams, winner.Level);

                default:
                    // MostRestrictive, Enforce, Merge -- placeholder until Plan 02
                    // Fall back to Override behavior
                    if (winner.CustomParameters is { } defaultParams)
                    {
                        foreach (var kvp in defaultParams)
                        {
                            mergedParams[kvp.Key] = kvp.Value;
                        }
                    }

                    return (winner.IntensityLevel, winner.AiAutonomy, mergedParams, winner.Level);
            }
        }

        /// <summary>
        /// Parses a VDE path into an ordered list of (level, pathPrefix) tuples from
        /// least-specific (VDE) to most-specific (Block).
        /// </summary>
        /// <param name="path">
        /// The VDE path (e.g., "/vdeName/containerName/objectName/chunkId/blockId").
        /// </param>
        /// <returns>
        /// Ordered list from VDE (index 0) to the deepest addressable level.
        /// </returns>
        private static List<(PolicyLevel Level, string PathForLevel)> ParsePathToLevels(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("Path must not be null or empty.", nameof(path));

            // Normalize: ensure leading slash, remove trailing slash
            var normalized = path.StartsWith('/') ? path : "/" + path;
            normalized = normalized.TrimEnd('/');

            // Split into segments (first element after split on leading "/" is empty)
            var parts = normalized.Split('/');
            var segments = new List<string>();
            for (var i = 0; i < parts.Length; i++)
            {
                if (!string.IsNullOrEmpty(parts[i]))
                    segments.Add(parts[i]);
            }

            if (segments.Count == 0)
                throw new ArgumentException("Path must contain at least one segment.", nameof(path));

            // Map segment count to levels
            // 1 segment = VDE only
            // 2 segments = VDE + Container
            // 3 segments = VDE + Container + Object
            // 4 segments = VDE + Container + Object + Chunk
            // 5 segments = VDE + Container + Object + Chunk + Block
            var levelMap = new[]
            {
                PolicyLevel.VDE,       // segment 1
                PolicyLevel.Container, // segment 2
                PolicyLevel.Object,    // segment 3
                PolicyLevel.Chunk,     // segment 4
                PolicyLevel.Block      // segment 5
            };

            var result = new List<(PolicyLevel Level, string PathForLevel)>();
            var maxLevels = Math.Min(segments.Count, levelMap.Length);

            for (var i = 0; i < maxLevels; i++)
            {
                // Build path prefix up to this level
                var pathPrefix = "/" + string.Join("/", segments.Take(i + 1));
                result.Add((levelMap[i], pathPrefix));
            }

            return result;
        }

        /// <summary>
        /// Builds a default effective policy when no overrides exist and no profile entry is found.
        /// </summary>
        private static EffectivePolicy BuildDefaultPolicy(string featureId)
        {
            return new EffectivePolicy(
                featureId: featureId,
                effectiveIntensity: 50,
                effectiveAiAutonomy: AiAutonomyLevel.SuggestExplain,
                appliedCascade: CascadeStrategy.Inherit,
                decidedAtLevel: PolicyLevel.VDE,
                resolutionChain: Array.Empty<FeaturePolicy>(),
                mergedParameters: new Dictionary<string, string>(),
                snapshotTimestamp: DateTimeOffset.UtcNow);
        }
    }
}
