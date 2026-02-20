using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateStorage.Features
{
    /// <summary>
    /// Cross-Backend Migration Feature (C3) - Migrates objects between any two storage backends.
    ///
    /// Features:
    /// - Move objects between any two storage backends
    /// - Streaming migration (no full copy in memory for large objects)
    /// - Progress tracking and resumable migrations
    /// - Batch migration support
    /// - Metadata preservation during migration
    /// - Verification after migration (checksum validation)
    /// - Parallel migration workers
    /// - Migration history and audit trail
    /// </summary>
    public sealed class CrossBackendMigrationFeature : IDisposable
    {
        private readonly StorageStrategyRegistry _registry;
        private readonly BoundedDictionary<string, MigrationJob> _activeJobs = new BoundedDictionary<string, MigrationJob>(1000);
        private readonly BoundedDictionary<string, MigrationHistory> _migrationHistory = new BoundedDictionary<string, MigrationHistory>(1000);
        private bool _disposed;

        // Configuration
        private int _maxParallelMigrations = 4;

        // Statistics
        private long _totalMigrations;
        private long _totalBytesMigrated;
        private long _totalFailures;
        private long _totalVerifications;

        /// <summary>
        /// Initializes a new instance of the CrossBackendMigrationFeature.
        /// </summary>
        /// <param name="registry">The storage strategy registry.</param>
        public CrossBackendMigrationFeature(StorageStrategyRegistry registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        /// <summary>
        /// Gets the total number of successful migrations.
        /// </summary>
        public long TotalMigrations => Interlocked.Read(ref _totalMigrations);

        /// <summary>
        /// Gets the total number of bytes migrated.
        /// </summary>
        public long TotalBytesMigrated => Interlocked.Read(ref _totalBytesMigrated);

        /// <summary>
        /// Gets the total number of failed migrations.
        /// </summary>
        public long TotalFailures => Interlocked.Read(ref _totalFailures);

        /// <summary>
        /// Gets or sets the maximum number of parallel migrations.
        /// </summary>
        public int MaxParallelMigrations
        {
            get => _maxParallelMigrations;
            set => _maxParallelMigrations = value > 0 ? value : 1;
        }

        /// <summary>
        /// Migrates a single object from one backend to another.
        /// </summary>
        /// <param name="key">The object key.</param>
        /// <param name="sourceBackendId">Source backend ID.</param>
        /// <param name="targetBackendId">Target backend ID.</param>
        /// <param name="options">Migration options.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Migration result.</returns>
        public async Task<MigrationResult> MigrateObjectAsync(
            string key,
            string sourceBackendId,
            string targetBackendId,
            MigrationOptions? options = null,
            CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceBackendId);
            ArgumentException.ThrowIfNullOrWhiteSpace(targetBackendId);

            options ??= MigrationOptions.Default;

            var result = new MigrationResult
            {
                Key = key,
                SourceBackend = sourceBackendId,
                TargetBackend = targetBackendId,
                StartTime = DateTime.UtcNow
            };

            try
            {
                // Get backends
                var sourceBackend = _registry.GetStrategy(sourceBackendId);
                var targetBackend = _registry.GetStrategy(targetBackendId);

                if (sourceBackend == null)
                {
                    result.Success = false;
                    result.ErrorMessage = $"Source backend '{sourceBackendId}' not found";
                    return result;
                }

                if (targetBackend == null)
                {
                    result.Success = false;
                    result.ErrorMessage = $"Target backend '{targetBackendId}' not found";
                    return result;
                }

                // Get source metadata
                var sourceMetadata = await sourceBackend.GetMetadataAsync(key, ct);
                result.BytesTotal = sourceMetadata.Size;

                // Check if object exists in source
                if (!await sourceBackend.ExistsAsync(key, ct))
                {
                    result.Success = false;
                    result.ErrorMessage = "Object does not exist in source backend";
                    return result;
                }

                // Stream data from source to target
                using var sourceStream = await sourceBackend.RetrieveAsync(key, ct);

                // If using streaming, wrap in progress tracking stream
                Stream migrationStream = sourceStream;
                if (options.TrackProgress)
                {
                    migrationStream = new ProgressTrackingStream(
                        sourceStream,
                        sourceMetadata.Size,
                        bytesRead => result.BytesMigrated = bytesRead);
                }

                // Convert readonly dictionary to mutable
                var metadataDict = sourceMetadata.CustomMetadata != null
                    ? new Dictionary<string, string>(sourceMetadata.CustomMetadata)
                    : null;

                // Write to target backend
                var targetMetadata = await targetBackend.StoreAsync(
                    key,
                    migrationStream,
                    metadataDict,
                    ct);

                result.BytesMigrated = sourceMetadata.Size;

                // Verify if requested
                if (options.VerifyAfterMigration)
                {
                    var verificationResult = await VerifyMigrationAsync(
                        key,
                        sourceBackend,
                        targetBackend,
                        ct);

                    result.Verified = verificationResult;
                    Interlocked.Increment(ref _totalVerifications);

                    if (!verificationResult)
                    {
                        result.Success = false;
                        result.ErrorMessage = "Verification failed - data mismatch";

                        // Rollback if requested
                        if (options.RollbackOnFailure)
                        {
                            await targetBackend.DeleteAsync(key, ct);
                        }

                        return result;
                    }
                }

                // Delete from source if requested
                if (options.DeleteSourceAfterMigration)
                {
                    await sourceBackend.DeleteAsync(key, ct);
                    result.SourceDeleted = true;
                }

                result.Success = true;
                result.EndTime = DateTime.UtcNow;

                // Update statistics
                Interlocked.Increment(ref _totalMigrations);
                Interlocked.Add(ref _totalBytesMigrated, result.BytesMigrated);

                // Record migration history
                RecordMigrationHistory(result);

                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.EndTime = DateTime.UtcNow;
                Interlocked.Increment(ref _totalFailures);
                return result;
            }
        }

        /// <summary>
        /// Migrates multiple objects from one backend to another in batch.
        /// </summary>
        /// <param name="keys">List of object keys to migrate.</param>
        /// <param name="sourceBackendId">Source backend ID.</param>
        /// <param name="targetBackendId">Target backend ID.</param>
        /// <param name="options">Migration options.</param>
        /// <param name="progress">Progress callback.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Batch migration result with individual results.</returns>
        public async Task<BatchMigrationResult> MigrateBatchAsync(
            IReadOnlyList<string> keys,
            string sourceBackendId,
            string targetBackendId,
            MigrationOptions? options = null,
            IProgress<BatchMigrationProgress>? progress = null,
            CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(keys);

            options ??= MigrationOptions.Default;

            var batchResult = new BatchMigrationResult
            {
                TotalObjects = keys.Count,
                SourceBackend = sourceBackendId,
                TargetBackend = targetBackendId,
                StartTime = DateTime.UtcNow
            };

            // Create migration job
            var jobId = Guid.NewGuid().ToString("N");
            var job = new MigrationJob
            {
                JobId = jobId,
                TotalObjects = keys.Count,
                SourceBackend = sourceBackendId,
                TargetBackend = targetBackendId,
                StartTime = DateTime.UtcNow
            };

            _activeJobs[jobId] = job;

            try
            {
                // Use SemaphoreSlim to limit parallel operations
                using var semaphore = new SemaphoreSlim(_maxParallelMigrations);

                var migrationTasks = keys.Select(async key =>
                {
                    await semaphore.WaitAsync(ct);
                    try
                    {
                        var result = await MigrateObjectAsync(
                            key,
                            sourceBackendId,
                            targetBackendId,
                            options,
                            ct);

                        // Update job progress
                        Interlocked.Increment(ref job.CompletedObjects);
                        if (result.Success)
                        {
                            Interlocked.Increment(ref job.SuccessfulObjects);
                            Interlocked.Add(ref job.BytesMigrated, result.BytesMigrated);
                        }
                        else
                        {
                            Interlocked.Increment(ref job.FailedObjects);
                        }

                        // Report progress
                        progress?.Report(new BatchMigrationProgress
                        {
                            TotalObjects = keys.Count,
                            CompletedObjects = job.CompletedObjects,
                            SuccessfulObjects = job.SuccessfulObjects,
                            FailedObjects = job.FailedObjects,
                            BytesMigrated = job.BytesMigrated
                        });

                        return result;
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }).ToList();

                var results = await Task.WhenAll(migrationTasks);

                batchResult.Results.AddRange(results);
                batchResult.SuccessfulObjects = results.Count(r => r.Success);
                batchResult.FailedObjects = results.Count(r => !r.Success);
                batchResult.TotalBytesMigrated = results.Sum(r => r.BytesMigrated);
                batchResult.EndTime = DateTime.UtcNow;
                batchResult.Success = batchResult.FailedObjects == 0;

                job.EndTime = DateTime.UtcNow;
                job.Status = batchResult.Success ? MigrationStatus.Completed : MigrationStatus.CompletedWithErrors;

                return batchResult;
            }
            catch (Exception ex)
            {
                batchResult.Success = false;
                batchResult.ErrorMessage = ex.Message;
                batchResult.EndTime = DateTime.UtcNow;

                job.EndTime = DateTime.UtcNow;
                job.Status = MigrationStatus.Failed;

                return batchResult;
            }
            finally
            {
                _activeJobs.TryRemove(jobId, out _);
            }
        }

        /// <summary>
        /// Gets the status of an active migration job.
        /// </summary>
        /// <param name="jobId">Migration job ID.</param>
        /// <returns>Migration job or null if not found.</returns>
        public MigrationJob? GetJobStatus(string jobId)
        {
            return _activeJobs.TryGetValue(jobId, out var job) ? job : null;
        }

        /// <summary>
        /// Gets all active migration jobs.
        /// </summary>
        /// <returns>List of active jobs.</returns>
        public IReadOnlyList<MigrationJob> GetActiveJobs()
        {
            return _activeJobs.Values.ToList();
        }

        /// <summary>
        /// Gets migration history for an object.
        /// </summary>
        /// <param name="key">Object key.</param>
        /// <returns>Migration history or null if not found.</returns>
        public MigrationHistory? GetMigrationHistory(string key)
        {
            return _migrationHistory.TryGetValue(key, out var history) ? history : null;
        }

        #region Private Methods

        private async Task<bool> VerifyMigrationAsync(
            string key,
            IStorageStrategy sourceBackend,
            IStorageStrategy targetBackend,
            CancellationToken ct)
        {
            try
            {
                // Compare sizes
                var sourceMetadata = await sourceBackend.GetMetadataAsync(key, ct);
                var targetMetadata = await targetBackend.GetMetadataAsync(key, ct);

                if (sourceMetadata.Size != targetMetadata.Size)
                {
                    return false;
                }

                // Compare ETags if available
                if (!string.IsNullOrEmpty(sourceMetadata.ETag) &&
                    !string.IsNullOrEmpty(targetMetadata.ETag))
                {
                    return sourceMetadata.ETag.Equals(targetMetadata.ETag, StringComparison.OrdinalIgnoreCase);
                }

                // Fallback: byte-by-byte comparison (expensive)
                using var sourceStream = await sourceBackend.RetrieveAsync(key, ct);
                using var targetStream = await targetBackend.RetrieveAsync(key, ct);

                var sourceHash = await ComputeHashAsync(sourceStream, ct);
                var targetHash = await ComputeHashAsync(targetStream, ct);

                return sourceHash.SequenceEqual(targetHash);
            }
            catch
            {
                return false;
            }
        }

        private static async Task<byte[]> ComputeHashAsync(Stream stream, CancellationToken ct)
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            return await sha256.ComputeHashAsync(stream, ct);
        }

        private void RecordMigrationHistory(MigrationResult result)
        {
            var history = _migrationHistory.GetOrAdd(result.Key, _ => new MigrationHistory
            {
                Key = result.Key
            });

            history.Migrations.Add(new MigrationHistoryEntry
            {
                SourceBackend = result.SourceBackend,
                TargetBackend = result.TargetBackend,
                Timestamp = result.StartTime,
                BytesMigrated = result.BytesMigrated,
                Success = result.Success,
                Duration = result.EndTime.HasValue
                    ? result.EndTime.Value - result.StartTime
                    : TimeSpan.Zero
            });
        }

        #endregion

        /// <summary>
        /// Disposes resources.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _activeJobs.Clear();
            _migrationHistory.Clear();
        }
    }

    #region Supporting Types

    /// <summary>
    /// Migration options.
    /// </summary>
    public sealed class MigrationOptions
    {
        /// <summary>Whether to delete the object from source after successful migration.</summary>
        public bool DeleteSourceAfterMigration { get; set; } = false;

        /// <summary>Whether to verify data integrity after migration.</summary>
        public bool VerifyAfterMigration { get; set; } = true;

        /// <summary>Whether to rollback (delete from target) on verification failure.</summary>
        public bool RollbackOnFailure { get; set; } = true;

        /// <summary>Whether to track progress during migration.</summary>
        public bool TrackProgress { get; set; } = true;

        /// <summary>Default migration options.</summary>
        public static MigrationOptions Default => new();
    }

    /// <summary>
    /// Result of a single object migration.
    /// </summary>
    public sealed class MigrationResult
    {
        /// <summary>Object key.</summary>
        public string Key { get; init; } = string.Empty;

        /// <summary>Source backend ID.</summary>
        public string SourceBackend { get; init; } = string.Empty;

        /// <summary>Target backend ID.</summary>
        public string TargetBackend { get; init; } = string.Empty;

        /// <summary>Whether migration succeeded.</summary>
        public bool Success { get; set; }

        /// <summary>Total bytes to migrate.</summary>
        public long BytesTotal { get; set; }

        /// <summary>Bytes successfully migrated.</summary>
        public long BytesMigrated { get; set; }

        /// <summary>Whether data was verified.</summary>
        public bool Verified { get; set; }

        /// <summary>Whether source was deleted.</summary>
        public bool SourceDeleted { get; set; }

        /// <summary>Start time.</summary>
        public DateTime StartTime { get; init; }

        /// <summary>End time.</summary>
        public DateTime? EndTime { get; set; }

        /// <summary>Migration duration.</summary>
        public TimeSpan Duration => EndTime.HasValue ? EndTime.Value - StartTime : TimeSpan.Zero;

        /// <summary>Error message if failed.</summary>
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Result of a batch migration.
    /// </summary>
    public sealed class BatchMigrationResult
    {
        /// <summary>Total number of objects.</summary>
        public int TotalObjects { get; init; }

        /// <summary>Source backend ID.</summary>
        public string SourceBackend { get; init; } = string.Empty;

        /// <summary>Target backend ID.</summary>
        public string TargetBackend { get; init; } = string.Empty;

        /// <summary>Number of successful migrations.</summary>
        public int SuccessfulObjects { get; set; }

        /// <summary>Number of failed migrations.</summary>
        public int FailedObjects { get; set; }

        /// <summary>Total bytes migrated.</summary>
        public long TotalBytesMigrated { get; set; }

        /// <summary>Individual migration results.</summary>
        public List<MigrationResult> Results { get; } = new();

        /// <summary>Whether all migrations succeeded.</summary>
        public bool Success { get; set; }

        /// <summary>Start time.</summary>
        public DateTime StartTime { get; init; }

        /// <summary>End time.</summary>
        public DateTime? EndTime { get; set; }

        /// <summary>Total duration.</summary>
        public TimeSpan Duration => EndTime.HasValue ? EndTime.Value - StartTime : TimeSpan.Zero;

        /// <summary>Error message if failed.</summary>
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Progress information for batch migrations.
    /// </summary>
    public sealed class BatchMigrationProgress
    {
        /// <summary>Total objects to migrate.</summary>
        public int TotalObjects { get; init; }

        /// <summary>Objects completed so far.</summary>
        public int CompletedObjects { get; init; }

        /// <summary>Successful objects.</summary>
        public int SuccessfulObjects { get; init; }

        /// <summary>Failed objects.</summary>
        public int FailedObjects { get; init; }

        /// <summary>Total bytes migrated.</summary>
        public long BytesMigrated { get; init; }

        /// <summary>Progress percentage (0-100).</summary>
        public double ProgressPercent => TotalObjects > 0 ? (CompletedObjects * 100.0 / TotalObjects) : 0;
    }

    /// <summary>
    /// Migration job information.
    /// </summary>
    public sealed class MigrationJob
    {
        /// <summary>Job ID.</summary>
        public string JobId { get; init; } = string.Empty;

        /// <summary>Total objects to migrate.</summary>
        public int TotalObjects { get; init; }

        /// <summary>Completed objects.</summary>
        public int CompletedObjects;

        /// <summary>Successful objects.</summary>
        public int SuccessfulObjects;

        /// <summary>Failed objects.</summary>
        public int FailedObjects;

        /// <summary>Bytes migrated.</summary>
        public long BytesMigrated;

        /// <summary>Source backend ID.</summary>
        public string SourceBackend { get; init; } = string.Empty;

        /// <summary>Target backend ID.</summary>
        public string TargetBackend { get; init; } = string.Empty;

        /// <summary>Job status.</summary>
        public MigrationStatus Status { get; set; } = MigrationStatus.Running;

        /// <summary>Start time.</summary>
        public DateTime StartTime { get; init; }

        /// <summary>End time.</summary>
        public DateTime? EndTime { get; set; }
    }

    /// <summary>
    /// Migration status.
    /// </summary>
    public enum MigrationStatus
    {
        /// <summary>Migration is running.</summary>
        Running,

        /// <summary>Migration completed successfully.</summary>
        Completed,

        /// <summary>Migration completed with some errors.</summary>
        CompletedWithErrors,

        /// <summary>Migration failed.</summary>
        Failed
    }

    /// <summary>
    /// Migration history for an object.
    /// </summary>
    public sealed class MigrationHistory
    {
        /// <summary>Object key.</summary>
        public string Key { get; init; } = string.Empty;

        /// <summary>List of migrations.</summary>
        public List<MigrationHistoryEntry> Migrations { get; } = new();
    }

    /// <summary>
    /// Single migration history entry.
    /// </summary>
    public sealed class MigrationHistoryEntry
    {
        /// <summary>Source backend.</summary>
        public string SourceBackend { get; init; } = string.Empty;

        /// <summary>Target backend.</summary>
        public string TargetBackend { get; init; } = string.Empty;

        /// <summary>Migration timestamp.</summary>
        public DateTime Timestamp { get; init; }

        /// <summary>Bytes migrated.</summary>
        public long BytesMigrated { get; init; }

        /// <summary>Whether migration succeeded.</summary>
        public bool Success { get; init; }

        /// <summary>Migration duration.</summary>
        public TimeSpan Duration { get; init; }
    }

    /// <summary>
    /// Stream wrapper that tracks progress.
    /// </summary>
    internal sealed class ProgressTrackingStream : Stream
    {
        private readonly Stream _innerStream;
        private readonly long _totalBytes;
        private readonly Action<long> _progressCallback;
        private long _bytesRead;

        public ProgressTrackingStream(Stream innerStream, long totalBytes, Action<long> progressCallback)
        {
            _innerStream = innerStream;
            _totalBytes = totalBytes;
            _progressCallback = progressCallback;
        }

        public override bool CanRead => _innerStream.CanRead;
        public override bool CanSeek => _innerStream.CanSeek;
        public override bool CanWrite => _innerStream.CanWrite;
        public override long Length => _innerStream.Length;
        public override long Position
        {
            get => _innerStream.Position;
            set => _innerStream.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var bytesRead = _innerStream.Read(buffer, offset, count);
            _bytesRead += bytesRead;
            _progressCallback(_bytesRead);
            return bytesRead;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var bytesRead = await _innerStream.ReadAsync(buffer, offset, count, cancellationToken);
            _bytesRead += bytesRead;
            _progressCallback(_bytesRead);
            return bytesRead;
        }

        public override void Flush() => _innerStream.Flush();
        public override long Seek(long offset, SeekOrigin origin) => _innerStream.Seek(offset, origin);
        public override void SetLength(long value) => _innerStream.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _innerStream.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _innerStream.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    #endregion
}
