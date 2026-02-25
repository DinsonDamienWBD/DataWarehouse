using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Verification;

/// <summary>
/// Categorizes the type of Tier 3 basic fallback behavior available for a module
/// when neither VDE module storage (Tier 1) nor plugin pipeline optimization (Tier 2) is active.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 80: Tier 3 fallback mode enum (TIER-03)")]
public enum Tier3FallbackMode : byte
{
    /// <summary>Feature uses in-memory storage only; data is lost on restart.</summary>
    InMemoryDefault = 0,

    /// <summary>Feature gracefully does nothing (e.g., metrics not collected, no audit trail).</summary>
    NoOpSafe = 1,

    /// <summary>Feature uses simple flat file storage for persistence.</summary>
    FileBasedSimple = 2,

    /// <summary>Feature uses configuration defaults without dynamic optimization.</summary>
    ConfigDriven = 3,
}

/// <summary>
/// Structured result of Tier 3 basic fallback verification for a single VDE module.
/// Tier 3 is the most degraded but always-available mode: in-memory defaults, no-op safe
/// behaviors, or simple file-based fallbacks that work without any plugin or VDE integration.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 80: Tier 3 verification result (TIER-03)")]
public sealed record Tier3VerificationResult
{
    /// <summary>The module being verified.</summary>
    public required ModuleId Module { get; init; }

    /// <summary>Human-readable module name.</summary>
    public required string ModuleName { get; init; }

    /// <summary>The category of Tier 3 fallback behavior for this module.</summary>
    public required Tier3FallbackMode FallbackMode { get; init; }

    /// <summary>Human-readable description of what Tier 3 means for this module.</summary>
    public required string Tier3Description { get; init; }

    /// <summary>
    /// True if the feature works at a basic level without plugin or VDE support.
    /// Always true for Tier 3: the system works, just without this feature's optimization.
    /// </summary>
    public required bool BasicFunctionalityAvailable { get; init; }

    /// <summary>What the user experiences at Tier 3 (minimal behavior description).</summary>
    public required string MinimalBehavior { get; init; }

    /// <summary>What causes promotion from Tier 3 to Tier 2 (plugin pipeline).</summary>
    public required string PromotionTrigger { get; init; }

    /// <summary>Overall Tier 3 verification pass/fail.</summary>
    public required bool Tier3Verified { get; init; }

    /// <summary>Additional verification details or notes.</summary>
    public required string Details { get; init; }
}
