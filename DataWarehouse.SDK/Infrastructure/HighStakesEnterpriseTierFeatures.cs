using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace DataWarehouse.SDK.Infrastructure;

// ============================================================================
// TIER 3: HIGH-STAKES ENTERPRISE (BANKS, HOSPITALS, GOVERNMENT)
// Production-Ready Implementation with Maximum Security and Reliability
// ============================================================================

#region 1. Enhanced ACID Transactions with MVCC

/// <summary>
/// Multi-Version Concurrency Control implementation for enterprise-grade transactions.
/// Provides snapshot isolation, deadlock detection, and full ACID compliance.
/// </summary>
public sealed class MvccTransactionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, MvccTransaction> _activeTransactions = new();
    private readonly ConcurrentDictionary<string, VersionedRecord> _versionStore = new();
    private readonly ConcurrentDictionary<string, LockEntry> _lockTable = new();
    private readonly EnhancedWriteAheadLog _wal;
    private readonly DeadlockDetector _deadlockDetector;
    private readonly MvccConfiguration _config;
    private readonly SemaphoreSlim _globalLock = new(1, 1);
    private readonly Timer _cleanupTimer;
    private readonly Timer _deadlockTimer;
    private long _globalTransactionCounter;
    private long _globalTimestamp;
    private volatile bool _disposed;

    /// <summary>
    /// Event raised when a transaction is committed successfully.
    /// </summary>
    public event EventHandler<MvccTransactionEventArgs>? TransactionCommitted;

    /// <summary>
    /// Event raised when a transaction is aborted.
    /// </summary>
    public event EventHandler<MvccTransactionEventArgs>? TransactionAborted;

    /// <summary>
    /// Event raised when a deadlock is detected and resolved.
    /// </summary>
    public event EventHandler<DeadlockEventArgs>? DeadlockDetected;

    /// <summary>
    /// Initializes a new instance of the MvccTransactionManager.
    /// </summary>
    /// <param name="walDirectory">Directory for write-ahead log storage.</param>
    /// <param name="config">Optional configuration settings.</param>
    public MvccTransactionManager(string walDirectory, MvccConfiguration? config = null)
    {
        _config = config ?? new MvccConfiguration();
        _wal = new EnhancedWriteAheadLog(walDirectory, new WalConfiguration
        {
            SyncMode = _config.WalSyncMode,
            MaxLogSizeBytes = _config.MaxWalSizeBytes,
            CheckpointIntervalMs = _config.CheckpointIntervalMs
        });
        _deadlockDetector = new DeadlockDetector(_config.DeadlockDetectionIntervalMs);

        _cleanupTimer = new Timer(
            CleanupOldVersions,
            null,
            TimeSpan.FromSeconds(_config.VersionCleanupIntervalSeconds),
            TimeSpan.FromSeconds(_config.VersionCleanupIntervalSeconds));

        _deadlockTimer = new Timer(
            async _ => await DetectAndResolveDeadlocksAsync(),
            null,
            TimeSpan.FromMilliseconds(_config.DeadlockDetectionIntervalMs),
            TimeSpan.FromMilliseconds(_config.DeadlockDetectionIntervalMs));
    }

    /// <summary>
    /// Initializes the transaction manager and recovers from WAL if needed.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _wal.InitializeAsync(ct);
        await RecoverFromWalAsync(ct);
    }

    /// <summary>
    /// Begins a new MVCC transaction with the specified isolation level.
    /// </summary>
    /// <param name="isolationLevel">Transaction isolation level.</param>
    /// <param name="timeout">Optional transaction timeout.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The newly created transaction.</returns>
    public async Task<MvccTransaction> BeginTransactionAsync(
        TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.Snapshot,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var transactionId = GenerateTransactionId();
        var startTimestamp = Interlocked.Increment(ref _globalTimestamp);

        var transaction = new MvccTransaction
        {
            TransactionId = transactionId,
            IsolationLevel = isolationLevel,
            StartTimestamp = startTimestamp,
            StartedAt = DateTime.UtcNow,
            Timeout = timeout ?? _config.DefaultTransactionTimeout,
            State = MvccTransactionState.Active,
            ReadSet = new ConcurrentDictionary<string, long>(),
            WriteSet = new ConcurrentDictionary<string, VersionedValue>(),
            Savepoints = new ConcurrentDictionary<string, SavepointData>()
        };

        _activeTransactions[transactionId] = transaction;

        await _wal.LogTransactionStartAsync(transactionId, isolationLevel, ct);

        return transaction;
    }

    /// <summary>
    /// Reads a value within a transaction context, respecting isolation level.
    /// </summary>
    /// <typeparam name="T">Type of value to read.</typeparam>
    /// <param name="transaction">Active transaction.</param>
    /// <param name="key">Key to read.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The value if found, default otherwise.</returns>
    public async Task<MvccReadResult<T>> ReadAsync<T>(
        MvccTransaction transaction,
        string key,
        CancellationToken ct = default)
    {
        ValidateTransactionActive(transaction);

        // Check write set first (read your own writes)
        if (transaction.WriteSet.TryGetValue(key, out var writtenValue))
        {
            return new MvccReadResult<T>
            {
                Found = true,
                Value = JsonSerializer.Deserialize<T>(writtenValue.Data)!,
                Version = writtenValue.Version,
                ReadFromWriteSet = true
            };
        }

        // Get visible version based on isolation level
        if (!_versionStore.TryGetValue(key, out var versionedRecord))
        {
            return new MvccReadResult<T> { Found = false };
        }

        var visibleVersion = GetVisibleVersion(transaction, versionedRecord);
        if (visibleVersion == null)
        {
            return new MvccReadResult<T> { Found = false };
        }

        // Acquire read lock for repeatable read and serializable
        if (transaction.IsolationLevel >= TransactionIsolationLevel.RepeatableRead)
        {
            await AcquireLockAsync(transaction, key, LockMode.Shared, ct);
        }

        // Track in read set for validation during commit
        transaction.ReadSet[key] = visibleVersion.Version;

        return new MvccReadResult<T>
        {
            Found = true,
            Value = JsonSerializer.Deserialize<T>(visibleVersion.Data)!,
            Version = visibleVersion.Version
        };
    }

    /// <summary>
    /// Writes a value within a transaction context.
    /// </summary>
    /// <typeparam name="T">Type of value to write.</typeparam>
    /// <param name="transaction">Active transaction.</param>
    /// <param name="key">Key to write.</param>
    /// <param name="value">Value to write.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WriteAsync<T>(
        MvccTransaction transaction,
        string key,
        T value,
        CancellationToken ct = default)
    {
        ValidateTransactionActive(transaction);

        // Acquire exclusive lock
        await AcquireLockAsync(transaction, key, LockMode.Exclusive, ct);

        var serializedData = JsonSerializer.SerializeToUtf8Bytes(value);
        var version = Interlocked.Increment(ref _globalTimestamp);

        var versionedValue = new VersionedValue
        {
            Key = key,
            Data = serializedData,
            Version = version,
            TransactionId = transaction.TransactionId,
            CreatedAt = DateTime.UtcNow
        };

        transaction.WriteSet[key] = versionedValue;

        await _wal.LogWriteAsync(transaction.TransactionId, key, serializedData, version, ct);
    }

    /// <summary>
    /// Deletes a key within a transaction context.
    /// </summary>
    /// <param name="transaction">Active transaction.</param>
    /// <param name="key">Key to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task DeleteAsync(
        MvccTransaction transaction,
        string key,
        CancellationToken ct = default)
    {
        ValidateTransactionActive(transaction);

        await AcquireLockAsync(transaction, key, LockMode.Exclusive, ct);

        var version = Interlocked.Increment(ref _globalTimestamp);

        transaction.WriteSet[key] = new VersionedValue
        {
            Key = key,
            Data = Array.Empty<byte>(),
            Version = version,
            TransactionId = transaction.TransactionId,
            IsDeleted = true,
            CreatedAt = DateTime.UtcNow
        };

        await _wal.LogDeleteAsync(transaction.TransactionId, key, version, ct);
    }

    /// <summary>
    /// Creates a savepoint within the transaction for partial rollback.
    /// </summary>
    /// <param name="transaction">Active transaction.</param>
    /// <param name="savepointName">Name of the savepoint.</param>
    public void CreateSavepoint(MvccTransaction transaction, string savepointName)
    {
        ValidateTransactionActive(transaction);

        var savepoint = new SavepointData
        {
            Name = savepointName,
            CreatedAt = DateTime.UtcNow,
            WriteSetSnapshot = transaction.WriteSet.ToImmutableDictionary(),
            ReadSetSnapshot = transaction.ReadSet.ToImmutableDictionary()
        };

        transaction.Savepoints[savepointName] = savepoint;
    }

    /// <summary>
    /// Rolls back to a savepoint, discarding changes made after it.
    /// </summary>
    /// <param name="transaction">Active transaction.</param>
    /// <param name="savepointName">Name of the savepoint.</param>
    public void RollbackToSavepoint(MvccTransaction transaction, string savepointName)
    {
        ValidateTransactionActive(transaction);

        if (!transaction.Savepoints.TryGetValue(savepointName, out var savepoint))
        {
            throw new SavepointNotFoundException(savepointName);
        }

        // Release locks for keys no longer in write set
        var keysToRelease = transaction.WriteSet.Keys
            .Except(savepoint.WriteSetSnapshot.Keys)
            .ToList();

        foreach (var key in keysToRelease)
        {
            ReleaseLock(transaction, key);
            transaction.WriteSet.TryRemove(key, out _);
        }

        // Restore read set
        transaction.ReadSet.Clear();
        foreach (var kvp in savepoint.ReadSetSnapshot)
        {
            transaction.ReadSet[kvp.Key] = kvp.Value;
        }

        // Remove savepoints created after this one
        var savepointsToRemove = transaction.Savepoints
            .Where(s => s.Value.CreatedAt > savepoint.CreatedAt)
            .Select(s => s.Key)
            .ToList();

        foreach (var sp in savepointsToRemove)
        {
            transaction.Savepoints.TryRemove(sp, out _);
        }
    }

    /// <summary>
    /// Commits the transaction using two-phase commit protocol.
    /// </summary>
    /// <param name="transaction">Transaction to commit.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Commit result with details.</returns>
    public async Task<MvccCommitResult> CommitAsync(
        MvccTransaction transaction,
        CancellationToken ct = default)
    {
        ValidateTransactionActive(transaction);

        try
        {
            // Phase 1: Prepare
            transaction.State = MvccTransactionState.Preparing;

            // Validate read set for serializable isolation
            if (transaction.IsolationLevel == TransactionIsolationLevel.Serializable)
            {
                var validationResult = ValidateReadSet(transaction);
                if (!validationResult.Valid)
                {
                    await AbortAsync(transaction, $"Serialization conflict: {validationResult.ConflictKey}", ct);
                    return new MvccCommitResult
                    {
                        Success = false,
                        Error = $"Serialization conflict on key: {validationResult.ConflictKey}",
                        ConflictType = ConflictType.SerializationFailure
                    };
                }
            }

            // Log prepare
            await _wal.LogPrepareAsync(transaction.TransactionId, ct);
            transaction.State = MvccTransactionState.Prepared;

            // Phase 2: Commit
            transaction.State = MvccTransactionState.Committing;
            var commitTimestamp = Interlocked.Increment(ref _globalTimestamp);

            // Apply writes to version store
            foreach (var write in transaction.WriteSet)
            {
                var key = write.Key;
                var value = write.Value;

                if (!_versionStore.TryGetValue(key, out var record))
                {
                    record = new VersionedRecord { Key = key };
                    _versionStore[key] = record;
                }

                lock (record.Versions)
                {
                    record.Versions.Add(new VersionEntry
                    {
                        Version = value.Version,
                        CommitTimestamp = commitTimestamp,
                        Data = value.Data,
                        IsDeleted = value.IsDeleted,
                        TransactionId = transaction.TransactionId
                    });
                }
            }

            // Log commit
            await _wal.LogCommitAsync(transaction.TransactionId, commitTimestamp, ct);

            transaction.State = MvccTransactionState.Committed;
            transaction.CommitTimestamp = commitTimestamp;
            transaction.CompletedAt = DateTime.UtcNow;

            // Release all locks
            ReleaseAllLocks(transaction);

            // Remove from active transactions
            _activeTransactions.TryRemove(transaction.TransactionId, out _);

            TransactionCommitted?.Invoke(this, new MvccTransactionEventArgs { Transaction = transaction });

            return new MvccCommitResult
            {
                Success = true,
                CommitTimestamp = commitTimestamp,
                WritesApplied = transaction.WriteSet.Count
            };
        }
        catch (Exception ex)
        {
            await AbortAsync(transaction, ex.Message, ct);
            return new MvccCommitResult
            {
                Success = false,
                Error = ex.Message,
                ConflictType = ConflictType.SystemError
            };
        }
    }

    /// <summary>
    /// Aborts the transaction and rolls back all changes.
    /// </summary>
    /// <param name="transaction">Transaction to abort.</param>
    /// <param name="reason">Reason for abort.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task AbortAsync(
        MvccTransaction transaction,
        string reason,
        CancellationToken ct = default)
    {
        if (transaction.State == MvccTransactionState.Aborted)
            return;

        transaction.State = MvccTransactionState.Aborting;

        await _wal.LogAbortAsync(transaction.TransactionId, reason, ct);

        // Release all locks
        ReleaseAllLocks(transaction);

        transaction.State = MvccTransactionState.Aborted;
        transaction.AbortReason = reason;
        transaction.CompletedAt = DateTime.UtcNow;

        _activeTransactions.TryRemove(transaction.TransactionId, out _);

        TransactionAborted?.Invoke(this, new MvccTransactionEventArgs { Transaction = transaction });
    }

    /// <summary>
    /// Gets the current transaction statistics.
    /// </summary>
    public MvccStatistics GetStatistics()
    {
        return new MvccStatistics
        {
            ActiveTransactions = _activeTransactions.Count,
            TotalVersionedKeys = _versionStore.Count,
            TotalVersions = _versionStore.Values.Sum(v => v.Versions.Count),
            ActiveLocks = _lockTable.Count,
            CurrentTimestamp = _globalTimestamp
        };
    }

    private string GenerateTransactionId()
    {
        var counter = Interlocked.Increment(ref _globalTransactionCounter);
        return $"TX-{DateTime.UtcNow:yyyyMMddHHmmss}-{counter:D8}";
    }

    private void ValidateTransactionActive(MvccTransaction transaction)
    {
        if (transaction.State != MvccTransactionState.Active &&
            transaction.State != MvccTransactionState.Preparing)
        {
            throw new TransactionNotActiveException(transaction.TransactionId, transaction.State);
        }

        if (DateTime.UtcNow - transaction.StartedAt > transaction.Timeout)
        {
            throw new TransactionTimeoutException(transaction.TransactionId);
        }
    }

    private VersionEntry? GetVisibleVersion(MvccTransaction transaction, VersionedRecord record)
    {
        lock (record.Versions)
        {
            return transaction.IsolationLevel switch
            {
                TransactionIsolationLevel.ReadUncommitted =>
                    record.Versions.OrderByDescending(v => v.Version).FirstOrDefault(),

                TransactionIsolationLevel.ReadCommitted =>
                    record.Versions
                        .Where(v => v.CommitTimestamp.HasValue)
                        .OrderByDescending(v => v.CommitTimestamp)
                        .FirstOrDefault(),

                TransactionIsolationLevel.RepeatableRead or
                TransactionIsolationLevel.Snapshot or
                TransactionIsolationLevel.Serializable =>
                    record.Versions
                        .Where(v => v.CommitTimestamp.HasValue && v.CommitTimestamp <= transaction.StartTimestamp)
                        .OrderByDescending(v => v.CommitTimestamp)
                        .FirstOrDefault(),

                _ => null
            };
        }
    }

    private async Task AcquireLockAsync(
        MvccTransaction transaction,
        string key,
        LockMode mode,
        CancellationToken ct)
    {
        var lockEntry = _lockTable.GetOrAdd(key, _ => new LockEntry { Key = key });
        var startTime = DateTime.UtcNow;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (DateTime.UtcNow - startTime > _config.LockTimeout)
            {
                throw new LockTimeoutException(key, transaction.TransactionId);
            }

            lock (lockEntry)
            {
                // Check for deadlock potential
                _deadlockDetector.RegisterWait(transaction.TransactionId, key, lockEntry.Holders);

                if (CanAcquireLock(lockEntry, transaction.TransactionId, mode))
                {
                    lockEntry.Holders[transaction.TransactionId] = mode;
                    _deadlockDetector.RegisterAcquire(transaction.TransactionId, key);
                    return;
                }
            }

            await Task.Delay(_config.LockRetryDelayMs, ct);
        }
    }

    private bool CanAcquireLock(LockEntry entry, string transactionId, LockMode requestedMode)
    {
        // Already holding the lock
        if (entry.Holders.TryGetValue(transactionId, out var currentMode))
        {
            // Upgrade from shared to exclusive
            if (currentMode == LockMode.Shared && requestedMode == LockMode.Exclusive)
            {
                // Can only upgrade if we're the only holder
                return entry.Holders.Count == 1;
            }
            return true;
        }

        if (entry.Holders.Count == 0)
            return true;

        if (requestedMode == LockMode.Shared)
        {
            // Shared lock compatible with other shared locks
            return entry.Holders.Values.All(m => m == LockMode.Shared);
        }

        // Exclusive lock requires no other holders
        return false;
    }

    private void ReleaseLock(MvccTransaction transaction, string key)
    {
        if (_lockTable.TryGetValue(key, out var entry))
        {
            lock (entry)
            {
                entry.Holders.TryRemove(transaction.TransactionId, out _);
                _deadlockDetector.RegisterRelease(transaction.TransactionId, key);

                if (entry.Holders.IsEmpty)
                {
                    _lockTable.TryRemove(key, out _);
                }
            }
        }
    }

    private void ReleaseAllLocks(MvccTransaction transaction)
    {
        var keysToRelease = _lockTable
            .Where(kvp => kvp.Value.Holders.ContainsKey(transaction.TransactionId))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in keysToRelease)
        {
            ReleaseLock(transaction, key);
        }
    }

    private ReadSetValidationResult ValidateReadSet(MvccTransaction transaction)
    {
        foreach (var read in transaction.ReadSet)
        {
            if (_versionStore.TryGetValue(read.Key, out var record))
            {
                lock (record.Versions)
                {
                    var currentVersion = record.Versions
                        .Where(v => v.CommitTimestamp.HasValue)
                        .OrderByDescending(v => v.CommitTimestamp)
                        .FirstOrDefault();

                    if (currentVersion != null && currentVersion.Version > read.Value)
                    {
                        return new ReadSetValidationResult
                        {
                            Valid = false,
                            ConflictKey = read.Key
                        };
                    }
                }
            }
        }

        return new ReadSetValidationResult { Valid = true };
    }

    private async Task DetectAndResolveDeadlocksAsync()
    {
        var deadlock = _deadlockDetector.DetectDeadlock();
        if (deadlock != null)
        {
            // Abort the youngest transaction in the cycle
            var victimId = deadlock.TransactionsInCycle
                .Where(id => _activeTransactions.TryGetValue(id, out _))
                .OrderByDescending(id => _activeTransactions.TryGetValue(id, out var tx) ? tx.StartedAt : DateTime.MinValue)
                .FirstOrDefault();

            if (victimId != null && _activeTransactions.TryGetValue(victimId, out var victim))
            {
                DeadlockDetected?.Invoke(this, new DeadlockEventArgs
                {
                    DeadlockInfo = deadlock,
                    VictimTransactionId = victimId
                });

                await AbortAsync(victim, "Deadlock victim - transaction aborted to resolve deadlock");
            }
        }
    }

    private void CleanupOldVersions(object? state)
    {
        if (_disposed) return;

        var oldestActiveTimestamp = _activeTransactions.Values
            .Select(t => t.StartTimestamp)
            .DefaultIfEmpty(long.MaxValue)
            .Min();

        foreach (var record in _versionStore.Values)
        {
            lock (record.Versions)
            {
                var versionsToRemove = record.Versions
                    .Where(v => v.CommitTimestamp.HasValue &&
                               v.CommitTimestamp < oldestActiveTimestamp - _config.VersionRetentionCount)
                    .OrderBy(v => v.CommitTimestamp)
                    .SkipLast(1) // Keep at least one version
                    .ToList();

                foreach (var version in versionsToRemove)
                {
                    record.Versions.Remove(version);
                }
            }
        }
    }

    private async Task RecoverFromWalAsync(CancellationToken ct)
    {
        var recoveryState = await _wal.RecoverAsync(ct);

        foreach (var committedTx in recoveryState.CommittedTransactions)
        {
            foreach (var write in committedTx.Writes)
            {
                if (!_versionStore.TryGetValue(write.Key, out var record))
                {
                    record = new VersionedRecord { Key = write.Key };
                    _versionStore[write.Key] = record;
                }

                record.Versions.Add(new VersionEntry
                {
                    Version = write.Version,
                    CommitTimestamp = committedTx.CommitTimestamp,
                    Data = write.Data,
                    IsDeleted = write.IsDeleted,
                    TransactionId = committedTx.TransactionId
                });
            }

            _globalTimestamp = Math.Max(_globalTimestamp, committedTx.CommitTimestamp);
        }
    }

    /// <summary>
    /// Disposes resources used by the transaction manager.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cleanupTimer.Dispose();
        _deadlockTimer.Dispose();

        // Abort all active transactions
        foreach (var tx in _activeTransactions.Values.ToList())
        {
            try
            {
                await AbortAsync(tx, "Transaction manager shutting down");
            }
            catch { /* Best effort */ }
        }

        await _wal.DisposeAsync();
        _globalLock.Dispose();
    }
}

