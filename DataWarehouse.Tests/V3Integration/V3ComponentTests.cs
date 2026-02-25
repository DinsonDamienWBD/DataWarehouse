using DataWarehouse.SDK.Deployment;
using DataWarehouse.SDK.Edge.Memory;
using DataWarehouse.SDK.Edge.Protocols;
using DataWarehouse.SDK.Federation.Addressing;
using DataWarehouse.SDK.Federation.Routing;
using DataWarehouse.SDK.Hardware;
using DataWarehouse.SDK.Storage;
using DataWarehouse.SDK.VirtualDiskEngine;
using DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;
using DataWarehouse.SDK.VirtualDiskEngine.Container;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.V3Integration;

/// <summary>
/// Integration tests for v3.0 SDK components.
/// Verifies that new v3.0 types compile, instantiate, and interact correctly.
/// </summary>
[Trait("Category", "Unit")]
public class V3ComponentTests
{
    #region StorageAddress Tests

    [Fact]
    public void StorageAddress_FilePath_ImplicitConversionFromString()
    {
        // Arrange
        string path = @"C:\data\test.dat";

        // Act
        StorageAddress address = path;

        // Assert
        address.Should().NotBeNull();
        address.Kind.Should().Be(StorageAddressKind.FilePath);
        address.Should().BeOfType<FilePathAddress>();
        ((FilePathAddress)address).Path.Should().Be(path);
    }

    [Fact]
    public void StorageAddress_ObjectKey_CreatedFromBucketKey()
    {
        // Arrange
        string key = "my-bucket/my-object-key";

        // Act
        StorageAddress address = StorageAddress.FromObjectKey(key);

        // Assert
        address.Should().NotBeNull();
        address.Kind.Should().Be(StorageAddressKind.ObjectKey);
        address.Should().BeOfType<ObjectKeyAddress>();
        ((ObjectKeyAddress)address).Key.Should().Be(key);
    }

    [Fact]
    public void StorageAddress_NetworkEndpoint_CreatedFromUri()
    {
        // Arrange
        var uri = new Uri("https://storage.example.com:443");

        // Act
        StorageAddress address = StorageAddress.FromUri(uri);

        // Assert
        address.Should().NotBeNull();
        address.Kind.Should().Be(StorageAddressKind.NetworkEndpoint);
        address.Should().BeOfType<NetworkEndpointAddress>();
        var endpoint = (NetworkEndpointAddress)address;
        endpoint.Host.Should().Be("storage.example.com");
        endpoint.Port.Should().Be(443);
        endpoint.Scheme.Should().Be("https");
    }

    [Fact]
    public void StorageAddressKind_HasExpectedValues()
    {
        // Act
        var values = Enum.GetValues<StorageAddressKind>();

        // Assert — original 9 values plus 3 DW-native address kinds added in v5.0
        values.Should().HaveCountGreaterThanOrEqualTo(9);
        values.Should().Contain(StorageAddressKind.FilePath);
        values.Should().Contain(StorageAddressKind.ObjectKey);
        values.Should().Contain(StorageAddressKind.NetworkEndpoint);
        values.Should().Contain(StorageAddressKind.BlockDevice);
        values.Should().Contain(StorageAddressKind.NvmeNamespace);
        values.Should().Contain(StorageAddressKind.GpioPin);
        values.Should().Contain(StorageAddressKind.I2cBus);
        values.Should().Contain(StorageAddressKind.SpiBus);
        values.Should().Contain(StorageAddressKind.CustomAddress);
    }

    [Fact]
    public void StorageAddress_AbstractRecord_HasKindProperty()
    {
        // Arrange
        StorageAddress address = StorageAddress.FromFilePath(@"C:\test.dat");

        // Assert
        address.Kind.Should().Be(StorageAddressKind.FilePath);
    }

    #endregion

    #region VDE Tests

    [Fact]
    public void VdeConstants_MagicBytesEquals0x44575644()
    {
        // Assert
        VdeConstants.MagicBytes.Should().Be(0x44575644u);
    }

    [Fact]
    public void VdeOptions_CanBeConstructedWithDefaults()
    {
        // Act
        var options = new VdeOptions
        {
            ContainerPath = @"C:\test\container.dwvd"
        };

        // Assert
        options.Should().NotBeNull();
        options.ContainerPath.Should().Be(@"C:\test\container.dwvd");
        options.BlockSize.Should().Be(4096);
        options.TotalBlocks.Should().Be(1_048_576);
        options.WalSizePercent.Should().Be(1);
        options.EnableChecksumVerification.Should().BeTrue();
    }

