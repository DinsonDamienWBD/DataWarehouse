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
        private readonly PolicyCategoryDefaults _categoryDefaults;
        private volatile OperationalProfile _activeProfile;

        /// <summary>
        /// Initializes a new instance of <see cref="PolicyResolutionEngine"/>.
        /// </summary>
        /// <param name="store">The policy store to query during resolution.</param>
        /// <param name="persistence">The persistence layer for hydration and profile storage.</param>
        /// <param name="defaultProfile">
        /// Optional default operational profile. If null, <see cref="OperationalProfile.Standard()"/> is used.
        /// </param>
        /// <param name="categoryDefaults">
        /// Optional category-to-cascade-strategy defaults. If null, a default instance with
        /// built-in mappings (Security=MostRestrictive, Compliance=Enforce, etc.) is used.
        /// </param>
        public PolicyResolutionEngine(
            IPolicyStore store,
            IPolicyPersistence persistence,
            OperationalProfile? defaultProfile = null,
            PolicyCategoryDefaults? categoryDefaults = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
            _activeProfile = defaultProfile ?? OperationalProfile.Standard();
            _categoryDefaults = categoryDefaults ?? new PolicyCategoryDefaults();
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

            // Determine the effective cascade strategy
            var cascadeStrategy = DetermineEffectiveCascade(featureId, resolutionChain);

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

            var cascadeStrategy = DetermineEffectiveCascade(featureId, resolutionChain);
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
        /// Determines the effective cascade strategy for a feature and resolution chain.
        /// <para>
        /// Resolution order:
        /// <list type="number">
        ///   <item><description>CASC-05: If ANY entry in the chain at a HIGHER level than the most-specific
        ///   has <see cref="CascadeStrategy.Enforce"/>, use Enforce. This ensures a VDE-level Enforce
        ///   overrides a Container-level Override.</description></item>
        ///   <item><description>If the most-specific policy has an explicit non-Inherit cascade, use that.</description></item>
        ///   <item><description>Look up the feature's category default from <see cref="PolicyCategoryDefaults"/>.</description></item>
        /// </list>
        /// </para>
        /// </summary>
        private CascadeStrategy DetermineEffectiveCascade(string featureId, IReadOnlyList<FeaturePolicy> chain)
        {
            // CASC-05: Scan entire chain for Enforce at a higher level than the most-specific entry.
            // chain[0] is most-specific (e.g., Block), entries with larger PolicyLevel values are higher.
            var mostSpecificLevel = chain[0].Level;

            for (var i = 1; i < chain.Count; i++)
            {
                if (chain[i].Cascade == CascadeStrategy.Enforce && (int)chain[i].Level > (int)mostSpecificLevel)
                {
                    return CascadeStrategy.Enforce;
                }
            }

            // Use the most-specific policy's explicit cascade if it is not Inherit (the default/zero-equivalent)
            var explicitCascade = chain[0].Cascade;
            if (explicitCascade != CascadeStrategy.Inherit)
            {
                return explicitCascade;
            }

            // Fall back to category defaults
            return _categoryDefaults.GetDefaultStrategy(featureId);
        }

        /// <summary>
        /// Applies the cascade strategy to a resolution chain by dispatching to the
        /// appropriate <see cref="CascadeStrategies"/> static method.
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
            return strategy switch
            {
                CascadeStrategy.Inherit => CascadeStrategies.Inherit(chain),
                CascadeStrategy.Override => CascadeStrategies.Override(chain),
                CascadeStrategy.MostRestrictive => CascadeStrategies.MostRestrictive(chain),
                CascadeStrategy.Enforce => CascadeStrategies.Enforce(chain),
                CascadeStrategy.Merge => CascadeStrategies.Merge(chain),
                _ => CascadeStrategies.Override(chain), // Unknown strategy falls back to Override
            };
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
