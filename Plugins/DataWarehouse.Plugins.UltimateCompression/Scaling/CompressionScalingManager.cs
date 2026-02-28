// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Scaling;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateCompression.Scaling;

/// <summary>
/// Compression quality level controlling the tradeoff between speed and compression ratio.
/// Maps to algorithm-specific parameters (e.g., Zstd level 1/10/22).
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 88-12: Per-request compression quality")]
public enum CompressionQualityLevel
{
    /// <summary>Fastest compression with minimal ratio. Maps to Zstd level 1, Brotli level 1.</summary>
    Fast,

    /// <summary>Balanced tradeoff between speed and ratio. Maps to Zstd level 10, Brotli level 6.</summary>
    Balanced,

    /// <summary>Best compression ratio at the cost of speed. Maps to Zstd level 22, Brotli level 11.</summary>
    Best
}

/// <summary>
/// Result of a parallel chunk compression operation.
/// </summary>
/// <param name="CompressedChunks">Ordered list of compressed chunk data.</param>
/// <param name="OriginalSize">Total original input size in bytes.</param>
/// <param name="CompressedSize">Total compressed output size in bytes.</param>
/// <param name="ChunkCount">Number of chunks processed.</param>
/// <param name="ParallelDegree">Actual degree of parallelism used.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 88-12: Parallel compression result")]
public record ChunkCompressionResult(
    IReadOnlyList<byte[]> CompressedChunks,
    long OriginalSize,
    long CompressedSize,
    int ChunkCount,
    int ParallelDegree);

/// <summary>
/// Scaling manager for the compression subsystem implementing <see cref="IScalableSubsystem"/>.
/// Provides adaptive buffer sizing based on input size and available memory, parallel chunk
/// compression for large inputs, and per-request quality level configuration.
/// </summary>
/// <remarks>
/// <para>
/// <b>Adaptive buffer sizing:</b> Small inputs (&lt;1MB) use a buffer equal to input size.
/// Medium inputs (1MB-100MB) use min(input/4, available_ram * 0.05). Large inputs (&gt;100MB)
/// stream with fixed 4MB buffers. All thresholds are configurable via <see cref="ScalingLimits"/>.
/// </para>
/// <para>
/// <b>Parallel chunk compression:</b> For large inputs, the data is split into configurable-sized
/// chunks (default 4MB), compressed in parallel up to <c>ProcessorCount</c> concurrent tasks,
/// and reassembled in order.
/// </para>
/// <para>
/// <b>Per-request quality:</b> Each compression request can specify a <see cref="CompressionQualityLevel"/>
/// (Fast/Balanced/Best) that maps to algorithm-specific parameters.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 88-12: Compression scaling with adaptive buffers and parallel chunks")]
public sealed class CompressionScalingManager : IScalableSubsystem, IDisposable
{
    /// <summary>Default chunk size for parallel compression: 4 MB.</summary>
    public const int DefaultChunkSize = 4 * 1024 * 1024;

    /// <summary>Threshold below which input is considered small: 1 MB.</summary>
    public const long SmallInputThreshold = 1 * 1024 * 1024;

    /// <summary>Threshold above which input is considered large: 100 MB.</summary>
    public const long LargeInputThreshold = 100 * 1024 * 1024;

    /// <summary>Fixed streaming buffer size for large inputs: 4 MB.</summary>
    public const int StreamingBufferSize = 4 * 1024 * 1024;

    private readonly ILogger _logger;

    // Bounded caches for compression dictionaries and result metadata
    private readonly BoundedCache<string, byte[]> _dictionaryCache;
    private readonly BoundedCache<string, long> _compressionRatioCache;

    // Concurrency control for parallel chunk compression
    private SemaphoreSlim _chunkSemaphore;
    private ScalingLimits _currentLimits;
    private readonly object _limitsLock = new();
    private int _chunkSize;
    private CompressionQualityLevel _defaultQuality;
    private long _pendingOperations;
    private long _totalBytesProcessed;
    private long _totalBytesCompressed;
    private bool _disposed;

