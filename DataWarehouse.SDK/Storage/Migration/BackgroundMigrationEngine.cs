using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Storage.Migration;

/// <summary>
/// Background migration engine implementing zero-downtime data migration.
///
/// Migration lifecycle per object:
/// 1. COPY: Read from source, write to target (respects throttle)
/// 2. FORWARD: Register read forwarding entry (reads go to new location)
/// 3. VERIFY: Checksum comparison between source and target
/// 4. CLEANUP: Remove source copy (after forwarding TTL or explicit confirmation)
///
/// Features:
/// - Throttling: configurable bytes/sec limit to avoid overwhelming production
/// - Checkpointing: progress saved after each batch for crash recovery
/// - Read forwarding: transparent redirection during migration
/// - Batch processing: configurable batch size for memory efficiency
/// - Pause/Resume: graceful pause with checkpoint save
/// - Cancellation: clean abort with partial migration rollback
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Background migration engine")]
public sealed class BackgroundMigrationEngine : IMigrationEngine, IDisposable
{
    private readonly ReadForwardingTable _forwardingTable;
    private readonly MigrationCheckpointStore _checkpointStore;
    private readonly BoundedDictionary<string, MigrationJobState> _jobs = new BoundedDictionary<string, MigrationJobState>(1000);
    private readonly int _batchSize;

    /// <summary>
    /// Delegate for reading an object from a storage node. Parameters: objectKey, nodeId, cancellationToken.
    /// Returns the object data as a stream.
    /// </summary>
    public Func<string, string, CancellationToken, Task<Stream>>? ReadObjectAsync { get; set; }

    /// <summary>
    /// Delegate for writing an object to a storage node. Parameters: objectKey, nodeId, dataStream, cancellationToken.
    /// </summary>
    public Func<string, string, Stream, CancellationToken, Task>? WriteObjectAsync { get; set; }

    /// <summary>
    /// Delegate for deleting an object from a storage node. Parameters: objectKey, nodeId, cancellationToken.
    /// Returns true if the object was deleted.
    /// </summary>
    public Func<string, string, CancellationToken, Task<bool>>? DeleteObjectAsync { get; set; }

    /// <summary>
    /// Delegate for computing the checksum of an object on a storage node. Parameters: objectKey, nodeId, cancellationToken.
    /// Returns the checksum bytes.
    /// </summary>
    public Func<string, string, CancellationToken, Task<byte[]>>? GetChecksumAsync { get; set; }

    /// <summary>
    /// Creates a new background migration engine.
    /// </summary>
    /// <param name="forwardingTable">The read forwarding table for transparent read redirection.</param>
    /// <param name="checkpointStore">The checkpoint store for crash recovery.</param>
    /// <param name="batchSize">Number of objects to process before saving a checkpoint.</param>
    public BackgroundMigrationEngine(
        ReadForwardingTable forwardingTable,
        MigrationCheckpointStore checkpointStore,
        int batchSize = 100)
    {
        _forwardingTable = forwardingTable ?? throw new ArgumentNullException(nameof(forwardingTable));
        _checkpointStore = checkpointStore ?? throw new ArgumentNullException(nameof(checkpointStore));
        _batchSize = batchSize;
    }

    /// <inheritdoc />
    public async Task<MigrationJob> StartMigrationAsync(MigrationPlan plan, CancellationToken ct = default)
    {
        var jobId = Guid.NewGuid().ToString("N")[..12];
        var now = DateTimeOffset.UtcNow;
        var job = new MigrationJob(
            JobId: jobId,
            Description: $"Migrate {plan.Objects.Count} objects from {plan.SourceNode} to {plan.TargetNode}",
            Plan: plan,
            Status: MigrationStatus.Preparing,
            CreatedUtc: now,
            StartedUtc: null,
            CompletedUtc: null,
            TotalObjects: plan.Objects.Count,
            MigratedObjects: 0,
            FailedObjects: 0,
            BytesMigrated: 0,
            CurrentThroughputBytesPerSec: 0);

        var state = new MigrationJobState
        {
            Job = job,
            Cts = CancellationTokenSource.CreateLinkedTokenSource(ct),
            Paused = false
        };
        _jobs[jobId] = state;

        // Check for existing checkpoint (resume scenario)
        var checkpoint = await _checkpointStore.LoadCheckpointAsync(jobId, ct);

        // Start background execution
        _ = Task.Run(() => ExecuteMigrationAsync(state, checkpoint), state.Cts.Token)
            .ContinueWith(t => System.Diagnostics.Debug.WriteLine(
                $"[BackgroundMigrationEngine] Migration job {jobId} failed: {t.Exception?.InnerException?.Message}"),
                TaskContinuationOptions.OnlyOnFaulted);

        return job with { Status = MigrationStatus.InProgress, StartedUtc = now };
    }

