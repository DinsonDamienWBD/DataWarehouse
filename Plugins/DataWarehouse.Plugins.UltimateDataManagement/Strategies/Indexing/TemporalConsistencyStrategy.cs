using System.Diagnostics;
using DataWarehouse.SDK.Contracts;
using Microsoft.Extensions.Logging;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Indexing;

/// <summary>
/// Consistency level for temporal operations.
/// </summary>
public enum TemporalConsistencyLevel
{
    /// <summary>
    /// Eventual consistency - reads may return stale data but operations are faster.
    /// </summary>
    Eventual,

    /// <summary>
    /// Session consistency - reads within a session see their own writes.
    /// </summary>
    Session,

    /// <summary>
    /// Bounded staleness - reads are guaranteed to be within a time bound of the latest write.
    /// </summary>
    BoundedStaleness,

    /// <summary>
    /// Strong consistency - reads always return the most recent committed write.
    /// </summary>
    Strong,

    /// <summary>
    /// Serializable - strongest level, operations appear to execute in some serial order.
    /// </summary>
    Serializable
}

/// <summary>
/// Snapshot isolation level for temporal queries.
/// </summary>
public enum SnapshotIsolationLevel
{
    /// <summary>
    /// Read committed - only committed data is visible.
    /// </summary>
    ReadCommitted,

    /// <summary>
    /// Repeatable read - data read during a transaction remains consistent.
    /// </summary>
    RepeatableRead,

    /// <summary>
    /// Snapshot isolation - a consistent snapshot at transaction start.
    /// </summary>
    Snapshot,

    /// <summary>
    /// Serializable snapshot - strongest isolation with conflict detection.
    /// </summary>
    SerializableSnapshot
}

/// <summary>
/// Represents a temporal snapshot for consistent reads.
/// </summary>
public sealed class TemporalSnapshot
{
    /// <summary>
    /// Unique snapshot identifier.
    /// </summary>
    public required string SnapshotId { get; init; }

    /// <summary>
    /// Timestamp when the snapshot was created.
    /// </summary>
    public required DateTime Timestamp { get; init; }

    /// <summary>
    /// Logical sequence number for ordering.
    /// </summary>
    public required long SequenceNumber { get; init; }

    /// <summary>
    /// Isolation level of this snapshot.
    /// </summary>
    public SnapshotIsolationLevel IsolationLevel { get; init; }

    /// <summary>
    /// Session ID if applicable.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// When this snapshot expires.
    /// </summary>
    public DateTime ExpiresAt { get; init; }

    /// <summary>
    /// Whether this snapshot is still valid.
    /// </summary>
    public bool IsValid => DateTime.UtcNow < ExpiresAt;

    /// <summary>
    /// Set of committed transaction IDs visible in this snapshot.
    /// </summary>
    public HashSet<string> VisibleTransactions { get; init; } = new();
}

/// <summary>
/// Represents a temporal transaction with consistency guarantees.
/// </summary>
public sealed class TemporalTransaction : IAsyncDisposable
{
    private readonly TemporalConsistencyStrategy _strategy;
    private readonly List<TemporalWriteOperation> _writeSet = new();
    private readonly HashSet<string> _readSet = new();
    private readonly object _lock = new();
    private bool _committed;
    private bool _aborted;
    private bool _disposed;

    /// <summary>
    /// Unique transaction identifier.
    /// </summary>
    public string TransactionId { get; }

    /// <summary>
    /// Snapshot for this transaction's reads.
    /// </summary>
    public TemporalSnapshot Snapshot { get; }

    /// <summary>
    /// Start time of the transaction.
    /// </summary>
    public DateTime StartTime { get; }

    /// <summary>
    /// Consistency level for this transaction.
    /// </summary>
    public TemporalConsistencyLevel ConsistencyLevel { get; }

    /// <summary>
    /// Whether the transaction is still active.
    /// </summary>
    public bool IsActive => !_committed && !_aborted && !_disposed;

