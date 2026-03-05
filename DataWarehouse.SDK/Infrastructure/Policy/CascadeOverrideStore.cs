using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;

namespace DataWarehouse.SDK.Infrastructure.Policy
{
    /// <summary>
    /// Stores per-feature per-level cascade strategy overrides that let users customize
    /// how policies cascade for specific features at specific hierarchy levels.
    /// <para>
    /// Thread-safe via <see cref="ConcurrentDictionary{TKey, TValue}"/> with composite
    /// "featureId:level" keys. Bounded to <see cref="MaxOverrides"/> entries to prevent abuse.
    /// </para>
    /// <para>
    /// Persistence uses a well-known feature ID convention (<c>__cascade_override__</c>) to
    /// store override configuration as a <see cref="FeaturePolicy"/> in the existing
    /// <see cref="IPolicyPersistence"/> layer, with overrides serialized in the
    /// <see cref="FeaturePolicy.CustomParameters"/> dictionary (key = "featureId:level",
    /// value = strategy enum name).
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 70: User cascade overrides (CASC-03)")]
    public sealed class CascadeOverrideStore
    {
        /// <summary>
        /// Maximum number of overrides allowed to prevent unbounded memory growth.
        /// </summary>
        public const int MaxOverrides = 10_000;

        /// <summary>
        /// Well-known feature ID used for persisting cascade overrides in the policy persistence layer.
        /// </summary>
        internal const string PersistenceFeatureId = "__cascade_override__";

        /// <summary>
        /// Path used for the persistence entry.
        /// </summary>
        internal const string PersistencePath = "/__cascade_overrides__";

        private readonly ConcurrentDictionary<string, CascadeStrategy> _overrides = new(StringComparer.Ordinal);

        /// <summary>
        /// Gets the current number of stored overrides.
        /// </summary>
        public int Count => _overrides.Count;

        /// <summary>
        /// Sets a cascade strategy override for a specific feature at a specific hierarchy level.
        /// </summary>
        /// <param name="featureId">The feature identifier (e.g., "encryption", "compression").</param>
        /// <param name="level">The hierarchy level at which the override applies.</param>
        /// <param name="strategy">The cascade strategy to use instead of the category default.</param>
        /// <exception cref="ArgumentException">Thrown when <paramref name="featureId"/> is null or empty.</exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the store is at capacity and the key is new.
        /// </exception>
        public void SetOverride(string featureId, PolicyLevel level, CascadeStrategy strategy)
        {
            if (string.IsNullOrEmpty(featureId))
                throw new ArgumentException("Feature ID must not be null or empty.", nameof(featureId));

            var key = BuildKey(featureId, level);

            // P2-488: Use atomic AddOrUpdate to eliminate ContainsKey + indexed-set TOCTOU race.
            // Capacity is enforced only on add; updates to existing keys bypass the limit.
            _overrides.AddOrUpdate(
                key,
                addValueFactory: k =>
                {
                    // Enforce bounded capacity for new entries only
                    if (_overrides.Count >= MaxOverrides)
                        throw new InvalidOperationException(
                            $"Cascade override store has reached its maximum capacity of {MaxOverrides} entries. " +
                            "Remove unused overrides before adding new ones.");
                    return strategy;
                },
                updateValueFactory: (k, _) => strategy);
        }

        /// <summary>
        /// Removes a cascade strategy override for a specific feature at a specific hierarchy level.
        /// </summary>
        /// <param name="featureId">The feature identifier.</param>
        /// <param name="level">The hierarchy level.</param>
        /// <returns><c>true</c> if the override was found and removed; <c>false</c> if it did not exist.</returns>
        public bool RemoveOverride(string featureId, PolicyLevel level)
        {
            if (string.IsNullOrEmpty(featureId))
                return false;

            var key = BuildKey(featureId, level);
            return _overrides.TryRemove(key, out _);
        }

        /// <summary>
        /// Attempts to retrieve a cascade strategy override for a specific feature at a specific hierarchy level.
        /// </summary>
        /// <param name="featureId">The feature identifier.</param>
        /// <param name="level">The hierarchy level.</param>
        /// <param name="strategy">
        /// When this method returns, contains the override strategy if found; otherwise, the default value.
        /// </param>
        /// <returns><c>true</c> if an override exists for the specified feature and level; otherwise, <c>false</c>.</returns>
        public bool TryGetOverride(string featureId, PolicyLevel level, out CascadeStrategy strategy)
        {
            strategy = default;

            if (string.IsNullOrEmpty(featureId))
                return false;

            var key = BuildKey(featureId, level);
            return _overrides.TryGetValue(key, out strategy);
        }

