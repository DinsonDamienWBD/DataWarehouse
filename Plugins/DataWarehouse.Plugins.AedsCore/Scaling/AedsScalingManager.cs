using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Scaling;

namespace DataWarehouse.Plugins.AedsCore.Scaling;

/// <summary>
/// Manages AEDS (Asynchronous Event Distribution System) scaling with bounded LRU caches,
/// per-collection locking, partitioned job queues, and dynamically adjustable chunk parameters.
/// Implements <see cref="IScalableSubsystem"/> for centralized scaling metrics and runtime reconfiguration.
/// </summary>
/// <remarks>
/// <para>
/// Addresses DSCL-09: AEDS previously used unbounded <c>ConcurrentDictionary</c> instances and a single
/// <c>SemaphoreSlim</c> for all collections. This manager provides:
/// <list type="bullet">
///   <item><description>Bounded LRU caches via <see cref="BoundedCache{TKey,TValue}"/> replacing all raw dictionaries</description></item>
///   <item><description>Per-collection <see cref="SemaphoreSlim"/> instances for concurrent access to different collections</description></item>
///   <item><description>Partitioned job queues for parallel processing with per-partition workers</description></item>
///   <item><description>Auto-adjusting <c>MaxConcurrentChunks</c> and <c>ChunkSizeBytes</c> from system resources</description></item>
/// </list>
/// </para>
/// <para>
/// All cache sizes are configurable via <see cref="ScalingLimits"/> and can be reconfigured at runtime.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 88-07: AEDS scaling manager with bounded caches, partitioned jobs, per-collection locks")]
public sealed class AedsScalingManager : IScalableSubsystem, IDisposable
{
    /// <summary>Default maximum entries for the manifests cache.</summary>
    public const int DefaultManifestsCacheSize = 10_000;

    /// <summary>Default maximum entries for the validations cache.</summary>
    public const int DefaultValidationsCacheSize = 50_000;

    /// <summary>Default maximum entries for the jobs cache.</summary>
    public const int DefaultJobsCacheSize = 100_000;

    /// <summary>Default maximum entries for the clients cache.</summary>
    public const int DefaultClientsCacheSize = 10_000;

    /// <summary>Default maximum entries for the channels cache.</summary>
    public const int DefaultChannelsCacheSize = 1_000;

    /// <summary>Default minimum chunk size in bytes.</summary>
    public const int MinChunkSizeBytes = 4 * 1024; // 4KB

    /// <summary>Default maximum chunk size in bytes.</summary>
    public const int MaxChunkSizeBytesLimit = 16 * 1024 * 1024; // 16MB

    /// <summary>Default chunk size in bytes.</summary>
    public const int DefaultChunkSizeBytes = 64 * 1024; // 64KB

    // ---- Bounded caches (replacing ConcurrentDictionary) ----
    private BoundedCache<string, byte[]> _manifestsCache;
    private BoundedCache<string, byte[]> _validationsCache;
    private BoundedCache<string, byte[]> _jobsCache;
    private BoundedCache<string, byte[]> _clientsCache;
    private BoundedCache<string, byte[]> _channelsCache;

    // ---- Per-collection locks ----
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _collectionLocks = new();

    // ---- Partitioned job queues ----
    private readonly int _partitionCount;
    private readonly ConcurrentQueue<AedsJob>[] _jobPartitions;
    private readonly Task[] _partitionWorkers;
    private readonly CancellationTokenSource _workerCts = new();

    // ---- Dynamic chunk parameters ----
    private int _maxConcurrentChunks;
    private int _chunkSizeBytes;
    private readonly SlidingWindowThroughput _throughputTracker = new();

    // ---- Scaling ----
    private readonly object _configLock = new();
    private ScalingLimits _currentLimits;

