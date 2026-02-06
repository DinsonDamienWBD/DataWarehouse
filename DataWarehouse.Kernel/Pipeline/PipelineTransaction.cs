using DataWarehouse.SDK.Contracts;
using PipelineContracts = DataWarehouse.SDK.Contracts.Pipeline;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Kernel.Pipeline;

/// <summary>
/// Default implementation of PipelineContracts.IPipelineTransaction.
/// Tracks executed stages and terminals for unified rollback on failure.
/// </summary>
public sealed class PipelineTransaction : PipelineContracts.IPipelineTransaction
{
    private readonly List<PipelineContracts.ExecutedStageInfo> _executedStages = new();
    private readonly List<PipelineContracts.ExecutedTerminalInfo> _executedTerminals = new();
    private readonly ILogger? _logger;
    private readonly IKernelContext? _kernelContext;
    private readonly object _lock = new();
    private PipelineContracts.PipelineTransactionState _state = PipelineContracts.PipelineTransactionState.Active;
    private Exception? _failureException;

    /// <summary>
    /// Creates a new pipeline transaction.
    /// </summary>
    public PipelineTransaction(
        string? transactionId = null,
        string? blobId = null,
        IKernelContext? kernelContext = null,
        ILogger? logger = null)
    {
        TransactionId = transactionId ?? Guid.NewGuid().ToString("N");
        BlobId = blobId ?? string.Empty;
        _kernelContext = kernelContext;
        _logger = logger;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    /// <inheritdoc />
    public string TransactionId { get; }

    /// <summary>Blob ID being processed in this transaction.</summary>
    public string BlobId { get; }

    /// <summary>When this transaction was created.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <inheritdoc />
    public PipelineContracts.PipelineTransactionState State
    {
        get { lock (_lock) return _state; }
    }

    /// <inheritdoc />
    public IReadOnlyList<PipelineContracts.ExecutedStageInfo> ExecutedStages
    {
        get { lock (_lock) return _executedStages.ToList().AsReadOnly(); }
    }

    /// <inheritdoc />
    public IReadOnlyList<PipelineContracts.ExecutedTerminalInfo> ExecutedTerminals
    {
        get { lock (_lock) return _executedTerminals.ToList().AsReadOnly(); }
    }

    /// <inheritdoc />
    public void RecordStageExecution(PipelineContracts.ExecutedStageInfo stageInfo)
    {
        ArgumentNullException.ThrowIfNull(stageInfo);

        lock (_lock)
        {
            if (_state != PipelineContracts.PipelineTransactionState.Active)
                throw new InvalidOperationException($"Cannot record stage execution: transaction is {_state}");

            _executedStages.Add(stageInfo);
            _logger?.LogDebug(
                "Transaction {TxId}: Recorded stage execution - {StageType} via {PluginId} (rollback: {SupportsRollback})",
                TransactionId, stageInfo.StageType, stageInfo.PluginId, stageInfo.SupportsRollback);
        }
    }

    /// <inheritdoc />
    public void RecordTerminalExecution(PipelineContracts.ExecutedTerminalInfo terminalInfo)
    {
        ArgumentNullException.ThrowIfNull(terminalInfo);

        lock (_lock)
        {
            if (_state != PipelineContracts.PipelineTransactionState.Active)
                throw new InvalidOperationException($"Cannot record terminal execution: transaction is {_state}");

            _executedTerminals.Add(terminalInfo);
            _logger?.LogDebug(
                "Transaction {TxId}: Recorded terminal write - {TerminalType} at {Path} (rollback: {SupportsRollback})",
                TransactionId, terminalInfo.TerminalType, terminalInfo.StoragePath, terminalInfo.SupportsRollback);
        }
    }

    /// <inheritdoc />
    public Task CommitAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_state != PipelineContracts.PipelineTransactionState.Active)
                throw new InvalidOperationException($"Cannot commit: transaction is {_state}");

            _state = PipelineContracts.PipelineTransactionState.Committed;
            _logger?.LogInformation(
                "Transaction {TxId}: Committed - {StageCount} stages, {TerminalCount} terminals",
                TransactionId, _executedStages.Count, _executedTerminals.Count);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void MarkFailed(Exception? exception = null)
    {
        lock (_lock)
        {
            if (_state != PipelineContracts.PipelineTransactionState.Active)
                return; // Already in terminal state

            _state = PipelineContracts.PipelineTransactionState.Failed;
            _failureException = exception;
            _logger?.LogWarning(
                exception,
                "Transaction {TxId}: Marked as failed - {StageCount} stages, {TerminalCount} terminals pending rollback",
                TransactionId, _executedStages.Count, _executedTerminals.Count);
        }
    }

