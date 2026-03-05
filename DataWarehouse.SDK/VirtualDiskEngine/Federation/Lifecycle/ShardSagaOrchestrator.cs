using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Infrastructure.Distributed;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.VirtualDiskEngine.Federation.Lifecycle;

/// <summary>
/// Status of an individual step within a saga execution.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 93: Shard Lifecycle - Saga Orchestration")]
public enum SagaStepStatus : byte
{
    /// <summary>Step has not yet been executed.</summary>
    Pending = 0,

    /// <summary>Step's forward action is currently running.</summary>
    Executing = 1,

    /// <summary>Step's forward action completed successfully.</summary>
    Completed = 2,

    /// <summary>Step failed and its preceding steps need compensation.</summary>
    CompensationNeeded = 3,

    /// <summary>Step's compensation action is currently running.</summary>
    Compensating = 4,

    /// <summary>Step's compensation action completed successfully.</summary>
    Compensated = 5,

    /// <summary>Step's compensation action failed after all retries.</summary>
    CompensationFailed = 6
}

/// <summary>
/// Overall status of a saga execution.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 93: Shard Lifecycle - Saga Orchestration")]
public enum SagaStatus : byte
{
    /// <summary>Saga is executing forward steps.</summary>
    Running = 0,

    /// <summary>All forward steps completed successfully.</summary>
    Completed = 1,

    /// <summary>A step failed and compensation is in progress.</summary>
    Compensating = 2,

    /// <summary>All completed steps have been successfully compensated.</summary>
    Compensated = 3,

    /// <summary>Compensation itself failed; saga is in an inconsistent state requiring manual intervention.</summary>
    Failed = 4
}

/// <summary>
/// Represents a single step in a saga: a forward action and its compensating rollback action,
/// targeting a specific shard with a named operation.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 93: Shard Lifecycle - Saga Orchestration")]
public sealed class SagaStep
{
    /// <summary>Gets the zero-based index of this step in the saga sequence.</summary>
    public int StepIndex { get; }

    /// <summary>Gets the identifier of the target shard for this step's operations.</summary>
    public Guid TargetShardId { get; }

    /// <summary>Gets the operation key that identifies the type of write operation this step performs.</summary>
    public string OperationKey { get; }

    /// <summary>Gets or sets the current status of this step.</summary>
    public SagaStepStatus Status { get; internal set; }

    /// <summary>Gets or sets the failure reason if this step failed during execution or compensation.</summary>
    public string? FailureReason { get; internal set; }

    /// <summary>
    /// Gets the forward action delegate. Returns true on success, false on failure.
    /// Exceptions are also treated as failure.
    /// </summary>
    public Func<CancellationToken, Task<bool>> ExecuteAsync { get; }

    /// <summary>
    /// Gets the compensation (rollback) action delegate. Returns true on success, false on failure.
    /// Exceptions are also treated as failure.
    /// </summary>
    public Func<CancellationToken, Task<bool>> CompensateAsync { get; }

    /// <summary>
    /// Creates a new saga step with forward and compensation actions.
    /// </summary>
    /// <param name="stepIndex">The zero-based index of this step.</param>
    /// <param name="targetShardId">The target shard identifier.</param>
    /// <param name="operationKey">A key identifying the write operation.</param>
    /// <param name="executeAsync">The forward action delegate.</param>
    /// <param name="compensateAsync">The compensation (rollback) action delegate.</param>
    /// <exception cref="ArgumentNullException">Thrown when operationKey, executeAsync, or compensateAsync is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when stepIndex is negative.</exception>
    public SagaStep(
        int stepIndex,
        Guid targetShardId,
        string operationKey,
        Func<CancellationToken, Task<bool>> executeAsync,
        Func<CancellationToken, Task<bool>> compensateAsync)
    {
        if (stepIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(stepIndex), "Step index must be non-negative.");
        ArgumentNullException.ThrowIfNull(operationKey);
        ArgumentNullException.ThrowIfNull(executeAsync);
        ArgumentNullException.ThrowIfNull(compensateAsync);

        StepIndex = stepIndex;
        TargetShardId = targetShardId;
        OperationKey = operationKey;
        ExecuteAsync = executeAsync;
        CompensateAsync = compensateAsync;
        Status = SagaStepStatus.Pending;
    }
}

