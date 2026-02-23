using System.Text;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Verification;

/// <summary>
/// Consolidated verification report produced by <see cref="ThreeTierVerificationSuite"/>.
/// Contains results from all three tier verifiers, the tier feature map, performance
/// benchmarks, and an overall summary.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 80: Three-tier verification report (TIER-01..TIER-05)")]
public sealed record VerificationReport
{
    /// <summary>Tier 1 (VDE-integrated) verification results for all 19 modules.</summary>
    public required IReadOnlyList<Tier1VerificationResult> Tier1Results { get; init; }

    /// <summary>Tier 2 (plugin pipeline) verification results for all 19 modules.</summary>
    public required IReadOnlyList<Tier2VerificationResult> Tier2Results { get; init; }

    /// <summary>Tier 3 (basic fallback) verification results for all 19 modules.</summary>
    public required IReadOnlyList<Tier3VerificationResult> Tier3Results { get; init; }

    /// <summary>Per-feature tier mapping for all 19 modules.</summary>
    public required IReadOnlyList<FeatureTierAssignment> TierMap { get; init; }

    /// <summary>Performance benchmarks for 5 representative features.</summary>
    public required IReadOnlyList<BenchmarkResult> Benchmarks { get; init; }

    /// <summary>True if all 19 modules passed Tier 1 verification.</summary>
    public required bool AllTier1Passed { get; init; }

    /// <summary>True if all 19 modules passed Tier 2 verification.</summary>
    public required bool AllTier2Passed { get; init; }

    /// <summary>True if all 19 modules passed Tier 3 verification.</summary>
    public required bool AllTier3Passed { get; init; }

    /// <summary>Total number of modules verified (always 19).</summary>
    public required int TotalModules { get; init; }

    /// <summary>
    /// Human-readable summary: "{N}/19 Tier 1 passed, {N}/19 Tier 2 passed,
    /// {N}/19 Tier 3 passed, {N} benchmarks completed".
    /// </summary>
    public required string Summary { get; init; }
}

/// <summary>
/// Unified entry point that orchestrates all three tier verifiers, the tier feature map,
/// and performance benchmarks into a single consolidated <see cref="VerificationReport"/>.
/// </summary>
/// <remarks>
/// This suite satisfies TIER-01 through TIER-05:
/// <list type="bullet">
/// <item>TIER-01: Tier 1 module verification (via <see cref="Tier1ModuleVerifier"/>)</item>
/// <item>TIER-02: Tier 2 pipeline verification (via <see cref="Tier2PipelineVerifier"/>)</item>
/// <item>TIER-03: Tier 3 fallback verification (via <see cref="Tier3BasicFallbackVerifier"/>)</item>
/// <item>TIER-04: Per-feature tier mapping (via <see cref="TierFeatureMap"/>)</item>
/// <item>TIER-05: Performance benchmarks (via <see cref="TierPerformanceBenchmark"/>)</item>
/// </list>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 80: Three-tier verification suite (TIER-01..TIER-05)")]
public static class ThreeTierVerificationSuite
{
    /// <summary>
    /// Runs the full three-tier verification suite: Tier 1, Tier 2, and Tier 3 module
    /// verification, tier feature mapping, and performance benchmarks.
    /// </summary>
    /// <returns>
    /// A <see cref="VerificationReport"/> containing all results, pass/fail flags,
    /// and a human-readable summary.
    /// </returns>
    public static VerificationReport RunFullVerification()
    {
        // a. Tier 1: VDE-integrated module verification
        var tier1Results = Tier1ModuleVerifier.VerifyAllModules();

        // b. Tier 2: Plugin pipeline verification
        var tier2Results = Tier2PipelineVerifier.VerifyAllModules();

        // c. Tier 3: Basic fallback verification
        var tier3Results = Tier3BasicFallbackVerifier.VerifyAllModules();

        // d. Tier feature map
        var tierMap = TierFeatureMap.GetFeatureMap();

        // e. Performance benchmarks
        var benchmarks = TierPerformanceBenchmark.RunBenchmarks();

        // f. Compute pass/fail flags
        int tier1Passed = 0;
        foreach (var r in tier1Results)
        {
            if (r.Tier1Verified) tier1Passed++;
        }

        int tier2Passed = 0;
        foreach (var r in tier2Results)
        {
            if (r.Tier2Verified) tier2Passed++;
        }

        int tier3Passed = 0;
        foreach (var r in tier3Results)
        {
            if (r.Tier3Verified) tier3Passed++;
        }

        bool allTier1 = tier1Passed == FormatConstants.DefinedModules;
        bool allTier2 = tier2Passed == FormatConstants.DefinedModules;
        bool allTier3 = tier3Passed == FormatConstants.DefinedModules;

        // g. Generate summary
        var summary = new StringBuilder();
        summary.Append($"{tier1Passed}/{FormatConstants.DefinedModules} Tier 1 passed, ");
        summary.Append($"{tier2Passed}/{FormatConstants.DefinedModules} Tier 2 passed, ");
        summary.Append($"{tier3Passed}/{FormatConstants.DefinedModules} Tier 3 passed, ");
        summary.Append($"{benchmarks.Count} benchmarks completed");

        return new VerificationReport
        {
            Tier1Results = tier1Results,
            Tier2Results = tier2Results,
            Tier3Results = tier3Results,
            TierMap = tierMap,
            Benchmarks = benchmarks,
            AllTier1Passed = allTier1,
            AllTier2Passed = allTier2,
            AllTier3Passed = allTier3,
            TotalModules = FormatConstants.DefinedModules,
            Summary = summary.ToString(),
        };
    }
}
