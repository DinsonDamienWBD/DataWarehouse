using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateRAID;

/// <summary>
/// Enhanced base class for RAID strategies with production-ready features:
/// - Statistics tracking
/// - Health monitoring with SMART integration
/// - Rebuild progress tracking with ETA
/// - Thread-safe operations
/// - XOR parity utilities
/// - Galois Field operations for erasure coding
/// - Disk I/O simulation infrastructure
/// </summary>
public abstract class RaidStrategyBase : IRaidStrategy, IDisposable
{
    #region Protected Fields

    protected RaidConfiguration? _config;
    protected List<VirtualDisk> _disks = new();
    protected List<VirtualDisk> _hotSpares = new();
    protected volatile RaidState _state = RaidState.Optimal;
    protected readonly object _stateLock = new();
    protected volatile bool _disposed;

    // Statistics tracking
    protected long _totalReads;
    protected long _totalWrites;
    protected long _bytesRead;
    protected long _bytesWritten;
    protected long _parityCalculations;
    protected long _reconstructionOperations;
    protected readonly Stopwatch _uptime = Stopwatch.StartNew();
    protected readonly DateTime _statsSince = DateTime.UtcNow;

    // Performance tracking
    protected readonly ConcurrentQueue<double> _readLatencies = new();
    protected readonly ConcurrentQueue<double> _writeLatencies = new();
    private const int MaxLatencySamples = 1000;

    // Rebuild tracking
    protected long _rebuildTotalBlocks;
    protected long _rebuildCompletedBlocks;
    protected DateTime _rebuildStartTime;
    protected readonly object _rebuildLock = new();

    // Intelligence integration
    protected IMessageBus? _messageBus;

    #endregion

    #region Abstract Properties

    /// <inheritdoc/>
    public abstract string StrategyId { get; }

    /// <inheritdoc/>
    public abstract string StrategyName { get; }

    /// <inheritdoc/>
    public abstract int RaidLevel { get; }

    /// <inheritdoc/>
    public abstract string Category { get; }

    /// <inheritdoc/>
    public abstract int MinimumDisks { get; }

    /// <inheritdoc/>
    public abstract int FaultTolerance { get; }

    /// <inheritdoc/>
    public abstract double StorageEfficiency { get; }

    /// <inheritdoc/>
    public abstract double ReadPerformanceMultiplier { get; }

    /// <inheritdoc/>
    public abstract double WritePerformanceMultiplier { get; }

    #endregion

    #region Virtual Properties

    /// <inheritdoc/>
    public virtual bool IsAvailable => true;

    /// <inheritdoc/>
    public virtual bool SupportsHotSpare => false;

    /// <inheritdoc/>
    public virtual bool SupportsOnlineExpansion => false;

    /// <inheritdoc/>
    public virtual bool SupportsHardwareAcceleration => false;

    /// <inheritdoc/>
    public virtual int DefaultStripeSizeBytes => 131072; // 128KB

    #endregion

    #region Initialization

    /// <inheritdoc/>
    public virtual async Task InitializeAsync(RaidConfiguration config, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(config);

        if (config.Disks.Count < MinimumDisks)
        {
            throw new ArgumentException(
                $"RAID {RaidLevel} requires at least {MinimumDisks} disks, but only {config.Disks.Count} provided.");
        }

        lock (_stateLock)
        {
            _config = config;
            _disks = new List<VirtualDisk>(config.Disks);
            _hotSpares = config.HotSpares != null ? new List<VirtualDisk>(config.HotSpares) : new();
            _state = RaidState.Optimal;
        }

        // Perform initial health check
        await PerformHealthCheckAsync(ct);
    }

    #endregion

    #region Abstract Operations

