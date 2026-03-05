// Hardening tests for Shared findings: NlpMessageBusRouter
// Finding: 45 (LOW) Non-accessed field _instanceManager
using DataWarehouse.Shared;
using DataWarehouse.Shared.Services;

namespace DataWarehouse.Hardening.Tests.Shared;

/// <summary>
/// Tests for NlpMessageBusRouter hardening findings.
/// </summary>
public class NlpMessageBusRouterTests
{
    /// <summary>
    /// Finding 45: _instanceManager field was assigned but never read.
    /// Fixed by converting to internal property InstanceManager.
    /// </summary>
    [Fact]
    public void Finding045_InstanceManagerExposedAsProperty()
    {
        var bridge = new MessageBridge();
        var manager = new InstanceManager();
        var router = new NlpMessageBusRouter(bridge, manager);

        // InstanceManager is now an accessible internal property
        Assert.NotNull(router.InstanceManager);
        Assert.Same(manager, router.InstanceManager);
    }

    [Fact]
    public void Finding045_InstanceManagerNullByDefault()
    {
        var bridge = new MessageBridge();
        var router = new NlpMessageBusRouter(bridge);

        Assert.Null(router.InstanceManager);
    }
}
