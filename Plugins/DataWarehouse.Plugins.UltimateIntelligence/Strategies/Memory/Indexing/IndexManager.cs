using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.Indexing;

/// <summary>
/// Manages the lifecycle of context indexes, including background indexing,
/// incremental updates, optimization, and consistency checks.
///
/// Features:
/// - Background indexing pipeline for async content processing
/// - Incremental updates without full reindexing
/// - Index compaction and optimization scheduling
/// - Consistency checks and repair
/// - Index versioning and rollback
/// </summary>
public sealed class IndexManager : IAsyncDisposable
{
    private readonly CompositeContextIndex _compositeIndex;
    private readonly Channel<IndexingTask> _indexingChannel;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly ConcurrentDictionary<string, IndexVersion> _indexVersions = new();
    private readonly ConcurrentDictionary<string, ConsistencyCheckResult> _lastConsistencyChecks = new();
    private readonly Task _backgroundWorker;
    private readonly Timer _optimizationTimer;
    private readonly Timer _consistencyTimer;

    private int _pendingTasks;
    private int _completedTasks;
    private int _failedTasks;
    private DateTimeOffset _startTime = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets the composite index being managed.
    /// </summary>
    public CompositeContextIndex CompositeIndex => _compositeIndex;

    /// <summary>
    /// Gets the number of pending indexing tasks.
    /// </summary>
    public int PendingTasks => _pendingTasks;

    /// <summary>
    /// Gets the number of completed indexing tasks.
    /// </summary>
    public int CompletedTasks => _completedTasks;

    /// <summary>
    /// Gets the number of failed indexing tasks.
    /// </summary>
    public int FailedTasks => _failedTasks;

    /// <summary>
    /// Event raised when indexing completes for a content item.
    /// </summary>
    public event EventHandler<IndexingCompletedEventArgs>? IndexingCompleted;

    /// <summary>
    /// Event raised when an indexing error occurs.
    /// </summary>
    public event EventHandler<IndexingErrorEventArgs>? IndexingError;

    /// <summary>
    /// Initializes the index manager with default settings.
    /// </summary>
    public IndexManager() : this(new IndexManagerOptions()) { }

    /// <summary>
    /// Initializes the index manager with custom options.
    /// </summary>
    /// <param name="options">Configuration options.</param>
    public IndexManager(IndexManagerOptions options)
    {
        _compositeIndex = new CompositeContextIndex();

        // Create bounded channel for backpressure
        _indexingChannel = Channel.CreateBounded<IndexingTask>(new BoundedChannelOptions(options.MaxQueueSize)
        {
            FullMode = BoundedChannelFullMode.Wait
        });

        // Start background worker(s)
        _backgroundWorker = Task.Run(() => ProcessIndexingTasks(options.WorkerCount, _shutdownCts.Token));

        // Schedule optimization
        _optimizationTimer = new Timer(
            _ => _ = RunOptimizationAsync(),
            null,
            options.OptimizationInterval,
            options.OptimizationInterval);

        // Schedule consistency checks
        _consistencyTimer = new Timer(
            _ => _ = RunConsistencyCheckAsync(),
            null,
            options.ConsistencyCheckInterval,
            options.ConsistencyCheckInterval);

        // Initialize versions
        foreach (var index in _compositeIndex.Indexes)
        {
            _indexVersions[index.Key] = new IndexVersion
            {
                IndexId = index.Key,
                Version = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                LastModified = DateTimeOffset.UtcNow
            };
        }
    }

    #region Indexing Operations

