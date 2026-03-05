using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// Level 0 adaptive index: stores exactly 0 or 1 key-value pair with O(1) lookup.
/// </summary>
/// <remarks>
/// This is the simplest possible index for the common case of a single-object container.
/// When a second entry is inserted, the caller should morph up to Level 1 (SortedArray).
/// Thread-safe via lock.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-01 Level 0 Direct Pointer")]
public sealed class DirectPointerIndex : IAdaptiveIndex
{
    private readonly object _lock = new();
    private (byte[] Key, long Value)? _entry;

    /// <inheritdoc />
    public MorphLevel CurrentLevel => MorphLevel.DirectPointer;

    /// <inheritdoc />
    public long ObjectCount
    {
        get
        {
            lock (_lock) { return _entry.HasValue ? 1 : 0; }
        }
    }

    /// <inheritdoc />
    public long RootBlockNumber => -1;

    /// <inheritdoc />
#pragma warning disable CS0067 // Event is required by IAdaptiveIndex but only raised by AdaptiveIndexEngine
    public event Action<MorphLevel, MorphLevel>? LevelChanged;
#pragma warning restore CS0067

    /// <inheritdoc />
    public Task<long?> LookupAsync(byte[] key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_lock)
        {
            if (_entry.HasValue && ByteArrayComparer.Instance.Compare(_entry.Value.Key, key) == 0)
            {
                return Task.FromResult<long?>(_entry.Value.Value);
            }
            return Task.FromResult<long?>(null);
        }
    }

    /// <inheritdoc />
    public Task InsertAsync(byte[] key, long value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_lock)
        {
            if (_entry.HasValue)
            {
                if (ByteArrayComparer.Instance.Compare(_entry.Value.Key, key) == 0)
                {
                    throw new InvalidOperationException("Duplicate key.");
                }
                throw new InvalidOperationException(
                    "DirectPointerIndex can hold at most 1 entry. Morph to a higher level before inserting.");
            }
            _entry = (key, value);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> UpdateAsync(byte[] key, long newValue, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_lock)
        {
            if (_entry.HasValue && ByteArrayComparer.Instance.Compare(_entry.Value.Key, key) == 0)
            {
                _entry = (_entry.Value.Key, newValue);
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(byte[] key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_lock)
        {
            if (_entry.HasValue && ByteArrayComparer.Instance.Compare(_entry.Value.Key, key) == 0)
            {
                _entry = null;
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<(byte[] Key, long Value)> RangeQueryAsync(
        byte[]? startKey,
        byte[]? endKey,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        (byte[] Key, long Value)? snapshot;
        lock (_lock) { snapshot = _entry; }

        if (snapshot.HasValue)
        {
            var key = snapshot.Value.Key;
            bool afterStart = startKey == null || ByteArrayComparer.Instance.Compare(key, startKey) >= 0;
            bool beforeEnd = endKey == null || ByteArrayComparer.Instance.Compare(key, endKey) < 0;

            if (afterStart && beforeEnd)
            {
                yield return snapshot.Value;
            }
        }

        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<long> CountAsync(CancellationToken ct = default)
    {
        lock (_lock) { return Task.FromResult(_entry.HasValue ? 1L : 0L); }
    }

    /// <inheritdoc />
    public Task MorphToAsync(MorphLevel targetLevel, CancellationToken ct = default)
    {
        throw new NotSupportedException(
            "DirectPointerIndex does not support self-morphing. Use AdaptiveIndexEngine for level transitions.");
    }

    /// <inheritdoc />
    public Task<MorphLevel> RecommendLevelAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            // Cat 12 (finding 737): DirectPointerIndex holds at most one entry.
            // Recommend promoting to SortedArray when the single slot is occupied (capacity exhausted).
            return Task.FromResult(_entry.HasValue ? MorphLevel.SortedArray : MorphLevel.DirectPointer);
        }
    }
}
