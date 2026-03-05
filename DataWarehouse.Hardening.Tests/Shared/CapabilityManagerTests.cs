// Hardening tests for Shared findings: CapabilityManager
// Finding: 6 (LOW) Non-accessed field
using DataWarehouse.Shared;

namespace DataWarehouse.Hardening.Tests.Shared;

/// <summary>
/// Tests for CapabilityManager hardening findings.
/// </summary>
public class CapabilityManagerTests
{
    /// <summary>
    /// Finding 6: _dynamicRegistry field was assigned but never used.
    /// Fixed by converting to internal property DynamicRegistry.
    /// </summary>
    [Fact]
    public void Finding006_DynamicRegistryIsExposedAsProperty()
    {
        var manager = new CapabilityManager();
        // DynamicRegistry should be null initially (internal property)
        Assert.Null(manager.DynamicRegistry);

        var registry = new DynamicCommandRegistry();
        manager.SetDynamicRegistry(registry);
        Assert.NotNull(manager.DynamicRegistry);
    }
}
