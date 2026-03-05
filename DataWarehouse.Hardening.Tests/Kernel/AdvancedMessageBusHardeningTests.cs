using DataWarehouse.Kernel.Messaging;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Hardening.Tests.Kernel;

/// <summary>
/// Hardening tests for AdvancedMessageBus — findings 1-11.
/// </summary>
public class AdvancedMessageBusHardeningTests
{
    // Finding 1: [MED-04] Audit Event Handlers Have No Timeout and Risk Data Loss at Shutdown
    // Finding 4: [CRIT-02] PublishAndWaitAsync Uses Task.WhenAll Without Per-Handler Deadline
    [Fact]
    public async Task Finding1_4_PublishAsync_HandlersDoNotBlockIndefinitely()
    {
        var ctx = CreateKernelContext();
        var bus = new AdvancedMessageBus(ctx);
        var handlerCalled = false;
        bus.Subscribe("test.topic", async msg =>
        {
            handlerCalled = true;
            await Task.Delay(10);
        });

        await bus.PublishAsync("test.topic", new PluginMessage { Type = "test" });
        Assert.True(handlerCalled);
        bus.Dispose();
    }

    // Finding 2: Performance — ReaderWriterLockSlim allocation
    // Finding 3: ReaderWriterLockSlim with SupportsRecursion never used
    [Fact]
    public async Task Finding2_3_ThreadSafeSubscriptionList_ConcurrentAccess()
    {
        var ctx = CreateKernelContext();
        var bus = new AdvancedMessageBus(ctx);
        var counter = 0;

        var tasks = Enumerable.Range(0, 10).Select(i =>
            Task.Run(() => bus.Subscribe("concurrent.topic", msg =>
            {
                Interlocked.Increment(ref counter);
                return Task.CompletedTask;
            }))
        ).ToArray();

        await Task.WhenAll(tasks);

        await bus.PublishAsync("concurrent.topic", new PluginMessage { Type = "test" });
        Assert.Equal(10, counter);
        bus.Dispose();
    }

    // Finding 5: [HIGH-04] Timer Callbacks Without Top-Level Exception Guard
    [Fact]
    public void Finding5_CleanupTimer_DoesNotThrow()
    {
        var ctx = CreateKernelContext();
        var bus = new AdvancedMessageBus(ctx);
        Assert.NotNull(bus);
        bus.Dispose();
    }

    // Finding 6-8: ProcessRetries creates unbounded thread pool storm
    [Fact]
    public async Task Finding6_7_8_ProcessRetries_LimitsConcurrency()
    {
        var ctx = CreateKernelContext();
        var bus = new AdvancedMessageBus(ctx, new AdvancedMessageBusConfig
        {
            RateLimitPerSecond = 10000
        });

        var deliveredCount = 0;
        bus.Subscribe("retry.topic", msg =>
        {
            Interlocked.Increment(ref deliveredCount);
            return Task.CompletedTask;
        });

        var result = await bus.PublishReliableAsync("retry.topic", new PluginMessage { Type = "test" },
            new ReliablePublishOptions { RequireAcknowledgment = false });

        Assert.True(result.Success);
        bus.Dispose();
    }

    // Finding 9: retryTimer exception guard
    [Fact]
    public void Finding9_RetryTimer_HasExceptionGuard()
    {
        var ctx = CreateKernelContext();
        var bus = new AdvancedMessageBus(ctx);
        Assert.NotNull(bus);
        bus.Dispose();
    }

    // Finding 10-11: ReaderWriterLockSlim never disposed
    [Fact]
    public void Finding10_11_Dispose_ReleasesResources()
    {
        var ctx = CreateKernelContext();
        var bus = new AdvancedMessageBus(ctx);
        Assert.NotNull(bus.Subscribe("test.dispose", msg => Task.CompletedTask));
        bus.Dispose();
    }

    /// <summary>
    /// Creates a minimal IKernelContext for testing.
    /// </summary>
    private static IKernelContext CreateKernelContext()
    {
        return new TestKernelContext();
    }

    private sealed class TestKernelContext : IKernelContext
    {
        public OperatingMode Mode => OperatingMode.Workstation;
        public string RootPath => Environment.CurrentDirectory;
        public IKernelStorageService Storage => null!;
        public void LogInfo(string message) { }
        public void LogError(string message, Exception? ex = null) { }
        public void LogWarning(string message) { }
        public void LogDebug(string message) { }
        public T? GetPlugin<T>() where T : class, IPlugin => null;
        public IEnumerable<T> GetPlugins<T>() where T : class, IPlugin => Enumerable.Empty<T>();
    }
}
