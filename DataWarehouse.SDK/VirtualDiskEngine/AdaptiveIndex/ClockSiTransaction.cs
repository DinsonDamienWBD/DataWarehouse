using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// Timestamp for Clock-based Snapshot Isolation, combining physical wall-clock time
/// with a logical counter for same-physical-time ordering (Lamport-like).
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-05 Clock-SI timestamp")]
public readonly struct ClockSiTimestamp : IComparable<ClockSiTimestamp>, IEquatable<ClockSiTimestamp>
{
    /// <summary>
    /// Gets the physical time component (UTC ticks from <see cref="DateTimeOffset.UtcNow"/>).
    /// </summary>
    public long PhysicalTime { get; }

    /// <summary>
    /// Gets the logical counter for disambiguation within the same physical time.
    /// </summary>
    public int LogicalCounter { get; }

    /// <summary>
    /// Initializes a new <see cref="ClockSiTimestamp"/>.
    /// </summary>
    public ClockSiTimestamp(long physicalTime, int logicalCounter)
    {
        PhysicalTime = physicalTime;
        LogicalCounter = logicalCounter;
    }

    /// <summary>
    /// Creates a timestamp representing the current instant.
    /// </summary>
    public static ClockSiTimestamp Now() => new(DateTimeOffset.UtcNow.Ticks, 0);

    /// <summary>
    /// Advances the logical counter (used when physical time hasn't changed).
    /// </summary>
    public ClockSiTimestamp Advance() => new(PhysicalTime, LogicalCounter + 1);

    /// <inheritdoc />
    public int CompareTo(ClockSiTimestamp other)
    {
        int cmp = PhysicalTime.CompareTo(other.PhysicalTime);
        return cmp != 0 ? cmp : LogicalCounter.CompareTo(other.LogicalCounter);
    }

    /// <inheritdoc />
    public bool Equals(ClockSiTimestamp other) =>
        PhysicalTime == other.PhysicalTime && LogicalCounter == other.LogicalCounter;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ClockSiTimestamp other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(PhysicalTime, LogicalCounter);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(ClockSiTimestamp left, ClockSiTimestamp right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(ClockSiTimestamp left, ClockSiTimestamp right) => !left.Equals(right);

    /// <summary>Less-than operator.</summary>
    public static bool operator <(ClockSiTimestamp left, ClockSiTimestamp right) => left.CompareTo(right) < 0;

    /// <summary>Greater-than operator.</summary>
    public static bool operator >(ClockSiTimestamp left, ClockSiTimestamp right) => left.CompareTo(right) > 0;

    /// <summary>Less-than-or-equal operator.</summary>
    public static bool operator <=(ClockSiTimestamp left, ClockSiTimestamp right) => left.CompareTo(right) <= 0;

    /// <summary>Greater-than-or-equal operator.</summary>
    public static bool operator >=(ClockSiTimestamp left, ClockSiTimestamp right) => left.CompareTo(right) >= 0;

    /// <inheritdoc />
    public override string ToString() => $"ClockSI({PhysicalTime}:{LogicalCounter})";
}

/// <summary>
/// State of a Clock-SI distributed transaction.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-05 Clock-SI transaction state")]
public enum TransactionState
{
    /// <summary>Transaction is active and accepting reads/writes.</summary>
    Active = 0,

    /// <summary>Transaction has been committed.</summary>
    Committed = 1,

    /// <summary>Transaction has been aborted.</summary>
    Aborted = 2
}

/// <summary>
/// A buffered write record within a Clock-SI transaction.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-05 Clock-SI write record")]
public sealed class WriteRecord
{
    /// <summary>Gets the key being written.</summary>
    public byte[] Key { get; }

    /// <summary>Gets the value being written.</summary>
    public long Value { get; }

    /// <summary>Gets the value at snapshot time for conflict detection. Null if key did not exist at snapshot.</summary>
    public long? SnapshotValue { get; }

    /// <summary>Gets the target shard ID for this write.</summary>
    public int TargetShardId { get; }

    /// <summary>
    /// Initializes a new <see cref="WriteRecord"/>.
    /// </summary>
    public WriteRecord(byte[] key, long value, int targetShardId, long? snapshotValue = null)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Value = value;
        TargetShardId = targetShardId;
        SnapshotValue = snapshotValue;
    }
}

/// <summary>
/// Clock-based Snapshot Isolation transaction for distributed index operations.
/// </summary>
/// <remarks>
/// <para>
/// Clock-SI enables consistent reads across distributed shards. A transaction begins by
/// capturing a snapshot timestamp; all reads see data committed before that timestamp.
/// Writes are buffered in a write set and applied atomically at commit time.
/// </para>
/// <para>
/// Commit uses a two-phase approach: first prepare (check for write-write conflicts on
/// each shard), then commit (apply all buffered writes). If any shard detects a conflict
/// with a concurrent committed transaction, the transaction aborts.
/// </para>
/// <para>
/// Thread safety: the write set is protected by a <see cref="SemaphoreSlim"/> for
/// concurrent access from multiple shard operations.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-05 Clock-SI distributed transaction")]
public sealed class ClockSiTransaction : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly List<WriteRecord> _writeSet = new();
    private TransactionState _state;