    // ---- Metrics ----
    private long _manifestHits;
    private long _manifestMisses;
    private long _validationHits;
    private long _validationMisses;
    private long _jobsEnqueued;
    private long _jobsProcessed;
    private long _jobsFailed;
    private long _lockAcquisitions;
    private long _lockContentions;
    private long _chunksProcessed;
    private long _bytesProcessed;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="AedsScalingManager"/> class.
    /// </summary>
    /// <param name="initialLimits">Initial scaling limits. Uses defaults if <c>null</c>.</param>
    /// <param name="partitionCount">
    /// Number of job queue partitions. Defaults to <see cref="Environment.ProcessorCount"/>.
    /// </param>
    public AedsScalingManager(
        ScalingLimits? initialLimits = null,
        int? partitionCount = null)
    {
        _currentLimits = initialLimits ?? new ScalingLimits();
        _partitionCount = partitionCount ?? Environment.ProcessorCount;

        // Initialize bounded caches
        _manifestsCache = CreateCache(DefaultManifestsCacheSize);
        _validationsCache = CreateCache(DefaultValidationsCacheSize);
        _jobsCache = CreateCache(DefaultJobsCacheSize);
        _clientsCache = CreateCache(DefaultClientsCacheSize);
        _channelsCache = CreateCache(DefaultChannelsCacheSize);

        // Initialize partitioned job queues
        _jobPartitions = new ConcurrentQueue<AedsJob>[_partitionCount];
        for (int i = 0; i < _partitionCount; i++)
        {
            _jobPartitions[i] = new ConcurrentQueue<AedsJob>();
        }

        // Auto-adjust chunk parameters
        AutoAdjustChunkParameters();

        // Start partition workers
        _partitionWorkers = new Task[_partitionCount];
        for (int i = 0; i < _partitionCount; i++)
        {
            int partition = i;
            _partitionWorkers[i] = Task.Run(() => ProcessPartitionAsync(partition, _workerCts.Token));
        }
    }

    // -------------------------------------------------------------------
    // Bounded cache access (replacing ConcurrentDictionary)
    // -------------------------------------------------------------------

    /// <summary>
    /// Gets or sets a manifest entry in the bounded manifests cache.
    /// </summary>
    /// <param name="key">The manifest key.</param>
    /// <returns>The manifest data, or <c>null</c> if not found.</returns>
    public byte[]? GetManifest(string key)
    {
        var result = _manifestsCache.GetOrDefault(key);
        if (result != null) Interlocked.Increment(ref _manifestHits);
        else Interlocked.Increment(ref _manifestMisses);
        return result;
    }

    /// <summary>
    /// Stores a manifest entry in the bounded manifests cache.
    /// </summary>
    /// <param name="key">The manifest key.</param>
    /// <param name="data">The manifest data.</param>
    public void PutManifest(string key, byte[] data)
    {
        _manifestsCache.Put(key, data);
    }

    /// <summary>
    /// Gets a validation entry from the bounded validations cache.
    /// </summary>
    /// <param name="key">The validation key.</param>
    /// <returns>The validation data, or <c>null</c> if not found.</returns>
    public byte[]? GetValidation(string key)
    {
        var result = _validationsCache.GetOrDefault(key);
        if (result != null) Interlocked.Increment(ref _validationHits);
        else Interlocked.Increment(ref _validationMisses);
        return result;
    }

    /// <summary>
    /// Stores a validation entry in the bounded validations cache.
    /// </summary>
    /// <param name="key">The validation key.</param>
    /// <param name="data">The validation data.</param>
    public void PutValidation(string key, byte[] data)
    {
        _validationsCache.Put(key, data);
    }

    /// <summary>
    /// Gets a job entry from the bounded jobs cache.
    /// </summary>
    /// <param name="key">The job key.</param>
    /// <returns>The job data, or <c>null</c> if not found.</returns>
    public byte[]? GetJob(string key)
    {
        return _jobsCache.GetOrDefault(key);
    }

    /// <summary>
    /// Stores a job entry in the bounded jobs cache.
    /// </summary>
    /// <param name="key">The job key.</param>
    /// <param name="data">The job data.</param>
    public void PutJob(string key, byte[] data)
    {
        _jobsCache.Put(key, data);
    }

    /// <summary>
    /// Gets a client entry from the bounded clients cache.
    /// </summary>
    /// <param name="key">The client key.</param>
    /// <returns>The client data, or <c>null</c> if not found.</returns>
    public byte[]? GetClient(string key)
    {
        return _clientsCache.GetOrDefault(key);
    }

