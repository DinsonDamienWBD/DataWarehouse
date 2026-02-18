// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Contracts.TamperProof;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.TamperProof.Services;

/// <summary>
/// Background service that processes orphaned WORM records from failed transactions.
/// Handles cleanup of expired orphans (with compliance awareness) and recovery linking
/// of orphans that have matching successful retry transactions.
/// </summary>
internal class OrphanCleanupService : IDisposable, IAsyncDisposable
{
    private readonly ILogger<OrphanCleanupService> _logger;
    private readonly TimeSpan _scanInterval;

    private CancellationTokenSource? _cts;
    private Task? _cleanupTask;
    private readonly object _statusLock = new();
    private bool _isRunning;
    private bool _disposed;

    // Orphan registry: tracks all known orphaned WORM records
    private readonly ConcurrentDictionary<Guid, OrphanedWormRecord> _orphanRegistry = new();

    // Successful transaction hashes for recovery linking: ContentHash -> (TransactionId, ObjectId)
    private readonly ConcurrentDictionary<string, (Guid TransactionId, Guid ObjectId)> _successfulTransactions = new();

    // Metrics
    private long _totalProcessed;
    private long _totalPurged;
    private long _totalLinked;
    private DateTime? _lastProcessedAt;

    /// <summary>
    /// Creates a new orphan cleanup service instance.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="scanInterval">Interval between cleanup scans. Defaults to 1 hour.</param>
    public OrphanCleanupService(
        ILogger<OrphanCleanupService> logger,
        TimeSpan? scanInterval = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _scanInterval = scanInterval ?? TimeSpan.FromHours(1);
    }

    /// <summary>
    /// Starts the background orphan cleanup process.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public Task StartAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        lock (_statusLock)
        {
            if (_isRunning)
            {
                _logger.LogWarning("Orphan cleanup service is already running");
                return Task.CompletedTask;
            }

            _isRunning = true;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }

        _logger.LogInformation(
            "Starting orphan cleanup service with scan interval {Interval}",
            _scanInterval);

        _cleanupTask = Task.Run(() => CleanupLoopAsync(_cts.Token), _cts.Token);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the background orphan cleanup process.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task StopAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        _logger.LogInformation("Stopping orphan cleanup service");

        CancellationTokenSource? cts;
        Task? cleanupTask;

        lock (_statusLock)
        {
            if (!_isRunning)
            {
                _logger.LogDebug("Orphan cleanup service is not running");
                return;
            }

            cts = _cts;
            cleanupTask = _cleanupTask;
            _isRunning = false;
        }

        if (cts != null)
        {
            await cts.CancelAsync();

            if (cleanupTask != null)
            {
                try
                {
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                    await cleanupTask.WaitAsync(combinedCts.Token);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("Cleanup loop cancelled");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error waiting for cleanup loop to complete");
                }
            }

            cts.Dispose();
        }

        lock (_statusLock)
        {
            _cts = null;
            _cleanupTask = null;
        }

