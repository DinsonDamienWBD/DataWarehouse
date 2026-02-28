using System.Collections.Frozen;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Verification;

/// <summary>
/// Defines the three performance tiers available for VDE module features.
/// Lower numeric value = higher performance tier with tighter VDE integration.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 80: Tier level enum (TIER-04)")]
public enum TierLevel : byte
{
    /// <summary>VDE-integrated: native region access with binary serialize/deserialize (fastest for structured data).</summary>
    Tier1_VdeIntegrated = 1,

    /// <summary>Plugin pipeline: feature served through plugin processing with serialization boundary overhead.</summary>
    Tier2_PipelineOptimized = 2,

    /// <summary>Basic fallback: in-memory, no-op, file-based, or config-driven defaults (always available).</summary>
    Tier3_BasicFallback = 3,
}

/// <summary>
/// Documents a single module's tier assignment: which tier it defaults to, what triggers
/// promotion or demotion, and the range of available tiers for that module.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 80: Per-feature tier assignment record (TIER-04)")]
public sealed record FeatureTierAssignment
{
    /// <summary>The module this assignment describes.</summary>
    public required ModuleId Module { get; init; }

    /// <summary>Human-readable feature name.</summary>
    public required string FeatureName { get; init; }

    /// <summary>The tier this module operates at by default when no explicit configuration is present.</summary>
    public required TierLevel DefaultTier { get; init; }

    /// <summary>Rationale explaining why this is the default tier.</summary>
    public required string DefaultTierRationale { get; init; }

    /// <summary>What causes this module to be promoted to a higher (lower-numbered) tier.</summary>
    public required string PromotionTrigger { get; init; }

    /// <summary>What causes this module to be demoted to a lower (higher-numbered) tier.</summary>
    public required string DemotionTrigger { get; init; }

    /// <summary>Best tier available for this module (lowest TierLevel value).</summary>
    public required TierLevel HighestAvailableTier { get; init; }

    /// <summary>Worst-case fallback tier for this module (highest TierLevel value).</summary>
    public required TierLevel LowestAvailableTier { get; init; }
}

/// <summary>
/// Static per-feature tier mapping for all 19 VDE modules. Documents which tier each module
/// defaults to and what triggers promotion or demotion between tiers.
/// </summary>
/// <remarks>
/// <para>
/// Modules WITH dedicated regions (17 of 19) default to Tier 1 because they have native
/// binary region storage in the DWVD format. They can be demoted to Tier 2 (plugin pipeline)
/// when the VDE module bit is cleared, or to Tier 3 (basic fallback) when the serving plugin
/// is also unloaded.
/// </para>
/// <para>
/// Modules WITHOUT dedicated regions (Sustainability, Transit) default to Tier 2 because
/// they rely on their serving plugin for optimized processing. They can only fall back to
/// Tier 3 when the plugin is unloaded.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 80: Per-feature tier mapping (TIER-04)")]
public static class TierFeatureMap
{
    private static readonly FrozenDictionary<ModuleId, FeatureTierAssignment> Assignments = BuildAssignments();

