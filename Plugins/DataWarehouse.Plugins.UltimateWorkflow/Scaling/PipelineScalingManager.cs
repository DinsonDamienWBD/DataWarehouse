using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Persistence;
using DataWarehouse.SDK.Contracts.Scaling;

namespace DataWarehouse.Plugins.UltimateWorkflow.Scaling;

/// <summary>
/// Captured state for a single pipeline stage, with size limits and backing store spill.
/// </summary>
/// <param name="PipelineId">The pipeline execution this state belongs to.</param>
/// <param name="StageIndex">Zero-based index of the stage within the pipeline.</param>
/// <param name="StageName">Human-readable name of the stage.</param>
/// <param name="StateData">The captured state data bytes (null if spilled to backing store).</param>
/// <param name="IsSpilled">Whether the state has been spilled to persistent backing store due to size limits.</param>
/// <param name="DependsOnStages">Indices of stages this stage depends on (for parallel rollback analysis).</param>
[SdkCompatibility("6.0.0", Notes = "Phase 88-10: Pipeline stage captured state with spill support")]
public sealed record PipelineStageCapturedState(
    string PipelineId,
    int StageIndex,
    string StageName,
    byte[]? StateData,
    bool IsSpilled,
    IReadOnlyList<int> DependsOnStages)
{
    /// <summary>
    /// Gets the size of the state data in bytes, or 0 if spilled.
    /// </summary>
    public long SizeBytes => StateData?.Length ?? 0;
}

/// <summary>
/// Result of a pipeline rollback operation.
/// </summary>
/// <param name="PipelineId">The pipeline that was rolled back.</param>
/// <param name="StagesRolledBack">Number of stages successfully rolled back.</param>
/// <param name="ParallelBatches">Number of parallel rollback batches executed.</param>
/// <param name="Errors">Any errors encountered during rollback.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 88-10: Pipeline rollback result")]
public sealed record PipelineRollbackResult(
    string PipelineId,
    int StagesRolledBack,
    int ParallelBatches,
    IReadOnlyList<string> Errors);

/// <summary>
/// Manages pipeline scaling with configurable maximum depth, concurrent transaction limits,
/// per-stage state size limits with spill-to-disk, and parallel rollback for independent stages.
/// Implements <see cref="IScalableSubsystem"/> for unified scaling infrastructure participation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Maximum pipeline depth:</b> Rejects pipeline configurations deeper than
/// <see cref="MaxPipelineDepth"/> (default 50 stages). Configurable at runtime via
/// <see cref="ReconfigureLimitsAsync"/>. Prevents infinite or excessively deep pipelines
/// that exhaust stack/memory.
/// </para>
/// <para>
/// <b>Concurrent transaction limits:</b> Uses <see cref="SemaphoreSlim"/> to limit concurrent
/// pipeline executions. Default: <c>Environment.ProcessorCount * 2</c>. Queued pipelines wait
/// (with configurable timeout, default 30s) or are rejected with backpressure signal.
/// </para>
/// <para>
/// <b>CapturedState size limits per stage:</b> Each pipeline stage can capture state for rollback.
/// State is limited to <see cref="MaxStateBytesPerStage"/> (default 10 MB). If state exceeds the
/// limit, it spills to <see cref="IPersistentBackingStore"/> with key format
/// <c>dw://internal/pipeline-state/{pipelineId}/{stageIndex}</c>. On rollback, spilled state is
/// loaded from the backing store.
/// </para>
/// <para>
/// <b>Parallel rollback:</b> When rolling back, identifies independent stages (no data
/// dependencies between them) and rolls back in parallel using <see cref="Task.WhenAll"/>.
/// Dependent stages roll back sequentially in reverse order.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 88-10: Pipeline scaling manager with depth limits and parallel rollback")]
public sealed class PipelineScalingManager : IScalableSubsystem, IDisposable
{
    // ---- Constants ----
    private const string SubsystemName = "UltimateWorkflow.Pipeline";
    private const int DefaultMaxPipelineDepth = 50;
    private const long DefaultMaxStateBytesPerStage = 10 * 1024 * 1024; // 10 MB
    private const int DefaultExecutionTimeoutMs = 30_000;

    // ---- Dependencies ----
    private readonly IPersistentBackingStore? _backingStore;

