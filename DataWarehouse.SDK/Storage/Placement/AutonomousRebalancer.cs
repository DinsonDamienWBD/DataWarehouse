using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Storage.Migration;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Storage.Placement;

/// <summary>
/// Autonomous rebalancer that continuously monitors cluster balance and triggers
/// gravity-aware data migrations. Combines CRUSH algorithm (for ideal placement),
/// gravity optimizer (for movement cost scoring), and migration engine (for actual data transfer).
/// </summary>
/// <remarks>
/// <para>
/// The rebalancer operates as a continuous background service with a timer-based monitor loop.
/// Each cycle computes the cluster imbalance ratio and, if above the configured threshold,
/// generates a rebalance plan using the gravity optimizer and executes it via the migration engine.
/// </para>
/// <para>
/// Key behaviors:
/// - Quiet hours support to avoid rebalancing during peak traffic windows
/// - Gravity protection threshold prevents moving high-gravity (strongly-bound) objects
/// - Cost and egress budgets prevent runaway spending per cycle
/// - Pause/resume/cancel lifecycle for all rebalance jobs
/// - Concurrent migration limit to bound cluster I/O impact
/// </para>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Autonomous rebalancer")]
public sealed class AutonomousRebalancer : IRebalancer, IAsyncDisposable
{
    private readonly IPlacementAlgorithm _crushAlgorithm;
    private readonly IPlacementOptimizer _optimizer;
    private readonly IMigrationEngine _migrationEngine;
    private readonly RebalancerOptions _options;
    private readonly BoundedDictionary<string, RebalanceJob> _jobs = new BoundedDictionary<string, RebalanceJob>(1000);
    private readonly Timer _monitorTimer;
    private bool _disposed;
    private int _isRunning; // 0 = idle, 1 = running (interlocked)

    /// <summary>
    /// Delegate to provide the current cluster map for imbalance calculation.
    /// Must be set by the host before monitoring will trigger rebalance cycles.
    /// </summary>
    public Func<CancellationToken, Task<IReadOnlyList<NodeDescriptor>>>? ClusterMapProvider { get; set; }

    /// <summary>
    /// Delegate to enumerate object keys on a given node. Used for per-node gravity analysis.
    /// Must be set by the host before monitoring will trigger rebalance cycles.
    /// </summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<string>>>? ObjectEnumeratorByNode { get; set; }

