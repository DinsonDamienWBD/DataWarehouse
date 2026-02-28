using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Storage.Migration;

/// <summary>
/// File-based checkpoint store for migration job resumption.
/// Saves progress after each batch so migrations can resume after crashes.
/// Checkpoints are persisted as JSON files on disk and cached in memory for fast lookups.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Background migration checkpointing")]
public sealed class MigrationCheckpointStore
{
    private readonly string _checkpointDirectory;
    private readonly BoundedDictionary<string, MigrationCheckpoint> _inMemory = new BoundedDictionary<string, MigrationCheckpoint>(1000);

    /// <summary>
    /// Creates a new checkpoint store backed by the specified directory.
    /// The directory is created if it does not exist.
    /// </summary>
    /// <param name="checkpointDirectory">Path to the directory where checkpoint files are stored.</param>
    public MigrationCheckpointStore(string checkpointDirectory)
    {
        _checkpointDirectory = checkpointDirectory ?? throw new ArgumentNullException(nameof(checkpointDirectory));
        Directory.CreateDirectory(checkpointDirectory);
    }

    /// <summary>
    /// Saves a migration checkpoint to both in-memory cache and disk.
    /// </summary>
    /// <param name="checkpoint">The checkpoint to save.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SaveCheckpointAsync(MigrationCheckpoint checkpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (string.IsNullOrEmpty(checkpoint.JobId))
            throw new ArgumentException("Checkpoint JobId must not be null or empty.", nameof(checkpoint));
        _inMemory.AddOrUpdate(checkpoint.JobId, checkpoint, (_, _) => checkpoint);
        var path = GetCheckpointPath(checkpoint.JobId);
        var json = JsonSerializer.Serialize(checkpoint);
        await File.WriteAllTextAsync(path, json, ct);
    }

    /// <summary>
    /// Loads a migration checkpoint, first checking in-memory cache then falling back to disk.
    /// </summary>
    /// <param name="jobId">The migration job ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The checkpoint if found, or null.</returns>
    public async Task<MigrationCheckpoint?> LoadCheckpointAsync(string jobId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(jobId))
            throw new ArgumentException("Job ID must not be null or empty.", nameof(jobId));

        if (_inMemory.TryGetValue(jobId, out var cached))
            return cached;

        var path = GetCheckpointPath(jobId);
        if (!File.Exists(path)) return null;

        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            var checkpoint = JsonSerializer.Deserialize<MigrationCheckpoint>(json);
            if (checkpoint != null)
                _inMemory.TryAdd(checkpoint.JobId, checkpoint);
            return checkpoint;
        }
        catch (System.Text.Json.JsonException ex)
        {
            // Corrupt checkpoint file — treat as missing, log and return null so caller retries from scratch
            System.Diagnostics.Trace.TraceWarning(
                $"[MigrationCheckpointStore] Corrupt checkpoint file for job '{jobId}': {ex.Message}. Treating as missing.");
            return null;
        }
    }

    /// <summary>
    /// Deletes a checkpoint from both in-memory cache and disk.
    /// Called after migration completes successfully.
    /// </summary>
    /// <param name="jobId">The migration job ID.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task DeleteCheckpointAsync(string jobId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(jobId))
            throw new ArgumentException("Job ID must not be null or empty.", nameof(jobId));
        _inMemory.TryRemove(jobId, out _);
        var path = GetCheckpointPath(jobId);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string GetCheckpointPath(string jobId)
    {
        // Sanitize jobId to prevent path traversal (e.g., "../../etc/passwd")
        var sanitized = Path.GetFileName(jobId);
        if (string.IsNullOrWhiteSpace(sanitized) || sanitized != jobId)
            throw new ArgumentException($"Invalid job ID: contains path separator characters", nameof(jobId));
        return Path.Combine(_checkpointDirectory, $"migration-{sanitized}.checkpoint.json");
    }
}
