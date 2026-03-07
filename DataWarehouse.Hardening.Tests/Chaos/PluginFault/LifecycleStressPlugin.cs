using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Hardening.Tests.Chaos.PluginFault;

/// <summary>
/// Lifecycle state for tracking plugin load/unload transitions.
/// </summary>
public enum LifecycleState
{
    /// <summary>Plugin is being loaded.</summary>
    Loading,

    /// <summary>Plugin is fully loaded and operational.</summary>
    Loaded,

    /// <summary>Plugin is being unloaded.</summary>
    Unloading,

    /// <summary>Plugin has been fully unloaded.</summary>
    Unloaded
}

/// <summary>
/// A lightweight plugin designed for rapid load/unload stress testing.
/// Extends PluginBase, registers multiple message bus subscriptions during initialization,
/// performs configurable-delay async operations, and tracks lifecycle state with atomic counters.
///
/// Used by ConcurrentLifecycleTests to prove 100+ load/unload cycles are safe under concurrent I/O.
/// </summary>
public sealed class LifecycleStressPlugin : PluginBase
{
    private volatile LifecycleState _lifecycleState = LifecycleState.Loading;

    /// <summary>Current lifecycle state (thread-safe via volatile).</summary>
    public LifecycleState CurrentState => _lifecycleState;

    /// <summary>Configurable delay (ms) for simulated I/O in Execute.</summary>
    public int SimulatedIoDelayMs { get; set; } = 50;

    // --- Atomic counters ---

    private int _operationsStarted;
    /// <summary>Number of execute operations started.</summary>
    public int OperationsStarted => Volatile.Read(ref _operationsStarted);

    private int _operationsCompleted;
    /// <summary>Number of execute operations completed successfully.</summary>
    public int OperationsCompleted => Volatile.Read(ref _operationsCompleted);

    private int _operationsInterrupted;
    /// <summary>Number of execute operations interrupted (canceled/faulted).</summary>
    public int OperationsInterrupted => Volatile.Read(ref _operationsInterrupted);

    private int _messagesReceived;
    /// <summary>Total messages received across all subscriptions.</summary>
    public int MessagesReceived => Volatile.Read(ref _messagesReceived);

    private int _disposeCalled;
    /// <summary>Whether Dispose/DisposeAsync has been called.</summary>
    public bool WasDisposed => Volatile.Read(ref _disposeCalled) > 0;

    /// <summary>Unique instance ID for tracking parallel instances.</summary>
    public string InstanceId { get; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Subscription handles for cleanup tracking.</summary>
    private readonly List<IDisposable> _subscriptionHandles = new();
    private readonly object _subLock = new();

    /// <summary>CancellationTokenSource for draining in-flight operations during unload.</summary>
    private readonly CancellationTokenSource _unloadCts = new();

    public override string Id => $"chaos.lifecycle-stress-{InstanceId}";
    public override PluginCategory Category => PluginCategory.DataTransformationProvider;
    public override string Name => $"Lifecycle Stress Plugin ({InstanceId})";
    public override string Version => "1.0.0";

    /// <summary>
    /// Subscribe to multiple test topics when kernel services become available.
    /// </summary>
    protected override void OnKernelServicesInjected()
    {
        base.OnKernelServicesInjected();
        _lifecycleState = LifecycleState.Loaded;

        if (MessageBus != null)
        {
            // Register multiple subscriptions to stress the bus
            var topics = new[]
            {
                "chaos.lifecycle.topic.a",
                "chaos.lifecycle.topic.b",
                "chaos.lifecycle.topic.c"
            };

            lock (_subLock)
            {
                foreach (var topic in topics)
                {
                    var handle = MessageBus.Subscribe(topic, msg =>
                    {
                        Interlocked.Increment(ref _messagesReceived);
                        return Task.CompletedTask;
                    });
                    _subscriptionHandles.Add(handle);
                }
            }
        }
    }

    /// <summary>
    /// Handle messages -- simulates a slow async I/O operation.
    /// Operations that are interrupted by unload throw OperationCanceledException cleanly.
    /// </summary>
    public override async Task OnMessageAsync(PluginMessage message)
    {
        Interlocked.Increment(ref _operationsStarted);

        try
        {
            // Simulate in-flight I/O with configurable delay
            await Task.Delay(SimulatedIoDelayMs, _unloadCts.Token);
            Interlocked.Increment(ref _operationsCompleted);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _operationsInterrupted);
            throw; // Re-throw so caller sees clean cancellation
        }
    }

    /// <summary>
    /// Execute a standalone I/O operation (for direct invocation in tests).
    /// Returns true if completed, false if interrupted.
    /// </summary>
    public async Task<bool> ExecuteIoOperationAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _operationsStarted);

        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_unloadCts.Token, ct);
            await Task.Delay(SimulatedIoDelayMs, linkedCts.Token);
            Interlocked.Increment(ref _operationsCompleted);
            return true;
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _operationsInterrupted);
            return false;
        }
    }

    /// <summary>
    /// Begin unload: cancel in-flight operations and transition state.
    /// </summary>
    public void BeginUnload()
    {
        _lifecycleState = LifecycleState.Unloading;
        _unloadCts.Cancel();
    }

    /// <summary>
    /// Gets the number of active subscription handles.
    /// </summary>
    public int SubscriptionCount
    {
        get { lock (_subLock) { return _subscriptionHandles.Count; } }
    }

    /// <summary>
    /// Disposes all subscription handles (simulating kernel cleanup on unload).
    /// </summary>
    public void CleanupSubscriptions()
    {
        lock (_subLock)
        {
            foreach (var handle in _subscriptionHandles)
            {
                try { handle.Dispose(); } catch { /* Best-effort cleanup */ }
            }
            _subscriptionHandles.Clear();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Interlocked.Increment(ref _disposeCalled);
            _lifecycleState = LifecycleState.Unloading;
            _unloadCts.Cancel();
            CleanupSubscriptions();
            _unloadCts.Dispose();
            _lifecycleState = LifecycleState.Unloaded;
        }
        base.Dispose(disposing);
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        Interlocked.Increment(ref _disposeCalled);
        _lifecycleState = LifecycleState.Unloading;
        _unloadCts.Cancel();
        CleanupSubscriptions();
        _unloadCts.Dispose();
        _lifecycleState = LifecycleState.Unloaded;
        await base.DisposeAsyncCore();
    }
}
