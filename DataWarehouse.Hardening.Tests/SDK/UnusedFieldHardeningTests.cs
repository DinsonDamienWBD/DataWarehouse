using System.Reflection;

namespace DataWarehouse.Hardening.Tests.SDK;

/// <summary>
/// Hardening tests for unused field findings.
/// Covers findings 9-11, 26-28, 32, 52, 54, 58, 61, 181, 202, 203.
/// These fields were either exposed as public properties or removed.
/// </summary>
public class UnusedFieldHardeningTests
{
    private static readonly Assembly SdkAssembly = typeof(DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex.ArtNode).Assembly;

    // Finding 9-11: AdaptiveIndexEngine unused private fields -> public properties
    [Theory]
    [InlineData("AdaptiveIndexEngine", "Device")]
    [InlineData("AdaptiveIndexEngine", "Allocator")]
    [InlineData("AdaptiveIndexEngine", "BlockSize")]
    public void Finding9_11_FieldsExposedAsProperties(string typeName, string propName)
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == typeName);
        var prop = type.GetProperty(propName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(prop);
    }

    // Finding 26-27: AlignedMemoryOwner unused private fields -> public properties
    [Theory]
    [InlineData("AlignedMemoryOwner", "ByteCount")]
    [InlineData("AlignedMemoryOwner", "Alignment")]
    public void Finding26_27_FieldsExposedAsProperties(string typeName, string propName)
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == typeName);
        var prop = type.GetProperty(propName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(prop);
    }

    // Finding 28: AllocationGroupDescriptorTable._blockSize -> BlockSize property
    [Fact]
    public void Finding28_AllocationGroupDescriptorTableBlockSizeExposed()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "AllocationGroupDescriptorTable");
        var prop = type.GetProperty("BlockSize",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(prop);
    }

    // Finding 32: ArcCacheL3NVMe._maxCacheBytes -> MaxCacheBytes property
    [Fact]
    public void Finding32_ArcCacheL3NVMeMaxCacheBytesExposed()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "ArcCacheL3NVMe");
        var prop = type.GetProperty("MaxCacheBytes",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(prop);
    }

    // Finding 52: AutonomousRebalancer._crushAlgorithm -> CrushAlgorithm property
    [Fact]
    public void Finding52_AutonomousRebalancerCrushAlgorithmExposed()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "AutonomousRebalancer");
        var prop = type.GetProperty("CrushAlgorithm",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(prop);
    }

    // Finding 54, 58: Cloud provider Logger properties exposed
    [Theory]
    [InlineData("AwsProvider")]
    [InlineData("AzureProvider")]
    public void Finding54_58_CloudProviderLoggerExposed(string typeName)
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == typeName);
        var prop = type.GetProperty("Logger",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(prop);
    }

    // Finding 61: BalloonDriver._hypervisorDetector -> HypervisorDetector property
    [Fact]
    public void Finding61_BalloonDriverHypervisorDetectorExposed()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "BalloonDriver");
        var prop = type.GetProperty("HypervisorDetector",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(prop);
    }

    // Finding 181: CannInterop._registry field removed
    [Fact]
    public void Finding181_CannInteropRegistryFieldRemoved()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "CannInterop");
        var field = type.GetField("_registry", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.Null(field); // Field should be removed
    }

    // Finding 202: ChecksumTable._checksumTableBlockCount -> property
    [Fact]
    public void Finding202_ChecksumTableBlockCountExposed()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "ChecksumTable");
        var prop = type.GetProperty("ChecksumTableBlockCount",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(prop);
    }

    // Finding 203: CloudKmsProvider._wrappedDekCache removed (write-only collection)
    [Theory]
    [InlineData("GcpKmsProvider")]
    [InlineData("AwsKmsProvider")]
    public void Finding203_KmsProviderWrappedDekCacheRemoved(string typeName)
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == typeName);
        var field = type.GetField("_wrappedDekCache", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.Null(field); // Write-only collection should be removed
    }
}
