using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.CrashRecovery;

/// <summary>
/// Production-ready crash recovery plugin implementing ARIES-style recovery with
/// Write-Ahead Logging (WAL), fuzzy checkpoints, point-in-time recovery,
/// and automatic transaction rollback for incomplete operations.
/// Designed for enterprise-grade data durability and crash-consistent recovery.
/// </summary>
public sealed class CrashRecoveryPlugin : SnapshotPluginBase, IAsyncDisposable
{
    #region Fields

    private readonly ConcurrentDictionary<string, SnapshotMetadata> _snapshots = new();
    private readonly ConcurrentDictionary<long, WalEntry> _walEntries = new();
    private readonly ConcurrentDictionary<string, TransactionState> _activeTransactions = new();
    private readonly ConcurrentDictionary<string, Checkpoint> _checkpoints = new();
    private readonly ConcurrentQueue<RecoveryOperation> _recoveryOperations = new();
    private readonly CrashRecoveryConfig _config;
    private readonly string _storagePath;
    private readonly string _walPath;
    private readonly string _checkpointPath;
    private readonly SemaphoreSlim _walLock = new(1, 1);
    private readonly SemaphoreSlim _checkpointLock = new(1, 1);
    private readonly SemaphoreSlim _recoveryLock = new(1, 1);
    private readonly Timer _checkpointTimer;
    private readonly Timer _walFlushTimer;
    private readonly WalManager _walManager;
    private readonly DirtyPageTable _dirtyPageTable;
    private readonly TransactionTable _transactionTable;
    private readonly DoubleWriteBuffer _doubleWriteBuffer;
    private readonly PageChecksumManager _checksumManager;
    private CancellationTokenSource? _cts;
    private volatile bool _disposed;
    private volatile bool _isRecovering;
    private long _currentLsn;
    private long _lastCheckpointLsn;
    private RecoveryProgress? _currentRecoveryProgress;

    #endregion

    #region Properties

    /// <inheritdoc />
    public override string Id => "com.datawarehouse.recovery.crash";

    /// <inheritdoc />
    public override string Name => "Crash Recovery Plugin";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override bool SupportsIncremental => true;

    /// <inheritdoc />
    public override bool SupportsLegalHold => true;

    /// <summary>
    /// Indicates whether the plugin is currently performing recovery.
    /// </summary>
    public bool IsRecovering => _isRecovering;

    /// <summary>
    /// Current Log Sequence Number (LSN).
    /// </summary>
    public long CurrentLsn => Interlocked.Read(ref _currentLsn);

    /// <summary>
    /// Last checkpoint LSN.
    /// </summary>
    public long LastCheckpointLsn => Interlocked.Read(ref _lastCheckpointLsn);

    /// <summary>
    /// Number of active transactions.
    /// </summary>
    public int ActiveTransactionCount => _activeTransactions.Count;

    /// <summary>
    /// Current recovery progress, if recovery is in progress.
    /// </summary>
    public RecoveryProgress? CurrentRecoveryProgress => _currentRecoveryProgress;

    /// <summary>
    /// Event raised when recovery starts.
    /// </summary>
    public event EventHandler<RecoveryStartedEventArgs>? RecoveryStarted;

    /// <summary>
    /// Event raised when recovery completes.
    /// </summary>
    public event EventHandler<RecoveryCompletedEventArgs>? RecoveryCompleted;

    /// <summary>
    /// Event raised when recovery progress updates.
    /// </summary>
    public event EventHandler<RecoveryProgress>? RecoveryProgressChanged;

    /// <summary>
    /// Event raised when a checkpoint is created.
    /// </summary>
    public event EventHandler<Checkpoint>? CheckpointCreated;

    /// <summary>
    /// Event raised when a transaction is rolled back.
    /// </summary>
    public event EventHandler<TransactionRollbackEventArgs>? TransactionRolledBack;

    /// <summary>
    /// Event raised when WAL is flushed.
    /// </summary>
    public event EventHandler<WalFlushEventArgs>? WalFlushed;

    #endregion

    #region Constructor

    /// <summary>
    /// Creates a new crash recovery plugin instance.
    /// </summary>
    /// <param name="config">Configuration options for the plugin.</param>
    public CrashRecoveryPlugin(CrashRecoveryConfig? config = null)
    {
        _config = config ?? new CrashRecoveryConfig();
        _storagePath = _config.StoragePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DataWarehouse", "CrashRecovery");

        _walPath = Path.Combine(_storagePath, "wal");
        _checkpointPath = Path.Combine(_storagePath, "checkpoints");

        Directory.CreateDirectory(_storagePath);
        Directory.CreateDirectory(_walPath);
        Directory.CreateDirectory(_checkpointPath);
        Directory.CreateDirectory(Path.Combine(_storagePath, "metadata"));
        Directory.CreateDirectory(Path.Combine(_storagePath, "snapshots"));

        _walManager = new WalManager(_walPath, _config.WalConfig ?? new WalConfig());

        // Initialize dirty page table for tracking modified pages
        _dirtyPageTable = new DirtyPageTable();

        // Initialize transaction table for tracking active transactions
        _transactionTable = new TransactionTable();

        // Initialize double-write buffer for atomic page writes
        var doubleWriteBufferPath = Path.Combine(_storagePath, "double_write_buffer");
        Directory.CreateDirectory(doubleWriteBufferPath);
        _doubleWriteBuffer = new DoubleWriteBuffer(doubleWriteBufferPath, _config.DoubleWriteBufferConfig ?? new DoubleWriteBufferConfig());

        // Initialize page checksum manager for data integrity
        _checksumManager = new PageChecksumManager(_config.ChecksumConfig ?? new PageChecksumConfig());

        // Initialize checkpoint timer
        _checkpointTimer = new Timer(
            async _ => await CreatePeriodicCheckpointAsync(),
            null,
            _config.CheckpointInterval,
            _config.CheckpointInterval);

        // Initialize WAL flush timer
        _walFlushTimer = new Timer(
            async _ => await FlushWalAsync(),
            null,
            _config.WalFlushInterval,
            _config.WalFlushInterval);
    }

    #endregion

    #region Lifecycle

    /// <inheritdoc />
    public override async Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Perform automatic recovery on startup
        await PerformAutomaticRecoveryAsync(ct);

