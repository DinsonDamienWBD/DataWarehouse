// <copyright file="SemanticSyncOrchestrator.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.SemanticSync;
using DataWarehouse.Plugins.SemanticSync.Strategies.Fidelity;

namespace DataWarehouse.Plugins.SemanticSync.Orchestration;

/// <summary>
/// Manages concurrent sync operations via an async work queue backed by <see cref="Channel{T}"/>.
/// Processes sync requests through the <see cref="SyncPipeline"/>, manages deferred items with
/// a timer-based re-enqueue mechanism, and tracks bandwidth consumption metrics.
/// </summary>
/// <remarks>
/// <para>
/// Concurrency is controlled by a <see cref="SemaphoreSlim"/> with a configurable maximum
/// (default 4). Background worker loop reads from the channel and processes requests in parallel
/// up to the concurrency limit.
/// </para>
/// <para>
/// Deferred items are stored in a <see cref="ConcurrentQueue{T}"/> and checked by a periodic
/// timer. When a deferred item's DeferUntil time has passed, it is re-enqueued for processing.
/// </para>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic sync orchestration")]
internal sealed class SemanticSyncOrchestrator : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Internal request record for the work queue.
    /// </summary>
    private sealed record SyncRequest(
        string DataId,
        ReadOnlyMemory<byte> Data,
        IDictionary<string, string>? Metadata,
        TaskCompletionSource<SyncPipelineResult> Completion);

    private readonly SyncPipeline _pipeline;
    private readonly BandwidthBudgetTracker? _budgetTracker;
    private readonly Channel<SyncRequest> _requestChannel;
    private readonly SemaphoreSlim _concurrencyLimiter;
    private readonly ConcurrentQueue<(SyncRequest Request, DateTimeOffset DeferUntil)> _deferredItems = new();
    private readonly Timer _deferTimer;
    private readonly CancellationTokenSource _shutdownCts = new();
    private Task? _workerTask;
    private bool _disposed;

    // Metrics
    private long _totalSyncs;
    private long _totalBytesSaved;
    private long _totalOriginalBytes;
    private long _totalPayloadBytes;

    /// <summary>
    /// Gets the default maximum number of concurrent sync operations.
    /// </summary>
    private const int DefaultMaxConcurrency = 4;

    /// <summary>
    /// Gets the interval at which deferred items are checked for re-enqueue.
    /// </summary>
    private static readonly TimeSpan DeferCheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Initializes a new instance of the <see cref="SemanticSyncOrchestrator"/> class.
    /// </summary>
    /// <param name="pipeline">The sync pipeline that processes individual sync requests.</param>
    /// <param name="budgetTracker">Optional bandwidth budget tracker for recording consumption metrics.</param>
    /// <param name="maxConcurrency">Maximum number of concurrent sync operations. Default is 4.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="pipeline"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxConcurrency"/> is less than 1.</exception>
    public SemanticSyncOrchestrator(
        SyncPipeline pipeline,
        BandwidthBudgetTracker? budgetTracker = null,
        int maxConcurrency = DefaultMaxConcurrency)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _budgetTracker = budgetTracker;

        if (maxConcurrency < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency), maxConcurrency,
                "Maximum concurrency must be at least 1.");
        }

        _concurrencyLimiter = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _requestChannel = Channel.CreateUnbounded<SyncRequest>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });

        _deferTimer = new Timer(
            CheckDeferredItems,
            state: null,
            dueTime: DeferCheckInterval,
            period: DeferCheckInterval);
    }

    /// <summary>
    /// Gets the total number of sync operations completed.
    /// </summary>
    public long TotalSyncs => Interlocked.Read(ref _totalSyncs);

    /// <summary>
    /// Gets the total bytes saved by compression/summarization (original - payload).
    /// </summary>
    public long TotalBytesSaved => Interlocked.Read(ref _totalBytesSaved);

    /// <summary>
    /// Gets the average compression ratio across all completed syncs.
    /// Returns 1.0 if no syncs have been processed.
    /// </summary>
    public double AverageCompressionRatio
    {
        get
        {
            long original = Interlocked.Read(ref _totalOriginalBytes);
            long payload = Interlocked.Read(ref _totalPayloadBytes);
            return original > 0 ? (double)payload / original : 1.0;
        }
    }

    /// <summary>
    /// Gets the number of items currently waiting in the deferred queue.
    /// </summary>
    public int DeferredCount => _deferredItems.Count;

    /// <summary>
    /// Starts the background worker loop that processes sync requests from the channel.
    /// </summary>
    public void StartAsync()
    {
        if (_workerTask is not null) return;

        _workerTask = Task.Run(() => WorkerLoopAsync(_shutdownCts.Token));
    }

    /// <summary>
    /// Stops the background worker loop and completes the request channel.
    /// Pending requests will be cancelled.
    /// </summary>
    public async Task StopAsync()
    {
        _requestChannel.Writer.TryComplete();
        _shutdownCts.Cancel();

        if (_workerTask is not null)
        {
            try
            {
                await _workerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
            }
        }
    }

    /// <summary>
    /// Submits a sync request to the orchestrator's work queue and awaits the result.
    /// </summary>
    /// <param name="dataId">Unique identifier of the data item to sync.</param>
    /// <param name="data">The raw data payload.</param>
    /// <param name="metadata">Optional metadata key-value pairs.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="SyncPipelineResult"/> from the pipeline execution.</returns>
    /// <exception cref="OperationCanceledException">Thrown when cancelled or orchestrator is shutting down.</exception>
    public async Task<SyncPipelineResult> SubmitSyncAsync(
        string dataId,
        ReadOnlyMemory<byte> data,
        IDictionary<string, string>? metadata,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        var tcs = new TaskCompletionSource<SyncPipelineResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Register cancellation to propagate to the TCS
        using var registration = ct.Register(() => tcs.TrySetCanceled(ct));

        var request = new SyncRequest(dataId, data, metadata, tcs);

        if (!_requestChannel.Writer.TryWrite(request))
        {
            throw new InvalidOperationException("Sync request channel is closed. Orchestrator may be shutting down.");
        }

        _budgetTracker?.RecordSyncQueued();

        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Background worker loop that reads sync requests from the channel and processes them
    /// with bounded concurrency using a semaphore.
    /// </summary>
    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        var activeTasks = new List<Task>();

        try
        {
            await foreach (var request in _requestChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await _concurrencyLimiter.WaitAsync(ct).ConfigureAwait(false);

                var task = ProcessRequestAsync(request, ct);
                activeTasks.Add(task);

                // Clean up completed tasks periodically
                activeTasks.RemoveAll(t => t.IsCompleted);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown
        }
        finally
        {
            // Wait for in-flight tasks to complete
            if (activeTasks.Count > 0)
            {
                try
                {
                    await Task.WhenAll(activeTasks).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort: don't throw during shutdown
                }
            }
        }
    }

    /// <summary>
    /// Processes a single sync request through the pipeline and completes its TCS.
    /// </summary>
    private async Task ProcessRequestAsync(SyncRequest request, CancellationToken ct)
    {
        try
        {
            var result = await _pipeline.ExecuteAsync(request.DataId, request.Data, request.Metadata, ct)
                .ConfigureAwait(false);

            // Handle deferred results
            if (result.Status == SyncPipelineStatus.Deferred && result.Decision.DeferUntil.HasValue)
            {
                var deferUntil = DateTimeOffset.UtcNow.Add(result.Decision.DeferUntil.Value);
                _deferredItems.Enqueue((request, deferUntil));
                request.Completion.TrySetResult(result);
            }
            else
            {
                // Record metrics for completed syncs
                RecordMetrics(result);
                request.Completion.TrySetResult(result);
            }
        }
        catch (OperationCanceledException ex)
        {
            request.Completion.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            request.Completion.TrySetException(ex);
        }
        finally
        {
            _concurrencyLimiter.Release();
            _budgetTracker?.RecordSyncCompleted();
        }
    }

    /// <summary>
    /// Records bandwidth consumption and compression metrics for a completed sync.
    /// </summary>
    private void RecordMetrics(SyncPipelineResult result)
    {
        if (result.Status != SyncPipelineStatus.Ready) return;

        Interlocked.Increment(ref _totalSyncs);
        Interlocked.Add(ref _totalOriginalBytes, result.OriginalSizeBytes);
        Interlocked.Add(ref _totalPayloadBytes, result.PayloadSizeBytes);

        long saved = result.OriginalSizeBytes - result.PayloadSizeBytes;
        if (saved > 0)
        {
            Interlocked.Add(ref _totalBytesSaved, saved);
        }

        // Record consumption to budget tracker
        if (_budgetTracker is not null)
        {
            _budgetTracker.RecordConsumption(result.Decision.Fidelity, result.PayloadSizeBytes);
        }
    }

    /// <summary>
    /// Timer callback that checks deferred items and re-enqueues those whose defer time has expired.
    /// </summary>
    private void CheckDeferredItems(object? state)
    {
        if (_disposed) return;

        var now = DateTimeOffset.UtcNow;
        int count = _deferredItems.Count;

        for (int i = 0; i < count; i++)
        {
            if (!_deferredItems.TryDequeue(out var item)) break;

            if (now >= item.DeferUntil)
            {
                // Re-enqueue for processing
                _requestChannel.Writer.TryWrite(item.Request);
            }
            else
            {
                // Not ready yet, put back in queue
                _deferredItems.Enqueue(item);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _shutdownCts.Cancel();
        _requestChannel.Writer.TryComplete();
        _deferTimer.Dispose();
        _concurrencyLimiter.Dispose();
        _shutdownCts.Dispose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        await StopAsync().ConfigureAwait(false);

        _disposed = true;
        await _deferTimer.DisposeAsync().ConfigureAwait(false);
        _concurrencyLimiter.Dispose();
        _shutdownCts.Dispose();
    }
}
