using Xunit;
using DataWarehouse.Plugins.UltimateResilience;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Tests.Plugins;

[Trait("Category", "Unit")]
public class UltimateResilienceTests
{
    [Fact]
    public void Plugin_HasCorrectIdentity()
    {
        using var plugin = new UltimateResiliencePlugin();

        Assert.Equal("com.datawarehouse.resilience.ultimate", plugin.Id);
        Assert.Equal("Ultimate Resilience", plugin.Name);
        Assert.Equal("1.0.0", plugin.Version);
        Assert.Equal(PluginCategory.InfrastructureProvider, plugin.Category);
    }

    [Fact]
    public void Registry_AutoDiscoversStrategies()
    {
        using var plugin = new UltimateResiliencePlugin();

        var strategies = plugin.Registry.GetAll();
        Assert.NotEmpty(strategies);
        Assert.True(strategies.Count > 10, "Should discover many resilience strategies");
    }

    [Fact]
    public void Registry_HasMultipleCategories()
    {
        using var plugin = new UltimateResiliencePlugin();

        var circuitBreakers = plugin.Registry.GetByPredicate(s => s.Category.Equals("CircuitBreaker", StringComparison.OrdinalIgnoreCase));
        var retries = plugin.Registry.GetByPredicate(s => s.Category.Equals("Retry", StringComparison.OrdinalIgnoreCase));

        Assert.NotEmpty(circuitBreakers);
        Assert.NotEmpty(retries);
    }

    [Fact]
    public void GetStrategy_ReturnsNullForUnknown()
    {
        using var plugin = new UltimateResiliencePlugin();

        var strategy = plugin.Registry.Get("non-existent");
        Assert.Null(strategy);
    }

    [Fact]
    public async Task GetResilienceHealth_ReturnsHealthInfo()
    {
        using var plugin = new UltimateResiliencePlugin();

        var health = await plugin.GetResilienceHealthAsync(CancellationToken.None);

        Assert.NotNull(health);
        Assert.True(health.TotalPolicies > 0);
    }
}