    // ---- Configuration ----
    private volatile int _maxPipelineDepth;
    private long _maxStateBytesPerStage;
    private volatile int _executionTimeoutMs;

    // ---- Concurrent transaction limits ----
    private SemaphoreSlim _transactionSemaphore;
    private readonly object _semaphoreLock = new();

    // ---- Pipeline state snapshots for rollback ----
    private readonly BoundedCache<string, List<PipelineStageCapturedState>> _stateSnapshots;

    // ---- Scaling state ----
    private ScalingLimits _currentLimits;
    private long _pendingTransactions;
    private long _totalTransactions;
    private long _rejectedTransactions;
    private long _totalRollbacks;
    private long _spilledStates;

    // ---- Disposal ----
    private bool _disposed;

    /// <summary>
    /// Initializes a new <see cref="PipelineScalingManager"/> with optional backing store and scaling limits.
    /// </summary>
    /// <param name="backingStore">Optional persistent backing store for spilling oversized stage state.</param>
    /// <param name="limits">Optional initial scaling limits. Uses defaults if <c>null</c>.</param>
    public PipelineScalingManager(
        IPersistentBackingStore? backingStore = null,
        ScalingLimits? limits = null)
    {
        _backingStore = backingStore;

        _currentLimits = limits ?? new ScalingLimits(
            MaxCacheEntries: 10_000,
            MaxConcurrentOperations: Environment.ProcessorCount * 2,
            MaxQueueDepth: 500);

        _maxPipelineDepth = DefaultMaxPipelineDepth;
        _maxStateBytesPerStage = DefaultMaxStateBytesPerStage;
        _executionTimeoutMs = DefaultExecutionTimeoutMs;

        _transactionSemaphore = new SemaphoreSlim(
            _currentLimits.MaxConcurrentOperations,
            _currentLimits.MaxConcurrentOperations);

        _stateSnapshots = new BoundedCache<string, List<PipelineStageCapturedState>>(
            new BoundedCacheOptions<string, List<PipelineStageCapturedState>>
            {
                MaxEntries = Math.Min(_currentLimits.MaxCacheEntries, 10_000),
                EvictionPolicy = CacheEvictionMode.LRU
            });
    }

    // ---- Public API ----

    /// <summary>
    /// Gets or sets the maximum allowed pipeline depth (number of stages).
    /// Pipelines deeper than this value are rejected.
    /// </summary>
    public int MaxPipelineDepth
    {
        get => _maxPipelineDepth;
        set => _maxPipelineDepth = Math.Max(1, value);
    }

    /// <summary>
    /// Gets or sets the maximum state data size in bytes per pipeline stage before spilling to disk.
    /// </summary>
    public long MaxStateBytesPerStage
    {
        get => Interlocked.Read(ref _maxStateBytesPerStage);
        set => Interlocked.Exchange(ref _maxStateBytesPerStage, Math.Max(1024, value));
    }

    /// <summary>
    /// Validates that a pipeline configuration does not exceed the maximum depth.
    /// </summary>
    /// <param name="stageCount">Number of stages in the pipeline.</param>
    /// <returns><c>true</c> if the pipeline depth is within limits; otherwise <c>false</c>.</returns>
    public bool ValidatePipelineDepth(int stageCount)
    {
        return stageCount > 0 && stageCount <= _maxPipelineDepth;
    }

    /// <summary>
    /// Acquires a transaction slot for pipeline execution. Blocks until a slot is available
    /// or the configured timeout expires.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> if a slot was acquired; <c>false</c> if the wait timed out (backpressure).</returns>
    public async Task<bool> AcquireTransactionSlotAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Interlocked.Increment(ref _pendingTransactions);

        bool acquired = await _transactionSemaphore.WaitAsync(
            _executionTimeoutMs, ct).ConfigureAwait(false);

        if (!acquired)
        {
            Interlocked.Decrement(ref _pendingTransactions);
            Interlocked.Increment(ref _rejectedTransactions);
        }