    private static FrozenDictionary<ModuleId, FeatureTierAssignment> BuildAssignments()
    {
        var map = new Dictionary<ModuleId, FeatureTierAssignment>(FormatConstants.DefinedModules);

        // ---- 17 modules WITH dedicated regions: default Tier 1 ----

        map[ModuleId.Security] = new FeatureTierAssignment
        {
            Module = ModuleId.Security,
            FeatureName = "Security (PolicyVault + EncryptionHeader)",
            DefaultTier = TierLevel.Tier1_VdeIntegrated,
            DefaultTierRationale = "PolicyVault and EncryptionHeader regions provide zero-copy binary policy and key storage directly in the DWVD format",
            PromotionTrigger = "VDE module bit set in manifest + region populated",
            DemotionTrigger = "VDE module removed -> Tier 2; plugin unloaded -> Tier 3",
            HighestAvailableTier = TierLevel.Tier1_VdeIntegrated,
            LowestAvailableTier = TierLevel.Tier3_BasicFallback,
        };

        map[ModuleId.Compliance] = new FeatureTierAssignment
        {
            Module = ModuleId.Compliance,
            FeatureName = "Compliance (ComplianceVault + AuditLog)",
            DefaultTier = TierLevel.Tier1_VdeIntegrated,
            DefaultTierRationale = "ComplianceVault region provides tamper-evident compliance passport storage with ECDSA signatures",
            PromotionTrigger = "VDE module bit set in manifest + region populated",
            DemotionTrigger = "VDE module removed -> Tier 2; plugin unloaded -> Tier 3",
            HighestAvailableTier = TierLevel.Tier1_VdeIntegrated,
            LowestAvailableTier = TierLevel.Tier3_BasicFallback,
        };

        map[ModuleId.Intelligence] = new FeatureTierAssignment
        {
            Module = ModuleId.Intelligence,
            FeatureName = "Intelligence (IntelligenceCache)",
            DefaultTier = TierLevel.Tier1_VdeIntegrated,
            DefaultTierRationale = "IntelligenceCache region provides 43-byte fixed-size entries for O(1) classification lookup",
            PromotionTrigger = "VDE module bit set in manifest + region populated",
            DemotionTrigger = "VDE module removed -> Tier 2; plugin unloaded -> Tier 3",
            HighestAvailableTier = TierLevel.Tier1_VdeIntegrated,
            LowestAvailableTier = TierLevel.Tier3_BasicFallback,
        };

        map[ModuleId.Tags] = new FeatureTierAssignment
        {
            Module = ModuleId.Tags,
            FeatureName = "Tags (TagIndexRegion)",
            DefaultTier = TierLevel.Tier1_VdeIntegrated,
            DefaultTierRationale = "TagIndexRegion provides B-tree indexed tag storage with O(log n) lookup directly in VDE",
            PromotionTrigger = "VDE module bit set in manifest + region populated",
            DemotionTrigger = "VDE module removed -> Tier 2; plugin unloaded -> Tier 3",
            HighestAvailableTier = TierLevel.Tier1_VdeIntegrated,
            LowestAvailableTier = TierLevel.Tier3_BasicFallback,
        };

        map[ModuleId.Replication] = new FeatureTierAssignment
        {
            Module = ModuleId.Replication,
            FeatureName = "Replication (ReplicationState)",
            DefaultTier = TierLevel.Tier1_VdeIntegrated,
            DefaultTierRationale = "ReplicationState region tracks version vectors, watermarks, and dirty bitmaps for efficient delta sync",
            PromotionTrigger = "VDE module bit set in manifest + region populated",
            DemotionTrigger = "VDE module removed -> Tier 2; plugin unloaded -> Tier 3",
            HighestAvailableTier = TierLevel.Tier1_VdeIntegrated,
            LowestAvailableTier = TierLevel.Tier3_BasicFallback,
        };

        map[ModuleId.Raid] = new FeatureTierAssignment
        {
            Module = ModuleId.Raid,
            FeatureName = "RAID (RAIDMetadata)",
            DefaultTier = TierLevel.Tier1_VdeIntegrated,
            DefaultTierRationale = "RAIDMetadata region stores shard descriptors and parity layout for hardware-level data protection",
            PromotionTrigger = "VDE module bit set in manifest + region populated",
            DemotionTrigger = "VDE module removed -> Tier 2; plugin unloaded -> Tier 3",
            HighestAvailableTier = TierLevel.Tier1_VdeIntegrated,
            LowestAvailableTier = TierLevel.Tier3_BasicFallback,
        };

        map[ModuleId.Streaming] = new FeatureTierAssignment
        {
            Module = ModuleId.Streaming,
            FeatureName = "Streaming (StreamingAppend + DataWAL)",
            DefaultTier = TierLevel.Tier1_VdeIntegrated,
            DefaultTierRationale = "StreamingAppend region provides WAL-backed append-only writes with configurable growth",
            PromotionTrigger = "VDE module bit set in manifest + region populated",
            DemotionTrigger = "VDE module removed -> Tier 2; plugin unloaded -> Tier 3",
            HighestAvailableTier = TierLevel.Tier1_VdeIntegrated,
            LowestAvailableTier = TierLevel.Tier3_BasicFallback,
        };

        map[ModuleId.Compute] = new FeatureTierAssignment
        {
            Module = ModuleId.Compute,
            FeatureName = "Compute (ComputeCodeCache)",
            DefaultTier = TierLevel.Tier1_VdeIntegrated,
            DefaultTierRationale = "ComputeCodeCache region stores WASM modules with SHA-256 content hashing for cached execution",
            PromotionTrigger = "VDE module bit set in manifest + region populated",
            DemotionTrigger = "VDE module removed -> Tier 2; plugin unloaded -> Tier 3",
            HighestAvailableTier = TierLevel.Tier1_VdeIntegrated,
            LowestAvailableTier = TierLevel.Tier3_BasicFallback,
        };

        map[ModuleId.Fabric] = new FeatureTierAssignment
        {
            Module = ModuleId.Fabric,
            FeatureName = "Fabric (CrossVDEReferenceTable)",
            DefaultTier = TierLevel.Tier1_VdeIntegrated,
            DefaultTierRationale = "CrossVDEReferenceTable region provides 74-byte fixed VDE references for federated namespace links",
            PromotionTrigger = "VDE module bit set in manifest + region populated",
            DemotionTrigger = "VDE module removed -> Tier 2; plugin unloaded -> Tier 3",
            HighestAvailableTier = TierLevel.Tier1_VdeIntegrated,
            LowestAvailableTier = TierLevel.Tier3_BasicFallback,
        };

        map[ModuleId.Consensus] = new FeatureTierAssignment
        {
            Module = ModuleId.Consensus,
            FeatureName = "Consensus (ConsensusLogRegion)",
            DefaultTier = TierLevel.Tier1_VdeIntegrated,
            DefaultTierRationale = "ConsensusLogRegion provides 89-byte fixed group state for distributed agreement tracking",
            PromotionTrigger = "VDE module bit set in manifest + region populated",
            DemotionTrigger = "VDE module removed -> Tier 2; plugin unloaded -> Tier 3",
            HighestAvailableTier = TierLevel.Tier1_VdeIntegrated,
            LowestAvailableTier = TierLevel.Tier3_BasicFallback,
        };

        map[ModuleId.Compression] = new FeatureTierAssignment
        {
            Module = ModuleId.Compression,
            FeatureName = "Compression (DictionaryRegion)",
            DefaultTier = TierLevel.Tier1_VdeIntegrated,
            DefaultTierRationale = "CompressionDictionary region provides O(1) dictionary lookup via flat array[256] for DictId",
            PromotionTrigger = "VDE module bit set in manifest + region populated",
            DemotionTrigger = "VDE module removed -> Tier 2; plugin unloaded -> Tier 3",
            HighestAvailableTier = TierLevel.Tier1_VdeIntegrated,
            LowestAvailableTier = TierLevel.Tier3_BasicFallback,
        };

        map[ModuleId.Integrity] = new FeatureTierAssignment
        {
            Module = ModuleId.Integrity,
            FeatureName = "Integrity (IntegrityTree)",
            DefaultTier = TierLevel.Tier1_VdeIntegrated,
            DefaultTierRationale = "IntegrityTree region provides 1-indexed heap Merkle tree with O(log n) hash verification proofs",
            PromotionTrigger = "VDE module bit set in manifest + region populated",
            DemotionTrigger = "VDE module removed -> Tier 2; plugin unloaded -> Tier 3",
            HighestAvailableTier = TierLevel.Tier1_VdeIntegrated,
            LowestAvailableTier = TierLevel.Tier3_BasicFallback,
        };

        map[ModuleId.Snapshot] = new FeatureTierAssignment
        {
            Module = ModuleId.Snapshot,
            FeatureName = "Snapshot (SnapshotTable)",
            DefaultTier = TierLevel.Tier1_VdeIntegrated,
            DefaultTierRationale = "SnapshotTable region provides fixed-size snapshot entries with parent-child chain and tombstone support",
            PromotionTrigger = "VDE module bit set in manifest + region populated",
            DemotionTrigger = "VDE module removed -> Tier 2; plugin unloaded -> Tier 3",
            HighestAvailableTier = TierLevel.Tier1_VdeIntegrated,
            LowestAvailableTier = TierLevel.Tier3_BasicFallback,
        };

        map[ModuleId.Query] = new FeatureTierAssignment
        {
            Module = ModuleId.Query,
            FeatureName = "Query (BTreeIndexForest)",
            DefaultTier = TierLevel.Tier1_VdeIntegrated,
            DefaultTierRationale = "BTreeIndexForest (TagIndexRegion) provides B-tree indexed queries with O(log n) search",
            PromotionTrigger = "VDE module bit set in manifest + region populated",
            DemotionTrigger = "VDE module removed -> Tier 2; plugin unloaded -> Tier 3",
            HighestAvailableTier = TierLevel.Tier1_VdeIntegrated,
            LowestAvailableTier = TierLevel.Tier3_BasicFallback,
        };

        map[ModuleId.Privacy] = new FeatureTierAssignment
        {
            Module = ModuleId.Privacy,
            FeatureName = "Privacy (AnonymizationTable)",
            DefaultTier = TierLevel.Tier1_VdeIntegrated,
            DefaultTierRationale = "AnonymizationTable region provides fixed-size PII mapping with subject-level erasure support",
            PromotionTrigger = "VDE module bit set in manifest + region populated",
            DemotionTrigger = "VDE module removed -> Tier 2; plugin unloaded -> Tier 3",
            HighestAvailableTier = TierLevel.Tier1_VdeIntegrated,
            LowestAvailableTier = TierLevel.Tier3_BasicFallback,
        };

        map[ModuleId.Observability] = new FeatureTierAssignment
        {
            Module = ModuleId.Observability,
            FeatureName = "Observability (MetricsLogRegion)",
            DefaultTier = TierLevel.Tier1_VdeIntegrated,
            DefaultTierRationale = "MetricsLog region provides auto-compacting metrics with 1-minute average windows",
            PromotionTrigger = "VDE module bit set in manifest + region populated",
            DemotionTrigger = "VDE module removed -> Tier 2; plugin unloaded -> Tier 3",
            HighestAvailableTier = TierLevel.Tier1_VdeIntegrated,
            LowestAvailableTier = TierLevel.Tier3_BasicFallback,
        };

        map[ModuleId.AuditLog] = new FeatureTierAssignment
        {
            Module = ModuleId.AuditLog,
            FeatureName = "AuditLog (AuditLogRegion)",
            DefaultTier = TierLevel.Tier1_VdeIntegrated,
            DefaultTierRationale = "AuditLog region provides SHA-256 chained append-only entries for tamper-evident audit trail",
            PromotionTrigger = "VDE module bit set in manifest + region populated",
            DemotionTrigger = "VDE module removed -> Tier 2; plugin unloaded -> Tier 3",
            HighestAvailableTier = TierLevel.Tier1_VdeIntegrated,
            LowestAvailableTier = TierLevel.Tier3_BasicFallback,
        };

        // ---- 2 modules WITHOUT dedicated regions: default Tier 2 ----

        map[ModuleId.Sustainability] = new FeatureTierAssignment
        {
            Module = ModuleId.Sustainability,
            FeatureName = "Sustainability (inode-only, no dedicated region)",
            DefaultTier = TierLevel.Tier2_PipelineOptimized,
            DefaultTierRationale = "No dedicated region — Tier 1 limited to 4-byte inode field only, full feature requires UltimateSustainability plugin pipeline",
            PromotionTrigger = "UltimateSustainability plugin loaded with metrics pipeline",
            DemotionTrigger = "Plugin unloaded -> Tier 3 (no metrics collection)",
            HighestAvailableTier = TierLevel.Tier2_PipelineOptimized,
            LowestAvailableTier = TierLevel.Tier3_BasicFallback,
        };

        map[ModuleId.Transit] = new FeatureTierAssignment
        {
            Module = ModuleId.Transit,
            FeatureName = "Transit (inode-only, no dedicated region)",
            DefaultTier = TierLevel.Tier2_PipelineOptimized,
            DefaultTierRationale = "No dedicated region — Tier 1 limited to 1-byte inode field only, full feature requires UltimateDataTransit plugin pipeline",
            PromotionTrigger = "UltimateDataTransit plugin loaded with transit optimization",
            DemotionTrigger = "Plugin unloaded -> Tier 3 (direct transfer only)",
            HighestAvailableTier = TierLevel.Tier2_PipelineOptimized,
            LowestAvailableTier = TierLevel.Tier3_BasicFallback,
        };

        return map.ToFrozenDictionary();
    }

