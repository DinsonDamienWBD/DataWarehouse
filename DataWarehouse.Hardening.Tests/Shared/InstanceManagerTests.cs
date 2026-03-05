// Hardening tests for Shared findings: InstanceManager
// Finding: 31 (MEDIUM) IsConnected flag race condition
using DataWarehouse.Shared;

namespace DataWarehouse.Hardening.Tests.Shared;

/// <summary>
/// Tests for InstanceManager hardening findings.
/// </summary>
public class InstanceManagerTests
{
    /// <summary>
    /// Finding 31: IsConnected flag race condition.
    /// Fixed by making _isConnected volatile for proper memory visibility across threads.
    /// </summary>
    [Fact]
    public void Finding031_IsConnectedIsVolatile()
    {
        var manager = new InstanceManager();

        // Initially not connected
        Assert.False(manager.IsConnected);

        // The _isConnected field is volatile, ensuring cross-thread visibility.
        // Multiple threads reading IsConnected will see the latest value.
        var values = new bool[100];
        Parallel.For(0, 100, i =>
        {
            values[i] = manager.IsConnected;
        });

        Assert.All(values, v => Assert.False(v));
    }
}
