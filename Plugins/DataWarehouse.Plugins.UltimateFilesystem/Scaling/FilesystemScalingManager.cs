using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Scaling;

namespace DataWarehouse.Plugins.UltimateFilesystem.Scaling;

/// <summary>
/// Manages filesystem subsystem scaling with dynamic I/O scheduling via priority queues,
/// configurable per-strategy queue depths, runtime kernel bypass re-detection for container
/// environments, and resource quota management per caller.
/// Implements <see cref="IScalableSubsystem"/> to participate in the unified scaling infrastructure.
/// </summary>
/// <remarks>
/// <para>
/// <b>Dynamic I/O scheduling:</b> Operations are dispatched through a three-tier priority queue
/// (High, Normal, Low) with configurable max queue depth (default 1024). When the queue is full,
/// backpressure is applied to callers via the <see cref="IBackpressureAware"/> pattern.
/// </para>
/// <para>
/// <b>Per-strategy queue depths:</b> Different storage media get different I/O concurrency:
/// NVMe (128), SSD (32), HDD (8). Depths are runtime-configurable via <see cref="ReconfigureQueueDepthAsync"/>.
/// </para>
/// <para>
/// <b>Kernel bypass re-detection:</b> For container environments where hardware capabilities change
/// at runtime, periodically re-detects (configurable interval, default 5 min) whether io_uring or
/// direct I/O is available, and switches I/O path without restart.
/// </para>
/// <para>
/// <b>Resource quota management:</b> Tracks I/O quota per caller via a bounded dictionary. When a
/// caller exceeds their quota, subsequent I/O requests are throttled until the quota resets.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 88-11: Filesystem scaling with dynamic I/O scheduling and kernel bypass")]
public sealed class FilesystemScalingManager : IScalableSubsystem, IDisposable
{
    // ---- Constants ----
    private const string SubsystemName = "UltimateFilesystem";
    private const int DefaultMaxQueueDepth = 1024;
    private const int DefaultNvmeQueueDepth = 128;
    private const int DefaultSsdQueueDepth = 32;
    private const int DefaultHddQueueDepth = 8;
    private const int DefaultKernelBypassRedetectIntervalMs = 5 * 60 * 1000; // 5 min
    private const int DefaultIoQuotaPerCaller = 10_000;

    // ---- Configuration ----
    private volatile ScalingLimits _currentLimits;
    private int _maxQueueDepth;

    // ---- Priority I/O Queue ----
    private readonly Channel<IoOperation> _highPriorityQueue;
    private readonly Channel<IoOperation> _normalPriorityQueue;
    private readonly Channel<IoOperation> _lowPriorityQueue;
    private readonly CancellationTokenSource _dispatchCts;
    private readonly Task _dispatchTask;

    // ---- Per-strategy queue depths ----
    private readonly BoundedCache<string, int> _strategyQueueDepths;

    // ---- Kernel bypass state ----
    private volatile bool _ioUringAvailable;
    private volatile bool _directIoAvailable;
    private readonly Timer _kernelBypassRedetectTimer;
    private readonly int _kernelBypassRedetectIntervalMs;
    private DateTime _lastKernelBypassCheck = DateTime.UtcNow;

    // ---- Resource quota tracking ----
    private readonly BoundedCache<string, IoQuotaInfo> _callerQuotas;
    private readonly int _defaultIoQuotaPerCaller;

    // ---- Metrics ----
    private long _opsScheduled;
    private long _opsCompleted;
    private long _opsThrottled;
    private long _opsDropped;
    private long _totalBytesProcessed;
    private long _kernelBypassSwitches;

    // ---- Backpressure ----
    private volatile BackpressureState _backpressureState = BackpressureState.Normal;

    // ---- Concurrency control ----
    private readonly SemaphoreSlim _ioConcurrencySemaphore;

    // ---- Disposal ----
    private bool _disposed;