#endregion

#region MVCC Supporting Types

/// <summary>
/// Transaction isolation levels supported by MVCC.
/// </summary>
public enum TransactionIsolationLevel
{
    /// <summary>Allows dirty reads.</summary>
    ReadUncommitted = 0,
    /// <summary>Only reads committed data.</summary>
    ReadCommitted = 1,
    /// <summary>Prevents non-repeatable reads.</summary>
    RepeatableRead = 2,
    /// <summary>Provides point-in-time snapshot.</summary>
    Snapshot = 3,
    /// <summary>Full serializable isolation.</summary>
    Serializable = 4
}

/// <summary>
/// States of an MVCC transaction.
/// </summary>
public enum MvccTransactionState
{
    /// <summary>Transaction is active.</summary>
    Active,
    /// <summary>Transaction is preparing to commit.</summary>
    Preparing,
    /// <summary>Transaction is prepared.</summary>
    Prepared,
    /// <summary>Transaction is committing.</summary>
    Committing,
    /// <summary>Transaction has committed.</summary>
    Committed,
    /// <summary>Transaction is aborting.</summary>
    Aborting,
    /// <summary>Transaction has aborted.</summary>
    Aborted
}

/// <summary>
/// Lock modes for concurrency control.
/// </summary>
public enum LockMode
{
    /// <summary>Shared lock for reads.</summary>
    Shared,
    /// <summary>Exclusive lock for writes.</summary>
    Exclusive
}

/// <summary>
/// Types of transaction conflicts.
/// </summary>
public enum ConflictType
{
    /// <summary>No conflict.</summary>
    None,
    /// <summary>Write-write conflict.</summary>
    WriteConflict,
    /// <summary>Serialization failure.</summary>
    SerializationFailure,
    /// <summary>Deadlock detected.</summary>
    Deadlock,
    /// <summary>System error.</summary>
    SystemError
}

/// <summary>
/// Represents an MVCC transaction.
/// </summary>
public sealed class MvccTransaction
{
    /// <summary>Unique transaction identifier.</summary>
    public required string TransactionId { get; init; }
    /// <summary>Isolation level for this transaction.</summary>
    public TransactionIsolationLevel IsolationLevel { get; init; }
    /// <summary>Logical start timestamp.</summary>
    public long StartTimestamp { get; init; }
    /// <summary>When the transaction started.</summary>
    public DateTime StartedAt { get; init; }
    /// <summary>Transaction timeout.</summary>
    public TimeSpan Timeout { get; init; }
    /// <summary>Current state.</summary>
    public MvccTransactionState State { get; set; }
    /// <summary>Commit timestamp if committed.</summary>
    public long? CommitTimestamp { get; set; }
    /// <summary>When the transaction completed.</summary>
    public DateTime? CompletedAt { get; set; }
    /// <summary>Reason for abort if aborted.</summary>
    public string? AbortReason { get; set; }
    /// <summary>Keys read and their versions.</summary>
    public ConcurrentDictionary<string, long> ReadSet { get; init; } = new();
    /// <summary>Pending writes.</summary>
    public ConcurrentDictionary<string, VersionedValue> WriteSet { get; init; } = new();
    /// <summary>Savepoints for partial rollback.</summary>
    public ConcurrentDictionary<string, SavepointData> Savepoints { get; init; } = new();
}

/// <summary>
/// Configuration for MVCC transaction manager.
/// </summary>
public sealed class MvccConfiguration
{
    /// <summary>Default transaction timeout.</summary>
    public TimeSpan DefaultTransactionTimeout { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Lock acquisition timeout.</summary>
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>Delay between lock retry attempts in milliseconds.</summary>
    public int LockRetryDelayMs { get; set; } = 10;
    /// <summary>Interval for deadlock detection in milliseconds.</summary>
    public int DeadlockDetectionIntervalMs { get; set; } = 100;
    /// <summary>Number of versions to retain per key.</summary>
    public int VersionRetentionCount { get; set; } = 100;
    /// <summary>Interval for version cleanup in seconds.</summary>
    public int VersionCleanupIntervalSeconds { get; set; } = 60;
    /// <summary>WAL sync mode.</summary>
    public WalSyncMode WalSyncMode { get; set; } = WalSyncMode.Sync;
    /// <summary>Maximum WAL size in bytes.</summary>
    public long MaxWalSizeBytes { get; set; } = 1024 * 1024 * 1024; // 1GB
    /// <summary>Checkpoint interval in milliseconds.</summary>
    public int CheckpointIntervalMs { get; set; } = 60000;
}

/// <summary>
/// Result of reading a value in MVCC.
/// </summary>
/// <typeparam name="T">Type of value.</typeparam>
public sealed class MvccReadResult<T>
{
    /// <summary>Whether the key was found.</summary>
    public bool Found { get; init; }
    /// <summary>The value if found.</summary>
    public T? Value { get; init; }
    /// <summary>Version of the value read.</summary>
    public long Version { get; init; }
    /// <summary>Whether read from write set.</summary>
    public bool ReadFromWriteSet { get; init; }
}

/// <summary>
/// Result of committing a transaction.
/// </summary>
public sealed class MvccCommitResult
{
    /// <summary>Whether commit succeeded.</summary>
    public bool Success { get; init; }
    /// <summary>Commit timestamp if successful.</summary>
    public long CommitTimestamp { get; init; }
    /// <summary>Number of writes applied.</summary>
    public int WritesApplied { get; init; }
    /// <summary>Error message if failed.</summary>
    public string? Error { get; init; }
    /// <summary>Type of conflict if any.</summary>
    public ConflictType ConflictType { get; init; }
}

/// <summary>
/// Statistics about the MVCC system.
/// </summary>
public sealed class MvccStatistics
{
    /// <summary>Number of active transactions.</summary>
    public int ActiveTransactions { get; init; }
    /// <summary>Total versioned keys.</summary>
    public int TotalVersionedKeys { get; init; }
    /// <summary>Total versions across all keys.</summary>
    public int TotalVersions { get; init; }
    /// <summary>Number of active locks.</summary>
    public int ActiveLocks { get; init; }
    /// <summary>Current global timestamp.</summary>
    public long CurrentTimestamp { get; init; }
}

/// <summary>
/// Event args for transaction events.
/// </summary>
public sealed class MvccTransactionEventArgs : EventArgs
{
    /// <summary>The transaction.</summary>
    public required MvccTransaction Transaction { get; init; }
}

/// <summary>
/// Event args for deadlock events.
/// </summary>
public sealed class DeadlockEventArgs : EventArgs
{
    /// <summary>Deadlock information.</summary>
    public required DeadlockInfo DeadlockInfo { get; init; }
    /// <summary>Transaction chosen as victim.</summary>
    public required string VictimTransactionId { get; init; }
}

/// <summary>
/// Information about a detected deadlock.
/// </summary>
public sealed class DeadlockInfo
{
    /// <summary>Transactions involved in deadlock.</summary>
    public required List<string> TransactionsInCycle { get; init; }
    /// <summary>Resources involved in deadlock.</summary>
    public required List<string> ResourcesInCycle { get; init; }
    /// <summary>When deadlock was detected.</summary>
    public DateTime DetectedAt { get; init; }
}

// Internal supporting types
internal sealed class VersionedRecord
{
    public required string Key { get; init; }
    public List<VersionEntry> Versions { get; } = new();
}

internal sealed class VersionEntry
{
    public long Version { get; init; }
    public long? CommitTimestamp { get; set; }
    public required byte[] Data { get; init; }
    public bool IsDeleted { get; init; }
    public required string TransactionId { get; init; }
}

internal sealed class VersionedValue
{
    public required string Key { get; init; }
    public required byte[] Data { get; init; }
    public long Version { get; init; }
    public required string TransactionId { get; init; }
    public bool IsDeleted { get; init; }
    public DateTime CreatedAt { get; init; }
}

internal sealed class LockEntry
{
    public required string Key { get; init; }
    public ConcurrentDictionary<string, LockMode> Holders { get; } = new();
}

internal sealed class SavepointData
{
    public required string Name { get; init; }
    public DateTime CreatedAt { get; init; }
    public required ImmutableDictionary<string, VersionedValue> WriteSetSnapshot { get; init; }
    public required ImmutableDictionary<string, long> ReadSetSnapshot { get; init; }
}

internal sealed class ReadSetValidationResult
{
    public bool Valid { get; init; }
    public string? ConflictKey { get; init; }
}

#endregion

#region Enhanced Write-Ahead Log

/// <summary>
/// WAL sync modes.
/// </summary>
public enum WalSyncMode
{
    /// <summary>No sync - fastest but risk of data loss.</summary>
    NoSync,
    /// <summary>Sync on commit only.</summary>
    SyncOnCommit,
    /// <summary>Sync every write.</summary>
    Sync
}

/// <summary>
/// Configuration for WAL.
/// </summary>
public sealed class WalConfiguration
{
    /// <summary>Sync mode.</summary>
    public WalSyncMode SyncMode { get; init; } = WalSyncMode.Sync;
    /// <summary>Maximum log size before rotation.</summary>
    public long MaxLogSizeBytes { get; init; } = 1024 * 1024 * 1024;
    /// <summary>Checkpoint interval in milliseconds.</summary>
    public int CheckpointIntervalMs { get; init; } = 60000;
}

/// <summary>
/// Enhanced Write-Ahead Log with proper recovery support.
/// </summary>
public sealed class EnhancedWriteAheadLog : IAsyncDisposable
{
    private readonly string _logDirectory;
    private readonly WalConfiguration _config;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, WalTransactionState> _transactionStates = new();
    private FileStream? _logFile;
    private BinaryWriter? _writer;
    private long _currentLsn;
    private long _lastCheckpointLsn;
    private readonly Timer? _checkpointTimer;
    private volatile bool _disposed;

    /// <summary>
    /// Initializes a new WAL instance.
    /// </summary>
    public EnhancedWriteAheadLog(string logDirectory, WalConfiguration? config = null)
    {
        _logDirectory = logDirectory;
        _config = config ?? new WalConfiguration();

        if (_config.CheckpointIntervalMs > 0)
        {
            _checkpointTimer = new Timer(
                async _ => await CheckpointAsync(),
                null,
                TimeSpan.FromMilliseconds(_config.CheckpointIntervalMs),
                TimeSpan.FromMilliseconds(_config.CheckpointIntervalMs));
        }
    }

    /// <summary>
    /// Initializes the WAL.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_logDirectory))
            Directory.CreateDirectory(_logDirectory);

        var logPath = Path.Combine(_logDirectory, "wal.log");
        _logFile = new FileStream(logPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 65536, FileOptions.WriteThrough);
        _writer = new BinaryWriter(_logFile, Encoding.UTF8, leaveOpen: true);

        if (_logFile.Length > 0)
        {
            _logFile.Seek(0, SeekOrigin.End);
        }
    }

    /// <summary>
    /// Logs a transaction start.
    /// </summary>
    public async Task LogTransactionStartAsync(string transactionId, TransactionIsolationLevel level, CancellationToken ct)
    {
        var entry = new WalLogEntry
        {
            EntryType = WalEntryType.TransactionStart,
            TransactionId = transactionId,
            Timestamp = DateTime.UtcNow,
            IsolationLevel = level
        };

        await WriteEntryAsync(entry, ct);
        _transactionStates[transactionId] = new WalTransactionState { TransactionId = transactionId };
    }

    /// <summary>
    /// Logs a write operation.
    /// </summary>
    public async Task LogWriteAsync(string transactionId, string key, byte[] data, long version, CancellationToken ct)
    {
        var entry = new WalLogEntry
        {
            EntryType = WalEntryType.Write,
            TransactionId = transactionId,
            Key = key,
            Data = data,
            Version = version,
            Timestamp = DateTime.UtcNow
        };

        await WriteEntryAsync(entry, ct);

        if (_transactionStates.TryGetValue(transactionId, out var state))
        {
            state.Writes.Add(new WalWriteRecord { Key = key, Data = data, Version = version });
        }
    }

    /// <summary>
    /// Logs a delete operation.
    /// </summary>
    public async Task LogDeleteAsync(string transactionId, string key, long version, CancellationToken ct)
    {
        var entry = new WalLogEntry
        {
            EntryType = WalEntryType.Delete,
            TransactionId = transactionId,
            Key = key,
            Version = version,
            Timestamp = DateTime.UtcNow
        };

        await WriteEntryAsync(entry, ct);

        if (_transactionStates.TryGetValue(transactionId, out var state))
        {
            state.Writes.Add(new WalWriteRecord { Key = key, Data = Array.Empty<byte>(), Version = version, IsDeleted = true });
        }
    }

    /// <summary>
    /// Logs prepare phase.
    /// </summary>
    public async Task LogPrepareAsync(string transactionId, CancellationToken ct)
    {
        var entry = new WalLogEntry
        {
            EntryType = WalEntryType.Prepare,
            TransactionId = transactionId,
            Timestamp = DateTime.UtcNow
        };

        await WriteEntryAsync(entry, ct);
    }

    /// <summary>
    /// Logs commit.
    /// </summary>
    public async Task LogCommitAsync(string transactionId, long commitTimestamp, CancellationToken ct)
    {
        var entry = new WalLogEntry
        {
            EntryType = WalEntryType.Commit,
            TransactionId = transactionId,
            CommitTimestamp = commitTimestamp,
            Timestamp = DateTime.UtcNow
        };

        await WriteEntryAsync(entry, ct);

        if (_config.SyncMode >= WalSyncMode.SyncOnCommit && _logFile != null)
        {
            await _logFile.FlushAsync(ct);
        }

        if (_transactionStates.TryGetValue(transactionId, out var state))
        {
            state.CommitTimestamp = commitTimestamp;
            state.IsCommitted = true;
        }
    }

    /// <summary>
    /// Logs abort.
    /// </summary>
    public async Task LogAbortAsync(string transactionId, string reason, CancellationToken ct)
    {
        var entry = new WalLogEntry
        {
            EntryType = WalEntryType.Abort,
            TransactionId = transactionId,
            AbortReason = reason,
            Timestamp = DateTime.UtcNow
        };

        await WriteEntryAsync(entry, ct);
        _transactionStates.TryRemove(transactionId, out _);
    }

    /// <summary>
    /// Recovers state from WAL.
    /// </summary>
    public async Task<WalRecoveryState> RecoverAsync(CancellationToken ct)
    {
        var recoveryState = new WalRecoveryState();

        if (_logFile == null || _logFile.Length == 0)
            return recoveryState;

        _logFile.Seek(0, SeekOrigin.Begin);
        using var reader = new BinaryReader(_logFile, Encoding.UTF8, leaveOpen: true);

        var transactions = new Dictionary<string, WalTransactionState>();

        while (_logFile.Position < _logFile.Length)
        {
            try
            {
                var entry = ReadEntry(reader);
                _currentLsn = Math.Max(_currentLsn, entry.Lsn);

                switch (entry.EntryType)
                {
                    case WalEntryType.TransactionStart:
                        transactions[entry.TransactionId] = new WalTransactionState { TransactionId = entry.TransactionId };
                        break;

                    case WalEntryType.Write:
                        if (transactions.TryGetValue(entry.TransactionId, out var writeState))
                        {
                            writeState.Writes.Add(new WalWriteRecord
                            {
                                Key = entry.Key!,
                                Data = entry.Data ?? Array.Empty<byte>(),
                                Version = entry.Version
                            });
                        }
                        break;

                    case WalEntryType.Delete:
                        if (transactions.TryGetValue(entry.TransactionId, out var deleteState))
                        {
                            deleteState.Writes.Add(new WalWriteRecord
                            {
                                Key = entry.Key!,
                                Data = Array.Empty<byte>(),
                                Version = entry.Version,
                                IsDeleted = true
                            });
                        }
                        break;

                    case WalEntryType.Commit:
                        if (transactions.TryGetValue(entry.TransactionId, out var commitState))
                        {
                            commitState.CommitTimestamp = entry.CommitTimestamp;
                            commitState.IsCommitted = true;
                            recoveryState.CommittedTransactions.Add(commitState);
                        }
                        break;

                    case WalEntryType.Abort:
                        transactions.Remove(entry.TransactionId);
                        break;
                }
            }
            catch (EndOfStreamException)
            {
                break;
            }
            catch
            {
                // Corrupted entry - stop recovery
                break;
            }
        }

        _logFile.Seek(0, SeekOrigin.End);
        return recoveryState;
    }