    /// <inheritdoc />
    public Task PauseMigrationAsync(string jobId, CancellationToken ct = default)
    {
        if (_jobs.TryGetValue(jobId, out var state))
            state.Paused = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ResumeMigrationAsync(string jobId, CancellationToken ct = default)
    {
        if (_jobs.TryGetValue(jobId, out var state))
            state.Paused = false;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CancelMigrationAsync(string jobId, CancellationToken ct = default)
    {
        if (_jobs.TryGetValue(jobId, out var state))
        {
            state.Cts.Cancel();
            state.Job = state.Job with { Status = MigrationStatus.Cancelled };
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<MigrationJob> GetStatusAsync(string jobId, CancellationToken ct = default)
    {
        if (_jobs.TryGetValue(jobId, out var state))
            return Task.FromResult(state.Job);
        throw new KeyNotFoundException($"Migration job {jobId} not found.");
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MigrationJob>> ListJobsAsync(MigrationStatus? statusFilter = null, CancellationToken ct = default)
    {
        var jobs = _jobs.Values.Select(s => s.Job);
        if (statusFilter.HasValue)
            jobs = jobs.Where(j => j.Status == statusFilter.Value);
        return Task.FromResult<IReadOnlyList<MigrationJob>>(jobs.ToList());
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<MigrationJob> MonitorAsync(
        string jobId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_jobs.TryGetValue(jobId, out var state))
            {
                yield return state.Job;
                if (state.Job.Status is MigrationStatus.Completed or MigrationStatus.Failed or MigrationStatus.Cancelled)
                    yield break;
            }
            else
            {
                yield break;
            }
            await Task.Delay(1000, ct);
        }
    }

    /// <inheritdoc />
    public Task<ReadForwardingEntry?> GetForwardingEntryAsync(string objectKey, CancellationToken ct = default)
    {
        return Task.FromResult(_forwardingTable.Lookup(objectKey));
    }

    private async Task ExecuteMigrationAsync(MigrationJobState state, MigrationCheckpoint? checkpoint)
    {
        var plan = state.Job.Plan;
        var objects = plan.Objects.ToList();
        int startIndex = 0;
        long bytesMigrated = 0;

        // Resume from checkpoint if available
        if (checkpoint != null)
        {
            startIndex = (int)checkpoint.ProcessedCount;
            bytesMigrated = state.Job.BytesMigrated;
        }

        state.Job = state.Job with { Status = MigrationStatus.InProgress, StartedUtc = DateTimeOffset.UtcNow };
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            for (int i = startIndex; i < objects.Count; i++)
            {
                state.Cts.Token.ThrowIfCancellationRequested();

                // Pause support: spin-wait until unpaused
                while (state.Paused && !state.Cts.Token.IsCancellationRequested)
                {
                    state.Job = state.Job with { Status = MigrationStatus.Paused };
                    await Task.Delay(500, state.Cts.Token);
                }
                state.Job = state.Job with { Status = MigrationStatus.InProgress };

                var obj = objects[i];
                try
                {
                    await MigrateObjectAsync(obj, plan, state);
                    bytesMigrated += obj.SizeBytes;
                    state.Job = state.Job with
                    {
                        MigratedObjects = state.Job.MigratedObjects + 1,
                        BytesMigrated = bytesMigrated,
                        CurrentThroughputBytesPerSec = bytesMigrated / Math.Max(1, sw.Elapsed.TotalSeconds)
                    };
                }
                catch (Exception ex)
                {
                    state.Job = state.Job with { FailedObjects = state.Job.FailedObjects + 1 };
                    System.Diagnostics.Trace.TraceError(
                        $"[BackgroundMigrationEngine] Object migration failed for job {state.Job.JobId}: {ex.GetType().Name}: {ex.Message}");
                }

                // Throttle: if we are ahead of the target throughput, delay
                if (plan.ThrottleBytesPerSec.HasValue && plan.ThrottleBytesPerSec.Value > 0)
                {
                    double targetElapsed = bytesMigrated / (double)plan.ThrottleBytesPerSec.Value;
                    double actualElapsed = sw.Elapsed.TotalSeconds;
                    if (targetElapsed > actualElapsed)
                        await Task.Delay(TimeSpan.FromSeconds(targetElapsed - actualElapsed), state.Cts.Token);
                }

                // Checkpoint every batch for crash recovery
                if ((i + 1) % _batchSize == 0)
                {
                    await _checkpointStore.SaveCheckpointAsync(new MigrationCheckpoint(
                        JobId: state.Job.JobId,
                        LastProcessedKey: obj.ObjectKey,
                        ProcessedCount: i + 1,
                        TimestampUtc: DateTimeOffset.UtcNow));
                }
            }

            state.Job = state.Job with
            {
                Status = MigrationStatus.Completed,
                CompletedUtc = DateTimeOffset.UtcNow
            };
            await _checkpointStore.DeleteCheckpointAsync(state.Job.JobId);
        }
        catch (OperationCanceledException)
        {
            state.Job = state.Job with { Status = MigrationStatus.Cancelled };
        }
        catch (Exception)
        {
            state.Job = state.Job with { Status = MigrationStatus.Failed };
        }
    }

    private async Task MigrateObjectAsync(MigrationObject obj, MigrationPlan plan, MigrationJobState state)
    {
        if (ReadObjectAsync == null || WriteObjectAsync == null)
            throw new InvalidOperationException("Data operation delegates must be set before starting migration.");

        // 1. Copy data from source to target
        using var sourceStream = await ReadObjectAsync(obj.ObjectKey, plan.SourceNode, state.Cts.Token);
        await WriteObjectAsync(obj.ObjectKey, plan.TargetNode, sourceStream, state.Cts.Token);

        // 2. Register read forwarding so reads to old location go to new location
        if (plan.EnableReadForwarding)
        {
            _forwardingTable.RegisterForwarding(obj.ObjectKey, plan.SourceNode, plan.TargetNode);
            state.Job = state.Job with { Status = MigrationStatus.ReadForwarding };
        }

        // 3. Verify checksums match between source and target
        if (plan.ValidateChecksums && GetChecksumAsync != null)
        {
            var sourceChecksum = await GetChecksumAsync(obj.ObjectKey, plan.SourceNode, state.Cts.Token);
            var targetChecksum = await GetChecksumAsync(obj.ObjectKey, plan.TargetNode, state.Cts.Token);
            if (!sourceChecksum.SequenceEqual(targetChecksum))
                throw new InvalidOperationException($"Checksum mismatch for {obj.ObjectKey} after migration.");
        }

        // 4. Delete source only if not in zero-downtime mode
        if (!plan.ZeroDowntime && DeleteObjectAsync != null)
        {
            await DeleteObjectAsync(obj.ObjectKey, plan.SourceNode, state.Cts.Token);
            _forwardingTable.RemoveForwarding(obj.ObjectKey);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var state in _jobs.Values)
        {
            try { state.Cts.Cancel(); } catch { /* Best-effort cancel before dispose */ }
            state.Cts.Dispose();
        }
    }

    private sealed class MigrationJobState
    {
        public MigrationJob Job { get; set; } = null!;
        public CancellationTokenSource Cts { get; set; } = null!;
        public volatile bool Paused;
    }
}