    /// <summary>
    /// Initializes a new <see cref="CompressionScalingManager"/> with the specified scaling limits.
    /// </summary>
    /// <param name="logger">Logger instance for diagnostics.</param>
    /// <param name="limits">Optional initial scaling limits. Uses defaults if <c>null</c>.</param>
    /// <param name="chunkSize">Chunk size in bytes for parallel compression. Default: 4 MB.</param>
    /// <param name="defaultQuality">Default compression quality level. Default: <see cref="CompressionQualityLevel.Balanced"/>.</param>
    public CompressionScalingManager(
        ILogger logger,
        ScalingLimits? limits = null,
        int chunkSize = DefaultChunkSize,
        CompressionQualityLevel defaultQuality = CompressionQualityLevel.Balanced)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _chunkSize = chunkSize > 0 ? chunkSize : DefaultChunkSize;
        _defaultQuality = defaultQuality;

        _currentLimits = limits ?? new ScalingLimits(
            MaxCacheEntries: 10_000,
            MaxConcurrentOperations: Environment.ProcessorCount);

        _chunkSemaphore = new SemaphoreSlim(
            _currentLimits.MaxConcurrentOperations,
            _currentLimits.MaxConcurrentOperations);

        _dictionaryCache = new BoundedCache<string, byte[]>(
            new BoundedCacheOptions<string, byte[]>
            {
                MaxEntries = Math.Min(_currentLimits.MaxCacheEntries, 1_000),
                EvictionPolicy = CacheEvictionMode.LRU
            });

        _compressionRatioCache = new BoundedCache<string, long>(
            new BoundedCacheOptions<string, long>
            {
                MaxEntries = Math.Min(_currentLimits.MaxCacheEntries, 50_000),
                EvictionPolicy = CacheEvictionMode.TTL,
                DefaultTtl = TimeSpan.FromMinutes(10)
            });

