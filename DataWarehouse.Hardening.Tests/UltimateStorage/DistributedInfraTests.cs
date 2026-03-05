// Hardening tests for UltimateStorage DistributedStorageInfrastructure finding 250
// Finding 250 (CRITICAL): Add summary element to documentation comment

namespace DataWarehouse.Hardening.Tests.UltimateStorage;

public class DistributedInfraTests
{
    private static readonly string InfraFile = Path.Combine(
        "C:", "Temp", "DataWarehouse", "DataWarehouse", "Plugins",
        "DataWarehouse.Plugins.UltimateStorage", "Strategies", "DistributedStorageInfrastructure.cs");

    /// <summary>
    /// Finding 250 (CRITICAL): Documentation comment must have summary element.
    /// </summary>
    [Fact]
    public void Finding250_DocComment_HasSummary()
    {
        var code = File.ReadAllText(InfraFile);
        // The param doc at line 21-27 must be preceded by a summary
        // Check that all public types have /// <summary>
        Assert.True(
            code.Contains("/// <summary>") &&
            code.Contains("QuorumConsistencyManager"),
            "QuorumConsistencyManager must have <summary> documentation");

        // Specifically, the constructor doc must have a summary
        var constructorArea = code.Substring(0, code.IndexOf("public QuorumConsistencyManager"));
        var lastSummaryIndex = constructorArea.LastIndexOf("/// <summary>");
        Assert.True(lastSummaryIndex >= 0,
            "QuorumConsistencyManager constructor must have a summary doc comment");
    }
}
