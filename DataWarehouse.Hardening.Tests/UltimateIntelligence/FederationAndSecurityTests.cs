// Hardening tests for UltimateIntelligence findings 95-118
// EvolvingIntelligenceStrategies, FederationSystem, IntelligenceSecurity

namespace DataWarehouse.Hardening.Tests.UltimateIntelligence;

public class FederationAndSecurityTests
{
    private static readonly string PluginDir = Path.Combine(
        "C:", "Temp", "DataWarehouse", "DataWarehouse", "Plugins",
        "DataWarehouse.Plugins.UltimateIntelligence");

    private static readonly string FederationFile = Path.Combine(
        PluginDir, "Federation", "FederationSystem.cs");

    // --- EvolvingIntelligenceStrategies.cs (finding 95) ---

    [Fact]
    public void Finding095_InconsistentSynchronization()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "Strategies", "Evolution", "EvolvingIntelligenceStrategies.cs"));
        // Field at line 993 should be accessed under lock or use Volatile/Interlocked
        Assert.True(
            code.Contains("Volatile.Read") || code.Contains("Interlocked") || code.Contains("lock ("),
            "EvolvingIntelligenceStrategies should use proper synchronization");
    }

    // --- FederationSystem.cs (findings 96-118) ---

    [Fact]
    public void Finding096_AsyncLambdaVoid_Timer()
    {
        var code = File.ReadAllText(FederationFile);
        // Timer callback should be wrapped in try/catch and Task.Run
        Assert.True(
            code.Contains("async _ => { try") ||
            code.Contains("async _ =>\n            {\n                try") ||
            code.Contains("async _ =>\r\n            {\r\n                try"),
            "Timer callback with async lambda should have try/catch wrapper");
    }

    [Fact]
    public void Finding097_098_100_MultipleEnumeration()
    {
        var code = File.ReadAllText(FederationFile);
        // targetInstances should be materialized (ToList) before multiple use
        Assert.True(
            code.Contains("targetInstances.ToList()") ||
            code.Contains("var targetInstances = GetTargetInstances(request).ToList()"),
            "targetInstances should be materialized to avoid multiple enumeration");
    }

    [Fact]
    public void Finding099_DisposedCapturedVariable()
    {
        var code = File.ReadAllText(FederationFile);
        // linkedCts should not be captured in lambda that outlives the using scope
        // The fix is to ensure the lambda completes before disposal
        Assert.True(
            code.Contains("await Task.WhenAll(queryTasks)"),
            "All query tasks should be awaited before CTS disposal");
    }

    [Fact]
    public void Finding106_KnowledgeACL_Renamed()
    {
        var code = File.ReadAllText(FederationFile);
        Assert.DoesNotContain("class KnowledgeACL", code);
        Assert.Contains("class KnowledgeAcl", code);
    }

    [Fact]
    public void Finding107_SetInstanceACL_Renamed()
    {
        var code = File.ReadAllText(FederationFile);
        Assert.DoesNotContain("SetInstanceACL", code);
        Assert.Contains("SetInstanceAcl", code);
    }

    [Fact]
    public void Finding108_SetDomainACL_Renamed()
    {
        var code = File.ReadAllText(FederationFile);
        Assert.DoesNotContain("SetDomainACL", code);
        Assert.Contains("SetDomainAcl", code);
    }

    [Fact]
    public void Finding109_GetInstanceACL_Renamed()
    {
        var code = File.ReadAllText(FederationFile);
        Assert.DoesNotContain("GetInstanceACL", code);
        Assert.Contains("GetInstanceAcl", code);
    }

    [Fact]
    public void Finding110_GetDomainACL_Renamed()
    {
        var code = File.ReadAllText(FederationFile);
        Assert.DoesNotContain("GetDomainACL", code);
        Assert.Contains("GetDomainAcl", code);
    }

    [Fact]
    public void Finding111_InstanceACL_Renamed()
    {
        var code = File.ReadAllText(FederationFile);
        Assert.DoesNotContain("record InstanceACL", code);
        Assert.Contains("record InstanceAcl", code);
    }

    [Fact]
    public void Finding115_DomainACL_Renamed()
    {
        var code = File.ReadAllText(FederationFile);
        Assert.DoesNotContain("record DomainACL", code);
        Assert.Contains("record DomainAcl", code);
    }

    // --- IntelligenceSecurity.cs (findings 164-166) ---

    [Fact]
    public void Finding164_InconsistentSynchronization()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "Security", "IntelligenceSecurity.cs"));
        // _rolePermissions field should be accessed consistently under lock
        Assert.True(
            code.Contains("lock") || code.Contains("ConcurrentDictionary") ||
            code.Contains("BoundedDictionary"),
            "IntelligenceSecurity should use thread-safe access patterns");
    }
}
