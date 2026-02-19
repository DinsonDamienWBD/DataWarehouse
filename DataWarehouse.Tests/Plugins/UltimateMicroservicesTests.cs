using Xunit;
using DataWarehouse.Plugins.UltimateMicroservices;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Tests.Plugins;

[Trait("Category", "Unit")]
public class UltimateMicroservicesTests
{
    [Fact]
    public void Plugin_HasCorrectIdentity()
    {
        using var plugin = new UltimateMicroservicesPlugin();

        Assert.Equal("com.datawarehouse.microservices.ultimate", plugin.Id);
        Assert.Equal("Ultimate Microservices", plugin.Name);
        Assert.Equal("1.0.0", plugin.Version);
        Assert.Equal(PluginCategory.InfrastructureProvider, plugin.Category);
    }

    [Fact]
    public void Plugin_AutoDiscoversStrategies()
    {
        using var plugin = new UltimateMicroservicesPlugin();

        var byCategory = plugin.StrategiesByCategory;
        Assert.NotEmpty(byCategory);
    }

    [Fact]
    public void RegisterService_CreatesServiceInstance()
    {
        using var plugin = new UltimateMicroservicesPlugin();

        var config = new MicroserviceConfig
        {
            ServiceId = "svc-001",
            ServiceName = "OrderService",
            Endpoint = "https://orders.local:8080"
        };

        plugin.RegisterService(config);

        var service = plugin.GetService("svc-001");
        Assert.NotNull(service);
        Assert.Equal("OrderService", service.ServiceName);
        Assert.Equal("https://orders.local:8080", service.Endpoint);
        Assert.Equal(ServiceHealthStatus.Healthy, service.HealthStatus);
    }

    [Fact]
    public void ListServices_ReturnsRegisteredServices()
    {
        using var plugin = new UltimateMicroservicesPlugin();

        plugin.RegisterService(new MicroserviceConfig
        {
            ServiceId = "svc-a",
            ServiceName = "ServiceA",
            Endpoint = "https://a.local"
        });
        plugin.RegisterService(new MicroserviceConfig
        {
            ServiceId = "svc-b",
            ServiceName = "ServiceB",
            Endpoint = "https://b.local"
        });

        var services = plugin.ListServices();
        Assert.Equal(2, services.Count);
    }

    [Fact]
    public void GetStrategy_ReturnsNullForUnknown()
    {
        using var plugin = new UltimateMicroservicesPlugin();

        Assert.Null(plugin.GetStrategy("non-existent"));
    }
}
