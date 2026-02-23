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
        private readonly CascadeOverrideStore? _overrideStore;
        private readonly VersionedPolicyCache? _cache;
        private readonly MergeConflictResolver? _conflictResolver;
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
        /// <param name="overrideStore">
        /// Optional cascade override store for per-feature per-level user overrides (CASC-03).
        /// If null, no user overrides are applied and the engine falls back to policy explicit
        /// cascade or category defaults.
        /// </param>
        /// <param name="cache">
        /// Optional versioned policy cache for snapshot isolation (CASC-06). When provided,
        /// resolution reads from the cache snapshot instead of live store queries, ensuring
        /// in-flight operations see a consistent view.
        /// </param>
        /// <param name="conflictResolver">
        /// Optional merge conflict resolver for per-tag-key resolution (CASC-08). When provided,
        /// the Merge cascade strategy delegates conflicting keys to this resolver instead of
        /// always using child-wins semantics.
        /// </param>
        public PolicyResolutionEngine(
            IPolicyStore store,
            IPolicyPersistence persistence,
            OperationalProfile? defaultProfile = null,
            PolicyCategoryDefaults? categoryDefaults = null,
            CascadeOverrideStore? overrideStore = null,
            VersionedPolicyCache? cache = null,
            MergeConflictResolver? conflictResolver = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
            _activeProfile = defaultProfile ?? OperationalProfile.Standard();
            _categoryDefaults = categoryDefaults ?? new PolicyCategoryDefaults();
            _overrideStore = overrideStore;
            _cache = cache;
            _conflictResolver = conflictResolver;
        }

        /// <inheritdoc />
        public async Task<IEffectivePolicy> ResolveAsync(string featureId, PolicyResolutionContext context, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(featureId))
                throw new ArgumentException("Feature ID must not be null or empty.", nameof(featureId));
            if (context is null)
                throw new ArgumentNullException(nameof(context));

            // CASC-06: If cache is available, take a snapshot at the start of resolution.
            // All lookups for this resolution use this snapshot, even if policies change mid-operation.
            var snapshot = _cache?.GetSnapshot();

            var levels = ParsePathToLevels(context.Path);
            var resolutionChain = new List<FeaturePolicy>();

            // Walk from most-specific (deepest) to least-specific (VDE)
            for (var i = levels.Count - 1; i >= 0; i--)
            {
                var (level, pathForLevel) = levels[i];

                FeaturePolicy? policy;
                if (snapshot is not null)
                {
                    // Read from immutable snapshot (CASC-06 snapshot isolation)
                    policy = snapshot.TryGetPolicy(featureId, level, pathForLevel);
                }
                else
                {
                    // Fall back to direct store query (backward compatible)
                    policy = await _store.GetAsync(featureId, level, pathForLevel, ct).ConfigureAwait(false);
                }

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

            // CASC-06: Use snapshot timestamp when available so in-flight operations
            // record when their policy view was captured, not when resolution completed.
            var snapshotTimestamp = snapshot?.Timestamp ?? DateTimeOffset.UtcNow;

            return new EffectivePolicy(
                featureId: featureId,
                effectiveIntensity: intensity,
                effectiveAiAutonomy: aiAutonomy,
                appliedCascade: cascadeStrategy,
                decidedAtLevel: decidedAt,
                resolutionChain: resolutionChain.AsReadOnly(),
                mergedParameters: mergedParams,
                snapshotTimestamp: snapshotTimestamp);
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

        /// <summary>
        /// Validates a policy for circular references and structural issues before storing it (CASC-07).
        /// This is a pre-write validation hook: if validation fails (circular reference detected),
        /// <see cref="PolicyCircularReferenceException"/> is thrown and the policy is NOT stored.
        /// </summary>
        /// <param name="featureId">The feature identifier for the policy.</param>
        /// <param name="level">The hierarchy level at which the policy will be stored.</param>
        /// <param name="path">The VDE path at which the policy will be stored.</param>
        /// <param name="policy">The policy to validate.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A <see cref="PolicyValidationResult"/> with any warnings (errors cause an exception).</returns>
        /// <exception cref="PolicyCircularReferenceException">Thrown when a circular reference is detected.</exception>
        public async Task<PolicyValidationResult> ValidatePolicyAsync(
            string featureId,
            PolicyLevel level,
            string path,
            FeaturePolicy policy,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            // CircularReferenceDetector.ValidateAsync throws PolicyCircularReferenceException on cycles
            var result = await CircularReferenceDetector.ValidateAsync(
                featureId, level, path, policy, _store, ct: ct).ConfigureAwait(false);

            if (!result.IsValid)
            {
                // Non-circular blocking errors (e.g., chain depth exceeded)
                throw new InvalidOperationException(
                    $"Policy validation failed for '{featureId}' at '{path}': {string.Join("; ", result.Errors)}");
            }

            return result;
        }

        /// <summary>
        /// Notifies the versioned cache that the policy store has been modified.
        /// Updates the cache snapshot from the current store state. If no cache is configured,
        /// this method is a no-op.
        /// </summary>
        /// <param name="featureIds">The set of feature IDs to refresh in the cache.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task NotifyCacheUpdateAsync(IEnumerable<string> featureIds, CancellationToken ct = default)
        {
            if (_cache is null) return;
            await _cache.UpdateFromStoreAsync(_store, featureIds, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Sets a cascade strategy override for a specific feature at a specific hierarchy level
        /// and persists the change. Requires the engine to have been initialized with a
        /// <see cref="CascadeOverrideStore"/>.
        /// </summary>
        /// <param name="featureId">The feature identifier (e.g., "encryption", "compression").</param>
        /// <param name="level">The hierarchy level at which the override applies.</param>
        /// <param name="strategy">The cascade strategy to use instead of the category default.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no <see cref="CascadeOverrideStore"/> was provided at construction time.
        /// </exception>
        public async Task SetCascadeOverrideAsync(string featureId, PolicyLevel level, CascadeStrategy strategy, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            if (_overrideStore is null)
                throw new InvalidOperationException("No CascadeOverrideStore was configured for this engine instance.");

            _overrideStore.SetOverride(featureId, level, strategy);
            await _overrideStore.SaveToPersistenceAsync(_persistence, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Removes a cascade strategy override for a specific feature at a specific hierarchy level
        /// and persists the change. Returns <c>false</c> if the override did not exist.
        /// </summary>
        /// <param name="featureId">The feature identifier.</param>
        /// <param name="level">The hierarchy level.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns><c>true</c> if the override was found and removed; <c>false</c> otherwise.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no <see cref="CascadeOverrideStore"/> was provided at construction time.
        /// </exception>
        public async Task<bool> RemoveCascadeOverrideAsync(string featureId, PolicyLevel level, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            if (_overrideStore is null)
                throw new InvalidOperationException("No CascadeOverrideStore was configured for this engine instance.");

            var removed = _overrideStore.RemoveOverride(featureId, level);
            if (removed)
            {
                await _overrideStore.SaveToPersistenceAsync(_persistence, ct).ConfigureAwait(false);
            }

            return removed;
        }

        /// <summary>
        /// Returns all current cascade strategy overrides as a read-only dictionary.
        /// Returns an empty dictionary if no <see cref="CascadeOverrideStore"/> was configured.
        /// </summary>
        /// <returns>A snapshot of all stored cascade overrides keyed by (FeatureId, Level).</returns>
        public IReadOnlyDictionary<(string FeatureId, PolicyLevel Level), CascadeStrategy> GetCascadeOverrides()
        {
            if (_overrideStore is null)
                return new Dictionary<(string, PolicyLevel), CascadeStrategy>();

            return _overrideStore.GetAllOverrides();
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
        ///   <item><description>CASC-03: Check <see cref="CascadeOverrideStore"/> for a user override
        ///   at the most-specific level in the chain. User overrides with Enforce strategy also
        ///   participate in the Enforce scan above.</description></item>
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

            // CASC-05 extension: Check if any user override in the chain uses Enforce at a higher level.
            // This ensures user-injected Enforce overrides win over lower-level Override policies.
            if (_overrideStore is not null)
            {
                for (var i = 1; i < chain.Count; i++)
                {
                    if (_overrideStore.TryGetOverride(featureId, chain[i].Level, out var overrideStrategy)
                        && overrideStrategy == CascadeStrategy.Enforce
                        && (int)chain[i].Level > (int)mostSpecificLevel)
                    {
                        return CascadeStrategy.Enforce;
                    }
                }
            }

            // CASC-03: Check user override store for the most-specific level before policy explicit cascade
            if (_overrideStore is not null
                && _overrideStore.TryGetOverride(featureId, mostSpecificLevel, out var userOverride))
            {
                return userOverride;
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
            // CASC-08: For Merge strategy, delegate conflict resolution to the configured resolver
            if (strategy == CascadeStrategy.Merge && _conflictResolver is not null)
            {
                return ApplyMergeWithConflictResolver(chain);
            }

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
        /// Applies the Merge strategy with per-tag-key conflict resolution via <see cref="MergeConflictResolver"/> (CASC-08).
        /// Intensity and AI autonomy come from the most-specific entry (chain[0]).
        /// Custom parameters from all levels are collected and conflicts resolved per-key.
        /// </summary>
        private (int Intensity, AiAutonomyLevel AiAutonomy, Dictionary<string, string> MergedParams, PolicyLevel DecidedAt) ApplyMergeWithConflictResolver(
            IReadOnlyList<FeaturePolicy> chain)
        {
            var winner = chain[0];

            // Collect custom parameter dictionaries ordered most-specific first
            var paramsByLevel = new List<Dictionary<string, string>?>(chain.Count);
            for (var i = 0; i < chain.Count; i++)
            {
                paramsByLevel.Add(chain[i].CustomParameters);
            }

            var mergedParams = _conflictResolver!.ResolveAll(paramsByLevel);
            return (winner.IntensityLevel, winner.AiAutonomy, mergedParams, winner.Level);
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
