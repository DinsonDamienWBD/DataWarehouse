using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Storage.Fabric;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

using IStorageStrategy = DataWarehouse.SDK.Contracts.Storage.IStorageStrategy;

namespace DataWarehouse.Plugins.UniversalFabric.Migration;

/// <summary>
/// Live migration engine that moves data between storage backends via the Universal Fabric.
/// Supports streaming transfers, bounded parallelism, pause/resume, integrity verification,
/// and retry with exponential backoff. Objects are streamed directly from source to destination
/// without buffering entire objects in memory.
/// </summary>
public class LiveMigrationEngine
{
    private readonly IStorageFabric _fabric;
    private readonly BoundedDictionary<string, MigrationJob> _jobs = new BoundedDictionary<string, MigrationJob>(1000);
    private readonly BoundedDictionary<string, CancellationTokenSource> _jobCts = new BoundedDictionary<string, CancellationTokenSource>(1000);

    /// <summary>
    /// Creates a new LiveMigrationEngine backed by the given storage fabric.
    /// </summary>
    /// <param name="fabric">The storage fabric providing backend registry and routing.</param>
    public LiveMigrationEngine(IStorageFabric fabric)
    {
        _fabric = fabric ?? throw new ArgumentNullException(nameof(fabric));
    }

    /// <summary>
    /// Starts a new migration job. The job runs in the background and can be monitored
    /// via <see cref="GetProgress"/> or managed via <see cref="PauseJob"/>, <see cref="ResumeJob"/>,
    /// and <see cref="CancelJob"/>.
    /// </summary>
    /// <param name="job">The configured migration job to start.</param>
    /// <param name="ct">Cancellation token that can abort the entire migration.</param>
    /// <returns>The started migration job (status will be Running).</returns>
    public Task<MigrationJob> StartMigrationAsync(MigrationJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        _jobs[job.JobId] = job;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _jobCts[job.JobId] = cts;
        job.Start();

        // Run migration in background -- fire and forget with error handling in ExecuteMigrationAsync
        _ = Task.Run(() => ExecuteMigrationAsync(job, cts.Token), cts.Token);
        return Task.FromResult(job);
    }

    /// <summary>
    /// Gets a progress snapshot for the specified job.
    /// </summary>
    /// <param name="jobId">The job ID to get progress for.</param>
    /// <returns>A progress snapshot, or null if the job is not found.</returns>
    public MigrationProgress? GetProgress(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
            return null;

        var elapsed = job.StartedAt.HasValue
            ? (job.CompletedAt ?? DateTime.UtcNow) - job.StartedAt.Value
            : TimeSpan.Zero;

        return new MigrationProgress
        {
            JobId = job.JobId,
            Status = job.Status,
            TotalObjects = job.TotalObjects,
            MigratedObjects = job.MigratedObjects,
            FailedObjects = job.FailedObjects,
            SkippedObjects = job.SkippedObjects,
            TotalBytes = job.TotalBytes,
            MigratedBytes = job.MigratedBytes,
            Elapsed = elapsed
        };
    }

    /// <summary>
    /// Pauses a running migration job. In-flight object transfers will complete
    /// but no new transfers will start until resumed.
    /// </summary>
    /// <param name="jobId">The job ID to pause.</param>
    /// <exception cref="KeyNotFoundException">Thrown when the job ID is not found.</exception>
    public void PauseJob(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
            throw new KeyNotFoundException($"Migration job '{jobId}' not found.");
        job.Pause();
    }

    /// <summary>
    /// Resumes a paused migration job.
    /// </summary>
    /// <param name="jobId">The job ID to resume.</param>
    /// <exception cref="KeyNotFoundException">Thrown when the job ID is not found.</exception>
    public void ResumeJob(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
            throw new KeyNotFoundException($"Migration job '{jobId}' not found.");
        job.Resume();
    }

    /// <summary>
    /// Cancels a migration job. In-flight transfers may be interrupted.
    /// </summary>
    /// <param name="jobId">The job ID to cancel.</param>
    /// <exception cref="KeyNotFoundException">Thrown when the job ID is not found.</exception>
    public void CancelJob(string jobId)
    {
        if (!_jobCts.TryGetValue(jobId, out var cts))
            throw new KeyNotFoundException($"Migration job '{jobId}' not found.");
        cts.Cancel();
    }

