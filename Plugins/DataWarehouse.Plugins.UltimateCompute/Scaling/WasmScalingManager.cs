using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Persistence;
using DataWarehouse.SDK.Contracts.Scaling;

namespace DataWarehouse.Plugins.UltimateCompute.Scaling;

/// <summary>
/// Configuration for a single WASM module specifying per-module resource limits.
/// </summary>
/// <param name="ModuleId">Unique identifier for the WASM module.</param>
/// <param name="MaxPages">Maximum linear memory pages (64 KiB each). Default 256 (16 MB), max 65536 (4 GB).</param>
/// <param name="MaxPoolSize">Maximum warm instances to keep in the pool for this module type. Default 10.</param>
/// <param name="PersistState">Whether to persist module linear memory on suspension. Default false.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 88-10: Per-module WASM configuration")]
public sealed record WasmModuleConfig(
    string ModuleId,
    int MaxPages = 256,
    int MaxPoolSize = 10,
    bool PersistState = false)
{
    /// <summary>Minimum allowed MaxPages value.</summary>
    public const int MinPages = 1;

    /// <summary>Maximum allowed MaxPages value (65536 pages = 4 GB linear memory).</summary>
    public const int MaxPagesLimit = 65_536;
}

/// <summary>
/// Represents a pre-initialized WASM module instance that can be reused from the warm pool.
/// </summary>
/// <param name="ModuleId">The module type this instance belongs to.</param>
/// <param name="InstanceId">Unique identifier for this specific instance.</param>
/// <param name="CreatedAtUtc">When this instance was created.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 88-10: Warm WASM instance for pool reuse")]
public sealed record WasmInstance(
    string ModuleId,
    string InstanceId,
    DateTime CreatedAtUtc)
{
    /// <summary>
    /// Whether this instance has been reset and is ready for reuse.
    /// </summary>
    public bool IsReady { get; set; } = true;
}

/// <summary>
/// Manages WASM runtime scaling with configurable per-module limits, dynamic execution
/// concurrency based on system load, module state persistence, and warm instance pooling.
/// Implements <see cref="IScalableSubsystem"/> for unified scaling infrastructure participation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per-module MaxPages:</b> Replaces hardcoded 256 page limit with per-module configuration
/// stored in <see cref="BoundedCache{TKey,TValue}"/>. Each module can request specific limits
/// via metadata. Configuration persists via <see cref="IPersistentBackingStore"/>.
/// </para>
/// <para>
/// <b>Dynamic MaxConcurrentExecutions:</b> Monitors system load (CPU via
/// <see cref="Environment.ProcessorCount"/> and thread pool stats, memory via
/// <see cref="GC.GetGCMemoryInfo()"/>). Adjusts concurrency using exponential moving average
/// for smooth transitions: low load (&lt;50% CPU) increases up to 2x ProcessorCount;
/// high load (&gt;80% CPU) decreases down to ProcessorCount/2.
/// </para>
/// <para>
/// <b>Module state persistence:</b> Serializes WASM module linear memory to
/// <see cref="IPersistentBackingStore"/> on suspension. Key format:
/// <c>dw://internal/wasm-state/{moduleId}</c>. Only persists if module opts in via
/// <see cref="WasmModuleConfig.PersistState"/>.
/// </para>
/// <para>
/// <b>Warm instance pooling:</b> Maintains pool of pre-initialized instances per module type
/// using <see cref="BoundedCache{TKey,TValue}"/> with LRU eviction of least-used module types.
/// On execution: dequeue from pool or create new. On completion: reset and enqueue back.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 88-10: WASM scaling manager with dynamic limits and instance pooling")]
public sealed class WasmScalingManager : IScalableSubsystem, IDisposable
{
    // ---- Constants ----
    private const string SubsystemName = "UltimateCompute.Wasm";
    private const int DefaultMaxPages = 256;
    private const int DynamicAdjustmentIntervalMs = 10_000;
    private const double EmaAlpha = 0.3;
    private const double LowCpuThreshold = 0.50;
    private const double HighCpuThreshold = 0.80;

    // ---- Dependencies ----
    private readonly IPersistentBackingStore? _backingStore;

    // ---- Per-module configuration cache ----
    private readonly BoundedCache<string, WasmModuleConfig> _moduleConfigCache;

    // ---- Warm instance pool: moduleId -> queue of ready instances ----
    private readonly BoundedCache<string, ConcurrentQueue<WasmInstance>> _instancePool;

    // ---- Dynamic concurrency ----
    private SemaphoreSlim _executionSemaphore;
    private volatile int _maxConcurrentExecutions;
    private double _emaCpuLoad;
    private readonly Timer _dynamicAdjustmentTimer;
    private readonly object _adjustmentLock = new();