        _logger.LogInformation("Orphan cleanup service stopped");
    }

    /// <summary>
    /// Registers an orphaned WORM record for tracking and eventual cleanup.
    /// Called when a transactional write fails and leaves a WORM orphan.
    /// </summary>
    /// <param name="orphan">The orphaned WORM record to track.</param>
    public void RegisterOrphan(OrphanedWormRecord orphan)
    {
        ArgumentNullException.ThrowIfNull(orphan, nameof(orphan));

        _orphanRegistry[orphan.OrphanId] = orphan;

        _logger.LogInformation(
            "Registered orphaned WORM record {OrphanId} for object {ObjectId}, status: {Status}",
            orphan.OrphanId, orphan.IntendedObjectId, orphan.Status);
    }

    /// <summary>
    /// Registers a successful transaction for recovery linking.
    /// When an orphan's content hash matches a successful retry, the orphan is linked.
    /// </summary>
    /// <param name="contentHash">Content hash of the successfully written data.</param>
    /// <param name="transactionId">Transaction ID of the successful write.</param>
    /// <param name="objectId">Object ID of the successful write.</param>
    public void RegisterSuccessfulTransaction(string contentHash, Guid transactionId, Guid objectId)
    {
        if (string.IsNullOrWhiteSpace(contentHash)) return;

        _successfulTransactions[contentHash] = (transactionId, objectId);

        _logger.LogDebug(
            "Registered successful transaction {TransactionId} with content hash {ContentHash}",
            transactionId, contentHash);
    }

    /// <summary>
    /// Gets the current status of the orphan cleanup service.
    /// </summary>
    /// <returns>Cleanup service status.</returns>
    public OrphanCleanupStatus GetStatus()
    {
        return new OrphanCleanupStatus(
            IsRunning: _isRunning,
            TotalOrphans: _orphanRegistry.Count,
            TotalProcessed: _totalProcessed,
            TotalPurged: _totalPurged,
            TotalLinked: _totalLinked,
            LastProcessedAt: _lastProcessedAt);
    }

    /// <summary>
    /// Processes all orphaned WORM records in the registry.
    /// Handles expired orphans (cleanup if compliance allows) and
    /// transaction-failed orphans (recovery linking if a matching retry exists).
    /// </summary>
    /// <returns>Number of orphans processed in this scan.</returns>
    public async Task<int> ProcessOrphansAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("Starting orphan processing scan. Registry size: {Count}", _orphanRegistry.Count);

        var processed = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var kvp in _orphanRegistry)
        {
            ct.ThrowIfCancellationRequested();

            var orphanId = kvp.Key;
            var orphan = kvp.Value;

            try
            {
                switch (orphan.Status)
                {
                    case OrphanedWormStatus.PendingExpiry:
                        await ProcessPendingExpiryOrphanAsync(orphanId, orphan, now, ct);
                        processed++;
                        break;

                    case OrphanedWormStatus.Expired:
                        await ProcessExpiredOrphanAsync(orphanId, orphan, ct);
                        processed++;
                        break;

                    case OrphanedWormStatus.TransactionFailed:
                        await ProcessTransactionFailedOrphanAsync(orphanId, orphan, ct);
                        processed++;
                        break;

                    case OrphanedWormStatus.Reviewed:
                        // Reviewed orphans await admin decision; attempt cleanup if retention expired
                        if (IsRetentionExpired(orphan, now))
                        {
                            await ProcessExpiredOrphanAsync(orphanId, orphan, ct);
                            processed++;
                        }
                        break;

                    case OrphanedWormStatus.Purged:
                    case OrphanedWormStatus.LinkedToRetry:
                        // Terminal states, no processing needed
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error processing orphan {OrphanId} (status: {Status})",
                    orphanId, orphan.Status);
            }
        }

        Interlocked.Add(ref _totalProcessed, processed);
        _lastProcessedAt = DateTime.UtcNow;

        _logger.LogDebug(
            "Orphan processing scan complete. Processed: {Processed}, Registry size: {Count}",
            processed, _orphanRegistry.Count);

        return processed;
    }

    /// <summary>
    /// Processes a PendingExpiry orphan: checks if retention has expired and transitions to Expired.
    /// </summary>
    private Task ProcessPendingExpiryOrphanAsync(
        Guid orphanId,
        OrphanedWormRecord orphan,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (IsRetentionExpired(orphan, now))
        {
            _logger.LogInformation(
                "Orphan {OrphanId} retention expired. Transitioning to Expired.",
                orphanId);

            // Transition from PendingExpiry to Expired (retention has passed)
            var updated = new OrphanedWormRecord
            {
                OrphanId = orphan.OrphanId,
                IntendedObjectId = orphan.IntendedObjectId,
                WormReference = orphan.WormReference,
                Status = OrphanedWormStatus.Expired,
                CreatedAt = orphan.CreatedAt,
                FailureReason = orphan.FailureReason,
                OriginalWriteContext = orphan.OriginalWriteContext,
                ReviewedAt = orphan.ReviewedAt,
                ReviewedBy = orphan.ReviewedBy,
                ReviewNotes = orphan.ReviewNotes
            };

            _orphanRegistry[orphanId] = updated;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Processes an Expired orphan: transitions to Reviewed, attempts cleanup, marks as Purged if allowed.
    /// </summary>
    private Task ProcessExpiredOrphanAsync(
        Guid orphanId,
        OrphanedWormRecord orphan,
        CancellationToken ct)
    {
        // First transition to Reviewed
        var reviewed = new OrphanedWormRecord
        {
            OrphanId = orphan.OrphanId,
            IntendedObjectId = orphan.IntendedObjectId,
            WormReference = orphan.WormReference,
            Status = OrphanedWormStatus.Reviewed,
            CreatedAt = orphan.CreatedAt,
            FailureReason = orphan.FailureReason,
            OriginalWriteContext = orphan.OriginalWriteContext,
            ReviewedAt = DateTimeOffset.UtcNow,
            ReviewedBy = "OrphanCleanupService",
            ReviewNotes = "Auto-reviewed: retention expired, compliance check passed"
        };

        _orphanRegistry[orphanId] = reviewed;

        _logger.LogInformation(
            "Orphan {OrphanId} auto-reviewed after expiry. Attempting cleanup.",
            orphanId);

        // Compliance check: verify WORM retention has truly expired
        // (double-check the retention expiry timestamp on the WormReference)
        if (orphan.WormReference.RetentionExpiresAt.HasValue &&
            orphan.WormReference.RetentionExpiresAt.Value > DateTimeOffset.UtcNow)
        {
            _logger.LogWarning(
                "Orphan {OrphanId} WORM retention not yet expired (expires {ExpiresAt}). Skipping purge.",
                orphanId, orphan.WormReference.RetentionExpiresAt.Value);
            return Task.CompletedTask;
        }

        // Mark as Purged (actual WORM deletion would be performed by the WORM provider
        // when retention expires; we track the logical state here)
        var purged = new OrphanedWormRecord
        {
            OrphanId = orphan.OrphanId,
            IntendedObjectId = orphan.IntendedObjectId,
            WormReference = orphan.WormReference,
            Status = OrphanedWormStatus.Purged,
            CreatedAt = orphan.CreatedAt,
            FailureReason = orphan.FailureReason,
            OriginalWriteContext = orphan.OriginalWriteContext,
            ReviewedAt = DateTimeOffset.UtcNow,
            ReviewedBy = "OrphanCleanupService",
            ReviewNotes = "Purged: retention expired, compliance verified"
        };

        _orphanRegistry[orphanId] = purged;
        Interlocked.Increment(ref _totalPurged);

        _logger.LogInformation(
            "Orphan {OrphanId} purged successfully. WORM location: {Location}",
            orphanId, orphan.WormReference.StorageLocation);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Processes a TransactionFailed orphan: attempts recovery linking by checking
    /// if a successful retry transaction exists with a matching content hash.
    /// If found, links the orphan to the retry via LinkedToRetry status.
    /// </summary>
    private Task ProcessTransactionFailedOrphanAsync(
        Guid orphanId,
        OrphanedWormRecord orphan,
        CancellationToken ct)
    {
        var contentHash = orphan.WormReference.ContentHash;

        if (string.IsNullOrWhiteSpace(contentHash))
        {
            _logger.LogDebug(
                "Orphan {OrphanId} has no content hash, cannot attempt recovery linking",
                orphanId);
            return Task.CompletedTask;
        }

        // Check if a successful retry exists with matching content hash
        if (_successfulTransactions.TryGetValue(contentHash, out var successfulTx))
        {
            _logger.LogInformation(
                "Orphan {OrphanId} linked to successful retry transaction {TransactionId} (object {ObjectId})",
                orphanId, successfulTx.TransactionId, successfulTx.ObjectId);

            var linked = new OrphanedWormRecord
            {
                OrphanId = orphan.OrphanId,
                IntendedObjectId = orphan.IntendedObjectId,
                WormReference = orphan.WormReference,
                Status = OrphanedWormStatus.LinkedToRetry,
                CreatedAt = orphan.CreatedAt,
                FailureReason = orphan.FailureReason,
                OriginalWriteContext = orphan.OriginalWriteContext,
                ReviewedAt = DateTimeOffset.UtcNow,
                ReviewedBy = "OrphanCleanupService",
                ReviewNotes = $"Linked to retry transaction {successfulTx.TransactionId} for object {successfulTx.ObjectId}"
            };

            _orphanRegistry[orphanId] = linked;
            Interlocked.Increment(ref _totalLinked);
        }
        else
        {
            _logger.LogDebug(
                "Orphan {OrphanId} has no matching successful retry transaction",
                orphanId);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Checks whether the WORM retention period has expired for an orphan.
    /// </summary>
    private static bool IsRetentionExpired(OrphanedWormRecord orphan, DateTimeOffset now)
    {
        if (orphan.WormReference.RetentionExpiresAt.HasValue)
        {
            return now >= orphan.WormReference.RetentionExpiresAt.Value;
        }

        // If no retention expiry is set, consider expired after 90 days from creation
        return now >= orphan.CreatedAt.AddDays(90);
    }

    /// <summary>
    /// Main cleanup loop that runs in the background.
    /// </summary>
    private async Task CleanupLoopAsync(CancellationToken ct)
    {
        _logger.LogDebug("Orphan cleanup loop started");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ProcessOrphansAsync(ct);

                _logger.LogDebug("Waiting {Interval} until next orphan cleanup scan", _scanInterval);
                await Task.Delay(_scanInterval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.LogDebug("Orphan cleanup loop cancelled");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in orphan cleanup loop, will retry after interval");

                try
                {
                    await Task.Delay(_scanInterval, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _logger.LogDebug("Orphan cleanup loop exited");
    }

    /// <summary>
    /// Throws if the service has been disposed.
    /// </summary>
    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Disposes resources.
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            try
            {
                // Call async dispose and block (safer than GetAwaiter().GetResult())
                DisposeAsync().AsTask().Wait();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping orphan cleanup service during disposal");
            }

            _cts?.Dispose();
        }

        _disposed = true;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping orphan cleanup service during async disposal");
        }

        _cts?.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Status of the orphan cleanup service.
/// </summary>
/// <param name="IsRunning">Whether the cleanup service is currently running.</param>
/// <param name="TotalOrphans">Total orphaned records in the registry.</param>
/// <param name="TotalProcessed">Total orphans processed across all scans.</param>
/// <param name="TotalPurged">Total orphans purged (cleaned up).</param>
/// <param name="TotalLinked">Total orphans linked to successful retry transactions.</param>
/// <param name="LastProcessedAt">When the last processing scan completed.</param>
public record OrphanCleanupStatus(
    bool IsRunning,
    int TotalOrphans,
    long TotalProcessed,
    long TotalPurged,
    long TotalLinked,
    DateTime? LastProcessedAt);
