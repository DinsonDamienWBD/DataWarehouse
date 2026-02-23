using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;

namespace DataWarehouse.SDK.Infrastructure.Policy.Compatibility;

/// <summary>
/// Migrates v5.0 <see cref="Primitives.Configuration.ConfigurationItem{T}"/>-based configuration
/// entries to VDE-level <see cref="FeaturePolicy"/> instances stored in the Policy Engine.
/// <para>
/// Migration is idempotent: a sentinel policy with featureId "__v5_migration_complete" is written
/// after successful migration and checked on subsequent invocations, returning
/// <see cref="V5MigrationResult.AlreadyMigrated"/> = true without re-processing.
/// </para>
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 81: v5.0 config migration (MIGR-03)")]
public sealed class V5ConfigMigrator
{
    private const string MigrationSentinelFeatureId = "__v5_migration_complete";

    private readonly IPolicyStore _store;
    private readonly IPolicyPersistence _persistence;

    /// <summary>
    /// Well-known v5.0 configuration key to v6.0 feature ID mapping.
    /// Uses <see cref="FrozenDictionary{TKey, TValue}"/> for zero-allocation O(1) lookups.
    /// </summary>
    private static readonly FrozenDictionary<string, string> V5KeyToFeatureId = BuildV5KeyMapping();

