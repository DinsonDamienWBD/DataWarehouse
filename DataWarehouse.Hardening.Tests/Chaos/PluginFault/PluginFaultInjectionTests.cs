using DataWarehouse.Kernel;
using DataWarehouse.Kernel.Messaging;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Hardening.Tests.Chaos.PluginFault;

/// <summary>
/// Chaos engineering tests that prove the Kernel and message bus survive fatal exceptions
/// thrown inside plugin methods mid-operation. These tests validate isolation guarantees:
/// a single misbehaving plugin must not bring down the entire system.
///
/// Report: "Stage 3 - Steps 1-2 - Plugin Fault Injection"
/// </summary>
public class PluginFaultInjectionTests : IAsyncDisposable
{
    private DataWarehouseKernel? _kernel;

    /// <summary>
    /// Build a minimal Kernel with in-memory storage and the given plugins registered.
    /// </summary>
    private async Task<DataWarehouseKernel> BuildKernelAsync(params IPlugin[] plugins)
    {
        var builder = KernelBuilder.Create()
            .WithKernelId($"chaos-test-{Guid.NewGuid():N}")
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
    /// Chaos scenario: A plugin throws OutOfMemoryException during message handling.
    /// The Kernel (via message bus) must catch the exception, log it, and continue
    /// serving the healthy plugin without process crash.
    /// </summary>
    [Fact]
    public async Task KernelIsolates_OomFault_OtherPluginsContinue()
    {
        // Arrange
        var faulty = new FaultyTestPlugin();
        var healthy = new HealthyTestPlugin();
        var kernel = await BuildKernelAsync(faulty, healthy);

        // Subscribe both plugins explicitly to the test topic
        var faultySub = kernel.MessageBus.Subscribe("chaos.oom.topic", msg =>
            faulty.OnMessageAsync(msg));

        var healthyCount = 0;
        var healthySub = kernel.MessageBus.Subscribe("chaos.oom.topic", _ =>
        {
            Interlocked.Increment(ref healthyCount);
            return Task.CompletedTask;
        });

        // Act -- send a normal message first to prove both plugins are alive
        await kernel.MessageBus.PublishAndWaitAsync("chaos.oom.topic", new PluginMessage
        {
            Type = "chaos.test.normal",
            Payload = new Dictionary<string, object> { ["data"] = "pre-fault" }
        });

        Assert.True(faulty.ProcessedBeforeFault >= 1,
            "FaultyTestPlugin should have processed at least 1 pre-fault message");

        // Arm the fault
        faulty.ActiveFault = FaultMode.OutOfMemory;

        // Send message that will trigger OOM in faulty plugin
        // The message bus catches subscriber exceptions -- this must NOT throw
        await kernel.MessageBus.PublishAndWaitAsync("chaos.oom.topic", new PluginMessage
        {
            Type = "chaos.test.oom",
            Payload = new Dictionary<string, object> { ["data"] = "trigger-oom" }
        });

        // Disarm
        faulty.ActiveFault = FaultMode.None;

        // Send another message to prove healthy plugin still works
        await kernel.MessageBus.PublishAndWaitAsync("chaos.oom.topic", new PluginMessage
        {
            Type = "chaos.test.post-fault",
            Payload = new Dictionary<string, object> { ["data"] = "post-fault" }
        });

        // Assert -- healthy subscriber received messages before and after the fault
        Assert.True(healthyCount >= 3,
            $"Healthy subscriber should have received at least 3 messages, got {healthyCount}");
        Assert.True(kernel.IsReady, "Kernel should still be ready after OOM fault");

        // The faulty plugin should have thrown at least once
        Assert.True(faulty.FaultsThrown >= 1, "FaultyTestPlugin should have thrown at least 1 fault");

        faultySub.Dispose();
        healthySub.Dispose();
    }

    /// <summary>
    /// Chaos scenario: A plugin throws AccessViolationException (simulating a severe
    /// native memory fault). The Kernel must isolate the fault without process crash.
    /// StackOverflowException cannot be caught in .NET, so we use AccessViolationException
    /// as the "severe fault" representative.
    /// </summary>
    [Fact]
    public async Task KernelIsolates_AccessViolationFault_NoProcessCrash()
    {
        // Arrange
        var faulty = new FaultyTestPlugin();
        var healthy = new HealthyTestPlugin();
        var kernel = await BuildKernelAsync(faulty, healthy);

        // Subscribe faulty plugin to the topic directly (to simulate it crashing during publish)
        var faultySub = kernel.MessageBus.Subscribe("chaos.fault.av", msg =>
        {
            throw new AccessViolationException("Chaos: simulated AV");
        });

        var healthyReceived = 0;
        var healthySub = kernel.MessageBus.Subscribe("chaos.fault.av", _ =>
        {
            Interlocked.Increment(ref healthyReceived);
            return Task.CompletedTask;
        });

        // Act -- publish a message where one subscriber throws AV
        await kernel.MessageBus.PublishAndWaitAsync("chaos.fault.av", new PluginMessage
        {
            Type = "chaos.test.av",
            Payload = new Dictionary<string, object> { ["data"] = "trigger-av" }
        });

        // Allow async fire-and-forget tasks to settle
        await Task.Delay(100);

        // Assert -- Kernel still alive, healthy subscriber received the message
        Assert.True(kernel.IsReady, "Kernel should remain ready after AccessViolation fault");
        Assert.True(healthyReceived >= 1,
            $"Healthy subscriber should have received the message, got {healthyReceived}");

        faultySub.Dispose();
        healthySub.Dispose();
    }

    /// <summary>
    /// Chaos scenario: After a plugin faults and its subscriptions are disposed,
    /// verify no orphaned subscriptions remain on the message bus.
    /// </summary>
    [Fact]
    public async Task MessageBusCleanup_AfterFault_NoOrphanedSubscriptions()
    {
        // Arrange
        var faulty = new FaultyTestPlugin();
        var healthy = new HealthyTestPlugin();
        var kernel = await BuildKernelAsync(faulty, healthy);

        // Subscribe both to a shared topic
        var faultySub = kernel.MessageBus.Subscribe("chaos.cleanup.topic", msg =>
            faulty.OnMessageAsync(msg));

        var healthySub = kernel.MessageBus.Subscribe("chaos.cleanup.topic", msg =>
            healthy.OnMessageAsync(msg));

        // Verify both subscriptions active
        var busImpl = kernel.MessageBus as DefaultMessageBus;
        Assert.NotNull(busImpl);

        var countBefore = busImpl.GetSubscriberCount("chaos.cleanup.topic");
        Assert.True(countBefore >= 2, $"Expected at least 2 subscribers before cleanup, got {countBefore}");

        // Simulate fault + cleanup: dispose faulty plugin's subscription
        faulty.ActiveFault = FaultMode.OutOfMemory;

        // Publish to trigger the fault
        await busImpl.PublishAndWaitAsync("chaos.cleanup.topic", new PluginMessage
        {
            Type = "chaos.test.cleanup",
            Payload = new Dictionary<string, object> { ["data"] = "fault-trigger" }
        });

        // Simulate kernel cleanup: dispose faulty subscription (as kernel would after detecting fault)
        faultySub.Dispose();

        // Assert
        var countAfter = busImpl.GetSubscriberCount("chaos.cleanup.topic");
        Assert.True(countAfter < countBefore,
            $"Subscriber count should decrease after cleanup: before={countBefore}, after={countAfter}");
        Assert.True(countAfter >= 1,
            $"Healthy subscriber should still be registered, count={countAfter}");

        healthySub.Dispose();
    }

    /// <summary>
    /// Chaos scenario: 10 cycles of (load faulty plugin -> trigger fault -> cleanup -> repeat)
    /// while a healthy plugin runs continuously. The Kernel must handle all cycles without
    /// degradation in the healthy plugin's operation.
    /// </summary>
    [Fact]
    public async Task RepeatedFaults_KernelRemainsStable()
    {
        // Arrange
        var healthy = new HealthyTestPlugin();
        var kernel = await BuildKernelAsync(healthy);

        const int cycles = 10;
        var faultCount = 0;

        for (int i = 0; i < cycles; i++)
        {
            // Create a fresh faulty subscriber each cycle
            var faultyTriggered = false;
            var faultySub = kernel.MessageBus.Subscribe("chaos.repeat.topic", _ =>
            {
                faultyTriggered = true;
                throw new InvalidOperationException($"Chaos: repeated fault cycle {i}");
            });

            var healthySub = kernel.MessageBus.Subscribe("chaos.repeat.topic", _ =>
            {
                healthy.SimulateOperation();
                return Task.CompletedTask;
            });

            // Trigger the fault
            await kernel.MessageBus.PublishAndWaitAsync("chaos.repeat.topic", new PluginMessage
            {
                Type = "chaos.test.repeat",
                Payload = new Dictionary<string, object> { ["cycle"] = i }
            });

            await Task.Delay(50); // let async settle

            if (faultyTriggered) faultCount++;

            // Cleanup faulty subscription (simulating kernel unload)
            faultySub.Dispose();
            healthySub.Dispose();
        }

        // Assert -- kernel stable after all cycles, healthy plugin processed operations
        Assert.True(kernel.IsReady, "Kernel should be ready after 10 fault cycles");
        Assert.True(healthy.OperationCount >= cycles,
            $"HealthyTestPlugin should have processed at least {cycles} operations, got {healthy.OperationCount}");
        Assert.True(faultCount >= cycles / 2,
            $"At least half the fault cycles should have triggered, got {faultCount}/{cycles}");
    }

    /// <summary>
    /// Chaos scenario: During a message bus publish, one subscriber faults while
    /// others must still receive the message. The message bus must not short-circuit
    /// delivery when one handler throws.
    /// </summary>
    [Fact]
    public async Task FaultDuringPublish_HealthySubscribersStillReceive()
    {
        // Arrange
        var kernel = await BuildKernelAsync();

        var received = new int[3];

        // Register 3 subscribers -- #1 faults, #0 and #2 must still get the message
        var sub0 = kernel.MessageBus.Subscribe("chaos.publish.topic", _ =>
        {
            Interlocked.Increment(ref received[0]);
            return Task.CompletedTask;
        });

        var sub1 = kernel.MessageBus.Subscribe("chaos.publish.topic", _ =>
        {
            Interlocked.Increment(ref received[1]);
            throw new OutOfMemoryException("Chaos: subscriber 1 OOM");
        });

        var sub2 = kernel.MessageBus.Subscribe("chaos.publish.topic", _ =>
        {
            Interlocked.Increment(ref received[2]);
            return Task.CompletedTask;
        });

        // Act
        await kernel.MessageBus.PublishAndWaitAsync("chaos.publish.topic", new PluginMessage
        {
            Type = "chaos.test.publish",
            Payload = new Dictionary<string, object> { ["data"] = "multi-subscriber" }
        });

        // Allow async tasks to complete
        await Task.Delay(100);

        // Assert -- all 3 subscribers were invoked (the bus catches per-subscriber exceptions)
        Assert.True(received[0] >= 1,
            $"Subscriber 0 (healthy) should have received message, got {received[0]}");
        Assert.True(received[1] >= 1,
            $"Subscriber 1 (faulty) should have been invoked, got {received[1]}");
        Assert.True(received[2] >= 1,
            $"Subscriber 2 (healthy) should have received message, got {received[2]}");

        Assert.True(kernel.IsReady, "Kernel should remain ready");

        sub0.Dispose();
        sub1.Dispose();
        sub2.Dispose();
    }

    /// <summary>
    /// Chaos scenario: AggregateException wrapping multiple inner faults is thrown
    /// by a plugin during message handling. The message bus must unwrap and handle
    /// gracefully without crashing.
    /// </summary>
    [Fact]
    public async Task KernelIsolates_AggregateFault_GracefulHandling()
    {
        // Arrange
        var faulty = new FaultyTestPlugin();
        var healthy = new HealthyTestPlugin();
        var kernel = await BuildKernelAsync(faulty, healthy);

        // Subscribe both
        var healthyCount = 0;
        var faultySub = kernel.MessageBus.Subscribe("chaos.aggregate.topic", msg =>
            faulty.OnMessageAsync(msg));

        var healthySub = kernel.MessageBus.Subscribe("chaos.aggregate.topic", _ =>
        {
            Interlocked.Increment(ref healthyCount);
            return Task.CompletedTask;
        });

        // Arm aggregate fault
        faulty.ActiveFault = FaultMode.AggregateFailure;

        // Act
        await kernel.MessageBus.PublishAndWaitAsync("chaos.aggregate.topic", new PluginMessage
        {
            Type = "chaos.test.aggregate",
            Payload = new Dictionary<string, object> { ["data"] = "trigger-aggregate" }
        });

        await Task.Delay(100);

        // Assert
        Assert.True(kernel.IsReady, "Kernel should remain ready after AggregateException");
        Assert.True(healthyCount >= 1,
            $"Healthy subscriber should have received the message, got {healthyCount}");
        Assert.True(faulty.FaultsThrown >= 1,
            $"FaultyTestPlugin should have thrown at least 1 aggregate fault");

        faultySub.Dispose();
        healthySub.Dispose();
    }

    /// <summary>
    /// Chaos scenario: TaskCanceledException thrown during message handling.
    /// This tests that cancellation exceptions (common in async code) are properly
    /// isolated and don't propagate to cancel other operations.
    /// </summary>
    [Fact]
    public async Task KernelIsolates_TaskCanceledFault_OtherOperationsContinue()
    {
        // Arrange
        var faulty = new FaultyTestPlugin();
        var healthy = new HealthyTestPlugin();
        var kernel = await BuildKernelAsync(faulty, healthy);

        var healthyCount = 0;
        var faultySub = kernel.MessageBus.Subscribe("chaos.cancel.topic", msg =>
            faulty.OnMessageAsync(msg));

        var healthySub = kernel.MessageBus.Subscribe("chaos.cancel.topic", _ =>
        {
            Interlocked.Increment(ref healthyCount);
            return Task.CompletedTask;
        });

        // Arm task canceled fault
        faulty.ActiveFault = FaultMode.TaskCanceled;

        // Act
        await kernel.MessageBus.PublishAndWaitAsync("chaos.cancel.topic", new PluginMessage
        {
            Type = "chaos.test.cancel",
            Payload = new Dictionary<string, object> { ["data"] = "trigger-cancel" }
        });

        await Task.Delay(100);

        // Disarm and send another message
        faulty.ActiveFault = FaultMode.None;

        await kernel.MessageBus.PublishAndWaitAsync("chaos.cancel.topic", new PluginMessage
        {
            Type = "chaos.test.post-cancel",
            Payload = new Dictionary<string, object> { ["data"] = "post-cancel" }
        });

        // Assert
        Assert.True(kernel.IsReady, "Kernel should remain ready after TaskCanceledException");
        Assert.True(healthyCount >= 2,
            $"Healthy subscriber should have received at least 2 messages, got {healthyCount}");

        faultySub.Dispose();
        healthySub.Dispose();
    }
}
