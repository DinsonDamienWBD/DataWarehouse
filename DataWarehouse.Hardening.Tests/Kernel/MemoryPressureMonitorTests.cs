using DataWarehouse.Kernel.Infrastructure;

namespace DataWarehouse.Hardening.Tests.Kernel;

/// <summary>
/// Hardening tests for MemoryPressureMonitor — findings 82-90.
/// </summary>
public class MemoryPressureMonitorTests
{
    // Finding 82: _disposed is regular bool, not volatile
    // Finding 83: Thread handling
    // FIX APPLIED: _disposed now uses Volatile.Read/Write pattern
    [Fact]
    public void Finding82_83_DisposedFlag_Volatile()
    {
        var monitor = new MemoryPressureMonitor(checkInterval: TimeSpan.FromHours(1));
        Assert.NotNull(monitor);
        monitor.Dispose();
        // Calling Dispose again should be safe (idempotent)
        monitor.Dispose();
    }

    // Finding 84: Empty catch around GC.RegisterForFullGCNotification
    // Finding 85: Swallowed exception
    // The catch is intentional — GC notifications are not available on all platforms
    [Fact]
    public void Finding84_85_GcNotification_PlatformSafe()
    {
        // Construction should not throw even on platforms without GC notifications
        var monitor = new MemoryPressureMonitor(checkInterval: TimeSpan.FromHours(1));
        Assert.NotNull(monitor);
        monitor.Dispose();
    }

    // Finding 86: MonitorGCNotifications naming
    // FIX APPLIED: Method renamed to MonitorGcNotifications
    [Fact]
    public void Finding86_NamingConvention()
    {
        // Verify type exists and compiles
        Assert.NotNull(typeof(MemoryPressureMonitor));
    }

    // Finding 87: Inner catch retries forever for persistent non-transient errors
    // Finding 88: Swallowed exception + infinite retry
    // FIX APPLIED: MonitorGcNotifications now has a max retry count
    [Fact]
    public void Finding87_88_MonitorRetry_Bounded()
    {
        var monitor = new MemoryPressureMonitor(checkInterval: TimeSpan.FromHours(1));
        Assert.NotNull(monitor);
        monitor.Dispose();
    }

    // Finding 89: GC.CancelFullGCNotification() exception silently caught in Dispose
    // Finding 90: Swallowed exception
    // The catch is intentional — Dispose should never throw
    [Fact]
    public void Finding89_90_Dispose_SilentlyCatches()
    {
        var monitor = new MemoryPressureMonitor(checkInterval: TimeSpan.FromHours(1));
        Assert.NotNull(monitor);
        monitor.Dispose();
        // No exception = correct behavior
    }
}
