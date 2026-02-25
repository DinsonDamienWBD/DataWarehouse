using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;

namespace DataWarehouse.SDK.Infrastructure.Policy.Performance
{
    /// <summary>
    /// Defines when a feature check should be evaluated during the VDE lifecycle (PERF-05).
    /// <para>
    /// Each timing category determines how the FastPathPolicyEngine routes a feature
    /// check: ConnectTime and SessionCached checks are resolved once and cached; PerOperation checks
    /// are evaluated on every read/write and routed by deployment tier; Deferred and Periodic checks
    /// use the materialized snapshot asynchronously.
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 76: Performance Optimization (PERF-05)")]
    public enum CheckTiming
    {
        /// <summary>Evaluated once at VDE open/connect; result cached for entire session (e.g., encryption algorithm, auth model).</summary>
        [Description("Evaluated once at VDE open/connect; result cached for entire session")]
        ConnectTime = 0,

        /// <summary>Evaluated once per session and cached; re-evaluated if policy changes mid-session (e.g., compression level, replication factor).</summary>
        [Description("Evaluated once per session and cached; re-evaluated if policy changes mid-session")]
        SessionCached = 1,

        /// <summary>Evaluated on every read/write operation (e.g., access control check, quota enforcement).</summary>
        [Description("Evaluated on every read/write operation")]
        PerOperation = 2,

        /// <summary>Evaluated asynchronously after the operation completes (e.g., audit logging, compliance recording).</summary>
        [Description("Evaluated asynchronously after the operation completes")]
        Deferred = 3,

        /// <summary>Evaluated on a timer (e.g., integrity verification, key rotation check, sustainability metrics).</summary>
        [Description("Evaluated on a timer")]
        Periodic = 4
    }

    /// <summary>
    /// Determines which fast path a VDE uses based on where policy overrides exist in the hierarchy (PERF-04).
    /// <para>
    /// The deployment tier is classified at VDE open time and determines which resolution path is taken
    /// for <see cref="CheckTiming.PerOperation"/> checks:
    /// <list type="bullet">
    ///   <item><description><see cref="VdeOnly"/>: no overrides anywhere; compiled delegate returns directly (0ns overhead).</description></item>
    ///   <item><description><see cref="ContainerStop"/>: overrides only at container level or above; bloom filter + cache (~20ns).</description></item>
    ///   <item><description><see cref="FullCascade"/>: overrides at object/chunk/block level; full resolution needed (~200ns).</description></item>
    /// </list>
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 76: Performance Optimization (PERF-04)")]
    public enum DeploymentTier
    {
        /// <summary>No overrides at any level; use materialized cache directly (0ns overhead).</summary>
        [Description("No overrides at any level; use materialized cache directly (0ns overhead)")]
        VdeOnly = 0,

        /// <summary>Overrides exist only at container level or above; bloom filter + cache (~20ns).</summary>
        [Description("Overrides exist only at container level or above; bloom filter + cache (~20ns)")]
        ContainerStop = 1,

        /// <summary>Overrides exist at object/chunk/block level; full resolution needed (~200ns).</summary>
        [Description("Overrides exist at object/chunk/block level; full resolution needed (~200ns)")]
        FullCascade = 2
    }

    /// <summary>
    /// Maps feature IDs to their <see cref="CheckTiming"/> classification using a
    /// <see cref="FrozenDictionary{TKey, TValue}"/> for zero-allocation O(1) lookups (PERF-05).
    /// <para>
    /// Contains 94 feature entries spanning the full DataWarehouse feature set, distributed across
    /// 5 timing categories. Unknown features default to <see cref="CheckTiming.PerOperation"/> for safety.
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 76: Performance Optimization (PERF-05)")]
    public static class CheckClassificationTable
    {
        private static readonly FrozenDictionary<string, CheckTiming> _classifications = BuildClassifications();
        private static readonly FrozenDictionary<CheckTiming, string[]> _featuresByTiming = BuildFeaturesByTiming();