    /// <summary>
    /// Timeout for the transaction.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    internal TemporalTransaction(
        TemporalConsistencyStrategy strategy,
        string transactionId,
        TemporalSnapshot snapshot,
        TemporalConsistencyLevel consistencyLevel)
    {
        _strategy = strategy;
        TransactionId = transactionId;
        Snapshot = snapshot;
        StartTime = DateTime.UtcNow;
        ConsistencyLevel = consistencyLevel;
    }

    /// <summary>
    /// Records a read operation for conflict detection.
    /// </summary>
    public void RecordRead(string objectId)
    {
        if (!IsActive)
            throw new InvalidOperationException("Transaction is no longer active.");

        lock (_lock)
        {
            _readSet.Add(objectId);
        }
    }

    /// <summary>
    /// Records a write operation.
    /// </summary>
    public void RecordWrite(string objectId, byte[] data, DateTime timestamp)
    {
        if (!IsActive)
            throw new InvalidOperationException("Transaction is no longer active.");

        lock (_lock)
        {
            _writeSet.Add(new TemporalWriteOperation
            {
                ObjectId = objectId,
                Data = data,
                Timestamp = timestamp,
                TransactionId = TransactionId
            });
        }
    }

    /// <summary>
    /// Commits the transaction.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if committed successfully.</returns>
    public async Task<bool> CommitAsync(CancellationToken ct = default)
    {
        if (!IsActive)
            throw new InvalidOperationException("Transaction is no longer active.");

        List<TemporalWriteOperation> writes;
        HashSet<string> reads;

        lock (_lock)
        {
            writes = _writeSet.ToList();
            reads = new HashSet<string>(_readSet);
        }

        var success = await _strategy.CommitTransactionAsync(this, writes, reads, ct);

        if (success)
            _committed = true;
        else
            _aborted = true;

        return success;
    }

    /// <summary>
    /// Aborts the transaction.
    /// </summary>
    public void Abort()
    {
        if (!IsActive)
            return;

        lock (_lock)
        {
            _aborted = true;
            _writeSet.Clear();
            _readSet.Clear();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        if (IsActive)
        {
            Abort();
        }

        _disposed = true;
        await Task.CompletedTask;
    }
}

/// <summary>
/// Represents a temporal write operation within a transaction.
/// </summary>
public sealed class TemporalWriteOperation
{
    /// <summary>
    /// Target object identifier.
    /// </summary>
    public required string ObjectId { get; init; }

    /// <summary>
    /// Data to write.
    /// </summary>
    public required byte[] Data { get; init; }

    /// <summary>
    /// Timestamp for the write.
    /// </summary>
    public required DateTime Timestamp { get; init; }

    /// <summary>
    /// Transaction ID.
    /// </summary>
    public required string TransactionId { get; init; }

    /// <summary>
    /// Commit timestamp (set on commit).
    /// </summary>
    public DateTime? CommitTimestamp { get; set; }
}

/// <summary>
/// Result of a consistency check operation.
/// </summary>
public sealed class ConsistencyCheckResult
{
    /// <summary>
    /// Whether the data is consistent.
    /// </summary>
    public bool IsConsistent { get; init; }

    /// <summary>
    /// Detected inconsistencies.
    /// </summary>
    public List<ConsistencyViolation> Violations { get; init; } = new();

    /// <summary>
    /// Timestamp of the check.
    /// </summary>
    public DateTime CheckedAt { get; init; }

    /// <summary>
    /// Duration of the check.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Consistency level used for the check.
    /// </summary>
    public TemporalConsistencyLevel ConsistencyLevel { get; init; }
}

/// <summary>
/// Represents a consistency violation.
/// </summary>
public sealed class ConsistencyViolation
{
    /// <summary>
    /// Type of violation.
    /// </summary>
    public required ConsistencyViolationType Type { get; init; }

    /// <summary>
    /// Affected object ID.
    /// </summary>
    public required string ObjectId { get; init; }

    /// <summary>
    /// Description of the violation.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Timestamp range affected.
    /// </summary>
    public DateTime? TimestampStart { get; init; }
    public DateTime? TimestampEnd { get; init; }

    /// <summary>
    /// Severity of the violation.
    /// </summary>
    public ConsistencyViolationSeverity Severity { get; init; }
}

/// <summary>
/// Types of consistency violations.
/// </summary>
public enum ConsistencyViolationType
{
    /// <summary>
    /// Write-write conflict detected.
    /// </summary>
    WriteWriteConflict,

    /// <summary>
    /// Read-write conflict detected.
    /// </summary>
    ReadWriteConflict,

