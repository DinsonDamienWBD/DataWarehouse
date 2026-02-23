using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Verification;

/// <summary>
/// Structured verification result for a single VDE module's Tier 1 (VDE-integrated)
/// implementation. Reports whether the module's region can round-trip serialize/deserialize
/// and whether its inode extension fields are correctly defined.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 80: Tier 1 verification result (TIER-01)")]
public sealed record Tier1VerificationResult
{
    /// <summary>Which module was verified.</summary>
    public ModuleId Module { get; init; }

    /// <summary>Human-readable name from <see cref="ModuleRegistry"/>.</summary>
    public string ModuleName { get; init; } = string.Empty;

    /// <summary>True if the module has at least one dedicated region (RegionNames.Length > 0).</summary>
    public bool HasDedicatedRegion { get; init; }

    /// <summary>True if the module contributes inode extension bytes (InodeFieldBytes > 0).</summary>
    public bool HasInodeFields { get; init; }

    /// <summary>
    /// True if the region serialize/deserialize round-trip succeeded.
    /// False for region-less modules (Sustainability, Transit).
    /// </summary>
    public bool RegionRoundTripPassed { get; init; }

    /// <summary>True if the inode extension field layout is confirmed in <see cref="InodeLayoutDescriptor"/>.</summary>
    public bool InodeFieldVerified { get; init; }

    /// <summary>
    /// Overall Tier 1 verification pass. True if the module has at least one working
    /// storage path: a dedicated region OR inode extension fields (or both).
    /// </summary>
    public bool Tier1Verified { get; init; }

    /// <summary>Human-readable explanation of the verification outcome.</summary>
    public string Details { get; init; } = string.Empty;
}
