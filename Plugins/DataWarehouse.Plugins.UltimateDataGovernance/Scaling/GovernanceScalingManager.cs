using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Persistence;
using DataWarehouse.SDK.Contracts.Scaling;

namespace DataWarehouse.Plugins.UltimateDataGovernance.Scaling;

/// <summary>
/// Manages scaling for the DataGovernance plugin with persistent policy/ownership/classification stores,
/// TTL-eviction bounded caches with stale-while-revalidate refresh, parallel strategy evaluation via
/// <see cref="SemaphoreSlim"/>-throttled <c>Task.WhenAll</c>, and full <see cref="IScalableSubsystem"/> implementation.
/// </summary>
/// <remarks>
/// <para>
/// Addresses DSCL-14: DataGovernance previously stored all policies, ownership records, and classifications
/// in unbounded in-memory dictionaries. On restart, all state was lost. Under load, memory grew without bound.
/// This manager provides:
/// <list type="bullet">
///   <item><description>Write-through <see cref="BoundedCache{TKey,TValue}"/> with TTL eviction (15 min) for policies (50K), ownerships (100K), and classifications (100K)</description></item>
///   <item><description>Stale-while-revalidate pattern: on TTL expiry, serve stale data and refresh in background to avoid latency spikes</description></item>
///   <item><description>Parallel strategy evaluation with configurable concurrency via <see cref="SemaphoreSlim"/> and <c>Task.WhenAll</c></description></item>
///   <item><description>Persistent backing via <see cref="IPersistentBackingStore"/> so state survives restarts</description></item>
/// </list>
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 88-08: Governance scaling with TTL cache, stale-while-revalidate, parallel evaluation")]
public sealed class GovernanceScalingManager : IScalableSubsystem, IDisposable
{
    /// <summary>Default maximum number of policies.</summary>
    public const int DefaultMaxPolicies = 50_000;

    /// <summary>Default maximum number of ownership records.</summary>
    public const int DefaultMaxOwnerships = 100_000;

    /// <summary>Default maximum number of classification records.</summary>
    public const int DefaultMaxClassifications = 100_000;

    /// <summary>Default TTL for cached governance entries.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(15);

    private const string BackingStorePrefix = "dw://internal/governance";

    // ---- TTL caches ----
    private readonly BoundedCache<string, byte[]> _policies;
    private readonly BoundedCache<string, byte[]> _ownerships;
    private readonly BoundedCache<string, byte[]> _classifications;

    // ---- Stale-while-revalidate tracking ----
    private readonly ConcurrentDictionary<string, DateTime> _policyRefreshTimes = new();
    private readonly ConcurrentDictionary<string, DateTime> _ownershipRefreshTimes = new();
    private readonly ConcurrentDictionary<string, DateTime> _classificationRefreshTimes = new();
    private readonly TimeSpan _ttl;

    // ---- Backing store ----
    private readonly IPersistentBackingStore? _backingStore;

    // ---- Parallel evaluation ----
    private readonly SemaphoreSlim _evaluationThrottle;
    private readonly int _maxConcurrentEvaluations;

    // ---- Scaling ----
    private readonly object _configLock = new();
    private ScalingLimits _currentLimits;

    // ---- Metrics ----
    private long _backingStoreReads;
    private long _backingStoreWrites;
    private long _evaluationsStarted;
    private long _evaluationsCompleted;
    private long _staleRefreshes;