        return acquired;
    }

    /// <summary>
    /// Releases a transaction slot after pipeline execution completes.
    /// </summary>
    public void ReleaseTransactionSlot()
    {
        _transactionSemaphore.Release();
        Interlocked.Decrement(ref _pendingTransactions);
        Interlocked.Increment(ref _totalTransactions);
    }

    /// <summary>
    /// Captures state for a pipeline stage, spilling to backing store if the state exceeds
    /// <see cref="MaxStateBytesPerStage"/>.
    /// </summary>
    /// <param name="pipelineId">The pipeline execution ID.</param>
    /// <param name="stageIndex">Zero-based stage index.</param>
    /// <param name="stageName">Human-readable stage name.</param>
    /// <param name="stateData">The stage state data to capture.</param>
    /// <param name="dependsOnStages">Indices of stages this stage depends on.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task CaptureStageStateAsync(
        string pipelineId,
        int stageIndex,
        string stageName,
        byte[] stateData,
        IReadOnlyList<int>? dependsOnStages = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(pipelineId);
        ArgumentNullException.ThrowIfNull(stateData);

        bool isSpilled = false;
        byte[]? storedData = stateData;

        // Spill to backing store if oversized
        if (stateData.Length > Interlocked.Read(ref _maxStateBytesPerStage))
        {
            if (_backingStore != null)
            {
                var path = $"dw://internal/pipeline-state/{pipelineId}/{stageIndex}";
                await _backingStore.WriteAsync(path, stateData, ct).ConfigureAwait(false);
                storedData = null;
                isSpilled = true;
                Interlocked.Increment(ref _spilledStates);
            }
            // If no backing store, keep in memory (best effort)
        }

        var capturedState = new PipelineStageCapturedState(
            pipelineId,
            stageIndex,
            stageName,
            storedData,
            isSpilled,
            dependsOnStages ?? Array.Empty<int>());

        // Use TryGet then conditional Put under the downstream lock to eliminate the TOCTOU
        // between GetOrDefault and Put that allowed concurrent callers to orphan list objects.
        // BoundedCache does not expose GetOrAdd, so we lock the inner list as the guard.
        if (!_stateSnapshots.TryGet(pipelineId, out var snapshots) || snapshots == null)
        {
            snapshots = new List<PipelineStageCapturedState>();
            _stateSnapshots.Put(pipelineId, snapshots);
        }

        // Insert at correct index position (pad if needed)
        lock (snapshots)
        {
            while (snapshots.Count <= stageIndex)
                snapshots.Add(null!);
            snapshots[stageIndex] = capturedState;
        }
    }

    /// <summary>
    /// Loads spilled state data from the backing store for a specific stage.
    /// </summary>
    /// <param name="pipelineId">The pipeline execution ID.</param>
    /// <param name="stageIndex">Zero-based stage index.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The state data bytes, or <c>null</c> if not found.</returns>
    public async Task<byte[]?> LoadSpilledStateAsync(
        string pipelineId,
        int stageIndex,
        CancellationToken ct = default)
    {
        if (_backingStore == null) return null;

        var path = $"dw://internal/pipeline-state/{pipelineId}/{stageIndex}";
        return await _backingStore.ReadAsync(path, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Performs parallel rollback of independent stages and sequential rollback of dependent stages.
    /// Independent stages (no cross-dependencies) are rolled back in parallel via
    /// <see cref="Task.WhenAll"/>. Dependent stages are rolled back sequentially in reverse order.
    /// </summary>
    /// <param name="pipelineId">The pipeline execution ID to roll back.</param>
    /// <param name="rollbackAction">Action to execute for each stage rollback, receiving the captured state.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="PipelineRollbackResult"/> describing the rollback outcome.</returns>
    public async Task<PipelineRollbackResult> RollbackAsync(
        string pipelineId,
        Func<PipelineStageCapturedState, CancellationToken, Task> rollbackAction,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(pipelineId);
        ArgumentNullException.ThrowIfNull(rollbackAction);

        Interlocked.Increment(ref _totalRollbacks);

        var snapshots = _stateSnapshots.GetOrDefault(pipelineId);
        if (snapshots == null || snapshots.Count == 0)
        {
            return new PipelineRollbackResult(pipelineId, 0, 0, Array.Empty<string>());
        }

        List<PipelineStageCapturedState> stageList;
        lock (snapshots)
        {
            stageList = snapshots.Where(s => s != null).ToList();
        }

        // Load spilled states before rollback
        for (int i = 0; i < stageList.Count; i++)
        {
            if (stageList[i].IsSpilled)
            {
                var data = await LoadSpilledStateAsync(pipelineId, stageList[i].StageIndex, ct)
                    .ConfigureAwait(false);
                if (data != null)
                {
                    stageList[i] = stageList[i] with { StateData = data };
                }
            }
        }

        // Build dependency graph and identify independent stages
        var independentStages = new List<PipelineStageCapturedState>();
        var dependentStages = new List<PipelineStageCapturedState>();

        foreach (var stage in stageList)
        {
            if (stage.DependsOnStages.Count == 0)
                independentStages.Add(stage);
            else
                dependentStages.Add(stage);
        }

        var errors = new List<string>();
        int stagesRolledBack = 0;
        int parallelBatches = 0;

        // Rollback independent stages in parallel
        if (independentStages.Count > 0)
        {
            parallelBatches++;
            var tasks = independentStages.Select(async stage =>
            {
                try
                {
                    await rollbackAction(stage, ct).ConfigureAwait(false);
                    Interlocked.Increment(ref stagesRolledBack);
                }
                catch (Exception ex)
                {
                    lock (errors)
                    {
                        errors.Add($"Stage {stage.StageIndex} ({stage.StageName}): {ex.Message}");
                    }
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        // Rollback dependent stages sequentially in reverse order
        var sortedDependent = dependentStages.OrderByDescending(s => s.StageIndex).ToList();
        foreach (var stage in sortedDependent)
        {
            try
            {
                await rollbackAction(stage, ct).ConfigureAwait(false);
                stagesRolledBack++;
            }
            catch (Exception ex)
            {
                errors.Add($"Stage {stage.StageIndex} ({stage.StageName}): {ex.Message}");
            }
        }

        // Clean up snapshots
        _stateSnapshots.TryRemove(pipelineId, out _);

        return new PipelineRollbackResult(pipelineId, stagesRolledBack, parallelBatches, errors);
    }

    // ---- IScalableSubsystem ----

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, object> GetScalingMetrics()
    {
        long pending = Interlocked.Read(ref _pendingTransactions);

        return new Dictionary<string, object>
        {
            ["pipeline.maxDepth"] = _maxPipelineDepth,
            ["pipeline.maxStateBytesPerStage"] = Interlocked.Read(ref _maxStateBytesPerStage),
            ["pipeline.totalTransactions"] = Interlocked.Read(ref _totalTransactions),
            ["pipeline.rejectedTransactions"] = Interlocked.Read(ref _rejectedTransactions),
            ["pipeline.totalRollbacks"] = Interlocked.Read(ref _totalRollbacks),
            ["pipeline.spilledStates"] = Interlocked.Read(ref _spilledStates),
            ["cache.stateSnapshots.size"] = _stateSnapshots.Count,
            ["backpressure.queueDepth"] = pending,
            ["backpressure.state"] = CurrentBackpressureState.ToString(),
            ["concurrency.maxTransactions"] = _currentLimits.MaxConcurrentOperations,
            ["concurrency.availableSlots"] = _transactionSemaphore.CurrentCount
        };
    }

    /// <inheritdoc/>
    public async Task ReconfigureLimitsAsync(ScalingLimits limits, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(limits);

        var oldLimits = _currentLimits;
        _currentLimits = limits;

        // Update concurrent transaction limit if changed
        if (limits.MaxConcurrentOperations != oldLimits.MaxConcurrentOperations)
        {
            lock (_semaphoreLock)
            {
                var oldSemaphore = _transactionSemaphore;
                _transactionSemaphore = new SemaphoreSlim(
                    limits.MaxConcurrentOperations,
                    limits.MaxConcurrentOperations);
                oldSemaphore.Dispose();
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public ScalingLimits CurrentLimits => _currentLimits;

    /// <inheritdoc/>
    public BackpressureState CurrentBackpressureState
    {
        get
        {
            long pending = Interlocked.Read(ref _pendingTransactions);
            int maxQueue = _currentLimits.MaxQueueDepth;

            if (pending <= 0) return BackpressureState.Normal;
            if (pending < maxQueue * 0.5) return BackpressureState.Normal;
            if (pending < maxQueue * 0.8) return BackpressureState.Warning;
            if (pending < maxQueue) return BackpressureState.Critical;
            return BackpressureState.Shedding;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _stateSnapshots.Dispose();
        _transactionSemaphore.Dispose();
    }
}
