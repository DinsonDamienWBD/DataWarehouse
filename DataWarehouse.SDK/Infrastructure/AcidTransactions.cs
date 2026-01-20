using System.Collections.Concurrent;

namespace DataWarehouse.SDK.Infrastructure;

// ============================================================================
// PHASE 5 - E2: ACID Transactions
// Uses: ITransactionScope from Kernel
// ============================================================================

#region Write-Ahead Log

/// <summary>
/// Write-ahead log for transaction durability.
/// </summary>
public sealed class WriteAheadLog : IAsyncDisposable
{
    private readonly string _logDirectory;
    private readonly ConcurrentDictionary<long, WalEntry> _entries = new();
    private readonly SemaphoreSlim _writeLock = new(1);
    private long _currentLsn;
    private FileStream? _logFile;
    private volatile bool _disposed;

    public WriteAheadLog(string logDirectory)
    {
        _logDirectory = logDirectory;
        if (!Directory.Exists(_logDirectory))
            Directory.CreateDirectory(_logDirectory);
    }

    /// <summary>
    /// Initializes the WAL, recovering from existing log if present.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var logPath = Path.Combine(_logDirectory, "wal.log");
        _logFile = new FileStream(logPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

        // Recover existing entries
        if (_logFile.Length > 0)
        {
            await RecoverAsync(ct);
        }
    }

    /// <summary>
    /// Appends an entry to the WAL.
    /// </summary>
    public async Task<long> AppendAsync(WalEntry entry, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            entry.Lsn = Interlocked.Increment(ref _currentLsn);
            entry.Timestamp = DateTime.UtcNow;

            _entries[entry.Lsn] = entry;

            if (_logFile != null)
            {
                var data = SerializeEntry(entry);
                await _logFile.WriteAsync(data, ct);
                await _logFile.FlushAsync(ct);
            }

            return entry.Lsn;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Commits a transaction by marking its entries as committed.
    /// </summary>
    public async Task CommitAsync(string transactionId, CancellationToken ct = default)
    {
        await AppendAsync(new WalEntry
        {
            TransactionId = transactionId,
            Type = WalEntryType.Commit
        }, ct);
    }

    /// <summary>
    /// Aborts a transaction by writing a rollback entry.
    /// </summary>
    public async Task AbortAsync(string transactionId, CancellationToken ct = default)
    {
        await AppendAsync(new WalEntry
        {
            TransactionId = transactionId,
            Type = WalEntryType.Abort
        }, ct);
    }

    /// <summary>
    /// Truncates the log up to a given LSN.
    /// </summary>
    public void Truncate(long upToLsn)
    {
        foreach (var lsn in _entries.Keys.Where(l => l <= upToLsn))
        {
            _entries.TryRemove(lsn, out _);
        }
    }

    /// <summary>
    /// Gets uncommitted entries for recovery.
    /// </summary>
    public IReadOnlyList<WalEntry> GetUncommittedEntries(string transactionId) =>
        _entries.Values.Where(e => e.TransactionId == transactionId && e.Type == WalEntryType.Data).ToList();

    private async Task RecoverAsync(CancellationToken ct)
    {
        // Simplified recovery - read all entries
        _logFile!.Seek(0, SeekOrigin.Begin);
        using var reader = new BinaryReader(_logFile, System.Text.Encoding.UTF8, leaveOpen: true);

        while (_logFile.Position < _logFile.Length)
        {
            try
            {
                var entry = DeserializeEntry(reader);
                _entries[entry.Lsn] = entry;
                _currentLsn = Math.Max(_currentLsn, entry.Lsn);
            }
            catch
            {
                break; // Corrupted entry, stop recovery
            }
        }
    }

    private byte[] SerializeEntry(WalEntry entry)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write(entry.Lsn);
        writer.Write(entry.TransactionId);
        writer.Write((int)entry.Type);
        writer.Write(entry.Data?.Length ?? 0);
        if (entry.Data != null) writer.Write(entry.Data);
        writer.Write(entry.Timestamp.Ticks);
        return ms.ToArray();
    }

    private WalEntry DeserializeEntry(BinaryReader reader)
    {
        var lsn = reader.ReadInt64();
        var txId = reader.ReadString();
        var type = (WalEntryType)reader.ReadInt32();
        var dataLen = reader.ReadInt32();
        var data = dataLen > 0 ? reader.ReadBytes(dataLen) : null;
        var timestamp = new DateTime(reader.ReadInt64());

        return new WalEntry
        {
            Lsn = lsn,
            TransactionId = txId,
            Type = type,
            Data = data,
            Timestamp = timestamp
        };
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_logFile != null)
        {
            await _logFile.FlushAsync();
            await _logFile.DisposeAsync();
        }
        _writeLock.Dispose();
    }
}

#endregion

#region Transaction Coordinator

