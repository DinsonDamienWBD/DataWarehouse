using System;
using System.Linq;
using System.Reflection;
using DataWarehouse.SDK.Compliance;
using DataWarehouse.SDK.Connectors;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Carbon;
using DataWarehouse.SDK.Contracts.EdgeComputing;
using DataWarehouse.SDK.Edge.Inference;
using DataWarehouse.SDK.Hardware;
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Sustainability;
using DataWarehouse.SDK.Virtualization;
using Xunit;

namespace DataWarehouse.Hardening.Tests.SDK;

/// <summary>
/// Hardening tests for SDK findings 955-998 covering ICacheableStorage
/// through IMultiMasterReplication interface/enum naming conventions.
/// </summary>
public class CarbonAndInterfaceHardeningTests
{
    // Finding 955: ICacheableStorage.CacheEvictionPolicy.TTL -> Ttl
    [Fact]
    public void Finding955_CacheEvictionPolicy_Ttl_PascalCase()
    {
        Assert.True(Enum.IsDefined(typeof(CacheEvictionPolicy), CacheEvictionPolicy.Ttl));
        var names = Enum.GetNames<CacheEvictionPolicy>();
        Assert.DoesNotContain("TTL", names);
    }

    // Finding 956: ICarbonAwareStorage.GramsCO2PerKwh -> GramsCo2PerKwh
    [Fact]
    public void Finding956_CarbonIntensityData_GramsCo2PerKwh_PascalCase()
    {
        var data = new CarbonIntensityData("us-west-2", 150.0, CarbonIntensityLevel.Medium, 40.0, DateTimeOffset.UtcNow, null);
        Assert.Equal(150.0, data.GramsCo2PerKwh);
    }

    // Finding 957: MaxGramsCO2PerKwh -> MaxGramsCo2PerKwh
    [Fact]
    public void Finding957_CarbonThreshold_MaxGramsCo2PerKwh_PascalCase()
    {
        var threshold = new CarbonThreshold(200.0, CarbonIntensityLevel.High);
        Assert.Equal(200.0, threshold.MaxGramsCo2PerKwh);
    }

    // Finding 958-960: OffsetStandard VCS->Vcs, ACR->Acr, CAR->Car
    [Theory]
    [InlineData("Vcs")]
    [InlineData("Acr")]
    [InlineData("Car")]
    public void Finding958_960_OffsetStandard_PascalCase(string name)
    {
        Assert.True(Enum.TryParse<OffsetStandard>(name, out _), $"OffsetStandard should have member '{name}'");
    }

    [Fact]
    public void Finding958_960_OffsetStandard_OldNamesRemoved()
    {
        var names = Enum.GetNames<OffsetStandard>();
        Assert.DoesNotContain("VCS", names);
        Assert.DoesNotContain("ACR", names);
        Assert.DoesNotContain("CAR", names);
    }

    // Finding 961: PricePerTonCO2 -> PricePerTonCo2
    [Fact]
    public void Finding961_OffsetProject_PricePerTonCo2_PascalCase()
    {
        var project = new OffsetProject("P1", "Test", "Reforestation", OffsetStandard.Vcs, 12.50, "US");
        Assert.Equal(12.50, project.PricePerTonCo2);
    }

    // Findings 962-964: ICarbonBudget parameter names CO2e -> Co2E
    [Fact]
    public void Finding962_964_ICarbonBudgetService_ParameterNames_PascalCase()
    {
        var method = typeof(ICarbonBudgetService).GetMethod("SetBudgetAsync");
        Assert.NotNull(method);
        var param = method!.GetParameters().FirstOrDefault(p => p.Name == "budgetGramsCo2E");
        Assert.NotNull(param);

        var canProceed = typeof(ICarbonBudgetService).GetMethod("CanProceedAsync");
        Assert.NotNull(canProceed);
        var param2 = canProceed!.GetParameters().FirstOrDefault(p => p.Name == "estimatedCarbonGramsCo2E");
        Assert.NotNull(param2);

        var recordUsage = typeof(ICarbonBudgetService).GetMethod("RecordUsageAsync");
        Assert.NotNull(recordUsage);
        var param3 = recordUsage!.GetParameters().FirstOrDefault(p => p.Name == "carbonGramsCo2E");
        Assert.NotNull(param3);
    }