<<<<<<< Updated upstream
<<<<<<< Updated upstream
<<<<<<< Updated upstream
<<<<<<< Updated upstream
<<<<<<< Updated upstream
<<<<<<< Updated upstream
    private int _disposed; // Cat 7 (finding 736): Interlocked guard for exactly-once Dispose
=======
    private int _disposed; // 0 = not disposed, 1 = disposed (Interlocked.Exchange guard)
>>>>>>> Stashed changes
=======
    private int _disposed; // 0 = not disposed, 1 = disposed (Interlocked.Exchange guard)
>>>>>>> Stashed changes
=======
    private int _disposed; // 0 = not disposed, 1 = disposed (Interlocked.Exchange guard)
>>>>>>> Stashed changes
=======
    private int _disposed; // 0 = not disposed, 1 = disposed (Interlocked.Exchange guard)
>>>>>>> Stashed changes
=======
    private int _disposed; // 0 = not disposed, 1 = disposed (Interlocked.Exchange guard)
>>>>>>> Stashed changes
=======
    private int _disposed; // 0 = not disposed, 1 = disposed (Interlocked.Exchange guard)
>>>>>>> Stashed changes

    /// <summary>
    /// Gets the unique identifier for this transaction.
    /// </summary>
    public Guid TransactionId { get; }

    /// <summary>
    /// Gets the snapshot timestamp captured at transaction begin.
    /// All reads see data committed before this timestamp.
    /// </summary>
    public ClockSiTimestamp SnapshotTime { get; private set; }

    /// <summary>
    /// Gets the commit timestamp assigned when the transaction commits.
    /// </summary>
    public ClockSiTimestamp CommitTime { get; private set; }

    /// <summary>
    /// Gets the current transaction state.
    /// </summary>
    public TransactionState State
    {
        get
        {
            _lock.Wait();
            try { return _state; }
            finally { _lock.Release(); }
        }
    }

    /// <summary>
    /// Gets the buffered write set.
    /// </summary>
    public IReadOnlyList<WriteRecord> WriteSet
    {
        get
        {
            _lock.Wait();
            try { return _writeSet.ToArray(); }
            finally { _lock.Release(); }
        }
    }

    /// <summary>
    /// Initializes a new <see cref="ClockSiTransaction"/>.
    /// </summary>
    public ClockSiTransaction()
    {
        TransactionId = Guid.NewGuid();
        _state = TransactionState.Active;
    }

    /// <summary>
    /// Begins a snapshot by capturing the current timestamp.
    /// All subsequent reads will see data committed before this point.
    /// </summary>
    /// <returns>The snapshot timestamp.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the transaction is not active.</exception>
    public ClockSiTimestamp BeginSnapshot()
    {
        _lock.Wait();
        try
        {
            if (_state != TransactionState.Active)
                throw new InvalidOperationException($"Cannot begin snapshot on {_state} transaction.");

            SnapshotTime = ClockSiTimestamp.Now();
            return SnapshotTime;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Reads a key from the specified shard. Returns the latest committed version
    /// at or before the snapshot timestamp.
    /// </summary>
    /// <param name="key">The key to read.</param>
    /// <param name="shard">The shard index to read from.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The value, or null if not found at the snapshot time.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the transaction is not active.</exception>
    public async Task<long?> ReadAsync(byte[] key, IAdaptiveIndex shard, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(shard);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state != TransactionState.Active)
                throw new InvalidOperationException($"Cannot read from {_state} transaction.");

            // Check write set first (read-your-own-writes)
            for (int i = _writeSet.Count - 1; i >= 0; i--)
            {
                if (KeyEquals(_writeSet[i].Key, key))
                    return _writeSet[i].Value;
            }
        }
        finally
        {
            _lock.Release();
        }

        // Read from shard — returns latest committed version <= snapshot time
        return await shard.LookupAsync(key, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Buffers a write in the transaction's write set. The write is not applied until commit.
    /// </summary>
    /// <param name="key">The key to write.</param>
    /// <param name="value">The value to write.</param>
    /// <param name="targetShardId">The target shard for this write.</param>
    /// <exception cref="InvalidOperationException">Thrown if the transaction is not active.</exception>
    public void Write(byte[] key, long value, int targetShardId = 0)
    {
        ArgumentNullException.ThrowIfNull(key);

        _lock.Wait();
        try
        {
            if (_state != TransactionState.Active)
                throw new InvalidOperationException($"Cannot write to {_state} transaction.");

            _writeSet.Add(new WriteRecord(key, value, targetShardId));
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Commits the transaction: assigns a commit timestamp and applies all buffered writes.
    /// If any shard detects a write-write conflict, the transaction aborts.
    /// </summary>
    /// <param name="shards">The shard index instances for applying writes.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if committed successfully, false if aborted due to conflict.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the transaction is not active.</exception>
    public async Task<bool> CommitAsync(IReadOnlyList<IAdaptiveIndex> shards, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(shards);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state != TransactionState.Active)
                throw new InvalidOperationException($"Cannot commit {_state} transaction.");

            if (_writeSet.Count == 0)
            {
                CommitTime = ClockSiTimestamp.Now();
                _state = TransactionState.Committed;
                return true;
            }

            // Assign commit timestamp
            CommitTime = ClockSiTimestamp.Now();

            // Prepare phase: check for conflicts on each shard
            bool prepared = await PrepareAllAsync(shards, ct).ConfigureAwait(false);
            if (!prepared)
            {
                _state = TransactionState.Aborted;
                return false;
            }

            // Commit phase: apply all writes
            foreach (var write in _writeSet)
            {
                if (write.TargetShardId >= 0 && write.TargetShardId < shards.Count)
                {
                    var shard = shards[write.TargetShardId];
                    // Try update first; if key doesn't exist, insert
                    bool updated = await shard.UpdateAsync(write.Key, write.Value, ct).ConfigureAwait(false);
                    if (!updated)
                    {
                        await shard.InsertAsync(write.Key, write.Value, ct).ConfigureAwait(false);
                    }
                }
            }

            _state = TransactionState.Committed;
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Aborts the transaction, discarding the write set.
    /// </summary>
    public void Abort()
    {
        _lock.Wait();
        try
        {
            if (_state == TransactionState.Active)
            {
                _state = TransactionState.Aborted;
                _writeSet.Clear();
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Two-phase prepare: checks each shard for write-write conflicts.
    /// A conflict exists if the same key was committed by another transaction
    /// after our snapshot time.
    /// </summary>
    /// <param name="shards">The shard instances.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if all shards prepared successfully.</returns>
    public async Task<bool> PrepareAllAsync(IReadOnlyList<IAdaptiveIndex> shards, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(shards);

        // Group writes by shard
        var writesByShard = new Dictionary<int, List<WriteRecord>>();
        foreach (var write in _writeSet)
        {
            if (!writesByShard.TryGetValue(write.TargetShardId, out var list))
            {
                list = new List<WriteRecord>();
                writesByShard[write.TargetShardId] = list;
            }
            list.Add(write);
        }

        // Check each shard in parallel
        var tasks = new List<Task<bool>>();
        foreach (var (shardId, writes) in writesByShard)
        {
            if (shardId >= 0 && shardId < shards.Count)
            {
                tasks.Add(PrepareShardAsync(shards[shardId], writes, ct));
            }
        }

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        // All shards must succeed
        foreach (bool result in results)
        {
            if (!result) return false;
        }

        return true;
    }

    /// <summary>
    /// Prepares a single shard by checking if any written keys have been modified
    /// since the snapshot time by other transactions.
    /// </summary>
    private async Task<bool> PrepareShardAsync(
        IAdaptiveIndex shard,
        List<WriteRecord> writes,
        CancellationToken ct)
    {
        // For each key in the write set, check if the current value differs from
        // what we'd have read at snapshot time. If the shard has a newer version,
        // we have a write-write conflict.
        foreach (var write in writes)
        {
            var currentValue = await shard.LookupAsync(write.Key, ct).ConfigureAwait(false);
            // Conflict detection: if the current value differs from the snapshot value,
            // another transaction modified this key — write-write conflict.
            if (currentValue.HasValue && write.SnapshotValue.HasValue &&
                currentValue.Value != write.SnapshotValue.Value)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[ClockSiTransaction.PrepareShardAsync] Write-write conflict detected on key (snapshot={write.SnapshotValue}, current={currentValue})");
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Compares two byte array keys for equality.
    /// </summary>
    private static bool KeyEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        return a.AsSpan().SequenceEqual(b);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Cat 7 (finding 736): Interlocked.Exchange ensures exactly-once disposal even under
<<<<<<< Updated upstream
<<<<<<< Updated upstream
<<<<<<< Updated upstream
<<<<<<< Updated upstream
<<<<<<< Updated upstream
<<<<<<< Updated upstream
        // concurrent Dispose+Commit — the first caller wins, subsequent calls are safe no-ops.
=======
        // concurrent Dispose+Commit — the first caller wins, second call is a safe no-op.
>>>>>>> Stashed changes
=======
        // concurrent Dispose+Commit — the first caller wins, second call is a safe no-op.
>>>>>>> Stashed changes
=======
        // concurrent Dispose+Commit — the first caller wins, second call is a safe no-op.
>>>>>>> Stashed changes
=======
        // concurrent Dispose+Commit — the first caller wins, second call is a safe no-op.
>>>>>>> Stashed changes
=======
        // concurrent Dispose+Commit — the first caller wins, second call is a safe no-op.
>>>>>>> Stashed changes
=======
        // concurrent Dispose+Commit — the first caller wins, second call is a safe no-op.
>>>>>>> Stashed changes
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_state == TransactionState.Active)
        {
            _state = TransactionState.Aborted;
            _writeSet.Clear();
        }
        _lock.Dispose();
    }
}