    // ---- Scaling state ----
    // _currentLimits is always written inside _adjustmentLock or before concurrent access begins.
    private volatile ScalingLimits _currentLimits;
    private long _pendingExecutions;
    private long _totalExecutions;
    private long _poolHits;
    private long _poolMisses;

    // ---- Disposal ----
    private bool _disposed;

    /// <summary>
    /// Initializes a new <see cref="WasmScalingManager"/> with optional backing store and scaling limits.
    /// </summary>
    /// <param name="backingStore">Optional persistent backing store for module configuration and state persistence.</param>
    /// <param name="limits">Optional initial scaling limits. Uses defaults if <c>null</c>.</param>
    public WasmScalingManager(
        IPersistentBackingStore? backingStore = null,
        ScalingLimits? limits = null)
    {
        _backingStore = backingStore;

        _currentLimits = limits ?? new ScalingLimits(
            MaxCacheEntries: 10_000,
            MaxConcurrentOperations: Environment.ProcessorCount,
            MaxQueueDepth: 500);

        // Initialize dynamic concurrency at ProcessorCount
        _maxConcurrentExecutions = _currentLimits.MaxConcurrentOperations;
        _executionSemaphore = new SemaphoreSlim(
            _maxConcurrentExecutions,
            Math.Max(_maxConcurrentExecutions, Environment.ProcessorCount * 2));

        // Per-module config cache with backing store persistence
        _moduleConfigCache = new BoundedCache<string, WasmModuleConfig>(
            new BoundedCacheOptions<string, WasmModuleConfig>
            {
                MaxEntries = Math.Min(_currentLimits.MaxCacheEntries, 10_000),
                EvictionPolicy = CacheEvictionMode.LRU,
                BackingStore = backingStore,
                BackingStorePath = "dw://internal/wasm-config",
                WriteThrough = true,
                KeyToString = static k => k,
                Serializer = static v => JsonSerializer.SerializeToUtf8Bytes(v),
                Deserializer = static d => JsonSerializer.Deserialize<WasmModuleConfig>(d)
                    ?? new WasmModuleConfig("unknown")
            });

        // Warm instance pool: LRU eviction of least-used module types
        _instancePool = new BoundedCache<string, ConcurrentQueue<WasmInstance>>(
            new BoundedCacheOptions<string, ConcurrentQueue<WasmInstance>>
            {
                MaxEntries = 1_000,
                EvictionPolicy = CacheEvictionMode.LRU
            });

        _emaCpuLoad = 0.5; // Start at mid-point

        // Start dynamic adjustment timer
        _dynamicAdjustmentTimer = new Timer(
            DynamicAdjustmentCallback,
            null,
            DynamicAdjustmentIntervalMs,
            DynamicAdjustmentIntervalMs);
    }

    // ---- Public API ----

    /// <summary>
    /// Gets or configures the per-module WASM limits. If no configuration exists for the module,
    /// returns a default configuration with <see cref="DefaultMaxPages"/> pages.
    /// </summary>
    /// <param name="moduleId">The WASM module identifier.</param>
    /// <returns>The module configuration.</returns>
    public WasmModuleConfig GetModuleConfig(string moduleId)
    {
        ArgumentException.ThrowIfNullOrEmpty(moduleId);

        var config = _moduleConfigCache.GetOrDefault(moduleId);
        if (config != null)
            return config;

        // Return default config
        return new WasmModuleConfig(moduleId, DefaultMaxPages);
    }

    /// <summary>
    /// Sets per-module configuration, persisting to the backing store if configured.
    /// Validates that MaxPages is within [1, 65536].
    /// </summary>
    /// <param name="config">The module configuration to store.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SetModuleConfigAsync(WasmModuleConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.MaxPages < WasmModuleConfig.MinPages || config.MaxPages > WasmModuleConfig.MaxPagesLimit)
            throw new ArgumentOutOfRangeException(
                nameof(config),
                $"MaxPages must be between {WasmModuleConfig.MinPages} and {WasmModuleConfig.MaxPagesLimit}.");

