using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// Level 1 adaptive index: sorted array with binary search for up to <see cref="MaxCapacity"/> entries.
/// Provides O(log n) lookup, O(n) insert/delete due to array shifting.
/// </summary>
/// <remarks>
/// Backed by a <see cref="List{T}"/> kept sorted by key using <see cref="ByteArrayComparer"/>.
/// When count exceeds <see cref="MaxCapacity"/>, callers should morph up to Level 2 (ART).
/// Thread-safe via <see cref="ReaderWriterLockSlim"/>.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-01 Level 1 Sorted Array")]
public sealed class SortedArrayIndex : IAdaptiveIndex, IDisposable
{
    private readonly List<(byte[] Key, long Value)> _entries = new();
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

    /// <summary>
    /// Maximum number of entries before callers should morph to a higher level.
    /// Default is 10,000.
    /// </summary>
    public int MaxCapacity { get; }

    /// <inheritdoc />
    public MorphLevel CurrentLevel => MorphLevel.SortedArray;

    /// <inheritdoc />
    public long ObjectCount
    {
        get
        {
            _lock.EnterReadLock();
            try { return _entries.Count; }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <inheritdoc />
    public long RootBlockNumber => -1;

    /// <inheritdoc />
#pragma warning disable CS0067 // Event is required by IAdaptiveIndex but only raised by AdaptiveIndexEngine
    public event Action<MorphLevel, MorphLevel>? LevelChanged;
#pragma warning restore CS0067

    /// <summary>
    /// Initializes a new <see cref="SortedArrayIndex"/> with the specified maximum capacity.
    /// </summary>
    /// <param name="maxCapacity">Maximum entries before morph-up is recommended. Default 10,000.</param>
    public SortedArrayIndex(int maxCapacity = 10_000)
    {
        MaxCapacity = maxCapacity > 0 ? maxCapacity : throw new ArgumentOutOfRangeException(nameof(maxCapacity));
    }

    /// <inheritdoc />
    public Task<long?> LookupAsync(byte[] key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        _lock.EnterReadLock();
        try
        {
            int index = BinarySearch(key);
            if (index >= 0)
            {
                return Task.FromResult<long?>(_entries[index].Value);
            }
            return Task.FromResult<long?>(null);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    public Task InsertAsync(byte[] key, long value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        _lock.EnterWriteLock();
        try
        {
            int index = BinarySearch(key);
            if (index >= 0)
            {
                throw new InvalidOperationException("Duplicate key.");
            }

            int insertionPoint = ~index;
            _entries.Insert(insertionPoint, (key, value));
            return Task.CompletedTask;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    public Task<bool> UpdateAsync(byte[] key, long newValue, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        _lock.EnterWriteLock();
        try
        {
            int index = BinarySearch(key);
            if (index >= 0)
            {
                _entries[index] = (_entries[index].Key, newValue);
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(byte[] key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        _lock.EnterWriteLock();
        try
        {
            int index = BinarySearch(key);
            if (index >= 0)
            {
                _entries.RemoveAt(index);
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<(byte[] Key, long Value)> RangeQueryAsync(
        byte[]? startKey,
        byte[]? endKey,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        List<(byte[] Key, long Value)> snapshot;
        _lock.EnterReadLock();
        try
        {
            // Find start position via binary search
            int startIndex = 0;
            if (startKey != null)
            {
                int idx = BinarySearch(startKey);
                startIndex = idx >= 0 ? idx : ~idx;
            }

            // Snapshot the relevant range
            snapshot = new List<(byte[] Key, long Value)>();
            for (int i = startIndex; i < _entries.Count; i++)
            {
                if (endKey != null && ByteArrayComparer.Instance.Compare(_entries[i].Key, endKey) >= 0)
                    break;
                snapshot.Add(_entries[i]);
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }

        foreach (var entry in snapshot)
        {
            yield return entry;
        }

        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<long> CountAsync(CancellationToken ct = default)
    {
        _lock.EnterReadLock();
        try { return Task.FromResult((long)_entries.Count); }
        finally { _lock.ExitReadLock(); }
    }

    /// <inheritdoc />
    public Task MorphToAsync(MorphLevel targetLevel, CancellationToken ct = default)
    {
        throw new NotSupportedException(
            "SortedArrayIndex does not support self-morphing. Use AdaptiveIndexEngine for level transitions.");
    }

    /// <inheritdoc />
    public Task<MorphLevel> RecommendLevelAsync(CancellationToken ct = default)
    {
        _lock.EnterReadLock();
        try
        {
            if (_entries.Count <= 1)
                return Task.FromResult(MorphLevel.DirectPointer);
            if (_entries.Count <= MaxCapacity)
                return Task.FromResult(MorphLevel.SortedArray);
            return Task.FromResult(MorphLevel.AdaptiveRadixTree);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Gets a read-only snapshot of all entries for migration purposes.
    /// </summary>
    internal IReadOnlyList<(byte[] Key, long Value)> GetAllEntries()
    {
        _lock.EnterReadLock();
        try { return _entries.ToArray(); }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Binary search for a key. Returns non-negative index if found,
    /// or bitwise complement of insertion point if not found.
    /// </summary>
    private int BinarySearch(byte[] key)
    {
        int left = 0, right = _entries.Count - 1;
        while (left <= right)
        {
            int mid = left + (right - left) / 2;
            int cmp = ByteArrayComparer.Instance.Compare(_entries[mid].Key, key);
            if (cmp == 0)
                return mid;
            if (cmp < 0)
                left = mid + 1;
            else
                right = mid - 1;
        }
        return ~left;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _lock.Dispose();
    }
}

/// <summary>
/// Lexicographic byte array comparer for sorted index operations.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-01 Byte array comparison utility")]
public sealed class ByteArrayComparer : IComparer<byte[]>
{
    /// <summary>
    /// Singleton instance of the comparer.
    /// </summary>
    public static readonly ByteArrayComparer Instance = new();

    private ByteArrayComparer() { }

    /// <inheritdoc />
    public int Compare(byte[]? x, byte[]? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int minLen = Math.Min(x.Length, y.Length);
        for (int i = 0; i < minLen; i++)
        {
            int cmp = x[i].CompareTo(y[i]);
            if (cmp != 0) return cmp;
        }
        return x.Length.CompareTo(y.Length);
    }
}