    /// <inheritdoc/>
    public abstract Task WriteAsync(long logicalBlockAddress, byte[] data, CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task<byte[]> ReadAsync(long logicalBlockAddress, int length, CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task RebuildAsync(int failedDiskIndex, IProgress<double>? progress = null, CancellationToken ct = default);

    #endregion

    #region Verification and Scrubbing

    /// <inheritdoc/>
    public virtual async Task<RaidVerificationResult> VerifyAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var result = new RaidVerificationResult
        {
            IsHealthy = true,
            TotalBlocks = CalculateTotalBlocks(),
            VerifiedBlocks = 0,
            ErrorCount = 0
        };

        var sw = Stopwatch.StartNew();

        try
        {
            lock (_stateLock)
            {
                _state = RaidState.Verifying;
            }

            // Verify each disk
            for (int i = 0; i < _disks.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var disk = _disks[i];
                if (disk.HealthStatus == DiskHealthStatus.Failed)
                {
                    result.ErrorCount++;
                    result.Errors.Add($"Disk {i} ({disk.DiskId}) is in failed state");
                    result.IsHealthy = false;
                    continue;
                }

                // Simulate verification
                await Task.Delay(10, ct);
                result.VerifiedBlocks++;
                progress?.Report((double)result.VerifiedBlocks / result.TotalBlocks);
            }
        }
        finally
        {
            lock (_stateLock)
            {
                _state = HasFailedDisks() ? RaidState.Degraded : RaidState.Optimal;
            }
            sw.Stop();
            result.Duration = sw.Elapsed;
        }

        return result;
    }

    /// <inheritdoc/>
    public virtual async Task<RaidScrubResult> ScrubAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var result = new RaidScrubResult
        {
            IsHealthy = true,
            TotalBlocks = CalculateTotalBlocks(),
            ScrubbedBlocks = 0
        };

        var sw = Stopwatch.StartNew();

        try
        {
            lock (_stateLock)
            {
                _state = RaidState.Scrubbing;
            }

            // Scrub all blocks
            for (long block = 0; block < result.TotalBlocks; block++)
            {
                ct.ThrowIfCancellationRequested();

                // Read and verify parity/redundancy
                try
                {
                    await VerifyBlockAsync(block, ct);
                    result.ScrubbedBlocks++;
                }
                catch
                {
                    result.ErrorsDetected++;

                    // Attempt to correct
                    try
                    {
                        await CorrectBlockAsync(block, ct);
                        result.ErrorsCorrected++;
                    }
                    catch
                    {
                        result.ErrorsUncorrectable++;
                        result.IsHealthy = false;
                        result.Details.Add($"Block {block}: Uncorrectable error");
                    }
                }

                if (block % 1000 == 0)
                {
                    progress?.Report((double)result.ScrubbedBlocks / result.TotalBlocks);
                }
            }
        }
        finally
        {
            lock (_stateLock)
            {
                _state = HasFailedDisks() ? RaidState.Degraded : RaidState.Optimal;
            }
            sw.Stop();
            result.Duration = sw.Elapsed;
        }

        return result;
    }

    /// <summary>
    /// Verifies integrity of a single block.
    /// </summary>
    protected virtual Task VerifyBlockAsync(long blockAddress, CancellationToken ct)
    {
        // Base implementation - override in derived classes
        return Task.CompletedTask;
    }

    /// <summary>
    /// Attempts to correct errors in a single block using redundancy.
    /// </summary>
    protected virtual Task CorrectBlockAsync(long blockAddress, CancellationToken ct)
    {
        // Base implementation - override in derived classes
        return Task.CompletedTask;
    }

    #endregion

    #region Health Monitoring

    /// <inheritdoc/>
    public virtual async Task<RaidHealthStatus> GetHealthStatusAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var status = new RaidHealthStatus
        {
            State = _state,
            UsableCapacityBytes = CalculateUsableCapacity(),
            UsedBytes = 0, // Would track actual usage in production
            DiskStatuses = new List<DiskStatus>()
        };

        foreach (var disk in _disks)
        {
            var diskStatus = new DiskStatus
            {
                DiskId = disk.DiskId,
                Health = disk.HealthStatus,
                SmartData = disk.SmartData
            };

            var ioStats = disk.DiskIO.GetStatistics();
            diskStatus.ReadErrors = ioStats.ReadErrors;
            diskStatus.WriteErrors = ioStats.WriteErrors;
            diskStatus.TemperatureCelsius = disk.SmartData?.Temperature ?? 0;

            status.DiskStatuses.Add(diskStatus);

            if (disk.HealthStatus == DiskHealthStatus.Healthy)
                status.HealthyDisks++;
            else if (disk.HealthStatus == DiskHealthStatus.Failed)
                status.FailedDisks++;
            else if (disk.HealthStatus == DiskHealthStatus.Rebuilding)
                status.RebuildingDisks++;
        }