    /// <summary>
    /// Initializes a new instance of the <see cref="GovernanceScalingManager"/> class.
    /// </summary>
    /// <param name="backingStore">
    /// Optional persistent backing store for write-through persistence.
    /// When <c>null</c>, operates in-memory only (state lost on restart).
    /// </param>
    /// <param name="initialLimits">Initial scaling limits. Uses defaults if <c>null</c>.</param>
    /// <param name="ttl">TTL for cached entries. Uses <see cref="DefaultTtl"/> if <c>null</c>.</param>
    /// <param name="maxConcurrentEvaluations">
    /// Maximum concurrent parallel strategy evaluations. Uses <c>Environment.ProcessorCount</c> if <c>null</c>.
    /// </param>
    public GovernanceScalingManager(
        IPersistentBackingStore? backingStore = null,
        ScalingLimits? initialLimits = null,
        TimeSpan? ttl = null,
        int? maxConcurrentEvaluations = null)
    {
        _backingStore = backingStore;
        _currentLimits = initialLimits ?? new ScalingLimits(MaxCacheEntries: DefaultMaxPolicies);
        _ttl = ttl ?? DefaultTtl;
        _maxConcurrentEvaluations = maxConcurrentEvaluations ?? Environment.ProcessorCount;
        _evaluationThrottle = new SemaphoreSlim(_maxConcurrentEvaluations, _maxConcurrentEvaluations);

        // Initialize policy cache (TTL, write-through)
        _policies = new BoundedCache<string, byte[]>(new BoundedCacheOptions<string, byte[]>
        {
            MaxEntries = DefaultMaxPolicies,
            EvictionPolicy = CacheEvictionMode.TTL,
            DefaultTtl = _ttl,
            BackingStore = _backingStore,
            BackingStorePath = $"{BackingStorePrefix}/policies",
            Serializer = static v => v,
            Deserializer = static v => v,
            KeyToString = static k => k,
            WriteThrough = _backingStore != null
        });

        // Initialize ownership cache (TTL, write-through)
        _ownerships = new BoundedCache<string, byte[]>(new BoundedCacheOptions<string, byte[]>
        {
            MaxEntries = DefaultMaxOwnerships,
            EvictionPolicy = CacheEvictionMode.TTL,
            DefaultTtl = _ttl,
            BackingStore = _backingStore,
            BackingStorePath = $"{BackingStorePrefix}/ownerships",
            Serializer = static v => v,
            Deserializer = static v => v,
            KeyToString = static k => k,
            WriteThrough = _backingStore != null
        });

        // Initialize classification cache (TTL, write-through)
        _classifications = new BoundedCache<string, byte[]>(new BoundedCacheOptions<string, byte[]>
        {
            MaxEntries = DefaultMaxClassifications,
            EvictionPolicy = CacheEvictionMode.TTL,
            DefaultTtl = _ttl,
            BackingStore = _backingStore,
            BackingStorePath = $"{BackingStorePrefix}/classifications",
            Serializer = static v => v,
            Deserializer = static v => v,
            KeyToString = static k => k,
            WriteThrough = _backingStore != null
        });
    }

    // -------------------------------------------------------------------
    // Policy operations
    // -------------------------------------------------------------------

    /// <summary>
    /// Stores a governance policy with write-through to the backing store.
    /// </summary>
    /// <param name="policyId">Unique policy identifier.</param>
    /// <param name="data">Serialized policy data.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task PutPolicyAsync(string policyId, byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policyId);
        ArgumentNullException.ThrowIfNull(data);