        _logger.LogInformation(
            "CompressionScalingManager initialized: MaxConcurrent={MaxConcurrent}, ChunkSize={ChunkSize}, DefaultQuality={Quality}",
            _currentLimits.MaxConcurrentOperations, _chunkSize, _defaultQuality);
    }

    /// <summary>
    /// Gets the bounded dictionary cache for compression dictionaries.
    /// </summary>
    public BoundedCache<string, byte[]> DictionaryCache => _dictionaryCache;

    /// <summary>
    /// Gets the bounded compression ratio cache for recently compressed data profiles.
    /// </summary>
    public BoundedCache<string, long> CompressionRatioCache => _compressionRatioCache;

    /// <summary>
    /// Gets or sets the default compression quality level for requests that do not specify one.
    /// </summary>
    public CompressionQualityLevel DefaultQuality
    {
        get => _defaultQuality;
        set => _defaultQuality = value;
    }

    /// <summary>
    /// Gets or sets the chunk size in bytes for parallel compression.
    /// </summary>
    public int ChunkSize
    {
        get => _chunkSize;
        set => _chunkSize = value > 0 ? value : DefaultChunkSize;
    }

    // ---- Adaptive Buffer Sizing ----

    /// <summary>
    /// Calculates the optimal buffer size for a given input size based on available memory.
    /// </summary>
    /// <param name="inputSize">Size of the input data in bytes.</param>
    /// <returns>The recommended buffer size in bytes.</returns>
    /// <remarks>
    /// <para>
    /// Small inputs (&lt;1MB): buffer = input size (no streaming overhead).
    /// Medium inputs (1MB-100MB): buffer = min(input/4, available_ram * 0.05).
    /// Large inputs (&gt;100MB): fixed 4MB streaming buffer.
    /// </para>
    /// </remarks>
    public int CalculateAdaptiveBufferSize(long inputSize)
    {
        if (inputSize <= 0)
            return StreamingBufferSize;

        // Small inputs: buffer equals input size
        if (inputSize < SmallInputThreshold)
            return (int)inputSize;

        // Large inputs: fixed streaming buffer
        if (inputSize > LargeInputThreshold)
            return StreamingBufferSize;

        // Medium inputs: adaptive based on input size and available RAM
        var memInfo = GC.GetGCMemoryInfo();
        long availableRam = memInfo.TotalAvailableMemoryBytes;
        long ramBudget = (long)(availableRam * 0.05);
        long inputBased = inputSize / 4;
        long selected = Math.Min(inputBased, ramBudget);

        // Clamp to reasonable bounds
        return (int)Math.Clamp(selected, 4096, _currentLimits.MaxMemoryBytes / 4);
    }

    /// <summary>
    /// Determines whether the given input size warrants parallel chunk compression.
    /// </summary>
    /// <param name="inputSize">Size of the input data in bytes.</param>
    /// <returns><c>true</c> if the input should be processed in parallel chunks; otherwise <c>false</c>.</returns>
    public bool ShouldUseParallelCompression(long inputSize)
    {
        return inputSize > _chunkSize * 2L;
    }

    /// <summary>
    /// Compresses input data in parallel chunks with concurrency control.
    /// </summary>
    /// <param name="data">The input data to compress.</param>
    /// <param name="compressChunk">
    /// A delegate that compresses a single chunk. Receives (chunk data, quality level)
    /// and returns the compressed bytes.
    /// </param>
    /// <param name="quality">
    /// Optional quality level for this request. Uses <see cref="DefaultQuality"/> if <c>null</c>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="ChunkCompressionResult"/> with the ordered compressed chunks.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> or <paramref name="compressChunk"/> is null.</exception>
    public async Task<ChunkCompressionResult> CompressParallelAsync(
        ReadOnlyMemory<byte> data,
        Func<ReadOnlyMemory<byte>, CompressionQualityLevel, byte[]> compressChunk,
        CompressionQualityLevel? quality = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(compressChunk);

        var effectiveQuality = quality ?? _defaultQuality;
        int totalLength = data.Length;

        if (totalLength == 0)
        {
            return new ChunkCompressionResult(
                Array.Empty<byte[]>(), 0, 0, 0, 0);
        }

        // Calculate chunk count and parallel degree
        int chunkCount = (totalLength + _chunkSize - 1) / _chunkSize;
        int parallelDegree = Math.Min(Environment.ProcessorCount, chunkCount);

        Interlocked.Increment(ref _pendingOperations);
        try
        {
            var results = new byte[chunkCount][];
            var tasks = new Task[chunkCount];

            for (int i = 0; i < chunkCount; i++)
            {
                int offset = i * _chunkSize;
                int length = Math.Min(_chunkSize, totalLength - offset);
                var chunk = data.Slice(offset, length);
                int chunkIndex = i;

                tasks[i] = Task.Run(async () =>
                {
                    await _chunkSemaphore.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        results[chunkIndex] = compressChunk(chunk, effectiveQuality);
                    }
                    finally
                    {
                        _chunkSemaphore.Release();
                    }
                }, ct);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);

            long compressedSize = 0;
            foreach (var r in results)
                compressedSize += r.Length;

            Interlocked.Add(ref _totalBytesProcessed, totalLength);
            Interlocked.Add(ref _totalBytesCompressed, compressedSize);

            return new ChunkCompressionResult(
                results, totalLength, compressedSize, chunkCount, parallelDegree);
        }
        finally
        {
            Interlocked.Decrement(ref _pendingOperations);
        }
    }

    /// <summary>
    /// Maps a <see cref="CompressionQualityLevel"/> to a Zstd compression level (1-22).
    /// </summary>
    /// <param name="quality">The quality level to map.</param>
    /// <returns>The Zstd compression level.</returns>
    public static int MapToZstdLevel(CompressionQualityLevel quality)
    {
        return quality switch
        {
            CompressionQualityLevel.Fast => 1,
            CompressionQualityLevel.Balanced => 10,
            CompressionQualityLevel.Best => 22,
            _ => 10
        };
    }

    /// <summary>
    /// Maps a <see cref="CompressionQualityLevel"/> to a Brotli compression level (1-11).
    /// </summary>
    /// <param name="quality">The quality level to map.</param>
    /// <returns>The Brotli compression level.</returns>
    public static int MapToBrotliLevel(CompressionQualityLevel quality)
    {
        return quality switch
        {
            CompressionQualityLevel.Fast => 1,
            CompressionQualityLevel.Balanced => 6,
            CompressionQualityLevel.Best => 11,
            _ => 6
        };
    }

    // ---- IScalableSubsystem ----

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, object> GetScalingMetrics()
    {
        var dictStats = _dictionaryCache.GetStatistics();
        var ratioStats = _compressionRatioCache.GetStatistics();
        long processed = Interlocked.Read(ref _totalBytesProcessed);
        long compressed = Interlocked.Read(ref _totalBytesCompressed);

        return new Dictionary<string, object>
        {
            ["cache.dictionary.size"] = dictStats.ItemCount,
            ["cache.dictionary.hitRate"] = (dictStats.Hits + dictStats.Misses) > 0
                ? (double)dictStats.Hits / (dictStats.Hits + dictStats.Misses)
                : 0.0,
            ["cache.ratio.size"] = ratioStats.ItemCount,
            ["compression.totalBytesProcessed"] = processed,
            ["compression.totalBytesCompressed"] = compressed,
            ["compression.overallRatio"] = processed > 0 ? (double)compressed / processed : 0.0,
            ["compression.chunkSize"] = _chunkSize,
            ["compression.defaultQuality"] = _defaultQuality.ToString(),
            ["backpressure.queueDepth"] = Interlocked.Read(ref _pendingOperations),
            ["backpressure.state"] = CurrentBackpressureState.ToString(),
            ["concurrency.maxOperations"] = _currentLimits.MaxConcurrentOperations,
            ["concurrency.availableSlots"] = _chunkSemaphore.CurrentCount
        };
    }

    /// <inheritdoc/>
    public async Task ReconfigureLimitsAsync(ScalingLimits limits, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(limits);

        SemaphoreSlim? oldSemaphore = null;

        lock (_limitsLock)
        {
            var oldLimits = _currentLimits;
            _currentLimits = limits;

            if (limits.MaxConcurrentOperations != oldLimits.MaxConcurrentOperations)
            {
                oldSemaphore = _chunkSemaphore;
                _chunkSemaphore = new SemaphoreSlim(
                    limits.MaxConcurrentOperations,
                    limits.MaxConcurrentOperations);
            }
        }

        oldSemaphore?.Dispose();

        _logger.LogInformation(
            "Compression scaling limits reconfigured: MaxCache={MaxCache}, MaxConcurrent={MaxConcurrent}",
            limits.MaxCacheEntries, limits.MaxConcurrentOperations);

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public ScalingLimits CurrentLimits => _currentLimits;

    /// <inheritdoc/>
    public BackpressureState CurrentBackpressureState
    {
        get
        {
            long pending = Interlocked.Read(ref _pendingOperations);
            int maxQueue = _currentLimits.MaxQueueDepth;

            if (pending <= 0) return BackpressureState.Normal;
            if (pending < maxQueue * 0.5) return BackpressureState.Normal;
            if (pending < maxQueue * 0.8) return BackpressureState.Warning;
            if (pending < maxQueue) return BackpressureState.Critical;
            return BackpressureState.Shedding;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _dictionaryCache.Dispose();
        _compressionRatioCache.Dispose();
        _chunkSemaphore.Dispose();
    }
}