    // Finding 965: CarbonSummary.TotalEmissionsGramsCo2E (already PascalCase)
    [Fact]
    public void Finding965_CarbonSummary_TotalEmissionsGramsCo2E_PascalCase()
    {
        var prop = typeof(CarbonSummary).GetProperty("TotalEmissionsGramsCo2E");
        Assert.NotNull(prop);
    }

    // Findings 966-967: ComplianceFramework GDPR->Gdpr, HIPAA->Hipaa (already fixed)
    [Theory]
    [InlineData("Gdpr")]
    [InlineData("Hipaa")]
    public void Finding966_967_ComplianceFramework_PascalCase(string name)
    {
        Assert.True(Enum.TryParse<ComplianceFramework>(name, out _));
    }

    [Fact]
    public void Finding966_967_ComplianceFramework_OldNamesRemoved()
    {
        var names = Enum.GetNames<ComplianceFramework>();
        Assert.DoesNotContain("GDPR", names);
        Assert.DoesNotContain("HIPAA", names);
    }

    // Finding 968: Soc2TypeII -> Soc2TypeIi
    [Fact]
    public void Finding968_ComplianceFramework_Soc2TypeIi_PascalCase()
    {
        Assert.True(Enum.IsDefined(typeof(ComplianceFramework), ComplianceFramework.Soc2TypeIi));
        Assert.DoesNotContain("Soc2TypeII", Enum.GetNames<ComplianceFramework>());
    }

    // Findings 969-971: ICompressionProvider LZ4->Lz4, properties Lz4HighCompression
    [Fact]
    public void Finding969_CompressionAlgorithm_Lz4_PascalCase()
    {
        Assert.True(Enum.IsDefined(typeof(CompressionAlgorithm), CompressionAlgorithm.Lz4));
        Assert.DoesNotContain("LZ4", Enum.GetNames<CompressionAlgorithm>());
    }

    [Fact]
    public void Finding970_971_CompressionOptions_Lz4Properties_PascalCase()
    {
        var options = new CompressionOptions();
        Assert.False(options.Lz4HighCompression);
        Assert.Equal(64 * 1024, options.Lz4BlockSize);
        Assert.Null(typeof(CompressionOptions).GetProperty("LZ4HighCompression"));
        Assert.Null(typeof(CompressionOptions).GetProperty("LZ4BlockSize"));
    }

    // Finding 972: IConnectionStrategy Properties collection (init-only by design)
    [Fact]
    public void Finding972_ConnectionConfig_Properties_InitOnly()
    {
        var config = new ConnectionConfig { ConnectionString = "test", Properties = new() { ["key"] = "val" } };
        Assert.Single(config.Properties);
    }

    // Finding 973: IDataConnector AI -> Ai
    [Fact]
    public void Finding973_ConnectorCategory_Ai_PascalCase()
    {
        Assert.True(Enum.IsDefined(typeof(ConnectorCategory), ConnectorCategory.Ai));
        Assert.DoesNotContain("AI", Enum.GetNames<ConnectorCategory>());
    }

    // Finding 975: IEdgeComputingStrategy SupportsEdgeML -> SupportsEdgeMl
    [Fact]
    public void Finding975_EdgeComputingCapabilities_SupportsEdgeMl_PascalCase()
    {
        var prop = typeof(EdgeComputingCapabilities).GetProperty("SupportsEdgeMl");
        Assert.NotNull(prop);
        Assert.Null(typeof(EdgeComputingCapabilities).GetProperty("SupportsEdgeML"));
    }

    // Finding 976: ModelType.NLP -> Nlp
    [Fact]
    public void Finding976_ModelType_Nlp_PascalCase()
    {
        Assert.True(Enum.IsDefined(typeof(DataWarehouse.SDK.Contracts.EdgeComputing.ModelType),
            DataWarehouse.SDK.Contracts.EdgeComputing.ModelType.Nlp));
        Assert.DoesNotContain("NLP", Enum.GetNames<DataWarehouse.SDK.Contracts.EdgeComputing.ModelType>());
    }

