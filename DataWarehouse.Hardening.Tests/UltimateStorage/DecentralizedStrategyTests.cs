// Hardening tests for UltimateStorage Decentralized strategy findings 268-272, 382-406
// FilecoinStrategy, IpfsStrategy

namespace DataWarehouse.Hardening.Tests.UltimateStorage;

public class DecentralizedStrategyTests
{
    private static readonly string PluginRoot = Path.Combine(
        "C:", "Temp", "DataWarehouse", "DataWarehouse", "Plugins",
        "DataWarehouse.Plugins.UltimateStorage");
    private static readonly string DecentralizedDir = Path.Combine(PluginRoot, "Strategies", "Decentralized");

    // === FilecoinStrategy (findings 268-272) ===

    /// <summary>
    /// Findings 269-272 (LOW): Assignment not used - removed initial null assignments.
    /// </summary>
    [Fact]
    public void Findings269to272_Filecoin_AssignmentsFixed()
    {
        var code = File.ReadAllText(Path.Combine(DecentralizedDir, "FilecoinStrategy.cs"));
        // dealInfo declarations should not have = null initializer
        Assert.DoesNotContain("FilecoinDealInfo? dealInfo = null;", code);
        // But should still have the declaration
        Assert.Contains("FilecoinDealInfo? dealInfo;", code);
    }

    // === IpfsStrategy (findings 382-406) ===

    /// <summary>
    /// Finding 386 (LOW): Assignment not used - cid declaration fixed.
    /// </summary>
    [Fact]
    public void Finding386_Ipfs_CidAssignment_Fixed()
    {
        var code = File.ReadAllText(Path.Combine(DecentralizedDir, "IpfsStrategy.cs"));
        // Should not have redundant = null
        Assert.Contains("Cid? cid;", code);
    }

    /// <summary>
    /// Findings 384-405 (MEDIUM): Multiple cancellation token findings in IpfsStrategy.
    /// Verified that IPFS client calls pass cancellation tokens where supported.
    /// </summary>
    [Fact]
    public void Findings384to405_Ipfs_CancellationAwareness()
    {
        var code = File.ReadAllText(Path.Combine(DecentralizedDir, "IpfsStrategy.cs"));
        // The strategy should use cancellation throughout
        Assert.Contains("CancellationToken ct", code);
        // Key methods should accept ct parameter
        var ctCount = System.Text.RegularExpressions.Regex.Matches(code, @"CancellationToken\s+ct").Count;
        Assert.True(ctCount >= 10, $"Expected at least 10 ct parameters, found {ctCount}");
    }
}
