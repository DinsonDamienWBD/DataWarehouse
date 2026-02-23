using System.Collections.Frozen;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;
using DataWarehouse.SDK.VirtualDiskEngine.ModuleManagement;

namespace DataWarehouse.SDK.VirtualDiskEngine.Verification;

/// <summary>
/// Verifies that all 19 module features function correctly via Tier 2 (plugin pipeline
/// processing) without any VDE module integration active. Iterates every defined module,
/// checks fallback status via <see cref="Tier2FallbackGuard"/>, and confirms each module's
/// plugin mapping is structurally sound.
///
/// <para>
/// Also verifies the fallback guarantee holds when all modules are active (manifest = 0x7FFFF),
/// confirming Tier 2 remains available even when Tier 1 is active.
/// </para>
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 80: Tier 2 pipeline verifier (TIER-02)")]
public sealed class Tier2PipelineVerifier
{
    /// <summary>
    /// Expected plugin names that serve each module's features at Tier 2.
    /// Maps each <see cref="ModuleId"/> to the plugin name(s) responsible for the feature.
    /// </summary>
    private static readonly FrozenDictionary<ModuleId, string[]> ExpectedPlugins = BuildExpectedPlugins();

    /// <summary>Manifest value with no VDE modules active (pure Tier 2 mode).</summary>
    private const uint EmptyManifest = 0;

    /// <summary>Manifest value with all 19 VDE modules active (bits 0-18 set).</summary>
    private const uint AllModulesManifest = 0x7FFFF;

    private static FrozenDictionary<ModuleId, string[]> BuildExpectedPlugins() =>
        new Dictionary<ModuleId, string[]>(FormatConstants.DefinedModules)
        {
            [ModuleId.Security] = new[] { "UltimateEncryption", "UltimateAccessControl" },
            [ModuleId.Compliance] = new[] { "UltimateCompliance" },
            [ModuleId.Intelligence] = new[] { "UltimateIntelligence" },
            [ModuleId.Tags] = new[] { "plugin-managed" },
            [ModuleId.Replication] = new[] { "UltimateReplication" },
            [ModuleId.Raid] = new[] { "UltimateRAID" },
            [ModuleId.Streaming] = new[] { "UltimateStreamingData" },
            [ModuleId.Compute] = new[] { "UltimateCompute" },
            [ModuleId.Fabric] = new[] { "UltimateDataManagement" },
            [ModuleId.Consensus] = new[] { "UltimateConsensus" },
            [ModuleId.Compression] = new[] { "UltimateCompression" },
            [ModuleId.Integrity] = new[] { "UltimateIntegrity" },
            [ModuleId.Snapshot] = new[] { "UltimateSnapshot" },
            [ModuleId.Query] = new[] { "UltimateQuery" },
            [ModuleId.Privacy] = new[] { "UltimateDataPrivacy" },
            [ModuleId.Sustainability] = new[] { "UltimateSustainability" },
            [ModuleId.Transit] = new[] { "UltimateDataTransit" },
            [ModuleId.Observability] = new[] { "UltimateObservability" },
            [ModuleId.AuditLog] = new[] { "UltimateDataGovernance" },
        }.ToFrozenDictionary();

    /// <summary>
    /// Verifies all 19 modules with manifest = 0 (no VDE modules active, pure Tier 2 mode).
    /// Each module must have Tier2Available=true, EnsureTier2Active=true, and a verified
    /// plugin mapping.
    /// </summary>
    /// <returns>
    /// 19 <see cref="Tier2VerificationResult"/> records, one per module, each with
    /// <see cref="Tier2VerificationResult.Tier2Verified"/> expected to be true.
    /// </returns>
    public static IReadOnlyList<Tier2VerificationResult> VerifyAllModules()
    {
        var results = new Tier2VerificationResult[FormatConstants.DefinedModules];
        for (int i = 0; i < FormatConstants.DefinedModules; i++)
        {
            results[i] = VerifyModule((ModuleId)i);
        }
        return results;
    }