        // Load persisted state
        await LoadStateAsync(ct);
    }

    /// <inheritdoc />
    public override async Task StopAsync()
    {
        _cts?.Cancel();

        // Create final checkpoint before shutdown
        await CreateCheckpointAsync(CheckpointType.Shutdown, CancellationToken.None);

        // Flush WAL
        await _walManager.FlushAsync(CancellationToken.None);

        // Stop timers
        await _checkpointTimer.DisposeAsync();
        await _walFlushTimer.DisposeAsync();

        // Persist final state
        await PersistStateAsync(CancellationToken.None);
    }

    #endregion

    #region Automatic Recovery

    /// <summary>
    /// Performs automatic crash recovery on startup using ARIES algorithm.
    /// Phase 1: Analysis - Determine dirty pages and active transactions
    /// Phase 2: Redo - Replay WAL from last checkpoint
    /// Phase 3: Undo - Rollback incomplete transactions
    /// </summary>
    private async Task PerformAutomaticRecoveryAsync(CancellationToken ct)
    {
        await _recoveryLock.WaitAsync(ct);
        try
        {
            _isRecovering = true;
            var startTime = DateTime.UtcNow;

            // Check if recovery is needed
            var needsRecovery = await CheckRecoveryNeededAsync(ct);
            if (!needsRecovery)
            {
                _isRecovering = false;
                return;
            }

            var progress = new RecoveryProgress
            {
                Phase = RecoveryPhase.Analysis,
                StartedAt = startTime,
                Status = RecoveryStatus.InProgress
            };
            _currentRecoveryProgress = progress;

            RecoveryStarted?.Invoke(this, new RecoveryStartedEventArgs
            {
                StartTime = startTime,
                LastCheckpointLsn = _lastCheckpointLsn,
                RequiredWalEntries = await _walManager.GetEntryCountSinceLsnAsync(_lastCheckpointLsn, ct)
            });

            try
            {
                // Phase 1: Analysis
                progress.Phase = RecoveryPhase.Analysis;
                progress.PhaseProgress = 0;
                ReportProgress(progress);

                var analysisResult = await PerformAnalysisPhaseAsync(ct);
                progress.PhaseProgress = 100;
                progress.AnalyzedEntries = analysisResult.EntriesAnalyzed;
                ReportProgress(progress);

                // Phase 2: Redo
                progress.Phase = RecoveryPhase.Redo;
                progress.PhaseProgress = 0;
                ReportProgress(progress);

                var redoResult = await PerformRedoPhaseAsync(analysisResult, ct);
                progress.PhaseProgress = 100;
                progress.RedoneOperations = redoResult.OperationsRedone;
                ReportProgress(progress);

                // Phase 3: Undo
                progress.Phase = RecoveryPhase.Undo;
                progress.PhaseProgress = 0;
                ReportProgress(progress);

                var undoResult = await PerformUndoPhaseAsync(analysisResult, ct);
                progress.PhaseProgress = 100;
                progress.UndoneTransactions = undoResult.TransactionsUndone;
                ReportProgress(progress);

                // Create recovery checkpoint
                await CreateCheckpointAsync(CheckpointType.Recovery, ct);

                progress.Status = RecoveryStatus.Completed;
                progress.CompletedAt = DateTime.UtcNow;
                progress.TotalDuration = progress.CompletedAt.Value - startTime;

                RecoveryCompleted?.Invoke(this, new RecoveryCompletedEventArgs
                {
                    Success = true,
                    Progress = progress,
                    Duration = progress.TotalDuration.Value,
                    RedoneOperations = redoResult.OperationsRedone,
                    UndoneTransactions = undoResult.TransactionsUndone
                });
            }
            catch (Exception ex)
            {
                progress.Status = RecoveryStatus.Failed;
                progress.ErrorMessage = ex.Message;

                RecoveryCompleted?.Invoke(this, new RecoveryCompletedEventArgs
                {
                    Success = false,
                    Progress = progress,
                    ErrorMessage = ex.Message
                });

                throw new RecoveryException("Crash recovery failed", ex);
            }
            finally
            {
                _isRecovering = false;
            }
        }
        finally
        {
            _recoveryLock.Release();
        }
    }

    /// <summary>
    /// Checks if crash recovery is needed by examining WAL and checkpoint state.
    /// </summary>
    private async Task<bool> CheckRecoveryNeededAsync(CancellationToken ct)
    {
        // Load last checkpoint
        var lastCheckpoint = await LoadLastCheckpointAsync(ct);
        if (lastCheckpoint == null)
        {
            // No checkpoint - check if WAL has entries
            var hasWalEntries = await _walManager.HasEntriesAsync(ct);
            return hasWalEntries;
        }

        _lastCheckpointLsn = lastCheckpoint.Lsn;

        // Check if there are WAL entries after the last checkpoint
        var entriesAfterCheckpoint = await _walManager.GetEntryCountSinceLsnAsync(lastCheckpoint.Lsn, ct);
        if (entriesAfterCheckpoint > 0)
        {
            return true;
        }

        // Check for unclean shutdown
        if (lastCheckpoint.Type != CheckpointType.Shutdown)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Analysis phase: Identifies dirty pages and active transactions at time of crash.
    /// </summary>
    private async Task<AnalysisResult> PerformAnalysisPhaseAsync(CancellationToken ct)
    {
        var result = new AnalysisResult();
        var dirtyPages = new Dictionary<string, long>();
        var activeTransactions = new Dictionary<string, TransactionInfo>();

        await foreach (var entry in _walManager.ReadEntriesFromLsnAsync(_lastCheckpointLsn, ct))
        {
            result.EntriesAnalyzed++;

            switch (entry.Type)
            {
                case WalEntryType.BeginTransaction:
                    activeTransactions[entry.TransactionId!] = new TransactionInfo
                    {
                        TransactionId = entry.TransactionId!,
                        StartLsn = entry.Lsn,
                        Status = TransactionStatus.Active
                    };
                    break;

                case WalEntryType.CommitTransaction:
                    if (activeTransactions.TryGetValue(entry.TransactionId!, out var committedTx))
                    {
                        committedTx.Status = TransactionStatus.Committed;
                        committedTx.EndLsn = entry.Lsn;
                    }
                    break;

                case WalEntryType.AbortTransaction:
                    if (activeTransactions.TryGetValue(entry.TransactionId!, out var abortedTx))
                    {
                        abortedTx.Status = TransactionStatus.Aborted;
                        abortedTx.EndLsn = entry.Lsn;
                    }
                    break;

                case WalEntryType.Write:
                case WalEntryType.Update:
                case WalEntryType.Delete:
                    if (!string.IsNullOrEmpty(entry.PageId))
                    {
                        dirtyPages[entry.PageId] = entry.Lsn;
                    }
                    if (!string.IsNullOrEmpty(entry.TransactionId) &&
                        activeTransactions.TryGetValue(entry.TransactionId, out var tx))
                    {
                        tx.Operations.Add(entry);
                    }
                    break;

                case WalEntryType.Checkpoint:
                    // Update recovery start point
                    result.RedoStartLsn = Math.Max(result.RedoStartLsn, entry.Lsn);
                    break;
            }

            _currentLsn = entry.Lsn;
        }

        result.DirtyPages = dirtyPages;
        result.ActiveTransactions = activeTransactions.Values
            .Where(t => t.Status == TransactionStatus.Active)
            .ToList();

        return result;
    }

    /// <summary>
    /// Redo phase: Replays all operations from WAL to restore database to crash state.
    /// </summary>
    private async Task<RedoResult> PerformRedoPhaseAsync(AnalysisResult analysis, CancellationToken ct)
    {
        var result = new RedoResult();
        var redoStartLsn = analysis.RedoStartLsn > 0 ? analysis.RedoStartLsn : _lastCheckpointLsn;

        await foreach (var entry in _walManager.ReadEntriesFromLsnAsync(redoStartLsn, ct))
        {
            // Skip if page is not dirty or LSN is too old
            if (!string.IsNullOrEmpty(entry.PageId) &&
                analysis.DirtyPages.TryGetValue(entry.PageId, out var pageLsn) &&
                entry.Lsn >= pageLsn)
            {
                continue;
            }

            try
            {
                await RedoOperationAsync(entry, ct);
                result.OperationsRedone++;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Redo failed for LSN {entry.Lsn}: {ex.Message}");
                if (_config.FailOnRedoError)
                {
                    throw;
                }
            }

            // Update progress
            if (_currentRecoveryProgress != null)
            {
                _currentRecoveryProgress.PhaseProgress =
                    (int)(result.OperationsRedone * 100.0 / Math.Max(1, analysis.EntriesAnalyzed));
                ReportProgress(_currentRecoveryProgress);
            }
        }

        return result;
    }

    /// <summary>
    /// Undo phase: Rolls back all uncommitted transactions.
    /// </summary>
    private async Task<UndoResult> PerformUndoPhaseAsync(AnalysisResult analysis, CancellationToken ct)
    {
        var result = new UndoResult();

        foreach (var transaction in analysis.ActiveTransactions)
        {
            try
            {
                await RollbackTransactionAsync(transaction.TransactionId, ct);
                result.TransactionsUndone++;

                TransactionRolledBack?.Invoke(this, new TransactionRollbackEventArgs
                {
                    TransactionId = transaction.TransactionId,
                    StartLsn = transaction.StartLsn,
                    OperationCount = transaction.Operations.Count,
                    Reason = RollbackReason.CrashRecovery
                });
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Undo failed for transaction {transaction.TransactionId}: {ex.Message}");
                if (_config.FailOnUndoError)
                {
                    throw;
                }
            }

            // Update progress
            if (_currentRecoveryProgress != null)
            {
                _currentRecoveryProgress.PhaseProgress =
                    (int)(result.TransactionsUndone * 100.0 / Math.Max(1, analysis.ActiveTransactions.Count));
                ReportProgress(_currentRecoveryProgress);
            }
        }

        return result;
    }

    /// <summary>
    /// Redoes a single WAL operation.
    /// </summary>
    private async Task RedoOperationAsync(WalEntry entry, CancellationToken ct)
    {
        switch (entry.Type)
        {
            case WalEntryType.Write:
                if (entry.AfterImage != null)
                {
                    await ApplyPageImageAsync(entry.PageId!, entry.AfterImage, ct);
                }
                break;

            case WalEntryType.Update:
                if (entry.AfterImage != null)
                {
                    await ApplyPageImageAsync(entry.PageId!, entry.AfterImage, ct);
                }
                break;

            case WalEntryType.Delete:
                if (!string.IsNullOrEmpty(entry.PageId))
                {
                    await DeletePageAsync(entry.PageId, ct);
                }
                break;
        }

        _recoveryOperations.Enqueue(new RecoveryOperation
        {
            Type = RecoveryOperationType.Redo,
            WalEntry = entry,
            ExecutedAt = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Applies a page image from WAL using double-write buffer for atomicity.
    /// Uses page-level checksums for data integrity verification.
    /// </summary>
    private async Task ApplyPageImageAsync(string pageId, byte[] image, CancellationToken ct)
    {
        var pagePath = GetPagePath(pageId);
        var pageDir = Path.GetDirectoryName(pagePath);
        if (!string.IsNullOrEmpty(pageDir))
        {
            Directory.CreateDirectory(pageDir);
        }

        // Add page checksum for integrity verification
        var pageWithChecksum = _checksumManager.AddChecksum(image);

        // Use double-write buffer for atomic page writes (prevents torn writes)
        await _doubleWriteBuffer.WritePageAtomicallyAsync(pagePath, pageWithChecksum, ct);

        // Update dirty page table
        _dirtyPageTable.MarkClean(pageId, CurrentLsn);
    }

    /// <summary>
    /// Reads and verifies a page, detecting torn writes via checksum validation.
    /// </summary>
    public async Task<PageReadResult> ReadAndVerifyPageAsync(string pageId, CancellationToken ct = default)
    {
        var pagePath = GetPagePath(pageId);
        if (!File.Exists(pagePath))
        {
            return new PageReadResult { Success = false, ErrorType = PageErrorType.NotFound };
        }

        var data = await File.ReadAllBytesAsync(pagePath, ct);

        // Verify checksum to detect torn writes or corruption
        var verifyResult = _checksumManager.VerifyAndExtract(data);
        if (!verifyResult.IsValid)
        {
            // Attempt recovery from double-write buffer
            var recovered = await _doubleWriteBuffer.RecoverPageAsync(pagePath, ct);
            if (recovered != null)
            {
                verifyResult = _checksumManager.VerifyAndExtract(recovered);
                if (verifyResult.IsValid)
                {
                    return new PageReadResult
                    {
                        Success = true,
                        Data = verifyResult.Data,
                        RecoveredFromDoubleWrite = true
                    };
                }
            }

            return new PageReadResult
            {
                Success = false,
                ErrorType = verifyResult.IsTornWrite ? PageErrorType.TornWrite : PageErrorType.Corruption,
                ExpectedChecksum = verifyResult.ExpectedChecksum,
                ActualChecksum = verifyResult.ActualChecksum
            };
        }

        return new PageReadResult { Success = true, Data = verifyResult.Data };
    }

    /// <summary>
    /// Deletes a page.
    /// </summary>
    private Task DeletePageAsync(string pageId, CancellationToken ct)
    {
        var pagePath = GetPagePath(pageId);
        if (File.Exists(pagePath))
        {
            File.Delete(pagePath);
        }
        _dirtyPageTable.Remove(pageId);
        return Task.CompletedTask;
    }

    #endregion

    #region Write-Ahead Logging

    /// <summary>
    /// Writes a WAL entry for a data operation.
    /// </summary>
    /// <param name="entry">The WAL entry to write.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The assigned LSN for the entry.</returns>
    public async Task<long> WriteWalEntryAsync(WalEntry entry, CancellationToken ct = default)
    {
        await _walLock.WaitAsync(ct);
        try
        {
            entry.Lsn = Interlocked.Increment(ref _currentLsn);
            entry.Timestamp = DateTime.UtcNow;

            await _walManager.AppendAsync(entry, ct);
            _walEntries[entry.Lsn] = entry;

            // Force sync for commit/abort entries
            if (entry.Type == WalEntryType.CommitTransaction ||
                entry.Type == WalEntryType.AbortTransaction)
            {
                await _walManager.SyncAsync(ct);
            }

            return entry.Lsn;
        }
        finally
        {
            _walLock.Release();
        }
    }

    /// <summary>
    /// Begins a new transaction and records it in the WAL.
    /// </summary>
    /// <param name="transactionId">Optional transaction ID. If not provided, one will be generated.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The transaction state.</returns>
    public async Task<TransactionState> BeginTransactionAsync(
        string? transactionId = null,
        CancellationToken ct = default)
    {
        var txId = transactionId ?? GenerateTransactionId();

        var entry = new WalEntry
        {
            Type = WalEntryType.BeginTransaction,
            TransactionId = txId
        };

        var lsn = await WriteWalEntryAsync(entry, ct);

        var state = new TransactionState
        {
            TransactionId = txId,
            Status = TransactionStatus.Active,
            StartLsn = lsn,
            StartTime = DateTime.UtcNow
        };

        _activeTransactions[txId] = state;
        return state;
    }

    /// <summary>
    /// Commits a transaction and records it in the WAL.
    /// </summary>
    /// <param name="transactionId">The transaction ID to commit.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if commit succeeded.</returns>
    public async Task<bool> CommitTransactionAsync(string transactionId, CancellationToken ct = default)
    {
        if (!_activeTransactions.TryGetValue(transactionId, out var state))
        {
            throw new InvalidOperationException($"Transaction '{transactionId}' not found");
        }

        if (state.Status != TransactionStatus.Active)
        {
            throw new InvalidOperationException($"Transaction '{transactionId}' is not active (status: {state.Status})");
        }

        var entry = new WalEntry
        {
            Type = WalEntryType.CommitTransaction,
            TransactionId = transactionId
        };

        var lsn = await WriteWalEntryAsync(entry, ct);

        state.Status = TransactionStatus.Committed;
        state.EndLsn = lsn;
        state.EndTime = DateTime.UtcNow;

        _activeTransactions.TryRemove(transactionId, out _);
        return true;
    }

    /// <summary>
    /// Aborts a transaction and performs rollback.
    /// </summary>
    /// <param name="transactionId">The transaction ID to abort.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if abort succeeded.</returns>
    public async Task<bool> AbortTransactionAsync(string transactionId, CancellationToken ct = default)
    {
        if (!_activeTransactions.TryGetValue(transactionId, out var state))
        {
            throw new InvalidOperationException($"Transaction '{transactionId}' not found");
        }

        if (state.Status != TransactionStatus.Active)
        {
            throw new InvalidOperationException($"Transaction '{transactionId}' is not active (status: {state.Status})");
        }

        // Rollback operations
        await RollbackTransactionAsync(transactionId, ct);

        var entry = new WalEntry
        {
            Type = WalEntryType.AbortTransaction,
            TransactionId = transactionId
        };

        var lsn = await WriteWalEntryAsync(entry, ct);

        state.Status = TransactionStatus.Aborted;
        state.EndLsn = lsn;
        state.EndTime = DateTime.UtcNow;

        _activeTransactions.TryRemove(transactionId, out _);

        TransactionRolledBack?.Invoke(this, new TransactionRollbackEventArgs
        {
            TransactionId = transactionId,
            StartLsn = state.StartLsn,
            OperationCount = state.Operations.Count,
            Reason = RollbackReason.UserRequested
        });

        return true;
    }

    /// <summary>
    /// Rolls back a transaction by undoing all its operations in reverse order.
    /// </summary>
    private async Task RollbackTransactionAsync(string transactionId, CancellationToken ct)
    {
        // Find all operations for this transaction
        var operations = _walEntries.Values
            .Where(e => e.TransactionId == transactionId)
            .OrderByDescending(e => e.Lsn)
            .ToList();

        foreach (var entry in operations)
        {
            await UndoOperationAsync(entry, ct);
        }
    }

    /// <summary>
    /// Undoes a single WAL operation.
    /// </summary>
    private async Task UndoOperationAsync(WalEntry entry, CancellationToken ct)
    {
        switch (entry.Type)
        {
            case WalEntryType.Write:
                // Undo write by deleting the page
                if (!string.IsNullOrEmpty(entry.PageId))
                {
                    await DeletePageAsync(entry.PageId, ct);
                }
                break;

            case WalEntryType.Update:
                // Undo update by restoring before image
                if (entry.BeforeImage != null && !string.IsNullOrEmpty(entry.PageId))
                {
                    await ApplyPageImageAsync(entry.PageId, entry.BeforeImage, ct);
                }
                break;

            case WalEntryType.Delete:
                // Undo delete by restoring before image
                if (entry.BeforeImage != null && !string.IsNullOrEmpty(entry.PageId))
                {
                    await ApplyPageImageAsync(entry.PageId, entry.BeforeImage, ct);
                }
                break;
        }

        // Write compensation log record (CLR)
        var clr = new WalEntry
        {
            Type = WalEntryType.CompensationLogRecord,
            TransactionId = entry.TransactionId,
            PageId = entry.PageId,
            UndoNextLsn = entry.PrevLsn
        };

        await WriteWalEntryAsync(clr, ct);

        _recoveryOperations.Enqueue(new RecoveryOperation
        {
            Type = RecoveryOperationType.Undo,
            WalEntry = entry,
            ExecutedAt = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Flushes the WAL to disk.
    /// </summary>
    private async Task FlushWalAsync()
    {
        if (_cts?.IsCancellationRequested ?? true) return;

        try
        {
            var flushedLsn = await _walManager.FlushAsync(CancellationToken.None);

            WalFlushed?.Invoke(this, new WalFlushEventArgs
            {
                FlushedLsn = flushedLsn,
                FlushTime = DateTime.UtcNow,
                EntriesFlushed = _walEntries.Count(e => e.Key <= flushedLsn)
            });
        }
        catch (Exception ex)
        {
            // Log but don't throw - this is a background operation
            System.Diagnostics.Debug.WriteLine($"WAL flush failed: {ex.Message}");
        }
    }

    #endregion

    #region Checkpoint Management

    /// <summary>
    /// Creates a checkpoint to enable faster recovery.
    /// </summary>
    /// <param name="type">The type of checkpoint to create.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created checkpoint.</returns>
    public async Task<Checkpoint> CreateCheckpointAsync(
        CheckpointType type = CheckpointType.Periodic,
        CancellationToken ct = default)
    {
        await _checkpointLock.WaitAsync(ct);
        try
        {
            var checkpointId = GenerateCheckpointId();
            var startTime = DateTime.UtcNow;
            var checkpointLsn = CurrentLsn;

            // Write begin checkpoint record
            var beginEntry = new WalEntry
            {
                Type = WalEntryType.CheckpointBegin,
                CheckpointId = checkpointId
            };
            await WriteWalEntryAsync(beginEntry, ct);

            // Capture active transactions
            var activeTransactionList = _activeTransactions.Values
                .Select(t => new CheckpointTransaction
                {
                    TransactionId = t.TransactionId,
                    StartLsn = t.StartLsn,
                    LastLsn = t.LastLsn,
                    Status = t.Status
                })
                .ToList();

            // Capture dirty pages
            var dirtyPages = new List<CheckpointDirtyPage>();
            foreach (var entry in _walEntries.Values.Where(e => e.PageId != null))
            {
                if (!dirtyPages.Any(p => p.PageId == entry.PageId))
                {
                    dirtyPages.Add(new CheckpointDirtyPage
                    {
                        PageId = entry.PageId!,
                        RecoveryLsn = entry.Lsn
                    });
                }
            }

            // Create checkpoint
            var checkpoint = new Checkpoint
            {
                CheckpointId = checkpointId,
                Type = type,
                Lsn = checkpointLsn,
                CreatedAt = startTime,
                ActiveTransactions = activeTransactionList,
                DirtyPages = dirtyPages,
                Duration = DateTime.UtcNow - startTime
            };

            // Persist checkpoint
            await PersistCheckpointAsync(checkpoint, ct);

            // Write end checkpoint record
            var endEntry = new WalEntry
            {
                Type = WalEntryType.CheckpointEnd,
                CheckpointId = checkpointId,
                Lsn = CurrentLsn
            };
            await WriteWalEntryAsync(endEntry, ct);

            // Update state
            _checkpoints[checkpointId] = checkpoint;
            Interlocked.Exchange(ref _lastCheckpointLsn, checkpointLsn);

            // Truncate old WAL entries if configured
            if (_config.WalTruncationEnabled)
            {
                await TruncateWalAsync(checkpoint, ct);
            }

            CheckpointCreated?.Invoke(this, checkpoint);
            return checkpoint;
        }
        finally
        {
            _checkpointLock.Release();
        }
    }

    /// <summary>
    /// Creates a periodic checkpoint if needed.
    /// </summary>
    private async Task CreatePeriodicCheckpointAsync()
    {
        if (_cts?.IsCancellationRequested ?? true) return;
        if (_isRecovering) return;

        try
        {
            // Check if checkpoint is needed based on WAL size
            var walSize = await _walManager.GetSizeAsync(CancellationToken.None);
            if (walSize > _config.CheckpointWalSizeThreshold)
            {
                await CreateCheckpointAsync(CheckpointType.Periodic, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Periodic checkpoint failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Persists a checkpoint to disk.
    /// </summary>
    private async Task PersistCheckpointAsync(Checkpoint checkpoint, CancellationToken ct)
    {
        var checkpointFile = Path.Combine(_checkpointPath, $"{checkpoint.CheckpointId}.json");
        var json = JsonSerializer.Serialize(checkpoint, JsonOptions);
        await File.WriteAllTextAsync(checkpointFile, json, ct);

        // Update latest checkpoint marker
        var latestFile = Path.Combine(_checkpointPath, "latest.txt");
        await File.WriteAllTextAsync(latestFile, checkpoint.CheckpointId, ct);
    }

    /// <summary>
    /// Loads the last checkpoint from disk.
    /// </summary>
    private async Task<Checkpoint?> LoadLastCheckpointAsync(CancellationToken ct)
    {
        var latestFile = Path.Combine(_checkpointPath, "latest.txt");
        if (!File.Exists(latestFile))
        {
            return null;
        }

        var checkpointId = await File.ReadAllTextAsync(latestFile, ct);
        var checkpointFile = Path.Combine(_checkpointPath, $"{checkpointId.Trim()}.json");

        if (!File.Exists(checkpointFile))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(checkpointFile, ct);
        return JsonSerializer.Deserialize<Checkpoint>(json, JsonOptions);
    }

    /// <summary>
    /// Truncates WAL entries before the checkpoint.
    /// </summary>
    private async Task TruncateWalAsync(Checkpoint checkpoint, CancellationToken ct)
    {
        // Find minimum LSN needed for recovery
        var minRequiredLsn = checkpoint.Lsn;

        // Check active transactions
        if (checkpoint.ActiveTransactions.Any())
        {
            var minTxLsn = checkpoint.ActiveTransactions.Min(t => t.StartLsn);
            minRequiredLsn = Math.Min(minRequiredLsn, minTxLsn);
        }

        // Check dirty pages
        if (checkpoint.DirtyPages.Any())
        {
            var minPageLsn = checkpoint.DirtyPages.Min(p => p.RecoveryLsn);
            minRequiredLsn = Math.Min(minRequiredLsn, minPageLsn);
        }

        await _walManager.TruncateBeforeLsnAsync(minRequiredLsn, ct);

        // Remove truncated entries from memory
        var keysToRemove = _walEntries.Keys.Where(k => k < minRequiredLsn).ToList();
        foreach (var key in keysToRemove)
        {
            _walEntries.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Lists all checkpoints.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of checkpoints.</returns>
    public async Task<IReadOnlyList<Checkpoint>> ListCheckpointsAsync(CancellationToken ct = default)
    {
        var checkpoints = new List<Checkpoint>();

        foreach (var file in Directory.EnumerateFiles(_checkpointPath, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file, ct);
                var checkpoint = JsonSerializer.Deserialize<Checkpoint>(json, JsonOptions);
                if (checkpoint != null)
                {
                    checkpoints.Add(checkpoint);
                }
            }
            catch
            {
                // Skip invalid checkpoint files
            }
        }

        return checkpoints.OrderByDescending(c => c.CreatedAt).ToList();
    }

    #endregion

    #region Point-in-Time Recovery

    /// <summary>
    /// Recovers to a specific point in time.
    /// </summary>
    /// <param name="targetTime">The target recovery time.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The recovery result.</returns>
    public async Task<PointInTimeRecoveryResult> RecoverToPointInTimeAsync(
        DateTime targetTime,
        CancellationToken ct = default)
    {
        var startTime = DateTime.UtcNow;
        var result = new PointInTimeRecoveryResult
        {
            TargetTime = targetTime,
            StartedAt = startTime
        };

        await _recoveryLock.WaitAsync(ct);
        try
        {
            _isRecovering = true;

            // Find the closest checkpoint before target time
            var checkpoints = await ListCheckpointsAsync(ct);
            var targetCheckpoint = checkpoints
                .Where(c => c.CreatedAt <= targetTime)
                .OrderByDescending(c => c.CreatedAt)
                .FirstOrDefault();

            if (targetCheckpoint == null)
            {
                result.Success = false;
                result.ErrorMessage = $"No checkpoint found before {targetTime:O}";
                return result;
            }

            result.BaseCheckpointId = targetCheckpoint.CheckpointId;

            // Redo operations up to target time
            var operationsRedone = 0;
            await foreach (var entry in _walManager.ReadEntriesFromLsnAsync(targetCheckpoint.Lsn, ct))
            {
                if (entry.Timestamp > targetTime)
                {
                    break;
                }

                await RedoOperationAsync(entry, ct);
                operationsRedone++;
            }

            result.Success = true;
            result.OperationsApplied = operationsRedone;
            result.CompletedAt = DateTime.UtcNow;
            result.Duration = result.CompletedAt.Value - startTime;

            // Create recovery checkpoint
            await CreateCheckpointAsync(CheckpointType.PointInTimeRecovery, ct);

            return result;
        }
        finally
        {
            _isRecovering = false;
            _recoveryLock.Release();
        }
    }

    /// <summary>
    /// Recovers to a specific LSN.
    /// </summary>
    /// <param name="targetLsn">The target LSN.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The recovery result.</returns>
    public async Task<PointInTimeRecoveryResult> RecoverToLsnAsync(
        long targetLsn,
        CancellationToken ct = default)
    {
        var startTime = DateTime.UtcNow;
        var result = new PointInTimeRecoveryResult
        {
            TargetLsn = targetLsn,
            StartedAt = startTime
        };

        await _recoveryLock.WaitAsync(ct);
        try
        {
            _isRecovering = true;

            // Find the closest checkpoint before target LSN
            var checkpoints = await ListCheckpointsAsync(ct);
            var targetCheckpoint = checkpoints
                .Where(c => c.Lsn <= targetLsn)
                .OrderByDescending(c => c.Lsn)
                .FirstOrDefault();

            if (targetCheckpoint == null)
            {
                result.Success = false;
                result.ErrorMessage = $"No checkpoint found before LSN {targetLsn}";
                return result;
            }

            result.BaseCheckpointId = targetCheckpoint.CheckpointId;

            // Redo operations up to target LSN
            var operationsRedone = 0;
            await foreach (var entry in _walManager.ReadEntriesFromLsnAsync(targetCheckpoint.Lsn, ct))
            {
                if (entry.Lsn > targetLsn)
                {
                    break;
                }

                await RedoOperationAsync(entry, ct);
                operationsRedone++;
            }

            result.Success = true;
            result.OperationsApplied = operationsRedone;
            result.CompletedAt = DateTime.UtcNow;
            result.Duration = result.CompletedAt.Value - startTime;

            return result;
        }
        finally
        {
            _isRecovering = false;
            _recoveryLock.Release();
        }
    }

    #endregion

    #region ISnapshotProvider Implementation

    /// <inheritdoc />
    public override async Task<SnapshotInfo> CreateSnapshotAsync(
        SnapshotRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.Name);

        // Ensure WAL is flushed before creating snapshot
        await _walManager.FlushAsync(ct);

        // Create checkpoint first
        var checkpoint = await CreateCheckpointAsync(CheckpointType.Snapshot, ct);

        var snapshotId = GenerateSnapshotId();
        var now = DateTime.UtcNow;

        var metadata = new SnapshotMetadata
        {
            SnapshotId = snapshotId,
            Name = request.Name,
            Description = request.Description,
            CreatedAt = now,
            State = SnapshotState.Available,
            CheckpointId = checkpoint.CheckpointId,
            Lsn = checkpoint.Lsn,
            Tags = new Dictionary<string, string>(request.Tags),
            RetentionPolicy = request.RetentionPolicy
        };

        _snapshots[snapshotId] = metadata;
        await PersistStateAsync(ct);

        return ToSnapshotInfo(metadata);
    }

    /// <inheritdoc />
    public override Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(
        SnapshotFilter? filter = null,
        CancellationToken ct = default)
    {
        var query = _snapshots.Values.AsEnumerable();

        if (filter != null)
        {
            if (filter.CreatedAfter.HasValue)
                query = query.Where(s => s.CreatedAt >= filter.CreatedAfter.Value);

            if (filter.CreatedBefore.HasValue)
                query = query.Where(s => s.CreatedAt <= filter.CreatedBefore.Value);

            if (!string.IsNullOrEmpty(filter.NamePattern))
            {
                var pattern = filter.NamePattern.Replace("*", ".*");
                var regex = new System.Text.RegularExpressions.Regex(
                    $"^{pattern}$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                query = query.Where(s => regex.IsMatch(s.Name));
            }

            if (filter.Tags != null && filter.Tags.Count > 0)
            {
                query = query.Where(s => filter.Tags.All(t =>
                    s.Tags.TryGetValue(t.Key, out var v) && v == t.Value));
            }

            if (filter.HasLegalHold.HasValue)
            {
                query = query.Where(s =>
                    filter.HasLegalHold.Value
                        ? s.LegalHolds.Count > 0
                        : s.LegalHolds.Count == 0);
            }
        }

        var limit = filter?.Limit ?? 100;
        var results = query
            .OrderByDescending(s => s.CreatedAt)
            .Take(limit)
            .Select(ToSnapshotInfo)
            .ToList();

        return Task.FromResult<IReadOnlyList<SnapshotInfo>>(results);
    }

    /// <inheritdoc />
    public override Task<SnapshotInfo?> GetSnapshotAsync(string snapshotId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(snapshotId);

        if (_snapshots.TryGetValue(snapshotId, out var metadata))
        {
            return Task.FromResult<SnapshotInfo?>(ToSnapshotInfo(metadata));
        }

        return Task.FromResult<SnapshotInfo?>(null);
    }

    /// <inheritdoc />
    public override async Task<bool> DeleteSnapshotAsync(string snapshotId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(snapshotId);

        if (!_snapshots.TryGetValue(snapshotId, out var metadata))
        {
            return false;
        }

        // Check legal hold
        if (metadata.LegalHolds.Count > 0)
        {
            throw new InvalidOperationException(
                $"Cannot delete snapshot '{snapshotId}': {metadata.LegalHolds.Count} legal hold(s) active");
        }

        // Check retention policy
        if (metadata.RetentionPolicy != null &&
            !metadata.RetentionPolicy.AllowEarlyDeletion &&
            metadata.RetentionPolicy.RetainUntil.HasValue &&
            DateTime.UtcNow < metadata.RetentionPolicy.RetainUntil.Value)
        {
            throw new InvalidOperationException(
                $"Cannot delete snapshot '{snapshotId}': Retention policy prohibits early deletion");
        }

        _snapshots.TryRemove(snapshotId, out _);
        await PersistStateAsync(ct);

        return true;
    }

    /// <inheritdoc />
    public override async Task<RestoreResult> RestoreSnapshotAsync(
        string snapshotId,
        RestoreOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(snapshotId);

        if (!_snapshots.TryGetValue(snapshotId, out var metadata))
        {
            return new RestoreResult
            {
                Success = false,
                Errors = new[] { $"Snapshot '{snapshotId}' not found" }
            };
        }

        // Recover to snapshot LSN
        var recoveryResult = await RecoverToLsnAsync(metadata.Lsn, ct);

        return new RestoreResult
        {
            Success = recoveryResult.Success,
            ObjectsRestored = recoveryResult.OperationsApplied,
            Duration = recoveryResult.Duration ?? TimeSpan.Zero,
            Errors = recoveryResult.Success ? Array.Empty<string>() : new[] { recoveryResult.ErrorMessage! }
        };
    }

    /// <inheritdoc />
    public override async Task<bool> PlaceLegalHoldAsync(
        string snapshotId,
        LegalHoldInfo holdInfo,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(snapshotId);
        ArgumentNullException.ThrowIfNull(holdInfo);

        if (!_snapshots.TryGetValue(snapshotId, out var metadata))
        {
            throw new KeyNotFoundException($"Snapshot '{snapshotId}' not found");
        }

        var hold = new LegalHoldMetadata
        {
            HoldId = string.IsNullOrEmpty(holdInfo.HoldId) ? $"hold_{Guid.NewGuid():N}" : holdInfo.HoldId,
            Reason = holdInfo.Reason,
            CaseNumber = holdInfo.CaseNumber,
            PlacedAt = DateTime.UtcNow,
            PlacedBy = holdInfo.PlacedBy,
            ExpiresAt = holdInfo.ExpiresAt
        };

        metadata.LegalHolds.Add(hold);
        await PersistStateAsync(ct);

        return true;
    }

    /// <inheritdoc />
    public override async Task<bool> RemoveLegalHoldAsync(
        string snapshotId,
        string holdId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(snapshotId);
        ArgumentException.ThrowIfNullOrEmpty(holdId);

        if (!_snapshots.TryGetValue(snapshotId, out var metadata))
        {
            throw new KeyNotFoundException($"Snapshot '{snapshotId}' not found");
        }

        var hold = metadata.LegalHolds.FirstOrDefault(h => h.HoldId == holdId);
        if (hold == null)
        {
            return false;
        }

        metadata.LegalHolds.Remove(hold);
        await PersistStateAsync(ct);

        return true;
    }

    /// <inheritdoc />
    public override async Task<bool> SetRetentionPolicyAsync(
        string snapshotId,
        SnapshotRetentionPolicy policy,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(snapshotId);
        ArgumentNullException.ThrowIfNull(policy);

        if (!_snapshots.TryGetValue(snapshotId, out var metadata))
        {
            throw new KeyNotFoundException($"Snapshot '{snapshotId}' not found");
        }

        metadata.RetentionPolicy = policy;
        await PersistStateAsync(ct);

        return true;
    }

    #endregion

    #region Statistics

    /// <summary>
    /// Gets crash recovery statistics.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Recovery statistics.</returns>
    public async Task<CrashRecoveryStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        var checkpoints = await ListCheckpointsAsync(ct);
        var walSize = await _walManager.GetSizeAsync(ct);

        return new CrashRecoveryStatistics
        {
            CurrentLsn = CurrentLsn,
            LastCheckpointLsn = LastCheckpointLsn,
            ActiveTransactions = _activeTransactions.Count,
            WalEntriesInMemory = _walEntries.Count,
            WalSizeBytes = walSize,
            CheckpointCount = checkpoints.Count,
            LastCheckpointTime = checkpoints.FirstOrDefault()?.CreatedAt,
            SnapshotCount = _snapshots.Count,
            RecoveryOperationsExecuted = _recoveryOperations.Count,
            IsRecovering = IsRecovering,
            CurrentRecoveryProgress = CurrentRecoveryProgress
        };
    }

    #endregion

    #region Helper Methods

    private void ReportProgress(RecoveryProgress progress)
    {
        RecoveryProgressChanged?.Invoke(this, progress);
    }

    private static string GenerateTransactionId() =>
        $"tx_{DateTime.UtcNow.Ticks:x}_{Guid.NewGuid():N}";

    private static string GenerateSnapshotId() =>
        $"snap_{DateTime.UtcNow.Ticks:x}_{Guid.NewGuid():N}";

    private static string GenerateCheckpointId() =>
        $"ckpt_{DateTime.UtcNow.Ticks:x}_{Guid.NewGuid():N}";

    private string GetPagePath(string pageId)
    {
        var prefix = pageId.Length >= 2 ? pageId[..2] : pageId;
        return Path.Combine(_storagePath, "pages", prefix, $"{pageId}.page");
    }

    private SnapshotInfo ToSnapshotInfo(SnapshotMetadata metadata)
    {
        return new SnapshotInfo
        {
            SnapshotId = metadata.SnapshotId,
            Name = metadata.Name,
            Description = metadata.Description,
            CreatedAt = metadata.CreatedAt,
            State = metadata.State,
            LegalHolds = metadata.LegalHolds.Select(h => new LegalHoldInfo
            {
                HoldId = h.HoldId,
                Reason = h.Reason,
                CaseNumber = h.CaseNumber,
                PlacedAt = h.PlacedAt,
                PlacedBy = h.PlacedBy,
                ExpiresAt = h.ExpiresAt
            }).ToList(),
            RetentionPolicy = metadata.RetentionPolicy,
            Tags = new Dictionary<string, string>(metadata.Tags)
        };
    }

    #endregion

    #region Persistence

    private async Task LoadStateAsync(CancellationToken ct)
    {
        var stateFile = Path.Combine(_storagePath, "metadata", "crash_recovery_state.json");
        if (!File.Exists(stateFile)) return;

        try
        {
            var json = await File.ReadAllTextAsync(stateFile, ct);
            var state = JsonSerializer.Deserialize<CrashRecoveryState>(json, JsonOptions);

            if (state == null) return;

            foreach (var snapshot in state.Snapshots)
            {
                _snapshots[snapshot.SnapshotId] = snapshot;
            }

            _currentLsn = state.CurrentLsn;
            _lastCheckpointLsn = state.LastCheckpointLsn;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load crash recovery state: {ex.Message}");
        }
    }

    private async Task PersistStateAsync(CancellationToken ct)
    {
        var state = new CrashRecoveryState
        {
            Snapshots = _snapshots.Values.ToList(),
            CurrentLsn = _currentLsn,
            LastCheckpointLsn = _lastCheckpointLsn,
            SavedAt = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(state, JsonOptions);
        var stateFile = Path.Combine(_storagePath, "metadata", "crash_recovery_state.json");
        await File.WriteAllTextAsync(stateFile, json, ct);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    #endregion

    #region Metadata

    /// <inheritdoc />
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return new List<PluginCapabilityDescriptor>
        {
            new()
            {
                Name = "CreateSnapshot",
                Description = "Creates a crash-consistent snapshot with WAL checkpoint",
                Parameters = new List<PluginParameterDescriptor>
                {
                    new() { Name = "name", Type = "string", Required = true, Description = "Snapshot name" }
                }
            },
            new()
            {
                Name = "BeginTransaction",
                Description = "Begins a new recoverable transaction",
                Parameters = new List<PluginParameterDescriptor>
                {
                    new() { Name = "transactionId", Type = "string", Required = false, Description = "Optional transaction ID" }
                }
            },
            new()
            {
                Name = "CommitTransaction",
                Description = "Commits a transaction with WAL durability",
                Parameters = new List<PluginParameterDescriptor>
                {
                    new() { Name = "transactionId", Type = "string", Required = true, Description = "Transaction ID to commit" }
                }
            },
            new()
            {
                Name = "CreateCheckpoint",
                Description = "Creates a checkpoint for faster recovery",
                Parameters = new List<PluginParameterDescriptor>
                {
                    new() { Name = "type", Type = "CheckpointType", Required = false, Description = "Checkpoint type" }
                }
            },
            new()
            {
                Name = "RecoverToPointInTime",
                Description = "Recovers database to a specific point in time",
                Parameters = new List<PluginParameterDescriptor>
                {
                    new() { Name = "targetTime", Type = "DateTime", Required = true, Description = "Target recovery time" }
                }
            },
            new()
            {
                Name = "RecoverToLsn",
                Description = "Recovers database to a specific Log Sequence Number",
                Parameters = new List<PluginParameterDescriptor>
                {
                    new() { Name = "targetLsn", Type = "long", Required = true, Description = "Target LSN" }
                }
            }
        };
    }

    /// <inheritdoc />
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["Description"] = "ARIES-style crash recovery with WAL, checkpoints, and point-in-time recovery";
        metadata["RecoveryAlgorithm"] = "ARIES";
        metadata["SupportsWAL"] = true;
        metadata["SupportsCheckpoints"] = true;
        metadata["SupportsPointInTimeRecovery"] = true;
        metadata["SupportsTransactionRollback"] = true;
        metadata["CurrentLsn"] = CurrentLsn;
        metadata["LastCheckpointLsn"] = LastCheckpointLsn;
        metadata["ActiveTransactions"] = ActiveTransactionCount;
        metadata["IsRecovering"] = IsRecovering;
        return metadata;
    }

    #endregion

    #region Disposal

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await StopAsync();
        _walLock.Dispose();
        _checkpointLock.Dispose();
        _recoveryLock.Dispose();
        await _walManager.DisposeAsync();
    }

    #endregion
}

#region WAL Manager

/// <summary>
/// Manages Write-Ahead Log operations including writing, reading, and truncation.
/// </summary>
internal sealed class WalManager : IAsyncDisposable
{
    private readonly string _walPath;
    private readonly WalConfig _config;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly List<WalSegment> _segments = new();
    private WalSegment? _activeSegment;
    private long _flushedLsn;

    public WalManager(string walPath, WalConfig config)
    {
        _walPath = walPath;
        _config = config;
        Directory.CreateDirectory(_walPath);
    }

    /// <summary>
    /// Appends a WAL entry.
    /// </summary>
    public async Task AppendAsync(WalEntry entry, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            EnsureActiveSegment();

            var data = SerializeEntry(entry);
            await _activeSegment!.AppendAsync(data, ct);

            // Check if we need to rotate
            if (_activeSegment.Size > _config.SegmentSizeBytes)
            {
                await RotateSegmentAsync(ct);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Syncs WAL to disk for durability.
    /// </summary>
    public async Task SyncAsync(CancellationToken ct)
    {
        if (_activeSegment != null)
        {
            await _activeSegment.SyncAsync(ct);
        }
    }

    /// <summary>
    /// Flushes WAL buffer to disk.
    /// </summary>
    public async Task<long> FlushAsync(CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            if (_activeSegment != null)
            {
                await _activeSegment.FlushAsync(ct);
                _flushedLsn = _activeSegment.LastLsn;
            }
            return _flushedLsn;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Reads WAL entries from a given LSN.
    /// </summary>
    public async IAsyncEnumerable<WalEntry> ReadEntriesFromLsnAsync(
        long startLsn,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var segment in GetSegmentsFromLsn(startLsn))
        {
            await foreach (var entry in segment.ReadEntriesAsync(startLsn, ct))
            {
                yield return entry;
            }
        }
    }

    /// <summary>
    /// Gets the number of entries since a given LSN.
    /// </summary>
    public async Task<int> GetEntryCountSinceLsnAsync(long lsn, CancellationToken ct)
    {
        var count = 0;
        await foreach (var _ in ReadEntriesFromLsnAsync(lsn, ct))
        {
            count++;
        }
        return count;
    }

    /// <summary>
    /// Checks if WAL has any entries.
    /// </summary>
    public Task<bool> HasEntriesAsync(CancellationToken ct)
    {
        return Task.FromResult(_segments.Count > 0 || _activeSegment != null);
    }

    /// <summary>
    /// Gets total WAL size in bytes.
    /// </summary>
    public Task<long> GetSizeAsync(CancellationToken ct)
    {
        var size = _segments.Sum(s => s.Size);
        if (_activeSegment != null)
        {
            size += _activeSegment.Size;
        }
        return Task.FromResult(size);
    }

    /// <summary>
    /// Truncates WAL entries before a given LSN.
    /// </summary>
    public async Task TruncateBeforeLsnAsync(long lsn, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var segmentsToRemove = _segments.Where(s => s.LastLsn < lsn).ToList();
            foreach (var segment in segmentsToRemove)
            {
                segment.Delete();
                _segments.Remove(segment);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void EnsureActiveSegment()
    {
        if (_activeSegment == null)
        {
            var segmentId = DateTime.UtcNow.Ticks;
            var segmentPath = Path.Combine(_walPath, $"wal_{segmentId:x16}.log");
            _activeSegment = new WalSegment(segmentPath, segmentId);
        }
    }

    private async Task RotateSegmentAsync(CancellationToken ct)
    {
        if (_activeSegment != null)
        {
            await _activeSegment.FlushAsync(ct);
            _segments.Add(_activeSegment);
        }

        var segmentId = DateTime.UtcNow.Ticks;
        var segmentPath = Path.Combine(_walPath, $"wal_{segmentId:x16}.log");
        _activeSegment = new WalSegment(segmentPath, segmentId);
    }

    private IEnumerable<WalSegment> GetSegmentsFromLsn(long lsn)
    {
        foreach (var segment in _segments.Where(s => s.LastLsn >= lsn))
        {
            yield return segment;
        }

        if (_activeSegment != null)
        {
            yield return _activeSegment;
        }
    }

    private static byte[] SerializeEntry(WalEntry entry)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(entry);
        var length = BitConverter.GetBytes(json.Length);
        var result = new byte[4 + json.Length];
        Buffer.BlockCopy(length, 0, result, 0, 4);
        Buffer.BlockCopy(json, 0, result, 4, json.Length);
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (_activeSegment != null)
        {
            await _activeSegment.DisposeAsync();
        }

        foreach (var segment in _segments)
        {
            await segment.DisposeAsync();
        }

        _writeLock.Dispose();
    }
}

/// <summary>
/// Represents a single WAL segment file.
/// </summary>
internal sealed class WalSegment : IAsyncDisposable
{
    private readonly string _path;
    private readonly long _segmentId;
    private FileStream? _stream;
    private long _size;
    private long _lastLsn;

    public long Size => _size;
    public long LastLsn => _lastLsn;
    public long SegmentId => _segmentId;

    public WalSegment(string path, long segmentId)
    {
        _path = path;
        _segmentId = segmentId;

        if (File.Exists(path))
        {
            _size = new FileInfo(path).Length;
        }
    }

    public async Task AppendAsync(byte[] data, CancellationToken ct)
    {
        EnsureStream();
        await _stream!.WriteAsync(data, ct);
        _size += data.Length;
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        if (_stream != null)
        {
            await _stream.FlushAsync(ct);
        }
    }

    public async Task SyncAsync(CancellationToken ct)
    {
        if (_stream != null)
        {
            await _stream.FlushAsync(ct);
            // Force fsync
            _stream.Flush(true);
        }
    }

    public async IAsyncEnumerable<WalEntry> ReadEntriesAsync(
        long startLsn,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (!File.Exists(_path))
        {
            yield break;
        }

        await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var lengthBuffer = new byte[4];

        while (stream.Position < stream.Length)
        {
            if (ct.IsCancellationRequested) yield break;

            var bytesRead = await stream.ReadAsync(lengthBuffer, ct);
            if (bytesRead < 4) break;

            var length = BitConverter.ToInt32(lengthBuffer);
            var entryBuffer = new byte[length];
            bytesRead = await stream.ReadAsync(entryBuffer, ct);
            if (bytesRead < length) break;

            var entry = JsonSerializer.Deserialize<WalEntry>(entryBuffer);
            if (entry != null && entry.Lsn >= startLsn)
            {
                _lastLsn = entry.Lsn;
                yield return entry;
            }
        }
    }

    public void Delete()
    {
        _stream?.Dispose();
        _stream = null;

        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    private void EnsureStream()
    {
        _stream ??= new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, true);
    }

    public async ValueTask DisposeAsync()
    {
        if (_stream != null)
        {
            await _stream.DisposeAsync();
        }
    }
}

#endregion

#region Supporting Types

/// <summary>
/// Configuration for the crash recovery plugin.
/// </summary>
public sealed record CrashRecoveryConfig
{
    /// <summary>Base storage path for recovery data.</summary>
    public string? StoragePath { get; init; }

    /// <summary>Interval between automatic checkpoints.</summary>
    public TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Interval between WAL flushes.</summary>
    public TimeSpan WalFlushInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>WAL size threshold that triggers a checkpoint.</summary>
    public long CheckpointWalSizeThreshold { get; init; } = 100 * 1024 * 1024; // 100 MB

    /// <summary>Whether to truncate WAL after checkpoint.</summary>
    public bool WalTruncationEnabled { get; init; } = true;

    /// <summary>Whether to fail recovery on redo errors.</summary>
    public bool FailOnRedoError { get; init; } = false;

    /// <summary>Whether to fail recovery on undo errors.</summary>
    public bool FailOnUndoError { get; init; } = false;

    /// <summary>WAL configuration.</summary>
    public WalConfig? WalConfig { get; init; }
}

/// <summary>
/// Configuration for Write-Ahead Log.
/// </summary>
public sealed record WalConfig
{
    /// <summary>Maximum size of a WAL segment in bytes.</summary>
    public long SegmentSizeBytes { get; init; } = 64 * 1024 * 1024; // 64 MB

    /// <summary>Buffer size for WAL writes.</summary>
    public int BufferSize { get; init; } = 65536;

    /// <summary>Whether to use direct I/O.</summary>
    public bool UseDirectIO { get; init; } = false;
}

/// <summary>
/// Write-Ahead Log entry.
/// </summary>
public sealed class WalEntry
{
    /// <summary>Log Sequence Number.</summary>
    public long Lsn { get; set; }

    /// <summary>Entry type.</summary>
    public WalEntryType Type { get; set; }

    /// <summary>Transaction ID associated with this entry.</summary>
    public string? TransactionId { get; set; }

    /// <summary>Page ID affected by this entry.</summary>
    public string? PageId { get; set; }

    /// <summary>Checkpoint ID for checkpoint entries.</summary>
    public string? CheckpointId { get; set; }

    /// <summary>Page image before the operation.</summary>
    public byte[]? BeforeImage { get; set; }

    /// <summary>Page image after the operation.</summary>
    public byte[]? AfterImage { get; set; }

    /// <summary>Previous LSN in the transaction chain.</summary>
    public long? PrevLsn { get; set; }

    /// <summary>Next LSN to undo (for CLR entries).</summary>
    public long? UndoNextLsn { get; set; }

    /// <summary>Timestamp of the entry.</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>Additional data associated with the entry.</summary>
    public byte[]? Data { get; set; }
}

/// <summary>
/// Type of WAL entry.
/// </summary>
public enum WalEntryType
{
    /// <summary>Begin transaction.</summary>
    BeginTransaction,

    /// <summary>Commit transaction.</summary>
    CommitTransaction,

    /// <summary>Abort transaction.</summary>
    AbortTransaction,

    /// <summary>Write operation.</summary>
    Write,

    /// <summary>Update operation.</summary>
    Update,

    /// <summary>Delete operation.</summary>
    Delete,

    /// <summary>Checkpoint begin.</summary>
    CheckpointBegin,

    /// <summary>Checkpoint end.</summary>
    CheckpointEnd,

    /// <summary>General checkpoint marker.</summary>
    Checkpoint,

    /// <summary>Compensation Log Record (CLR).</summary>
    CompensationLogRecord
}

/// <summary>
/// Checkpoint information.
/// </summary>
public sealed class Checkpoint
{
    /// <summary>Unique checkpoint ID.</summary>
    public string CheckpointId { get; init; } = string.Empty;

    /// <summary>Type of checkpoint.</summary>
    public CheckpointType Type { get; init; }

    /// <summary>LSN at checkpoint time.</summary>
    public long Lsn { get; init; }

    /// <summary>Time checkpoint was created.</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>Active transactions at checkpoint time.</summary>
    public List<CheckpointTransaction> ActiveTransactions { get; init; } = new();

    /// <summary>Dirty pages at checkpoint time.</summary>
    public List<CheckpointDirtyPage> DirtyPages { get; init; } = new();

    /// <summary>Duration to create checkpoint.</summary>
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// Type of checkpoint.
/// </summary>
public enum CheckpointType
{
    /// <summary>Periodic automatic checkpoint.</summary>
    Periodic,

    /// <summary>Clean shutdown checkpoint.</summary>
    Shutdown,

    /// <summary>Recovery checkpoint.</summary>
    Recovery,

    /// <summary>Snapshot checkpoint.</summary>
    Snapshot,

    /// <summary>Point-in-time recovery checkpoint.</summary>
    PointInTimeRecovery,

    /// <summary>User-requested checkpoint.</summary>
    Manual
}

/// <summary>
/// Transaction recorded in checkpoint.
/// </summary>
public sealed class CheckpointTransaction
{
    /// <summary>Transaction ID.</summary>
    public string TransactionId { get; init; } = string.Empty;

    /// <summary>Start LSN of transaction.</summary>
    public long StartLsn { get; init; }

    /// <summary>Last LSN of transaction.</summary>
    public long LastLsn { get; init; }

    /// <summary>Transaction status.</summary>
    public TransactionStatus Status { get; init; }
}

/// <summary>
/// Dirty page recorded in checkpoint.
/// </summary>
public sealed class CheckpointDirtyPage
{
    /// <summary>Page ID.</summary>
    public string PageId { get; init; } = string.Empty;

    /// <summary>LSN from which recovery should start for this page.</summary>
    public long RecoveryLsn { get; init; }
}

/// <summary>
/// Transaction state information.
/// </summary>
public sealed class TransactionState
{
    /// <summary>Transaction ID.</summary>
    public string TransactionId { get; init; } = string.Empty;

    /// <summary>Transaction status.</summary>
    public TransactionStatus Status { get; set; }

    /// <summary>Start LSN.</summary>
    public long StartLsn { get; init; }

    /// <summary>End LSN.</summary>
    public long EndLsn { get; set; }

    /// <summary>Last operation LSN.</summary>
    public long LastLsn { get; set; }

    /// <summary>Transaction start time.</summary>
    public DateTime StartTime { get; init; }

    /// <summary>Transaction end time.</summary>
    public DateTime? EndTime { get; set; }

    /// <summary>Operations in this transaction.</summary>
    public List<WalEntry> Operations { get; init; } = new();
}

/// <summary>
/// Transaction status.
/// </summary>
public enum TransactionStatus
{
    /// <summary>Transaction is active.</summary>
    Active,

    /// <summary>Transaction is committed.</summary>
    Committed,

    /// <summary>Transaction is aborted.</summary>
    Aborted,

    /// <summary>Transaction is being prepared (2PC).</summary>
    Preparing,

    /// <summary>Transaction is prepared (2PC).</summary>
    Prepared
}

/// <summary>
/// Recovery operation record.
/// </summary>
public sealed class RecoveryOperation
{
    /// <summary>Operation type.</summary>
    public RecoveryOperationType Type { get; init; }

    /// <summary>Associated WAL entry.</summary>
    public WalEntry? WalEntry { get; init; }

    /// <summary>Time operation was executed.</summary>
    public DateTime ExecutedAt { get; init; }
}

/// <summary>
/// Type of recovery operation.
/// </summary>
public enum RecoveryOperationType
{
    /// <summary>Redo operation.</summary>
    Redo,

    /// <summary>Undo operation.</summary>
    Undo
}

/// <summary>
/// Recovery progress information.
/// </summary>
public sealed class RecoveryProgress
{
    /// <summary>Current recovery phase.</summary>
    public RecoveryPhase Phase { get; set; }

    /// <summary>Progress within current phase (0-100).</summary>
    public int PhaseProgress { get; set; }

    /// <summary>Recovery status.</summary>
    public RecoveryStatus Status { get; set; }

    /// <summary>Time recovery started.</summary>
    public DateTime StartedAt { get; init; }

    /// <summary>Time recovery completed.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Total recovery duration.</summary>
    public TimeSpan? TotalDuration { get; set; }

    /// <summary>Number of entries analyzed.</summary>
    public int AnalyzedEntries { get; set; }

    /// <summary>Number of operations redone.</summary>
    public int RedoneOperations { get; set; }

    /// <summary>Number of transactions undone.</summary>
    public int UndoneTransactions { get; set; }

    /// <summary>Error message if recovery failed.</summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Recovery phase.
/// </summary>
public enum RecoveryPhase
{
    /// <summary>Analysis phase.</summary>
    Analysis,

    /// <summary>Redo phase.</summary>
    Redo,

    /// <summary>Undo phase.</summary>
    Undo,

    /// <summary>Completed.</summary>
    Completed
}

/// <summary>
/// Recovery status.
/// </summary>
public enum RecoveryStatus
{
    /// <summary>Not started.</summary>
    NotStarted,

    /// <summary>In progress.</summary>
    InProgress,

    /// <summary>Completed successfully.</summary>
    Completed,

    /// <summary>Failed.</summary>
    Failed
}

/// <summary>
/// Reason for transaction rollback.
/// </summary>
public enum RollbackReason
{
    /// <summary>User requested abort.</summary>
    UserRequested,

    /// <summary>Crash recovery.</summary>
    CrashRecovery,

    /// <summary>Timeout.</summary>
    Timeout,

    /// <summary>Deadlock.</summary>
    Deadlock,

    /// <summary>Constraint violation.</summary>
    ConstraintViolation
}

/// <summary>
/// Result of point-in-time recovery.
/// </summary>
public sealed class PointInTimeRecoveryResult
{
    /// <summary>Whether recovery succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Target recovery time.</summary>
    public DateTime? TargetTime { get; init; }

    /// <summary>Target recovery LSN.</summary>
    public long? TargetLsn { get; init; }

    /// <summary>Base checkpoint ID used for recovery.</summary>
    public string? BaseCheckpointId { get; set; }

    /// <summary>Number of operations applied.</summary>
    public int OperationsApplied { get; set; }

    /// <summary>Time recovery started.</summary>
    public DateTime StartedAt { get; init; }

    /// <summary>Time recovery completed.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Recovery duration.</summary>
    public TimeSpan? Duration { get; set; }

    /// <summary>Error message if recovery failed.</summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Crash recovery statistics.
/// </summary>
public sealed class CrashRecoveryStatistics
{
    /// <summary>Current LSN.</summary>
    public long CurrentLsn { get; init; }

    /// <summary>Last checkpoint LSN.</summary>
    public long LastCheckpointLsn { get; init; }

    /// <summary>Number of active transactions.</summary>
    public int ActiveTransactions { get; init; }

    /// <summary>Number of WAL entries in memory.</summary>
    public int WalEntriesInMemory { get; init; }

    /// <summary>Total WAL size in bytes.</summary>
    public long WalSizeBytes { get; init; }

    /// <summary>Number of checkpoints.</summary>
    public int CheckpointCount { get; init; }

    /// <summary>Time of last checkpoint.</summary>
    public DateTime? LastCheckpointTime { get; init; }

    /// <summary>Number of snapshots.</summary>
    public int SnapshotCount { get; init; }

    /// <summary>Number of recovery operations executed.</summary>
    public int RecoveryOperationsExecuted { get; init; }

    /// <summary>Whether recovery is in progress.</summary>
    public bool IsRecovering { get; init; }

    /// <summary>Current recovery progress.</summary>
    public RecoveryProgress? CurrentRecoveryProgress { get; init; }
}

#endregion

#region Event Args

/// <summary>
/// Event arguments for recovery started event.
/// </summary>
public sealed class RecoveryStartedEventArgs : EventArgs
{
    /// <summary>Time recovery started.</summary>
    public DateTime StartTime { get; init; }

    /// <summary>Last checkpoint LSN.</summary>
    public long LastCheckpointLsn { get; init; }

    /// <summary>Number of WAL entries to process.</summary>
    public int RequiredWalEntries { get; init; }
}

/// <summary>
/// Event arguments for recovery completed event.
/// </summary>
public sealed class RecoveryCompletedEventArgs : EventArgs
{
    /// <summary>Whether recovery succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Recovery progress.</summary>
    public RecoveryProgress? Progress { get; init; }

    /// <summary>Recovery duration.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Number of operations redone.</summary>
    public int RedoneOperations { get; init; }

    /// <summary>Number of transactions undone.</summary>
    public int UndoneTransactions { get; init; }

    /// <summary>Error message if recovery failed.</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Event arguments for transaction rollback event.
/// </summary>
public sealed class TransactionRollbackEventArgs : EventArgs
{
    /// <summary>Transaction ID.</summary>
    public string TransactionId { get; init; } = string.Empty;

    /// <summary>Start LSN of transaction.</summary>
    public long StartLsn { get; init; }

    /// <summary>Number of operations rolled back.</summary>
    public int OperationCount { get; init; }

    /// <summary>Reason for rollback.</summary>
    public RollbackReason Reason { get; init; }
}

/// <summary>
/// Event arguments for WAL flush event.
/// </summary>
public sealed class WalFlushEventArgs : EventArgs
{
    /// <summary>LSN up to which WAL was flushed.</summary>
    public long FlushedLsn { get; init; }

    /// <summary>Time of flush.</summary>
    public DateTime FlushTime { get; init; }

    /// <summary>Number of entries flushed.</summary>
    public int EntriesFlushed { get; init; }
}

#endregion

#region Exceptions

/// <summary>
/// Exception thrown when crash recovery fails.
/// </summary>
public class RecoveryException : Exception
{
    /// <summary>
    /// Creates a new recovery exception.
    /// </summary>
    public RecoveryException(string message) : base(message) { }

    /// <summary>
    /// Creates a new recovery exception with inner exception.
    /// </summary>
    public RecoveryException(string message, Exception inner) : base(message, inner) { }
}

#endregion

#region Internal Types

/// <summary>
/// Internal snapshot metadata.
/// </summary>
internal sealed class SnapshotMetadata
{
    public string SnapshotId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public DateTime CreatedAt { get; init; }
    public SnapshotState State { get; set; }
    public string CheckpointId { get; init; } = string.Empty;
    public long Lsn { get; init; }
    public Dictionary<string, string> Tags { get; init; } = new();
    public SnapshotRetentionPolicy? RetentionPolicy { get; set; }
    public List<LegalHoldMetadata> LegalHolds { get; init; } = new();
}

/// <summary>
/// Internal legal hold metadata.
/// </summary>
internal sealed class LegalHoldMetadata
{
    public string HoldId { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string? CaseNumber { get; init; }
    public DateTime PlacedAt { get; init; }
    public string? PlacedBy { get; init; }
    public DateTime? ExpiresAt { get; init; }
}

/// <summary>
/// Internal plugin state for persistence.
/// </summary>
internal sealed class CrashRecoveryState
{
    public List<SnapshotMetadata> Snapshots { get; init; } = new();
    public long CurrentLsn { get; init; }
    public long LastCheckpointLsn { get; init; }
    public DateTime SavedAt { get; init; }
}

/// <summary>
/// Result of analysis phase.
/// </summary>
internal sealed class AnalysisResult
{
    public int EntriesAnalyzed { get; set; }
    public long RedoStartLsn { get; set; }
    public Dictionary<string, long> DirtyPages { get; set; } = new();
    public List<TransactionInfo> ActiveTransactions { get; set; } = new();
}

/// <summary>
/// Transaction information for recovery.
/// </summary>
internal sealed class TransactionInfo
{
    public string TransactionId { get; init; } = string.Empty;
    public long StartLsn { get; init; }
    public long EndLsn { get; set; }
    public TransactionStatus Status { get; set; }
    public List<WalEntry> Operations { get; init; } = new();
}

/// <summary>
/// Result of redo phase.
/// </summary>
internal sealed class RedoResult
{
    public int OperationsRedone { get; set; }
    public List<string> Errors { get; } = new();
}

/// <summary>
/// Result of undo phase.
/// </summary>
internal sealed class UndoResult
{
    public int TransactionsUndone { get; set; }
    public List<string> Errors { get; } = new();
}

#endregion