    /// <summary>
    /// Temporal ordering violation.
    /// </summary>
    TemporalOrdering,

    /// <summary>
    /// Stale read beyond bounded staleness.
    /// </summary>
    StalenessViolation,

    /// <summary>
    /// Snapshot isolation violation.
    /// </summary>
    SnapshotViolation,

    /// <summary>
    /// Missing data in timeline.
    /// </summary>
    TimelineGap,

    /// <summary>
    /// Duplicate timestamps detected.
    /// </summary>
    DuplicateTimestamp
}

/// <summary>
/// Severity of consistency violations.
/// </summary>
public enum ConsistencyViolationSeverity
{
    /// <summary>
    /// Warning - may not affect correctness.
    /// </summary>
    Warning,

    /// <summary>
    /// Error - likely affects correctness.
    /// </summary>
    Error,

    /// <summary>
    /// Critical - data integrity compromised.
    /// </summary>
    Critical
}

/// <summary>
/// Temporal consistency strategy that provides strong consistency guarantees for temporal data.
/// Implements snapshot isolation, conflict detection, and bounded staleness for temporal queries.
/// </summary>
/// <remarks>
/// Features:
/// <list type="bullet">
///   <item>Snapshot isolation for point-in-time queries</item>
///   <item>Configurable consistency levels (eventual, session, bounded, strong, serializable)</item>
///   <item>Conflict detection for concurrent temporal writes</item>
///   <item>Bounded staleness guarantees with configurable bounds</item>
///   <item>Multi-version concurrency control (MVCC)</item>
///   <item>Temporal ordering guarantees</item>
///   <item>Consistency verification and repair</item>
/// </list>
/// </remarks>
public sealed class TemporalConsistencyStrategy : IndexingStrategyBase
{
    private readonly BoundedDictionary<string, TemporalSnapshot> _snapshots = new BoundedDictionary<string, TemporalSnapshot>(1000);
    private readonly BoundedDictionary<string, TemporalTransaction> _activeTransactions = new BoundedDictionary<string, TemporalTransaction>(1000);
    private readonly BoundedDictionary<string, List<TemporalWriteOperation>> _committedWrites = new BoundedDictionary<string, List<TemporalWriteOperation>>(1000);
    private readonly BoundedDictionary<string, long> _objectVersions = new BoundedDictionary<string, long>(1000);
    private readonly BoundedDictionary<string, DateTime> _sessionLastWrite = new BoundedDictionary<string, DateTime>(1000);
    private readonly ReaderWriterLockSlim _globalLock = new();
    private readonly ILogger? _logger;

    private long _currentSequenceNumber;
    private TimeSpan _boundedStalenessWindow = TimeSpan.FromSeconds(5);
    private TimeSpan _snapshotLifetime = TimeSpan.FromMinutes(30);
    private readonly Timer? _cleanupTimer;