        private static Dictionary<string, CheckTiming> BuildEntries() => new(StringComparer.Ordinal)
        {
            // ConnectTime (20): evaluated once at VDE open, cached for entire session
            ["encryption"] = CheckTiming.ConnectTime,
            ["auth_model"] = CheckTiming.ConnectTime,
            ["key_management"] = CheckTiming.ConnectTime,
            ["fips_mode"] = CheckTiming.ConnectTime,
            ["air_gap_mode"] = CheckTiming.ConnectTime,
            ["security_clearance"] = CheckTiming.ConnectTime,
            ["mfa_policy"] = CheckTiming.ConnectTime,
            ["zero_trust"] = CheckTiming.ConnectTime,
            ["enclave_mode"] = CheckTiming.ConnectTime,
            ["steganography"] = CheckTiming.ConnectTime,
            ["forensic_watermark"] = CheckTiming.ConnectTime,
            ["dead_drop"] = CheckTiming.ConnectTime,
            ["canary"] = CheckTiming.ConnectTime,
            ["honeypot"] = CheckTiming.ConnectTime,
            ["sovereignty"] = CheckTiming.ConnectTime,
            ["tamper_detection"] = CheckTiming.ConnectTime,
            ["authority_chain"] = CheckTiming.ConnectTime,
            ["tls_policy"] = CheckTiming.ConnectTime,
            ["protocol_version"] = CheckTiming.ConnectTime,
            ["identity_provider"] = CheckTiming.ConnectTime,

            // SessionCached (24): evaluated once per session, re-evaluated on policy change
            ["compression"] = CheckTiming.SessionCached,
            ["replication"] = CheckTiming.SessionCached,
            ["storage_backend"] = CheckTiming.SessionCached,
            ["tiering"] = CheckTiming.SessionCached,
            ["deduplication"] = CheckTiming.SessionCached,
            ["erasure_coding"] = CheckTiming.SessionCached,
            ["snapshot_policy"] = CheckTiming.SessionCached,
            ["branching"] = CheckTiming.SessionCached,
            ["cdp_interval"] = CheckTiming.SessionCached,
            ["cache_strategy"] = CheckTiming.SessionCached,
            ["indexing"] = CheckTiming.SessionCached,
            ["format_preference"] = CheckTiming.SessionCached,
            ["compute_runtime"] = CheckTiming.SessionCached,
            ["wasm_runtime"] = CheckTiming.SessionCached,
            ["container_runtime"] = CheckTiming.SessionCached,
            ["gpu_policy"] = CheckTiming.SessionCached,
            ["streaming_protocol"] = CheckTiming.SessionCached,
            ["data_transit"] = CheckTiming.SessionCached,
            ["network_qos"] = CheckTiming.SessionCached,
            ["versioning"] = CheckTiming.SessionCached,
            ["lifecycle_policy"] = CheckTiming.SessionCached,
            ["geo_routing"] = CheckTiming.SessionCached,
            ["federation"] = CheckTiming.SessionCached,
            ["namespace_isolation"] = CheckTiming.SessionCached,

            // PerOperation (18): evaluated on every read/write
            ["access_control"] = CheckTiming.PerOperation,
            ["quota"] = CheckTiming.PerOperation,
            ["rate_limit"] = CheckTiming.PerOperation,
            ["data_classification"] = CheckTiming.PerOperation,
            ["lineage_tracking"] = CheckTiming.PerOperation,
            ["quality_check"] = CheckTiming.PerOperation,
            ["schema_validation"] = CheckTiming.PerOperation,
            ["transformation"] = CheckTiming.PerOperation,
            ["routing"] = CheckTiming.PerOperation,
            ["load_balancing"] = CheckTiming.PerOperation,
            ["cost_routing"] = CheckTiming.PerOperation,
            ["encryption_per_op"] = CheckTiming.PerOperation,
            ["integrity_inline"] = CheckTiming.PerOperation,
            ["watermarking"] = CheckTiming.PerOperation,
            ["redaction"] = CheckTiming.PerOperation,
            ["tokenization"] = CheckTiming.PerOperation,
            ["masking"] = CheckTiming.PerOperation,
            ["consent_check"] = CheckTiming.PerOperation,

            // Deferred (16): evaluated asynchronously after operation completes
            ["audit_logging"] = CheckTiming.Deferred,
            ["compliance_recording"] = CheckTiming.Deferred,
            ["metrics_collection"] = CheckTiming.Deferred,
            ["telemetry"] = CheckTiming.Deferred,
            ["sustainability_tracking"] = CheckTiming.Deferred,
            ["carbon_aware"] = CheckTiming.Deferred,
            ["billing"] = CheckTiming.Deferred,
            ["usage_analytics"] = CheckTiming.Deferred,
            ["anomaly_detection"] = CheckTiming.Deferred,
            ["threat_detection"] = CheckTiming.Deferred,
            ["data_lineage_publish"] = CheckTiming.Deferred,
            ["event_notification"] = CheckTiming.Deferred,
            ["webhook_dispatch"] = CheckTiming.Deferred,
            ["replication_journal"] = CheckTiming.Deferred,
            ["change_data_capture"] = CheckTiming.Deferred,
            ["backup_verification"] = CheckTiming.Deferred,

            // Periodic (16): evaluated on a timer
            ["integrity_verification"] = CheckTiming.Periodic,
            ["key_rotation"] = CheckTiming.Periodic,
            ["certificate_renewal"] = CheckTiming.Periodic,
            ["health_check"] = CheckTiming.Periodic,
            ["capacity_planning"] = CheckTiming.Periodic,
            ["defragmentation"] = CheckTiming.Periodic,
            ["garbage_collection"] = CheckTiming.Periodic,
            ["replication_sync"] = CheckTiming.Periodic,
            ["backup_schedule"] = CheckTiming.Periodic,
            ["retention_enforcement"] = CheckTiming.Periodic,
            ["deprecation_check"] = CheckTiming.Periodic,
            ["compaction"] = CheckTiming.Periodic,
            ["statistics_refresh"] = CheckTiming.Periodic,
            ["license_check"] = CheckTiming.Periodic,
            ["quota_recalculation"] = CheckTiming.Periodic,
            ["orphan_cleanup"] = CheckTiming.Periodic
        };

