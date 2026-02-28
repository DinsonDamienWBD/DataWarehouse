using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;
using DataWarehouse.SDK.VirtualDiskEngine.Journal;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// Orchestrator that implements <see cref="IAdaptiveIndex"/> by delegating to the optimal
/// level implementation and auto-morphing based on object count thresholds.
/// </summary>
/// <remarks>
/// <para>
/// The engine starts at Level 0 (DirectPointer) and automatically morphs up when the object
/// count exceeds a level's threshold, or morphs down when it drops below. Morph transitions
/// are WAL-journaled for crash safety: a morph-start marker is written before migration begins
/// and a morph-complete marker after all entries have been migrated.
/// </para>
/// <para>
/// Configurable thresholds: Level0Max=1, Level1Max=10,000, Level2Max=1,000,000.
/// Levels 3-6 throw <see cref="NotSupportedException"/> until implemented in later plans.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-01 Adaptive Index Engine orchestrator")]
public sealed class AdaptiveIndexEngine : IAdaptiveIndex, IAsyncDisposable
{
    private readonly IBlockDevice _device;
    private readonly IBlockAllocator _allocator;
    private readonly IWriteAheadLog _wal;
    private readonly long _rootBlockNumber;
    private readonly int _blockSize;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

    private IAdaptiveIndex _current;
    private MorphLevel _currentLevel;

    /// <summary>
    /// Maximum object count for Level 0 (DirectPointer). Default: 1.
    /// </summary>
    public long Level0Max { get; }

    /// <summary>
    /// Maximum object count for Level 1 (SortedArray). Default: 10,000.
    /// </summary>
    public long Level1Max { get; }

    /// <summary>
    /// Maximum object count for Level 2 (ART). Default: 1,000,000.
    /// </summary>
    public long Level2Max { get; }

