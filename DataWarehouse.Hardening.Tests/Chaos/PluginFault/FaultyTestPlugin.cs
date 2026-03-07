using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Hardening.Tests.Chaos.PluginFault;

/// <summary>
/// Fault mode that controls which exception the plugin throws.
/// </summary>
public enum FaultMode
{
    /// <summary>No fault -- plugin behaves normally.</summary>
    None,

    /// <summary>Throw OutOfMemoryException.</summary>
    OutOfMemory,

    /// <summary>Throw InvalidOperationException (simulates unhandled logic error).</summary>
    UnhandledException,

    /// <summary>Throw TaskCanceledException.</summary>
    TaskCanceled,

    /// <summary>Hang indefinitely (simulate deadlock/infinite loop).</summary>
    Hang,

    /// <summary>Throw AccessViolationException.</summary>
    AccessViolation,

    /// <summary>Throw AggregateException wrapping multiple inner faults.</summary>
    AggregateFailure
}

/// <summary>
/// A test plugin that extends PluginBase and throws configurable fatal exceptions
/// on demand. The fault is triggered ONLY when <see cref="ActiveFault"/> is set
/// to a non-None mode, so tests control exactly when the fault fires.
/// </summary>
public sealed class FaultyTestPlugin : PluginBase
{
    /// <summary>
    /// The fault mode to trigger on the next message/operation.
    /// Thread-safe via volatile.
    /// </summary>
    public volatile FaultMode ActiveFault = FaultMode.None;

    /// <summary>
    /// Counter tracking how many messages were processed before a fault was triggered.
    /// </summary>
    private int _processedBeforeFault;
    public int ProcessedBeforeFault => _processedBeforeFault;

    /// <summary>
    /// Counter tracking total faults thrown.
    /// </summary>
    private int _faultsThrown;
    public int FaultsThrown => _faultsThrown;

    public override string Id => "chaos.faulty-plugin";
    public override PluginCategory Category => PluginCategory.DataTransformationProvider;
    public override string Name => "Faulty Test Plugin";
    public override string Version => "1.0.0";

    /// <summary>
    /// Handle messages -- if ActiveFault is set, throw the configured exception.
    /// Otherwise, increment the processed counter.
    /// </summary>
    public override Task OnMessageAsync(PluginMessage message)
    {
        var fault = ActiveFault;
        if (fault != FaultMode.None)
        {
            Interlocked.Increment(ref _faultsThrown);
            ThrowConfiguredFault(fault);
        }

        Interlocked.Increment(ref _processedBeforeFault);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Triggers the configured fault. This is the core chaos injection mechanism.
    /// </summary>
    private static void ThrowConfiguredFault(FaultMode mode)
    {
        switch (mode)
        {
            case FaultMode.OutOfMemory:
                throw new OutOfMemoryException("Chaos: simulated OOM in FaultyTestPlugin");

            case FaultMode.UnhandledException:
                throw new InvalidOperationException("Chaos: simulated unhandled exception in FaultyTestPlugin");

            case FaultMode.TaskCanceled:
                throw new TaskCanceledException("Chaos: simulated task cancellation in FaultyTestPlugin");

            case FaultMode.AccessViolation:
                throw new AccessViolationException("Chaos: simulated access violation in FaultyTestPlugin");

            case FaultMode.AggregateFailure:
                throw new AggregateException("Chaos: simulated aggregate failure",
                    new OutOfMemoryException("inner OOM"),
                    new InvalidOperationException("inner logic error"));

            case FaultMode.Hang:
                // Block the thread indefinitely to simulate a hang/deadlock.
                // Tests should use a timeout to detect this.
                Thread.Sleep(Timeout.Infinite);
                break;

            default:
                break;
        }
    }
}