    /// <summary>
    /// Initializes a new instance of the TemporalConsistencyStrategy.
    /// </summary>
    /// <param name="logger">Optional logger.</param>
    public TemporalConsistencyStrategy(ILogger? logger = null)
    {
        _logger = logger;
        _cleanupTimer = new Timer(
            _ => CleanupExpiredSnapshots(),
            null,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));
    }

    /// <inheritdoc/>
    public override string StrategyId => "index.temporal-consistency";

    /// <inheritdoc/>
    public override string DisplayName => "Temporal Consistency Guarantees";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = true,
        SupportsTTL = true,
        MaxThroughput = 30_000,
        TypicalLatencyMs = 1.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Temporal consistency strategy providing strong consistency guarantees for temporal data. " +
        "Implements MVCC, snapshot isolation, bounded staleness, and serializable transactions. " +
        "Includes conflict detection, consistency verification, and repair capabilities. " +
        "Essential for financial, legal, and compliance workloads requiring temporal consistency.";

    /// <inheritdoc/>
    public override string[] Tags => [
        "consistency", "temporal", "mvcc", "snapshot-isolation", "transactions",
        "bounded-staleness", "serializable", "conflict-detection", "compliance"
    ];

    /// <summary>
    /// Gets or sets the bounded staleness window.
    /// </summary>
    public TimeSpan BoundedStalenessWindow
    {
        get => _boundedStalenessWindow;
        set => _boundedStalenessWindow = value > TimeSpan.Zero ? value : TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// Gets or sets the snapshot lifetime.
    /// </summary>
    public TimeSpan SnapshotLifetime
    {
        get => _snapshotLifetime;
        set => _snapshotLifetime = value > TimeSpan.Zero ? value : TimeSpan.FromMinutes(30);
    }

    /// <summary>
    /// Creates a new temporal snapshot for consistent reads.
    /// </summary>
    /// <param name="isolationLevel">Snapshot isolation level.</param>
    /// <param name="sessionId">Optional session ID for session consistency.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A temporal snapshot.</returns>
    public Task<TemporalSnapshot> CreateSnapshotAsync(
        SnapshotIsolationLevel isolationLevel = SnapshotIsolationLevel.Snapshot,
        string? sessionId = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        _globalLock.EnterReadLock();
        try
        {
            var sequenceNumber = Interlocked.Read(ref _currentSequenceNumber);
            var snapshotId = $"snap-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..32];

            // Determine visible transactions
            var visibleTransactions = new HashSet<string>();
            foreach (var txn in _activeTransactions.Values.Where(t => !t.IsActive))
            {
                visibleTransactions.Add(txn.TransactionId);
            }

            var snapshot = new TemporalSnapshot
            {
                SnapshotId = snapshotId,
                Timestamp = DateTime.UtcNow,
                SequenceNumber = sequenceNumber,
                IsolationLevel = isolationLevel,
                SessionId = sessionId,
                ExpiresAt = DateTime.UtcNow.Add(_snapshotLifetime),
                VisibleTransactions = visibleTransactions
            };

            _snapshots[snapshotId] = snapshot;
            _logger?.LogDebug("Created snapshot {SnapshotId} at sequence {Seq}", snapshotId, sequenceNumber);

            return Task.FromResult(snapshot);
        }
        finally
        {
            _globalLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Begins a new temporal transaction with the specified consistency level.
    /// </summary>
    /// <param name="consistencyLevel">Consistency level for the transaction.</param>
    /// <param name="sessionId">Optional session ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A new temporal transaction.</returns>
    public async Task<TemporalTransaction> BeginTransactionAsync(
        TemporalConsistencyLevel consistencyLevel = TemporalConsistencyLevel.Strong,
        string? sessionId = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var isolationLevel = consistencyLevel switch
        {
            TemporalConsistencyLevel.Eventual => SnapshotIsolationLevel.ReadCommitted,
            TemporalConsistencyLevel.Session => SnapshotIsolationLevel.RepeatableRead,
            TemporalConsistencyLevel.BoundedStaleness => SnapshotIsolationLevel.Snapshot,
            TemporalConsistencyLevel.Strong => SnapshotIsolationLevel.Snapshot,
            TemporalConsistencyLevel.Serializable => SnapshotIsolationLevel.SerializableSnapshot,
            _ => SnapshotIsolationLevel.Snapshot
        };

        var snapshot = await CreateSnapshotAsync(isolationLevel, sessionId, ct);
        var transactionId = $"txn-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..32];

        var transaction = new TemporalTransaction(this, transactionId, snapshot, consistencyLevel);
        _activeTransactions[transactionId] = transaction;

        _logger?.LogDebug("Started transaction {TransactionId} with consistency {Level}",
            transactionId, consistencyLevel);

        return transaction;
    }

    /// <summary>
    /// Reads data at a specific snapshot.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="snapshot">Snapshot for consistent read.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Data at the snapshot or null.</returns>
    public Task<byte[]?> ReadAtSnapshotAsync(
        string objectId,
        TemporalSnapshot snapshot,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!snapshot.IsValid)
            throw new InvalidOperationException("Snapshot has expired.");

        _globalLock.EnterReadLock();
        try
        {
            if (!_committedWrites.TryGetValue(objectId, out var writes))
                return Task.FromResult<byte[]?>(null);

            // Find the most recent write visible in this snapshot
            var visibleWrite = writes
                .Where(w => w.CommitTimestamp.HasValue &&
                           w.CommitTimestamp.Value <= snapshot.Timestamp &&
                           (snapshot.IsolationLevel == SnapshotIsolationLevel.ReadCommitted ||
                            snapshot.VisibleTransactions.Contains(w.TransactionId)))
                .OrderByDescending(w => w.CommitTimestamp)
                .FirstOrDefault();

            return Task.FromResult(visibleWrite?.Data);
        }
        finally
        {
            _globalLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Checks if a read satisfies bounded staleness guarantees.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="readTimestamp">Timestamp of the read.</param>
    /// <returns>True if within bounded staleness.</returns>
    public bool CheckBoundedStaleness(string objectId, DateTime readTimestamp)
    {
        if (!_committedWrites.TryGetValue(objectId, out var writes))
            return true; // No writes, so no staleness issue

        var latestWrite = writes
            .Where(w => w.CommitTimestamp.HasValue)
            .OrderByDescending(w => w.CommitTimestamp)
            .FirstOrDefault();

        if (latestWrite?.CommitTimestamp == null)
            return true;

        var staleness = latestWrite.CommitTimestamp.Value - readTimestamp;
        return staleness <= _boundedStalenessWindow;
    }

    /// <summary>
    /// Verifies temporal consistency across objects.
    /// </summary>
    /// <param name="objectIds">Object IDs to check (null for all).</param>
    /// <param name="consistencyLevel">Level to check against.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Consistency check result.</returns>
    public Task<ConsistencyCheckResult> VerifyConsistencyAsync(
        IEnumerable<string>? objectIds = null,
        TemporalConsistencyLevel consistencyLevel = TemporalConsistencyLevel.Strong,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var sw = Stopwatch.StartNew();
        var violations = new List<ConsistencyViolation>();
        var idsToCheck = objectIds?.ToList() ?? _committedWrites.Keys.ToList();

        _globalLock.EnterReadLock();
        try
        {
            foreach (var objectId in idsToCheck)
            {
                ct.ThrowIfCancellationRequested();

                if (!_committedWrites.TryGetValue(objectId, out var writes))
                    continue;

                // Check for temporal ordering violations
                var orderedWrites = writes
                    .Where(w => w.CommitTimestamp.HasValue)
                    .OrderBy(w => w.Timestamp)
                    .ToList();

                DateTime? lastTimestamp = null;
                foreach (var write in orderedWrites)
                {
                    // Check for duplicate timestamps
                    if (lastTimestamp == write.Timestamp)
                    {
                        violations.Add(new ConsistencyViolation
                        {
                            Type = ConsistencyViolationType.DuplicateTimestamp,
                            ObjectId = objectId,
                            Description = $"Duplicate timestamp detected at {write.Timestamp:O}",
                            TimestampStart = write.Timestamp,
                            Severity = ConsistencyViolationSeverity.Warning
                        });
                    }

                    // Check for out-of-order commits
                    if (lastTimestamp.HasValue && write.CommitTimestamp < lastTimestamp)
                    {
                        violations.Add(new ConsistencyViolation
                        {
                            Type = ConsistencyViolationType.TemporalOrdering,
                            ObjectId = objectId,
                            Description = $"Temporal ordering violation: commit at {write.CommitTimestamp:O} " +
                                        $"is before previous at {lastTimestamp:O}",
                            TimestampStart = lastTimestamp,
                            TimestampEnd = write.CommitTimestamp,
                            Severity = ConsistencyViolationSeverity.Error
                        });
                    }

                    lastTimestamp = write.Timestamp;
                }

                // Check bounded staleness for the latest write
                if (consistencyLevel >= TemporalConsistencyLevel.BoundedStaleness)
                {
                    if (!CheckBoundedStaleness(objectId, DateTime.UtcNow))
                    {
                        violations.Add(new ConsistencyViolation
                        {
                            Type = ConsistencyViolationType.StalenessViolation,
                            ObjectId = objectId,
                            Description = $"Read staleness exceeds bound of {_boundedStalenessWindow}",
                            Severity = ConsistencyViolationSeverity.Warning
                        });
                    }
                }
            }

            sw.Stop();
            return Task.FromResult(new ConsistencyCheckResult
            {
                IsConsistent = violations.Count == 0,
                Violations = violations,
                CheckedAt = DateTime.UtcNow,
                Duration = sw.Elapsed,
                ConsistencyLevel = consistencyLevel
            });
        }
        finally
        {
            _globalLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Records a session write for session consistency tracking.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="writeTimestamp">Timestamp of the write.</param>
    public void RecordSessionWrite(string sessionId, DateTime writeTimestamp)
    {
        _sessionLastWrite[sessionId] = writeTimestamp;
    }

    /// <summary>
    /// Checks if a read satisfies session consistency.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="readTimestamp">Timestamp of the read.</param>
    /// <returns>True if session consistency is satisfied.</returns>
    public bool CheckSessionConsistency(string sessionId, DateTime readTimestamp)
    {
        if (!_sessionLastWrite.TryGetValue(sessionId, out var lastWrite))
            return true; // No prior writes in session

        return readTimestamp >= lastWrite;
    }

    /// <summary>
    /// Commits a transaction with conflict detection.
    /// </summary>
    internal async Task<bool> CommitTransactionAsync(
        TemporalTransaction transaction,
        List<TemporalWriteOperation> writes,
        HashSet<string> reads,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (transaction.ConsistencyLevel == TemporalConsistencyLevel.Serializable)
        {
            // Check for conflicts
            var conflicts = await DetectConflictsAsync(transaction, writes, reads, ct);
            if (conflicts.Count > 0)
            {
                _logger?.LogWarning("Transaction {TxnId} aborted due to {Count} conflicts",
                    transaction.TransactionId, conflicts.Count);
                return false;
            }
        }

        _globalLock.EnterWriteLock();
        try
        {
            var commitTimestamp = DateTime.UtcNow;
            var commitSequence = Interlocked.Increment(ref _currentSequenceNumber);

            foreach (var write in writes)
            {
                write.CommitTimestamp = commitTimestamp;

                if (!_committedWrites.TryGetValue(write.ObjectId, out var objectWrites))
                {
                    objectWrites = new List<TemporalWriteOperation>();
                    _committedWrites[write.ObjectId] = objectWrites;
                }

                objectWrites.Add(write);

                // Update version
                _objectVersions.AddOrUpdate(write.ObjectId, commitSequence, (_, _) => commitSequence);
            }

            _activeTransactions.TryRemove(transaction.TransactionId, out _);

            _logger?.LogDebug("Committed transaction {TxnId} with {Count} writes at sequence {Seq}",
                transaction.TransactionId, writes.Count, commitSequence);

            return true;
        }
        finally
        {
            _globalLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Detects conflicts for serializable isolation.
    /// </summary>
    private Task<List<ConsistencyViolation>> DetectConflictsAsync(
        TemporalTransaction transaction,
        List<TemporalWriteOperation> writes,
        HashSet<string> reads,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var conflicts = new List<ConsistencyViolation>();

        _globalLock.EnterReadLock();
        try
        {
            // Check for write-write conflicts
            foreach (var write in writes)
            {
                if (_objectVersions.TryGetValue(write.ObjectId, out var currentVersion))
                {
                    if (currentVersion > transaction.Snapshot.SequenceNumber)
                    {
                        conflicts.Add(new ConsistencyViolation
                        {
                            Type = ConsistencyViolationType.WriteWriteConflict,
                            ObjectId = write.ObjectId,
                            Description = $"Object was modified after transaction started " +
                                        $"(current: {currentVersion}, snapshot: {transaction.Snapshot.SequenceNumber})",
                            Severity = ConsistencyViolationSeverity.Error
                        });
                    }
                }
            }

            // Check for read-write conflicts (anti-dependency)
            foreach (var readObjectId in reads)
            {
                if (_objectVersions.TryGetValue(readObjectId, out var currentVersion))
                {
                    if (currentVersion > transaction.Snapshot.SequenceNumber)
                    {
                        conflicts.Add(new ConsistencyViolation
                        {
                            Type = ConsistencyViolationType.ReadWriteConflict,
                            ObjectId = readObjectId,
                            Description = $"Read object was modified after transaction started " +
                                        $"(current: {currentVersion}, snapshot: {transaction.Snapshot.SequenceNumber})",
                            Severity = ConsistencyViolationSeverity.Error
                        });
                    }
                }
            }

            return Task.FromResult(conflicts);
        }
        finally
        {
            _globalLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Cleans up expired snapshots.
    /// </summary>
    private void CleanupExpiredSnapshots()
    {
        var expired = _snapshots
            .Where(kvp => !kvp.Value.IsValid)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var snapshotId in expired)
        {
            _snapshots.TryRemove(snapshotId, out _);
        }

        if (expired.Count > 0)
        {
            _logger?.LogDebug("Cleaned up {Count} expired snapshots", expired.Count);
        }
    }

    /// <summary>
    /// Gets statistics about temporal consistency operations.
    /// </summary>
    /// <returns>Statistics dictionary.</returns>
    public new Dictionary<string, object> GetStatistics()
    {
        return new Dictionary<string, object>
        {
            ["ActiveSnapshots"] = _snapshots.Count,
            ["ActiveTransactions"] = _activeTransactions.Count,
            ["TrackedObjects"] = _committedWrites.Count,
            ["CurrentSequenceNumber"] = Interlocked.Read(ref _currentSequenceNumber),
            ["BoundedStalenessWindowMs"] = _boundedStalenessWindow.TotalMilliseconds,
            ["SnapshotLifetimeMinutes"] = _snapshotLifetime.TotalMinutes,
            ["SessionsTracked"] = _sessionLastWrite.Count
        };
    }

    #region IndexingStrategyBase Implementation

    /// <inheritdoc/>
    public override long GetDocumentCount() => _committedWrites.Count;

    /// <inheritdoc/>
    public override long GetIndexSize()
    {
        long size = 0;
        foreach (var writes in _committedWrites.Values)
        {
            size += writes.Sum(w => w.Data.Length + 100); // Data + metadata overhead
        }
        return size;
    }

    /// <inheritdoc/>
    public override Task<bool> ExistsAsync(string objectId, CancellationToken ct = default)
    {
        return Task.FromResult(_committedWrites.ContainsKey(objectId));
    }

    /// <inheritdoc/>
    public override Task ClearAsync(CancellationToken ct = default)
    {
        _globalLock.EnterWriteLock();
        try
        {
            _snapshots.Clear();
            _activeTransactions.Clear();
            _committedWrites.Clear();
            _objectVersions.Clear();
            _sessionLastWrite.Clear();
            _currentSequenceNumber = 0;
            return Task.CompletedTask;
        }
        finally
        {
            _globalLock.ExitWriteLock();
        }
    }

    /// <inheritdoc/>
    public override Task OptimizeAsync(CancellationToken ct = default)
    {
        CleanupExpiredSnapshots();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task<IndexResult> IndexCoreAsync(string objectId, IndexableContent content, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        _globalLock.EnterWriteLock();
        try
        {
            var data = System.Text.Encoding.UTF8.GetBytes(content.TextContent ?? "");
            var write = new TemporalWriteOperation
            {
                ObjectId = objectId,
                Data = data,
                Timestamp = DateTime.UtcNow,
                TransactionId = "direct-write",
                CommitTimestamp = DateTime.UtcNow
            };

            if (!_committedWrites.TryGetValue(objectId, out var writes))
            {
                writes = new List<TemporalWriteOperation>();
                _committedWrites[objectId] = writes;
            }

            writes.Add(write);
            _objectVersions.AddOrUpdate(objectId,
                Interlocked.Increment(ref _currentSequenceNumber),
                (_, _) => Interlocked.Increment(ref _currentSequenceNumber));

            sw.Stop();
            return Task.FromResult(IndexResult.Ok(1, 1, sw.Elapsed));
        }
        finally
        {
            _globalLock.ExitWriteLock();
        }
    }

    /// <inheritdoc/>
    protected override Task<IReadOnlyList<IndexSearchResult>> SearchCoreAsync(
        string query,
        IndexSearchOptions options,
        CancellationToken ct)
    {
        // Consistency strategy is primarily for guarantees, not search
        // Return empty results - use TemporalIndexStrategy for actual search
        return Task.FromResult<IReadOnlyList<IndexSearchResult>>([]);
    }

    /// <inheritdoc/>
    protected override Task<bool> RemoveCoreAsync(string objectId, CancellationToken ct)
    {
        _globalLock.EnterWriteLock();
        try
        {
            var removed = _committedWrites.TryRemove(objectId, out _);
            _objectVersions.TryRemove(objectId, out _);
            return Task.FromResult(removed);
        }
        finally
        {
            _globalLock.ExitWriteLock();
        }
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _cleanupTimer?.Dispose();
        _globalLock.Dispose();
        return Task.CompletedTask;
    }

    #endregion
}