    /// <summary>
    /// Lists all migration jobs (active and completed).
    /// </summary>
    public IReadOnlyList<MigrationJob> ListJobs() => _jobs.Values.ToList();

    /// <summary>
    /// Gets a specific migration job by ID.
    /// </summary>
    /// <param name="jobId">The job ID to retrieve.</param>
    /// <returns>The migration job, or null if not found.</returns>
    public MigrationJob? GetJob(string jobId)
    {
        _jobs.TryGetValue(jobId, out var job);
        return job;
    }

    // --- Private implementation ---

    private async Task ExecuteMigrationAsync(MigrationJob job, CancellationToken ct)
    {
        try
        {
            var sourceBackend = _fabric.Registry.GetStrategy(job.SourceBackendId);
            if (sourceBackend is null)
                throw new BackendNotFoundException(
                    $"Source backend '{job.SourceBackendId}' not found.",
                    job.SourceBackendId, address: null);

            var destBackend = _fabric.Registry.GetStrategy(job.DestinationBackendId);
            if (destBackend is null)
                throw new BackendNotFoundException(
                    $"Destination backend '{job.DestinationBackendId}' not found.",
                    job.DestinationBackendId, address: null);

            // Phase 1: Enumerate source objects
            var objects = new List<StorageObjectMetadata>();
            await foreach (var obj in sourceBackend.ListAsync(job.SourcePrefix, ct))
            {
                objects.Add(obj);
            }
            job.SetTotal(objects.Count, objects.Sum(o => o.Size));

            // Phase 2: Migrate objects with bounded parallelism
            using var semaphore = new SemaphoreSlim(job.MaxConcurrency);
            var tasks = objects.Select(async obj =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    // Wait while paused
                    while (job.Status == MigrationJobStatus.Paused && !ct.IsCancellationRequested)
                        await Task.Delay(500, ct);

                    ct.ThrowIfCancellationRequested();
                    await MigrateObjectAsync(sourceBackend, destBackend, obj, job, ct);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);

            // If all objects failed, mark the job as failed
            if (job.FailedObjects > 0 && job.MigratedObjects == 0 && job.SkippedObjects == 0)
            {
                job.Fail($"All {job.FailedObjects} objects failed to migrate.");
            }
            else
            {
                job.Complete();
            }
        }
        catch (OperationCanceledException)
        {
            job.Cancel();
        }
        catch (Exception ex)
        {
            job.Fail(ex.Message);
        }
    }

    private async Task MigrateObjectAsync(
        IStorageStrategy source,
        IStorageStrategy dest,
        StorageObjectMetadata obj,
        MigrationJob job,
        CancellationToken ct)
    {
        for (int attempt = 0; attempt <= job.MaxRetries; attempt++)
        {
            try
            {
                // Check if exists in destination (skip if configured)
                if (job.SkipExisting && await dest.ExistsAsync(obj.Key, ct))
                {
                    job.RecordSkipped();
                    return;
                }

                // Stream from source to destination (no full object buffering)
                using var stream = await source.RetrieveAsync(obj.Key, ct);

                // Pass custom metadata from source object
                var metadata = obj.CustomMetadata is not null
                    ? new Dictionary<string, string>(obj.CustomMetadata)
                    : null;

                await dest.StoreAsync(obj.Key, stream, metadata, ct);

                // Verify integrity if configured
                if (job.VerifyAfterCopy)
                {
                    var destMeta = await dest.GetMetadataAsync(obj.Key, ct);
                    if (destMeta.Size != obj.Size)
                    {
                        throw new IOException(
                            $"Size mismatch for '{obj.Key}': source={obj.Size}, dest={destMeta.Size}");
                    }
                }

                // Delete source if Move mode with verification
                if (job.Mode == MigrationMode.Move && job.DeleteSourceAfterVerify)
                {
                    await source.DeleteAsync(obj.Key, ct);
                }

                job.RecordMigrated(obj.Size);
                return;
            }
            catch (OperationCanceledException)
            {
                throw; // Don't retry cancellations
            }
            catch (Exception ex) when (attempt < job.MaxRetries)
            {
                // Exponential backoff before retry
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                await Task.Delay(delay, ct);
            }
            catch (Exception ex)
            {
                // Final attempt failed -- record failure
                job.RecordFailed(obj.Key, ex.Message);
            }
        }
    }
}
