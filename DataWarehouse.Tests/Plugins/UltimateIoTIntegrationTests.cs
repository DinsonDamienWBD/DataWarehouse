using Xunit;
using DataWarehouse.Plugins.UltimateIoTIntegration;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Tests.Plugins;

[Trait("Category", "Unit")]
public class UltimateIoTIntegrationTests
{
    [Fact]
    public void Plugin_HasCorrectIdentity()
    {
        using var plugin = new UltimateIoTIntegrationPlugin();

        Assert.Equal("com.datawarehouse.iot.ultimate", plugin.Id);
        Assert.Equal("Ultimate IoT Integration", plugin.Name);
        Assert.Equal("1.0.0", plugin.Version);
        Assert.Equal(PluginCategory.FeatureProvider, plugin.Category);
    }

    [Fact]
    public void Registry_AutoDiscoversStrategies()
    {
        using var plugin = new UltimateIoTIntegrationPlugin();

        Assert.True(plugin.Registry.Count > 0, "Should auto-discover IoT strategies");
    }

    [Fact]
    public void SemanticTags_ContainExpectedProtocols()
    {
        using var plugin = new UltimateIoTIntegrationPlugin();

        Assert.Contains("mqtt", plugin.SemanticTags);
        Assert.Contains("coap", plugin.SemanticTags);
        Assert.Contains("opcua", plugin.SemanticTags);
        Assert.Contains("modbus", plugin.SemanticTags);
    }

    [Fact]
    public void SemanticDescription_MentionsKeyCapabilities()
    {
        using var plugin = new UltimateIoTIntegrationPlugin();

        Assert.Contains("device management", plugin.SemanticDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MQTT", plugin.SemanticDescription);
        Assert.Contains("provisioning", plugin.SemanticDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IoTTypes_DeviceStatusEnumValues()
    {
        Assert.Equal(0, (int)DeviceStatus.Unknown);
        Assert.True(Enum.IsDefined(typeof(DeviceStatus), DeviceStatus.Registered));
        Assert.True(Enum.IsDefined(typeof(DeviceStatus), DeviceStatus.Provisioned));
    }
}