    /// <summary>
    /// Initializes a new instance of <see cref="V5ConfigMigrator"/>.
    /// </summary>
    /// <param name="store">The policy store to write converted policies into.</param>
    /// <param name="persistence">The persistence layer for durable storage of migrated policies.</param>
    public V5ConfigMigrator(IPolicyStore store, IPolicyPersistence persistence)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
    }

    /// <summary>
    /// Migrates v5.0 configuration entries to VDE-level feature policies.
    /// <para>
    /// For each recognized v5.0 config key, converts the value to a <see cref="FeaturePolicy"/> with:
    /// intensity derived from the v5 value, <see cref="CascadeStrategy.Inherit"/> (single-level default),
    /// and <see cref="AiAutonomyLevel.ManualOnly"/>. Unrecognized keys are counted as skipped.
    /// </para>
    /// </summary>
    /// <param name="v5Config">The v5.0 configuration dictionary to migrate.</param>
    /// <param name="vdePath">The VDE path at which to store migrated policies (e.g., "/myVde").</param>
    /// <param name="ct">Cancellation token for cooperative cancellation.</param>
    /// <returns>A <see cref="V5MigrationResult"/> summarizing the migration outcome.</returns>
    public async Task<V5MigrationResult> MigrateFromConfigAsync(
        IReadOnlyDictionary<string, object> v5Config,
        string vdePath,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (v5Config is null) throw new ArgumentNullException(nameof(v5Config));
        if (string.IsNullOrEmpty(vdePath)) throw new ArgumentException("VDE path must not be null or empty.", nameof(vdePath));

        // Idempotency check: look for the migration sentinel
        var sentinel = await _store.GetAsync(MigrationSentinelFeatureId, PolicyLevel.VDE, vdePath, ct).ConfigureAwait(false);
        if (sentinel is not null)
        {
            return new V5MigrationResult(
                PoliciesMigrated: 0,
                PoliciesSkipped: 0,
                MigratedFeatureIds: Array.Empty<string>(),
                Warnings: Array.Empty<string>(),
                AlreadyMigrated: true);
        }

        var migratedIds = new List<string>();
        var warnings = new List<string>();
        int skipped = 0;

        foreach (var (key, value) in v5Config)
        {
            if (!V5KeyToFeatureId.TryGetValue(key, out var featureId))
            {
                skipped++;
                continue;
            }

            try
            {
                int intensity = ConvertV5Value(value);

                var policy = new FeaturePolicy
                {
                    FeatureId = featureId,
                    Level = PolicyLevel.VDE,
                    IntensityLevel = intensity,
                    Cascade = CascadeStrategy.Inherit,
                    AiAutonomy = AiAutonomyLevel.ManualOnly
                };

                await _store.SetAsync(featureId, PolicyLevel.VDE, vdePath, policy, ct).ConfigureAwait(false);
                migratedIds.Add(featureId);
            }
            catch (Exception ex)
            {
                warnings.Add($"Failed to migrate key '{key}' (featureId '{featureId}'): {ex.Message}");
                skipped++;
            }
        }

        // Write migration sentinel
        var sentinelPolicy = new FeaturePolicy
        {
            FeatureId = MigrationSentinelFeatureId,
            Level = PolicyLevel.VDE,
            IntensityLevel = 100,
            Cascade = CascadeStrategy.Inherit,
            AiAutonomy = AiAutonomyLevel.ManualOnly
        };

        await _store.SetAsync(MigrationSentinelFeatureId, PolicyLevel.VDE, vdePath, sentinelPolicy, ct).ConfigureAwait(false);

        // Persist all changes
        await _persistence.FlushAsync(ct).ConfigureAwait(false);

        return new V5MigrationResult(
            PoliciesMigrated: migratedIds.Count,
            PoliciesSkipped: skipped,
            MigratedFeatureIds: migratedIds.AsReadOnly(),
            Warnings: warnings.AsReadOnly(),
            AlreadyMigrated: false);
    }

    /// <summary>
    /// Returns the well-known v5.0 configuration key to v6.0 feature ID mapping.
    /// Useful for tooling, diagnostics, and migration preview.
    /// </summary>
    /// <returns>A read-only dictionary mapping v5.0 config keys to v6.0 feature IDs.</returns>
    public static IReadOnlyDictionary<string, string> GetKnownV5ConfigKeys() => V5KeyToFeatureId;

    /// <summary>
    /// Converts a v5.0 configuration value to a 0-100 intensity level.
    /// <list type="bullet">
    ///   <item><description>bool: true=80, false=0</description></item>
    ///   <item><description>int/long/double: clamped to 0-100 range</description></item>
    ///   <item><description>string: parsed as int, or default 50 if unparseable</description></item>
    ///   <item><description>null or other: default 50</description></item>
    /// </list>
    /// </summary>
    private static int ConvertV5Value(object? value)
    {
        return value switch
        {
            bool b => b ? 80 : 0,
            int i => Math.Clamp(i, 0, 100),
            long l => Math.Clamp((int)Math.Min(l, 100), 0, 100),
            double d => Math.Clamp((int)d, 0, 100),
            float f => Math.Clamp((int)f, 0, 100),
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) =>
                Math.Clamp(parsed, 0, 100),
            string => 50,
            null => 50,
            _ => 50
        };
    }

    /// <summary>
    /// Builds the static mapping from v5.0 configuration keys to v6.0 feature IDs.
    /// Contains 20+ well-known v5.0 config keys covering the core feature set.
    /// </summary>
    private static FrozenDictionary<string, string> BuildV5KeyMapping()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Security features
            ["encryption_enabled"] = "encryption",
            ["access_control_model"] = "access_control",
            ["auth_model"] = "auth_model",
            ["fips_mode"] = "fips_mode",
            ["tls_policy"] = "tls_policy",
            ["key_management_mode"] = "key_management",
            ["zero_trust_enabled"] = "zero_trust",

            // Storage features
            ["compression_level"] = "compression",
            ["replication_factor"] = "replication",
            ["deduplication_enabled"] = "deduplication",
            ["erasure_coding_enabled"] = "erasure_coding",
            ["tiering_mode"] = "tiering",
            ["storage_backend"] = "storage_backend",
            ["snapshot_policy"] = "snapshot_policy",
            ["cache_strategy"] = "cache_strategy",

            // Compliance features
            ["audit_logging"] = "audit_logging",
            ["compliance_recording"] = "compliance_recording",
            ["data_classification"] = "data_classification",
            ["retention_policy"] = "retention_enforcement",

            // Operations features
            ["indexing_enabled"] = "indexing",
            ["versioning_enabled"] = "versioning",
            ["lifecycle_policy"] = "lifecycle_policy",
            ["quota_enforcement"] = "quota"
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Describes the outcome of a v5.0 configuration migration to VDE-level policies.
/// </summary>
/// <param name="PoliciesMigrated">Number of v5.0 config entries successfully converted to feature policies.</param>
/// <param name="PoliciesSkipped">Number of v5.0 config entries that had no policy equivalent or failed conversion.</param>
/// <param name="MigratedFeatureIds">Feature IDs that were successfully migrated.</param>
/// <param name="Warnings">Non-fatal issues encountered during migration.</param>
/// <param name="AlreadyMigrated">True if the migration sentinel was already present (idempotent no-op).</param>
[SdkCompatibility("6.0.0", Notes = "Phase 81: v5.0 config migration result (MIGR-03)")]
public readonly record struct V5MigrationResult(
    int PoliciesMigrated,
    int PoliciesSkipped,
    IReadOnlyList<string> MigratedFeatureIds,
    IReadOnlyList<string> Warnings,
    bool AlreadyMigrated);