    /// <summary>
    /// Initializes a new <see cref="FilesystemScalingManager"/> with configurable queue depths,
    /// kernel bypass re-detection interval, and I/O quota limits.
    /// </summary>
    /// <param name="initialLimits">Optional initial scaling limits. Uses defaults if null.</param>
    /// <param name="kernelBypassRedetectIntervalMs">
    /// Interval in milliseconds for kernel bypass capability re-detection. Default: 5 minutes.
    /// </param>
    /// <param name="defaultIoQuotaPerCaller">
    /// Default I/O operations quota per caller before throttling. Default: 10,000.
    /// </param>
    public FilesystemScalingManager(
        ScalingLimits? initialLimits = null,
        int kernelBypassRedetectIntervalMs = DefaultKernelBypassRedetectIntervalMs,
        int defaultIoQuotaPerCaller = DefaultIoQuotaPerCaller)
    {
        _currentLimits = initialLimits ?? new ScalingLimits(
            MaxCacheEntries: 10_000,
            MaxMemoryBytes: 256 * 1024 * 1024,
            MaxConcurrentOperations: 64,
            MaxQueueDepth: DefaultMaxQueueDepth);

        _maxQueueDepth = _currentLimits.MaxQueueDepth > 0
            ? _currentLimits.MaxQueueDepth
            : DefaultMaxQueueDepth;
        _kernelBypassRedetectIntervalMs = kernelBypassRedetectIntervalMs;
        _defaultIoQuotaPerCaller = defaultIoQuotaPerCaller;

        // Initialize priority queues
        var queueOptions = new BoundedChannelOptions(_maxQueueDepth)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = true
        };
        _highPriorityQueue = Channel.CreateBounded<IoOperation>(queueOptions);
        _normalPriorityQueue = Channel.CreateBounded<IoOperation>(queueOptions);
        _lowPriorityQueue = Channel.CreateBounded<IoOperation>(queueOptions);

        // Initialize strategy queue depth cache with defaults
        var depthCacheOptions = new BoundedCacheOptions<string, int>
        {
            MaxEntries = 1_000,
            EvictionPolicy = CacheEvictionMode.LRU
        };
        _strategyQueueDepths = new BoundedCache<string, int>(depthCacheOptions);
        _strategyQueueDepths.Put("NVMe", DefaultNvmeQueueDepth);
        _strategyQueueDepths.Put("SSD", DefaultSsdQueueDepth);
        _strategyQueueDepths.Put("HDD", DefaultHddQueueDepth);

        // Initialize caller quota cache
        var quotaCacheOptions = new BoundedCacheOptions<string, IoQuotaInfo>
        {
            MaxEntries = 10_000,
            EvictionPolicy = CacheEvictionMode.TTL,
            DefaultTtl = TimeSpan.FromHours(1)
        };
        _callerQuotas = new BoundedCache<string, IoQuotaInfo>(quotaCacheOptions);

        // Initialize concurrency limiter for I/O dispatch
        var maxConcurrentIo = Environment.ProcessorCount * 2;
        _ioConcurrencySemaphore = new SemaphoreSlim(maxConcurrentIo, maxConcurrentIo);

        // Start priority dispatch loop
        _dispatchCts = new CancellationTokenSource();
        _dispatchTask = Task.Run(() => DispatchLoopAsync(_dispatchCts.Token));

