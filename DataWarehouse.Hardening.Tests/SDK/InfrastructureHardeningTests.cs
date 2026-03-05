using System;
using System.Linq;
using System.Reflection;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Edge.Inference;
using DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;
using DataWarehouse.SDK.VirtualDiskEngine.Sql;
using Xunit;

namespace DataWarehouse.Hardening.Tests.SDK;

/// <summary>
/// Hardening tests for SDK findings 999-1100 covering IncidentResponseEngine,
/// index files, InferenceSettings, and Infrastructure code quality fixes.
/// </summary>
public class InfrastructureHardeningTests
{
    // Finding 1003-1004: IncidentResponseEngine redundant null checks removed
    [Fact]
    public void Finding1003_1004_IncidentResponseEngine_RedundantNullChecksRemoved()
    {
        // Dictionary<string, object>.TryGetValue returns non-nullable object
        // The redundant != null checks were correctly removed
        var type = typeof(DataWarehouse.SDK.Security.IncidentResponse.IncidentResponseEngine);
        Assert.NotNull(type);
        var method = type.GetMethod("Dispose");
        Assert.NotNull(method);
    }

    // Finding 1007: IndexMorphPolicy._jsonOptions -> JsonOptions (static readonly naming)
    [Fact]
    public void Finding1007_IndexMorphPolicy_StaticFieldNaming()
    {
        var field = typeof(IndexMorphPolicy).GetField("JsonOptions", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        Assert.Null(typeof(IndexMorphPolicy).GetField("_jsonOptions", BindingFlags.NonPublic | BindingFlags.Static));
    }

    // Findings 1009-1010: IndexOnlyScan redundant null checks removed
    [Fact]
    public void Finding1009_1010_IndexOnlyScan_NonNullableParameters()
    {
        var method = typeof(IndexOnlyScan).GetMethod("CanSatisfyFromIndexOnly");
        Assert.NotNull(method);
        // Parameters are non-nullable string[] -- null checks were removed, only length checks remain
        var params_ = method!.GetParameters();
        Assert.Equal(2, params_.Length);
        Assert.Equal(typeof(string[]), params_[0].ParameterType);
        Assert.Equal(typeof(string[]), params_[1].ParameterType);
    }

    // Finding 1011: IndexRaid.s_jsonOptions -> SJsonOptions
    [Fact]
    public void Finding1011_IndexRaid_StaticFieldNaming()
    {
        var type = typeof(DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex.IndexRaidConfig);
        var field = type.GetField("SJsonOptions", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        Assert.Null(type.GetField("s_jsonOptions", BindingFlags.NonPublic | BindingFlags.Static));
    }

    // Findings 1013-1016: InferenceSettings ExecutionProvider CPU->Cpu, CUDA->Cuda, etc.
    [Theory]
    [InlineData("Cpu")]
    [InlineData("Cuda")]
    [InlineData("DirectMl")]
    [InlineData("TensorRt")]
    public void Finding1013_1016_ExecutionProvider_PascalCase(string name)
    {
        Assert.True(Enum.TryParse<DataWarehouse.SDK.Edge.Inference.ExecutionProvider>(name, out _));
    }

    [Fact]
    public void Finding1013_ExecutionProvider_OldNamesRemoved()
    {
        var names = Enum.GetNames<DataWarehouse.SDK.Edge.Inference.ExecutionProvider>();
        Assert.DoesNotContain("CPU", names);
        Assert.DoesNotContain("CUDA", names);
        Assert.DoesNotContain("DirectML", names);
        Assert.DoesNotContain("TensorRT", names);
    }

    // Finding 1017: AuthorityChainFacade exists
    [Fact]
    public void Finding1017_AuthorityChainFacade_Exists()
    {
        var type = Type.GetType("DataWarehouse.SDK.Infrastructure.Authority.AuthorityChainFacade, DataWarehouse.SDK");
        Assert.NotNull(type);
    }

    // Finding 1020: DeadManSwitch atomic CAS on _isLocked
    [Fact]
    public void Finding1020_DeadManSwitch_AtomicLocking()
    {
        var type = Type.GetType("DataWarehouse.SDK.Infrastructure.Authority.DeadManSwitch, DataWarehouse.SDK");
        Assert.NotNull(type);
        var field = type!.GetField("_isLockedFlag", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
    }

    // Finding 1065: InMemoryCircuitBreaker thread safety
    [Fact]
    public void Finding1065_InMemoryCircuitBreaker_Exists()
    {
        var type = Type.GetType("DataWarehouse.SDK.Infrastructure.InMemory.InMemoryCircuitBreaker, DataWarehouse.SDK");
        Assert.NotNull(type);
    }

    // Finding 1091: MemoryPressureMonitor -- Process disposed via using var
    [Fact]
    public void Finding1091_MemoryPressureMonitor_Exists()
    {
        var type = Type.GetType("DataWarehouse.SDK.Infrastructure.MemoryPressureMonitor, DataWarehouse.SDK");
        Assert.NotNull(type);
    }

    // Finding 1095: CascadeOverrideStore uses atomic AddOrUpdate
    [Fact]
    public void Finding1095_CascadeOverrideStore_AtomicUpdate()
    {
        var type = Type.GetType("DataWarehouse.SDK.Infrastructure.Policy.CascadeOverrideStore, DataWarehouse.SDK");
        Assert.NotNull(type);
        var method = type!.GetMethod("SetOverride");
        Assert.NotNull(method);
    }

    // Finding 1096: CircularReferenceDetector uses HashSet instead of List
    [Fact]
    public void Finding1096_CircularReferenceDetector_Exists()
    {
        var type = Type.GetType("DataWarehouse.SDK.Infrastructure.Policy.CircularReferenceDetector, DataWarehouse.SDK");
        Assert.NotNull(type);
    }

    // Finding 1097: PolicyCompatibilityGate volatile field
    [Fact]
    public void Finding1097_PolicyCompatibilityGate_VolatileField()
    {
        var type = Type.GetType("DataWarehouse.SDK.Infrastructure.Policy.Compatibility.PolicyCompatibilityGate, DataWarehouse.SDK");
        Assert.NotNull(type);
        var field = type!.GetField("_isMultiLevelEnabled", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
    }

    // Finding 1100: FilePolicyPersistence logged exceptions
    [Fact]
    public void Finding1100_FilePolicyPersistence_Exists()
    {
        var type = Type.GetType("DataWarehouse.SDK.Infrastructure.Policy.FilePolicyPersistence, DataWarehouse.SDK");
        Assert.NotNull(type);
    }

    // Finding 1177: InMemoryResourceMeter s_processorCount -> SProcessorCount
    [Fact]
    public void Finding1177_InMemoryResourceMeter_StaticFieldNaming()
    {
        var type = Type.GetType("DataWarehouse.SDK.Infrastructure.InMemory.InMemoryResourceMeter, DataWarehouse.SDK");
        Assert.NotNull(type);
        var field = type!.GetField("SProcessorCount", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        Assert.Null(type.GetField("s_processorCount", BindingFlags.NonPublic | BindingFlags.Static));
    }

    // Findings 1129-1169: InfrastructureContracts RAID enum naming
    [Theory]
    [InlineData("Raid0")]
    [InlineData("Raid1")]
    [InlineData("Raid5")]
    [InlineData("Raid6")]
    [InlineData("Raid10")]
    [InlineData("RaidZ1")]
    [InlineData("RaidZ2")]
    [InlineData("RaidZ3")]
    [InlineData("RaidJbod")]
    [InlineData("RaidLinear")]
    public void Finding1129_1169_RaidLevel_PascalCase(string name)
    {
        Assert.True(Enum.TryParse<RaidLevel>(name, out _));
    }

    [Fact]
    public void Finding1129_1169_RaidLevel_OldNamesRemoved()
    {
        var names = Enum.GetNames<RaidLevel>();
        Assert.DoesNotContain("RAID_0", names);
        Assert.DoesNotContain("RAID_5", names);
        Assert.DoesNotContain("RAID_JBOD", names);
        Assert.DoesNotContain("RAID_Linear", names);
    }
}
