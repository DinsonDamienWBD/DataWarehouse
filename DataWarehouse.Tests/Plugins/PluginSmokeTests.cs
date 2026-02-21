using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using HealthStatus = DataWarehouse.SDK.Contracts.HealthStatus;

namespace DataWarehouse.Tests.Plugins;

/// <summary>
/// Comprehensive smoke tests for all 63+ plugins.
/// Verifies that every plugin can be instantiated and has correct identity,
/// capabilities, and health status.
/// Uses [Theory] with [MemberData] to test all plugins from shared test methods.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Scope", "SmokeTest")]
public class PluginSmokeTests : IDisposable
{
    private readonly List<IDisposable> _disposables = new();

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            try { d.Dispose(); } catch { /* ignore cleanup errors */ }
        }
    }

    // ===== Plugin Factory Methods =====
    // Each returns (string name, Func<PluginBase> factory, string idHint)
    // Plugins requiring DI use Moq for ILogger dependencies.

    public static IEnumerable<object[]> AllPluginFactories => new List<object[]>
    {
        // === Storage Plugins ===
        new object[] { "UltimateStorage", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateStorage.UltimateStoragePlugin()), "storage" },
        new object[] { "UltimateStorageProcessing", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateStorageProcessing.UltimateStorageProcessingPlugin()), "storageprocessing" },
        new object[] { "UltimateDatabaseStorage", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateDatabaseStorage.UltimateDatabaseStoragePlugin()), "database" },
        new object[] { "UltimateDatabaseProtocol", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateDatabaseProtocol.UltimateDatabaseProtocolPlugin()), "protocol" },
        new object[] { "UltimateFilesystem", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateFilesystem.UltimateFilesystemPlugin()), "filesystem" },

        // === Security Plugins ===
        new object[] { "UltimateAccessControl", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateAccessControl.UltimateAccessControlPlugin()), "accesscontrol" },
        new object[] { "UltimateEncryption", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateEncryption.UltimateEncryptionPlugin()), "encryption" },
        new object[] { "UltimateKeyManagement", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateKeyManagement.UltimateKeyManagementPlugin()), "key" },
        new object[] { "UltimateCompliance", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateCompliance.UltimateCompliancePlugin()), "compliance" },
        new object[] { "UltimateDataProtection", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateDataProtection.UltimateDataProtectionPlugin()), "protection" },
        new object[] { "UltimateDataPrivacy", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateDataPrivacy.UltimateDataPrivacyPlugin()), "privacy" },

        // === Compression ===
        new object[] { "UltimateCompression", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateCompression.UltimateCompressionPlugin()), "compression" },

        // === Data Management Plugins ===
        new object[] { "UltimateDataCatalog", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateDataCatalog.UltimateDataCatalogPlugin()), "catalog" },
        new object[] { "UltimateDataFabric", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateDataFabric.UltimateDataFabricPlugin()), "fabric" },
        new object[] { "UltimateDataFormat", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateDataFormat.UltimateDataFormatPlugin()), "dataformat" },
        new object[] { "UltimateDataGovernance", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateDataGovernance.UltimateDataGovernancePlugin()), "governance" },
        new object[] { "UltimateDataIntegration", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateDataIntegration.UltimateDataIntegrationPlugin()), "integration" },
        new object[] { "UltimateDataIntegrity", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateDataIntegrity.UltimateDataIntegrityPlugin()), "integrity" },
        new object[] { "UltimateDataLake", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateDataLake.UltimateDataLakePlugin()), "datalake" },
        new object[] { "UltimateDataLineage", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateDataLineage.UltimateDataLineagePlugin()), "lineage" },
        new object[] { "UltimateDataManagement", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateDataManagement.UltimateDataManagementPlugin()), "management" },
        new object[] { "UltimateDataMesh", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateDataMesh.UltimateDataMeshPlugin()), "mesh" },
        new object[] { "UltimateDataQuality", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateDataQuality.UltimateDataQualityPlugin()), "quality" },

        // === Infrastructure Plugins ===
        new object[] { "UltimateDeployment", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateDeployment.UltimateDeploymentPlugin()), "deployment" },
        new object[] { "UltimateMultiCloud", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateMultiCloud.UltimateMultiCloudPlugin()), "multicloud" },
        new object[] { "UltimateResourceManager", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateResourceManager.UltimateResourceManagerPlugin()), "resource" },
        new object[] { "UltimateSustainability", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateSustainability.UltimateSustainabilityPlugin()), "sustainability" },

        // === Compute Plugins ===
        new object[] { "UltimateCompute", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateCompute.UltimateComputePlugin()), "compute" },
        new object[] { "UltimateServerless", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateServerless.UltimateServerlessPlugin()), "serverless" },
        new object[] { "SelfEmulatingObjects", new Func<PluginBase>(() => new DataWarehouse.Plugins.SelfEmulatingObjects.SelfEmulatingObjectsPlugin()), "selfemulating" },
        new object[] { "ComputeWasm", new Func<PluginBase>(() => new DataWarehouse.Plugins.Compute.Wasm.WasmComputePlugin()), "wasm" },

        // === Streaming / Transport Plugins ===
        new object[] { "UltimateStreamingData", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateStreamingData.UltimateStreamingDataPlugin()), "streaming" },
        new object[] { "UltimateIoTIntegration", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateIoTIntegration.UltimateIoTIntegrationPlugin()), "iot" },
        new object[] { "AdaptiveTransport", new Func<PluginBase>(() => new DataWarehouse.Plugins.AdaptiveTransport.AdaptiveTransportPlugin()), "transport" },
        new object[] { "UltimateRTOSBridge", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateRTOSBridge.UltimateRTOSBridgePlugin()), "rtos" },

        // === Replication / Consensus Plugins ===
        new object[] { "UltimateReplication", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateReplication.UltimateReplicationPlugin()), "replication" },
        new object[] { "UltimateRAID", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateRAID.UltimateRaidPlugin()), "raid" },
        new object[] { "UltimateConsensus", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateConsensus.UltimateConsensusPlugin()), "consensus" },

        // === Platform Plugins ===
        new object[] { "UltimateMicroservices", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateMicroservices.UltimateMicroservicesPlugin()), "microservices" },
        new object[] { "UltimateSDKPorts", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateSDKPorts.UltimateSDKPortsPlugin()), "sdk" },
        new object[] { "UltimateDocGen", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateDocGen.UltimateDocGenPlugin()), "docgen" },
        new object[] { "AppPlatform", new Func<PluginBase>(() => new DataWarehouse.Plugins.AppPlatform.AppPlatformPlugin()), "platform" },
        new object[] { "DataMarketplace", new Func<PluginBase>(() => new DataWarehouse.Plugins.DataMarketplace.DataMarketplacePlugin()), "marketplace" },
        new object[] { "PluginMarketplace", new Func<PluginBase>(() => new DataWarehouse.Plugins.PluginMarketplace.PluginMarketplacePlugin()), "marketplace" },

        // === Interface Plugins ===
        new object[] { "UltimateConnector", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateConnector.UltimateConnectorPlugin()), "connector" },
        new object[] { "UltimateInterface", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateInterface.UltimateInterfacePlugin()), "interface" },
        new object[] { "FuseDriver", new Func<PluginBase>(() => new DataWarehouse.Plugins.FuseDriver.FuseDriverPlugin()), "fuse" },
        new object[] { "KubernetesCsi", new Func<PluginBase>(() => new DataWarehouse.Plugins.KubernetesCsi.KubernetesCsiPlugin()), "csi" },
        new object[] { "UniversalDashboards", new Func<PluginBase>(() => new DataWarehouse.Plugins.UniversalDashboards.UniversalDashboardsPlugin()), "dashboard" },

        // === Orchestration Plugins ===
        new object[] { "UltimateWorkflow", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateWorkflow.UltimateWorkflowPlugin()), "workflow" },
        new object[] { "UltimateEdgeComputing", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateEdgeComputing.UltimateEdgeComputingPlugin()), "edge" },
        new object[] { "SemanticSync", new Func<PluginBase>(() => new DataWarehouse.Plugins.SemanticSync.SemanticSyncPlugin()), "semantic" },

        // === Resilience Plugins ===
        new object[] { "UltimateResilience", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateResilience.UltimateResiliencePlugin()), "resilience" },
        new object[] { "ChaosVaccination", new Func<PluginBase>(() => new DataWarehouse.Plugins.ChaosVaccination.ChaosVaccinationPlugin()), "chaos" },

        // === Observability Plugins ===
        new object[] { "UniversalObservability", new Func<PluginBase>(() => new DataWarehouse.Plugins.UniversalObservability.UniversalObservabilityPlugin()), "observability" },

        // === Intelligence / Transform Plugins ===
        new object[] { "UltimateIntelligence", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateIntelligence.UltimateIntelligencePlugin()), "intelligence" },

        // === Transcoding Plugins ===
        new object[] { "MediaTranscoding", new Func<PluginBase>(() => new DataWarehouse.Plugins.Transcoding.Media.MediaTranscodingPlugin()), "transcoding" },

        // === Data Format / Virtualization ===
        new object[] { "SqlOverObject", new Func<PluginBase>(() => new DataWarehouse.Plugins.Virtualization.SqlOverObject.SqlOverObjectPlugin()), "sql" },

        // === Storage Fabric ===
        new object[] { "UniversalFabric", new Func<PluginBase>(() => new DataWarehouse.Plugins.UniversalFabric.UniversalFabricPlugin()), "fabric" },

        // === AirGap ===
        new object[] { "AirGapBridge", new Func<PluginBase>(() => new DataWarehouse.Plugins.AirGapBridge.AirGapBridgePlugin()), "airgap" },

        // === Plugins requiring DI (ILogger) - use Moq ===
        new object[] { "UltimateBlockchain", new Func<PluginBase>(() => new DataWarehouse.Plugins.UltimateBlockchain.UltimateBlockchainPlugin(new Mock<ILogger<DataWarehouse.Plugins.UltimateBlockchain.UltimateBlockchainPlugin>>().Object)), "blockchain" },
        new object[] { "AedsCore", new Func<PluginBase>(() => new DataWarehouse.Plugins.AedsCore.AedsCorePlugin(new Mock<ILogger<DataWarehouse.Plugins.AedsCore.AedsCorePlugin>>().Object)), "aeds" },
        new object[] { "IntentManifestSigner", new Func<PluginBase>(() => new DataWarehouse.Plugins.AedsCore.IntentManifestSignerPlugin(new Mock<ILogger<DataWarehouse.Plugins.AedsCore.IntentManifestSignerPlugin>>().Object)), "intent" },
    };

    // Note: TamperProofPlugin requires many DI dependencies and is tested separately in its own test class.
    // WinFspDriverPlugin targets net10.0-windows only and is tested via reflection in WinFspDriverTests.cs.

    [Theory]
    [MemberData(nameof(AllPluginFactories))]
    public void Plugin_HasNonEmptyId(string name, Func<PluginBase> factory, string idHint)
    {
        var plugin = factory();
        if (plugin is IDisposable d) _disposables.Add(d);

        plugin.Id.Should().NotBeNullOrWhiteSpace($"Plugin {name} must have a non-empty Id");
        _ = idHint; // used by other test methods
    }

    [Theory]
    [MemberData(nameof(AllPluginFactories))]
    public void Plugin_HasNonEmptyName(string name, Func<PluginBase> factory, string idHint)
    {
        var plugin = factory();
        if (plugin is IDisposable d) _disposables.Add(d);

        plugin.Name.Should().NotBeNullOrWhiteSpace($"Plugin {name} must have a non-empty Name");
        _ = idHint;
    }

    [Theory]
    [MemberData(nameof(AllPluginFactories))]
    public void Plugin_HasValidVersion(string name, Func<PluginBase> factory, string idHint)
    {
        var plugin = factory();
        if (plugin is IDisposable d) _disposables.Add(d);

        plugin.Version.Should().NotBeNullOrWhiteSpace($"Plugin {name} must have a Version");

        // Version should be parseable (at least major.minor.patch)
        var versionPart = plugin.Version.Split('-')[0].Split('+')[0];
        if (versionPart.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            versionPart = versionPart[1..];

        var parts = versionPart.Split('.');
        parts.Length.Should().BeGreaterThanOrEqualTo(2, $"Plugin {name} version '{plugin.Version}' should have at least major.minor");
        int.TryParse(parts[0], out _).Should().BeTrue($"Plugin {name} major version should be numeric");
        int.TryParse(parts[1], out _).Should().BeTrue($"Plugin {name} minor version should be numeric");
        _ = idHint;
    }

    [Theory]
    [MemberData(nameof(AllPluginFactories))]
    public void Plugin_HasValidCategory(string name, Func<PluginBase> factory, string idHint)
    {
        var plugin = factory();
        if (plugin is IDisposable d) _disposables.Add(d);

        Enum.IsDefined(typeof(PluginCategory), plugin.Category).Should().BeTrue(
            $"Plugin {name} category {plugin.Category} should be a defined PluginCategory value");
        _ = idHint;
    }

    [Theory]
    [MemberData(nameof(AllPluginFactories))]
    public async Task Plugin_DefaultHealthIsHealthy(string name, Func<PluginBase> factory, string idHint)
    {
        var plugin = factory();
        if (plugin is IDisposable d) _disposables.Add(d);

        var health = await plugin.CheckHealthAsync();

        health.Should().NotBeNull($"Plugin {name} health check should return a result");
        health.Status.Should().Be(HealthStatus.Healthy,
            $"Plugin {name} should report Healthy status by default, but got {health.Status}: {health.Message}");
        _ = idHint;
    }

    [Theory]
    [MemberData(nameof(AllPluginFactories))]
    public void Plugin_IdContainsExpectedHint(string name, Func<PluginBase> factory, string idHint)
    {
        var plugin = factory();
        if (plugin is IDisposable d) _disposables.Add(d);

        // The plugin ID should contain some form of the expected hint
        // This catches plugins that accidentally return wrong IDs
        var idLower = plugin.Id.ToLowerInvariant();
        var nameLower = plugin.Name.ToLowerInvariant();
        var combined = idLower + "|" + nameLower;

        combined.Should().Contain(idHint.ToLowerInvariant(),
            $"Plugin {name} Id '{plugin.Id}' or Name '{plugin.Name}' should relate to '{idHint}'");
    }
}