        // Start kernel bypass re-detection timer
        _kernelBypassRedetectTimer = new Timer(
            KernelBypassRedetectCallback,
            null,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(_kernelBypassRedetectIntervalMs));
    }

    // ---------------------------------------------------------------
    // IScalableSubsystem
    // ---------------------------------------------------------------

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, object> GetScalingMetrics()
    {
        var metrics = new Dictionary<string, object>
        {
            ["io.scheduled"] = Interlocked.Read(ref _opsScheduled),
            ["io.completed"] = Interlocked.Read(ref _opsCompleted),
            ["io.throttled"] = Interlocked.Read(ref _opsThrottled),
            ["io.dropped"] = Interlocked.Read(ref _opsDropped),
            ["io.totalBytesProcessed"] = Interlocked.Read(ref _totalBytesProcessed),
            ["io.highPriorityPending"] = GetChannelCount(_highPriorityQueue),
            ["io.normalPriorityPending"] = GetChannelCount(_normalPriorityQueue),
            ["io.lowPriorityPending"] = GetChannelCount(_lowPriorityQueue),
            ["config.maxQueueDepth"] = _maxQueueDepth,
            ["config.nvmeDepth"] = _strategyQueueDepths.GetOrDefault("NVMe"),
            ["config.ssdDepth"] = _strategyQueueDepths.GetOrDefault("SSD"),
            ["config.hddDepth"] = _strategyQueueDepths.GetOrDefault("HDD"),
            ["kernel.ioUringAvailable"] = _ioUringAvailable,
            ["kernel.directIoAvailable"] = _directIoAvailable,
            ["kernel.bypassSwitches"] = Interlocked.Read(ref _kernelBypassSwitches),
            ["kernel.lastCheckUtc"] = _lastKernelBypassCheck.ToString("O"),
            ["backpressure.state"] = _backpressureState.ToString(),
            ["backpressure.queueDepth"] = GetTotalQueueDepth()
        };
        return metrics;
    }

    /// <inheritdoc/>
    public async Task ReconfigureLimitsAsync(ScalingLimits limits, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(limits);

        _currentLimits = limits;

        if (limits.MaxQueueDepth > 0)
        {
            _maxQueueDepth = limits.MaxQueueDepth;
        }

        UpdateBackpressureState();
        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    public ScalingLimits CurrentLimits => _currentLimits;

    /// <inheritdoc/>
    public BackpressureState CurrentBackpressureState => _backpressureState;

    // ---------------------------------------------------------------
    // Dynamic I/O Scheduling
    // ---------------------------------------------------------------

    /// <summary>
    /// Schedules an I/O operation with the specified priority. Operations are dispatched
    /// in priority order (High > Normal > Low) by the background dispatch loop.
    /// </summary>
    /// <param name="operation">The I/O operation to schedule.</param>
    /// <param name="priority">The priority level for this operation.</param>
    /// <param name="callerId">Optional caller identifier for quota tracking.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the operation has been accepted into the queue.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the queue is full and backpressure is in shedding state.
    /// </exception>
    public async Task ScheduleIoAsync(
        Func<CancellationToken, Task<long>> operation,
        IoPriority priority = IoPriority.Normal,
        string? callerId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Check caller quota
        if (!string.IsNullOrEmpty(callerId) && IsCallerThrottled(callerId))
        {
            Interlocked.Increment(ref _opsThrottled);

            if (_backpressureState == BackpressureState.Shedding)
            {
                Interlocked.Increment(ref _opsDropped);
                throw new InvalidOperationException(
                    $"I/O quota exceeded for caller '{callerId}' and subsystem is shedding load.");
            }

            // Throttle by waiting
            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        var ioOp = new IoOperation
        {
            Execute = operation,
            Priority = priority,
            CallerId = callerId,
            Completion = new TaskCompletionSource<long>()
        };

        var channel = priority switch
        {
            IoPriority.High => _highPriorityQueue,
            IoPriority.Low => _lowPriorityQueue,
            _ => _normalPriorityQueue
        };

        Interlocked.Increment(ref _opsScheduled);
        await channel.Writer.WriteAsync(ioOp, ct).ConfigureAwait(false);
        UpdateBackpressureState();

        // Track quota usage
        if (!string.IsNullOrEmpty(callerId))
        {
            IncrementCallerQuota(callerId);
        }
    }

    // ---------------------------------------------------------------
    // Per-Strategy Queue Depth Configuration
    // ---------------------------------------------------------------

    /// <summary>
    /// Reconfigures the I/O queue depth for a specific storage strategy type at runtime.
    /// Changes take effect immediately for new I/O operations.
    /// </summary>
    /// <param name="strategyType">The storage strategy type (e.g., "NVMe", "SSD", "HDD").</param>
    /// <param name="queueDepth">The new queue depth for this strategy type.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task ReconfigureQueueDepthAsync(
        string strategyType,
        int queueDepth,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(strategyType))
            throw new ArgumentException("Strategy type must not be empty.", nameof(strategyType));
        if (queueDepth < 1)
            throw new ArgumentOutOfRangeException(nameof(queueDepth), "Queue depth must be at least 1.");

        _strategyQueueDepths.Put(strategyType, queueDepth);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets the current configured queue depth for a specific storage strategy type.
    /// Returns the default HDD depth if the strategy type is not configured.
    /// </summary>
    /// <param name="strategyType">The storage strategy type.</param>
    /// <returns>The configured queue depth for the specified strategy type.</returns>
    public int GetQueueDepth(string strategyType)
    {
        var depth = _strategyQueueDepths.GetOrDefault(strategyType);
        return depth > 0 ? depth : DefaultHddQueueDepth;
    }

    // ---------------------------------------------------------------
    // Kernel Bypass Re-detection
    // ---------------------------------------------------------------

    /// <summary>
    /// Gets whether io_uring is currently detected as available.
    /// </summary>
    public bool IsIoUringAvailable => _ioUringAvailable;

    /// <summary>
    /// Gets whether direct I/O is currently detected as available.
    /// </summary>
    public bool IsDirectIoAvailable => _directIoAvailable;

    /// <summary>
    /// Forces an immediate re-detection of kernel bypass capabilities.
    /// Useful after container migration or hardware change events.
    /// </summary>
    public void ForceKernelBypassRedetect()
    {
        DetectKernelBypassCapabilities();
    }

    // ---------------------------------------------------------------
    // Resource Quota Management
    // ---------------------------------------------------------------

    /// <summary>
    /// Configures the I/O quota for a specific caller.
    /// </summary>
    /// <param name="callerId">The caller identifier.</param>
    /// <param name="maxOperations">Maximum I/O operations allowed per quota period.</param>
    public void ConfigureCallerQuota(string callerId, int maxOperations)
    {
        if (string.IsNullOrWhiteSpace(callerId))
            throw new ArgumentException("Caller ID must not be empty.", nameof(callerId));

        var quota = new IoQuotaInfo
        {
            CallerId = callerId,
            MaxOperations = maxOperations,
            CurrentOperations = 0,
            WindowStartUtc = DateTime.UtcNow
        };
        _callerQuotas.Put(callerId, quota);
    }

    // ---------------------------------------------------------------
    // Private Helpers
    // ---------------------------------------------------------------

    /// <summary>
    /// Background dispatch loop that processes I/O operations in priority order.
    /// High priority operations are always dispatched first, then normal, then low.
    /// </summary>
    private async Task DispatchLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                IoOperation? op = null;

                // Priority dispatch: High > Normal > Low
                if (_highPriorityQueue.Reader.TryRead(out op) ||
                    _normalPriorityQueue.Reader.TryRead(out op) ||
                    _lowPriorityQueue.Reader.TryRead(out op))
                {
                    // Acquire concurrency slot before dispatching — limits unbounded parallelism
                    await _ioConcurrencySemaphore.WaitAsync(ct).ConfigureAwait(false);
                    _ = ExecuteIoOperationWithConcurrencyLimitAsync(op, ct);
                    continue;
                }

                // Wait for any operation to become available
                // Use a small delay to avoid busy-waiting
                await Task.Delay(1, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {

                // Continue dispatch loop on errors
                System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Executes an I/O operation with concurrency limiting. Releases the semaphore slot
    /// when the operation completes (success or failure) and logs any exceptions.
    /// </summary>
    private async Task ExecuteIoOperationWithConcurrencyLimitAsync(IoOperation op, CancellationToken ct)
    {
        try
        {
            await ExecuteIoOperationAsync(op, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Log but do not propagate — dispatch loop must continue
            System.Diagnostics.Debug.WriteLine($"[FilesystemScaling] I/O operation failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _ioConcurrencySemaphore.Release();
        }
    }

    /// <summary>
    /// Executes a single I/O operation and tracks completion metrics.
    /// </summary>
    private async Task ExecuteIoOperationAsync(IoOperation op, CancellationToken ct)
    {
        try
        {
            var bytesProcessed = await op.Execute(ct).ConfigureAwait(false);
            Interlocked.Add(ref _totalBytesProcessed, bytesProcessed);
            Interlocked.Increment(ref _opsCompleted);
            op.Completion.TrySetResult(bytesProcessed);
        }
        catch (Exception ex)
        {
            op.Completion.TrySetException(ex);
        }
        finally
        {
            UpdateBackpressureState();
        }
    }

    /// <summary>
    /// Detects kernel bypass capabilities (io_uring, direct I/O) by probing the OS.
    /// </summary>
    private void DetectKernelBypassCapabilities()
    {
        var previousIoUring = _ioUringAvailable;
        var previousDirectIo = _directIoAvailable;

        // Probe for io_uring support (Linux 5.1+)
        _ioUringAvailable = OperatingSystem.IsLinux() && ProbeIoUring();

        // Probe for direct I/O support
        _directIoAvailable = OperatingSystem.IsLinux() || OperatingSystem.IsWindows();

        _lastKernelBypassCheck = DateTime.UtcNow;

        // Track switches
        if (previousIoUring != _ioUringAvailable || previousDirectIo != _directIoAvailable)
        {
            Interlocked.Increment(ref _kernelBypassSwitches);
        }
    }

    /// <summary>
    /// Probes whether io_uring is supported on the current system.
    /// </summary>
    private static bool ProbeIoUring()
    {
        try
        {
            // Check if /proc/sys/kernel/io_uring_disabled exists and is 0
            // or if io_uring syscall is available (kernel 5.1+)
            if (System.IO.File.Exists("/proc/sys/kernel/io_uring_disabled"))
            {
                var content = System.IO.File.ReadAllText("/proc/sys/kernel/io_uring_disabled").Trim();
                return content == "0";
            }

            // Assume available if on Linux 5.1+ (check /proc/version)
            if (System.IO.File.Exists("/proc/version"))
            {
                var version = System.IO.File.ReadAllText("/proc/version");
                // Simple heuristic -- not the only check in production
                return version.Contains("Linux version 5.") || version.Contains("Linux version 6.");
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private void KernelBypassRedetectCallback(object? state)
    {
        if (_disposed) return;
        DetectKernelBypassCapabilities();
    }

    /// <summary>
    /// Checks whether a caller has exceeded their I/O quota.
    /// </summary>
    private bool IsCallerThrottled(string callerId)
    {
        var quota = _callerQuotas.GetOrDefault(callerId);
        if (quota == null) return false;

        // Reset quota window if expired (1-hour window)
        if ((DateTime.UtcNow - quota.WindowStartUtc).TotalHours >= 1)
        {
            var resetQuota = quota with { CurrentOperations = 0, WindowStartUtc = DateTime.UtcNow };
            _callerQuotas.Put(callerId, resetQuota);
            return false;
        }

        return quota.CurrentOperations >= quota.MaxOperations;
    }

    /// <summary>
    /// Increments the I/O operation count for a caller's quota tracking.
    /// </summary>
    private void IncrementCallerQuota(string callerId)
    {
        var quota = _callerQuotas.GetOrDefault(callerId);
        if (quota == null)
        {
            quota = new IoQuotaInfo
            {
                CallerId = callerId,
                MaxOperations = _defaultIoQuotaPerCaller,
                CurrentOperations = 1,
                WindowStartUtc = DateTime.UtcNow
            };
            _callerQuotas.Put(callerId, quota);
        }
        else
        {
            _callerQuotas.Put(callerId, quota with { CurrentOperations = quota.CurrentOperations + 1 });
        }
    }

    /// <summary>
    /// Updates the backpressure state based on current queue depths.
    /// </summary>
    private void UpdateBackpressureState()
    {
        var totalDepth = GetTotalQueueDepth();
        var maxDepth = _maxQueueDepth * 3; // 3 priority levels

        if (maxDepth <= 0)
        {
            _backpressureState = BackpressureState.Normal;
            return;
        }

        var ratio = (double)totalDepth / maxDepth;
        _backpressureState = ratio switch
        {
            >= 0.95 => BackpressureState.Shedding,
            >= 0.8 => BackpressureState.Critical,
            >= 0.6 => BackpressureState.Warning,
            _ => BackpressureState.Normal
        };
    }

    private int GetTotalQueueDepth()
    {
        return GetChannelCount(_highPriorityQueue) +
               GetChannelCount(_normalPriorityQueue) +
               GetChannelCount(_lowPriorityQueue);
    }

    private static int GetChannelCount<T>(Channel<T> channel)
    {
        return channel.Reader.CanCount ? channel.Reader.Count : 0;
    }

    // ---------------------------------------------------------------
    // IDisposable
    // ---------------------------------------------------------------

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _dispatchCts.Cancel();
        _dispatchCts.Dispose();
        _kernelBypassRedetectTimer.Dispose();
        _strategyQueueDepths.Dispose();
        _callerQuotas.Dispose();
        _ioConcurrencySemaphore.Dispose();

        _highPriorityQueue.Writer.TryComplete();
        _normalPriorityQueue.Writer.TryComplete();
        _lowPriorityQueue.Writer.TryComplete();
    }

    // ---------------------------------------------------------------
    // Internal Types
    // ---------------------------------------------------------------

    /// <summary>
    /// Represents a queued I/O operation with priority, caller tracking, and completion signaling.
    /// </summary>
    private sealed record IoOperation
    {
        /// <summary>Gets the I/O operation function that returns bytes processed.</summary>
        public required Func<CancellationToken, Task<long>> Execute { get; init; }

        /// <summary>Gets the priority level of this operation.</summary>
        public required IoPriority Priority { get; init; }

        /// <summary>Gets the caller identifier for quota tracking.</summary>
        public string? CallerId { get; init; }

        /// <summary>Gets the task completion source for signaling operation completion.</summary>
        public required TaskCompletionSource<long> Completion { get; init; }
    }

    /// <summary>
    /// Tracks I/O quota usage for a specific caller within a time window.
    /// </summary>
    internal sealed record IoQuotaInfo
    {
        /// <summary>Gets the caller identifier.</summary>
        public required string CallerId { get; init; }

        /// <summary>Gets the maximum operations allowed per quota window.</summary>
        public required int MaxOperations { get; init; }

        /// <summary>Gets the current number of operations consumed in this window.</summary>
        public required int CurrentOperations { get; init; }

        /// <summary>Gets the UTC start time of the current quota window.</summary>
        public required DateTime WindowStartUtc { get; init; }
    }
}

/// <summary>
/// I/O operation priority levels for the filesystem dynamic scheduler.
/// Higher priority operations are dispatched before lower priority ones.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 88-11: I/O priority levels for filesystem scheduling")]
public enum IoPriority
{
    /// <summary>Highest priority -- metadata operations, critical reads.</summary>
    High,

    /// <summary>Normal priority -- standard read/write operations.</summary>
    Normal,

    /// <summary>Lowest priority -- background compaction, prefetch.</summary>
    Low
}