    /// <summary>
    /// Creates a new autonomous rebalancer with the specified placement subsystem components.
    /// </summary>
    /// <param name="crushAlgorithm">The CRUSH placement algorithm for computing ideal placement.</param>
    /// <param name="optimizer">The gravity-aware placement optimizer for generating rebalance plans.</param>
    /// <param name="migrationEngine">The migration engine for executing data moves.</param>
    /// <param name="options">Rebalancer configuration. Uses <see cref="RebalancerOptions.Default"/> if null.</param>
    public AutonomousRebalancer(
        IPlacementAlgorithm crushAlgorithm,
        IPlacementOptimizer optimizer,
        IMigrationEngine migrationEngine,
        RebalancerOptions? options = null)
    {
        _crushAlgorithm = crushAlgorithm ?? throw new ArgumentNullException(nameof(crushAlgorithm));
        _optimizer = optimizer ?? throw new ArgumentNullException(nameof(optimizer));
        _migrationEngine = migrationEngine ?? throw new ArgumentNullException(nameof(migrationEngine));
        _options = options ?? RebalancerOptions.Default;

        _monitorTimer = new Timer(
            async _ => { try { await CheckAndRebalanceAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Timer callback failed: {ex.Message}"); } },
            null,
            _options.CheckInterval,
            _options.CheckInterval);
    }

    /// <inheritdoc />
    public async Task<RebalanceJob> StartRebalanceAsync(RebalancePlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var jobId = $"rebal-{Guid.NewGuid():N}"[..16];
        var now = DateTimeOffset.UtcNow;
        var job = new RebalanceJob(
            JobId: jobId,
            Plan: plan,
            Status: RebalanceStatus.Running,
            CreatedUtc: now,
            StartedUtc: now,
            CompletedUtc: null,
            TotalMoves: plan.Moves.Count,
            CompletedMoves: 0,
            FailedMoves: 0);

        _jobs[jobId] = job;

        // Execute moves via migration engine in the background
        _ = Task.Run(async () => await ExecuteRebalanceAsync(jobId, plan, ct).ConfigureAwait(false), ct)
            .ContinueWith(t => System.Diagnostics.Debug.WriteLine(
                $"[AutonomousRebalancer] Rebalance job {jobId} failed: {t.Exception?.InnerException?.Message}"),
                TaskContinuationOptions.OnlyOnFaulted);

        return job;
    }

    /// <inheritdoc />
    public Task PauseRebalanceAsync(string jobId, CancellationToken ct = default)
    {
        if (_jobs.TryGetValue(jobId, out var job) && job.Status == RebalanceStatus.Running)
        {
            _jobs[jobId] = job with { Status = RebalanceStatus.Paused };
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ResumeRebalanceAsync(string jobId, CancellationToken ct = default)
    {
        if (_jobs.TryGetValue(jobId, out var job) && job.Status == RebalanceStatus.Paused)
        {
            _jobs[jobId] = job with { Status = RebalanceStatus.Running };
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CancelRebalanceAsync(string jobId, CancellationToken ct = default)
    {
        if (_jobs.TryGetValue(jobId, out var job) &&
            job.Status is RebalanceStatus.Running or RebalanceStatus.Paused)
        {
            _jobs[jobId] = job with { Status = RebalanceStatus.Cancelled, CompletedUtc = DateTimeOffset.UtcNow };
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<RebalanceJob> GetStatusAsync(string jobId, CancellationToken ct = default)
    {
        if (_jobs.TryGetValue(jobId, out var job))
            return Task.FromResult(job);

        throw new KeyNotFoundException($"Rebalance job '{jobId}' not found.");
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RebalanceJob>> ListJobsAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<RebalanceJob>>(_jobs.Values.ToList());
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RebalanceJob> MonitorAsync(
        string jobId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!_jobs.TryGetValue(jobId, out var job))
                yield break;

            yield return job;

            if (job.Status is RebalanceStatus.Completed or RebalanceStatus.Failed or RebalanceStatus.Cancelled)
                yield break;

            await Task.Delay(1000, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            await _monitorTimer.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes all moves in a rebalance plan by delegating to the migration engine.
    /// Groups moves by source-target pair for batch migration and respects concurrency limits.
    /// </summary>
    private async Task ExecuteRebalanceAsync(string jobId, RebalancePlan plan, CancellationToken ct)
    {
        int completed = 0;
        int failed = 0;

        try
        {
            // Group moves by source->target pair for batch migration
            var moveGroups = plan.Moves
                .GroupBy(m => (m.SourceNode, m.TargetNode))
                .ToList();

            var activeMigrations = new List<Task<(int Completed, int Failed)>>();

            foreach (var group in moveGroups)
            {
                // Respect max concurrent migrations by waiting for a slot
                while (activeMigrations.Count(t => !t.IsCompleted) >= _options.MaxConcurrentMigrations)
                {
                    await Task.WhenAny(activeMigrations.Where(t => !t.IsCompleted)).ConfigureAwait(false);
                }

                ct.ThrowIfCancellationRequested();

                // Check for pause state - wait until resumed or cancelled
                if (_jobs.TryGetValue(jobId, out var current) && current.Status == RebalanceStatus.Paused)
                {
                    var paused = current;
                    while (paused.Status == RebalanceStatus.Paused && !ct.IsCancellationRequested)
                    {
                        await Task.Delay(500, ct).ConfigureAwait(false);
                        if (_jobs.TryGetValue(jobId, out var updated))
                            paused = updated;
                        else
                            break;
                    }

                    // If cancelled during pause
                    if (paused.Status is RebalanceStatus.Cancelled or RebalanceStatus.Failed)
                        return;
                }

                // Check if job was cancelled
                if (_jobs.TryGetValue(jobId, out var jobState) &&
                    jobState.Status is RebalanceStatus.Cancelled or RebalanceStatus.Failed)
                {
                    return;
                }

                var objects = group.Select(m => new MigrationObject(
                    ObjectKey: m.ObjectKey,
                    SizeBytes: m.SizeBytes,
                    SourceLocation: m.SourceNode,
                    TargetLocation: m.TargetNode,
                    Priority: m.Priority)).ToList();

                var migrationPlan = new MigrationPlan(
                    SourceNode: group.Key.SourceNode,
                    TargetNode: group.Key.TargetNode,
                    Objects: objects,
                    ThrottleBytesPerSec: _options.ThrottleBytesPerSec,
                    EnableReadForwarding: _options.EnableReadForwarding,
                    ZeroDowntime: true,
                    ValidateChecksums: _options.ValidateChecksums);

                var groupCount = group.Count();
                var migrationTask = ExecuteSingleMigrationAsync(
                    jobId, migrationPlan, groupCount, ct);

                activeMigrations.Add(migrationTask);
            }

            var results = await Task.WhenAll(activeMigrations).ConfigureAwait(false);
            foreach (var (c, f) in results)
            {
                completed += c;
                failed += f;
            }

            _jobs[jobId] = _jobs[jobId] with
            {
                Status = RebalanceStatus.Completed,
                CompletedUtc = DateTimeOffset.UtcNow,
                CompletedMoves = completed,
                FailedMoves = failed
            };
        }
        catch (OperationCanceledException)
        {
            if (_jobs.TryGetValue(jobId, out var cancelledJob))
            {
                _jobs[jobId] = cancelledJob with
                {
                    Status = RebalanceStatus.Cancelled,
                    CompletedUtc = DateTimeOffset.UtcNow,
                    CompletedMoves = completed,
                    FailedMoves = failed
                };
            }
        }
        catch (Exception)
        {
            if (_jobs.TryGetValue(jobId, out var failedJob))
            {
                _jobs[jobId] = failedJob with
                {
                    Status = RebalanceStatus.Failed,
                    CompletedUtc = DateTimeOffset.UtcNow,
                    CompletedMoves = completed,
                    FailedMoves = failed
                };
            }
        }
    }

    /// <summary>
    /// Executes a single migration batch and monitors it to completion.
    /// Returns the number of completed and failed moves for aggregation.
    /// </summary>
    private async Task<(int Completed, int Failed)> ExecuteSingleMigrationAsync(
        string rebalanceJobId,
        MigrationPlan migrationPlan,
        int moveCount,
        CancellationToken ct)
    {
        int batchCompleted = 0;
        int batchFailed = 0;

        try
        {
            var migJob = await _migrationEngine.StartMigrationAsync(migrationPlan, ct).ConfigureAwait(false);

            // Monitor until complete
            await foreach (var status in _migrationEngine.MonitorAsync(migJob.JobId, ct).ConfigureAwait(false))
            {
                if (status.Status == MigrationStatus.Completed)
                {
                    batchCompleted = (int)status.MigratedObjects;
                    break;
                }

                if (status.Status == MigrationStatus.Failed)
                {
                    batchFailed = (int)status.FailedObjects;
                    break;
                }
            }
        }
        catch (Exception)
        {
            batchFailed = moveCount;
        }

        // Update the rebalance job with intermediate progress
        if (_jobs.TryGetValue(rebalanceJobId, out var current))
        {
            _jobs[rebalanceJobId] = current with
            {
                CompletedMoves = current.CompletedMoves + batchCompleted,
                FailedMoves = current.FailedMoves + batchFailed
            };
        }

        return (batchCompleted, batchFailed);
    }

    /// <summary>
    /// Timer callback that checks cluster imbalance and triggers rebalancing if needed.
    /// Uses interlocked compare-exchange to prevent overlapping runs.
    /// </summary>
    private async Task CheckAndRebalanceAsync(CancellationToken ct)
    {
        // Prevent overlapping runs
        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
            return;

        try
        {
            // Quiet hours check
            if (_options.QuietHours.HasValue)
            {
                int currentHour = DateTime.UtcNow.Hour;
                var (start, end) = _options.QuietHours.Value;
                bool inQuietHours = start <= end
                    ? (currentHour >= start && currentHour < end)
                    : (currentHour >= start || currentHour < end);

                if (inQuietHours)
                    return;
            }

            // Providers must be set by host
            if (ClusterMapProvider == null || ObjectEnumeratorByNode == null)
                return;

            var clusterMap = await ClusterMapProvider(ct).ConfigureAwait(false);
            if (clusterMap.Count < 2) return;

            // Calculate imbalance as the spread of usage ratios
            double totalCapacity = clusterMap.Sum(n => (double)n.CapacityBytes);
            if (totalCapacity <= 0) return;

            double maxUsageRatio = clusterMap.Max(n =>
                n.CapacityBytes > 0 ? (double)n.UsedBytes / n.CapacityBytes : 0);
            double minUsageRatio = clusterMap.Min(n =>
                n.CapacityBytes > 0 ? (double)n.UsedBytes / n.CapacityBytes : 0);
            double imbalance = maxUsageRatio - minUsageRatio;

            if (imbalance < _options.ImbalanceThreshold)
                return; // Cluster is balanced enough

            // Generate gravity-aware rebalance plan
            var rebalanceOptions = new RebalanceOptions(
                MaxMoves: _options.MaxMovesPerCycle,
                MaxEgressBytes: _options.MaxEgressBytesPerCycle,
                MaxCostBudget: _options.MaxCostPerCycle,
                MinGravityThreshold: _options.GravityProtectionThreshold,
                DryRun: false);

            var plan = await _optimizer.GenerateRebalancePlanAsync(clusterMap, rebalanceOptions, ct)
                .ConfigureAwait(false);

            if (plan.Moves.Count > 0)
            {
                await StartRebalanceAsync(plan, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _isRunning, 0);
        }
    }
}
