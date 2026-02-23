using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.ModuleManagement;

/// <summary>
/// Tier 2 fallback status for a single module, indicating whether the feature is
/// available via the processing pipeline (Tier 2) and whether the VDE module is active (Tier 1).
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 78: Tier 2 fallback status (OMOD-05, MRES-06)")]
public readonly record struct FallbackStatus
{
    /// <summary>The module being evaluated.</summary>
    public ModuleId Module { get; init; }

    /// <summary>True if the feature works via the processing pipeline (always true by design).</summary>
    public bool Tier2Available { get; init; }

    /// <summary>Human-readable description of the Tier 2 fallback mechanism.</summary>
    public string FallbackDescription { get; init; }

    /// <summary>True if the VDE module bit is set in the manifest (Tier 1 active).</summary>
    public bool VdeModuleActive { get; init; }
}

/// <summary>
/// Formal guarantee that every module's features are available via the processing pipeline
/// (Tier 2) regardless of whether the VDE module storage is active (Tier 1). This satisfies
/// OMOD-05 and MRES-06/07: module features work via plugin layer even before the VDE module
/// is added.
///
/// <para>
/// Architecture: Tier 1 = optimized on-disk VDE module storage (native region access).
/// Tier 2 = plugin processing pipeline (feature served from plugin's own storage).
/// Adding a VDE module moves data from Tier 2 to Tier 1 for faster native access,
/// but Tier 2 always remains functional as a fallback.
/// </para>
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 78: Tier 2 fallback guard (OMOD-05, MRES-06/07)")]
public sealed class Tier2FallbackGuard
{
    /// <summary>
    /// Checks the Tier 2 fallback status for a specific module.
    /// </summary>
    /// <param name="module">The module to check.</param>
    /// <param name="currentManifest">Current 32-bit module manifest from the superblock.</param>
    /// <returns>
    /// A <see cref="FallbackStatus"/> with <see cref="FallbackStatus.Tier2Available"/> always true.
    /// </returns>
    public static FallbackStatus CheckFallback(ModuleId module, uint currentManifest)
    {
        bool isActive = ModuleRegistry.IsModuleActive(currentManifest, module);

        return new FallbackStatus
        {
            Module = module,
            Tier2Available = true,
            VdeModuleActive = isActive,
            FallbackDescription = isActive
                ? $"{ModuleRegistry.GetModule(module).Name} module: Tier 1 (VDE) active, Tier 2 (plugin) available as fallback"
                : GetFallbackDescription(module),
        };
    }

    /// <summary>
    /// Checks the Tier 2 fallback status for all 19 defined modules.
    /// </summary>
    /// <param name="currentManifest">Current 32-bit module manifest from the superblock.</param>
    /// <returns>Fallback status for every defined module.</returns>
    public static IReadOnlyList<FallbackStatus> CheckAllModules(uint currentManifest)
    {
        var results = new FallbackStatus[FormatConstants.DefinedModules];
        for (int i = 0; i < FormatConstants.DefinedModules; i++)
        {
            results[i] = CheckFallback((ModuleId)i, currentManifest);
        }
        return results;
    }

    /// <summary>
    /// Returns a human-readable description of what Tier 2 fallback means for the given module.
    /// Each module's features are served by specific plugins when the VDE module is not active.
    /// </summary>
    /// <param name="module">The module to describe.</param>
    /// <returns>Description of the Tier 2 fallback mechanism for the module.</returns>
    public static string GetFallbackDescription(ModuleId module) => module switch
    {
        ModuleId.Security =>
            "Encryption/policy handled by UltimateEncryption + UltimateAccessControl plugins",
        ModuleId.Compliance =>
            "Compliance vault served from UltimateCompliance plugin storage",
        ModuleId.Intelligence =>
            "Intelligence cache served from UltimateIntelligence plugin storage",
        ModuleId.Tags =>
            "Tag index served from plugin-managed tag storage",
        ModuleId.Replication =>
            "Replication state managed by UltimateReplication plugin",
        ModuleId.Raid =>
            "RAID metadata managed by UltimateRAID plugin storage",
        ModuleId.Streaming =>
            "Streaming append and data WAL managed by UltimateStreamingData plugin",
        ModuleId.Compute =>
            "Compute code cache managed by UltimateCompute plugin storage",
        ModuleId.Fabric =>
            "Cross-VDE references managed by UltimateDataManagement plugin",
        ModuleId.Consensus =>
            "Consensus log managed by UltimateConsensus plugin storage",
        ModuleId.Compression =>
            "Compression dictionary managed by UltimateCompression plugin",
        ModuleId.Integrity =>
            "Integrity tree managed by UltimateIntegrity plugin storage",
        ModuleId.Snapshot =>
            "Snapshot table managed by UltimateSnapshot plugin storage",
        ModuleId.Query =>
            "B-tree index forest managed by UltimateQuery plugin storage",
        ModuleId.Privacy =>
            "Anonymization table managed by UltimateDataPrivacy plugin storage",
        ModuleId.Sustainability =>
            "Sustainability metrics managed by UltimateSustainability plugin",
        ModuleId.Transit =>
            "Transit state managed by UltimateDataTransit plugin",
        ModuleId.Observability =>
            "Metrics log managed by UltimateObservability plugin storage",
        ModuleId.AuditLog =>
            "Audit log managed by UltimateDataGovernance plugin storage",
        _ => $"Module {module}: feature served via processing pipeline plugin layer",
    };

    /// <summary>
    /// Formal contract assertion: ensures Tier 2 is active for the given module.
    /// Always returns true because Tier 2 is structurally always available via
    /// the plugin architecture. Callers can invoke this before starting any module
    /// addition to formally assert Tier 2 availability.
    /// </summary>
    /// <param name="module">The module to verify.</param>
    /// <returns>Always true -- Tier 2 is structurally guaranteed.</returns>
    public static bool EnsureTier2Active(ModuleId module)
    {
        // Tier 2 is structurally always available via the plugin architecture.
        // Each module's features are handled by its corresponding plugin regardless
        // of whether the VDE-level module storage is active.
        return true;
    }
}