/// <summary>
/// Defines a saga: an ordered sequence of steps with compensation semantics,
/// providing eventual consistency for cross-shard operations when strict 2PC is impractical.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 93: Shard Lifecycle - Saga Orchestration")]
public sealed class SagaDefinition
{
    /// <summary>Gets the unique identifier for this saga.</summary>
    public Guid SagaId { get; }

    /// <summary>Gets the ordered list of steps in this saga.</summary>
    public IReadOnlyList<SagaStep> Steps { get; }

    /// <summary>Gets or sets the overall status of this saga.</summary>
    public SagaStatus Status { get; internal set; }

    /// <summary>Gets the maximum number of retry attempts for each compensation step.</summary>
    public int MaxCompensationRetries { get; }

    /// <summary>Gets the timeout applied to each individual step execution.</summary>
    public TimeSpan StepTimeout { get; }

    /// <summary>Gets the count of steps that have completed their forward action successfully.</summary>
    public int CompletedStepCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < Steps.Count; i++)
            {
                if (Steps[i].Status == SagaStepStatus.Completed)
                    count++;
            }
            return count;
        }
    }

    /// <summary>
    /// Gets the index of the first step that failed, or -1 if no step has failed.
    /// </summary>
    public int FailedStepIndex
    {
        get
        {
            for (int i = 0; i < Steps.Count; i++)
            {
                if (Steps[i].Status is SagaStepStatus.CompensationNeeded
                    or SagaStepStatus.CompensationFailed)
                {
                    return i;
                }
            }
            return -1;
        }
    }

    /// <summary>
    /// Gets whether this saga has reached a terminal state (Completed, Compensated, or Failed).
    /// </summary>
    public bool IsTerminal => Status is SagaStatus.Completed
        or SagaStatus.Compensated
        or SagaStatus.Failed;

    /// <summary>
    /// Creates a new saga definition.
    /// </summary>
    /// <param name="steps">The ordered list of saga steps.</param>
    /// <param name="maxCompensationRetries">Maximum retry attempts per compensation step.</param>
    /// <param name="stepTimeout">Timeout for each individual step.</param>
    /// <exception cref="ArgumentNullException">Thrown when steps is null.</exception>
    /// <exception cref="ArgumentException">Thrown when steps is empty.</exception>
    internal SagaDefinition(
        IReadOnlyList<SagaStep> steps,
        int maxCompensationRetries,
        TimeSpan stepTimeout)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0)
            throw new ArgumentException("At least one step is required.", nameof(steps));

        SagaId = Guid.NewGuid();
        Steps = steps;
        MaxCompensationRetries = maxCompensationRetries;
        StepTimeout = stepTimeout;
        Status = SagaStatus.Running;
    }
}

/// <summary>
/// Orchestrates saga-based compensating transactions across multiple shards.
/// Executes forward steps in order, and on failure compensates previously completed
/// steps in reverse order with configurable retries and exponential backoff.
/// </summary>
/// <remarks>
/// <para>
/// Use sagas when strict 2PC is impractical (e.g., long-running operations, cross-region
/// writes, or operations that cannot hold distributed locks). Sagas provide eventual
/// consistency through compensating actions rather than atomic commits.
/// </para>
/// <para>
/// Each step acquires a distributed lock scoped to the saga and step index, preventing
/// concurrent execution of the same step. Locks are released after step completion.
/// </para>
/// <para>
/// If compensation itself fails after all retries, the saga transitions to Failed status.
/// Failed sagas require manual intervention to resolve the inconsistent state.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 93: Shard Lifecycle - Saga Orchestration")]
public sealed class ShardSagaOrchestrator : IAsyncDisposable
{
    private readonly IDistributedLockService _lockService;
    private readonly FederationOptions _options;
    private readonly BoundedDictionary<Guid, SagaDefinition> _activeSagas;
    private readonly string _lockOwner;
    private volatile bool _disposed;

