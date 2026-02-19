using System;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Concurrency;

/// <summary>
/// Scoped async write region that releases the stripe lock on disposal.
/// Returned by <see cref="StripedWriteLock.AcquireAsync"/> to provide RAII-style
/// lock management via <c>await using</c>.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: VDE write-path parallelism")]
public readonly struct WriteRegion : IAsyncDisposable
{
    private readonly SemaphoreSlim _semaphore;

    internal WriteRegion(SemaphoreSlim semaphore)
    {
        _semaphore = semaphore;
    }

    /// <summary>
    /// Releases the stripe lock.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        _semaphore.Release();
        return ValueTask.CompletedTask;
    }
}
