using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Hardening.Tests.Chaos.PluginFault;

/// <summary>
/// A control plugin that always succeeds. Used alongside <see cref="FaultyTestPlugin"/>
/// to verify that healthy plugins continue operating when a peer plugin faults.
/// Tracks operation counts and received messages via atomic counters.
/// </summary>
public sealed class HealthyTestPlugin : PluginBase
{
    /// <summary>
    /// Atomic counter of successfully processed operations.
    /// </summary>
    private int _operationCount;
    public int OperationCount => Volatile.Read(ref _operationCount);

    /// <summary>
    /// Atomic counter of messages received via the message bus.
    /// </summary>
    private int _messagesReceived;
    public int MessagesReceived => Volatile.Read(ref _messagesReceived);

    /// <summary>
    /// List of subscription handles for cleanup tracking.
    /// </summary>
    private readonly List<IDisposable> _subscriptionHandles = new();

    /// <summary>
    /// Whether this plugin is still healthy (no exceptions thrown).
    /// </summary>
    public bool IsHealthy => true;

    public override string Id => "chaos.healthy-plugin";
    public override PluginCategory Category => PluginCategory.DataTransformationProvider;
    public override string Name => "Healthy Test Plugin";
    public override string Version => "1.0.0";

    /// <summary>
    /// Subscribe to test topics when kernel services become available.
    /// </summary>
    protected override void OnKernelServicesInjected()
    {
        base.OnKernelServicesInjected();

        if (MessageBus != null)
        {
            var handle = MessageBus.Subscribe("chaos.test.topic", msg =>
            {
                Interlocked.Increment(ref _messagesReceived);
                return Task.CompletedTask;
            });
            _subscriptionHandles.Add(handle);
        }
    }

    /// <summary>
    /// Handle messages -- always succeeds, increments counter.
    /// </summary>
    public override Task OnMessageAsync(PluginMessage message)
    {
        Interlocked.Increment(ref _operationCount);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Increments operation count externally (for direct invocation in tests).
    /// </summary>
    public void SimulateOperation()
    {
        Interlocked.Increment(ref _operationCount);
    }

    /// <summary>
    /// Gets the number of active subscription handles this plugin holds.
    /// </summary>
    public int SubscriptionCount => _subscriptionHandles.Count;

    /// <summary>
    /// Disposes all subscription handles.
    /// </summary>
    public void CleanupSubscriptions()
    {
        foreach (var handle in _subscriptionHandles)
        {
            handle.Dispose();
        }
        _subscriptionHandles.Clear();
    }
}
