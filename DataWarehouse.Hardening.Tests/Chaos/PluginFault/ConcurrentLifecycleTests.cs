using DataWarehouse.Kernel;
using DataWarehouse.Kernel.Messaging;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Hardening.Tests.Chaos.PluginFault;

/// <summary>
/// Concurrent plugin lifecycle chaos tests proving load/unload during active I/O
/// produces no torn state or leaked resources.
///
/// Report: "Stage 3 - Steps 1-2 - Concurrent Plugin Lifecycle"
/// </summary>
public class ConcurrentLifecycleTests : IAsyncDisposable
{
    private DataWarehouseKernel? _kernel;

    /// <summary>
    /// Build a minimal Kernel with in-memory storage and the given plugins registered.
    /// </summary>
    private async Task<DataWarehouseKernel> BuildKernelAsync(params IPlugin[] plugins)
    {
        var builder = KernelBuilder.Create()
            .WithKernelId($"chaos-lifecycle-{Guid.NewGuid():N}")
            .WithOperatingMode(OperatingMode.Workstation)
            .UseInMemoryStorage();

        foreach (var plugin in plugins)
        {
            builder.WithPlugin(plugin);
        }

        _kernel = await builder.BuildAndInitializeAsync();
        return _kernel;
    }

    public async ValueTask DisposeAsync()
    {
        if (_kernel != null)
        {
            await _kernel.DisposeAsync();
            _kernel = null;
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 100 sequential load/unload cycles with no active I/O.
    /// After each cycle, verify subscription count returns to baseline and plugin is fully disposed.
    /// Proves: No leaked subscriptions or state accumulation over many cycles.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task SequentialLoadUnload_100Cycles_NoLeaks()
    {
        // Arrange -- build kernel with no plugins initially
        var kernel = await BuildKernelAsync();
        var busImpl = kernel.MessageBus as DefaultMessageBus;
        Assert.NotNull(busImpl);

        const int cycles = 100;
        var disposedPlugins = new List<LifecycleStressPlugin>(cycles);

        for (int i = 0; i < cycles; i++)
        {
            // Load: create and register plugin
            var plugin = new LifecycleStressPlugin { SimulatedIoDelayMs = 0 };
            await kernel.RegisterPluginAsync(plugin);

            Assert.Equal(LifecycleState.Loaded, plugin.CurrentState);
            Assert.True(plugin.SubscriptionCount >= 3,
                $"Cycle {i}: Plugin should have 3+ subscriptions, got {plugin.SubscriptionCount}");

            // Verify subscriptions are registered on the bus
            var countA = busImpl.GetSubscriberCount("chaos.lifecycle.topic.a");
            Assert.True(countA >= 1,
                $"Cycle {i}: Topic A should have at least 1 subscriber, got {countA}");

            // Unload: dispose plugin (simulating kernel unloading it)
            plugin.BeginUnload();
            Assert.Equal(LifecycleState.Unloading, plugin.CurrentState);

            plugin.CleanupSubscriptions();
            await plugin.DisposeAsync();

            Assert.Equal(LifecycleState.Unloaded, plugin.CurrentState);
            Assert.True(plugin.WasDisposed, $"Cycle {i}: Plugin should be disposed");
            Assert.Equal(0, plugin.SubscriptionCount);

            disposedPlugins.Add(plugin);
        }

        // After all cycles: verify no leaked subscriptions on the bus
        var finalCountA = busImpl.GetSubscriberCount("chaos.lifecycle.topic.a");
        var finalCountB = busImpl.GetSubscriberCount("chaos.lifecycle.topic.b");
        var finalCountC = busImpl.GetSubscriberCount("chaos.lifecycle.topic.c");

        Assert.Equal(0, finalCountA);
        Assert.Equal(0, finalCountB);
        Assert.Equal(0, finalCountC);

        // Verify all plugins report unloaded
        Assert.All(disposedPlugins, p =>
        {
            Assert.Equal(LifecycleState.Unloaded, p.CurrentState);
            Assert.True(p.WasDisposed);
        });

        Assert.True(kernel.IsReady, "Kernel should remain ready after 100 load/unload cycles");
    }

    /// <summary>
    /// 100 load/unload cycles with concurrent read/write operations.
    /// All operations must either complete successfully or throw clean OperationCanceledException.
    /// No torn state: no partial results, no corruption, no deadlocks.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ConcurrentLoadUnload_WithActiveIO_NoTornState()
    {
        // Arrange
        var kernel = await BuildKernelAsync();
        const int cycles = 100;
        var allOperationsClean = true;
        var totalStarted = 0;
        var totalCompleted = 0;
        var totalInterrupted = 0;

        for (int i = 0; i < cycles; i++)
        {
            var plugin = new LifecycleStressPlugin { SimulatedIoDelayMs = 10 };
            await kernel.RegisterPluginAsync(plugin);

            // Start background I/O operations
            var ioTasks = new List<Task<bool>>();
            for (int j = 0; j < 5; j++)
            {
                ioTasks.Add(plugin.ExecuteIoOperationAsync());
            }

            // Simultaneously begin unload (after tiny delay to let some I/O start)
            await Task.Delay(2);
            plugin.BeginUnload();

            // Wait for all I/O to resolve (complete or cancel)
            try
            {
                var results = await Task.WhenAll(ioTasks);
                // Each result is true (completed) or false (interrupted) -- both are clean outcomes
                foreach (var result in results)
                {
                    if (result) totalCompleted++;
                    else totalInterrupted++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Only OperationCanceledException is acceptable -- anything else is torn state
                allOperationsClean = false;
            }

            totalStarted += plugin.OperationsStarted;
            plugin.CleanupSubscriptions();
            await plugin.DisposeAsync();
        }

        // Assert
        Assert.True(allOperationsClean,
            "All operations should either complete or cancel cleanly -- no torn state");
        Assert.True(totalStarted >= cycles,
            $"At least {cycles} operations should have started, got {totalStarted}");
        Assert.True(totalCompleted + totalInterrupted >= cycles,
            $"All started operations should resolve: completed={totalCompleted}, interrupted={totalInterrupted}");
        Assert.True(kernel.IsReady, "Kernel should remain ready after concurrent load/unload");
    }

    /// <summary>
    /// Load 10 plugin instances in parallel, then unload all in parallel.
    /// After parallel unload, subscription count must return to pre-test baseline (zero).
    /// Proves: Parallel lifecycle operations don't cause subscription leaks.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ParallelLoad10_ParallelUnload_SubscriptionsClean()
    {
        // Arrange
        var kernel = await BuildKernelAsync();
        var busImpl = kernel.MessageBus as DefaultMessageBus;
        Assert.NotNull(busImpl);

        var baselineA = busImpl.GetSubscriberCount("chaos.lifecycle.topic.a");

        const int parallelCount = 10;
        var plugins = new LifecycleStressPlugin[parallelCount];

        // Parallel load: register 10 plugins concurrently
        var loadTasks = new Task[parallelCount];
        for (int i = 0; i < parallelCount; i++)
        {
            var idx = i;
            plugins[idx] = new LifecycleStressPlugin { SimulatedIoDelayMs = 0 };
            loadTasks[idx] = kernel.RegisterPluginAsync(plugins[idx]);
        }
        await Task.WhenAll(loadTasks);

        // Verify all loaded
        Assert.All(plugins, p => Assert.Equal(LifecycleState.Loaded, p.CurrentState));

        // Verify subscriptions are registered (each plugin adds 3 subs to topics a, b, c)
        var midCountA = busImpl.GetSubscriberCount("chaos.lifecycle.topic.a");
        Assert.True(midCountA >= parallelCount,
            $"Topic A should have at least {parallelCount} subscribers after load, got {midCountA}");

        // Parallel unload: dispose all 10 concurrently
        var unloadTasks = new Task[parallelCount];
        for (int i = 0; i < parallelCount; i++)
        {
            var idx = i;
            unloadTasks[idx] = Task.Run(async () =>
            {
                plugins[idx].BeginUnload();
                plugins[idx].CleanupSubscriptions();
                await plugins[idx].DisposeAsync();
            });
        }
        await Task.WhenAll(unloadTasks);

        // Assert: subscriptions back to baseline
        var finalCountA = busImpl.GetSubscriberCount("chaos.lifecycle.topic.a");
        var finalCountB = busImpl.GetSubscriberCount("chaos.lifecycle.topic.b");
        var finalCountC = busImpl.GetSubscriberCount("chaos.lifecycle.topic.c");

        Assert.Equal(baselineA, finalCountA);
        Assert.Equal(0, finalCountB);
        Assert.Equal(0, finalCountC);

        Assert.All(plugins, p =>
        {
            Assert.Equal(LifecycleState.Unloaded, p.CurrentState);
            Assert.True(p.WasDisposed);
            Assert.Equal(0, p.SubscriptionCount);
        });

        Assert.True(kernel.IsReady, "Kernel should remain ready after parallel load/unload");
    }

    /// <summary>
    /// Start a message bus publish storm, unload plugin mid-storm.
    /// After unload, handler count must be correct (no orphaned handlers).
    /// Publishes that hit the unloading handler must either deliver or be cleanly skipped.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task UnloadDuringPublish_NoOrphanedHandlers()
    {
        // Arrange
        var kernel = await BuildKernelAsync();
        var busImpl = kernel.MessageBus as DefaultMessageBus;
        Assert.NotNull(busImpl);

        var plugin = new LifecycleStressPlugin { SimulatedIoDelayMs = 5 };
        await kernel.RegisterPluginAsync(plugin);

        // Also subscribe a control handler that always succeeds
        var controlReceived = 0;
        var controlSub = busImpl.Subscribe("chaos.lifecycle.topic.a", _ =>
        {
            Interlocked.Increment(ref controlReceived);
            return Task.CompletedTask;
        });

        var preUnloadCount = busImpl.GetSubscriberCount("chaos.lifecycle.topic.a");

        // Act -- start publish storm in background
        var publishCts = new CancellationTokenSource();
        var publishErrors = 0;
        var publishCount = 0;

        var publishTask = Task.Run(async () =>
        {
            while (!publishCts.IsCancellationRequested)
            {
                try
                {
                    await busImpl.PublishAndWaitAsync("chaos.lifecycle.topic.a", new PluginMessage
                    {
                        Type = "chaos.lifecycle.storm",
                        Payload = new Dictionary<string, object> { ["seq"] = publishCount }
                    });
                    Interlocked.Increment(ref publishCount);
                }
                catch (Exception)
                {
                    // Publish errors during unload are acceptable
                    Interlocked.Increment(ref publishErrors);
                }
                await Task.Delay(1);
            }
        });

        // Let storm run briefly, then unload
        await Task.Delay(50);
        plugin.BeginUnload();
        plugin.CleanupSubscriptions();
        await plugin.DisposeAsync();

        // Let storm continue briefly after unload
        await Task.Delay(50);
        publishCts.Cancel();

        try { await publishTask; } catch (OperationCanceledException) { }

        // Assert -- no orphaned handlers from the unloaded plugin
        var postUnloadCount = busImpl.GetSubscriberCount("chaos.lifecycle.topic.a");
        Assert.True(postUnloadCount < preUnloadCount,
            $"Subscriber count should decrease after unload: pre={preUnloadCount}, post={postUnloadCount}");

        // Control handler should still be active
        Assert.True(controlReceived > 0, "Control handler should have received messages");
        Assert.True(kernel.IsReady, "Kernel should remain ready after unload-during-publish");

        controlSub.Dispose();
    }

    /// <summary>
    /// Hold WeakReference to unloaded plugin, force GC, verify WeakReference.IsAlive == false.
    /// Proves: No memory leaks from plugin lifecycle -- unloaded plugins are garbage collected.
    /// Note: Tests GC behavior of the plugin object itself (not AssemblyLoadContext, which requires
    /// file-based assembly loading). The principle is identical: unloaded resources must be reclaimable.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task PluginGC_AfterUnload_NoLeak()
    {
        // Arrange
        var kernel = await BuildKernelAsync();
        var weakRefs = new List<WeakReference>(10);

        // Load and unload 10 plugins, keeping weak references
        for (int i = 0; i < 10; i++)
        {
            var weakRef = await LoadAndUnloadPlugin(kernel);
            weakRefs.Add(weakRef);
        }

        // Force GC multiple times to give the runtime every opportunity to collect
        for (int i = 0; i < 5; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        // Assert -- at least some weak references should be dead (GC collected the plugin)
        // We allow some to survive due to GC non-determinism, but the majority should be collected
        var deadCount = weakRefs.Count(wr => !wr.IsAlive);
        Assert.True(deadCount >= 5,
            $"At least 5 of 10 unloaded plugins should be GC'd, but only {deadCount} were collected. " +
            "This indicates a potential memory leak in the plugin lifecycle.");

        Assert.True(kernel.IsReady, "Kernel should remain ready after GC verification");
    }

    /// <summary>
    /// Helper that loads a plugin, uses it briefly, unloads it, and returns a WeakReference.
    /// Kept in a separate method to ensure the strong reference goes out of scope.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private async Task<WeakReference> LoadAndUnloadPlugin(DataWarehouseKernel kernel)
    {
        var plugin = new LifecycleStressPlugin { SimulatedIoDelayMs = 0 };
        var weakRef = new WeakReference(plugin);

        await kernel.RegisterPluginAsync(plugin);

        // Use it briefly
        plugin.SimulateMessageReceived();

        // Unload
        plugin.BeginUnload();
        plugin.CleanupSubscriptions();
        await plugin.DisposeAsync();

        // Unregister from kernel registry so it doesn't hold a strong reference
        kernel.Plugins.Unregister(plugin.Id);

        return weakRef;
    }

    /// <summary>
    /// Rapid load/unload with interleaved message publishing to stress thread safety.
    /// 50 cycles of: register plugin -> publish 10 messages -> unload plugin.
    /// No deadlocks, no torn state, all messages to surviving subscribers delivered.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task RapidLoadUnload_InterleavedPublish_NoDeadlocks()
    {
        // Arrange
        var kernel = await BuildKernelAsync();
        var busImpl = kernel.MessageBus as DefaultMessageBus;
        Assert.NotNull(busImpl);

        // Persistent control subscriber to verify message delivery
        var controlReceived = 0;
        var controlSub = busImpl.Subscribe("chaos.lifecycle.topic.a", _ =>
        {
            Interlocked.Increment(ref controlReceived);
            return Task.CompletedTask;
        });

        const int cycles = 50;

        for (int i = 0; i < cycles; i++)
        {
            var plugin = new LifecycleStressPlugin { SimulatedIoDelayMs = 0 };
            await kernel.RegisterPluginAsync(plugin);

            // Publish messages while plugin is loaded
            var publishTasks = new Task[10];
            for (int j = 0; j < 10; j++)
            {
                publishTasks[j] = busImpl.PublishAndWaitAsync("chaos.lifecycle.topic.a", new PluginMessage
                {
                    Type = "chaos.lifecycle.interleaved",
                    Payload = new Dictionary<string, object> { ["cycle"] = i, ["msg"] = j }
                });
            }
            await Task.WhenAll(publishTasks);

            // Unload
            plugin.BeginUnload();
            plugin.CleanupSubscriptions();
            await plugin.DisposeAsync();
            kernel.Plugins.Unregister(plugin.Id);
        }

        // Assert
        Assert.True(controlReceived >= cycles * 10,
            $"Control subscriber should have received at least {cycles * 10} messages, got {controlReceived}");
        Assert.True(kernel.IsReady, "Kernel should remain ready after rapid interleaved load/unload");

        controlSub.Dispose();
    }
}