    // Findings 978-980: IHardwareAcceleration IntelQAT->IntelQat, OpenCL->OpenCl
    [Fact]
    public void Finding978_AcceleratorType_IntelQat_PascalCase()
    {
        Assert.True(Enum.IsDefined(typeof(AcceleratorType), AcceleratorType.IntelQat));
        Assert.DoesNotContain("IntelQAT", Enum.GetNames<AcceleratorType>());
    }

    [Fact]
    public void Finding979_980_GpuRuntime_OpenCl_PascalCase()
    {
        Assert.True(Enum.IsDefined(typeof(GpuRuntime), GpuRuntime.OpenCl));
        Assert.DoesNotContain("OpenCL", Enum.GetNames<GpuRuntime>());
    }

    // Findings 981-983: IHypervisorSupport VMwareESXi->VMwareEsXi, OVirtRHV->OVirtRhv, etc.
    [Theory]
    [InlineData("VMwareEsXi")]
    [InlineData("OVirtRhv")]
    [InlineData("NutanixAhv")]
    public void Finding981_983_HypervisorType_PascalCase(string name)
    {
        Assert.True(Enum.TryParse<HypervisorType>(name, out _));
    }

    // Findings 984-985: BackupApiType VMwareVADP->VMwareVadp, HyperVVSS->HyperVvss
    [Theory]
    [InlineData("VMwareVadp")]
    [InlineData("HyperVvss")]
    public void Finding984_985_BackupApiType_PascalCase(string name)
    {
        Assert.True(Enum.TryParse<BackupApiType>(name, out _));
    }

    // Findings 986-987: II2cBusController -> II2CBusController, II2cDevice -> II2CDevice
    [Fact]
    public void Finding986_987_I2C_InterfaceNaming()
    {
        var busType = typeof(DataWarehouse.SDK.Edge.Bus.II2CBusController);
        Assert.True(busType.IsInterface);
        var deviceType = typeof(DataWarehouse.SDK.Edge.Bus.II2CDevice);
        Assert.True(deviceType.IsInterface);
    }

    // Finding 988: IKernelInfrastructure GCTotalMemory -> GcTotalMemory
    [Fact]
    public void Finding988_MemoryStatistics_GcTotalMemory_PascalCase()
    {
        var prop = typeof(DataWarehouse.SDK.Contracts.MemoryStatistics).GetProperty("GcTotalMemory");
        Assert.NotNull(prop);
    }

    // Findings 990-993: IMessageBus constants AIQuery->AiQuery, AIEmbed->AiEmbed, SecurityACL->SecurityAcl
    [Fact]
    public void Finding990_993_MessageTopics_PascalCase()
    {
        var topicsType = typeof(DataWarehouse.SDK.Contracts.MessageTopics);
        Assert.NotNull(topicsType.GetField("AiQuery"));
        Assert.NotNull(topicsType.GetField("AiEmbed"));
        Assert.NotNull(topicsType.GetField("SecurityAcl"));
        Assert.Null(topicsType.GetField("AIQuery"));
        Assert.Null(topicsType.GetField("AIEmbed"));
        Assert.Null(topicsType.GetField("SecurityACL"));
    }

    // Findings 994-997: IMilitarySecurity DestructionMethod naming
    [Theory]
    [InlineData("DoD522022M")]
    [InlineData("DoD522022MEce")]
    [InlineData("Nist80088Clear")]
    [InlineData("Nist80088Purge")]
    public void Finding994_997_DestructionMethod_PascalCase(string name)
    {
        Assert.True(Enum.TryParse<DataWarehouse.SDK.Security.DestructionMethod>(name, out _));
    }

    // Finding 998: IMultiMasterReplication CRDT -> Crdt
    [Fact]
    public void Finding998_ConflictResolution_Crdt_PascalCase()
    {
        Assert.True(Enum.IsDefined(typeof(DataWarehouse.SDK.Replication.ConflictResolution),
            DataWarehouse.SDK.Replication.ConflictResolution.Crdt));
        Assert.DoesNotContain("CRDT", Enum.GetNames<DataWarehouse.SDK.Replication.ConflictResolution>());
    }
}