    /// <summary>
    /// Returns the complete tier mapping for all 19 modules, ordered by <see cref="ModuleId"/>.
    /// </summary>
    public static IReadOnlyList<FeatureTierAssignment> GetFeatureMap()
    {
        var result = new List<FeatureTierAssignment>(FormatConstants.DefinedModules);
        for (int i = 0; i < FormatConstants.DefinedModules; i++)
        {
            var moduleId = (ModuleId)i;
            if (Assignments.ContainsKey(moduleId))
            {
                result.Add(Assignments[moduleId]);
            }
        }
        return result;
    }

    /// <summary>
    /// Returns the tier assignment for a specific module.
    /// </summary>
    /// <param name="module">The module to look up.</param>
    /// <returns>The feature tier assignment for the module.</returns>
    /// <exception cref="KeyNotFoundException">The module has no tier assignment.</exception>
    public static FeatureTierAssignment GetAssignment(ModuleId module) => Assignments[module];

    /// <summary>
    /// Returns all modules that currently default to the specified tier level.
    /// </summary>
    /// <param name="tier">The tier level to filter by.</param>
    /// <returns>List of modules defaulting to the specified tier.</returns>
    public static IReadOnlyList<FeatureTierAssignment> GetModulesAtTier(TierLevel tier)
    {
        var result = new List<FeatureTierAssignment>();
        foreach (var kvp in Assignments)
        {
            if (kvp.Value.DefaultTier == tier)
            {
                result.Add(kvp.Value);
            }
        }
        return result;
    }
}
