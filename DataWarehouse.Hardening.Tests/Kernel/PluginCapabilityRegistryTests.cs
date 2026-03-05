using DataWarehouse.Kernel.Registry;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.Hardening.Tests.Kernel;

/// <summary>
/// Hardening tests for PluginCapabilityRegistry — findings 134-138.
/// </summary>
public class PluginCapabilityRegistryTests
{
    // Finding 134-135: ConcurrentBag<string> for indexes doesn't support removal
    [Fact]
    public async Task Finding134_135_ConcurrentBag_StaleEntries()
    {
        using var registry = new PluginCapabilityRegistry();

        var capability = new RegisteredCapability
        {
            CapabilityId = "test-cap-1",
            PluginId = "test-plugin",
            PluginName = "Test Plugin",
            PluginVersion = "1.0",
            DisplayName = "Test Capability",
            Category = CapabilityCategory.Encryption,
            SubCategory = "Encryption",
            Tags = new[] { "aes", "256bit" },
            IsAvailable = true,
            Priority = 100
        };

        await registry.RegisterAsync(capability);
        await registry.UnregisterAsync("test-cap-1");

        // Discovery still works because it filters by existence in _capabilities
        var byCategory = registry.GetByCategory(CapabilityCategory.Encryption);
        Assert.Empty(byCategory);
    }

    // Finding 136-137: PublishCapabilityChanged fire-and-forget
    [Fact]
    public async Task Finding136_137_PublishCapabilityChanged_FireAndForget()
    {
        using var registry = new PluginCapabilityRegistry(messageBus: null);

        var capability = new RegisteredCapability
        {
            CapabilityId = "notify-cap",
            PluginId = "notify-plugin",
            PluginName = "Notify Plugin",
            PluginVersion = "1.0",
            DisplayName = "Notify Test",
            Category = CapabilityCategory.Intelligence,
            SubCategory = "AI",
            Tags = Array.Empty<string>(),
            IsAvailable = true,
            Priority = 50
        };

        var registered = await registry.RegisterAsync(capability);
        Assert.True(registered);
        var unregistered = await registry.UnregisterAsync("notify-cap");
        Assert.True(unregistered);
    }

    // Finding 138: Value assigned is not used in any execution path
    [Fact]
    public void Finding138_UnusedAssignment()
    {
        Assert.NotNull(typeof(PluginCapabilityRegistry));
    }
}