        await _policies.PutAsync(policyId, data, ct).ConfigureAwait(false);
        _policyRefreshTimes[policyId] = DateTime.UtcNow;
        Interlocked.Increment(ref _backingStoreWrites);
    }

    /// <summary>
    /// Retrieves a governance policy by ID with stale-while-revalidate semantics.
    /// On cache miss, loads from the backing store. On TTL expiry, serves stale data
    /// and triggers a background refresh.
    /// </summary>
    /// <param name="policyId">Unique policy identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Serialized policy data, or <c>null</c> if not found.</returns>
    public async Task<byte[]?> GetPolicyAsync(string policyId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policyId);

        // Try cache first (may return stale data)
        var cached = _policies.GetOrDefault(policyId);
        if (cached != null)
        {
            // Check if stale refresh needed
            if (_policyRefreshTimes.TryGetValue(policyId, out var refreshTime)
                && DateTime.UtcNow - refreshTime > _ttl)
            {
                // Stale-while-revalidate: serve cached, refresh in background
                _ = RefreshInBackgroundAsync(policyId, $"{BackingStorePrefix}/policies/{policyId}",
                    _policies, _policyRefreshTimes);
                Interlocked.Increment(ref _staleRefreshes);
            }
            return cached;
        }

        // Cache miss -- load from backing store
        var result = await _policies.GetAsync(policyId, ct).ConfigureAwait(false);
        if (result != null)
        {
            _policyRefreshTimes[policyId] = DateTime.UtcNow;
            Interlocked.Increment(ref _backingStoreReads);
        }
        return result;
    }

    // -------------------------------------------------------------------
    // Ownership operations
    // -------------------------------------------------------------------

    /// <summary>
    /// Stores an ownership record with write-through to the backing store.
    /// </summary>
    /// <param name="ownerId">Unique ownership record identifier.</param>
    /// <param name="data">Serialized ownership data.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task PutOwnershipAsync(string ownerId, byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ownerId);
        ArgumentNullException.ThrowIfNull(data);

        await _ownerships.PutAsync(ownerId, data, ct).ConfigureAwait(false);
        _ownershipRefreshTimes[ownerId] = DateTime.UtcNow;
        Interlocked.Increment(ref _backingStoreWrites);
    }

    /// <summary>
    /// Retrieves an ownership record by ID with stale-while-revalidate semantics.
    /// </summary>
    /// <param name="ownerId">Unique ownership record identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Serialized ownership data, or <c>null</c> if not found.</returns>
    public async Task<byte[]?> GetOwnershipAsync(string ownerId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ownerId);

        var cached = _ownerships.GetOrDefault(ownerId);
        if (cached != null)
        {
            if (_ownershipRefreshTimes.TryGetValue(ownerId, out var refreshTime)
                && DateTime.UtcNow - refreshTime > _ttl)
            {
                _ = RefreshInBackgroundAsync(ownerId, $"{BackingStorePrefix}/ownerships/{ownerId}",
                    _ownerships, _ownershipRefreshTimes);
                Interlocked.Increment(ref _staleRefreshes);
            }
            return cached;
        }

        var result = await _ownerships.GetAsync(ownerId, ct).ConfigureAwait(false);
        if (result != null)
        {
            _ownershipRefreshTimes[ownerId] = DateTime.UtcNow;
            Interlocked.Increment(ref _backingStoreReads);
        }
        return result;
    }

    // -------------------------------------------------------------------
    // Classification operations
    // -------------------------------------------------------------------

    /// <summary>
    /// Stores a classification record with write-through to the backing store.
    /// </summary>
    /// <param name="classificationId">Unique classification record identifier.</param>
    /// <param name="data">Serialized classification data.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task PutClassificationAsync(string classificationId, byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(classificationId);
        ArgumentNullException.ThrowIfNull(data);

        await _classifications.PutAsync(classificationId, data, ct).ConfigureAwait(false);
        _classificationRefreshTimes[classificationId] = DateTime.UtcNow;
        Interlocked.Increment(ref _backingStoreWrites);
    }

    /// <summary>
    /// Retrieves a classification record by ID with stale-while-revalidate semantics.
    /// </summary>
    /// <param name="classificationId">Unique classification record identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Serialized classification data, or <c>null</c> if not found.</returns>
    public async Task<byte[]?> GetClassificationAsync(string classificationId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(classificationId);

        var cached = _classifications.GetOrDefault(classificationId);
        if (cached != null)
        {
            if (_classificationRefreshTimes.TryGetValue(classificationId, out var refreshTime)
                && DateTime.UtcNow - refreshTime > _ttl)
            {
                _ = RefreshInBackgroundAsync(classificationId, $"{BackingStorePrefix}/classifications/{classificationId}",
                    _classifications, _classificationRefreshTimes);
                Interlocked.Increment(ref _staleRefreshes);
            }
            return cached;
        }

        var result = await _classifications.GetAsync(classificationId, ct).ConfigureAwait(false);
        if (result != null)
        {
            _classificationRefreshTimes[classificationId] = DateTime.UtcNow;
            Interlocked.Increment(ref _backingStoreReads);
        }
        return result;
    }

    // -------------------------------------------------------------------
    // Parallel strategy evaluation
    // -------------------------------------------------------------------

    /// <summary>
    /// Evaluates multiple governance frameworks in parallel with bounded concurrency.
    /// Each evaluation function is throttled by <see cref="SemaphoreSlim"/> to limit
    /// concurrent evaluations to <see cref="MaxConcurrentEvaluations"/>.
    /// </summary>
    /// <typeparam name="TResult">The evaluation result type.</typeparam>
    /// <param name="evaluations">
    /// A collection of evaluation functions, each representing a governance framework check.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An array of evaluation results, one per input function, in the same order.</returns>
    public async Task<TResult[]> EvaluateInParallelAsync<TResult>(
        IReadOnlyList<Func<CancellationToken, Task<TResult>>> evaluations,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evaluations);
        if (evaluations.Count == 0) return Array.Empty<TResult>();

        Interlocked.Add(ref _evaluationsStarted, evaluations.Count);

        var tasks = new Task<TResult>[evaluations.Count];
        for (int i = 0; i < evaluations.Count; i++)
        {
            var evaluation = evaluations[i];
            tasks[i] = ThrottledEvaluateAsync(evaluation, ct);
        }

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        Interlocked.Add(ref _evaluationsCompleted, results.Length);

        return results;
    }

    /// <summary>
    /// Gets the maximum concurrent evaluation limit.
    /// </summary>
    public int MaxConcurrentEvaluations => _maxConcurrentEvaluations;

    private async Task<TResult> ThrottledEvaluateAsync<TResult>(
        Func<CancellationToken, Task<TResult>> evaluation,
        CancellationToken ct)
    {
        await _evaluationThrottle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await evaluation(ct).ConfigureAwait(false);
        }
        finally
        {
            _evaluationThrottle.Release();
        }
    }

    // -------------------------------------------------------------------
    // Stale-while-revalidate
    // -------------------------------------------------------------------

    private async Task RefreshInBackgroundAsync(
        string key,
        string backingStorePath,
        BoundedCache<string, byte[]> cache,
        ConcurrentDictionary<string, DateTime> refreshTimes)
    {
        if (_backingStore == null) return;

        try
        {
            var data = await _backingStore.ReadAsync(backingStorePath).ConfigureAwait(false);
            if (data != null)
            {
                cache.Put(key, data);
                refreshTimes[key] = DateTime.UtcNow;
                Interlocked.Increment(ref _backingStoreReads);
            }
        }
        catch
        {
            // Best-effort background refresh; stale data remains in cache
        }
    }

    // -------------------------------------------------------------------
    // IScalableSubsystem
    // -------------------------------------------------------------------

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object> GetScalingMetrics()
    {
        var policyStats = _policies.GetStatistics();
        var ownershipStats = _ownerships.GetStatistics();
        var classStats = _classifications.GetStatistics();

        return new Dictionary<string, object>
        {
            ["governance.policyCacheSize"] = policyStats.ItemCount,
            ["governance.policyCacheHitRate"] = policyStats.HitRatio,
            ["governance.ownershipCacheSize"] = ownershipStats.ItemCount,
            ["governance.ownershipCacheHitRate"] = ownershipStats.HitRatio,
            ["governance.classificationCacheSize"] = classStats.ItemCount,
            ["governance.classificationCacheHitRate"] = classStats.HitRatio,
            ["governance.backingStoreReads"] = Interlocked.Read(ref _backingStoreReads),
            ["governance.backingStoreWrites"] = Interlocked.Read(ref _backingStoreWrites),
            ["governance.evaluationsStarted"] = Interlocked.Read(ref _evaluationsStarted),
            ["governance.evaluationsCompleted"] = Interlocked.Read(ref _evaluationsCompleted),
            ["governance.staleRefreshes"] = Interlocked.Read(ref _staleRefreshes),
            ["governance.maxConcurrentEvaluations"] = _maxConcurrentEvaluations
        };
    }

    /// <inheritdoc />
    public Task ReconfigureLimitsAsync(ScalingLimits limits, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_configLock)
        {
            _currentLimits = limits;
        }

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
            int totalEntries = _policies.Count + _ownerships.Count + _classifications.Count;
            int maxCapacity = DefaultMaxPolicies + DefaultMaxOwnerships + DefaultMaxClassifications;

            if (maxCapacity == 0) return BackpressureState.Normal;

            double utilization = (double)totalEntries / maxCapacity;
            return utilization switch
            {
                >= 0.85 => BackpressureState.Critical,
                >= 0.50 => BackpressureState.Warning,
                _ => BackpressureState.Normal
            };
        }
    }

    // -------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------

    /// <inheritdoc />
    public void Dispose()
    {
        _policies.Dispose();
        _ownerships.Dispose();
        _classifications.Dispose();
        _evaluationThrottle.Dispose();
    }
}