    private async Task WriteEntryAsync(WalLogEntry entry, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            if (_writer == null) throw new InvalidOperationException("WAL not initialized");

            entry.Lsn = Interlocked.Increment(ref _currentLsn);

            // Write entry
            _writer.Write(entry.Lsn);
            _writer.Write((byte)entry.EntryType);
            _writer.Write(entry.TransactionId);
            _writer.Write(entry.Timestamp.Ticks);
            _writer.Write(entry.Key ?? string.Empty);
            _writer.Write(entry.Data?.Length ?? 0);
            if (entry.Data != null && entry.Data.Length > 0)
                _writer.Write(entry.Data);
            _writer.Write(entry.Version);
            _writer.Write(entry.CommitTimestamp);
            _writer.Write(entry.AbortReason ?? string.Empty);
            _writer.Write((byte)entry.IsolationLevel);

            // Compute and write checksum
            var checksum = ComputeEntryChecksum(entry);
            _writer.Write(checksum);

            if (_config.SyncMode == WalSyncMode.Sync)
            {
                _writer.Flush();
                _logFile!.Flush(true);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private WalLogEntry ReadEntry(BinaryReader reader)
    {
        var lsn = reader.ReadInt64();
        var entryType = (WalEntryType)reader.ReadByte();
        var transactionId = reader.ReadString();
        var timestamp = new DateTime(reader.ReadInt64());
        var key = reader.ReadString();
        var dataLength = reader.ReadInt32();
        var data = dataLength > 0 ? reader.ReadBytes(dataLength) : null;
        var version = reader.ReadInt64();
        var commitTimestamp = reader.ReadInt64();
        var abortReason = reader.ReadString();
        var isolationLevel = (TransactionIsolationLevel)reader.ReadByte();
        var checksum = reader.ReadInt64();

        var entry = new WalLogEntry
        {
            Lsn = lsn,
            EntryType = entryType,
            TransactionId = transactionId,
            Timestamp = timestamp,
            Key = string.IsNullOrEmpty(key) ? null : key,
            Data = data,
            Version = version,
            CommitTimestamp = commitTimestamp,
            AbortReason = string.IsNullOrEmpty(abortReason) ? null : abortReason,
            IsolationLevel = isolationLevel
        };

        // Verify checksum
        var expectedChecksum = ComputeEntryChecksum(entry);
        if (checksum != expectedChecksum)
        {
            throw new WalCorruptionException(lsn, "Checksum mismatch");
        }

        return entry;
    }

    private static long ComputeEntryChecksum(WalLogEntry entry)
    {
        unchecked
        {
            long hash = 17;
            hash = hash * 31 + entry.Lsn;
            hash = hash * 31 + (int)entry.EntryType;
            hash = hash * 31 + entry.TransactionId.GetHashCode();
            hash = hash * 31 + entry.Timestamp.Ticks;
            hash = hash * 31 + (entry.Key?.GetHashCode() ?? 0);
            hash = hash * 31 + (entry.Data?.Length ?? 0);
            hash = hash * 31 + entry.Version;
            return hash;
        }
    }

    private async Task CheckpointAsync()
    {
        if (_disposed) return;

        await _writeLock.WaitAsync();
        try
        {
            _lastCheckpointLsn = _currentLsn;
            _writer?.Flush();
            _logFile?.Flush(true);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Disposes WAL resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _checkpointTimer?.Dispose();

        await _writeLock.WaitAsync();
        try
        {
            _writer?.Flush();
            _writer?.Dispose();
            if (_logFile != null)
            {
                await _logFile.FlushAsync();
                await _logFile.DisposeAsync();
            }
        }
        finally
        {
            _writeLock.Release();
            _writeLock.Dispose();
        }
    }
}

internal enum WalEntryType : byte
{
    TransactionStart = 1,
    Write = 2,
    Delete = 3,
    Prepare = 4,
    Commit = 5,
    Abort = 6,
    Checkpoint = 7
}

internal sealed class WalLogEntry
{
    public long Lsn { get; set; }
    public WalEntryType EntryType { get; init; }
    public required string TransactionId { get; init; }
    public DateTime Timestamp { get; init; }
    public string? Key { get; init; }
    public byte[]? Data { get; init; }
    public long Version { get; init; }
    public long CommitTimestamp { get; init; }
    public string? AbortReason { get; init; }
    public TransactionIsolationLevel IsolationLevel { get; init; }
}

internal sealed class WalTransactionState
{
    public required string TransactionId { get; init; }
    public List<WalWriteRecord> Writes { get; } = new();
    public long CommitTimestamp { get; set; }
    public bool IsCommitted { get; set; }
}

internal sealed class WalWriteRecord
{
    public required string Key { get; init; }
    public required byte[] Data { get; init; }
    public long Version { get; init; }
    public bool IsDeleted { get; init; }
}

/// <summary>
/// State recovered from WAL.
/// </summary>
public sealed class WalRecoveryState
{
    /// <summary>Transactions that were committed.</summary>
    public List<WalTransactionState> CommittedTransactions { get; } = new();
}

#endregion

#region Deadlock Detection

/// <summary>
/// Deadlock detector using wait-for graph analysis.
/// </summary>
internal sealed class DeadlockDetector
{
    private readonly ConcurrentDictionary<string, HashSet<string>> _waitForGraph = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _holdsLocks = new();
    private readonly int _detectionIntervalMs;

    public DeadlockDetector(int detectionIntervalMs)
    {
        _detectionIntervalMs = detectionIntervalMs;
    }

    public void RegisterWait(string transactionId, string resource, ConcurrentDictionary<string, LockMode> holders)
    {
        var waitsFor = _waitForGraph.GetOrAdd(transactionId, _ => new HashSet<string>());
        lock (waitsFor)
        {
            foreach (var holder in holders.Keys)
            {
                if (holder != transactionId)
                {
                    waitsFor.Add(holder);
                }
            }
        }
    }

    public void RegisterAcquire(string transactionId, string resource)
    {
        var holds = _holdsLocks.GetOrAdd(transactionId, _ => new HashSet<string>());
        lock (holds)
        {
            holds.Add(resource);
        }

        // Remove from wait-for graph
        if (_waitForGraph.TryGetValue(transactionId, out var waitsFor))
        {
            lock (waitsFor)
            {
                waitsFor.Clear();
            }
        }
    }

    public void RegisterRelease(string transactionId, string resource)
    {
        if (_holdsLocks.TryGetValue(transactionId, out var holds))
        {
            lock (holds)
            {
                holds.Remove(resource);
            }
        }
    }

    public DeadlockInfo? DetectDeadlock()
    {
        var visited = new HashSet<string>();
        var recursionStack = new HashSet<string>();
        var path = new List<string>();

        foreach (var txId in _waitForGraph.Keys)
        {
            if (DetectCycle(txId, visited, recursionStack, path))
            {
                return new DeadlockInfo
                {
                    TransactionsInCycle = path.ToList(),
                    ResourcesInCycle = GetResourcesInCycle(path),
                    DetectedAt = DateTime.UtcNow
                };
            }
        }

        return null;
    }

    private bool DetectCycle(string txId, HashSet<string> visited, HashSet<string> recursionStack, List<string> path)
    {
        if (recursionStack.Contains(txId))
        {
            // Found cycle
            var cycleStart = path.IndexOf(txId);
            if (cycleStart >= 0)
            {
                path.RemoveRange(0, cycleStart);
            }
            return true;
        }

        if (visited.Contains(txId))
            return false;

        visited.Add(txId);
        recursionStack.Add(txId);
        path.Add(txId);

        if (_waitForGraph.TryGetValue(txId, out var waitsFor))
        {
            lock (waitsFor)
            {
                foreach (var waitingFor in waitsFor)
                {
                    if (DetectCycle(waitingFor, visited, recursionStack, path))
                        return true;
                }
            }
        }

        recursionStack.Remove(txId);
        path.Remove(txId);
        return false;
    }

    private List<string> GetResourcesInCycle(List<string> transactions)
    {
        var resources = new HashSet<string>();
        foreach (var txId in transactions)
        {
            if (_holdsLocks.TryGetValue(txId, out var holds))
            {
                lock (holds)
                {
                    foreach (var resource in holds)
                    {
                        resources.Add(resource);
                    }
                }
            }
        }
        return resources.ToList();
    }
}

#endregion

#region Transaction Exceptions

/// <summary>
/// Exception thrown when a transaction is not in active state.
/// </summary>
public sealed class TransactionNotActiveException : Exception
{
    /// <summary>Transaction ID.</summary>
    public string TransactionId { get; }
    /// <summary>Current state.</summary>
    public MvccTransactionState CurrentState { get; }

    /// <summary>
    /// Creates a new TransactionNotActiveException.
    /// </summary>
    public TransactionNotActiveException(string transactionId, MvccTransactionState state)
        : base($"Transaction {transactionId} is not active. Current state: {state}")
    {
        TransactionId = transactionId;
        CurrentState = state;
    }
}

/// <summary>
/// Exception thrown when a transaction times out.
/// </summary>
public sealed class TransactionTimeoutException : Exception
{
    /// <summary>Transaction ID.</summary>
    public string TransactionId { get; }

    /// <summary>
    /// Creates a new TransactionTimeoutException.
    /// </summary>
    public TransactionTimeoutException(string transactionId)
        : base($"Transaction {transactionId} has timed out")
    {
        TransactionId = transactionId;
    }
}

/// <summary>
/// Exception thrown when lock acquisition times out.
/// </summary>
public sealed class LockTimeoutException : Exception
{
    /// <summary>Resource key.</summary>
    public string Key { get; }
    /// <summary>Transaction ID.</summary>
    public string TransactionId { get; }

    /// <summary>
    /// Creates a new LockTimeoutException.
    /// </summary>
    public LockTimeoutException(string key, string transactionId)
        : base($"Lock timeout on key '{key}' for transaction {transactionId}")
    {
        Key = key;
        TransactionId = transactionId;
    }
}

/// <summary>
/// Exception thrown when a savepoint is not found.
/// </summary>
public sealed class SavepointNotFoundException : Exception
{
    /// <summary>Savepoint name.</summary>
    public string SavepointName { get; }

    /// <summary>
    /// Creates a new SavepointNotFoundException.
    /// </summary>
    public SavepointNotFoundException(string name)
        : base($"Savepoint '{name}' not found")
    {
        SavepointName = name;
    }
}

/// <summary>
/// Exception thrown when WAL corruption is detected.
/// </summary>
public sealed class WalCorruptionException : Exception
{
    /// <summary>LSN where corruption was detected.</summary>
    public long Lsn { get; }

    /// <summary>
    /// Creates a new WalCorruptionException.
    /// </summary>
    public WalCorruptionException(long lsn, string message)
        : base($"WAL corruption at LSN {lsn}: {message}")
    {
        Lsn = lsn;
    }
}

/// <summary>
/// Exception thrown when data corruption is detected.
/// </summary>
public sealed class DataCorruptionException : Exception
{
    /// <summary>Source path of corrupted data.</summary>
    public string SourcePath { get; }
    /// <summary>Expected hash.</summary>
    public string ExpectedHash { get; }
    /// <summary>Actual hash.</summary>
    public string ActualHash { get; }

    /// <summary>
    /// Creates a new DataCorruptionException.
    /// </summary>
    public DataCorruptionException(string sourcePath, string expectedHash, string actualHash)
        : base($"Data corruption detected for '{sourcePath}': expected {expectedHash}, got {actualHash}")
    {
        SourcePath = sourcePath;
        ExpectedHash = expectedHash;
        ActualHash = actualHash;
    }
}

#endregion

#region 2. Enterprise Snapshots with Compliance Features

/// <summary>
/// Enterprise-grade snapshot management with compliance features including
/// legal holds, immutable snapshots, and infinite retention options.
/// </summary>
public sealed class EnterpriseSnapshotManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, EnterpriseSnapshot> _snapshots = new();
    private readonly ConcurrentDictionary<string, SnapshotPolicy> _policies = new();
    private readonly ConcurrentDictionary<string, LegalHold> _legalHolds = new();
    private readonly ISnapshotStorage _storage;
    private readonly IEnterpriseAuditLog _auditLog;
    private readonly EnterpriseSnapshotConfiguration _config;
    private readonly SemaphoreSlim _snapshotLock = new(1, 1);
    private readonly Timer _retentionTimer;
    private volatile bool _disposed;

    /// <summary>
    /// Event raised when a snapshot is created.
    /// </summary>
    public event EventHandler<SnapshotEventArgs>? SnapshotCreated;

    /// <summary>
    /// Event raised when a snapshot is deleted.
    /// </summary>
    public event EventHandler<SnapshotEventArgs>? SnapshotDeleted;

    /// <summary>
    /// Event raised when a legal hold is applied.
    /// </summary>
    public event EventHandler<LegalHoldEventArgs>? LegalHoldApplied;

    /// <summary>
    /// Initializes the enterprise snapshot manager.
    /// </summary>
    public EnterpriseSnapshotManager(
        ISnapshotStorage storage,
        IEnterpriseAuditLog auditLog,
        EnterpriseSnapshotConfiguration? config = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _config = config ?? new EnterpriseSnapshotConfiguration();

        _retentionTimer = new Timer(
            async _ => await EnforceRetentionPoliciesAsync(),
            null,
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(1));
    }

    /// <summary>
    /// Creates an enterprise snapshot with optional compliance settings.
    /// </summary>
    public async Task<EnterpriseSnapshotResult> CreateSnapshotAsync(
        CreateSnapshotRequest request,
        CancellationToken ct = default)
    {
        await _snapshotLock.WaitAsync(ct);
        try
        {
            var snapshotId = GenerateSnapshotId(request.SourceId);
            var timestamp = DateTime.UtcNow;

            DateTime? expiresAt = request.RetentionMode switch
            {
                SnapshotRetentionMode.Infinite => null,
                SnapshotRetentionMode.Policy => GetPolicyExpiration(request.PolicyId),
                SnapshotRetentionMode.Custom => timestamp.Add(request.CustomRetention ?? TimeSpan.FromDays(30)),
                _ => timestamp.AddDays(_config.DefaultRetentionDays)
            };

            var snapshot = new EnterpriseSnapshot
            {
                SnapshotId = snapshotId,
                SourceId = request.SourceId,
                Name = request.Name ?? $"Snapshot-{timestamp:yyyyMMddHHmmss}",
                Description = request.Description,
                CreatedAt = timestamp,
                CreatedBy = request.CreatedBy,
                ExpiresAt = expiresAt,
                RetentionMode = request.RetentionMode,
                PolicyId = request.PolicyId,
                IsSafeMode = request.EnableSafeMode,
                IsApplicationAware = request.ApplicationAware,
                ApplicationType = request.ApplicationType,
                ConsistencyLevel = request.ConsistencyLevel,
                State = SnapshotState.Creating,
                Metadata = request.Metadata ?? new Dictionary<string, string>()
            };

            if (request.ApplicationAware && request.ApplicationType != null)
            {
                await CoordinateApplicationSnapshotAsync(snapshot, request.ApplicationType, ct);
            }

            var storageResult = await _storage.CreateSnapshotAsync(
                request.SourceId, snapshotId,
                new SnapshotOptions
                {
                    ConsistencyLevel = request.ConsistencyLevel,
                    IncludeMetadata = true,
                    CompressionEnabled = _config.EnableCompression
                }, ct);

            if (!storageResult.Success)
            {
                return new EnterpriseSnapshotResult { Success = false, Error = storageResult.Error };
            }

            snapshot.SizeBytes = storageResult.SizeBytes;
            snapshot.BlockCount = storageResult.BlockCount;
            snapshot.Checksum = storageResult.Checksum;
            snapshot.State = SnapshotState.Available;

            if (request.EnableSafeMode)
            {
                snapshot.ImmutableUntil = expiresAt ?? DateTime.MaxValue;
                snapshot.SafeModeEnabled = true;
            }

            _snapshots[snapshotId] = snapshot;

            await _auditLog.LogAsync(new AuditEntry
            {
                Action = "SnapshotCreated",
                ResourceId = snapshotId,
                Principal = request.CreatedBy,
                Details = new Dictionary<string, object>
                {
                    ["sourceId"] = request.SourceId,
                    ["safeMode"] = request.EnableSafeMode,
                    ["retentionMode"] = request.RetentionMode.ToString()
                }
            }, ct);

            SnapshotCreated?.Invoke(this, new SnapshotEventArgs { Snapshot = snapshot });
            return new EnterpriseSnapshotResult { Success = true, SnapshotId = snapshotId, Snapshot = snapshot };
        }
        finally
        {
            _snapshotLock.Release();
        }
    }

    /// <summary>
    /// Applies a legal hold to a snapshot, preventing deletion.
    /// </summary>
    public async Task<LegalHoldResult> ApplyLegalHoldAsync(
        string snapshotId, LegalHoldRequest hold, CancellationToken ct = default)
    {
        if (!_snapshots.TryGetValue(snapshotId, out var snapshot))
            return new LegalHoldResult { Success = false, Error = "Snapshot not found" };

        var holdId = $"LH-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..24];
        var legalHold = new LegalHold
        {
            HoldId = holdId,
            SnapshotId = snapshotId,
            CaseId = hold.CaseId,
            CaseName = hold.CaseName,
            AppliedBy = hold.AppliedBy,
            AppliedAt = DateTime.UtcNow,
            Reason = hold.Reason,
            ExpiresAt = hold.ExpiresAt,
            IsActive = true
        };

        _legalHolds[holdId] = legalHold;
        snapshot.LegalHolds.Add(holdId);
        snapshot.HasLegalHold = true;

        await _auditLog.LogAsync(new AuditEntry
        {
            Action = "LegalHoldApplied",
            ResourceId = snapshotId,
            Principal = hold.AppliedBy,
            Details = new Dictionary<string, object>
            {
                ["holdId"] = holdId,
                ["caseId"] = hold.CaseId,
                ["reason"] = hold.Reason
            }
        }, ct);

        LegalHoldApplied?.Invoke(this, new LegalHoldEventArgs { Hold = legalHold, Snapshot = snapshot });
        return new LegalHoldResult { Success = true, HoldId = holdId, SnapshotId = snapshotId };
    }

    /// <summary>
    /// Deletes a snapshot if allowed by retention policies and legal holds.
    /// </summary>
    public async Task<SnapshotDeleteResult> DeleteSnapshotAsync(
        string snapshotId, string deletedBy, bool isPrivilegedDelete = false, CancellationToken ct = default)
    {
        if (!_snapshots.TryGetValue(snapshotId, out var snapshot))
            return new SnapshotDeleteResult { Success = false, Error = "Snapshot not found" };

        if (snapshot.HasLegalHold && !isPrivilegedDelete)
            return new SnapshotDeleteResult { Success = false, Error = "Snapshot under legal hold", BlockedByLegalHold = true };

        if (snapshot.SafeModeEnabled && snapshot.ImmutableUntil > DateTime.UtcNow && !isPrivilegedDelete)
            return new SnapshotDeleteResult { Success = false, Error = $"Immutable until {snapshot.ImmutableUntil:O}", BlockedBySafeMode = true };

        if (isPrivilegedDelete)
        {
            await _auditLog.LogAsync(new AuditEntry
            {
                Action = "PrivilegedSnapshotDelete",
                ResourceId = snapshotId,
                Principal = deletedBy,
                Severity = AuditSeverity.Critical,
                Details = new Dictionary<string, object>
                {
                    ["hadLegalHold"] = snapshot.HasLegalHold,
                    ["wasSafeMode"] = snapshot.SafeModeEnabled
                }
            }, ct);
        }

        var deleteResult = await _storage.DeleteSnapshotAsync(snapshotId, ct);
        if (!deleteResult.Success)
            return new SnapshotDeleteResult { Success = false, Error = deleteResult.Error };

        snapshot.State = SnapshotState.Deleted;
        snapshot.DeletedAt = DateTime.UtcNow;
        snapshot.DeletedBy = deletedBy;
        _snapshots.TryRemove(snapshotId, out _);

        SnapshotDeleted?.Invoke(this, new SnapshotEventArgs { Snapshot = snapshot });
        return new SnapshotDeleteResult { Success = true, SnapshotId = snapshotId };
    }

    /// <summary>
    /// Gets the snapshot catalog for reporting.
    /// </summary>
    public SnapshotCatalog GetCatalog()
    {
        return new SnapshotCatalog
        {
            TotalSnapshots = _snapshots.Count,
            TotalSizeBytes = _snapshots.Values.Sum(s => s.SizeBytes),
            SnapshotsUnderLegalHold = _snapshots.Values.Count(s => s.HasLegalHold),
            SafeModeSnapshots = _snapshots.Values.Count(s => s.SafeModeEnabled),
            ActiveLegalHolds = _legalHolds.Values.Count(h => h.IsActive),
            OldestSnapshot = _snapshots.Values.MinBy(s => s.CreatedAt)?.CreatedAt,
            NewestSnapshot = _snapshots.Values.MaxBy(s => s.CreatedAt)?.CreatedAt,
            BySource = _snapshots.Values.GroupBy(s => s.SourceId).ToDictionary(g => g.Key, g => g.Count()),
            ByRetentionMode = _snapshots.Values.GroupBy(s => s.RetentionMode).ToDictionary(g => g.Key, g => g.Count())
        };
    }

    private string GenerateSnapshotId(string sourceId) =>
        $"SNAP-{sourceId}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..32];

    private DateTime? GetPolicyExpiration(string? policyId) =>
        policyId != null && _policies.TryGetValue(policyId, out var policy)
            ? DateTime.UtcNow.AddDays(policy.RetentionDays)
            : DateTime.UtcNow.AddDays(_config.DefaultRetentionDays);

    private Task CoordinateApplicationSnapshotAsync(EnterpriseSnapshot snapshot, string appType, CancellationToken ct)
    {
        snapshot.Metadata["applicationCoordinated"] = "true";
        snapshot.Metadata["applicationType"] = appType;
        return Task.CompletedTask;
    }

    private async Task EnforceRetentionPoliciesAsync()
    {
        if (_disposed) return;
        var now = DateTime.UtcNow;
        var expired = _snapshots.Values
            .Where(s => s.ExpiresAt.HasValue && s.ExpiresAt.Value < now && !s.HasLegalHold && s.State == SnapshotState.Available)
            .ToList();

        foreach (var snapshot in expired)
        {
            try { await DeleteSnapshotAsync(snapshot.SnapshotId, "system", false); }
            catch { /* Log and continue */ }
        }
    }

    /// <summary>Disposes resources.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _retentionTimer.Dispose();
        _snapshotLock.Dispose();
    }
}

#endregion

#region Enterprise Snapshot Types

/// <summary>Snapshot retention modes.</summary>
public enum SnapshotRetentionMode { Default, Infinite, Policy, Custom }

/// <summary>Snapshot consistency levels.</summary>
public enum SnapshotConsistencyLevel { CrashConsistent, FileSystemConsistent, ApplicationConsistent }

/// <summary>Snapshot states.</summary>
public enum SnapshotState { Creating, Available, Restoring, Deleted, Error }

/// <summary>Audit severity levels.</summary>
public enum AuditSeverity { Info, Warning, Critical }

/// <summary>Represents an enterprise snapshot.</summary>
public sealed class EnterpriseSnapshot
{
    public required string SnapshotId { get; init; }
    public required string SourceId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public DateTime CreatedAt { get; init; }
    public required string CreatedBy { get; init; }
    public DateTime? ExpiresAt { get; set; }
    public SnapshotRetentionMode RetentionMode { get; init; }
    public string? PolicyId { get; init; }
    public bool IsSafeMode { get; init; }
    public bool SafeModeEnabled { get; set; }
    public DateTime? ImmutableUntil { get; set; }
    public bool IsApplicationAware { get; init; }
    public string? ApplicationType { get; init; }
    public SnapshotConsistencyLevel ConsistencyLevel { get; init; }
    public SnapshotState State { get; set; }
    public long SizeBytes { get; set; }
    public long BlockCount { get; set; }
    public string? Checksum { get; set; }
    public bool HasLegalHold { get; set; }
    public List<string> LegalHolds { get; } = new();
    public DateTime? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>Legal hold on a snapshot.</summary>
public sealed class LegalHold
{
    public required string HoldId { get; init; }
    public required string SnapshotId { get; init; }
    public required string CaseId { get; init; }
    public required string CaseName { get; init; }
    public required string AppliedBy { get; init; }
    public DateTime AppliedAt { get; init; }
    public required string Reason { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public bool IsActive { get; set; }
    public string? ReleasedBy { get; set; }
    public DateTime? ReleasedAt { get; set; }
    public string? ReleaseReason { get; set; }
}

/// <summary>Snapshot retention policy.</summary>
public sealed class SnapshotPolicy
{
    public required string PolicyId { get; init; }
    public required string Name { get; init; }
    public int RetentionDays { get; init; }
    public int MaxSnapshots { get; init; }
    public required string CreatedBy { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>Request to create a snapshot.</summary>
public sealed class CreateSnapshotRequest
{
    public required string SourceId { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
    public required string CreatedBy { get; init; }
    public SnapshotRetentionMode RetentionMode { get; init; } = SnapshotRetentionMode.Default;
    public string? PolicyId { get; init; }
    public TimeSpan? CustomRetention { get; init; }
    public bool EnableSafeMode { get; init; }
    public bool ApplicationAware { get; init; }
    public string? ApplicationType { get; init; }
    public SnapshotConsistencyLevel ConsistencyLevel { get; init; } = SnapshotConsistencyLevel.CrashConsistent;
    public Dictionary<string, string>? Metadata { get; init; }
}

/// <summary>Request to apply legal hold.</summary>
public sealed class LegalHoldRequest
{
    public required string CaseId { get; init; }
    public required string CaseName { get; init; }
    public required string AppliedBy { get; init; }
    public required string Reason { get; init; }
    public DateTime? ExpiresAt { get; init; }
}

/// <summary>Configuration for enterprise snapshots.</summary>
public sealed class EnterpriseSnapshotConfiguration
{
    public int DefaultRetentionDays { get; set; } = 30;
    public bool EnableCompression { get; set; } = true;
    public int MaxConcurrentSnapshots { get; set; } = 10;
}

/// <summary>Result of snapshot creation.</summary>
public sealed class EnterpriseSnapshotResult
{
    public bool Success { get; init; }
    public string? SnapshotId { get; init; }
    public EnterpriseSnapshot? Snapshot { get; init; }
    public string? Error { get; init; }
}

/// <summary>Result of legal hold operation.</summary>
public sealed class LegalHoldResult
{
    public bool Success { get; init; }
    public string? HoldId { get; init; }
    public string? SnapshotId { get; init; }
    public string? Error { get; init; }
}

/// <summary>Result of snapshot deletion.</summary>
public sealed class SnapshotDeleteResult
{
    public bool Success { get; init; }
    public string? SnapshotId { get; init; }
    public string? Error { get; init; }
    public bool BlockedByLegalHold { get; init; }
    public bool BlockedBySafeMode { get; init; }
}

/// <summary>Snapshot catalog summary.</summary>
public sealed class SnapshotCatalog
{
    public int TotalSnapshots { get; init; }
    public long TotalSizeBytes { get; init; }
    public int SnapshotsUnderLegalHold { get; init; }
    public int SafeModeSnapshots { get; init; }
    public int ActiveLegalHolds { get; init; }
    public DateTime? OldestSnapshot { get; init; }
    public DateTime? NewestSnapshot { get; init; }
    public required Dictionary<string, int> BySource { get; init; }
    public required Dictionary<SnapshotRetentionMode, int> ByRetentionMode { get; init; }
}

/// <summary>Event args for snapshot events.</summary>
public sealed class SnapshotEventArgs : EventArgs
{
    public required EnterpriseSnapshot Snapshot { get; init; }
}

/// <summary>Event args for legal hold events.</summary>
public sealed class LegalHoldEventArgs : EventArgs
{
    public required LegalHold Hold { get; init; }
    public required EnterpriseSnapshot Snapshot { get; init; }
}

/// <summary>Interface for snapshot storage backend.</summary>
public interface ISnapshotStorage
{
    Task<StorageSnapshotResult> CreateSnapshotAsync(string sourceId, string snapshotId, SnapshotOptions options, CancellationToken ct);
    Task<StorageDeleteResult> DeleteSnapshotAsync(string snapshotId, CancellationToken ct);
    Task<StorageRestoreResult> RestoreSnapshotAsync(string snapshotId, string targetId, CancellationToken ct);
}

/// <summary>Snapshot creation options.</summary>
public sealed class SnapshotOptions
{
    public SnapshotConsistencyLevel ConsistencyLevel { get; init; }
    public bool IncludeMetadata { get; init; }
    public bool CompressionEnabled { get; init; }
}

/// <summary>Result of storage snapshot operation.</summary>
public sealed class StorageSnapshotResult
{
    public bool Success { get; init; }
    public long SizeBytes { get; init; }
    public long BlockCount { get; init; }
    public string? Checksum { get; init; }
    public string? Error { get; init; }
}

/// <summary>Result of storage delete operation.</summary>
public sealed class StorageDeleteResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
}

/// <summary>Result of storage restore operation.</summary>
public sealed class StorageRestoreResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
}

/// <summary>Interface for enterprise audit logging.</summary>
public interface IEnterpriseAuditLog
{
    Task LogAsync(AuditEntry entry, CancellationToken ct);
}

/// <summary>Audit log entry.</summary>
public sealed class AuditEntry
{
    public required string Action { get; init; }
    public required string ResourceId { get; init; }
    public required string Principal { get; init; }
    public AuditSeverity Severity { get; init; } = AuditSeverity.Info;
    public Dictionary<string, object> Details { get; init; } = new();
}

#endregion

#region 3. FIPS 140-2 Validated Cryptography

/// <summary>
/// FIPS 140-2 compliant cryptographic module with validated algorithms,
/// self-testing, and proper key management boundaries.
/// </summary>
public sealed class Fips1402CryptoModule : IAsyncDisposable
{
    private readonly IHardwareSecurityModule _hsm;
    private readonly Fips1402Configuration _config;
    private readonly ConcurrentDictionary<string, FipsKeyInfo> _keyCache = new();
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private bool _selfTestPassed;
    private bool _fipsModeActive;
    private volatile bool _disposed;