    [Fact]
    public void VdeHealthReport_CanBeInstantiated()
    {
        // Act
        var report = new VdeHealthReport
        {
            TotalBlocks = 10000,
            FreeBlocks = 8000,
            UsedBlocks = 2000,
            TotalInodes = 1024,
            AllocatedInodes = 100,
            WalUtilizationPercent = 15.5,
            ChecksumErrorCount = 0,
            SnapshotCount = 2,
            HealthStatus = "Healthy",
            GeneratedAtUtc = DateTimeOffset.UtcNow
        };

        // Assert
        report.Should().NotBeNull();
        report.TotalBlocks.Should().Be(10000);
        report.FreeBlocks.Should().Be(8000);
        report.UsedBlocks.Should().Be(2000);
        report.UsagePercent.Should().BeApproximately(20.0, 0.01);
        report.HealthStatus.Should().Be("Healthy");
    }

    [Fact]
    public void ContainerFormat_ComputeLayout_ReturnsValidLayout()
    {
        // Arrange
        int blockSize = 4096;
        long totalBlocks = 10000;

        // Act
        var layout = ContainerFormat.ComputeLayout(blockSize, totalBlocks);

        // Assert
        layout.Should().NotBeNull();
        layout.BlockSize.Should().Be(blockSize);
        layout.TotalBlocks.Should().Be(totalBlocks);
        layout.PrimarySuperblockBlock.Should().Be(0);
        layout.MirrorSuperblockBlock.Should().Be(1);
        layout.DataBlockCount.Should().BeGreaterThan(0);
        layout.DataStartBlock.Should().BeGreaterThan(layout.MirrorSuperblockBlock);
    }

    [Fact]
    public void Superblock_SerializeDeserialize_RoundTrips()
    {
        // Arrange - Don't set checksum, Serialize will compute it
        var original = new Superblock
        {
            Magic = VdeConstants.MagicBytes,
            FormatVersion = VdeConstants.FormatVersion,
            Flags = 0,
            BlockSize = 4096,
            TotalBlocks = 10000,
            FreeBlocks = 8000,
            InodeTableBlock = 10,
            BitmapStartBlock = 2,
            BitmapBlockCount = 5,
            BTreeRootBlock = 100,
            WalStartBlock = 200,
            WalBlockCount = 256,
            ChecksumTableBlock = 500,
            CreatedTimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ModifiedTimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            CheckpointSequence = 1,
            Checksum = 0 // Will be computed during Serialize
        };

        Span<byte> buffer = stackalloc byte[VdeConstants.SuperblockSize];

        // Act - Serialize (computes checksum)
        Superblock.Serialize(original, buffer);

        // Act - Deserialize
        bool success = Superblock.Deserialize(buffer, out var deserialized);

        // Assert
        success.Should().BeTrue("deserialization should succeed with valid checksum");
        deserialized.Magic.Should().Be(VdeConstants.MagicBytes);
        deserialized.FormatVersion.Should().Be(VdeConstants.FormatVersion);
        deserialized.BlockSize.Should().Be(4096);
        deserialized.TotalBlocks.Should().Be(10000);
        deserialized.FreeBlocks.Should().Be(8000);
        deserialized.Checksum.Should().NotBe(0, "checksum should be computed");
    }

    #endregion

    #region Federation Tests

