using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Verification;

/// <summary>
/// Structured result of Tier 2 pipeline verification for a single module.
/// Each result confirms whether the module's features are available via the plugin
/// processing pipeline (Tier 2) without requiring VDE module integration (Tier 1).
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 80: Tier 2 verification result (TIER-02)")]
public sealed record Tier2VerificationResult
{
    /// <summary>The module that was verified.</summary>
    public required ModuleId Module { get; init; }

    /// <summary>Human-readable name of the module.</summary>
    public required string ModuleName { get; init; }

    /// <summary>True if Tier2FallbackGuard.CheckFallback returns Tier2Available=true.</summary>
    public required bool FallbackGuardPassed { get; init; }

    /// <summary>True if Tier2FallbackGuard.EnsureTier2Active returns true.</summary>
    public required bool EnsureTier2ActivePassed { get; init; }

    /// <summary>
    /// Extracted plugin name(s) from the fallback description
    /// (e.g., "UltimateEncryption + UltimateAccessControl" for Security).
    /// </summary>
    public required string FallbackPluginName { get; init; }

    /// <summary>Full description from Tier2FallbackGuard.GetFallbackDescription.</summary>
    public required string FallbackDescription { get; init; }

    /// <summary>
    /// True if the fallback description references a known plugin name
    /// (not a generic fallback). Verified against the expected plugin mapping.
    /// </summary>
    public required bool PluginMappingVerified { get; init; }

    /// <summary>
    /// Overall Tier 2 verification pass: FallbackGuardPassed AND EnsureTier2ActivePassed AND PluginMappingVerified.
    /// </summary>
    public bool Tier2Verified => FallbackGuardPassed && EnsureTier2ActivePassed && PluginMappingVerified;

    /// <summary>Additional details about the verification outcome.</summary>
    public required string Details { get; init; }
}