    /// <summary>
    /// Creates a new saga orchestrator.
    /// </summary>
    /// <param name="lockService">Distributed lock service for step-level mutual exclusion.</param>
    /// <param name="options">Federation configuration including timeouts.</param>
    /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>
    public ShardSagaOrchestrator(
        IDistributedLockService lockService,
        FederationOptions options)
    {
        ArgumentNullException.ThrowIfNull(lockService);
        ArgumentNullException.ThrowIfNull(options);

        _lockService = lockService;
        _options = options;
        _activeSagas = new BoundedDictionary<Guid, SagaDefinition>(4096);
        _lockOwner = $"saga-orch-{Environment.MachineName}";
    }

    /// <summary>
    /// Creates a new saga definition from the given steps.
    /// Validates that all steps have non-null Execute and Compensate delegates.
    /// </summary>
    /// <param name="steps">The ordered list of saga steps to execute.</param>
    /// <param name="stepTimeout">Optional per-step timeout. Defaults to ShardOperationTimeout.</param>
    /// <returns>A new saga definition ready for execution.</returns>
    /// <exception cref="ArgumentNullException">Thrown when steps is null.</exception>
    /// <exception cref="ArgumentException">Thrown when steps is empty or any step has null delegates.</exception>
    public SagaDefinition CreateSaga(
        IReadOnlyList<SagaStep> steps,
        TimeSpan? stepTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0)
            throw new ArgumentException("At least one step is required.", nameof(steps));

        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i].ExecuteAsync == null)
                throw new ArgumentException($"Step {i} has a null ExecuteAsync delegate.", nameof(steps));
            if (steps[i].CompensateAsync == null)
                throw new ArgumentException($"Step {i} has a null CompensateAsync delegate.", nameof(steps));
        }

        var effectiveTimeout = stepTimeout ?? _options.ShardOperationTimeout;
        if (effectiveTimeout <= TimeSpan.Zero)
            effectiveTimeout = TimeSpan.FromSeconds(30);

        var saga = new SagaDefinition(steps, maxCompensationRetries: 3, stepTimeout: effectiveTimeout);
        _activeSagas[saga.SagaId] = saga;
        return saga;
    }

    /// <summary>
    /// Executes a saga by running forward steps in order. On any step failure,
    /// compensates all previously completed steps in reverse order.
    /// </summary>
    /// <param name="saga">The saga definition to execute.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The saga definition with updated status after execution.</returns>
    /// <exception cref="ArgumentNullException">Thrown when saga is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when compensation fails after all retries.</exception>
    public async Task<SagaDefinition> ExecuteSagaAsync(SagaDefinition saga, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(saga);
        ObjectDisposedException.ThrowIf(_disposed, this);

        for (int i = 0; i < saga.Steps.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var step = saga.Steps[i];

            // Transition to Executing
            step.Status = SagaStepStatus.Executing;

            var lockId = $"saga:{saga.SagaId}:step:{step.StepIndex}";
            DistributedLock? acquiredLock = null;

            try
            {
                // Acquire lock for this step
                acquiredLock = await _lockService.TryAcquireAsync(
                    lockId,
                    _lockOwner,
                    saga.StepTimeout,
                    ct).ConfigureAwait(false);

                // Execute forward action with step timeout
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(saga.StepTimeout);

                bool success;
                try
                {
                    success = await step.ExecuteAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Step timed out (not caller cancellation)
                    success = false;
                    step.FailureReason = "Step execution timed out";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    success = false;
                    step.FailureReason = $"Step execution failed: {ex.Message}";
                }

                if (success)
                {
                    step.Status = SagaStepStatus.Completed;
                }
                else
                {
                    step.FailureReason ??= "Step returned false";
                    step.Status = SagaStepStatus.CompensationNeeded;
                    saga.Status = SagaStatus.Compensating;

                    // Compensate all previously completed steps in reverse order
                    await CompensateFromStepAsync(saga, i - 1, ct).ConfigureAwait(false);
                    return saga;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            finally
            {
                // Release step lock
                if (acquiredLock != null)
                {
                    try
                    {
                        await _lockService.ReleaseAsync(lockId, _lockOwner, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        // Lock will expire via lease timeout
                    }
                }
            }
        }

        // All steps completed successfully
        saga.Status = SagaStatus.Completed;
        return saga;
    }

    /// <summary>
    /// Gets a saga by its ID from the active saga store.
    /// </summary>
    /// <param name="sagaId">The saga ID to look up.</param>
    /// <returns>The saga definition if found; null otherwise.</returns>
    public SagaDefinition? GetSaga(Guid sagaId)
    {
        return _activeSagas.TryPeek(sagaId, out var saga) ? saga : null;
    }

    /// <summary>
    /// Returns all active (non-terminal) sagas.
    /// </summary>
    /// <returns>A read-only list of non-terminal sagas.</returns>
    public IReadOnlyList<SagaDefinition> GetActiveSagas()
    {
        var result = new List<SagaDefinition>();
        foreach (var kvp in _activeSagas)
        {
            if (!kvp.Value.IsTerminal)
            {
                result.Add(kvp.Value);
            }
        }
        return result;
    }

    /// <summary>
    /// Disposes the orchestrator by best-effort compensating all running sagas.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Best-effort compensate all running sagas
        var runningIds = new List<Guid>();
        foreach (var kvp in _activeSagas)
        {
            if (kvp.Value.Status == SagaStatus.Running)
            {
                runningIds.Add(kvp.Key);
            }
        }

        for (int i = 0; i < runningIds.Count; i++)
        {
            try
            {
                if (_activeSagas.TryPeek(runningIds[i], out var saga))
                {
                    saga.Status = SagaStatus.Compensating;
                    int lastCompletedIndex = -1;
                    for (int j = saga.Steps.Count - 1; j >= 0; j--)
                    {
                        if (saga.Steps[j].Status == SagaStepStatus.Completed)
                        {
                            lastCompletedIndex = j;
                            break;
                        }
                    }
                    if (lastCompletedIndex >= 0)
                    {
                        await CompensateFromStepAsync(saga, lastCompletedIndex, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                }
            }
            catch
            {
                // Best-effort cleanup during disposal
            }
        }

        await _activeSagas.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Compensates completed steps in reverse order from the given starting index down to 0.
    /// Uses exponential backoff retries for failed compensations.
    /// </summary>
    /// <param name="saga">The saga whose steps need compensation.</param>
    /// <param name="fromStepIndex">The highest index to start compensating from (inclusive).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a compensation step fails after all retries, leaving the saga in a Failed state.
    /// </exception>
    private async Task CompensateFromStepAsync(
        SagaDefinition saga,
        int fromStepIndex,
        CancellationToken ct)
    {
        for (int i = fromStepIndex; i >= 0; i--)
        {
            ct.ThrowIfCancellationRequested();
            var step = saga.Steps[i];

            // Only compensate steps that were completed
            if (step.Status != SagaStepStatus.Completed)
                continue;

            step.Status = SagaStepStatus.Compensating;

            bool compensated = false;
            for (int attempt = 0; attempt <= saga.MaxCompensationRetries; attempt++)
            {
                try
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(saga.StepTimeout);

                    bool success = await step.CompensateAsync(timeoutCts.Token).ConfigureAwait(false);
                    if (success)
                    {
                        step.Status = SagaStepStatus.Compensated;
                        compensated = true;
                        break;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // Retry on next attempt
                }

                // Exponential backoff: 100ms * 2^attempt, max 5 seconds
                if (attempt < saga.MaxCompensationRetries)
                {
                    int delayMs = Math.Min(100 * (1 << attempt), 5000);
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                }
            }

            if (!compensated)
            {
                step.Status = SagaStepStatus.CompensationFailed;
                step.FailureReason = $"Compensation failed after {saga.MaxCompensationRetries + 1} attempts";
                saga.Status = SagaStatus.Failed;
                throw new InvalidOperationException(
                    $"Saga {saga.SagaId} compensation failed at step {i}: {step.OperationKey}");
            }
        }

        // All compensations succeeded
        if (saga.Status == SagaStatus.Compensating)
        {
            saga.Status = SagaStatus.Compensated;
        }
    }
}