        /// <summary>
        /// Returns all current overrides as a read-only dictionary keyed by (FeatureId, Level) tuples.
        /// </summary>
        /// <returns>A snapshot of all stored overrides.</returns>
        public IReadOnlyDictionary<(string FeatureId, PolicyLevel Level), CascadeStrategy> GetAllOverrides()
        {
            var result = new Dictionary<(string FeatureId, PolicyLevel Level), CascadeStrategy>();

            foreach (var kvp in _overrides)
            {
                var (featureId, level) = ParseKey(kvp.Key);
                result[(featureId, level)] = kvp.Value;
            }

            return result;
        }

        /// <summary>
        /// Loads previously persisted overrides from the given <see cref="IPolicyPersistence"/> instance.
        /// Uses the well-known <see cref="PersistenceFeatureId"/> convention to retrieve override
        /// configuration stored as <see cref="FeaturePolicy.CustomParameters"/>.
        /// </summary>
        /// <param name="persistence">The persistence layer to load from.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>The number of overrides loaded.</returns>
        public async Task<int> LoadFromPersistenceAsync(IPolicyPersistence persistence, CancellationToken ct = default)
        {
            if (persistence is null)
                throw new ArgumentNullException(nameof(persistence));

            ct.ThrowIfCancellationRequested();

            var allPolicies = await persistence.LoadAllAsync(ct).ConfigureAwait(false);
            var overrideEntry = allPolicies.FirstOrDefault(p =>
                string.Equals(p.FeatureId, PersistenceFeatureId, StringComparison.Ordinal));

            if (overrideEntry.Policy?.CustomParameters is null || overrideEntry.Policy.CustomParameters.Count == 0)
                return 0;

            var loaded = 0;
            foreach (var kvp in overrideEntry.Policy.CustomParameters)
            {
                if (TryParseOverrideEntry(kvp.Key, kvp.Value, out var featureId, out var level, out var strategy))
                {
                    var key = BuildKey(featureId, level);
                    _overrides[key] = strategy;
                    loaded++;

                    if (loaded >= MaxOverrides)
                        break;
                }
            }

            return loaded;
        }

        /// <summary>
        /// Persists current overrides to the given <see cref="IPolicyPersistence"/> instance.
        /// Serializes all overrides as <see cref="FeaturePolicy.CustomParameters"/> entries
        /// on a well-known <see cref="PersistenceFeatureId"/> policy record.
        /// </summary>
        /// <param name="persistence">The persistence layer to save to.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        public async Task SaveToPersistenceAsync(IPolicyPersistence persistence, CancellationToken ct = default)
        {
            if (persistence is null)
                throw new ArgumentNullException(nameof(persistence));

            ct.ThrowIfCancellationRequested();

            var customParams = new Dictionary<string, string>();
            foreach (var kvp in _overrides)
            {
                customParams[kvp.Key] = kvp.Value.ToString();
            }

            var policy = new FeaturePolicy
            {
                FeatureId = PersistenceFeatureId,
                Level = PolicyLevel.VDE,
                IntensityLevel = 0,
                Cascade = CascadeStrategy.Inherit,
                AiAutonomy = AiAutonomyLevel.ManualOnly,
                CustomParameters = customParams
            };

            await persistence.SaveAsync(PersistenceFeatureId, PolicyLevel.VDE, PersistencePath, policy, ct).ConfigureAwait(false);
            await persistence.FlushAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Builds a composite key from a feature ID and policy level.
        /// </summary>
        private static string BuildKey(string featureId, PolicyLevel level) => $"{featureId}:{level}";

        /// <summary>
        /// Parses a composite key back into feature ID and policy level.
        /// </summary>
        private static (string FeatureId, PolicyLevel Level) ParseKey(string key)
        {
            var lastColon = key.LastIndexOf(':');
            if (lastColon < 0)
                return (key, PolicyLevel.VDE);

            var featureId = key.Substring(0, lastColon);
            var levelStr = key.Substring(lastColon + 1);

            return Enum.TryParse<PolicyLevel>(levelStr, ignoreCase: false, out var level)
                ? (featureId, level)
                : (featureId, PolicyLevel.VDE);
        }

        /// <summary>
        /// Tries to parse a persisted override entry (key = "featureId:level", value = strategy name).
        /// </summary>
        private static bool TryParseOverrideEntry(
            string key, string value,
            out string featureId, out PolicyLevel level, out CascadeStrategy strategy)
        {
            featureId = string.Empty;
            level = PolicyLevel.VDE;
            strategy = default;

            var (parsedFeature, parsedLevel) = ParseKey(key);
            if (string.IsNullOrEmpty(parsedFeature))
                return false;

            if (!Enum.TryParse<CascadeStrategy>(value, ignoreCase: false, out var parsedStrategy))
                return false;

            featureId = parsedFeature;
            level = parsedLevel;
            strategy = parsedStrategy;
            return true;
        }
    }
}
