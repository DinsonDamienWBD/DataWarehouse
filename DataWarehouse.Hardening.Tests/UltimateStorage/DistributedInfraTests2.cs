// Hardening tests for UltimateStorage findings 251-261
// DistributedStorageInfrastructure: enum renames, batch collection, local constant naming,
// unused field exposure, multiple enumeration materialization

namespace DataWarehouse.Hardening.Tests.UltimateStorage;

public class DistributedInfraTests2
{
    private static readonly string InfraFile = Path.Combine(
        "C:", "Temp", "DataWarehouse", "DataWarehouse", "Plugins",
        "DataWarehouse.Plugins.UltimateStorage", "Strategies", "DistributedStorageInfrastructure.cs");

    /// <summary>
    /// Findings 251-256 (LOW): ConsistencyLevel enum members must be PascalCase.
    /// ONE->One, TWO->Two, THREE->Three, QUORUM->Quorum, LOCAL_QUORUM->LocalQuorum, ALL->All
    /// </summary>
    [Theory]
    [InlineData("One")]
    [InlineData("Two")]
    [InlineData("Three")]
    [InlineData("Quorum")]
    [InlineData("LocalQuorum")]
    [InlineData("All")]
    public void Findings251to256_ConsistencyLevel_PascalCase(string memberName)
    {
        var code = File.ReadAllText(InfraFile);
        Assert.Contains(memberName, code);
    }

    /// <summary>
    /// Findings 251-256: Verify ALL_CAPS enum members are removed.
    /// </summary>
    [Theory]
    [InlineData("    ONE,")]
    [InlineData("    TWO,")]
    [InlineData("    THREE,")]
    [InlineData("    QUORUM,")]
    [InlineData("    LOCAL_QUORUM,")]
    [InlineData("    ALL\n")]
    public void Findings251to256_OldNames_Removed(string oldMember)
    {
        var code = File.ReadAllText(InfraFile);
        Assert.DoesNotContain(oldMember, code);
    }

    /// <summary>
    /// Finding 257 (LOW): Batch collection was only updated but never used. Removed.
    /// </summary>
    [Fact]
    public void Finding257_BatchCollection_Removed()
    {
        var code = File.ReadAllText(InfraFile);
        Assert.DoesNotContain("var batch = new List<ReplicationEvent>()", code);
    }

    /// <summary>
    /// Finding 258 (LOW): Local constant MaxLogEntries renamed to camelCase maxLogEntries.
    /// </summary>
    [Fact]
    public void Finding258_LocalConstant_CamelCase()
    {
        var code = File.ReadAllText(InfraFile);
        Assert.Contains("const int maxLogEntries", code);
        Assert.DoesNotContain("const int MaxLogEntries", code);
    }

    /// <summary>
    /// Finding 259 (LOW): _healthCheckInterval unused field exposed as internal property.
    /// </summary>
    [Fact]
    public void Finding259_HealthCheckInterval_Exposed()
    {
        var code = File.ReadAllText(InfraFile);
        Assert.Contains("internal TimeSpan HealthCheckInterval", code);
    }

    /// <summary>
    /// Findings 260-261 (MEDIUM): PossibleMultipleEnumeration materialized with .ToList().
    /// </summary>
    [Fact]
    public void Findings260to261_MultipleEnumeration_Materialized()
    {
        var code = File.ReadAllText(InfraFile);
        Assert.Contains(".ToList();", code);
        // The healthy variable should be materialized
        Assert.Contains("n.IsHealthy && n.Role == GeoNodeRole.Active).ToList()", code);
    }
}
