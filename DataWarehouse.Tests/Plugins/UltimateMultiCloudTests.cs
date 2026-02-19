using Xunit;
using DataWarehouse.Plugins.UltimateMultiCloud;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Tests.Plugins;

[Trait("Category", "Unit")]
public class UltimateMultiCloudTests
{
    [Fact]
    public void Plugin_HasCorrectIdentity()
    {
        using var plugin = new UltimateMultiCloudPlugin();

        Assert.Equal("com.datawarehouse.multicloud.ultimate", plugin.Id);
        Assert.Equal("Ultimate Multi-Cloud", plugin.Name);
        Assert.Equal("1.0.0", plugin.Version);
        Assert.Equal(PluginCategory.InfrastructureProvider, plugin.Category);
    }

    [Fact]
    public void Registry_AutoDiscoversStrategies()
    {
        using var plugin = new UltimateMultiCloudPlugin();

        var strategies = plugin.Registry.GetAllStrategies();
        Assert.NotEmpty(strategies);
    }

    [Fact]
    public void RegisterProvider_AddsToProviders()
    {
        using var plugin = new UltimateMultiCloudPlugin();

        plugin.RegisterProvider(new CloudProviderConfig
        {
            ProviderId = "aws-us",
            Name = "AWS US East",
            Type = CloudProviderType.AWS,
            Regions = new List<string> { "us-east-1", "us-west-2" },
            Priority = 10,
            CostMultiplier = 1.0
        });

        Assert.Single(plugin.Providers);
        Assert.True(plugin.Providers.ContainsKey("aws-us"));
        Assert.True(plugin.Providers["aws-us"].IsHealthy);
    }

    [Fact]
    public void GetOptimalProvider_SelectsBestMatch()
    {
        using var plugin = new UltimateMultiCloudPlugin();

        plugin.RegisterProvider(new CloudProviderConfig
        {
            ProviderId = "aws-1",
            Name = "AWS",
            Type = CloudProviderType.AWS,
            Regions = new List<string> { "us-east-1" },
            Priority = 10,
            CostMultiplier = 1.5
        });
        plugin.RegisterProvider(new CloudProviderConfig
        {
            ProviderId = "gcp-1",
            Name = "GCP",
            Type = CloudProviderType.GCP,
            Regions = new List<string> { "us-east-1" },
            Priority = 5,
            CostMultiplier = 1.0
        });

        var recommendation = plugin.GetOptimalProvider(new WorkloadRequirements
        {
            OptimizeForCost = true,
            RequiredRegions = new List<string> { "us-east-1" }
        });

        Assert.True(recommendation.Success);
        Assert.NotNull(recommendation.ProviderId);
        Assert.NotNull(recommendation.Reason);
    }

    [Fact]
    public void GetOptimalProvider_ReturnsFailureWhenNoProviders()
    {
        using var plugin = new UltimateMultiCloudPlugin();

        var recommendation = plugin.GetOptimalProvider(new WorkloadRequirements());

        Assert.False(recommendation.Success);
        Assert.Contains("No healthy providers", recommendation.Reason);
    }

    [Fact]
    public void CloudProviderType_HasExpectedValues()
    {
        Assert.True(Enum.IsDefined(typeof(CloudProviderType), CloudProviderType.AWS));
        Assert.True(Enum.IsDefined(typeof(CloudProviderType), CloudProviderType.Azure));
        Assert.True(Enum.IsDefined(typeof(CloudProviderType), CloudProviderType.GCP));
        Assert.True(Enum.IsDefined(typeof(CloudProviderType), CloudProviderType.OnPremise));
    }
}