        private static FrozenDictionary<string, CheckTiming> BuildClassifications()
            => BuildEntries().ToFrozenDictionary(StringComparer.Ordinal);

        private static FrozenDictionary<CheckTiming, string[]> BuildFeaturesByTiming()
        {
            var entries = BuildEntries();
            var grouped = new Dictionary<CheckTiming, string[]>();
            foreach (CheckTiming timing in Enum.GetValues<CheckTiming>())
            {
                grouped[timing] = entries
                    .Where(kv => kv.Value == timing)
                    .Select(kv => kv.Key)
                    .OrderBy(k => k, StringComparer.Ordinal)
                    .ToArray();
            }
            return grouped.ToFrozenDictionary();
        }

        /// <summary>
        /// Returns the <see cref="CheckTiming"/> classification for the given feature.
        /// Defaults to <see cref="CheckTiming.PerOperation"/> for unknown features (safest default).
        /// </summary>
        /// <param name="featureId">The feature identifier to classify.</param>
        /// <returns>The timing classification for the feature.</returns>
        public static CheckTiming GetTiming(string featureId)
        {
            if (featureId is null) throw new ArgumentNullException(nameof(featureId));
            return _classifications.TryGetValue(featureId, out var timing) ? timing : CheckTiming.PerOperation;
        }

        /// <summary>
        /// Returns all feature IDs classified under the given <see cref="CheckTiming"/> category.
        /// </summary>
        /// <param name="timing">The timing category to query.</param>
        /// <returns>A read-only list of feature IDs with the specified timing classification.</returns>
        public static IReadOnlyList<string> GetFeaturesByTiming(CheckTiming timing)
        {
            return _featuresByTiming.TryGetValue(timing, out var features) ? features : Array.Empty<string>();
        }