    /// <summary>
    /// Stores a client entry in the bounded clients cache.
    /// </summary>
    /// <param name="key">The client key.</param>
    /// <param name="data">The client data.</param>
    public void PutClient(string key, byte[] data)
    {
        _clientsCache.Put(key, data);
    }

    /// <summary>
    /// Gets a channel entry from the bounded channels cache.
    /// </summary>
    /// <param name="key">The channel key.</param>
    /// <returns>The channel data, or <c>null</c> if not found.</returns>
    public byte[]? GetChannel(string key)
    {
        return _channelsCache.GetOrDefault(key);
    }

    /// <summary>
    /// Stores a channel entry in the bounded channels cache.
    /// </summary>
    /// <param name="key">The channel key.</param>
    /// <param name="data">The channel data.</param>
    public void PutChannel(string key, byte[] data)
    {
        _channelsCache.Put(key, data);
    }

    // -------------------------------------------------------------------
    // Per-collection locks
    // -------------------------------------------------------------------

    /// <summary>
    /// Acquires the per-collection lock for the specified collection.
    /// Operations on different collections proceed concurrently.
    /// </summary>
    /// <param name="collectionName">The collection name (e.g., "manifests", "jobs", "clients").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the lock is acquired.</returns>
    public async Task AcquireCollectionLockAsync(string collectionName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        Interlocked.Increment(ref _lockAcquisitions);

        var semaphore = _collectionLocks.GetOrAdd(collectionName, _ => new SemaphoreSlim(1, 1));

        if (semaphore.CurrentCount == 0)
        {
            Interlocked.Increment(ref _lockContentions);
        }

        await semaphore.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Releases the per-collection lock for the specified collection.
    /// </summary>
    /// <param name="collectionName">The collection name.</param>
    public void ReleaseCollectionLock(string collectionName)
    {
        if (_collectionLocks.TryGetValue(collectionName, out var semaphore))
        {
            semaphore.Release();
        }
    }

    // -------------------------------------------------------------------
    // Partitioned job queues
    // -------------------------------------------------------------------

    /// <summary>
    /// Enqueues a job to the appropriate partition based on hash of the job ID.
    /// </summary>
    /// <param name="job">The job to enqueue.</param>
    public void EnqueueJob(AedsJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        int partition = GetPartitionIndex(job.JobId);
        _jobPartitions[partition].Enqueue(job);
        Interlocked.Increment(ref _jobsEnqueued);
    }

    /// <summary>
    /// Gets the current depth of each job queue partition.
    /// </summary>
    /// <returns>A read-only dictionary mapping partition index to queue depth.</returns>
    public IReadOnlyDictionary<int, int> GetPartitionDepths()
    {
        var depths = new Dictionary<int, int>();
        for (int i = 0; i < _partitionCount; i++)
        {
            depths[i] = _jobPartitions[i].Count;
        }
        return depths;
    }

    /// <summary>
    /// Gets the total number of queued jobs across all partitions.
    /// </summary>
    public int TotalQueuedJobs => _jobPartitions.Sum(p => p.Count);

    // -------------------------------------------------------------------
    // Dynamic chunk parameters
    // -------------------------------------------------------------------

    /// <summary>
    /// Gets the current maximum concurrent chunks, auto-adjusted from system resources.
    /// </summary>
    public int MaxConcurrentChunks => Volatile.Read(ref _maxConcurrentChunks);

    /// <summary>
    /// Gets the current chunk size in bytes, auto-adjusted from network bandwidth estimation.
    /// </summary>
    public int ChunkSizeBytes => Volatile.Read(ref _chunkSizeBytes);

    /// <summary>
    /// Records chunk throughput for bandwidth estimation.
    /// </summary>
    /// <param name="bytesTransferred">Number of bytes transferred.</param>
    /// <param name="elapsed">Time taken for the transfer.</param>
    public void RecordChunkThroughput(long bytesTransferred, TimeSpan elapsed)
    {
        _throughputTracker.Record(bytesTransferred, elapsed);
        Interlocked.Increment(ref _chunksProcessed);
        Interlocked.Add(ref _bytesProcessed, bytesTransferred);

        // Re-adjust chunk parameters periodically
        AutoAdjustChunkParameters();
    }

    /// <summary>
    /// Auto-adjusts <c>MaxConcurrentChunks</c> based on available memory and CPU count,
    /// and <c>ChunkSizeBytes</c> based on network bandwidth estimation.
    /// </summary>
    public void AutoAdjustChunkParameters()
    {
        // MaxConcurrentChunks: based on available memory and CPU count
        var memInfo = GC.GetGCMemoryInfo();
        long availableBytes = memInfo.TotalAvailableMemoryBytes;
        int cpuCount = Environment.ProcessorCount;

        // Reserve 10% of available memory for chunks, divide by current chunk size
        long chunkBudgetBytes = availableBytes / 10;
        int currentChunkSize = Volatile.Read(ref _chunkSizeBytes);
        if (currentChunkSize <= 0) currentChunkSize = DefaultChunkSizeBytes;

        int maxChunksFromMemory = (int)Math.Min(chunkBudgetBytes / currentChunkSize, int.MaxValue);
        int maxChunksFromCpu = cpuCount * 4; // 4 chunks per CPU core

        int newMaxChunks = Math.Max(1, Math.Min(maxChunksFromMemory, maxChunksFromCpu));
        newMaxChunks = Math.Min(newMaxChunks, _currentLimits.MaxConcurrentOperations);
        Volatile.Write(ref _maxConcurrentChunks, newMaxChunks);

        // ChunkSizeBytes: based on throughput estimation
        double avgBytesPerSec = _throughputTracker.GetAverageBytesPerSecond();
        if (avgBytesPerSec > 0)
        {
            // Target chunk processing time of ~100ms
            int newChunkSize = (int)Math.Min(avgBytesPerSec * 0.1, MaxChunkSizeBytesLimit);
            newChunkSize = Math.Max(MinChunkSizeBytes, newChunkSize);
            Volatile.Write(ref _chunkSizeBytes, newChunkSize);
        }
        else
        {
            Volatile.Write(ref _chunkSizeBytes, DefaultChunkSizeBytes);
        }
    }

    // -------------------------------------------------------------------
    // IScalableSubsystem
    // -------------------------------------------------------------------

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object> GetScalingMetrics()
    {
        var metrics = new Dictionary<string, object>
        {
            // Cache sizes
            ["aeds.manifests.size"] = _manifestsCache.Count,
            ["aeds.validations.size"] = _validationsCache.Count,
            ["aeds.jobs.size"] = _jobsCache.Count,
            ["aeds.clients.size"] = _clientsCache.Count,
            ["aeds.channels.size"] = _channelsCache.Count,

            // Cache hit rates
            ["aeds.manifests.hits"] = Interlocked.Read(ref _manifestHits),
            ["aeds.manifests.misses"] = Interlocked.Read(ref _manifestMisses),
            ["aeds.manifests.hitRate"] = ComputeHitRate(_manifestHits, _manifestMisses),
            ["aeds.validations.hits"] = Interlocked.Read(ref _validationHits),
            ["aeds.validations.misses"] = Interlocked.Read(ref _validationMisses),
            ["aeds.validations.hitRate"] = ComputeHitRate(_validationHits, _validationMisses),

            // Lock contention
            ["aeds.lockAcquisitions"] = Interlocked.Read(ref _lockAcquisitions),
            ["aeds.lockContentions"] = Interlocked.Read(ref _lockContentions),
            ["aeds.lockContentionRate"] = ComputeContentionRate(),
            ["aeds.activeCollectionLocks"] = _collectionLocks.Count,

            // Job queue metrics
            ["aeds.jobsEnqueued"] = Interlocked.Read(ref _jobsEnqueued),
            ["aeds.jobsProcessed"] = Interlocked.Read(ref _jobsProcessed),
            ["aeds.jobsFailed"] = Interlocked.Read(ref _jobsFailed),
            ["aeds.totalQueuedJobs"] = TotalQueuedJobs,
            ["aeds.partitionCount"] = _partitionCount,

            // Chunk throughput
            ["aeds.chunksProcessed"] = Interlocked.Read(ref _chunksProcessed),
            ["aeds.bytesProcessed"] = Interlocked.Read(ref _bytesProcessed),
            ["aeds.maxConcurrentChunks"] = MaxConcurrentChunks,
            ["aeds.chunkSizeBytes"] = ChunkSizeBytes,
            ["aeds.avgThroughputBytesPerSec"] = _throughputTracker.GetAverageBytesPerSecond()
        };

        // Per-partition queue depths
        var partitionDepths = new Dictionary<string, object>();
        for (int i = 0; i < _partitionCount; i++)
        {
            partitionDepths[$"partition_{i}"] = _jobPartitions[i].Count;
        }
        metrics["aeds.jobQueueDepths"] = partitionDepths;

        return metrics;
    }

    /// <inheritdoc />
    public Task ReconfigureLimitsAsync(ScalingLimits limits, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_configLock)
        {
            _currentLimits = limits;
        }

        // Recreate caches with new sizes (proportional to MaxCacheEntries)
        int baseFactor = limits.MaxCacheEntries;
        ReplaceCaches(
            manifestsSize: Math.Max(1, baseFactor),
            validationsSize: Math.Max(1, baseFactor * 5),
            jobsSize: Math.Max(1, baseFactor * 10),
            clientsSize: Math.Max(1, baseFactor),
            channelsSize: Math.Max(1, baseFactor / 10));

        // Re-adjust chunk parameters
        AutoAdjustChunkParameters();

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ScalingLimits CurrentLimits
    {
        get
        {
            lock (_configLock)
            {
                return _currentLimits;
            }
        }
    }

    /// <inheritdoc />
    public BackpressureState CurrentBackpressureState
    {
        get
        {
            int totalQueued = TotalQueuedJobs;
            if (_currentLimits.MaxQueueDepth == 0) return BackpressureState.Normal;

            double utilization = (double)totalQueued / _currentLimits.MaxQueueDepth;
            return utilization switch
            {
                >= 0.80 => BackpressureState.Critical,
                >= 0.50 => BackpressureState.Warning,
                _ => BackpressureState.Normal
            };
        }
    }

    // -------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------

    private static BoundedCache<string, byte[]> CreateCache(int maxEntries)
    {
        return new BoundedCache<string, byte[]>(new BoundedCacheOptions<string, byte[]>
        {
            MaxEntries = maxEntries,
            EvictionPolicy = CacheEvictionMode.LRU
        });
    }

    private void ReplaceCaches(int manifestsSize, int validationsSize, int jobsSize, int clientsSize, int channelsSize)
    {
        var oldManifests = _manifestsCache;
        var oldValidations = _validationsCache;
        var oldJobs = _jobsCache;
        var oldClients = _clientsCache;
        var oldChannels = _channelsCache;

        _manifestsCache = CreateCache(manifestsSize);
        _validationsCache = CreateCache(validationsSize);
        _jobsCache = CreateCache(jobsSize);
        _clientsCache = CreateCache(clientsSize);
        _channelsCache = CreateCache(channelsSize);

        // Migrate existing entries (up to new capacity)
        MigrateCache(oldManifests, _manifestsCache);
        MigrateCache(oldValidations, _validationsCache);
        MigrateCache(oldJobs, _jobsCache);
        MigrateCache(oldClients, _clientsCache);
        MigrateCache(oldChannels, _channelsCache);

        oldManifests.Dispose();
        oldValidations.Dispose();
        oldJobs.Dispose();
        oldClients.Dispose();
        oldChannels.Dispose();
    }

    private static void MigrateCache(BoundedCache<string, byte[]> source, BoundedCache<string, byte[]> target)
    {
        foreach (var kvp in source)
        {
            target.Put(kvp.Key, kvp.Value);
        }
    }

    private int GetPartitionIndex(string jobId)
    {
        // Use hash code for consistent partition routing
        int hash = jobId.GetHashCode(StringComparison.Ordinal);
        return Math.Abs(hash % _partitionCount);
    }

    private async Task ProcessPartitionAsync(int partition, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_jobPartitions[partition].TryDequeue(out var job))
            {
                try
                {
                    await job.ExecuteAsync(ct).ConfigureAwait(false);
                    Interlocked.Increment(ref _jobsProcessed);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    Interlocked.Increment(ref _jobsFailed);
                }
            }
            else
            {
                // No work available -- yield to avoid busy-spin
                try
                {
                    await Task.Delay(10, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private static double ComputeHitRate(long hits, long misses)
    {
        long total = Interlocked.Read(ref hits) + Interlocked.Read(ref misses);
        return total == 0 ? 0.0 : (double)Interlocked.Read(ref hits) / total;
    }

    private double ComputeContentionRate()
    {
        long acquisitions = Interlocked.Read(ref _lockAcquisitions);
        long contentions = Interlocked.Read(ref _lockContentions);
        return acquisitions == 0 ? 0.0 : (double)contentions / acquisitions;
    }

    /// <summary>
    /// Disposes managed resources including caches, worker tasks, and collection locks.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _workerCts.Cancel();

        try
        {
            Task.WaitAll(_partitionWorkers, TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Workers may throw on cancellation
        }

        _workerCts.Dispose();
        _manifestsCache.Dispose();
        _validationsCache.Dispose();
        _jobsCache.Dispose();
        _clientsCache.Dispose();
        _channelsCache.Dispose();

        foreach (var semaphore in _collectionLocks.Values)
        {
            semaphore.Dispose();
        }
        _collectionLocks.Clear();
    }
}

/// <summary>
/// Represents a job in the AEDS partitioned job queue.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 88-07: AEDS job for partitioned queue processing")]
public sealed class AedsJob
{
    /// <summary>Unique identifier for the job, used to determine partition routing.</summary>
    public required string JobId { get; init; }

    /// <summary>The collection this job operates on.</summary>
    public required string CollectionName { get; init; }

    /// <summary>The type of operation (e.g., Index, Validate, Transform).</summary>
    public required string OperationType { get; init; }

    /// <summary>Optional payload data for the job.</summary>
    public byte[]? Payload { get; init; }

    /// <summary>Timestamp when the job was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// The async execution delegate for this job.
    /// Set by the AEDS plugin when creating the job.
    /// </summary>
    public Func<CancellationToken, Task>? Handler { get; init; }

    /// <summary>
    /// Executes the job using the configured handler.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    internal async Task ExecuteAsync(CancellationToken ct)
    {
        if (Handler != null)
        {
            await Handler(ct).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Tracks throughput over a sliding window for bandwidth estimation.
/// Used to auto-adjust chunk size based on observed network performance.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 88-07: Sliding window throughput tracker for AEDS chunk sizing")]
internal sealed class SlidingWindowThroughput
{
    private const int WindowSize = 100;
    private readonly (long Bytes, double Seconds)[] _samples = new (long, double)[WindowSize];
    private long _index;
    private long _count;

    /// <summary>
    /// Records a throughput sample.
    /// </summary>
    /// <param name="bytes">Number of bytes transferred.</param>
    /// <param name="elapsed">Time elapsed for the transfer.</param>
    public void Record(long bytes, TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds <= 0) return;

        var idx = Interlocked.Increment(ref _index) - 1;
        _samples[idx % WindowSize] = (bytes, elapsed.TotalSeconds);
        Interlocked.Increment(ref _count);
    }

    /// <summary>
    /// Computes the average throughput in bytes per second over the sliding window.
    /// </summary>
    /// <returns>Average bytes per second, or 0 if no samples recorded.</returns>
    public double GetAverageBytesPerSecond()
    {
        long count = Interlocked.Read(ref _count);
        if (count == 0) return 0.0;

        int sampleCount = (int)Math.Min(count, WindowSize);
        double totalBytes = 0;
        double totalSeconds = 0;

        for (int i = 0; i < sampleCount; i++)
        {
            var sample = _samples[i];
            totalBytes += sample.Bytes;
            totalSeconds += sample.Seconds;
        }

        return totalSeconds > 0 ? totalBytes / totalSeconds : 0.0;
    }
}