    /// <inheritdoc />
    public MorphLevel CurrentLevel
    {
        get
        {
            _lock.EnterReadLock();
            try { return _currentLevel; }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <inheritdoc />
    public long ObjectCount
    {
        get
        {
            _lock.EnterReadLock();
            try { return _current.ObjectCount; }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <inheritdoc />
    public long RootBlockNumber => _rootBlockNumber;

    /// <inheritdoc />
    public event Action<MorphLevel, MorphLevel>? LevelChanged;

    /// <summary>
    /// Initializes a new <see cref="AdaptiveIndexEngine"/>.
    /// </summary>
    /// <param name="device">Block device for I/O.</param>
    /// <param name="allocator">Block allocator for new nodes.</param>
    /// <param name="wal">Write-ahead log for crash recovery.</param>
    /// <param name="rootBlockNumber">Block number of the root node.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="level0Max">Max objects for Level 0. Default 1.</param>
    /// <param name="level1Max">Max objects for Level 1. Default 10,000.</param>
    /// <param name="level2Max">Max objects for Level 2. Default 1,000,000.</param>
    public AdaptiveIndexEngine(
        IBlockDevice device,
        IBlockAllocator allocator,
        IWriteAheadLog wal,
        long rootBlockNumber,
        int blockSize,
        long level0Max = 1,
        long level1Max = 10_000,
        long level2Max = 1_000_000)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _wal = wal ?? throw new ArgumentNullException(nameof(wal));
        _rootBlockNumber = rootBlockNumber;
        _blockSize = blockSize;
        Level0Max = level0Max;
        Level1Max = level1Max;
        Level2Max = level2Max;

        _current = new DirectPointerIndex();
        _currentLevel = MorphLevel.DirectPointer;
    }

    /// <inheritdoc />
    public async Task<long?> LookupAsync(byte[] key, CancellationToken ct = default)
    {
        _lock.EnterReadLock();
        try
        {
            return await _current.LookupAsync(key, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    public async Task InsertAsync(byte[] key, long value, CancellationToken ct = default)
    {
        _lock.EnterWriteLock();
        try
        {
            // Check if we need to morph up before insert
            long count = _current.ObjectCount;
            var needed = RecommendLevelForCount(count + 1);
            if (needed > _currentLevel)
            {
                await MorphToInternalAsync(needed, ct).ConfigureAwait(false);
            }

            await _current.InsertAsync(key, value, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(byte[] key, long newValue, CancellationToken ct = default)
    {
        _lock.EnterWriteLock();
        try
        {
            return await _current.UpdateAsync(key, newValue, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(byte[] key, CancellationToken ct = default)
    {
        _lock.EnterWriteLock();
        try
        {
            bool deleted = await _current.DeleteAsync(key, ct).ConfigureAwait(false);
            if (deleted)
            {
                // Check if we should morph down
                long count = _current.ObjectCount;
                var needed = RecommendLevelForCount(count);
                if (needed < _currentLevel)
                {
                    await MorphToInternalAsync(needed, ct).ConfigureAwait(false);
                }
            }
            return deleted;
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
        // Snapshot current implementation under read lock
        IAdaptiveIndex snapshot;
        _lock.EnterReadLock();
        try
        {
            snapshot = _current;
        }
        finally
        {
            _lock.ExitReadLock();
        }

        await foreach (var entry in snapshot.RangeQueryAsync(startKey, endKey, ct).ConfigureAwait(false))
        {
            yield return entry;
        }
    }

    /// <inheritdoc />
    public async Task<long> CountAsync(CancellationToken ct = default)
    {
        _lock.EnterReadLock();
        try
        {
            return await _current.CountAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    public async Task MorphToAsync(MorphLevel targetLevel, CancellationToken ct = default)
    {
        _lock.EnterWriteLock();
        try
        {
            if (targetLevel == _currentLevel) return;
            await MorphToInternalAsync(targetLevel, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    public Task<MorphLevel> RecommendLevelAsync(CancellationToken ct = default)
    {
        _lock.EnterReadLock();
        try
        {
            long count = _current.ObjectCount;
            return Task.FromResult(RecommendLevelForCount(count));
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Determines the optimal level for a given object count.
    /// </summary>
    private MorphLevel RecommendLevelForCount(long count)
    {
        if (count <= Level0Max) return MorphLevel.DirectPointer;
        if (count <= Level1Max) return MorphLevel.SortedArray;
        if (count <= Level2Max) return MorphLevel.AdaptiveRadixTree;
        return MorphLevel.BeTree; // Will throw NotSupportedException if attempted
    }

    /// <summary>
    /// Internal morph implementation. Must be called under write lock.
    /// WAL-journals the morph transition with start/complete markers.
    /// </summary>
    private async Task MorphToInternalAsync(MorphLevel targetLevel, CancellationToken ct)
    {
        if (targetLevel == _currentLevel) return;

        var oldLevel = _currentLevel;

        // WAL: morph-start marker
        var morphStartEntry = new JournalEntry
        {
            TransactionId = -1,
            Type = JournalEntryType.Checkpoint,
            TargetBlockNumber = _rootBlockNumber,
            BeforeImage = new byte[] { (byte)oldLevel },
            AfterImage = new[] { (byte)targetLevel }
        };
        await _wal.AppendEntryAsync(morphStartEntry, ct).ConfigureAwait(false);
        await _wal.FlushAsync(ct).ConfigureAwait(false);

        // Create new index at target level
        var newIndex = CreateIndexForLevel(targetLevel);

        // Migrate all entries from current to new
        await foreach (var entry in _current.RangeQueryAsync(null, null, ct).ConfigureAwait(false))
        {
            await newIndex.InsertAsync(entry.Key, entry.Value, ct).ConfigureAwait(false);
        }

        // Dispose old index — prefer IAsyncDisposable over IDisposable (old index may have async cleanup).
        if (_current is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else if (_current is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _current = newIndex;
        _currentLevel = targetLevel;

        // WAL: morph-complete marker
        var morphCompleteEntry = new JournalEntry
        {
            TransactionId = -1,
            Type = JournalEntryType.Checkpoint,
            TargetBlockNumber = _rootBlockNumber,
            BeforeImage = new byte[] { (byte)oldLevel },
            AfterImage = new byte[] { (byte)targetLevel, 0xFF } // 0xFF = complete marker
        };
        await _wal.AppendEntryAsync(morphCompleteEntry, ct).ConfigureAwait(false);
        await _wal.FlushAsync(ct).ConfigureAwait(false);

        // Fire event
        LevelChanged?.Invoke(oldLevel, targetLevel);
    }

    /// <summary>
    /// Factory method to create the appropriate index implementation for a morph level.
    /// </summary>
    private IAdaptiveIndex CreateIndexForLevel(MorphLevel level) => level switch
    {
        MorphLevel.DirectPointer => new DirectPointerIndex(),
        MorphLevel.SortedArray => new SortedArrayIndex((int)Level1Max),
        MorphLevel.AdaptiveRadixTree => new ArtIndex(),
        MorphLevel.BeTree => throw new NotSupportedException("Level 3 (BeTree) not yet implemented."),
        MorphLevel.LearnedIndex => throw new NotSupportedException("Level 4 (LearnedIndex) not yet implemented."),
        MorphLevel.BeTreeForest => throw new NotSupportedException("Level 5 (BeTreeForest) not yet implemented."),
        MorphLevel.DistributedRouting => throw new NotSupportedException("Level 6 (DistributedRouting) not yet implemented."),
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown morph level.")
    };

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_current is IDisposable disposable)
        {
            disposable.Dispose();
        }
        _lock.Dispose();
        return ValueTask.CompletedTask;
    }
}