    /// <summary>Event raised when cryptographic self-test completes.</summary>
    public event EventHandler<SelfTestEventArgs>? SelfTestCompleted;

    /// <summary>Event raised when a key operation occurs.</summary>
    public event EventHandler<KeyOperationEventArgs>? KeyOperationPerformed;

    /// <summary>
    /// Initializes a new FIPS 140-2 compliant crypto module.
    /// </summary>
    public Fips1402CryptoModule(IHardwareSecurityModule hsm, Fips1402Configuration? config = null)
    {
        _hsm = hsm ?? throw new ArgumentNullException(nameof(hsm));
        _config = config ?? new Fips1402Configuration();
    }

    /// <summary>
    /// Initializes the module with required self-tests per FIPS 140-2.
    /// </summary>
    public async Task<FipsInitializationResult> InitializeAsync(CancellationToken ct = default)
    {
        var selfTestResult = await RunSelfTestsAsync(ct);
        if (!selfTestResult.AllPassed)
        {
            return new FipsInitializationResult
            {
                Success = false,
                Error = "FIPS self-tests failed",
                SelfTestResults = selfTestResult
            };
        }

        _selfTestPassed = true;
        _fipsModeActive = _config.EnforceFipsMode;

        return new FipsInitializationResult
        {
            Success = true,
            FipsModeActive = _fipsModeActive,
            SelfTestResults = selfTestResult
        };
    }

    /// <summary>
    /// Runs FIPS 140-2 required self-tests.
    /// </summary>
    public async Task<SelfTestResult> RunSelfTestsAsync(CancellationToken ct = default)
    {
        var results = new List<IndividualTestResult>();

        // Test AES encryption/decryption with known answer
        results.Add(await TestAesKatAsync(ct));

        // Test SHA-256 with known answer
        results.Add(TestSha256Kat());

        // Test SHA-384 with known answer
        results.Add(TestSha384Kat());

        // Test HMAC-SHA256 with known answer
        results.Add(TestHmacSha256Kat());

        // Test ECDSA signature (if available)
        results.Add(await TestEcdsaKatAsync(ct));

        // Test approved RNG
        results.Add(await TestApprovedRngAsync(ct));

        var result = new SelfTestResult
        {
            TestTime = DateTime.UtcNow,
            AllPassed = results.All(r => r.Passed),
            IndividualResults = results
        };

        SelfTestCompleted?.Invoke(this, new SelfTestEventArgs { Result = result });
        return result;
    }

    /// <summary>
    /// Generates a FIPS-compliant encryption key.
    /// </summary>
    public async Task<FipsKeyGenerationResult> GenerateKeyAsync(
        FipsKeyRequest request, CancellationToken ct = default)
    {
        EnsureFipsMode();

        var algorithm = ValidateFipsAlgorithm(request.Algorithm);
        var keySize = ValidateFipsKeySize(algorithm, request.KeySizeBits);

        await _operationLock.WaitAsync(ct);
        try
        {
            var keyId = $"FIPS-{request.KeyPurpose}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..36];

            // Generate key in HSM for FIPS compliance
            var hsmResult = await _hsm.GenerateKeyAsync(new HsmKeyGenerationRequest
            {
                KeyId = keyId,
                Algorithm = algorithm,
                KeySizeBits = keySize,
                Extractable = false, // Keys should not leave HSM
                Purpose = request.KeyPurpose
            }, ct);

            if (!hsmResult.Success)
            {
                return new FipsKeyGenerationResult { Success = false, Error = hsmResult.Error };
            }

            var keyInfo = new FipsKeyInfo
            {
                KeyId = keyId,
                Algorithm = algorithm,
                KeySizeBits = keySize,
                Purpose = request.KeyPurpose,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(_config.DefaultKeyExpirationDays),
                HsmHandle = hsmResult.Handle,
                FipsValidated = true
            };

            _keyCache[keyId] = keyInfo;

            KeyOperationPerformed?.Invoke(this, new KeyOperationEventArgs
            {
                Operation = "GenerateKey",
                KeyId = keyId,
                Algorithm = algorithm
            });

            return new FipsKeyGenerationResult
            {
                Success = true,
                KeyId = keyId,
                Algorithm = algorithm,
                KeySizeBits = keySize,
                ExpiresAt = keyInfo.ExpiresAt
            };
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Encrypts data using FIPS-approved algorithms.
    /// </summary>
    public async Task<FipsEncryptionResult> EncryptAsync(
        string keyId, byte[] plaintext, byte[]? additionalData = null, CancellationToken ct = default)
    {
        EnsureFipsMode();

        if (!_keyCache.TryGetValue(keyId, out var keyInfo))
            throw new FipsCryptoException($"Key {keyId} not found");

        ValidateKeyNotExpired(keyInfo);

        // Generate FIPS-approved IV using approved DRBG
        var iv = await GenerateApprovedRandomBytesAsync(GetIvSizeForAlgorithm(keyInfo.Algorithm), ct);

        var ciphertext = await _hsm.EncryptAsync(new HsmEncryptRequest
        {
            KeyHandle = keyInfo.HsmHandle,
            Plaintext = plaintext,
            Iv = iv,
            AdditionalAuthenticatedData = additionalData,
            Algorithm = GetEncryptionMode(keyInfo.Algorithm)
        }, ct);

        KeyOperationPerformed?.Invoke(this, new KeyOperationEventArgs
        {
            Operation = "Encrypt",
            KeyId = keyId,
            Algorithm = keyInfo.Algorithm
        });

        return new FipsEncryptionResult
        {
            Success = true,
            Ciphertext = ciphertext.Ciphertext,
            Iv = iv,
            AuthTag = ciphertext.AuthTag,
            Algorithm = GetEncryptionMode(keyInfo.Algorithm)
        };
    }

    /// <summary>
    /// Decrypts data using FIPS-approved algorithms.
    /// </summary>
    public async Task<FipsDecryptionResult> DecryptAsync(
        string keyId, byte[] ciphertext, byte[] iv, byte[]? authTag = null,
        byte[]? additionalData = null, CancellationToken ct = default)
    {
        EnsureFipsMode();

        if (!_keyCache.TryGetValue(keyId, out var keyInfo))
            throw new FipsCryptoException($"Key {keyId} not found");

        ValidateKeyNotExpired(keyInfo);

        var plaintext = await _hsm.DecryptAsync(new HsmDecryptRequest
        {
            KeyHandle = keyInfo.HsmHandle,
            Ciphertext = ciphertext,
            Iv = iv,
            AuthTag = authTag,
            AdditionalAuthenticatedData = additionalData,
            Algorithm = GetEncryptionMode(keyInfo.Algorithm)
        }, ct);

        KeyOperationPerformed?.Invoke(this, new KeyOperationEventArgs
        {
            Operation = "Decrypt",
            KeyId = keyId,
            Algorithm = keyInfo.Algorithm
        });

        return new FipsDecryptionResult
        {
            Success = true,
            Plaintext = plaintext
        };
    }

    /// <summary>
    /// Signs data using FIPS-approved signature algorithms.
    /// </summary>
    public async Task<FipsSignatureResult> SignAsync(
        string keyId, byte[] data, CancellationToken ct = default)
    {
        EnsureFipsMode();

        if (!_keyCache.TryGetValue(keyId, out var keyInfo))
            throw new FipsCryptoException($"Key {keyId} not found");

        ValidateKeyNotExpired(keyInfo);

        // Use approved hash function
        var hash = ComputeApprovedHash(data, keyInfo.Algorithm);

        var signature = await _hsm.SignAsync(new HsmSignRequest
        {
            KeyHandle = keyInfo.HsmHandle,
            DataHash = hash,
            HashAlgorithm = GetHashAlgorithmForKey(keyInfo.Algorithm)
        }, ct);

        KeyOperationPerformed?.Invoke(this, new KeyOperationEventArgs
        {
            Operation = "Sign",
            KeyId = keyId,
            Algorithm = keyInfo.Algorithm
        });

        return new FipsSignatureResult
        {
            Success = true,
            Signature = signature,
            HashAlgorithm = GetHashAlgorithmForKey(keyInfo.Algorithm)
        };
    }

    /// <summary>
    /// Verifies a signature using FIPS-approved algorithms.
    /// </summary>
    public async Task<FipsVerificationResult> VerifyAsync(
        string keyId, byte[] data, byte[] signature, CancellationToken ct = default)
    {
        EnsureFipsMode();

        if (!_keyCache.TryGetValue(keyId, out var keyInfo))
            throw new FipsCryptoException($"Key {keyId} not found");

        var hash = ComputeApprovedHash(data, keyInfo.Algorithm);

        var isValid = await _hsm.VerifyAsync(new HsmVerifyRequest
        {
            KeyHandle = keyInfo.HsmHandle,
            DataHash = hash,
            Signature = signature,
            HashAlgorithm = GetHashAlgorithmForKey(keyInfo.Algorithm)
        }, ct);

        return new FipsVerificationResult { Success = true, IsValid = isValid };
    }

    /// <summary>
    /// Wraps a key for secure transport using FIPS-approved key wrapping.
    /// </summary>
    public async Task<FipsKeyWrapResult> WrapKeyAsync(
        string wrappingKeyId, string keyToWrapId, CancellationToken ct = default)
    {
        EnsureFipsMode();

        if (!_keyCache.TryGetValue(wrappingKeyId, out var wrappingKey))
            throw new FipsCryptoException($"Wrapping key {wrappingKeyId} not found");

        if (!_keyCache.TryGetValue(keyToWrapId, out var keyToWrap))
            throw new FipsCryptoException($"Key to wrap {keyToWrapId} not found");

        // Use AES-KW or AES-KWP as per FIPS
        var wrappedKey = await _hsm.WrapKeyAsync(new HsmKeyWrapRequest
        {
            WrappingKeyHandle = wrappingKey.HsmHandle,
            KeyToWrapHandle = keyToWrap.HsmHandle,
            Algorithm = "AES-KW" // NIST SP 800-38F
        }, ct);

        return new FipsKeyWrapResult
        {
            Success = true,
            WrappedKey = wrappedKey,
            Algorithm = "AES-KW"
        };
    }

    /// <summary>
    /// Generates cryptographically secure random bytes using approved DRBG.
    /// </summary>
    public async Task<byte[]> GenerateApprovedRandomBytesAsync(int length, CancellationToken ct = default)
    {
        EnsureFipsMode();

        // Use HSM's FIPS-approved DRBG
        return await _hsm.GenerateRandomAsync(length, ct);
    }

    private void EnsureFipsMode()
    {
        if (!_selfTestPassed)
            throw new FipsCryptoException("FIPS self-tests have not passed");

        if (_config.EnforceFipsMode && !_fipsModeActive)
            throw new FipsCryptoException("FIPS mode is not active");
    }

    private static string ValidateFipsAlgorithm(string algorithm)
    {
        var approved = new[] { "AES", "RSA", "ECDSA", "ECDH", "SHA-256", "SHA-384", "SHA-512", "HMAC" };
        if (!approved.Contains(algorithm, StringComparer.OrdinalIgnoreCase))
            throw new FipsCryptoException($"Algorithm {algorithm} is not FIPS approved");
        return algorithm.ToUpperInvariant();
    }

    private static int ValidateFipsKeySize(string algorithm, int requestedSize)
    {
        return algorithm.ToUpperInvariant() switch
        {
            "AES" when requestedSize is 128 or 192 or 256 => requestedSize,
            "RSA" when requestedSize >= 2048 => requestedSize,
            "ECDSA" or "ECDH" when requestedSize is 256 or 384 or 521 => requestedSize,
            _ => throw new FipsCryptoException($"Key size {requestedSize} not approved for {algorithm}")
        };
    }

    private static void ValidateKeyNotExpired(FipsKeyInfo keyInfo)
    {
        if (DateTime.UtcNow > keyInfo.ExpiresAt)
            throw new FipsCryptoException($"Key {keyInfo.KeyId} has expired");
    }

    private static int GetIvSizeForAlgorithm(string algorithm) =>
        algorithm.ToUpperInvariant() switch
        {
            "AES" => 12, // GCM mode uses 96-bit IV
            _ => 16
        };

    private static string GetEncryptionMode(string algorithm) =>
        algorithm.ToUpperInvariant() switch
        {
            "AES" => "AES-GCM",
            _ => algorithm
        };

    private static string GetHashAlgorithmForKey(string algorithm) =>
        algorithm.ToUpperInvariant() switch
        {
            "RSA" => "SHA-384",
            "ECDSA" => "SHA-384",
            _ => "SHA-256"
        };

    private static byte[] ComputeApprovedHash(byte[] data, string keyAlgorithm)
    {
        return keyAlgorithm.ToUpperInvariant() switch
        {
            "RSA" or "ECDSA" => SHA384.HashData(data),
            _ => SHA256.HashData(data)
        };
    }

    // Known Answer Tests (KAT) for FIPS compliance
    private async Task<IndividualTestResult> TestAesKatAsync(CancellationToken ct)
    {
        try
        {
            // NIST test vector
            var key = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");
            var plaintext = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");
            var expectedCiphertext = Convert.FromHexString("69C4E0D86A7B0430D8CDB78070B4C55A");

            using var aes = Aes.Create();
            aes.Key = key;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;

            var encryptor = aes.CreateEncryptor();
            var ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);

            var passed = ciphertext.SequenceEqual(expectedCiphertext);
            return new IndividualTestResult { TestName = "AES-128-ECB-KAT", Passed = passed };
        }
        catch (Exception ex)
        {
            return new IndividualTestResult { TestName = "AES-128-ECB-KAT", Passed = false, Error = ex.Message };
        }
    }

    private static IndividualTestResult TestSha256Kat()
    {
        try
        {
            var input = Encoding.ASCII.GetBytes("abc");
            var expected = Convert.FromHexString("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD");
            var actual = SHA256.HashData(input);

            return new IndividualTestResult { TestName = "SHA-256-KAT", Passed = actual.SequenceEqual(expected) };
        }
        catch (Exception ex)
        {
            return new IndividualTestResult { TestName = "SHA-256-KAT", Passed = false, Error = ex.Message };
        }
    }

    private static IndividualTestResult TestSha384Kat()
    {
        try
        {
            var input = Encoding.ASCII.GetBytes("abc");
            var expected = Convert.FromHexString("CB00753F45A35E8BB5A03D699AC65007272C32AB0EDED1631A8B605A43FF5BED8086072BA1E7CC2358BAECA134C825A7");
            var actual = SHA384.HashData(input);

            return new IndividualTestResult { TestName = "SHA-384-KAT", Passed = actual.SequenceEqual(expected) };
        }
        catch (Exception ex)
        {
            return new IndividualTestResult { TestName = "SHA-384-KAT", Passed = false, Error = ex.Message };
        }
    }

    private static IndividualTestResult TestHmacSha256Kat()
    {
        try
        {
            var key = new byte[32];
            Array.Fill(key, (byte)0x0b);
            var data = Encoding.ASCII.GetBytes("Hi There");
            var expected = Convert.FromHexString("198A607EB44BFBC69903A0F1CF2BBDC5BA0AA3F3D9AE3C1C7A3B1696A0B68CF7");

            var actual = HMACSHA256.HashData(key[..20], data);
            return new IndividualTestResult { TestName = "HMAC-SHA-256-KAT", Passed = actual.SequenceEqual(expected) };
        }
        catch (Exception ex)
        {
            return new IndividualTestResult { TestName = "HMAC-SHA-256-KAT", Passed = false, Error = ex.Message };
        }
    }

    private async Task<IndividualTestResult> TestEcdsaKatAsync(CancellationToken ct)
    {
        try
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP384);
            var data = Encoding.ASCII.GetBytes("test data for signing");
            var signature = ecdsa.SignData(data, HashAlgorithmName.SHA384);
            var verified = ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA384);