        await _moduleConfigCache.PutAsync(config.ModuleId, config, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the effective MaxPages for a module. Returns per-module config if set, otherwise the default.
    /// </summary>
    /// <param name="moduleId">The WASM module identifier.</param>
    /// <returns>Maximum linear memory pages allowed for this module.</returns>
    public int GetEffectiveMaxPages(string moduleId)
    {
        return GetModuleConfig(moduleId).MaxPages;
    }

    /// <summary>
    /// Acquires an execution slot, respecting the dynamic concurrency limit.
    /// Returns when a slot is available.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task AcquireExecutionSlotAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Interlocked.Increment(ref _pendingExecutions);
        await _executionSemaphore.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Releases an execution slot after a WASM execution completes.
    /// </summary>
    public void ReleaseExecutionSlot()
    {
        _executionSemaphore.Release();
        Interlocked.Decrement(ref _pendingExecutions);
        Interlocked.Increment(ref _totalExecutions);
    }

    /// <summary>
    /// Gets the current dynamically-adjusted maximum concurrent executions.
    /// </summary>
    public int MaxConcurrentExecutions => _maxConcurrentExecutions;

    /// <summary>
    /// Acquires a warm WASM instance from the pool, or returns <c>null</c> if none available.
    /// </summary>
    /// <param name="moduleId">The module type to acquire an instance for.</param>
    /// <returns>A warm <see cref="WasmInstance"/> if available; otherwise <c>null</c>.</returns>
    public WasmInstance? AcquireWarmInstance(string moduleId)
    {
        ArgumentException.ThrowIfNullOrEmpty(moduleId);

        var queue = _instancePool.GetOrDefault(moduleId);
        if (queue != null && queue.TryDequeue(out var instance))
        {
            Interlocked.Increment(ref _poolHits);
            return instance;
        }

        Interlocked.Increment(ref _poolMisses);
        return null;
    }

    /// <summary>
    /// Returns a WASM instance to the warm pool after resetting its state.
    /// If the pool for this module type is at capacity, the instance is discarded.
    /// </summary>
    /// <param name="instance">The instance to return to the pool.</param>
    public void ReturnWarmInstance(WasmInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var config = GetModuleConfig(instance.ModuleId);
        var queue = _instancePool.GetOrDefault(instance.ModuleId);

        if (queue == null)
        {
            queue = new ConcurrentQueue<WasmInstance>();
            _instancePool.Put(instance.ModuleId, queue);
        }

        // Only return to pool if under capacity
        if (queue.Count < config.MaxPoolSize)
        {
            instance.IsReady = true;
            queue.Enqueue(instance);
        }
    }

    /// <summary>
    /// Persists module linear memory state to the backing store for suspension.
    /// Only persists if the module has opted in via <see cref="WasmModuleConfig.PersistState"/>.
    /// </summary>
    /// <param name="moduleId">The module whose state to persist.</param>
    /// <param name="linearMemory">The module's linear memory bytes.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> if state was persisted; <c>false</c> if persistence is not enabled for this module.</returns>
    public async Task<bool> PersistModuleStateAsync(string moduleId, byte[] linearMemory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(moduleId);
        ArgumentNullException.ThrowIfNull(linearMemory);

        if (_backingStore == null)
            return false;

        var config = GetModuleConfig(moduleId);
        if (!config.PersistState)
            return false;

        var path = $"dw://internal/wasm-state/{moduleId}";
        await _backingStore.WriteAsync(path, linearMemory, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Restores module linear memory state from the backing store.
    /// </summary>
    /// <param name="moduleId">The module whose state to restore.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The restored linear memory bytes, or <c>null</c> if no saved state exists.</returns>
    public async Task<byte[]?> RestoreModuleStateAsync(string moduleId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(moduleId);

        if (_backingStore == null)
            return null;

        var config = GetModuleConfig(moduleId);
        if (!config.PersistState)
            return null;

        var path = $"dw://internal/wasm-state/{moduleId}";
        return await _backingStore.ReadAsync(path, ct).ConfigureAwait(false);
    }

    // ---- Dynamic Concurrency Adjustment ----

    /// <summary>
    /// Timer callback that adjusts max concurrent executions based on system load.
    /// Uses exponential moving average for smooth transitions.
    /// </summary>
    private void DynamicAdjustmentCallback(object? state)
    {
        if (_disposed) return;

        lock (_adjustmentLock)
        {
            var currentCpuLoad = EstimateCpuLoad();
            _emaCpuLoad = (EmaAlpha * currentCpuLoad) + ((1.0 - EmaAlpha) * _emaCpuLoad);

            int processorCount = Environment.ProcessorCount;
            int minConcurrency = Math.Max(processorCount / 2, 1);
            int maxConcurrency = processorCount * 2;

            int targetConcurrency;
            if (_emaCpuLoad < LowCpuThreshold)
            {
                // Low load: scale up toward 2x ProcessorCount
                double scaleFactor = 1.0 + ((LowCpuThreshold - _emaCpuLoad) / LowCpuThreshold);
                targetConcurrency = (int)(processorCount * scaleFactor);
            }
            else if (_emaCpuLoad > HighCpuThreshold)
            {
                // High load: scale down toward ProcessorCount/2
                double scaleFactor = 1.0 - ((_emaCpuLoad - HighCpuThreshold) / (1.0 - HighCpuThreshold));
                targetConcurrency = (int)(processorCount * Math.Max(scaleFactor, 0.5));
            }
            else
            {
                // Normal range: keep at ProcessorCount
                targetConcurrency = processorCount;
            }

            targetConcurrency = Math.Clamp(targetConcurrency, minConcurrency, maxConcurrency);

            if (targetConcurrency != _maxConcurrentExecutions)
            {
                _maxConcurrentExecutions = targetConcurrency;
                // Recreate semaphore with new limit
                var oldSemaphore = _executionSemaphore;
                _executionSemaphore = new SemaphoreSlim(
                    targetConcurrency,
                    Math.Max(targetConcurrency, maxConcurrency));
                oldSemaphore.Dispose();
            }
        }
    }

    /// <summary>
    /// Estimates current CPU load based on thread pool utilization and GC memory pressure.
    /// </summary>
    private static double EstimateCpuLoad()
    {
        ThreadPool.GetAvailableThreads(out int availableWorker, out _);
        ThreadPool.GetMaxThreads(out int maxWorker, out _);

        double threadPoolUtilization = maxWorker > 0
            ? 1.0 - ((double)availableWorker / maxWorker)
            : 0.5;

        var memInfo = GC.GetGCMemoryInfo();
        double memoryPressure = memInfo.TotalAvailableMemoryBytes > 0
            ? (double)memInfo.HeapSizeBytes / memInfo.TotalAvailableMemoryBytes
            : 0.0;

        // Weighted average: 70% thread pool, 30% memory pressure
        return Math.Clamp((threadPoolUtilization * 0.7) + (memoryPressure * 0.3), 0.0, 1.0);
    }

    // ---- IScalableSubsystem ----

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, object> GetScalingMetrics()
    {
        var configStats = _moduleConfigCache.GetStatistics();
        long pending = Interlocked.Read(ref _pendingExecutions);

        return new Dictionary<string, object>
        {
            ["wasm.maxConcurrentExecutions"] = _maxConcurrentExecutions,
            ["wasm.emaCpuLoad"] = _emaCpuLoad,
            ["wasm.totalExecutions"] = Interlocked.Read(ref _totalExecutions),
            ["wasm.pool.hits"] = Interlocked.Read(ref _poolHits),
            ["wasm.pool.misses"] = Interlocked.Read(ref _poolMisses),
            ["wasm.pool.hitRate"] = (Interlocked.Read(ref _poolHits) + Interlocked.Read(ref _poolMisses)) > 0
                ? (double)Interlocked.Read(ref _poolHits) / (Interlocked.Read(ref _poolHits) + Interlocked.Read(ref _poolMisses))
                : 0.0,
            ["cache.moduleConfig.size"] = configStats.ItemCount,
            ["cache.moduleConfig.hitRate"] = (configStats.Hits + configStats.Misses) > 0
                ? (double)configStats.Hits / (configStats.Hits + configStats.Misses)
                : 0.0,
            ["backpressure.queueDepth"] = pending,
            ["backpressure.state"] = CurrentBackpressureState.ToString(),
            ["concurrency.availableSlots"] = _executionSemaphore.CurrentCount
        };
    }

    /// <inheritdoc/>
    public async Task ReconfigureLimitsAsync(ScalingLimits limits, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(limits);

        lock (_adjustmentLock)
        {
            var oldLimits = _currentLimits;
            _currentLimits = limits;

            // Update max concurrent executions if changed
            if (limits.MaxConcurrentOperations != oldLimits.MaxConcurrentOperations)
            {
                _maxConcurrentExecutions = limits.MaxConcurrentOperations;
                var oldSemaphore = _executionSemaphore;
                _executionSemaphore = new SemaphoreSlim(
                    limits.MaxConcurrentOperations,
                    Math.Max(limits.MaxConcurrentOperations, Environment.ProcessorCount * 2));
                oldSemaphore.Dispose();
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public ScalingLimits CurrentLimits => _currentLimits;

    /// <inheritdoc/>
    public BackpressureState CurrentBackpressureState
    {
        get
        {
            long pending = Interlocked.Read(ref _pendingExecutions);
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

        _dynamicAdjustmentTimer.Dispose();
        _moduleConfigCache.Dispose();
        _instancePool.Dispose();
        _executionSemaphore.Dispose();
    }
}