        if (status.RebuildingDisks > 0)
        {
            lock (_rebuildLock)
            {
                status.RebuildProgress = _rebuildTotalBlocks > 0
                    ? (double)_rebuildCompletedBlocks / _rebuildTotalBlocks
                    : 0.0;

                if (status.RebuildProgress > 0)
                {
                    var elapsed = DateTime.UtcNow - _rebuildStartTime;
                    var estimatedTotal = TimeSpan.FromTicks((long)(elapsed.Ticks / status.RebuildProgress));
                    status.EstimatedRebuildTime = estimatedTotal - elapsed;
                }
            }
        }

        return await Task.FromResult(status);
    }

    /// <inheritdoc/>
    public virtual async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        var status = await GetHealthStatusAsync(ct);
        return status.State != RaidState.Failed && status.FailedDisks <= FaultTolerance;
    }

    /// <summary>
    /// Performs a comprehensive health check including SMART data.
    /// </summary>
    protected virtual async Task PerformHealthCheckAsync(CancellationToken ct)
    {
        foreach (var disk in _disks)
        {
            // Update SMART data if available
            if (disk.SmartData != null)
            {
                // Check for warning conditions
                if (disk.SmartData.ReallocatedSectorCount > 10 ||
                    disk.SmartData.PendingSectorCount > 0 ||
                    disk.SmartData.UncorrectableErrorCount > 0)
                {
                    disk.HealthStatus = DiskHealthStatus.Warning;
                }

                // Check for failure conditions
                if (disk.SmartData.HealthPercentage < 50 ||
                    disk.SmartData.ReallocatedSectorCount > 100)
                {
                    disk.HealthStatus = DiskHealthStatus.Failed;
                }
            }

            await Task.Yield();
        }
    }

    #endregion

    #region Statistics

    /// <inheritdoc/>
    public virtual Task<RaidStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var stats = new RaidStatistics
        {
            TotalReads = Interlocked.Read(ref _totalReads),
            TotalWrites = Interlocked.Read(ref _totalWrites),
            BytesRead = Interlocked.Read(ref _bytesRead),
            BytesWritten = Interlocked.Read(ref _bytesWritten),
            ParityCalculations = Interlocked.Read(ref _parityCalculations),
            ReconstructionOperations = Interlocked.Read(ref _reconstructionOperations),
            AverageReadLatencyMs = CalculateAverageLatency(_readLatencies),
            AverageWriteLatencyMs = CalculateAverageLatency(_writeLatencies),
            StatsSince = _statsSince,
            Uptime = _uptime.Elapsed
        };

        // Calculate throughput
        var uptimeSeconds = _uptime.Elapsed.TotalSeconds;
        if (uptimeSeconds > 0)
        {
            stats.ReadThroughputMBps = (stats.BytesRead / (1024.0 * 1024.0)) / uptimeSeconds;
            stats.WriteThroughputMBps = (stats.BytesWritten / (1024.0 * 1024.0)) / uptimeSeconds;
        }

        return Task.FromResult(stats);
    }

    /// <summary>
    /// Tracks latency for performance monitoring.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void TrackLatency(ConcurrentQueue<double> queue, double latencyMs)
    {
        queue.Enqueue(latencyMs);

        // Keep queue size bounded
        while (queue.Count > MaxLatencySamples)
        {
            queue.TryDequeue(out _);
        }
    }

    /// <summary>
    /// Calculates average latency from samples.
    /// </summary>
    private static double CalculateAverageLatency(ConcurrentQueue<double> samples)
    {
        if (samples.IsEmpty) return 0;
        return samples.Average();
    }

    #endregion

    #region Disk Management

    /// <inheritdoc/>
    public virtual Task AddDiskAsync(VirtualDisk disk, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!SupportsOnlineExpansion)
        {
            throw new NotSupportedException($"RAID {RaidLevel} does not support online expansion");
        }

        lock (_stateLock)
        {
            _disks.Add(disk);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public virtual Task RemoveDiskAsync(int diskIndex, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (diskIndex < 0 || diskIndex >= _disks.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(diskIndex));
        }

        if (_disks.Count - 1 < MinimumDisks)
        {
            throw new InvalidOperationException(
                $"Cannot remove disk - would fall below minimum disk count of {MinimumDisks}");
        }

        lock (_stateLock)
        {
            _disks.RemoveAt(diskIndex);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public virtual async Task ReplaceDiskAsync(int failedDiskIndex, VirtualDisk replacementDisk,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (failedDiskIndex < 0 || failedDiskIndex >= _disks.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(failedDiskIndex));
        }

        lock (_stateLock)
        {
            _disks[failedDiskIndex] = replacementDisk;
            replacementDisk.HealthStatus = DiskHealthStatus.Rebuilding;
        }

        await RebuildAsync(failedDiskIndex, progress, ct);
    }

    #endregion

    #region Parity and Erasure Coding Utilities

    /// <summary>
    /// Calculates XOR parity across multiple data blocks.
    /// Used in RAID 5, RAID 6, and other parity-based RAID levels.
    /// </summary>
    protected byte[] CalculateXorParity(params byte[][] dataBlocks)
    {
        if (dataBlocks.Length == 0)
            throw new ArgumentException("At least one data block required", nameof(dataBlocks));

        int blockSize = dataBlocks[0].Length;
        byte[] parity = new byte[blockSize];

        // XOR all blocks together
        for (int i = 0; i < blockSize; i++)
        {
            byte result = 0;
            foreach (var block in dataBlocks)
            {
                result ^= block[i];
            }
            parity[i] = result;
        }

        Interlocked.Increment(ref _parityCalculations);
        return parity;
    }

    /// <summary>
    /// Reconstructs a missing data block using XOR parity.
    /// </summary>
    protected byte[] ReconstructFromXorParity(byte[] parity, params byte[][] knownBlocks)
    {
        // XOR parity with all known blocks to recover missing block
        return CalculateXorParity(new[] { parity }.Concat(knownBlocks).ToArray());
    }

    /// <summary>
    /// Galois Field multiplication for Reed-Solomon erasure coding (RAID 6).
    /// Operates in GF(2^8).
    /// </summary>
    protected static byte GaloisMultiply(byte a, byte b)
    {
        byte result = 0;
        byte temp = a;

        for (int i = 0; i < 8; i++)
        {
            if ((b & 1) != 0)
                result ^= temp;

            bool highBitSet = (temp & 0x80) != 0;
            temp <<= 1;

            if (highBitSet)
                temp ^= 0x1D; // Primitive polynomial x^8 + x^4 + x^3 + x^2 + 1

            b >>= 1;
        }

        return result;
    }

    /// <summary>
    /// Calculates Q parity for RAID 6 using Galois Field arithmetic.
    /// </summary>
    protected byte[] CalculateQParity(params byte[][] dataBlocks)
    {
        if (dataBlocks.Length == 0)
            throw new ArgumentException("At least one data block required", nameof(dataBlocks));

        int blockSize = dataBlocks[0].Length;
        byte[] qParity = new byte[blockSize];

        // Q parity uses Galois Field multiplication
        for (int i = 0; i < blockSize; i++)
        {
            byte result = 0;
            for (int j = 0; j < dataBlocks.Length; j++)
            {
                byte coefficient = (byte)(1 << j); // g^j where g is generator
                result ^= GaloisMultiply(dataBlocks[j][i], coefficient);
            }
            qParity[i] = result;
        }

        Interlocked.Increment(ref _parityCalculations);
        return qParity;
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Calculates total logical blocks in the array.
    /// </summary>
    protected virtual long CalculateTotalBlocks()
    {
        if (_disks.Count == 0) return 0;

        var stripeSizeBytes = _config?.StripeSizeBytes ?? DefaultStripeSizeBytes;
        var totalCapacity = CalculateUsableCapacity();
        return totalCapacity / stripeSizeBytes;
    }

    /// <summary>
    /// Calculates usable capacity based on RAID level and disk count.
    /// </summary>
    protected virtual long CalculateUsableCapacity()
    {
        if (_disks.Count == 0) return 0;

        var minCapacity = _disks.Min(d => d.CapacityBytes);
        return (long)(minCapacity * _disks.Count * StorageEfficiency);
    }

    /// <summary>
    /// Checks if any disks have failed.
    /// </summary>
    protected bool HasFailedDisks()
    {
        return _disks.Any(d => d.HealthStatus == DiskHealthStatus.Failed);
    }

    /// <summary>
    /// Maps logical block address to physical disk and offset.
    /// </summary>
    protected (int diskIndex, long offset) MapBlockToDisk(long logicalBlockAddress)
    {
        var stripeSizeBytes = _config?.StripeSizeBytes ?? DefaultStripeSizeBytes;
        var stripeNumber = logicalBlockAddress / _disks.Count;
        var diskIndex = (int)(logicalBlockAddress % _disks.Count);
        var offset = stripeNumber * stripeSizeBytes;

        return (diskIndex, offset);
    }

    /// <summary>
    /// Updates rebuild progress tracking.
    /// </summary>
    protected void UpdateRebuildProgress(long completedBlocks, long totalBlocks)
    {
        lock (_rebuildLock)
        {
            _rebuildCompletedBlocks = completedBlocks;
            _rebuildTotalBlocks = totalBlocks;

            if (completedBlocks == 0)
            {
                _rebuildStartTime = DateTime.UtcNow;
            }
        }
    }

    #endregion

    #region Intelligence Integration

    /// <summary>
    /// Sets the message bus for Intelligence integration.
    /// </summary>
    /// <param name="messageBus">The message bus instance.</param>
    public void SetMessageBus(IMessageBus? messageBus)
    {
        _messageBus = messageBus;
    }

    /// <summary>
    /// Requests disk failure prediction from Intelligence.
    /// </summary>
    /// <param name="diskIndex">Index of the disk to analyze.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Failure prediction result, or null if Intelligence unavailable.</returns>
    protected async Task<DiskFailurePrediction?> RequestFailurePredictionAsync(int diskIndex, CancellationToken ct = default)
    {
        if (_messageBus == null || diskIndex < 0 || diskIndex >= _disks.Count)
            return null;

        var disk = _disks[diskIndex];
        var smartData = disk.SmartData;
        var ioStats = disk.DiskIO.GetStatistics();

        var correlationId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<DiskFailurePrediction?>();

        // Subscribe to response
        using var subscription = _messageBus.Subscribe(RaidTopics.PredictFailureResponse, msg =>
        {
            if (msg.CorrelationId == correlationId)
            {
                var prediction = new DiskFailurePrediction
                {
                    DiskIndex = diskIndex,
                    FailureProbability = msg.Payload.TryGetValue("failureProbability", out var fp) && fp is double prob ? prob : 0.0,
                    Confidence = msg.Payload.TryGetValue("confidence", out var conf) && conf is double c ? c : 0.0
                };

                if (msg.Payload.TryGetValue("estimatedTimeToFailure", out var ttf) && ttf is TimeSpan timespan)
                {
                    prediction.EstimatedTimeToFailure = timespan;
                }

                tcs.TrySetResult(prediction);
            }
            return Task.CompletedTask;
        });

        // Send request
        var request = new PluginMessage
        {
            Type = RaidTopics.PredictFailure,
            CorrelationId = correlationId,
            Source = "raid-strategy",
            Payload = new Dictionary<string, object>
            {
                ["strategyId"] = StrategyId,
                ["diskIndex"] = diskIndex,
                ["diskId"] = disk.DiskId,
                ["smartData"] = smartData ?? new SmartAttributes(),
                ["readErrors"] = ioStats.ReadErrors,
                ["writeErrors"] = ioStats.WriteErrors,
                ["temperature"] = smartData?.Temperature ?? 0
            }
        };

        await _messageBus.PublishAsync(RaidTopics.PredictFailure, request, ct);

        // Wait for response with timeout
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Requests optimal RAID level recommendation from Intelligence.
    /// </summary>
    /// <param name="workloadProfile">Workload characteristics.</param>
    /// <param name="priorityGoal">Primary optimization goal.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>RAID level recommendation, or null if Intelligence unavailable.</returns>
    protected async Task<RaidLevelRecommendation?> RequestOptimalRaidLevelAsync(
        string workloadProfile,
        string priorityGoal = "balanced",
        CancellationToken ct = default)
    {
        if (_messageBus == null)
            return null;

        var correlationId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<RaidLevelRecommendation?>();

        // Subscribe to response
        using var subscription = _messageBus.Subscribe(RaidTopics.OptimizeLevelResponse, msg =>
        {
            if (msg.CorrelationId == correlationId)
            {
                var recommendation = new RaidLevelRecommendation
                {
                    RecommendedStrategyId = msg.Payload.TryGetValue("recommendedLevel", out var level) && level is string s ? s : "",
                    Confidence = msg.Payload.TryGetValue("confidence", out var conf) && conf is double c ? c : 0.0
                };

                if (msg.Payload.TryGetValue("reasoning", out var reasoning) && reasoning is string reason)
                {
                    recommendation.Reasoning = reason;
                }

                tcs.TrySetResult(recommendation);
            }
            return Task.CompletedTask;
        });

        // Send request
        var request = new PluginMessage
        {
            Type = RaidTopics.OptimizeLevel,
            CorrelationId = correlationId,
            Source = "raid-strategy",
            Payload = new Dictionary<string, object>
            {
                ["workloadProfile"] = workloadProfile,
                ["availableDisks"] = _disks.Count,
                ["priorityGoal"] = priorityGoal,
                ["currentStrategy"] = StrategyId
            }
        };

        await _messageBus.PublishAsync(RaidTopics.OptimizeLevel, request, ct);

        // Wait for response with timeout
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reports health data to Intelligence for learning and pattern recognition.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if report was sent successfully.</returns>
    protected async Task<bool> ReportHealthToIntelligenceAsync(CancellationToken ct = default)
    {
        if (_messageBus == null)
            return false;

        try
        {
            var health = await GetHealthStatusAsync(ct);
            var stats = await GetStatisticsAsync(ct);

            var report = new PluginMessage
            {
                Type = RaidTopics.ReportHealth,
                Source = "raid-strategy",
                Payload = new Dictionary<string, object>
                {
                    ["strategyId"] = StrategyId,
                    ["timestamp"] = DateTime.UtcNow,
                    ["state"] = health.State.ToString(),
                    ["healthyDisks"] = health.HealthyDisks,
                    ["failedDisks"] = health.FailedDisks,
                    ["rebuildingDisks"] = health.RebuildingDisks,
                    ["totalReads"] = stats.TotalReads,
                    ["totalWrites"] = stats.TotalWrites,
                    ["readLatency"] = stats.AverageReadLatencyMs,
                    ["writeLatency"] = stats.AverageWriteLatencyMs,
                    ["readThroughput"] = stats.ReadThroughputMBps,
                    ["writeThroughput"] = stats.WriteThroughputMBps
                }
            };

            await _messageBus.PublishAsync(RaidTopics.ReportHealth, report, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region IDisposable

    /// <inheritdoc/>
    public virtual void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _uptime.Stop();

        GC.SuppressFinalize(this);
    }

    #endregion
}

/// <summary>
/// Result of disk failure prediction.
/// </summary>
public sealed class DiskFailurePrediction
{
    /// <summary>Index of the disk being analyzed.</summary>
    public int DiskIndex { get; set; }

    /// <summary>Probability of failure (0.0-1.0).</summary>
    public double FailureProbability { get; set; }

    /// <summary>Prediction confidence (0.0-1.0).</summary>
    public double Confidence { get; set; }

    /// <summary>Estimated time until failure.</summary>
    public TimeSpan? EstimatedTimeToFailure { get; set; }

    /// <summary>Additional recommendations.</summary>
    public List<string> Recommendations { get; set; } = new();
}

/// <summary>
/// Result of RAID level recommendation.
/// </summary>
public sealed class RaidLevelRecommendation
{
    /// <summary>Recommended RAID strategy ID.</summary>
    public string RecommendedStrategyId { get; set; } = string.Empty;

    /// <summary>Recommendation confidence (0.0-1.0).</summary>
    public double Confidence { get; set; }

    /// <summary>Explanation for recommendation.</summary>
    public string? Reasoning { get; set; }

    /// <summary>Alternative recommendations.</summary>
    public List<AlternativeRecommendation> Alternatives { get; set; } = new();
}

/// <summary>
/// Alternative RAID level recommendation.
/// </summary>
public sealed class AlternativeRecommendation
{
    /// <summary>Strategy ID.</summary>
    public string StrategyId { get; set; } = string.Empty;

    /// <summary>Confidence score.</summary>
    public double Confidence { get; set; }

    /// <summary>Brief explanation.</summary>
    public string? Reasoning { get; set; }
}