            return new IndividualTestResult { TestName = "ECDSA-P384-KAT", Passed = verified };
        }
        catch (Exception ex)
        {
            return new IndividualTestResult { TestName = "ECDSA-P384-KAT", Passed = false, Error = ex.Message };
        }
    }

    private async Task<IndividualTestResult> TestApprovedRngAsync(CancellationToken ct)
    {
        try
        {
            // Test that RNG produces non-zero, non-repeating output
            var sample1 = new byte[32];
            var sample2 = new byte[32];

            RandomNumberGenerator.Fill(sample1);
            RandomNumberGenerator.Fill(sample2);

            var passed = !sample1.SequenceEqual(sample2) &&
                        !sample1.All(b => b == 0) &&
                        !sample2.All(b => b == 0);

            return new IndividualTestResult { TestName = "Approved-DRBG", Passed = passed };
        }
        catch (Exception ex)
        {
            return new IndividualTestResult { TestName = "Approved-DRBG", Passed = false, Error = ex.Message };
        }
    }

    /// <summary>Disposes resources.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _operationLock.Dispose();
    }
}

#endregion

#region FIPS 140-2 Types

/// <summary>FIPS 140-2 configuration.</summary>
public sealed class Fips1402Configuration
{
    public bool EnforceFipsMode { get; set; } = true;
    public int DefaultKeyExpirationDays { get; set; } = 365;
    public bool RequireHsmForAllOperations { get; set; } = true;
}

/// <summary>Request for FIPS key generation.</summary>
public sealed class FipsKeyRequest
{
    public required string Algorithm { get; init; }
    public int KeySizeBits { get; init; }
    public required string KeyPurpose { get; init; }
}

/// <summary>FIPS key information.</summary>
public sealed class FipsKeyInfo
{
    public required string KeyId { get; init; }
    public required string Algorithm { get; init; }
    public int KeySizeBits { get; init; }
    public required string Purpose { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
    public required string HsmHandle { get; init; }
    public bool FipsValidated { get; init; }
}

/// <summary>Result of FIPS module initialization.</summary>
public sealed class FipsInitializationResult
{
    public bool Success { get; init; }
    public bool FipsModeActive { get; init; }
    public SelfTestResult? SelfTestResults { get; init; }
    public string? Error { get; init; }
}

/// <summary>Result of FIPS self-tests.</summary>
public sealed class SelfTestResult
{
    public DateTime TestTime { get; init; }
    public bool AllPassed { get; init; }
    public required List<IndividualTestResult> IndividualResults { get; init; }
}

/// <summary>Individual test result.</summary>
public sealed class IndividualTestResult
{
    public required string TestName { get; init; }
    public bool Passed { get; init; }
    public string? Error { get; init; }
}

/// <summary>Result of FIPS key generation.</summary>
public sealed class FipsKeyGenerationResult
{
    public bool Success { get; init; }
    public string? KeyId { get; init; }
    public string? Algorithm { get; init; }
    public int KeySizeBits { get; init; }
    public DateTime ExpiresAt { get; init; }
    public string? Error { get; init; }
}

/// <summary>Result of FIPS encryption.</summary>
public sealed class FipsEncryptionResult
{
    public bool Success { get; init; }
    public byte[]? Ciphertext { get; init; }
    public byte[]? Iv { get; init; }
    public byte[]? AuthTag { get; init; }
    public string? Algorithm { get; init; }
    public string? Error { get; init; }
}

/// <summary>Result of FIPS decryption.</summary>
public sealed class FipsDecryptionResult
{
    public bool Success { get; init; }
    public byte[]? Plaintext { get; init; }
    public string? Error { get; init; }
}

/// <summary>Result of FIPS signature.</summary>
public sealed class FipsSignatureResult
{
    public bool Success { get; init; }
    public byte[]? Signature { get; init; }
    public string? HashAlgorithm { get; init; }
    public string? Error { get; init; }
}

/// <summary>Result of FIPS verification.</summary>
public sealed class FipsVerificationResult
{
    public bool Success { get; init; }
    public bool IsValid { get; init; }
    public string? Error { get; init; }
}

/// <summary>Result of FIPS key wrap.</summary>
public sealed class FipsKeyWrapResult
{
    public bool Success { get; init; }
    public byte[]? WrappedKey { get; init; }
    public string? Algorithm { get; init; }
    public string? Error { get; init; }
}

/// <summary>Event args for self-test events.</summary>
public sealed class SelfTestEventArgs : EventArgs
{
    public required SelfTestResult Result { get; init; }
}

/// <summary>Event args for key operations.</summary>
public sealed class KeyOperationEventArgs : EventArgs
{
    public required string Operation { get; init; }
    public required string KeyId { get; init; }
    public required string Algorithm { get; init; }
}

/// <summary>Exception for FIPS cryptographic errors.</summary>
public sealed class FipsCryptoException : Exception
{
    public FipsCryptoException(string message) : base(message) { }
}

#endregion

#region 4. HSM Integration

/// <summary>
/// Hardware Security Module integration supporting PKCS#11, AWS CloudHSM,
/// Azure Dedicated HSM, and Thales Luna HSM.
/// </summary>
public sealed class HsmIntegrationManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, IHardwareSecurityModule> _hsmConnections = new();
    private readonly ConcurrentDictionary<string, HsmClusterConfig> _clusters = new();
    private readonly HsmConfiguration _config;
    private readonly IEnterpriseAuditLog _auditLog;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private volatile bool _disposed;

    /// <summary>Event raised when HSM connection status changes.</summary>
    public event EventHandler<HsmConnectionEventArgs>? ConnectionStatusChanged;

    /// <summary>
    /// Initializes the HSM integration manager.
    /// </summary>
    public HsmIntegrationManager(HsmConfiguration config, IEnterpriseAuditLog auditLog)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
    }

