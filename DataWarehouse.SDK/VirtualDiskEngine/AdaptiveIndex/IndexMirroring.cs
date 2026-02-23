using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Index;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// Specifies how writes are propagated to mirror copies.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-08 Index RAID - Mirror write mode")]
public enum MirrorWriteMode
{
    /// <summary>
    /// All mirrors must acknowledge the write before returning. Strongest consistency.
    /// </summary>
    Synchronous,

    /// <summary>
    /// Primary acknowledges immediately; secondaries receive writes asynchronously with retry.
    /// Lower latency at the cost of temporary inconsistency.
    /// </summary>
    Asynchronous
}

/// <summary>
/// Health status of an individual mirror copy.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-08 Index RAID - Mirror health")]
public enum MirrorHealthStatus
{
    /// <summary>Mirror is fully synchronized and operational.</summary>
    Healthy,

    /// <summary>Mirror has fallen behind or experienced write failures.</summary>
    Degraded,

    /// <summary>Mirror is currently being rebuilt from the primary.</summary>
    Rebuilding
}

/// <summary>
/// Health information for a single mirror instance.
/// </summary>
/// <param name="MirrorIndex">The ordinal index of this mirror.</param>
/// <param name="Status">Current health status.</param>
/// <param name="PendingRetries">Number of operations pending retry (async mode only).</param>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-08 Index RAID - Mirror health")]
public readonly record struct MirrorHealth(int MirrorIndex, MirrorHealthStatus Status, int PendingRetries);

/// <summary>
/// M-copy index mirroring that maintains redundant copies of an index for fault tolerance.
/// Reads are served from the primary; writes are propagated to all mirrors.
/// </summary>
/// <remarks>
/// <para>
/// In <see cref="MirrorWriteMode.Synchronous"/> mode, all mirrors must successfully complete
/// each write operation. In <see cref="MirrorWriteMode.Asynchronous"/> mode, only the primary
/// must succeed; secondary writes are queued and retried on failure.
/// </para>
/// <para>
/// Analogous to RAID-1 for storage: mirroring adds redundancy at the cost of write amplification.
/// Use <see cref="Rebuild"/> to restore a degraded mirror from the primary.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-08 Index RAID - Mirroring")]
public sealed class IndexMirroring : IAdaptiveIndex, IAsyncDisposable
{
    private readonly IAdaptiveIndex[] _mirrors;
    private readonly IAdaptiveIndex _primary;
    private readonly MirrorWriteMode _writeMode;
    private readonly MirrorHealthStatus[] _healthStatuses;
    private readonly ConcurrentQueue<Func<CancellationToken, Task>> _asyncRetryQueue = new();
    private readonly CancellationTokenSource _backgroundCts = new();
    private readonly Task _retryProcessorTask;
    private int _disposed;

    /// <summary>
    /// Initializes a new mirrored index with M copies created by the provided factory.
    /// </summary>
    /// <param name="mirrorCount">Number of mirror copies (must be >= 2).</param>
    /// <param name="mirrorFactory">Factory that creates an index for a given mirror ordinal.</param>
    /// <param name="mode">Write propagation mode.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if mirrorCount is less than 2.</exception>
    /// <exception cref="ArgumentNullException">Thrown if mirrorFactory is null.</exception>
    public IndexMirroring(int mirrorCount, Func<int, IAdaptiveIndex> mirrorFactory, MirrorWriteMode mode = MirrorWriteMode.Synchronous)
    {
        ArgumentNullException.ThrowIfNull(mirrorFactory);
        ArgumentOutOfRangeException.ThrowIfLessThan(mirrorCount, 2);

        _writeMode = mode;
        _mirrors = new IAdaptiveIndex[mirrorCount];
        _healthStatuses = new MirrorHealthStatus[mirrorCount];
        for (int i = 0; i < mirrorCount; i++)
        {
            _mirrors[i] = mirrorFactory(i) ?? throw new InvalidOperationException(
                $"Mirror factory returned null for mirror {i}.");
            _healthStatuses[i] = MirrorHealthStatus.Healthy;
        }

        _primary = _mirrors[0];
        _retryProcessorTask = mode == MirrorWriteMode.Asynchronous
            ? Task.Run(() => ProcessRetryQueueAsync(_backgroundCts.Token))
            : Task.CompletedTask;
    }

    /// <summary>
    /// Gets the number of mirror copies.
    /// </summary>
    public int MirrorCount => _mirrors.Length;

    /// <inheritdoc />
    public MorphLevel CurrentLevel => _primary.CurrentLevel;

    /// <inheritdoc />
    public long ObjectCount => _primary.ObjectCount;

    /// <inheritdoc />
    public long RootBlockNumber => _primary.RootBlockNumber;

    /// <inheritdoc />
#pragma warning disable CS0067 // Event is required by IAdaptiveIndex but only raised by AdaptiveIndexEngine
    public event Action<MorphLevel, MorphLevel>? LevelChanged;
#pragma warning restore CS0067

    /// <inheritdoc />
    public Task<long?> LookupAsync(byte[] key, CancellationToken ct = default)
        => _primary.LookupAsync(key, ct);

    /// <inheritdoc />
    public Task InsertAsync(byte[] key, long value, CancellationToken ct = default)
        => PropagateWriteAsync(
            (mirror, token) => mirror.InsertAsync(key, value, token),
            ct);

    /// <inheritdoc />
    public Task<bool> UpdateAsync(byte[] key, long newValue, CancellationToken ct = default)
        => PropagateWriteWithResultAsync(
            (mirror, token) => mirror.UpdateAsync(key, newValue, token),
            ct);