/// <summary>
/// Distributed transaction coordinator implementing 2PC protocol.
/// </summary>
public sealed class DistributedTransactionCoordinator : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, DistributedTransaction> _transactions = new();
    private readonly ConcurrentDictionary<string, TransactionParticipant> _participants = new();
    private readonly WriteAheadLog _wal;
    private readonly Timer _timeoutTimer;
    private readonly TransactionConfig _config;
    private volatile bool _disposed;

    public event EventHandler<TransactionEventArgs>? TransactionCommitted;
    public event EventHandler<TransactionEventArgs>? TransactionAborted;

    public DistributedTransactionCoordinator(WriteAheadLog wal, TransactionConfig? config = null)
    {
        _wal = wal;
        _config = config ?? new TransactionConfig();
        _timeoutTimer = new Timer(CheckTimeouts, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Begins a new distributed transaction.
    /// </summary>
    public DistributedTransaction Begin(IsolationLevel isolationLevel = IsolationLevel.ReadCommitted)
    {
        var tx = new DistributedTransaction
        {
            TransactionId = Guid.NewGuid().ToString("N"),
            IsolationLevel = isolationLevel,
            StartedAt = DateTime.UtcNow,
            Timeout = _config.DefaultTimeout,
            State = TransactionState.Active
        };

        _transactions[tx.TransactionId] = tx;
        return tx;
    }

    /// <summary>
    /// Registers a participant in a transaction.
    /// </summary>
    public void RegisterParticipant(string transactionId, TransactionParticipant participant)
    {
        if (_transactions.TryGetValue(transactionId, out var tx))
        {
            tx.Participants.Add(participant.ParticipantId);
            _participants[participant.ParticipantId] = participant;
        }
    }

    /// <summary>
    /// Prepares the transaction (Phase 1 of 2PC).
    /// </summary>
    public async Task<bool> PrepareAsync(string transactionId, CancellationToken ct = default)
    {
        if (!_transactions.TryGetValue(transactionId, out var tx))
            return false;

        tx.State = TransactionState.Preparing;

        // Ask all participants to prepare
        foreach (var participantId in tx.Participants)
        {
            if (_participants.TryGetValue(participantId, out var participant))
            {
                var prepared = await participant.PrepareAsync(transactionId, ct);
                if (!prepared)
                {
                    tx.State = TransactionState.Aborting;
                    return false;
                }
            }
        }

        tx.State = TransactionState.Prepared;
        await _wal.AppendAsync(new WalEntry { TransactionId = transactionId, Type = WalEntryType.Prepare }, ct);
        return true;
    }

    /// <summary>
    /// Commits the transaction (Phase 2 of 2PC).
    /// </summary>
    public async Task<bool> CommitAsync(string transactionId, CancellationToken ct = default)
    {
        if (!_transactions.TryGetValue(transactionId, out var tx))
            return false;

        if (tx.State != TransactionState.Prepared)
        {
            var prepared = await PrepareAsync(transactionId, ct);
            if (!prepared)
            {
                await AbortAsync(transactionId, ct);
                return false;
            }
        }

        tx.State = TransactionState.Committing;

        // Tell all participants to commit
        foreach (var participantId in tx.Participants)
        {
            if (_participants.TryGetValue(participantId, out var participant))
            {
                await participant.CommitAsync(transactionId, ct);
            }
        }

        tx.State = TransactionState.Committed;
        tx.CompletedAt = DateTime.UtcNow;
        await _wal.CommitAsync(transactionId, ct);

        TransactionCommitted?.Invoke(this, new TransactionEventArgs { Transaction = tx });
        return true;
    }

    /// <summary>
    /// Aborts the transaction.
    /// </summary>
    public async Task AbortAsync(string transactionId, CancellationToken ct = default)
    {
        if (!_transactions.TryGetValue(transactionId, out var tx))
            return;

        tx.State = TransactionState.Aborting;

        // Tell all participants to abort
        foreach (var participantId in tx.Participants)
        {
            if (_participants.TryGetValue(participantId, out var participant))
            {
                await participant.AbortAsync(transactionId, ct);
            }
        }

        tx.State = TransactionState.Aborted;
        tx.CompletedAt = DateTime.UtcNow;
        await _wal.AbortAsync(transactionId, ct);

        TransactionAborted?.Invoke(this, new TransactionEventArgs { Transaction = tx });
    }

    /// <summary>
    /// Creates a savepoint for partial rollback.
    /// </summary>
    public Savepoint CreateSavepoint(string transactionId, string name)
    {
        if (!_transactions.TryGetValue(transactionId, out var tx))
            throw new InvalidOperationException("Transaction not found");

        var savepoint = new Savepoint
        {
            Name = name,
            TransactionId = transactionId,
            CreatedAt = DateTime.UtcNow,
            Lsn = 0 // Would be set from WAL
        };

        tx.Savepoints[name] = savepoint;
        return savepoint;
    }

    /// <summary>
    /// Rolls back to a savepoint.
    /// </summary>
    public async Task RollbackToSavepointAsync(string transactionId, string savepointName, CancellationToken ct = default)
    {
        if (!_transactions.TryGetValue(transactionId, out var tx))
            throw new InvalidOperationException("Transaction not found");

        if (!tx.Savepoints.TryGetValue(savepointName, out var savepoint))
            throw new InvalidOperationException("Savepoint not found");

        await _wal.AppendAsync(new WalEntry
        {
            TransactionId = transactionId,
            Type = WalEntryType.RollbackToSavepoint,
            Data = System.Text.Encoding.UTF8.GetBytes(savepointName)
        }, ct);

        // Remove savepoints created after this one
        var toRemove = tx.Savepoints.Where(kvp => kvp.Value.CreatedAt > savepoint.CreatedAt).Select(kvp => kvp.Key).ToList();
        foreach (var name in toRemove)
            tx.Savepoints.Remove(name);
    }

    /// <summary>
    /// Gets transaction by ID.
    /// </summary>
    public DistributedTransaction? GetTransaction(string transactionId) =>
        _transactions.TryGetValue(transactionId, out var tx) ? tx : null;

    private void CheckTimeouts(object? state)
    {
        if (_disposed) return;

        foreach (var tx in _transactions.Values.Where(t => t.State == TransactionState.Active || t.State == TransactionState.Prepared))
        {
            if (DateTime.UtcNow - tx.StartedAt > tx.Timeout)
            {
                _ = AbortAsync(tx.TransactionId);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _timeoutTimer.Dispose();
        return ValueTask.CompletedTask;
    }
}

#endregion

#region Transaction Participant

/// <summary>
/// Represents a participant in a distributed transaction.
/// </summary>
public sealed class TransactionParticipant
{
    public string ParticipantId { get; init; } = string.Empty;
    public string NodeId { get; init; } = string.Empty;
    private readonly ConcurrentDictionary<string, ParticipantState> _states = new();

    public Func<string, CancellationToken, Task<bool>>? OnPrepare { get; set; }
    public Func<string, CancellationToken, Task>? OnCommit { get; set; }
    public Func<string, CancellationToken, Task>? OnAbort { get; set; }

    public async Task<bool> PrepareAsync(string transactionId, CancellationToken ct)
    {
        _states[transactionId] = ParticipantState.Preparing;

        var result = OnPrepare != null && await OnPrepare(transactionId, ct);

        _states[transactionId] = result ? ParticipantState.Prepared : ParticipantState.Aborted;
        return result;
    }

    public async Task CommitAsync(string transactionId, CancellationToken ct)
    {
        _states[transactionId] = ParticipantState.Committing;

        if (OnCommit != null)
            await OnCommit(transactionId, ct);

        _states[transactionId] = ParticipantState.Committed;
    }

    public async Task AbortAsync(string transactionId, CancellationToken ct)
    {
        _states[transactionId] = ParticipantState.Aborting;

        if (OnAbort != null)
            await OnAbort(transactionId, ct);

        _states[transactionId] = ParticipantState.Aborted;
    }

    public ParticipantState GetState(string transactionId) =>
        _states.TryGetValue(transactionId, out var state) ? state : ParticipantState.Unknown;
}

#endregion

#region Types

public sealed class WalEntry
{
    public long Lsn { get; set; }
    public string TransactionId { get; init; } = string.Empty;
    public WalEntryType Type { get; init; }
    public byte[]? Data { get; init; }
    public DateTime Timestamp { get; set; }
}

public enum WalEntryType { Data, Prepare, Commit, Abort, RollbackToSavepoint, Checkpoint }

public sealed class DistributedTransaction
{
    public string TransactionId { get; init; } = string.Empty;
    public IsolationLevel IsolationLevel { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; set; }
    public TimeSpan Timeout { get; init; }
    public TransactionState State { get; set; }
    public List<string> Participants { get; } = new();
    public Dictionary<string, Savepoint> Savepoints { get; } = new();
}

public enum IsolationLevel { ReadUncommitted, ReadCommitted, RepeatableRead, Serializable, Snapshot }
public enum TransactionState { Active, Preparing, Prepared, Committing, Committed, Aborting, Aborted }
public enum ParticipantState { Unknown, Preparing, Prepared, Committing, Committed, Aborting, Aborted }

public sealed class Savepoint
{
    public string Name { get; init; } = string.Empty;
    public string TransactionId { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public long Lsn { get; init; }
}

public sealed class TransactionConfig
{
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxRetries { get; set; } = 3;
    public bool AutoCommit { get; set; } = false;
}

public sealed class TransactionEventArgs : EventArgs
{
    public DistributedTransaction Transaction { get; init; } = null!;
}

#endregion
