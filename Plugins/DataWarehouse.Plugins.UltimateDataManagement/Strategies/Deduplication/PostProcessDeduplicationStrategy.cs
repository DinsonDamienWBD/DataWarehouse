using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Deduplication;

/// <summary>
/// Background batch deduplication strategy with scheduling support.
/// Writes data immediately and performs deduplication in background batches.
/// </summary>
/// <remarks>
/// Features:
/// - Zero write latency (dedup happens in background)
/// - Configurable batch size and scheduling
/// - Automatic background processing
/// - Space reclamation after deduplication
/// - Progress tracking and statistics
/// </remarks>
public sealed class PostProcessDeduplicationStrategy : DeduplicationStrategyBase
{
    private readonly BoundedDictionary<string, PendingData> _pendingDedup = new BoundedDictionary<string, PendingData>(1000);
    private readonly BoundedDictionary<string, byte[]> _dataStore = new BoundedDictionary<string, byte[]>(1000);
    private readonly ConcurrentQueue<string> _dedupQueue = new();
    private readonly Timer _batchTimer;
    private readonly SemaphoreSlim _batchLock = new(1, 1);
    private readonly int _batchSize;
    private readonly TimeSpan _batchInterval;
    private volatile bool _isProcessing;

    /// <summary>
    /// Initializes with default batch size of 100 and 30-second interval.
    /// </summary>
    public PostProcessDeduplicationStrategy() : this(100, TimeSpan.FromSeconds(30)) { }

    /// <summary>
    /// Initializes with specified batch size and interval.
    /// </summary>
    /// <param name="batchSize">Number of items to process per batch.</param>
    /// <param name="batchInterval">Time between batch processing runs.</param>
    public PostProcessDeduplicationStrategy(int batchSize, TimeSpan batchInterval)
    {
        if (batchSize < 1)
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be at least 1");

        _batchSize = batchSize;
        _batchInterval = batchInterval;
        _batchTimer = new Timer(ProcessBatchCallback, null, batchInterval, batchInterval);
    }

    /// <inheritdoc/>
    public override string StrategyId => "dedup.postprocess";

    /// <inheritdoc/>
    public override string DisplayName => "Post-Process Deduplication";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = false,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 1_000_000,
        TypicalLatencyMs = 0.1
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Background batch deduplication that writes data immediately and deduplicates in scheduled background jobs. " +
        "Provides zero-latency writes with eventual space savings through batch processing. " +
        "Ideal for high-throughput workloads where immediate deduplication would impact performance.";

    /// <inheritdoc/>
    public override string[] Tags => ["deduplication", "batch", "background", "scheduled", "post-process"];

    /// <summary>
    /// Gets the count of items pending deduplication.
    /// </summary>
    public int PendingCount => _dedupQueue.Count;

    /// <summary>
    /// Gets whether batch processing is currently running.
    /// </summary>
    public bool IsProcessing => _isProcessing;

    /// <inheritdoc/>
    protected override async Task<DeduplicationResult> DeduplicateCoreAsync(
        Stream data,
        DeduplicationContext context,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // Read data
        using var memoryStream = new MemoryStream(65536);
        await data.CopyToAsync(memoryStream, ct);
        var dataBytes = memoryStream.ToArray();
        var originalSize = dataBytes.Length;

        // Compute hash for tracking
        var hash = ComputeHash(dataBytes);
        var hashString = HashToString(hash);

        // Store data immediately
        _dataStore[context.ObjectId] = dataBytes;

        // Queue for background deduplication
        var pending = new PendingData
        {
            ObjectId = context.ObjectId,
            Hash = hash,
            HashString = hashString,
            Size = originalSize,
            QueuedAt = DateTime.UtcNow
        };

        _pendingDedup[context.ObjectId] = pending;
        _dedupQueue.Enqueue(context.ObjectId);

        sw.Stop();

        // Return immediately - dedup happens in background
        return DeduplicationResult.Unique(hash, originalSize, originalSize, 1, 0, sw.Elapsed);
    }

    /// <summary>
    /// Manually triggers batch processing.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of items processed.</returns>
    public async Task<int> ProcessBatchAsync(CancellationToken ct = default)
    {
        if (!await _batchLock.WaitAsync(0, ct))
            return 0; // Already processing

        try
        {
            _isProcessing = true;
            return await ProcessBatchInternalAsync(ct);
        }
        finally
        {
            _isProcessing = false;
            _batchLock.Release();
        }
    }

    private void ProcessBatchCallback(object? state)
    {
        if (!_batchLock.Wait(0))
            return; // Already processing

        try
        {
            _isProcessing = true;
            // P2-2415: ProcessBatchInternalAsync is synchronous (returns Task.FromResult).
            // Use .GetAwaiter().GetResult() only because there is no SynchronizationContext
            // on a timer callback thread. This is safe and avoids spawning another Task.
            var t = ProcessBatchInternalAsync(CancellationToken.None);
            // The task is already completed; GetResult() does NOT block.
            _ = t.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PostProcessDedup] Batch processing error: {ex}");
        }
        finally
        {
            _isProcessing = false;
            _batchLock.Release();
        }
    }

    private Task<int> ProcessBatchInternalAsync(CancellationToken ct)
    {
        var processed = 0;
        var toProcess = new List<PendingData>();

        // Collect batch
        while (processed < _batchSize && _dedupQueue.TryDequeue(out var objectId))
        {
            if (_pendingDedup.TryRemove(objectId, out var pending))
            {
                toProcess.Add(pending);
            }
            processed++;
        }

        // Process batch - identify duplicates
        foreach (var pending in toProcess)
        {
            ct.ThrowIfCancellationRequested();

            if (HashIndex.TryGetValue(pending.HashString, out var existing))
            {
                // Found duplicate - remove data and create reference
                if (existing.ObjectId != pending.ObjectId)
                {
                    _dataStore.TryRemove(pending.ObjectId, out _);
                    existing.IncrementRef();
                }
            }
            else
            {
                // First occurrence - add to index
                HashIndex[pending.HashString] = new HashEntry
                {
                    ObjectId = pending.ObjectId,
                    Size = pending.Size,
                    ReferenceCount = 1
                };
            }
        }

        return Task.FromResult(toProcess.Count);
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _batchTimer.Dispose();
        _batchLock.Dispose();
        _dataStore.Clear();
        _pendingDedup.Clear();
        HashIndex.Clear();
        return Task.CompletedTask;
    }

    private sealed class PendingData
    {
        public required string ObjectId { get; init; }
        public required byte[] Hash { get; init; }
        public required string HashString { get; init; }
        public required long Size { get; init; }
        public required DateTime QueuedAt { get; init; }
    }
}
