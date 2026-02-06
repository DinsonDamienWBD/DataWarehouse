using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Pipeline;

/// <summary>
/// Interface for pipeline stages that support rollback/compensation.
/// Stages implementing this interface can undo their effects when a later stage fails.
/// </summary>
public interface IRollbackable
{
    /// <summary>
    /// Whether this stage supports rollback operations.
    /// </summary>
    bool SupportsRollback { get; }

    /// <summary>
    /// Rolls back the effects of a previously executed write operation.
    /// Called when a subsequent stage fails and the pipeline needs to be compensated.
    /// </summary>
    /// <param name="context">The rollback context containing state from the original operation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if rollback succeeded, false otherwise.</returns>
    Task<bool> RollbackAsync(RollbackContext context, CancellationToken ct = default);
}

/// <summary>
/// Context for rollback operations, containing state captured during the original execution.
/// </summary>
public record RollbackContext
{
    /// <summary>Unique transaction ID for this pipeline execution.</summary>
    public string TransactionId { get; init; } = string.Empty;

    /// <summary>The blob ID being processed.</summary>
    public string BlobId { get; init; } = string.Empty;

    /// <summary>Stage type that is being rolled back.</summary>
    public string StageType { get; init; } = string.Empty;

    /// <summary>Plugin ID that executed the stage.</summary>
    public string PluginId { get; init; } = string.Empty;

    /// <summary>Strategy name that was used.</summary>
    public string StrategyName { get; init; } = string.Empty;

    /// <summary>State captured during execution for rollback purposes.</summary>
    public Dictionary<string, object> CapturedState { get; init; } = new();

    /// <summary>Original parameters passed to the stage.</summary>
    public Dictionary<string, object> OriginalParameters { get; init; } = new();

    /// <summary>When the original operation was executed.</summary>
    public DateTimeOffset ExecutedAt { get; init; }

    /// <summary>Kernel context for service access.</summary>
    public IKernelContext? KernelContext { get; init; }
}

/// <summary>
/// Represents a pipeline transaction that tracks executed stages for potential rollback.
/// </summary>
public interface IPipelineTransaction : IAsyncDisposable
{
    /// <summary>Unique transaction ID.</summary>
    string TransactionId { get; }

    /// <summary>Current transaction state.</summary>
    PipelineTransactionState State { get; }

    /// <summary>List of stages that have been executed (in order).</summary>
    IReadOnlyList<ExecutedStageInfo> ExecutedStages { get; }

    /// <summary>List of terminals that have been written to.</summary>
    IReadOnlyList<ExecutedTerminalInfo> ExecutedTerminals { get; }

    /// <summary>
    /// Records a stage execution for potential rollback.
    /// </summary>
    void RecordStageExecution(ExecutedStageInfo stageInfo);

    /// <summary>
    /// Records a terminal write for potential rollback.
    /// </summary>
    void RecordTerminalExecution(ExecutedTerminalInfo terminalInfo);

    /// <summary>
    /// Commits the transaction, finalizing all changes.
    /// After commit, rollback is no longer possible.
    /// </summary>
    Task CommitAsync(CancellationToken ct = default);

    /// <summary>
    /// Rolls back all executed stages and terminals in reverse order.
    /// </summary>
    /// <returns>Rollback result with details of each rollback attempt.</returns>
    Task<RollbackResult> RollbackAsync(CancellationToken ct = default);

    /// <summary>
    /// Marks the transaction as failed without automatic rollback.
    /// Use when you want to inspect state before deciding on rollback.
    /// </summary>
    void MarkFailed(Exception? exception = null);
}

/// <summary>
/// Transaction state.
/// </summary>
public enum PipelineTransactionState
{
    /// <summary>Transaction is active and accepting operations.</summary>
    Active,
    /// <summary>Transaction has been committed successfully.</summary>
    Committed,
    /// <summary>Transaction failed and rollback is pending or complete.</summary>
    Failed,
    /// <summary>Transaction was rolled back.</summary>
    RolledBack
}

/// <summary>
/// Information about an executed stage for rollback purposes.
/// </summary>
public record ExecutedStageInfo
{
    /// <summary>Stage type (e.g., "Compression", "Encryption").</summary>
    public required string StageType { get; init; }

    /// <summary>Plugin ID that executed this stage.</summary>
    public required string PluginId { get; init; }

    /// <summary>Strategy name used.</summary>
    public required string StrategyName { get; init; }

    /// <summary>The stage instance (for calling rollback if supported).</summary>
    public required object StageInstance { get; init; }

    /// <summary>Whether this stage supports rollback.</summary>
    public bool SupportsRollback { get; init; }

    /// <summary>State captured during execution.</summary>
    public Dictionary<string, object> CapturedState { get; init; } = new();

    /// <summary>Original parameters passed to the stage.</summary>
    public Dictionary<string, object> Parameters { get; init; } = new();

    /// <summary>When the stage was executed.</summary>
    public DateTimeOffset ExecutedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Execution order position.</summary>
    public int Order { get; init; }
}

/// <summary>
/// Information about an executed terminal write for rollback purposes.
/// </summary>
public record ExecutedTerminalInfo
{
    /// <summary>Terminal type (e.g., "primary", "replica").</summary>
    public required string TerminalType { get; init; }

    /// <summary>Terminal ID.</summary>
    public required string TerminalId { get; init; }

    /// <summary>The terminal instance (for calling delete if needed).</summary>
    public required object TerminalInstance { get; init; }

    /// <summary>Storage path where data was written.</summary>
    public required string StoragePath { get; init; }

    /// <summary>Blob ID that was written.</summary>
    public string BlobId { get; init; } = string.Empty;

    /// <summary>Whether this terminal supports rollback (deletion).</summary>
    public bool SupportsRollback { get; init; } = true;

    /// <summary>When the terminal write was executed.</summary>
    public DateTimeOffset ExecutedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Version ID if versioning is supported (for precise rollback).</summary>
    public string? VersionId { get; init; }
}

/// <summary>
/// Result of a rollback operation.
/// </summary>
public record RollbackResult
{
    /// <summary>Whether all rollbacks succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Number of stages successfully rolled back.</summary>
    public int StagesRolledBack { get; init; }

    /// <summary>Number of terminals successfully rolled back.</summary>
    public int TerminalsRolledBack { get; init; }

    /// <summary>Number of rollback failures.</summary>
    public int FailureCount { get; init; }

    /// <summary>Details of each rollback attempt.</summary>
    public List<RollbackAttempt> Attempts { get; init; } = new();

    /// <summary>Total time taken for rollback.</summary>
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// Details of a single rollback attempt.
/// </summary>
public record RollbackAttempt
{
    /// <summary>Type of component (Stage or Terminal).</summary>
    public required string ComponentType { get; init; }

    /// <summary>Identifier of the component.</summary>
    public required string ComponentId { get; init; }

    /// <summary>Whether this rollback succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Error message if failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Exception if failed.</summary>
    public Exception? Exception { get; init; }

    /// <summary>Time taken for this rollback.</summary>
    public TimeSpan Duration { get; init; }
}