    /// <inheritdoc />
    public async Task<PipelineContracts.RollbackResult> RollbackAsync(CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var attempts = new List<PipelineContracts.RollbackAttempt>();
        int stagesRolledBack = 0;
        int terminalsRolledBack = 0;
        int failures = 0;

        List<PipelineContracts.ExecutedStageInfo> stagesToRollback;
        List<PipelineContracts.ExecutedTerminalInfo> terminalsToRollback;

        lock (_lock)
        {
            if (_state == PipelineContracts.PipelineTransactionState.Committed)
                throw new InvalidOperationException("Cannot rollback: transaction is already committed");

            if (_state == PipelineContracts.PipelineTransactionState.RolledBack)
                return new PipelineContracts.RollbackResult { Success = true, Duration = TimeSpan.Zero };

            _state = PipelineContracts.PipelineTransactionState.RolledBack;

            // Copy lists for rollback (reversed order)
            stagesToRollback = _executedStages.ToList();
            stagesToRollback.Reverse();

            terminalsToRollback = _executedTerminals.ToList();
            terminalsToRollback.Reverse();
        }

        _logger?.LogInformation(
            "Transaction {TxId}: Starting rollback - {StageCount} stages, {TerminalCount} terminals",
            TransactionId, stagesToRollback.Count, terminalsToRollback.Count);

        // First, rollback terminals (delete written data)
        foreach (var terminal in terminalsToRollback)
        {
            if (ct.IsCancellationRequested)
                break;

            var attemptStopwatch = Stopwatch.StartNew();
            var attempt = new PipelineContracts.RollbackAttempt
            {
                ComponentType = "Terminal",
                ComponentId = $"{terminal.TerminalType}:{terminal.TerminalId}"
            };

            try
            {
                if (!terminal.SupportsRollback)
                {
                    _logger?.LogDebug(
                        "Transaction {TxId}: Terminal {TerminalId} does not support rollback, skipping",
                        TransactionId, terminal.TerminalId);
                    attempts.Add(attempt with { Success = true, Duration = attemptStopwatch.Elapsed });
                    continue;
                }

                if (terminal.TerminalInstance is IDataTerminal dataTerminal)
                {
                    var terminalContext = new TerminalContext
                    {
                        BlobId = BlobId,
                        StoragePath = terminal.StoragePath,
                        KernelContext = _kernelContext
                    };

                    var deleted = await dataTerminal.DeleteAsync(terminalContext, ct);

                    if (deleted)
                    {
                        terminalsRolledBack++;
                        _logger?.LogDebug(
                            "Transaction {TxId}: Rolled back terminal {TerminalId} at {Path}",
                            TransactionId, terminal.TerminalId, terminal.StoragePath);
                    }
                    else
                    {
                        _logger?.LogWarning(
                            "Transaction {TxId}: Terminal {TerminalId} delete returned false for {Path}",
                            TransactionId, terminal.TerminalId, terminal.StoragePath);
                    }

                    attempts.Add(attempt with { Success = deleted, Duration = attemptStopwatch.Elapsed });
                    if (!deleted) failures++;
                }
                else
                {
                    _logger?.LogWarning(
                        "Transaction {TxId}: Terminal instance is not IDataTerminal, cannot rollback",
                        TransactionId);
                    attempts.Add(attempt with {
                        Success = false,
                        ErrorMessage = "Terminal instance does not implement IDataTerminal",
                        Duration = attemptStopwatch.Elapsed
                    });
                    failures++;
                }
            }
            catch (Exception ex)
            {
                failures++;
                _logger?.LogError(
                    ex,
                    "Transaction {TxId}: Failed to rollback terminal {TerminalId}",
                    TransactionId, terminal.TerminalId);
                attempts.Add(attempt with {
                    Success = false,
                    ErrorMessage = ex.Message,
                    Exception = ex,
                    Duration = attemptStopwatch.Elapsed
                });
            }
        }

        // Then, rollback stages in reverse order
        foreach (var stage in stagesToRollback)
        {
            if (ct.IsCancellationRequested)
                break;

            var attemptStopwatch = Stopwatch.StartNew();
            var attempt = new PipelineContracts.RollbackAttempt
            {
                ComponentType = "Stage",
                ComponentId = $"{stage.StageType}:{stage.PluginId}"
            };

            try
            {
                if (!stage.SupportsRollback)
                {
                    _logger?.LogDebug(
                        "Transaction {TxId}: Stage {StageType} does not support rollback, skipping",
                        TransactionId, stage.StageType);
                    attempts.Add(attempt with { Success = true, Duration = attemptStopwatch.Elapsed });
                    continue;
                }

                if (stage.StageInstance is PipelineContracts.IRollbackable rollbackable)
                {
                    var rollbackContext = new PipelineContracts.RollbackContext
                    {
                        TransactionId = TransactionId,
                        BlobId = BlobId,
                        StageType = stage.StageType,
                        PluginId = stage.PluginId,
                        StrategyName = stage.StrategyName,
                        CapturedState = stage.CapturedState,
                        OriginalParameters = stage.Parameters,
                        ExecutedAt = stage.ExecutedAt,
                        KernelContext = _kernelContext
                    };

                    var rolledBack = await rollbackable.RollbackAsync(rollbackContext, ct);

                    if (rolledBack)
                    {
                        stagesRolledBack++;
                        _logger?.LogDebug(
                            "Transaction {TxId}: Rolled back stage {StageType}",
                            TransactionId, stage.StageType);
                    }

                    attempts.Add(attempt with { Success = rolledBack, Duration = attemptStopwatch.Elapsed });
                    if (!rolledBack) failures++;
                }
                else
                {
                    // Stage doesn't implement PipelineContracts.IRollbackable but was marked as supporting rollback
                    _logger?.LogWarning(
                        "Transaction {TxId}: Stage {StageType} marked as supporting rollback but doesn't implement PipelineContracts.IRollbackable",
                        TransactionId, stage.StageType);
                    attempts.Add(attempt with { Success = true, Duration = attemptStopwatch.Elapsed });
                }
            }
            catch (Exception ex)
            {
                failures++;
                _logger?.LogError(
                    ex,
                    "Transaction {TxId}: Failed to rollback stage {StageType}",
                    TransactionId, stage.StageType);
                attempts.Add(attempt with {
                    Success = false,
                    ErrorMessage = ex.Message,
                    Exception = ex,
                    Duration = attemptStopwatch.Elapsed
                });
            }
        }

        stopwatch.Stop();

        var result = new PipelineContracts.RollbackResult
        {
            Success = failures == 0,
            StagesRolledBack = stagesRolledBack,
            TerminalsRolledBack = terminalsRolledBack,
            FailureCount = failures,
            Attempts = attempts,
            Duration = stopwatch.Elapsed
        };

        _logger?.LogInformation(
            "Transaction {TxId}: Rollback complete - Success: {Success}, Stages: {Stages}, Terminals: {Terminals}, Failures: {Failures}, Duration: {Duration}ms",
            TransactionId, result.Success, stagesRolledBack, terminalsRolledBack, failures, stopwatch.ElapsedMilliseconds);

        return result;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // If transaction is still active when disposed, rollback
        if (_state == PipelineContracts.PipelineTransactionState.Active || _state == PipelineContracts.PipelineTransactionState.Failed)
        {
            _logger?.LogWarning(
                "Transaction {TxId}: Disposing without commit, initiating rollback",
                TransactionId);

            try
            {
                await RollbackAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Transaction {TxId}: Rollback during dispose failed", TransactionId);
            }
        }
    }
}

/// <summary>
/// Factory for creating pipeline transactions.
/// </summary>
public interface IPipelineTransactionFactory
{
    /// <summary>
    /// Creates a new pipeline transaction.
    /// </summary>
    PipelineContracts.IPipelineTransaction Create(string? transactionId = null, string? blobId = null, IKernelContext? kernelContext = null);
}

/// <summary>
/// Default transaction factory implementation.
/// </summary>
public sealed class PipelineTransactionFactory : IPipelineTransactionFactory
{
    private readonly ILogger? _logger;

    public PipelineTransactionFactory(ILogger? logger = null)
    {
        _logger = logger;
    }

    public PipelineContracts.IPipelineTransaction Create(string? transactionId = null, string? blobId = null, IKernelContext? kernelContext = null)
    {
        return new PipelineTransaction(transactionId, blobId, kernelContext, _logger);
    }
}