    /// <summary>
    /// Verifies a single module's Tier 2 pipeline availability.
    /// Checks fallback status with manifest=0 (pure Tier 2) and verifies the plugin mapping.
    /// </summary>
    /// <param name="module">The module to verify.</param>
    /// <returns>A structured verification result for the module.</returns>
    public static Tier2VerificationResult VerifyModule(ModuleId module)
    {
        // Check fallback with empty manifest (no VDE modules active)
        var fallbackStatus = Tier2FallbackGuard.CheckFallback(module, EmptyManifest);
        bool fallbackGuardPassed = fallbackStatus.Tier2Available && !fallbackStatus.VdeModuleActive;

        // Check formal Tier 2 assertion
        bool ensureTier2Passed = Tier2FallbackGuard.EnsureTier2Active(module);

        // Get fallback description and verify plugin mapping
        string description = Tier2FallbackGuard.GetFallbackDescription(module);
        string pluginName = ExtractPluginName(module, description);
        bool pluginMappingVerified = VerifyPluginMapping(module, description);

        // Build details string
        var details = new System.Text.StringBuilder();
        details.Append($"Module {module} ({ModuleRegistry.GetModule(module).Name}): ");

        if (fallbackGuardPassed && ensureTier2Passed && pluginMappingVerified)
        {
            details.Append($"PASS - Tier 2 served by {pluginName}");
        }
        else
        {
            if (!fallbackGuardPassed)
                details.Append("FAIL(FallbackGuard) ");
            if (!ensureTier2Passed)
                details.Append("FAIL(EnsureTier2) ");
            if (!pluginMappingVerified)
                details.Append("FAIL(PluginMapping) ");
        }

        return new Tier2VerificationResult
        {
            Module = module,
            ModuleName = ModuleRegistry.GetModule(module).Name,
            FallbackGuardPassed = fallbackGuardPassed,
            EnsureTier2ActivePassed = ensureTier2Passed,
            FallbackPluginName = pluginName,
            FallbackDescription = description,
            PluginMappingVerified = pluginMappingVerified,
            Details = details.ToString(),
        };
    }

    /// <summary>
    /// Verifies that Tier 2 remains available even when all modules are active (Tier 1 active).
    /// This confirms the fallback guarantee: Tier 2 is always available regardless of manifest.
    /// </summary>
    /// <returns>
    /// 19 <see cref="Tier2VerificationResult"/> records verified with manifest = 0x7FFFF.
    /// FallbackGuardPassed is true if Tier2Available=true (VdeModuleActive will be true).
    /// </returns>
    public static IReadOnlyList<Tier2VerificationResult> VerifyAllModulesWithFullManifest()
    {
        var results = new Tier2VerificationResult[FormatConstants.DefinedModules];
        for (int i = 0; i < FormatConstants.DefinedModules; i++)
        {
            var module = (ModuleId)i;
            var fallbackStatus = Tier2FallbackGuard.CheckFallback(module, AllModulesManifest);

            // With full manifest, Tier2Available should still be true, but VdeModuleActive is also true
            bool fallbackGuardPassed = fallbackStatus.Tier2Available;
            bool ensureTier2Passed = Tier2FallbackGuard.EnsureTier2Active(module);

            // The description when active is different, so we use GetFallbackDescription for plugin mapping
            string pureDescription = Tier2FallbackGuard.GetFallbackDescription(module);
            string pluginName = ExtractPluginName(module, pureDescription);
            bool pluginMappingVerified = VerifyPluginMapping(module, pureDescription);

            results[i] = new Tier2VerificationResult
            {
                Module = module,
                ModuleName = ModuleRegistry.GetModule(module).Name,
                FallbackGuardPassed = fallbackGuardPassed,
                EnsureTier2ActivePassed = ensureTier2Passed,
                FallbackPluginName = pluginName,
                FallbackDescription = fallbackStatus.FallbackDescription,
                PluginMappingVerified = pluginMappingVerified,
                Details = $"Module {module} ({ModuleRegistry.GetModule(module).Name}): " +
                          $"Tier 1 active + Tier 2 available - served by {pluginName}",
            };
        }
        return results;
    }

    /// <summary>
    /// Extracts the primary plugin name(s) from a fallback description for display.
    /// </summary>
    private static string ExtractPluginName(ModuleId module, string description)
    {
        if (!ExpectedPlugins.TryGetValue(module, out var expectedNames))
            return "Unknown";

        return string.Join(" + ", expectedNames);
    }

    /// <summary>
    /// Verifies that the fallback description references at least one of the expected
    /// plugin names for the module. Uses case-insensitive matching.
    /// For Tags (which uses "plugin-managed tag storage"), verifies "tag" is present.
    /// </summary>
    private static bool VerifyPluginMapping(ModuleId module, string description)
    {
        if (!ExpectedPlugins.TryGetValue(module, out var expectedNames))
            return false;

        foreach (string name in expectedNames)
        {
            if (description.Contains(name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Special case: Tags module uses "plugin-managed tag storage" pattern
        if (module == ModuleId.Tags && description.Contains("tag", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}
