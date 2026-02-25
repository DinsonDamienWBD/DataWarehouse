using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Concurrency;

/// <summary>
/// Lock striping for VDE write parallelism.
/// Keys are hashed to one of N stripes. Writes to different stripes proceed in parallel.
/// Writes to the same stripe (and therefore same key hash bucket) are serialized.
/// </summary>
/// <remarks>
/// Stripe count should be a power of 2 for fast modulo (bitwise AND).
/// Default: 64 stripes, allowing up to 64 concurrent writers.
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 58: VDE write-path parallelism")]
public sealed class StripedWriteLock : IAsyncDisposable
{
    private readonly SemaphoreSlim[] _stripes;
    private readonly int _stripeMask;
    private bool _disposed;

    /// <summary>
    /// Creates a new striped write lock with the specified number of stripes.
    /// </summary>
    /// <param name="stripeCount">Number of stripes (must be a positive power of 2). Default: 64.</param>
    /// <exception cref="ArgumentException">Thrown if <paramref name="stripeCount"/> is not a positive power of 2.</exception>
    public StripedWriteLock(int stripeCount = 64)
    {
        if (stripeCount <= 0 || (stripeCount & (stripeCount - 1)) != 0)
            throw new ArgumentException("Stripe count must be a positive power of 2.", nameof(stripeCount));

        _stripes = new SemaphoreSlim[stripeCount];
        _stripeMask = stripeCount - 1;

        for (int i = 0; i < stripeCount; i++)
            _stripes[i] = new SemaphoreSlim(1, 1);
    }

    /// <summary>
    /// Gets the number of stripes in this lock.
    /// </summary>
    public int StripeCount => _stripes.Length;

    /// <summary>
    /// Acquires the stripe lock for the given key. Returns a <see cref="WriteRegion"/>
    /// that releases the lock on disposal.
    /// </summary>
    /// <param name="key">The key to acquire a lock for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="WriteRegion"/> that releases the stripe lock when disposed.</returns>
    public async ValueTask<WriteRegion> AcquireAsync(string key, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int stripe = GetStripe(key);
        await _stripes[stripe].WaitAsync(ct);
        return new WriteRegion(_stripes[stripe]);
    }

    /// <summary>
    /// Gets the stripe index for a key using FNV-1a hash.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetStripe(string key)
    {
        uint hash = 2166136261u;
        foreach (char c in key)
        {
            hash ^= (uint)c;
            hash *= 16777619u;
        }
        return (int)(hash & (uint)_stripeMask);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            foreach (var s in _stripes)
                s.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