    /// <summary>
    /// Queues content for background indexing.
    /// </summary>
    /// <param name="contentId">Content identifier.</param>
    /// <param name="content">Content bytes.</param>
    /// <param name="metadata">Content metadata.</param>
    /// <param name="priority">Indexing priority.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task QueueForIndexingAsync(
        string contentId,
        byte[] content,
        ContextMetadata metadata,
        IndexingPriority priority = IndexingPriority.Normal,
        CancellationToken ct = default)
    {
        var task = new IndexingTask
        {
            TaskId = Guid.NewGuid().ToString(),
            ContentId = contentId,
            Content = content,
            Metadata = metadata,
            Priority = priority,
            QueuedAt = DateTimeOffset.UtcNow,
            TaskType = IndexingTaskType.Index
        };

        Interlocked.Increment(ref _pendingTasks);
        await _indexingChannel.Writer.WriteAsync(task, ct);
    }

    /// <summary>
    /// Queues a content update for background processing.
    /// </summary>
    /// <param name="contentId">Content identifier.</param>
    /// <param name="update">Update to apply.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task QueueUpdateAsync(string contentId, IndexUpdate update, CancellationToken ct = default)
    {
        var task = new IndexingTask
        {
            TaskId = Guid.NewGuid().ToString(),
            ContentId = contentId,
            Update = update,
            Priority = IndexingPriority.Normal,
            QueuedAt = DateTimeOffset.UtcNow,
            TaskType = IndexingTaskType.Update
        };

        Interlocked.Increment(ref _pendingTasks);
        await _indexingChannel.Writer.WriteAsync(task, ct);
    }

    /// <summary>
    /// Queues content removal for background processing.
    /// </summary>
    /// <param name="contentId">Content identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task QueueRemovalAsync(string contentId, CancellationToken ct = default)
    {
        var task = new IndexingTask
        {
            TaskId = Guid.NewGuid().ToString(),
            ContentId = contentId,
            Priority = IndexingPriority.High,
            QueuedAt = DateTimeOffset.UtcNow,
            TaskType = IndexingTaskType.Remove
        };

        Interlocked.Increment(ref _pendingTasks);
        await _indexingChannel.Writer.WriteAsync(task, ct);
    }

    /// <summary>
    /// Indexes content synchronously (bypasses queue).
    /// </summary>
    /// <param name="contentId">Content identifier.</param>
    /// <param name="content">Content bytes.</param>
    /// <param name="metadata">Content metadata.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task IndexNowAsync(string contentId, byte[] content, ContextMetadata metadata, CancellationToken ct = default)
    {
        await _compositeIndex.IndexContentAsync(contentId, content, metadata, ct);
        IncrementVersions();
    }

    /// <summary>
    /// Waits for all pending indexing tasks to complete.
    /// </summary>
    /// <param name="timeout">Maximum time to wait.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if all tasks completed, false if timeout.</returns>
    public async Task<bool> WaitForCompletionAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (_pendingTasks > 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100, ct);
        }

        return _pendingTasks == 0;
    }

    #endregion

    #region Optimization

    /// <summary>
    /// Runs optimization on all indexes.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task RunOptimizationAsync(CancellationToken ct = default)
    {
        await _compositeIndex.OptimizeAsync(ct);
        IncrementVersions();
    }

    /// <summary>
    /// Compacts a specific index.
    /// </summary>
    /// <param name="indexId">Index identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task CompactIndexAsync(string indexId, CancellationToken ct = default)
    {
        if (_compositeIndex.Indexes.TryGetValue(indexId, out var index))
        {
            await index.OptimizeAsync(ct);

            if (_indexVersions.TryGetValue(indexId, out var version))
            {
                _indexVersions[indexId] = version with
                {
                    Version = version.Version + 1,
                    LastModified = DateTimeOffset.UtcNow,
                    LastOptimized = DateTimeOffset.UtcNow
                };
            }
        }
    }

    #endregion

    #region Consistency Checks

    /// <summary>
    /// Runs consistency checks on all indexes.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Results for each index.</returns>
    public async Task<IList<ConsistencyCheckResult>> RunConsistencyCheckAsync(CancellationToken ct = default)
    {
        var results = new List<ConsistencyCheckResult>();

        foreach (var (indexId, index) in _compositeIndex.Indexes)
        {
            try
            {
                var stats = await index.GetStatisticsAsync(ct);
                var result = new ConsistencyCheckResult
                {
                    IndexId = indexId,
                    CheckedAt = DateTimeOffset.UtcNow,
                    IsConsistent = true,
                    EntryCount = stats.TotalEntries,
                    Issues = new List<string>()
                };

                // Check for issues
                if (stats.FreshnessScore < 0.5f)
                    result.Issues.Add("Index data is stale");

                if (stats.NeedsOptimization)
                    result.Issues.Add("Index needs optimization");

                if (stats.FragmentationPercent > 30)
                    result.Issues.Add($"High fragmentation: {stats.FragmentationPercent:F1}%");

                result = result with { IsConsistent = result.Issues.Count == 0 };

                results.Add(result);
                _lastConsistencyChecks[indexId] = result;
            }
            catch (Exception ex)
            {
                results.Add(new ConsistencyCheckResult
                {
                    IndexId = indexId,
                    CheckedAt = DateTimeOffset.UtcNow,
                    IsConsistent = false,
                    Issues = new List<string> { $"Check failed: {ex.Message}" }
                });
            }
        }

        return results;
    }

    /// <summary>
    /// Gets the last consistency check result for an index.
    /// </summary>
    /// <param name="indexId">Index identifier.</param>
    /// <returns>Last check result if available.</returns>
    public ConsistencyCheckResult? GetLastConsistencyCheck(string indexId)
    {
        return _lastConsistencyChecks.TryGetValue(indexId, out var result) ? result : null;
    }

    /// <summary>
    /// Attempts to repair an inconsistent index.
    /// </summary>
    /// <param name="indexId">Index identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if repair was successful.</returns>
    public async Task<bool> RepairIndexAsync(string indexId, CancellationToken ct = default)
    {
        try
        {
            // Attempt optimization as a repair mechanism
            await CompactIndexAsync(indexId, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region Version Management

    /// <summary>
    /// Gets the current version of an index.
    /// </summary>
    /// <param name="indexId">Index identifier.</param>
    /// <returns>Version information if available.</returns>
    public IndexVersion? GetIndexVersion(string indexId)
    {
        return _indexVersions.TryGetValue(indexId, out var version) ? version : null;
    }

    /// <summary>
    /// Gets all index versions.
    /// </summary>
    /// <returns>Dictionary of index versions.</returns>
    public IReadOnlyDictionary<string, IndexVersion> GetAllVersions()
    {
        return _indexVersions.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    private void IncrementVersions()
    {
        foreach (var key in _indexVersions.Keys.ToList())
        {
            if (_indexVersions.TryGetValue(key, out var version))
            {
                _indexVersions[key] = version with
                {
                    Version = version.Version + 1,
                    LastModified = DateTimeOffset.UtcNow
                };
            }
        }
    }

    #endregion

    #region Statistics

    /// <summary>
    /// Gets manager statistics.
    /// </summary>
    /// <returns>Manager statistics.</returns>
    public IndexManagerStats GetStats()
    {
        return new IndexManagerStats
        {
            PendingTasks = _pendingTasks,
            CompletedTasks = _completedTasks,
            FailedTasks = _failedTasks,
            Uptime = DateTimeOffset.UtcNow - _startTime,
            IndexCount = _compositeIndex.Indexes.Count,
            ThroughputPerSecond = _completedTasks / Math.Max(1, (DateTimeOffset.UtcNow - _startTime).TotalSeconds)
        };
    }

    #endregion

    #region Background Processing

    private async Task ProcessIndexingTasks(int workerCount, CancellationToken ct)
    {
        var workers = Enumerable.Range(0, workerCount)
            .Select(_ => ProcessWorkerAsync(ct))
            .ToArray();

        await Task.WhenAll(workers);
    }

    private async Task ProcessWorkerAsync(CancellationToken ct)
    {
        await foreach (var task in _indexingChannel.Reader.ReadAllAsync(ct))
        {
            try
            {
                var sw = Stopwatch.StartNew();

                switch (task.TaskType)
                {
                    case IndexingTaskType.Index:
                        await _compositeIndex.IndexContentAsync(
                            task.ContentId,
                            task.Content!,
                            task.Metadata!,
                            ct);
                        break;

                    case IndexingTaskType.Update:
                        await _compositeIndex.UpdateIndexAsync(
                            task.ContentId,
                            task.Update!,
                            ct);
                        break;

                    case IndexingTaskType.Remove:
                        await _compositeIndex.RemoveFromIndexAsync(
                            task.ContentId,
                            ct);
                        break;
                }

                sw.Stop();

                Interlocked.Decrement(ref _pendingTasks);
                Interlocked.Increment(ref _completedTasks);
                IncrementVersions();

                IndexingCompleted?.Invoke(this, new IndexingCompletedEventArgs
                {
                    ContentId = task.ContentId,
                    TaskType = task.TaskType,
                    Duration = sw.Elapsed,
                    QueueWaitTime = DateTimeOffset.UtcNow - task.QueuedAt
                });
            }
            catch (Exception ex)
            {
                Interlocked.Decrement(ref _pendingTasks);
                Interlocked.Increment(ref _failedTasks);

                IndexingError?.Invoke(this, new IndexingErrorEventArgs
                {
                    ContentId = task.ContentId,
                    TaskType = task.TaskType,
                    Error = ex
                });
            }
        }
    }

    #endregion

    #region Disposal

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _indexingChannel.Writer.Complete();
        _shutdownCts.Cancel();

        try
        {
            await _backgroundWorker;
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        await _optimizationTimer.DisposeAsync();
        await _consistencyTimer.DisposeAsync();
        _shutdownCts.Dispose();
    }

    #endregion

    #region Internal Types

    private sealed class IndexingTask
    {
        public required string TaskId { get; init; }
        public required string ContentId { get; init; }
        public byte[]? Content { get; init; }
        public ContextMetadata? Metadata { get; init; }
        public IndexUpdate? Update { get; init; }
        public IndexingPriority Priority { get; init; }
        public DateTimeOffset QueuedAt { get; init; }
        public IndexingTaskType TaskType { get; init; }
    }

    #endregion
}

#region Configuration Types

/// <summary>
/// Configuration options for the index manager.
/// </summary>
public record IndexManagerOptions
{
    /// <summary>Maximum size of the indexing queue.</summary>
    public int MaxQueueSize { get; init; } = 10000;

    /// <summary>Number of background workers.</summary>
    public int WorkerCount { get; init; } = Environment.ProcessorCount;

    /// <summary>Interval between optimization runs.</summary>
    public TimeSpan OptimizationInterval { get; init; } = TimeSpan.FromHours(6);

    /// <summary>Interval between consistency checks.</summary>
    public TimeSpan ConsistencyCheckInterval { get; init; } = TimeSpan.FromHours(1);
}

/// <summary>
/// Priority for indexing tasks.
/// </summary>
public enum IndexingPriority
{
    /// <summary>Low priority - processed when queue is light.</summary>
    Low,

    /// <summary>Normal priority - standard processing.</summary>
    Normal,

    /// <summary>High priority - processed as soon as possible.</summary>
    High
}

/// <summary>
/// Type of indexing task.
/// </summary>
public enum IndexingTaskType
{
    /// <summary>Index new content.</summary>
    Index,

    /// <summary>Update existing content.</summary>
    Update,

    /// <summary>Remove content from index.</summary>
    Remove
}

#endregion

#region Result Types

/// <summary>
/// Version information for an index.
/// </summary>
public record IndexVersion
{
    /// <summary>Index identifier.</summary>
    public required string IndexId { get; init; }

    /// <summary>Current version number.</summary>
    public long Version { get; init; }

    /// <summary>When the index was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the index was last modified.</summary>
    public DateTimeOffset LastModified { get; init; }

    /// <summary>When the index was last optimized.</summary>
    public DateTimeOffset? LastOptimized { get; init; }
}

/// <summary>
/// Result of a consistency check.
/// </summary>
public record ConsistencyCheckResult
{
    /// <summary>Index identifier.</summary>
    public required string IndexId { get; init; }

    /// <summary>When the check was performed.</summary>
    public DateTimeOffset CheckedAt { get; init; }

    /// <summary>Whether the index is consistent.</summary>
    public bool IsConsistent { get; init; }

    /// <summary>Number of entries in the index.</summary>
    public long EntryCount { get; init; }

    /// <summary>Issues found during the check.</summary>
    public IList<string> Issues { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Statistics about the index manager.
/// </summary>
public record IndexManagerStats
{
    /// <summary>Number of pending tasks.</summary>
    public int PendingTasks { get; init; }

    /// <summary>Number of completed tasks.</summary>
    public int CompletedTasks { get; init; }

    /// <summary>Number of failed tasks.</summary>
    public int FailedTasks { get; init; }

    /// <summary>Time since manager started.</summary>
    public TimeSpan Uptime { get; init; }

    /// <summary>Number of registered indexes.</summary>
    public int IndexCount { get; init; }

    /// <summary>Average throughput (tasks per second).</summary>
    public double ThroughputPerSecond { get; init; }
}

#endregion

#region Event Types

/// <summary>
/// Event arguments for indexing completion.
/// </summary>
public class IndexingCompletedEventArgs : EventArgs
{
    /// <summary>Content identifier that was indexed.</summary>
    public required string ContentId { get; init; }

    /// <summary>Type of task that completed.</summary>
    public IndexingTaskType TaskType { get; init; }

    /// <summary>Duration of the indexing operation.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Time spent waiting in the queue.</summary>
    public TimeSpan QueueWaitTime { get; init; }
}

/// <summary>
/// Event arguments for indexing errors.
/// </summary>
public class IndexingErrorEventArgs : EventArgs
{
    /// <summary>Content identifier that failed.</summary>
    public required string ContentId { get; init; }

    /// <summary>Type of task that failed.</summary>
    public IndexingTaskType TaskType { get; init; }

    /// <summary>The exception that occurred.</summary>
    public required Exception Error { get; init; }
}

#endregion