        /// <summary>
        /// Gets the total number of classified features in the routing table.
        /// </summary>
        public static int TotalFeatures => _classifications.Count;
    }

    /// <summary>
    /// Classifies a VDE's deployment tier by checking where policy overrides exist in the hierarchy (PERF-04).
    /// <para>
    /// The tier determines which fast path the FastPathPolicyEngine uses for
    /// <see cref="CheckTiming.PerOperation"/> checks. Classification runs once at VDE open time.
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 76: Performance Optimization (PERF-04)")]
    public static class DeploymentTierClassifier
    {
        /// <summary>
        /// Classifies the deployment tier by querying the policy store for overrides at each hierarchy level.
        /// <para>
        /// Checks from finest (Block) to coarsest (Container) granularity. If any override exists at
        /// Object, Chunk, or Block level, returns <see cref="DeploymentTier.FullCascade"/>. If overrides
        /// exist only at Container level, returns <see cref="DeploymentTier.ContainerStop"/>. Otherwise
        /// returns <see cref="DeploymentTier.VdeOnly"/>.
        /// </para>
        /// </summary>
        /// <param name="store">The policy store to query for existing overrides.</param>
        /// <param name="vdePath">The VDE path to classify.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>The classified <see cref="DeploymentTier"/> for the VDE.</returns>
        public static async Task<DeploymentTier> ClassifyAsync(
            IPolicyStore store,
            string vdePath,
            CancellationToken ct = default)
        {
            if (store is null) throw new ArgumentNullException(nameof(store));
            if (vdePath is null) throw new ArgumentNullException(nameof(vdePath));

            // Check finest granularity first -- any override at Block/Chunk/Object means full cascade
            if (await store.HasOverrideAsync(PolicyLevel.Block, vdePath, ct).ConfigureAwait(false))
                return DeploymentTier.FullCascade;
            if (await store.HasOverrideAsync(PolicyLevel.Chunk, vdePath, ct).ConfigureAwait(false))
                return DeploymentTier.FullCascade;
            if (await store.HasOverrideAsync(PolicyLevel.Object, vdePath, ct).ConfigureAwait(false))
                return DeploymentTier.FullCascade;

            // Override at Container level means bloom filter + cache path
            if (await store.HasOverrideAsync(PolicyLevel.Container, vdePath, ct).ConfigureAwait(false))
                return DeploymentTier.ContainerStop;

            // No overrides anywhere -- pure materialized cache path
            return DeploymentTier.VdeOnly;
        }

        /// <summary>
        /// Classifies the deployment tier using a pre-built <see cref="BloomFilterSkipIndex"/> for O(1) checks.
        /// <para>
        /// Same classification logic as <see cref="ClassifyAsync"/> but uses the bloom filter instead
        /// of the store, providing O(1) classification with zero false negatives. False positives
        /// may cause a higher tier classification (safe: we fall through to a more thorough path).
        /// </para>
        /// </summary>
        /// <param name="filter">The bloom filter to check for override existence.</param>
        /// <param name="vdePath">The VDE path to classify.</param>
        /// <returns>The classified <see cref="DeploymentTier"/> for the VDE.</returns>
        public static DeploymentTier ClassifyFromBloomFilter(BloomFilterSkipIndex filter, string vdePath)
        {
            if (filter is null) throw new ArgumentNullException(nameof(filter));
            if (vdePath is null) throw new ArgumentNullException(nameof(vdePath));

            // Bloom filter: false positive is safe (classifies to a higher/more thorough tier)
            if (filter.MayContain(PolicyLevel.Block, vdePath))
                return DeploymentTier.FullCascade;
            if (filter.MayContain(PolicyLevel.Chunk, vdePath))
                return DeploymentTier.FullCascade;
            if (filter.MayContain(PolicyLevel.Object, vdePath))
                return DeploymentTier.FullCascade;

            if (filter.MayContain(PolicyLevel.Container, vdePath))
                return DeploymentTier.ContainerStop;

            return DeploymentTier.VdeOnly;
        }
    }
}