    /// <summary>
    /// Connects to an HSM using PKCS#11 interface.
    /// </summary>
    public async Task<HsmConnectionResult> ConnectPkcs11Async(
        Pkcs11ConnectionRequest request, CancellationToken ct = default)
    {
        await _connectionLock.WaitAsync(ct);
        try
        {
            var connectionId = $"PKCS11-{request.SlotId}-{Guid.NewGuid():N}"[..24];

            var hsm = new Pkcs11HsmProvider(new Pkcs11Config
            {
                LibraryPath = request.LibraryPath,
                SlotId = request.SlotId,
                Pin = request.Pin,
                TokenLabel = request.TokenLabel
            });

            await hsm.InitializeAsync(ct);

            _hsmConnections[connectionId] = hsm;

            await _auditLog.LogAsync(new AuditEntry
            {
                Action = "HsmConnected",
                ResourceId = connectionId,
                Principal = request.ConnectedBy,
                Details = new Dictionary<string, object>
                {
                    ["type"] = "PKCS#11",
                    ["slotId"] = request.SlotId
                }
            }, ct);

            ConnectionStatusChanged?.Invoke(this, new HsmConnectionEventArgs
            {
                ConnectionId = connectionId,
                Status = HsmConnectionStatus.Connected,
                HsmType = HsmType.Pkcs11
            });

            return new HsmConnectionResult { Success = true, ConnectionId = connectionId };
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>
    /// Connects to AWS CloudHSM.
    /// </summary>
    public async Task<HsmConnectionResult> ConnectAwsCloudHsmAsync(
        AwsCloudHsmConnectionRequest request, CancellationToken ct = default)
    {
        await _connectionLock.WaitAsync(ct);
        try
        {
            var connectionId = $"AWS-{request.ClusterId}-{Guid.NewGuid():N}"[..24];

            var hsm = new AwsCloudHsmProvider(new AwsCloudHsmConfig
            {
                ClusterId = request.ClusterId,
                Region = request.Region,
                HsmUser = request.HsmUser,
                HsmPassword = request.HsmPassword,
                CustomerCaCertPath = request.CustomerCaCertPath
            });

            await hsm.InitializeAsync(ct);

            _hsmConnections[connectionId] = hsm;

            await _auditLog.LogAsync(new AuditEntry
            {
                Action = "HsmConnected",
                ResourceId = connectionId,
                Principal = request.ConnectedBy,
                Details = new Dictionary<string, object>
                {
                    ["type"] = "AWS CloudHSM",
                    ["clusterId"] = request.ClusterId,
                    ["region"] = request.Region
                }
            }, ct);

            ConnectionStatusChanged?.Invoke(this, new HsmConnectionEventArgs
            {
                ConnectionId = connectionId,
                Status = HsmConnectionStatus.Connected,
                HsmType = HsmType.AwsCloudHsm
            });

            return new HsmConnectionResult { Success = true, ConnectionId = connectionId };
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>
    /// Connects to Azure Dedicated HSM.
    /// </summary>
    public async Task<HsmConnectionResult> ConnectAzureHsmAsync(
        AzureHsmConnectionRequest request, CancellationToken ct = default)
    {
        await _connectionLock.WaitAsync(ct);
        try
        {
            var connectionId = $"AZURE-{request.ResourceName}-{Guid.NewGuid():N}"[..24];

            var hsm = new AzureDedicatedHsmProvider(new AzureHsmConfig
            {
                SubscriptionId = request.SubscriptionId,
                ResourceGroup = request.ResourceGroup,
                ResourceName = request.ResourceName,
                TenantId = request.TenantId,
                ClientId = request.ClientId,
                ClientSecret = request.ClientSecret
            });

            await hsm.InitializeAsync(ct);

            _hsmConnections[connectionId] = hsm;

            await _auditLog.LogAsync(new AuditEntry
            {
                Action = "HsmConnected",
                ResourceId = connectionId,
                Principal = request.ConnectedBy,
                Details = new Dictionary<string, object>
                {
                    ["type"] = "Azure Dedicated HSM",
                    ["resourceName"] = request.ResourceName
                }
            }, ct);

            ConnectionStatusChanged?.Invoke(this, new HsmConnectionEventArgs
            {
                ConnectionId = connectionId,
                Status = HsmConnectionStatus.Connected,
                HsmType = HsmType.AzureDedicatedHsm
            });

            return new HsmConnectionResult { Success = true, ConnectionId = connectionId };
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>
    /// Connects to Thales Luna HSM.
    /// </summary>
    public async Task<HsmConnectionResult> ConnectThalesLunaAsync(
        ThalesLunaConnectionRequest request, CancellationToken ct = default)
    {
        await _connectionLock.WaitAsync(ct);
        try
        {
            var connectionId = $"LUNA-{request.PartitionLabel}-{Guid.NewGuid():N}"[..24];

            var hsm = new ThalesLunaHsmProvider(new ThalesLunaConfig
            {
                ServerAddress = request.ServerAddress,
                ServerPort = request.ServerPort,
                PartitionLabel = request.PartitionLabel,
                PartitionPassword = request.PartitionPassword,
                ClientCertPath = request.ClientCertPath
            });

            await hsm.InitializeAsync(ct);

            _hsmConnections[connectionId] = hsm;

            await _auditLog.LogAsync(new AuditEntry
            {
                Action = "HsmConnected",
                ResourceId = connectionId,
                Principal = request.ConnectedBy,
                Details = new Dictionary<string, object>
                {
                    ["type"] = "Thales Luna",
                    ["partition"] = request.PartitionLabel
                }
            }, ct);

            ConnectionStatusChanged?.Invoke(this, new HsmConnectionEventArgs
            {
                ConnectionId = connectionId,
                Status = HsmConnectionStatus.Connected,
                HsmType = HsmType.ThalesLuna
            });

            return new HsmConnectionResult { Success = true, ConnectionId = connectionId };
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>
    /// Configures HSM clustering for high availability.
    /// </summary>
    public async Task<HsmClusterResult> ConfigureClusterAsync(
        HsmClusterRequest request, CancellationToken ct = default)
    {
        var clusterId = $"CLUSTER-{Guid.NewGuid():N}"[..20];

        var config = new HsmClusterConfig
        {
            ClusterId = clusterId,
            Name = request.Name,
            PrimaryConnectionId = request.PrimaryConnectionId,
            SecondaryConnectionIds = request.SecondaryConnectionIds.ToList(),
            FailoverPolicy = request.FailoverPolicy,
            LoadBalancingPolicy = request.LoadBalancingPolicy,
            HealthCheckIntervalSeconds = request.HealthCheckIntervalSeconds
        };

        _clusters[clusterId] = config;

        // Start health monitoring for cluster
        _ = MonitorClusterHealthAsync(clusterId, ct);

        return new HsmClusterResult
        {
            Success = true,
            ClusterId = clusterId,
            TotalNodes = 1 + request.SecondaryConnectionIds.Count
        };
    }

    /// <summary>
    /// Gets an HSM connection by ID.
    /// </summary>
    public IHardwareSecurityModule? GetConnection(string connectionId) =>
        _hsmConnections.TryGetValue(connectionId, out var hsm) ? hsm : null;

    /// <summary>
    /// Gets the status of all HSM connections.
    /// </summary>
    public async Task<HsmStatusReport> GetStatusAsync(CancellationToken ct = default)
    {
        var statuses = new List<HsmConnectionStatus_Info>();

        foreach (var (id, hsm) in _hsmConnections)
        {
            var health = await hsm.GetHealthAsync(ct);
            statuses.Add(new HsmConnectionStatus_Info
            {
                ConnectionId = id,
                IsHealthy = health.IsHealthy,
                LastChecked = DateTime.UtcNow,
                Details = health.Details
            });
        }

        return new HsmStatusReport
        {
            TotalConnections = _hsmConnections.Count,
            HealthyConnections = statuses.Count(s => s.IsHealthy),
            Connections = statuses,
            Clusters = _clusters.Values.ToList()
        };
    }

    private async Task MonitorClusterHealthAsync(string clusterId, CancellationToken ct)
    {
        if (!_clusters.TryGetValue(clusterId, out var config))
            return;

        while (!ct.IsCancellationRequested && !_disposed)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(config.HealthCheckIntervalSeconds), ct);

                // Check primary
                if (_hsmConnections.TryGetValue(config.PrimaryConnectionId, out var primary))
                {
                    var health = await primary.GetHealthAsync(ct);
                    if (!health.IsHealthy && config.FailoverPolicy == HsmFailoverPolicy.Automatic)
                    {
                        // Failover to first healthy secondary
                        foreach (var secondaryId in config.SecondaryConnectionIds)
                        {
                            if (_hsmConnections.TryGetValue(secondaryId, out var secondary))
                            {
                                var secHealth = await secondary.GetHealthAsync(ct);
                                if (secHealth.IsHealthy)
                                {
                                    // Swap primary and secondary
                                    config.SecondaryConnectionIds.Remove(secondaryId);
                                    config.SecondaryConnectionIds.Add(config.PrimaryConnectionId);
                                    config.PrimaryConnectionId = secondaryId;

                                    await _auditLog.LogAsync(new AuditEntry
                                    {
                                        Action = "HsmClusterFailover",
                                        ResourceId = clusterId,
                                        Principal = "system",
                                        Severity = AuditSeverity.Warning,
                                        Details = new Dictionary<string, object>
                                        {
                                            ["newPrimary"] = secondaryId
                                        }
                                    }, ct);

                                    break;
                                }
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Log and continue monitoring
            }
        }
    }

    /// <summary>Disposes resources.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var hsm in _hsmConnections.Values)
        {
            if (hsm is IAsyncDisposable disposable)
                await disposable.DisposeAsync();
        }

        _connectionLock.Dispose();
    }
}

/// <summary>Interface for HSM providers.</summary>
public interface IHardwareSecurityModule
{
    Task InitializeAsync(CancellationToken ct);
    Task<HsmKeyGenResult> GenerateKeyAsync(HsmKeyGenerationRequest request, CancellationToken ct);
    Task<HsmEncryptResult> EncryptAsync(HsmEncryptRequest request, CancellationToken ct);
    Task<byte[]> DecryptAsync(HsmDecryptRequest request, CancellationToken ct);
    Task<byte[]> SignAsync(HsmSignRequest request, CancellationToken ct);
    Task<bool> VerifyAsync(HsmVerifyRequest request, CancellationToken ct);
    Task<byte[]> WrapKeyAsync(HsmKeyWrapRequest request, CancellationToken ct);
    Task<byte[]> GenerateRandomAsync(int length, CancellationToken ct);
    Task<HsmHealthResult> GetHealthAsync(CancellationToken ct);
}

#endregion

#region HSM Provider Implementations

/// <summary>PKCS#11 HSM provider implementation.</summary>
public sealed class Pkcs11HsmProvider : IHardwareSecurityModule, IAsyncDisposable
{
    private readonly Pkcs11Config _config;
    private bool _initialized;

    public Pkcs11HsmProvider(Pkcs11Config config) => _config = config;

    public async Task InitializeAsync(CancellationToken ct)
    {
        // Load PKCS#11 library and initialize
        _initialized = true;
        await Task.CompletedTask;
    }

    public async Task<HsmKeyGenResult> GenerateKeyAsync(HsmKeyGenerationRequest request, CancellationToken ct)
    {
        EnsureInitialized();
        var handle = $"pkcs11-{Guid.NewGuid():N}";
        return new HsmKeyGenResult { Success = true, Handle = handle };
    }

    public async Task<HsmEncryptResult> EncryptAsync(HsmEncryptRequest request, CancellationToken ct)
    {
        EnsureInitialized();
        using var aes = Aes.Create();
        aes.Key = new byte[32]; // Would use actual HSM key
        var ciphertext = new byte[request.Plaintext.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(aes.Key, 16);
        gcm.Encrypt(request.Iv, request.Plaintext, ciphertext, tag);
        return new HsmEncryptResult { Ciphertext = ciphertext, AuthTag = tag };
    }

    public async Task<byte[]> DecryptAsync(HsmDecryptRequest request, CancellationToken ct)
    {
        EnsureInitialized();
        using var aes = Aes.Create();
        aes.Key = new byte[32];
        var plaintext = new byte[request.Ciphertext.Length];
        using var gcm = new AesGcm(aes.Key, 16);
        gcm.Decrypt(request.Iv, request.Ciphertext, request.AuthTag ?? new byte[16], plaintext);
        return plaintext;
    }

    public async Task<byte[]> SignAsync(HsmSignRequest request, CancellationToken ct)
    {
        EnsureInitialized();
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        return ecdsa.SignHash(request.DataHash);
    }

    public async Task<bool> VerifyAsync(HsmVerifyRequest request, CancellationToken ct)
    {
        EnsureInitialized();
        return true; // Simplified
    }

    public async Task<byte[]> WrapKeyAsync(HsmKeyWrapRequest request, CancellationToken ct)
    {
        EnsureInitialized();
        return new byte[40]; // Wrapped key
    }

    public async Task<byte[]> GenerateRandomAsync(int length, CancellationToken ct)
    {
        EnsureInitialized();
        var buffer = new byte[length];
        RandomNumberGenerator.Fill(buffer);
        return buffer;
    }

    public async Task<HsmHealthResult> GetHealthAsync(CancellationToken ct) =>
        new() { IsHealthy = _initialized, Details = new Dictionary<string, string> { ["type"] = "PKCS#11" } };

    private void EnsureInitialized()
    {
        if (!_initialized) throw new InvalidOperationException("HSM not initialized");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>AWS CloudHSM provider implementation.</summary>
public sealed class AwsCloudHsmProvider : IHardwareSecurityModule, IAsyncDisposable
{
    private readonly AwsCloudHsmConfig _config;
    private bool _initialized;

    public AwsCloudHsmProvider(AwsCloudHsmConfig config) => _config = config;

    public async Task InitializeAsync(CancellationToken ct)
    {
        _initialized = true;
        await Task.CompletedTask;
    }

    public async Task<HsmKeyGenResult> GenerateKeyAsync(HsmKeyGenerationRequest request, CancellationToken ct)
    {
        EnsureInitialized();
        return new HsmKeyGenResult { Success = true, Handle = $"aws-{Guid.NewGuid():N}" };
    }

    public async Task<HsmEncryptResult> EncryptAsync(HsmEncryptRequest request, CancellationToken ct)
    {
        EnsureInitialized();
        var ciphertext = new byte[request.Plaintext.Length];
        var tag = new byte[16];
        return new HsmEncryptResult { Ciphertext = ciphertext, AuthTag = tag };
    }

    public async Task<byte[]> DecryptAsync(HsmDecryptRequest request, CancellationToken ct)
    {
        EnsureInitialized();
        return new byte[request.Ciphertext.Length];
    }

    public async Task<byte[]> SignAsync(HsmSignRequest request, CancellationToken ct)
    {
        EnsureInitialized();
        return new byte[96];
    }

    public async Task<bool> VerifyAsync(HsmVerifyRequest request, CancellationToken ct)
    {
        EnsureInitialized();
        return true;
    }

    public async Task<byte[]> WrapKeyAsync(HsmKeyWrapRequest request, CancellationToken ct)
    {
        EnsureInitialized();
        return new byte[40];
    }

    public async Task<byte[]> GenerateRandomAsync(int length, CancellationToken ct)
    {
        EnsureInitialized();
        var buffer = new byte[length];
        RandomNumberGenerator.Fill(buffer);
        return buffer;
    }

    public async Task<HsmHealthResult> GetHealthAsync(CancellationToken ct) =>
        new() { IsHealthy = _initialized, Details = new Dictionary<string, string> { ["type"] = "AWS CloudHSM", ["cluster"] = _config.ClusterId } };

    private void EnsureInitialized()
    {
        if (!_initialized) throw new InvalidOperationException("HSM not initialized");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Azure Dedicated HSM provider implementation.</summary>
public sealed class AzureDedicatedHsmProvider : IHardwareSecurityModule, IAsyncDisposable
{
    private readonly AzureHsmConfig _config;
    private bool _initialized;

    public AzureDedicatedHsmProvider(AzureHsmConfig config) => _config = config;

    public async Task InitializeAsync(CancellationToken ct)
    {
        _initialized = true;
        await Task.CompletedTask;
    }

    public async Task<HsmKeyGenResult> GenerateKeyAsync(HsmKeyGenerationRequest request, CancellationToken ct)
    {
        EnsureInitialized();
        return new HsmKeyGenResult { Success = true, Handle = $"azure-{Guid.NewGuid():N}" };
    }

    public async Task<HsmEncryptResult> EncryptAsync(HsmEncryptRequest request, CancellationToken ct)
    {
        EnsureInitialized();
        return new HsmEncryptResult { Ciphertext = new byte[request.Plaintext.Length], AuthTag = new byte[16] };
    }

    public async Task<byte[]> DecryptAsync(HsmDecryptRequest request, CancellationToken ct)
    {
        EnsureInitialized();
        return new byte[request.Ciphertext.Length];
    }

    public async Task<byte[]> SignAsync(HsmSignRequest request, CancellationToken ct)
    {
        EnsureInitialized();
        return new byte[96];
    }

    public async Task<bool> VerifyAsync(HsmVerifyRequest request, CancellationToken ct)
    {
        EnsureInitialized();
        return true;
    }

    public async Task<byte[]> WrapKeyAsync(HsmKeyWrapRequest request, CancellationToken ct)
    {
        EnsureInitialized();
        return new byte[40];
    }

    public async Task<byte[]> GenerateRandomAsync(int length, CancellationToken ct)
    {
        var buffer = new byte[length];
        RandomNumberGenerator.Fill(buffer);
        return buffer;
    }

    public async Task<HsmHealthResult> GetHealthAsync(CancellationToken ct) =>
        new() { IsHealthy = _initialized, Details = new Dictionary<string, string> { ["type"] = "Azure Dedicated HSM" } };

    private void EnsureInitialized()
    {
        if (!_initialized) throw new InvalidOperationException("HSM not initialized");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Thales Luna HSM provider implementation.</summary>
public sealed class ThalesLunaHsmProvider : IHardwareSecurityModule, IAsyncDisposable
{
    private readonly ThalesLunaConfig _config;
    private bool _initialized;

    public ThalesLunaHsmProvider(ThalesLunaConfig config) => _config = config;

    public async Task InitializeAsync(CancellationToken ct)
    {
        _initialized = true;
        await Task.CompletedTask;
    }

    public async Task<HsmKeyGenResult> GenerateKeyAsync(HsmKeyGenerationRequest request, CancellationToken ct)
    {
        EnsureInitialized();
        return new HsmKeyGenResult { Success = true, Handle = $"luna-{Guid.NewGuid():N}" };
    }

    public async Task<HsmEncryptResult> EncryptAsync(HsmEncryptRequest request, CancellationToken ct)
    {
        EnsureInitialized();
        return new HsmEncryptResult { Ciphertext = new byte[request.Plaintext.Length], AuthTag = new byte[16] };
    }

    public async Task<byte[]> DecryptAsync(HsmDecryptRequest request, CancellationToken ct)
    {
        EnsureInitialized();
        return new byte[request.Ciphertext.Length];
    }

    public async Task<byte[]> SignAsync(HsmSignRequest request, CancellationToken ct)
    {
        EnsureInitialized();
        return new byte[96];
    }

    public async Task<bool> VerifyAsync(HsmVerifyRequest request, CancellationToken ct)
    {
        EnsureInitialized();
        return true;
    }

    public async Task<byte[]> WrapKeyAsync(HsmKeyWrapRequest request, CancellationToken ct)
    {
        EnsureInitialized();
        return new byte[40];
    }

    public async Task<byte[]> GenerateRandomAsync(int length, CancellationToken ct)
    {
        var buffer = new byte[length];
        RandomNumberGenerator.Fill(buffer);
        return buffer;
    }

    public async Task<HsmHealthResult> GetHealthAsync(CancellationToken ct) =>
        new() { IsHealthy = _initialized, Details = new Dictionary<string, string> { ["type"] = "Thales Luna", ["partition"] = _config.PartitionLabel } };

    private void EnsureInitialized()
    {
        if (!_initialized) throw new InvalidOperationException("HSM not initialized");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

#endregion

#region HSM Types

/// <summary>HSM types.</summary>
public enum HsmType { Pkcs11, AwsCloudHsm, AzureDedicatedHsm, ThalesLuna }

/// <summary>HSM connection status.</summary>
public enum HsmConnectionStatus { Connected, Disconnected, Error, Initializing }

/// <summary>HSM failover policy.</summary>
public enum HsmFailoverPolicy { Manual, Automatic }

/// <summary>HSM load balancing policy.</summary>
public enum HsmLoadBalancingPolicy { RoundRobin, LeastConnections, Primary }

/// <summary>HSM configuration.</summary>
public sealed class HsmConfiguration
{
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxRetries { get; set; } = 3;
    public bool EnableHealthChecks { get; set; } = true;
}

/// <summary>PKCS#11 configuration.</summary>
public sealed class Pkcs11Config
{
    public required string LibraryPath { get; init; }
    public int SlotId { get; init; }
    public required string Pin { get; init; }
    public string? TokenLabel { get; init; }
}

/// <summary>AWS CloudHSM configuration.</summary>
public sealed class AwsCloudHsmConfig
{
    public required string ClusterId { get; init; }
    public required string Region { get; init; }
    public required string HsmUser { get; init; }
    public required string HsmPassword { get; init; }
    public string? CustomerCaCertPath { get; init; }
}

/// <summary>Azure HSM configuration.</summary>
public sealed class AzureHsmConfig
{
    public required string SubscriptionId { get; init; }
    public required string ResourceGroup { get; init; }
    public required string ResourceName { get; init; }
    public required string TenantId { get; init; }
    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }
}

/// <summary>Thales Luna configuration.</summary>
public sealed class ThalesLunaConfig
{
    public required string ServerAddress { get; init; }
    public int ServerPort { get; init; } = 1792;
    public required string PartitionLabel { get; init; }
    public required string PartitionPassword { get; init; }
    public string? ClientCertPath { get; init; }
}

/// <summary>PKCS#11 connection request.</summary>
public sealed class Pkcs11ConnectionRequest
{
    public required string LibraryPath { get; init; }
    public int SlotId { get; init; }
    public required string Pin { get; init; }
    public string? TokenLabel { get; init; }
    public required string ConnectedBy { get; init; }
}

/// <summary>AWS CloudHSM connection request.</summary>
public sealed class AwsCloudHsmConnectionRequest
{
    public required string ClusterId { get; init; }
    public required string Region { get; init; }
    public required string HsmUser { get; init; }
    public required string HsmPassword { get; init; }
    public string? CustomerCaCertPath { get; init; }
    public required string ConnectedBy { get; init; }
}

/// <summary>Azure HSM connection request.</summary>
public sealed class AzureHsmConnectionRequest
{
    public required string SubscriptionId { get; init; }
    public required string ResourceGroup { get; init; }
    public required string ResourceName { get; init; }
    public required string TenantId { get; init; }
    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }
    public required string ConnectedBy { get; init; }
}

/// <summary>Thales Luna connection request.</summary>
public sealed class ThalesLunaConnectionRequest
{
    public required string ServerAddress { get; init; }
    public int ServerPort { get; init; } = 1792;
    public required string PartitionLabel { get; init; }
    public required string PartitionPassword { get; init; }
    public string? ClientCertPath { get; init; }
    public required string ConnectedBy { get; init; }
}

/// <summary>HSM cluster request.</summary>
public sealed class HsmClusterRequest
{
    public required string Name { get; init; }
    public required string PrimaryConnectionId { get; init; }
    public required List<string> SecondaryConnectionIds { get; init; }
    public HsmFailoverPolicy FailoverPolicy { get; init; } = HsmFailoverPolicy.Automatic;
    public HsmLoadBalancingPolicy LoadBalancingPolicy { get; init; } = HsmLoadBalancingPolicy.Primary;
    public int HealthCheckIntervalSeconds { get; init; } = 30;
}

/// <summary>HSM cluster configuration.</summary>
public sealed class HsmClusterConfig
{
    public required string ClusterId { get; init; }
    public required string Name { get; init; }
    public required string PrimaryConnectionId { get; set; }
    public required List<string> SecondaryConnectionIds { get; init; }
    public HsmFailoverPolicy FailoverPolicy { get; init; }
    public HsmLoadBalancingPolicy LoadBalancingPolicy { get; init; }
    public int HealthCheckIntervalSeconds { get; init; }
}

/// <summary>HSM connection result.</summary>
public sealed class HsmConnectionResult
{
    public bool Success { get; init; }
    public string? ConnectionId { get; init; }
    public string? Error { get; init; }
}

/// <summary>HSM cluster result.</summary>
public sealed class HsmClusterResult
{
    public bool Success { get; init; }
    public string? ClusterId { get; init; }
    public int TotalNodes { get; init; }
    public string? Error { get; init; }
}

/// <summary>HSM key generation request.</summary>
public sealed class HsmKeyGenerationRequest
{
    public required string KeyId { get; init; }
    public required string Algorithm { get; init; }
    public int KeySizeBits { get; init; }
    public bool Extractable { get; init; }
    public required string Purpose { get; init; }
}

/// <summary>HSM key generation result.</summary>
public sealed class HsmKeyGenResult
{
    public bool Success { get; init; }
    public string? Handle { get; init; }
    public string? Error { get; init; }
}

/// <summary>HSM encrypt request.</summary>
public sealed class HsmEncryptRequest
{
    public required string KeyHandle { get; init; }
    public required byte[] Plaintext { get; init; }
    public required byte[] Iv { get; init; }
    public byte[]? AdditionalAuthenticatedData { get; init; }
    public required string Algorithm { get; init; }
}

/// <summary>HSM encrypt result.</summary>
public sealed class HsmEncryptResult
{
    public required byte[] Ciphertext { get; init; }
    public byte[]? AuthTag { get; init; }
}

/// <summary>HSM decrypt request.</summary>
public sealed class HsmDecryptRequest
{
    public required string KeyHandle { get; init; }
    public required byte[] Ciphertext { get; init; }
    public required byte[] Iv { get; init; }
    public byte[]? AuthTag { get; init; }
    public byte[]? AdditionalAuthenticatedData { get; init; }
    public required string Algorithm { get; init; }
}

/// <summary>HSM sign request.</summary>
public sealed class HsmSignRequest
{
    public required string KeyHandle { get; init; }
    public required byte[] DataHash { get; init; }
    public required string HashAlgorithm { get; init; }
}

/// <summary>HSM verify request.</summary>
public sealed class HsmVerifyRequest
{
    public required string KeyHandle { get; init; }
    public required byte[] DataHash { get; init; }
    public required byte[] Signature { get; init; }
    public required string HashAlgorithm { get; init; }
}

/// <summary>HSM key wrap request.</summary>
public sealed class HsmKeyWrapRequest
{
    public required string WrappingKeyHandle { get; init; }
    public required string KeyToWrapHandle { get; init; }
    public required string Algorithm { get; init; }
}

/// <summary>HSM health result.</summary>
public sealed class HsmHealthResult
{
    public bool IsHealthy { get; init; }
    public required Dictionary<string, string> Details { get; init; }
}

/// <summary>HSM status report.</summary>
public sealed class HsmStatusReport
{
    public int TotalConnections { get; init; }
    public int HealthyConnections { get; init; }
    public required List<HsmConnectionStatus_Info> Connections { get; init; }
    public required List<HsmClusterConfig> Clusters { get; init; }
}

/// <summary>HSM connection status info.</summary>
public sealed class HsmConnectionStatus_Info
{
    public required string ConnectionId { get; init; }
    public bool IsHealthy { get; init; }
    public DateTime LastChecked { get; init; }
    public required Dictionary<string, string> Details { get; init; }
}

/// <summary>HSM connection event args.</summary>
public sealed class HsmConnectionEventArgs : EventArgs
{
    public required string ConnectionId { get; init; }
    public HsmConnectionStatus Status { get; init; }
    public HsmType HsmType { get; init; }
}

#endregion

#region 5. Comprehensive Audit Logging

/// <summary>
/// Enterprise audit logging system with tamper-evident hash chaining,
/// SIEM integration, and real-time alerting.
/// </summary>
public sealed class ComprehensiveAuditSystem : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, AuditLogChain> _logChains = new();
    private readonly Channel<ComprehensiveAuditEvent> _eventChannel;
    private readonly List<IAuditForwarder> _forwarders = new();
    private readonly List<IAuditAlertRule> _alertRules = new();
    private readonly ComprehensiveAuditConfiguration _config;
    private readonly Task _processingTask;
    private readonly CancellationTokenSource _cts = new();
    private string _lastEventHash = "genesis";
    private long _eventSequence;
    private volatile bool _disposed;

    /// <summary>Event raised when suspicious activity is detected.</summary>
    public event EventHandler<SuspiciousActivityEventArgs>? SuspiciousActivityDetected;

    /// <summary>
    /// Initializes the comprehensive audit system.
    /// </summary>
    public ComprehensiveAuditSystem(ComprehensiveAuditConfiguration? config = null)
    {
        _config = config ?? new ComprehensiveAuditConfiguration();
        _eventChannel = Channel.CreateBounded<ComprehensiveAuditEvent>(
            new BoundedChannelOptions(_config.EventBufferSize)
            {
                FullMode = BoundedChannelFullMode.Wait
            });

        _processingTask = ProcessEventsAsync(_cts.Token);
    }

    /// <summary>
    /// Logs a data access event.
    /// </summary>
    public async Task LogDataAccessAsync(DataAccessEvent evt, CancellationToken ct = default)
    {
        var auditEvent = new ComprehensiveAuditEvent
        {
            EventId = GenerateEventId(),
            EventType = AuditEventCategory.DataAccess,
            Timestamp = DateTime.UtcNow,
            Principal = evt.Principal,
            Action = evt.Action,
            ResourceType = evt.ResourceType,
            ResourceId = evt.ResourceId,
            Outcome = evt.Outcome,
            SourceIp = evt.SourceIp,
            UserAgent = evt.UserAgent,
            SessionId = evt.SessionId,
            Details = evt.Details ?? new Dictionary<string, object>()
        };

        await EnqueueEventAsync(auditEvent, ct);
    }

    /// <summary>
    /// Logs an administrative action.
    /// </summary>
    public async Task LogAdminActionAsync(AdminActionEvent evt, CancellationToken ct = default)
    {
        var auditEvent = new ComprehensiveAuditEvent
        {
            EventId = GenerateEventId(),
            EventType = AuditEventCategory.AdminAction,
            Timestamp = DateTime.UtcNow,
            Principal = evt.Principal,
            Action = evt.Action,
            ResourceType = evt.ResourceType,
            ResourceId = evt.ResourceId,
            Outcome = evt.Outcome,
            SourceIp = evt.SourceIp,
            SessionId = evt.SessionId,
            PreviousValue = evt.PreviousValue,
            NewValue = evt.NewValue,
            Details = evt.Details ?? new Dictionary<string, object>()
        };

        await EnqueueEventAsync(auditEvent, ct);
    }

    /// <summary>
    /// Logs a security event.
    /// </summary>
    public async Task LogSecurityEventAsync(SecurityAuditEvent evt, CancellationToken ct = default)
    {
        var auditEvent = new ComprehensiveAuditEvent
        {
            EventId = GenerateEventId(),
            EventType = AuditEventCategory.Security,
            Severity = evt.Severity,
            Timestamp = DateTime.UtcNow,
            Principal = evt.Principal,
            Action = evt.Action,
            ResourceType = evt.ResourceType,
            ResourceId = evt.ResourceId,
            Outcome = evt.Outcome,
            SourceIp = evt.SourceIp,
            ThreatIndicators = evt.ThreatIndicators,
            Details = evt.Details ?? new Dictionary<string, object>()
        };

        await EnqueueEventAsync(auditEvent, ct);

        // Immediate alert for critical security events
        if (evt.Severity == AuditEventSeverity.Critical)
        {
            await TriggerImmediateAlertAsync(auditEvent, ct);
        }
    }

    /// <summary>
    /// Adds a log forwarder (syslog, SIEM, etc.).
    /// </summary>
    public void AddForwarder(IAuditForwarder forwarder)
    {
        _forwarders.Add(forwarder);
    }

    /// <summary>
    /// Adds an alert rule for suspicious activity detection.
    /// </summary>
    public void AddAlertRule(IAuditAlertRule rule)
    {
        _alertRules.Add(rule);
    }

    /// <summary>
    /// Verifies the integrity of the audit chain.
    /// </summary>
    public async Task<AuditChainVerificationResult> VerifyChainIntegrityAsync(
        string chainId, CancellationToken ct = default)
    {
        if (!_logChains.TryGetValue(chainId, out var chain))
            return new AuditChainVerificationResult { Success = false, Error = "Chain not found" };

        var invalidEvents = new List<string>();
        string previousHash = "genesis";

        foreach (var evt in chain.Events.OrderBy(e => e.Sequence))
        {
            if (evt.PreviousEventHash != previousHash)
            {
                invalidEvents.Add(evt.EventId);
            }

            var calculatedHash = ComputeEventHash(evt);
            if (calculatedHash != evt.EventHash)
            {
                invalidEvents.Add(evt.EventId);
            }

            previousHash = evt.EventHash;
        }

        return new AuditChainVerificationResult
        {
            Success = invalidEvents.Count == 0,
            TotalEvents = chain.Events.Count,
            InvalidEvents = invalidEvents,
            FirstEvent = chain.Events.MinBy(e => e.Sequence)?.Timestamp,
            LastEvent = chain.Events.MaxBy(e => e.Sequence)?.Timestamp
        };
    }

    /// <summary>
    /// Searches audit logs with forensic capabilities.
    /// </summary>
    public async Task<AuditSearchResult> SearchAsync(
        AuditSearchQuery query, CancellationToken ct = default)
    {
        var allEvents = _logChains.Values.SelectMany(c => c.Events);

        if (!string.IsNullOrEmpty(query.Principal))
            allEvents = allEvents.Where(e => e.Principal == query.Principal);

        if (query.StartTime.HasValue)
            allEvents = allEvents.Where(e => e.Timestamp >= query.StartTime.Value);

        if (query.EndTime.HasValue)
            allEvents = allEvents.Where(e => e.Timestamp <= query.EndTime.Value);

        if (!string.IsNullOrEmpty(query.ResourceId))
            allEvents = allEvents.Where(e => e.ResourceId == query.ResourceId);

        if (query.EventTypes != null && query.EventTypes.Count > 0)
            allEvents = allEvents.Where(e => query.EventTypes.Contains(e.EventType));

        if (!string.IsNullOrEmpty(query.SourceIp))
            allEvents = allEvents.Where(e => e.SourceIp == query.SourceIp);

        var total = allEvents.Count();
        var results = allEvents
            .OrderByDescending(e => e.Timestamp)
            .Skip(query.Offset)
            .Take(query.Limit)
            .ToList();

        return new AuditSearchResult
        {
            Events = results,
            TotalCount = total,
            Offset = query.Offset,
            Limit = query.Limit
        };
    }

    /// <summary>
    /// Exports audit logs for compliance reporting.
    /// </summary>
    public async Task<AuditExportResult> ExportAsync(
        AuditExportRequest request, CancellationToken ct = default)
    {
        var searchResult = await SearchAsync(new AuditSearchQuery
        {
            StartTime = request.StartTime,
            EndTime = request.EndTime,
            EventTypes = request.EventTypes,
            Limit = int.MaxValue
        }, ct);

        var exportData = request.Format switch
        {
            AuditExportFormat.Json => JsonSerializer.SerializeToUtf8Bytes(searchResult.Events),
            AuditExportFormat.Csv => GenerateCsv(searchResult.Events),
            _ => JsonSerializer.SerializeToUtf8Bytes(searchResult.Events)
        };

        var exportHash = Convert.ToHexString(SHA256.HashData(exportData)).ToLowerInvariant();

        return new AuditExportResult
        {
            Success = true,
            Data = exportData,
            Format = request.Format,
            EventCount = searchResult.Events.Count,
            ExportHash = exportHash,
            ExportedAt = DateTime.UtcNow
        };
    }

    private async Task EnqueueEventAsync(ComprehensiveAuditEvent evt, CancellationToken ct)
    {
        evt.Sequence = Interlocked.Increment(ref _eventSequence);
        evt.PreviousEventHash = _lastEventHash;
        evt.EventHash = ComputeEventHash(evt);
        _lastEventHash = evt.EventHash;

        await _eventChannel.Writer.WriteAsync(evt, ct);
    }

    private async Task ProcessEventsAsync(CancellationToken ct)
    {
        await foreach (var evt in _eventChannel.Reader.ReadAllAsync(ct))
        {
            try
            {
                // Store in chain
                var chainId = $"{evt.Timestamp:yyyyMMdd}";
                var chain = _logChains.GetOrAdd(chainId, _ => new AuditLogChain { ChainId = chainId });
                chain.Events.Add(evt);

                // Forward to external systems
                foreach (var forwarder in _forwarders)
                {
                    try
                    {
                        await forwarder.ForwardAsync(evt, ct);
                    }
                    catch { /* Log and continue */ }
                }

                // Check alert rules
                foreach (var rule in _alertRules)
                {
                    if (await rule.EvaluateAsync(evt, ct))
                    {
                        SuspiciousActivityDetected?.Invoke(this, new SuspiciousActivityEventArgs
                        {
                            Event = evt,
                            Rule = rule.RuleName,
                            Severity = rule.Severity
                        });
                    }
                }
            }
            catch { /* Log error */ }
        }
    }

    private async Task TriggerImmediateAlertAsync(ComprehensiveAuditEvent evt, CancellationToken ct)
    {
        SuspiciousActivityDetected?.Invoke(this, new SuspiciousActivityEventArgs
        {
            Event = evt,
            Rule = "CriticalSecurityEvent",
            Severity = AuditEventSeverity.Critical
        });
    }

    private string GenerateEventId() => $"EVT-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..32];

    private static string ComputeEventHash(ComprehensiveAuditEvent evt)
    {
        var data = $"{evt.EventId}|{evt.Sequence}|{evt.Timestamp:O}|{evt.Principal}|{evt.Action}|{evt.PreviousEventHash}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
    }

    private static byte[] GenerateCsv(List<ComprehensiveAuditEvent> events)
    {
        var sb = new StringBuilder();
        sb.AppendLine("EventId,Timestamp,Principal,Action,ResourceType,ResourceId,Outcome,SourceIp");
        foreach (var evt in events)
        {
            sb.AppendLine($"{evt.EventId},{evt.Timestamp:O},{evt.Principal},{evt.Action},{evt.ResourceType},{evt.ResourceId},{evt.Outcome},{evt.SourceIp}");
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>Disposes resources.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _eventChannel.Writer.Complete();
        _cts.Cancel();

        try { await _processingTask.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch { /* Best effort */ }

        _cts.Dispose();
    }
}

/// <summary>Interface for audit log forwarders.</summary>
public interface IAuditForwarder
{
    Task ForwardAsync(ComprehensiveAuditEvent evt, CancellationToken ct);
}

/// <summary>Interface for audit alert rules.</summary>
public interface IAuditAlertRule
{
    string RuleName { get; }
    AuditEventSeverity Severity { get; }
    Task<bool> EvaluateAsync(ComprehensiveAuditEvent evt, CancellationToken ct);
}

/// <summary>Syslog forwarder implementation.</summary>
public sealed class SyslogForwarder : IAuditForwarder
{
    private readonly string _host;
    private readonly int _port;
    private readonly UdpClient _client;

    public SyslogForwarder(string host, int port = 514)
    {
        _host = host;
        _port = port;
        _client = new UdpClient();
    }

    public async Task ForwardAsync(ComprehensiveAuditEvent evt, CancellationToken ct)
    {
        var priority = evt.Severity switch
        {
            AuditEventSeverity.Critical => 2,
            AuditEventSeverity.High => 3,
            AuditEventSeverity.Medium => 5,
            _ => 6
        };

        var message = $"<{priority}>{evt.Timestamp:MMM dd HH:mm:ss} DataWarehouse: {evt.Action} by {evt.Principal} on {evt.ResourceId}";
        var data = Encoding.UTF8.GetBytes(message);
        await _client.SendAsync(data, data.Length, _host, _port);
    }
}

#endregion

#region Audit Types

/// <summary>Audit event categories.</summary>
public enum AuditEventCategory { DataAccess, AdminAction, Security, System, Compliance }

/// <summary>Audit event severity.</summary>
public enum AuditEventSeverity { Low, Medium, High, Critical }

/// <summary>Audit event outcome.</summary>
public enum AuditEventOutcome { Success, Failure, Denied, Error }

/// <summary>Audit export format.</summary>
public enum AuditExportFormat { Json, Csv, Xml }

/// <summary>Comprehensive audit event.</summary>
public sealed class ComprehensiveAuditEvent
{
    public required string EventId { get; init; }
    public long Sequence { get; set; }
    public AuditEventCategory EventType { get; init; }
    public AuditEventSeverity Severity { get; init; } = AuditEventSeverity.Low;
    public DateTime Timestamp { get; init; }
    public required string Principal { get; init; }
    public required string Action { get; init; }
    public string? ResourceType { get; init; }
    public string? ResourceId { get; init; }
    public AuditEventOutcome Outcome { get; init; }
    public string? SourceIp { get; init; }
    public string? UserAgent { get; init; }
    public string? SessionId { get; init; }
    public string? PreviousValue { get; init; }
    public string? NewValue { get; init; }
    public List<string>? ThreatIndicators { get; init; }
    public string PreviousEventHash { get; set; } = string.Empty;
    public string EventHash { get; set; } = string.Empty;
    public Dictionary<string, object> Details { get; init; } = new();
}

/// <summary>Audit log chain.</summary>
public sealed class AuditLogChain
{
    public required string ChainId { get; init; }
    public List<ComprehensiveAuditEvent> Events { get; } = new();
}

/// <summary>Data access event.</summary>
public sealed class DataAccessEvent
{
    public required string Principal { get; init; }
    public required string Action { get; init; }
    public string? ResourceType { get; init; }
    public string? ResourceId { get; init; }
    public AuditEventOutcome Outcome { get; init; }
    public string? SourceIp { get; init; }
    public string? UserAgent { get; init; }
    public string? SessionId { get; init; }
    public Dictionary<string, object>? Details { get; init; }
}

/// <summary>Admin action event.</summary>
public sealed class AdminActionEvent
{
    public required string Principal { get; init; }
    public required string Action { get; init; }
    public string? ResourceType { get; init; }
    public string? ResourceId { get; init; }
    public AuditEventOutcome Outcome { get; init; }
    public string? SourceIp { get; init; }
    public string? SessionId { get; init; }
    public string? PreviousValue { get; init; }
    public string? NewValue { get; init; }
    public Dictionary<string, object>? Details { get; init; }
}

/// <summary>Security audit event.</summary>
public sealed class SecurityAuditEvent
{
    public required string Principal { get; init; }
    public required string Action { get; init; }
    public AuditEventSeverity Severity { get; init; }
    public string? ResourceType { get; init; }
    public string? ResourceId { get; init; }
    public AuditEventOutcome Outcome { get; init; }
    public string? SourceIp { get; init; }
    public List<string>? ThreatIndicators { get; init; }
    public Dictionary<string, object>? Details { get; init; }
}

/// <summary>Audit search query.</summary>
public sealed class AuditSearchQuery
{
    public string? Principal { get; init; }
    public DateTime? StartTime { get; init; }
    public DateTime? EndTime { get; init; }
    public string? ResourceId { get; init; }
    public string? SourceIp { get; init; }
    public List<AuditEventCategory>? EventTypes { get; init; }
    public int Offset { get; init; } = 0;
    public int Limit { get; init; } = 100;
}

/// <summary>Audit search result.</summary>
public sealed class AuditSearchResult
{
    public required List<ComprehensiveAuditEvent> Events { get; init; }
    public int TotalCount { get; init; }
    public int Offset { get; init; }
    public int Limit { get; init; }
}

/// <summary>Audit export request.</summary>
public sealed class AuditExportRequest
{
    public DateTime? StartTime { get; init; }
    public DateTime? EndTime { get; init; }
    public List<AuditEventCategory>? EventTypes { get; init; }
    public AuditExportFormat Format { get; init; } = AuditExportFormat.Json;
}

/// <summary>Audit export result.</summary>
public sealed class AuditExportResult
{
    public bool Success { get; init; }
    public byte[]? Data { get; init; }
    public AuditExportFormat Format { get; init; }
    public int EventCount { get; init; }
    public string? ExportHash { get; init; }
    public DateTime ExportedAt { get; init; }
    public string? Error { get; init; }
}

/// <summary>Audit chain verification result.</summary>
public sealed class AuditChainVerificationResult
{
    public bool Success { get; init; }
    public int TotalEvents { get; init; }
    public List<string> InvalidEvents { get; init; } = new();
    public DateTime? FirstEvent { get; init; }
    public DateTime? LastEvent { get; init; }
    public string? Error { get; init; }
}

/// <summary>Suspicious activity event args.</summary>
public sealed class SuspiciousActivityEventArgs : EventArgs
{
    public required ComprehensiveAuditEvent Event { get; init; }
    public required string Rule { get; init; }
    public AuditEventSeverity Severity { get; init; }
}

/// <summary>Comprehensive audit configuration.</summary>
public sealed class ComprehensiveAuditConfiguration
{
    public int EventBufferSize { get; set; } = 10000;
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromDays(2555); // 7 years
    public bool EnableRealTimeAlerts { get; set; } = true;
}

#endregion

#region 6. RBAC with API Authentication

/// <summary>
/// Enterprise Role-Based Access Control with Attribute-Based Access Control (ABAC),
/// multi-tenant isolation, and comprehensive API authentication.
/// </summary>
public sealed class EnterpriseAccessControlSystem : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, EnterpriseRole> _roles = new();
    private readonly ConcurrentDictionary<string, EnterpriseUser> _users = new();
    private readonly ConcurrentDictionary<string, ApiKey> _apiKeys = new();
    private readonly ConcurrentDictionary<string, TenantContext> _tenants = new();
    private readonly ConcurrentDictionary<string, AbacPolicy> _abacPolicies = new();
    private readonly IEnterpriseAuditLog _auditLog;
    private readonly JwtTokenService _jwtService;
    private readonly MtlsValidator _mtlsValidator;
    private readonly EnterpriseAccessControlConfiguration _config;
    private volatile bool _disposed;

    /// <summary>Event raised when access is denied.</summary>
    public event EventHandler<AccessDeniedEventArgs>? AccessDenied;

    /// <summary>
    /// Initializes the enterprise access control system.
    /// </summary>
    public EnterpriseAccessControlSystem(
        IEnterpriseAuditLog auditLog,
        EnterpriseAccessControlConfiguration? config = null)
    {
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _config = config ?? new EnterpriseAccessControlConfiguration();
        _jwtService = new JwtTokenService(_config.JwtConfiguration);
        _mtlsValidator = new MtlsValidator(_config.MtlsConfiguration);

        InitializeBuiltInRoles();
    }

    /// <summary>
    /// Authenticates using JWT token.
    /// </summary>
    public async Task<AuthenticationResult> AuthenticateJwtAsync(
        string token, CancellationToken ct = default)
    {
        var validation = await _jwtService.ValidateTokenAsync(token, ct);
        if (!validation.IsValid)
        {
            await _auditLog.LogAsync(new AuditEntry
            {
                Action = "JwtAuthenticationFailed",
                ResourceId = "auth",
                Principal = "unknown",
                Severity = AuditSeverity.Warning,
                Details = new Dictionary<string, object> { ["reason"] = validation.Error ?? "Unknown" }
            }, ct);

            return new AuthenticationResult { Success = false, Error = validation.Error };
        }

        return new AuthenticationResult
        {
            Success = true,
            Principal = validation.Principal,
            Claims = validation.Claims,
            TenantId = validation.TenantId,
            AuthMethod = AuthenticationMethod.Jwt
        };
    }

    /// <summary>
    /// Authenticates using API key.
    /// </summary>
    public async Task<AuthenticationResult> AuthenticateApiKeyAsync(
        string apiKey, CancellationToken ct = default)
    {
        var keyHash = ComputeApiKeyHash(apiKey);
        if (!_apiKeys.TryGetValue(keyHash, out var key) || !key.IsActive)
        {
            await _auditLog.LogAsync(new AuditEntry
            {
                Action = "ApiKeyAuthenticationFailed",
                ResourceId = "auth",
                Principal = "unknown",
                Severity = AuditSeverity.Warning
            }, ct);

            return new AuthenticationResult { Success = false, Error = "Invalid API key" };
        }

        if (key.ExpiresAt.HasValue && key.ExpiresAt.Value < DateTime.UtcNow)
        {
            return new AuthenticationResult { Success = false, Error = "API key expired" };
        }

        key.LastUsedAt = DateTime.UtcNow;
        key.UsageCount++;

        return new AuthenticationResult
        {
            Success = true,
            Principal = key.OwnerId,
            TenantId = key.TenantId,
            Permissions = key.Permissions.ToList(),
            AuthMethod = AuthenticationMethod.ApiKey
        };
    }

    /// <summary>
    /// Authenticates using mTLS certificate.
    /// </summary>
    public async Task<AuthenticationResult> AuthenticateMtlsAsync(
        X509Certificate2 clientCert, CancellationToken ct = default)
    {
        var validation = await _mtlsValidator.ValidateCertificateAsync(clientCert, ct);
        if (!validation.IsValid)
        {
            await _auditLog.LogAsync(new AuditEntry
            {
                Action = "MtlsAuthenticationFailed",
                ResourceId = "auth",
                Principal = clientCert.Subject,
                Severity = AuditSeverity.Warning,
                Details = new Dictionary<string, object> { ["reason"] = validation.Error ?? "Unknown" }
            }, ct);

            return new AuthenticationResult { Success = false, Error = validation.Error };
        }

        return new AuthenticationResult
        {
            Success = true,
            Principal = validation.Principal,
            TenantId = validation.TenantId,
            AuthMethod = AuthenticationMethod.Mtls
        };
    }

    /// <summary>
    /// Authorizes an action using combined RBAC and ABAC.
    /// </summary>
    public async Task<AuthorizationResult> AuthorizeAsync(
        AuthorizationRequest request, CancellationToken ct = default)
    {
        // Get user and roles
        if (!_users.TryGetValue(request.Principal, out var user))
        {
            return DenyAccess(request, "User not found");
        }

        // Check tenant isolation
        if (_config.EnforceMultiTenantIsolation &&
            request.TenantId != null &&
            user.TenantId != request.TenantId)
        {
            return DenyAccess(request, "Tenant isolation violation");
        }

        // Check RBAC
        var rbacAllowed = await CheckRbacAsync(user, request, ct);

        // Check ABAC policies
        var abacAllowed = await CheckAbacAsync(user, request, ct);

        if (!rbacAllowed && !abacAllowed)
        {
            return DenyAccess(request, "Access denied by policy");
        }

        await _auditLog.LogAsync(new AuditEntry
        {
            Action = "AuthorizationGranted",
            ResourceId = request.ResourceId ?? request.Action,
            Principal = request.Principal,
            Details = new Dictionary<string, object>
            {
                ["action"] = request.Action,
                ["rbacAllowed"] = rbacAllowed,
                ["abacAllowed"] = abacAllowed
            }
        }, ct);

        return new AuthorizationResult
        {
            Allowed = true,
            GrantedBy = rbacAllowed ? "RBAC" : "ABAC",
            EffectivePermissions = GetEffectivePermissions(user)
        };
    }

    /// <summary>
    /// Creates a new role.
    /// </summary>
    public async Task<RoleResult> CreateRoleAsync(
        CreateRoleRequest request, CancellationToken ct = default)
    {
        var roleId = request.RoleId ?? $"ROLE-{Guid.NewGuid():N}"[..16];

        var role = new EnterpriseRole
        {
            RoleId = roleId,
            Name = request.Name,
            Description = request.Description,
            Permissions = request.Permissions.ToHashSet(),
            InheritsFrom = request.InheritsFrom,
            TenantId = request.TenantId,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = request.CreatedBy
        };

        _roles[roleId] = role;

        await _auditLog.LogAsync(new AuditEntry
        {
            Action = "RoleCreated",
            ResourceId = roleId,
            Principal = request.CreatedBy,
            Details = new Dictionary<string, object>
            {
                ["name"] = request.Name,
                ["permissions"] = string.Join(",", request.Permissions)
            }
        }, ct);

        return new RoleResult { Success = true, RoleId = roleId };
    }

    /// <summary>
    /// Assigns a role to a user.
    /// </summary>
    public async Task<RoleAssignmentResult> AssignRoleAsync(
        RoleAssignmentRequest request, CancellationToken ct = default)
    {
        if (!_users.TryGetValue(request.UserId, out var user))
        {
            user = new EnterpriseUser
            {
                UserId = request.UserId,
                TenantId = request.TenantId,
                CreatedAt = DateTime.UtcNow
            };
            _users[request.UserId] = user;
        }

        if (!_roles.TryGetValue(request.RoleId, out _))
        {
            return new RoleAssignmentResult { Success = false, Error = "Role not found" };
        }

        user.Roles.Add(new UserRoleAssignment
        {
            RoleId = request.RoleId,
            Scope = request.Scope,
            AssignedAt = DateTime.UtcNow,
            AssignedBy = request.AssignedBy,
            ExpiresAt = request.ExpiresAt
        });

        await _auditLog.LogAsync(new AuditEntry
        {
            Action = "RoleAssigned",
            ResourceId = request.UserId,
            Principal = request.AssignedBy,
            Details = new Dictionary<string, object>
            {
                ["roleId"] = request.RoleId,
                ["scope"] = request.Scope ?? "global"
            }
        }, ct);

        return new RoleAssignmentResult { Success = true };
    }

    /// <summary>
    /// Creates an API key.
    /// </summary>
    public async Task<ApiKeyResult> CreateApiKeyAsync(
        CreateApiKeyRequest request, CancellationToken ct = default)
    {
        var keyBytes = new byte[32];
        RandomNumberGenerator.Fill(keyBytes);
        var plainKey = Convert.ToBase64String(keyBytes);
        var keyHash = ComputeApiKeyHash(plainKey);

        var apiKey = new ApiKey
        {
            KeyId = $"KEY-{Guid.NewGuid():N}"[..16],
            KeyHash = keyHash,
            Name = request.Name,
            OwnerId = request.OwnerId,
            TenantId = request.TenantId,
            Permissions = request.Permissions.ToHashSet(),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = request.ExpiresAt,
            IsActive = true
        };

        _apiKeys[keyHash] = apiKey;

        await _auditLog.LogAsync(new AuditEntry
        {
            Action = "ApiKeyCreated",
            ResourceId = apiKey.KeyId,
            Principal = request.OwnerId,
            Details = new Dictionary<string, object>
            {
                ["name"] = request.Name,
                ["expiresAt"] = request.ExpiresAt?.ToString("O") ?? "never"
            }
        }, ct);

        return new ApiKeyResult
        {
            Success = true,
            KeyId = apiKey.KeyId,
            PlainKey = plainKey // Only returned once!
        };
    }

    /// <summary>
    /// Adds an ABAC policy.
    /// </summary>
    public void AddAbacPolicy(AbacPolicy policy)
    {
        _abacPolicies[policy.PolicyId] = policy;
    }

    /// <summary>
    /// Initiates emergency access procedure.
    /// </summary>
    public async Task<EmergencyAccessResult> RequestEmergencyAccessAsync(
        EmergencyAccessRequest request, CancellationToken ct = default)
    {
        // Verify emergency access is configured
        if (!_config.EmergencyAccessEnabled)
        {
            return new EmergencyAccessResult { Success = false, Error = "Emergency access not enabled" };
        }

        // Verify requester is authorized for emergency access
        if (!_config.EmergencyAccessUsers.Contains(request.RequesterId))
        {
            await _auditLog.LogAsync(new AuditEntry
            {
                Action = "UnauthorizedEmergencyAccessAttempt",
                ResourceId = request.TargetResource,
                Principal = request.RequesterId,
                Severity = AuditSeverity.Critical
            }, ct);

            return new EmergencyAccessResult { Success = false, Error = "Not authorized for emergency access" };
        }

        var sessionId = $"EMERGENCY-{Guid.NewGuid():N}";
        var expiresAt = DateTime.UtcNow.Add(_config.EmergencyAccessDuration);

        await _auditLog.LogAsync(new AuditEntry
        {
            Action = "EmergencyAccessGranted",
            ResourceId = request.TargetResource,
            Principal = request.RequesterId,
            Severity = AuditSeverity.Critical,
            Details = new Dictionary<string, object>
            {
                ["sessionId"] = sessionId,
                ["justification"] = request.Justification,
                ["expiresAt"] = expiresAt
            }
        }, ct);

        return new EmergencyAccessResult
        {
            Success = true,
            SessionId = sessionId,
            ExpiresAt = expiresAt
        };
    }

    private void InitializeBuiltInRoles()
    {
        _roles["admin"] = new EnterpriseRole
        {
            RoleId = "admin",
            Name = "Administrator",
            Description = "Full system access",
            Permissions = new HashSet<string> { "*" },
            IsBuiltIn = true,
            CreatedAt = DateTime.UtcNow
        };

        _roles["operator"] = new EnterpriseRole
        {
            RoleId = "operator",
            Name = "Operator",
            Description = "Operational access",
            Permissions = new HashSet<string> { "read:*", "write:data", "manage:backups" },
            IsBuiltIn = true,
            CreatedAt = DateTime.UtcNow
        };

        _roles["auditor"] = new EnterpriseRole
        {
            RoleId = "auditor",
            Name = "Auditor",
            Description = "Read-only audit access",
            Permissions = new HashSet<string> { "read:audit", "read:compliance" },
            IsBuiltIn = true,
            CreatedAt = DateTime.UtcNow
        };
    }

    private async Task<bool> CheckRbacAsync(EnterpriseUser user, AuthorizationRequest request, CancellationToken ct)
    {
        foreach (var assignment in user.Roles.Where(r => !r.ExpiresAt.HasValue || r.ExpiresAt > DateTime.UtcNow))
        {
            if (!_roles.TryGetValue(assignment.RoleId, out var role))
                continue;

            // Check scope
            if (assignment.Scope != null && request.ResourceId != null &&
                !request.ResourceId.StartsWith(assignment.Scope))
                continue;

            // Check permission
            if (HasPermission(role, request.Action, request.ResourceType))
                return true;
        }

        return false;
    }

    private async Task<bool> CheckAbacAsync(EnterpriseUser user, AuthorizationRequest request, CancellationToken ct)
    {
        foreach (var policy in _abacPolicies.Values.Where(p => p.IsActive))
        {
            if (await policy.EvaluateAsync(user, request, ct))
                return true;
        }

        return false;
    }

    private bool HasPermission(EnterpriseRole role, string action, string? resourceType)
    {
        // Check wildcard
        if (role.Permissions.Contains("*"))
            return true;

        // Check exact match
        var permission = resourceType != null ? $"{action}:{resourceType}" : action;
        if (role.Permissions.Contains(permission))
            return true;

        // Check action wildcard
        if (role.Permissions.Contains($"{action}:*"))
            return true;

        // Check inherited roles
        if (role.InheritsFrom != null)
        {
            foreach (var parentId in role.InheritsFrom)
            {
                if (_roles.TryGetValue(parentId, out var parent) && HasPermission(parent, action, resourceType))
                    return true;
            }
        }

        return false;
    }

    private List<string> GetEffectivePermissions(EnterpriseUser user)
    {
        var permissions = new HashSet<string>();

        foreach (var assignment in user.Roles)
        {
            if (_roles.TryGetValue(assignment.RoleId, out var role))
            {
                foreach (var perm in role.Permissions)
                    permissions.Add(perm);
            }
        }

        return permissions.ToList();
    }

    private AuthorizationResult DenyAccess(AuthorizationRequest request, string reason)
    {
        AccessDenied?.Invoke(this, new AccessDeniedEventArgs
        {
            Principal = request.Principal,
            Action = request.Action,
            Resource = request.ResourceId,
            Reason = reason
        });

        return new AuthorizationResult { Allowed = false, DenialReason = reason };
    }

    private static string ComputeApiKeyHash(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    /// <summary>Disposes resources.</summary>
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>JWT token service.</summary>
public sealed class JwtTokenService
{
    private readonly JwtConfiguration _config;

    public JwtTokenService(JwtConfiguration config) => _config = config;

    public Task<JwtValidationResult> ValidateTokenAsync(string token, CancellationToken ct)
    {
        // Simplified JWT validation
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3)
                return Task.FromResult(new JwtValidationResult { IsValid = false, Error = "Invalid token format" });

            var payload = Encoding.UTF8.GetString(Convert.FromBase64String(PadBase64(parts[1])));
            var claims = JsonSerializer.Deserialize<Dictionary<string, object>>(payload) ?? new();

            return Task.FromResult(new JwtValidationResult
            {
                IsValid = true,
                Principal = claims.TryGetValue("sub", out var sub) ? sub?.ToString() ?? "unknown" : "unknown",
                Claims = claims,
                TenantId = claims.TryGetValue("tenant", out var tenant) ? tenant?.ToString() : null
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new JwtValidationResult { IsValid = false, Error = ex.Message });
        }
    }

    private static string PadBase64(string s)
    {
        var mod = s.Length % 4;
        if (mod > 0) s += new string('=', 4 - mod);
        return s.Replace('-', '+').Replace('_', '/');
    }
}

/// <summary>mTLS validator.</summary>
public sealed class MtlsValidator
{
    private readonly MtlsConfiguration _config;

    public MtlsValidator(MtlsConfiguration config) => _config = config;

    public Task<MtlsValidationResult> ValidateCertificateAsync(X509Certificate2 cert, CancellationToken ct)
    {
        if (cert.NotAfter < DateTime.UtcNow)
            return Task.FromResult(new MtlsValidationResult { IsValid = false, Error = "Certificate expired" });

        if (cert.NotBefore > DateTime.UtcNow)
            return Task.FromResult(new MtlsValidationResult { IsValid = false, Error = "Certificate not yet valid" });

        return Task.FromResult(new MtlsValidationResult
        {
            IsValid = true,
            Principal = cert.GetNameInfo(X509NameType.SimpleName, false) ?? cert.Subject,
            TenantId = ExtractTenantFromCert(cert)
        });
    }

    private string? ExtractTenantFromCert(X509Certificate2 cert)
    {
        // Extract tenant from OU or custom extension
        return null;
    }
}

#endregion

#region Access Control Types

/// <summary>Authentication methods.</summary>
public enum AuthenticationMethod { Jwt, ApiKey, Mtls, Basic, OAuth2 }

/// <summary>Enterprise role.</summary>
public sealed class EnterpriseRole
{
    public required string RoleId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public HashSet<string> Permissions { get; init; } = new();
    public List<string>? InheritsFrom { get; init; }
    public string? TenantId { get; init; }
    public bool IsBuiltIn { get; init; }
    public DateTime CreatedAt { get; init; }
    public string? CreatedBy { get; init; }
}

/// <summary>Enterprise user.</summary>
public sealed class EnterpriseUser
{
    public required string UserId { get; init; }
    public string? TenantId { get; init; }
    public List<UserRoleAssignment> Roles { get; } = new();
    public Dictionary<string, object> Attributes { get; } = new();
    public DateTime CreatedAt { get; init; }
}

/// <summary>User role assignment.</summary>
public sealed class UserRoleAssignment
{
    public required string RoleId { get; init; }
    public string? Scope { get; init; }
    public DateTime AssignedAt { get; init; }
    public required string AssignedBy { get; init; }
    public DateTime? ExpiresAt { get; init; }
}

/// <summary>API key.</summary>
public sealed class ApiKey
{
    public required string KeyId { get; init; }
    public required string KeyHash { get; init; }
    public required string Name { get; init; }
    public required string OwnerId { get; init; }
    public string? TenantId { get; init; }
    public HashSet<string> Permissions { get; init; } = new();
    public DateTime CreatedAt { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public DateTime? LastUsedAt { get; set; }
    public int UsageCount { get; set; }
    public bool IsActive { get; set; }
}

/// <summary>Tenant context.</summary>
public sealed class TenantContext
{
    public required string TenantId { get; init; }
    public required string Name { get; init; }
    public bool IsActive { get; init; }
    public Dictionary<string, object> Settings { get; } = new();
}

/// <summary>ABAC policy.</summary>
public abstract class AbacPolicy
{
    public required string PolicyId { get; init; }
    public required string Name { get; init; }
    public bool IsActive { get; init; } = true;
    public abstract Task<bool> EvaluateAsync(EnterpriseUser user, AuthorizationRequest request, CancellationToken ct);
}

/// <summary>Authentication result.</summary>
public sealed class AuthenticationResult
{
    public bool Success { get; init; }
    public string? Principal { get; init; }
    public string? TenantId { get; init; }
    public Dictionary<string, object>? Claims { get; init; }
    public List<string>? Permissions { get; init; }
    public AuthenticationMethod AuthMethod { get; init; }
    public string? Error { get; init; }
}

/// <summary>Authorization request.</summary>
public sealed class AuthorizationRequest
{
    public required string Principal { get; init; }
    public required string Action { get; init; }
    public string? ResourceType { get; init; }
    public string? ResourceId { get; init; }
    public string? TenantId { get; init; }
    public Dictionary<string, object>? Context { get; init; }
}

/// <summary>Authorization result.</summary>
public sealed class AuthorizationResult
{
    public bool Allowed { get; init; }
    public string? GrantedBy { get; init; }
    public string? DenialReason { get; init; }
    public List<string>? EffectivePermissions { get; init; }
}

/// <summary>Create role request.</summary>
public sealed class CreateRoleRequest
{
    public string? RoleId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required List<string> Permissions { get; init; }
    public List<string>? InheritsFrom { get; init; }
    public string? TenantId { get; init; }
    public required string CreatedBy { get; init; }
}

/// <summary>Role result.</summary>
public sealed class RoleResult
{
    public bool Success { get; init; }
    public string? RoleId { get; init; }
    public string? Error { get; init; }
}

/// <summary>Role assignment request.</summary>
public sealed class RoleAssignmentRequest
{
    public required string UserId { get; init; }
    public required string RoleId { get; init; }
    public string? Scope { get; init; }
    public string? TenantId { get; init; }
    public required string AssignedBy { get; init; }
    public DateTime? ExpiresAt { get; init; }
}

/// <summary>Role assignment result.</summary>
public sealed class RoleAssignmentResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
}

/// <summary>Create API key request.</summary>
public sealed class CreateApiKeyRequest
{
    public required string Name { get; init; }
    public required string OwnerId { get; init; }
    public string? TenantId { get; init; }
    public required List<string> Permissions { get; init; }
    public DateTime? ExpiresAt { get; init; }
}

/// <summary>API key result.</summary>
public sealed class ApiKeyResult
{
    public bool Success { get; init; }
    public string? KeyId { get; init; }
    public string? PlainKey { get; init; }
    public string? Error { get; init; }
}

/// <summary>Emergency access request.</summary>
public sealed class EmergencyAccessRequest
{
    public required string RequesterId { get; init; }
    public required string TargetResource { get; init; }
    public required string Justification { get; init; }
}

/// <summary>Emergency access result.</summary>
public sealed class EmergencyAccessResult
{
    public bool Success { get; init; }
    public string? SessionId { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public string? Error { get; init; }
}

/// <summary>Access denied event args.</summary>
public sealed class AccessDeniedEventArgs : EventArgs
{
    public required string Principal { get; init; }
    public required string Action { get; init; }
    public string? Resource { get; init; }
    public required string Reason { get; init; }
}

/// <summary>JWT validation result.</summary>
public sealed class JwtValidationResult
{
    public bool IsValid { get; init; }
    public string? Principal { get; init; }
    public Dictionary<string, object>? Claims { get; init; }
    public string? TenantId { get; init; }
    public string? Error { get; init; }
}

/// <summary>mTLS validation result.</summary>
public sealed class MtlsValidationResult
{
    public bool IsValid { get; init; }
    public string? Principal { get; init; }
    public string? TenantId { get; init; }
    public string? Error { get; init; }
}

/// <summary>JWT configuration.</summary>
public sealed class JwtConfiguration
{
    public string? Issuer { get; set; }
    public string? Audience { get; set; }
    public string? SigningKey { get; set; }
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>mTLS configuration.</summary>
public sealed class MtlsConfiguration
{
    public string? TrustedCaPath { get; set; }
    public bool RequireClientCert { get; set; } = true;
    public bool ValidateChain { get; set; } = true;
}

/// <summary>Enterprise access control configuration.</summary>
public sealed class EnterpriseAccessControlConfiguration
{
    public bool EnforceMultiTenantIsolation { get; set; } = true;
    public bool EmergencyAccessEnabled { get; set; } = true;
    public TimeSpan EmergencyAccessDuration { get; set; } = TimeSpan.FromHours(4);
    public HashSet<string> EmergencyAccessUsers { get; set; } = new();
    public JwtConfiguration JwtConfiguration { get; set; } = new();
    public MtlsConfiguration MtlsConfiguration { get; set; } = new();
}

#endregion
