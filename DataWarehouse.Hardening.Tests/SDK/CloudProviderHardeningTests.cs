using System.Reflection;

namespace DataWarehouse.Hardening.Tests.SDK;

/// <summary>
/// Hardening tests for cloud provider findings 55-56, 59.
/// Status: IMPROVED - providers now throw PlatformNotSupportedException.
/// </summary>
public class CloudProviderHardeningTests
{
    private static readonly Assembly SdkAssembly = typeof(DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex.ArtNode).Assembly;

    [Theory]
    [InlineData("AwsProvider")]
    [InlineData("AzureProvider")]
    [InlineData("GcpProvider")]
    public void Finding55_56_59_CloudProvidersExist(string typeName)
    {
        var type = SdkAssembly.GetTypes().FirstOrDefault(t => t.Name == typeName);
        Assert.NotNull(type);
    }

    [Theory]
    [InlineData("AwsProvider", "ProvisionVmAsync")]
    [InlineData("AzureProvider", "ProvisionVmAsync")]
    [InlineData("GcpProvider", "ProvisionVmAsync")]
    public void Finding55_56_59_CloudProvidersHaveExpectedMethods(string typeName, string methodName)
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == typeName);
        var methods = type.GetMethods().Select(m => m.Name).ToArray();
        Assert.Contains(methodName, methods);
    }
}