    [Fact]
    public void UuidGenerator_ProducesValidV7Uuids()
    {
        // Arrange
        var generator = new UuidGenerator();

        // Act
        var id1 = generator.Generate();
        var id2 = generator.Generate();

        // Assert
        id1.Should().NotBe(ObjectIdentity.Empty, "generated UUID should not be empty");
        id2.Should().NotBe(ObjectIdentity.Empty, "generated UUID should not be empty");
        id1.Should().NotBe(id2, "each UUID should be unique");

        // Verify they are valid UUID v7
        generator.IsValid(id1).Should().BeTrue("id1 should be valid UUID v7");
        generator.IsValid(id2).Should().BeTrue("id2 should be valid UUID v7");

        // Verify timestamp is reasonable (within last minute)
        var now = DateTimeOffset.UtcNow;
        id1.Timestamp.Should().BeCloseTo(now, TimeSpan.FromMinutes(1));
        id2.Timestamp.Should().BeCloseTo(now, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void ObjectIdentity_CanBeCreated_HasExpectedProperties()
    {
        // Arrange
        var guid = Guid.NewGuid();

        // Act
        var identity = new ObjectIdentity(guid);

        // Assert
        identity.Value.Should().Be(guid);
        identity.IsEmpty.Should().BeFalse();
        identity.ToString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void StorageRequest_CanBeInstantiated()
    {
        // Arrange
        var address = StorageAddress.FromObjectKey("test-key");

        // Act
        var request = new StorageRequest
        {
            RequestId = Guid.NewGuid().ToString(),
            Address = address,
            Operation = StorageOperation.Read
        };

        // Assert
        request.Should().NotBeNull();
        request.RequestId.Should().NotBeNullOrEmpty();
        request.Address.Should().Be(address);
        request.Operation.Should().Be(StorageOperation.Read);
    }

    #endregion

    #region Hardware Tests

    [Fact]
    public void HardwareDeviceType_FlagsEnumHasExpectedValues()
    {
        // Assert
        HardwareDeviceType.None.Should().Be((HardwareDeviceType)0);
        HardwareDeviceType.PciDevice.Should().Be((HardwareDeviceType)1);
        HardwareDeviceType.UsbDevice.Should().Be((HardwareDeviceType)2);
        HardwareDeviceType.NvmeController.Should().Be((HardwareDeviceType)4);
        HardwareDeviceType.NvmeNamespace.Should().Be((HardwareDeviceType)8);
        HardwareDeviceType.GpuAccelerator.Should().Be((HardwareDeviceType)16);
        HardwareDeviceType.BlockDevice.Should().Be((HardwareDeviceType)32);
        HardwareDeviceType.NetworkAdapter.Should().Be((HardwareDeviceType)64);
        HardwareDeviceType.GpioController.Should().Be((HardwareDeviceType)128);
        HardwareDeviceType.I2cBus.Should().Be((HardwareDeviceType)256);
        HardwareDeviceType.SpiBus.Should().Be((HardwareDeviceType)512);
        HardwareDeviceType.TpmDevice.Should().Be((HardwareDeviceType)1024);
        HardwareDeviceType.HsmDevice.Should().Be((HardwareDeviceType)2048);

        // Test flag combination
        var combined = HardwareDeviceType.NvmeController | HardwareDeviceType.BlockDevice;
        combined.HasFlag(HardwareDeviceType.NvmeController).Should().BeTrue();
        combined.HasFlag(HardwareDeviceType.BlockDevice).Should().BeTrue();
    }

    [Fact]
    public void HardwareDevice_CanBeConstructed()
    {
        // Act
        var device = new HardwareDevice
        {
            DeviceId = "PCI\\VEN_8086&DEV_0A54",
            Name = "Intel NVMe Controller",
            Type = HardwareDeviceType.NvmeController | HardwareDeviceType.BlockDevice,
            Vendor = "Intel"
        };

        // Assert
        device.Should().NotBeNull();
        device.DeviceId.Should().Be("PCI\\VEN_8086&DEV_0A54");
        device.Name.Should().Be("Intel NVMe Controller");
        device.Type.HasFlag(HardwareDeviceType.NvmeController).Should().BeTrue();
        device.Type.HasFlag(HardwareDeviceType.BlockDevice).Should().BeTrue();
        device.Vendor.Should().Be("Intel");
    }

    [Fact]
    public void NullHardwareProbe_ReturnsEmptyDiscoveryResults()
    {
        // Arrange
        var probe = new NullHardwareProbe();

        // Act
        var devices = probe.DiscoverAsync().Result;

        // Assert
        devices.Should().NotBeNull();
        devices.Should().BeEmpty();
    }

    [Fact]
    public void PlatformCapabilityRegistry_CanBeInstantiated()
    {
        // Arrange
        var probe = new NullHardwareProbe();

        // Act
        var registry = new PlatformCapabilityRegistry(probe);

        // Assert
        registry.Should().NotBeNull();

        // Cleanup
        registry.Dispose();
    }

    #endregion

    #region Edge/IoT Tests

    [Fact]
    public void MqttConnectionSettings_CanBeConstructed()
    {
        // Act
        var settings = new MqttConnectionSettings
        {
            BrokerAddress = "mqtt.example.com",
            Port = 8883,
            UseTls = true
        };

        // Assert
        settings.Should().NotBeNull();
        settings.BrokerAddress.Should().Be("mqtt.example.com");
        settings.Port.Should().Be(8883);
        settings.UseTls.Should().BeTrue();
        settings.KeepAlive.Should().Be(TimeSpan.FromSeconds(60));
        settings.CleanSession.Should().BeTrue();
        settings.AutoReconnect.Should().BeTrue();
    }

    [Fact]
    public void MemorySettings_CanBeConfiguredWithBudget()
    {
        // Act
        var settings = new MemorySettings
        {
            MemoryCeiling = 256 * 1024 * 1024, // 256MB
            Enabled = true,
            GcPressureThreshold = 0.85
        };

        // Assert
        settings.Should().NotBeNull();
        settings.MemoryCeiling.Should().Be(256 * 1024 * 1024);
        settings.Enabled.Should().BeTrue();
        settings.GcPressureThreshold.Should().BeApproximately(0.85, 0.001);
        settings.ArrayPoolMaxArraySize.Should().Be(1024 * 1024);
    }

    #endregion

    #region Deployment Tests

    [Fact]
    public void DeploymentEnvironment_EnumHasExpectedValues()
    {
        // Assert
        DeploymentEnvironment.Unknown.Should().Be((DeploymentEnvironment)0);
        DeploymentEnvironment.HostedVm.Should().Be((DeploymentEnvironment)1);
        DeploymentEnvironment.Hypervisor.Should().Be((DeploymentEnvironment)2);
        DeploymentEnvironment.BareMetalSpdk.Should().Be((DeploymentEnvironment)3);
        DeploymentEnvironment.BareMetalLegacy.Should().Be((DeploymentEnvironment)4);
        DeploymentEnvironment.HyperscaleCloud.Should().Be((DeploymentEnvironment)5);
        DeploymentEnvironment.EdgeDevice.Should().Be((DeploymentEnvironment)6);
    }

    [Fact]
    public void DeploymentProfile_CanBeConstructed()
    {
        // Act
        var profile = new DeploymentProfile
        {
            Name = "edge-raspberry-pi",
            Environment = DeploymentEnvironment.EdgeDevice,
            MaxMemoryBytes = 256 * 1024 * 1024,
            AllowedPlugins = new[] { "UltimateStorage", "TamperProof" }
        };

        // Assert
        profile.Should().NotBeNull();
        profile.Name.Should().Be("edge-raspberry-pi");
        profile.Environment.Should().Be(DeploymentEnvironment.EdgeDevice);
        profile.MaxMemoryBytes.Should().Be(256 * 1024 * 1024);
        profile.AllowedPlugins.Should().NotBeNull();
        profile.AllowedPlugins.Should().Contain("UltimateStorage");
        profile.AllowedPlugins.Should().Contain("TamperProof");
    }

    #endregion

    #region Block Allocation Tests

    [Fact]
    public void BitmapAllocator_AllocateAndFree_NoLeaks()
    {
        // Arrange
        var allocator = new BitmapAllocator(totalBlocks: 1000);

        // Act - Allocate 10 blocks
        var allocated = new List<long>();
        for (int i = 0; i < 10; i++)
        {
            allocated.Add(allocator.AllocateBlock());
        }

        long freeAfterAlloc = allocator.FreeBlockCount;

        // Free all allocated blocks
        foreach (var block in allocated)
        {
            allocator.FreeBlock(block);
        }

        long freeAfterFree = allocator.FreeBlockCount;

        // Assert
        allocated.Should().HaveCount(10);
        allocated.Should().OnlyHaveUniqueItems();
        freeAfterAlloc.Should().Be(1000 - 10);
        freeAfterFree.Should().Be(1000); // All blocks freed
    }

    [Fact]
    public void ExtentTree_MergesAdjacentFreeRegions()
    {
        // Arrange
        var tree = new ExtentTree();

        // Act - Add two adjacent extents
        tree.AddFreeExtent(start: 0, count: 10);
        tree.AddFreeExtent(start: 10, count: 10);

        int countAfterMerge = tree.ExtentCount;

        // Assert - Should merge into single extent
        countAfterMerge.Should().Be(1, "adjacent extents should merge");

        // Find the merged extent
        var extent = tree.FindExtent(minBlocks: 1);
        extent.Should().NotBeNull();
        extent!.StartBlock.Should().Be(0);
        extent.BlockCount.Should().Be(20, "two 10-block extents should merge into 20");
    }

    #endregion
}