    /// <inheritdoc />
    public Task<bool> DeleteAsync(byte[] key, CancellationToken ct = default)
        => PropagateWriteWithResultAsync(
            (mirror, token) => mirror.DeleteAsync(key, token),
            ct);

    /// <inheritdoc />
    public IAsyncEnumerable<(byte[] Key, long Value)> RangeQueryAsync(
        byte[]? startKey, byte[]? endKey, CancellationToken ct = default)
        => _primary.RangeQueryAsync(startKey, endKey, ct);

    /// <inheritdoc />
    public Task<long> CountAsync(CancellationToken ct = default)
        => _primary.CountAsync(ct);

    /// <inheritdoc />
    public Task MorphToAsync(MorphLevel targetLevel, CancellationToken ct = default)
        => PropagateWriteAsync(
            (mirror, token) => mirror.MorphToAsync(targetLevel, token),
            ct);

    /// <inheritdoc />
    public Task<MorphLevel> RecommendLevelAsync(CancellationToken ct = default)
        => _primary.RecommendLevelAsync(ct);

    /// <summary>
    /// Rebuilds a degraded mirror by copying all entries from the primary.
    /// This operation runs in the background and does not block reads or writes.
    /// </summary>
    /// <param name="mirrorIndex">The index of the mirror to rebuild (1..MirrorCount-1).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if mirrorIndex is out of range or is 0 (primary).</exception>
    public async Task Rebuild(int mirrorIndex, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(mirrorIndex, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(mirrorIndex, _mirrors.Length);

        _healthStatuses[mirrorIndex] = MirrorHealthStatus.Rebuilding;

        try
        {
            var target = _mirrors[mirrorIndex];

            // Copy all entries from primary
            await foreach (var (key, value) in _primary.RangeQueryAsync(null, null, ct).ConfigureAwait(false))
            {
                try
                {
                    await target.InsertAsync(key, value, ct).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    // Key already exists in target, update instead
                    await target.UpdateAsync(key, value, ct).ConfigureAwait(false);
                }
            }

            _healthStatuses[mirrorIndex] = MirrorHealthStatus.Healthy;
        }
        catch (OperationCanceledException)
        {
            _healthStatuses[mirrorIndex] = MirrorHealthStatus.Degraded;
            throw;
        }
        catch
        {
            _healthStatuses[mirrorIndex] = MirrorHealthStatus.Degraded;
            throw;
        }
    }

    /// <summary>
    /// Returns the health status of all mirrors.
    /// </summary>
    /// <returns>An array of <see cref="MirrorHealth"/> entries, one per mirror.</returns>
    public MirrorHealth[] GetHealth()
    {
        var result = new MirrorHealth[_mirrors.Length];
        for (int i = 0; i < _mirrors.Length; i++)
        {
            result[i] = new MirrorHealth(i, _healthStatuses[i], 0);
        }
        // The primary's pending retries count comes from the retry queue
        if (_writeMode == MirrorWriteMode.Asynchronous)
        {
            result[0] = result[0] with { PendingRetries = _asyncRetryQueue.Count };
        }
        return result;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _backgroundCts.Cancel();
        try { await _retryProcessorTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected */ }

        _backgroundCts.Dispose();

        for (int i = 0; i < _mirrors.Length; i++)
        {
            if (_mirrors[i] is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (_mirrors[i] is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private async Task PropagateWriteAsync(
        Func<IAdaptiveIndex, CancellationToken, Task> operation,
        CancellationToken ct)
    {
        if (_writeMode == MirrorWriteMode.Synchronous)
        {
            var tasks = new Task[_mirrors.Length];
            for (int i = 0; i < _mirrors.Length; i++)
            {
                tasks[i] = operation(_mirrors[i], ct);
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        else
        {
            // Write primary first
            await operation(_primary, ct).ConfigureAwait(false);

            // Fire-and-forget to secondaries with retry on failure
            for (int i = 1; i < _mirrors.Length; i++)
            {
                int idx = i;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await operation(_mirrors[idx], ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { /* don't retry cancellations */ }
                    catch
                    {
                        _healthStatuses[idx] = MirrorHealthStatus.Degraded;
                        _asyncRetryQueue.Enqueue((token) => operation(_mirrors[idx], token));
                    }
                }, ct);
            }
        }
    }

    private async Task<bool> PropagateWriteWithResultAsync(
        Func<IAdaptiveIndex, CancellationToken, Task<bool>> operation,
        CancellationToken ct)
    {
        if (_writeMode == MirrorWriteMode.Synchronous)
        {
            var tasks = new Task<bool>[_mirrors.Length];
            for (int i = 0; i < _mirrors.Length; i++)
            {
                tasks[i] = operation(_mirrors[i], ct);
            }
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            return results[0]; // Return primary result
        }
        else
        {
            bool result = await operation(_primary, ct).ConfigureAwait(false);

            for (int i = 1; i < _mirrors.Length; i++)
            {
                int idx = i;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await operation(_mirrors[idx], ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { /* don't retry cancellations */ }
                    catch
                    {
                        _healthStatuses[idx] = MirrorHealthStatus.Degraded;
                        _asyncRetryQueue.Enqueue((token) => operation(_mirrors[idx], token).ContinueWith(_ => { }, token));
                    }
                }, ct);
            }

            return result;
        }
    }

    private async Task ProcessRetryQueueAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);

                while (_asyncRetryQueue.TryDequeue(out var retryOp))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        await retryOp(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch
                    {
                        // Re-enqueue for next retry cycle
                        _asyncRetryQueue.Enqueue(retryOp);
                        break; // Avoid tight retry loop
                    }
                }
            }
            catch (OperationCanceledException) { break; }
        }
    }
}
