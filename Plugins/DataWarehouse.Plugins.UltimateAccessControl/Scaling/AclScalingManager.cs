using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Persistence;
using DataWarehouse.SDK.Contracts.Scaling;

namespace DataWarehouse.Plugins.UltimateAccessControl.Scaling;

/// <summary>
/// Manages scaling for the UltimateAccessControl plugin with a fixed-size ring buffer audit log,
/// parallel multi-strategy evaluation with configurable weighted thresholds, runtime-reconfigurable
/// threat detection thresholds, and TTL-cached policy decisions.
/// </summary>
/// <remarks>
/// <para>
/// Addresses DSCL-11: The original <c>ConcurrentQueue&lt;PolicyAccessDecision&gt;</c> audit log grew
/// without bound. This manager replaces it with a lock-free circular buffer of configurable capacity
/// (default: 1M entries). When full, the oldest entries are silently overwritten. For long-term
/// retention, an optional background drain can persist entries to an <see cref="IPersistentBackingStore"/>
/// at a configurable interval.
/// </para>
/// <para>
/// Addresses DSCL-25: When multiple ACL strategies are registered (RBAC, ABAC, ZeroTrust, etc.),
/// they are evaluated in parallel using <see cref="Task.WhenAll"/>. Results are combined via a
/// configurable weighted threshold (default: all must pass). Concurrency is bounded by
/// <see cref="ScalingLimits.MaxConcurrentOperations"/>.
/// </para>
/// <para>
/// Addresses DSCL-26: All threat detection thresholds (failed auth rate, anomaly scores, trust
/// score decay rates) are stored in a <see cref="BoundedCache{TKey,TValue}"/> and can be
/// reconfigured at runtime via <see cref="ReconfigureLimitsAsync"/>.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 88-09: ACL scaling with ring buffer audit, parallel evaluation, TTL policy cache")]
public sealed class AclScalingManager : IScalableSubsystem, IDisposable
{
    // -------------------------------------------------------------------
    // Constants
    // -------------------------------------------------------------------

    /// <summary>Default ring buffer capacity for audit log entries.</summary>
    public const int DefaultAuditBufferCapacity = 1_000_000;

    /// <summary>Default TTL for cached policy decisions.</summary>
    public static readonly TimeSpan DefaultPolicyDecisionTtl = TimeSpan.FromSeconds(30);

    /// <summary>Default maximum concurrent strategy evaluations.</summary>
    public const int DefaultMaxConcurrentEvaluations = 64;

    /// <summary>Default policy cache capacity.</summary>
    public const int DefaultPolicyCacheCapacity = 100_000;

    /// <summary>Default threshold cache capacity.</summary>
    public const int DefaultThresholdCacheCapacity = 10_000;

    /// <summary>Default drain interval for persisting audit entries.</summary>
    public static readonly TimeSpan DefaultDrainInterval = TimeSpan.FromMinutes(5);

    /// <summary>Default retention period for drained audit entries.</summary>
    public static readonly TimeSpan DefaultRetentionPeriod = TimeSpan.FromDays(90);

    // -------------------------------------------------------------------
    // Ring buffer audit log
    // -------------------------------------------------------------------

    private readonly PolicyAccessDecision?[] _auditBuffer;
    private readonly int _auditCapacity;
    private long _auditWriteIndex;
    private long _auditTotalWritten;

    // -------------------------------------------------------------------
    // Optional audit drain to persistent store
    // -------------------------------------------------------------------

    private readonly bool _drainEnabled;
    private readonly TimeSpan _drainInterval;
    private readonly TimeSpan _retentionPeriod;
    private readonly IPersistentBackingStore? _backingStore;
    private Timer? _drainTimer;
    private long _lastDrainIndex;

    // -------------------------------------------------------------------
    // Parallel strategy evaluation
    // -------------------------------------------------------------------

    private SemaphoreSlim _evaluationThrottle;
    private int _maxConcurrentEvaluations;

    // -------------------------------------------------------------------
    // Policy decision cache (TTL)
    // -------------------------------------------------------------------

    private readonly BoundedCache<string, PolicyAccessDecision> _policyCache;

    // -------------------------------------------------------------------
    // Threshold cache
    // -------------------------------------------------------------------

    private readonly BoundedCache<string, double> _thresholdCache;

    // -------------------------------------------------------------------
    // Scaling state
    // -------------------------------------------------------------------

    private readonly object _configLock = new();
    private ScalingLimits _currentLimits;

    // -------------------------------------------------------------------
    // Metrics
    // -------------------------------------------------------------------

    private long _totalEvaluations;
    private long _parallelEvaluations;
    private long _policyCacheHits;
    private long _policyCacheMisses;
    private long _drainedEntries;

    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="AclScalingManager"/> class.
    /// </summary>
    /// <param name="auditBufferCapacity">
    /// Ring buffer capacity for audit log entries. Default: <see cref="DefaultAuditBufferCapacity"/>.
    /// </param>
    /// <param name="initialLimits">Initial scaling limits. Uses defaults if <c>null</c>.</param>
    /// <param name="policyDecisionTtl">TTL for cached policy decisions. Default: 30 seconds.</param>
    /// <param name="policyCacheCapacity">Maximum cached policy decisions. Default: 100K.</param>
    /// <param name="enableDrain">Whether to enable background drain to persistent store.</param>
    /// <param name="drainInterval">Interval between drain cycles. Default: 5 minutes.</param>
    /// <param name="retentionPeriod">Retention period for drained entries. Default: 90 days.</param>
    /// <param name="backingStore">Optional persistent backing store for audit drain.</param>
    public AclScalingManager(
        int auditBufferCapacity = DefaultAuditBufferCapacity,
        ScalingLimits? initialLimits = null,
        TimeSpan? policyDecisionTtl = null,
        int policyCacheCapacity = DefaultPolicyCacheCapacity,
        bool enableDrain = false,
        TimeSpan? drainInterval = null,
        TimeSpan? retentionPeriod = null,
        IPersistentBackingStore? backingStore = null)
    {
        if (auditBufferCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(auditBufferCapacity), "Must be at least 1.");

        _auditCapacity = auditBufferCapacity;
        _auditBuffer = new PolicyAccessDecision?[_auditCapacity];

        _currentLimits = initialLimits ?? new ScalingLimits(
            MaxCacheEntries: policyCacheCapacity,
            MaxConcurrentOperations: DefaultMaxConcurrentEvaluations);

        _maxConcurrentEvaluations = _currentLimits.MaxConcurrentOperations;
        _evaluationThrottle = new SemaphoreSlim(_maxConcurrentEvaluations, _maxConcurrentEvaluations);

        // Policy decision cache with TTL
        var ttl = policyDecisionTtl ?? DefaultPolicyDecisionTtl;
        _policyCache = new BoundedCache<string, PolicyAccessDecision>(
            new BoundedCacheOptions<string, PolicyAccessDecision>
            {
                MaxEntries = policyCacheCapacity,
                EvictionPolicy = CacheEvictionMode.TTL,
                DefaultTtl = ttl
            });

        // Threshold cache (LRU for fast lookup of configurable thresholds)
        _thresholdCache = new BoundedCache<string, double>(
            new BoundedCacheOptions<string, double>
            {
                MaxEntries = DefaultThresholdCacheCapacity,
                EvictionPolicy = CacheEvictionMode.LRU
            });

        // Seed default thresholds
        _thresholdCache.Put("failedAuthRate", 0.1);
        _thresholdCache.Put("anomalyScoreThreshold", 0.75);
        _thresholdCache.Put("trustScoreDecayRate", 0.05);
        _thresholdCache.Put("maxFailedAttemptsPerMinute", 10.0);
        _thresholdCache.Put("sessionTimeoutMinutes", 30.0);

        // Drain configuration
        _drainEnabled = enableDrain && backingStore != null;
        _drainInterval = drainInterval ?? DefaultDrainInterval;
        _retentionPeriod = retentionPeriod ?? DefaultRetentionPeriod;
        _backingStore = backingStore;

        if (_drainEnabled)
        {
            _drainTimer = new Timer(
                DrainCallback,
                null,
                _drainInterval,
                _drainInterval);
        }
    }

    // -------------------------------------------------------------------
    // Ring buffer audit log
    // -------------------------------------------------------------------

    /// <summary>
    /// Records a policy access decision in the ring buffer audit log.
    /// When the buffer is full, the oldest entry is overwritten.
    /// This operation is lock-free using <see cref="Interlocked.Increment(ref long)"/>.
    /// </summary>
    /// <param name="decision">The policy decision to record.</param>
    public void RecordAuditEntry(PolicyAccessDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        long index = Interlocked.Increment(ref _auditWriteIndex) - 1;
        int slot = (int)(index % _auditCapacity);
        _auditBuffer[slot] = decision;
        Interlocked.Increment(ref _auditTotalWritten);
    }

    /// <summary>
    /// Reads recent audit entries from the ring buffer, newest first.
    /// </summary>
    /// <param name="maxCount">Maximum number of entries to return.</param>
    /// <returns>Recent audit entries in reverse chronological order.</returns>
    public IReadOnlyList<PolicyAccessDecision> GetRecentAuditEntries(int maxCount = 100)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);

        long writeIndex = Interlocked.Read(ref _auditWriteIndex);
        int available = (int)Math.Min(writeIndex, _auditCapacity);
        int toRead = Math.Min(maxCount, available);

        var result = new List<PolicyAccessDecision>(toRead);
        for (int i = 0; i < toRead; i++)
        {
            long idx = writeIndex - 1 - i;
            if (idx < 0) break;
            int slot = (int)(idx % _auditCapacity);
            var entry = _auditBuffer[slot];
            if (entry != null)
                result.Add(entry);
        }

        return result;
    }

    /// <summary>
    /// Gets the total number of audit entries written (including overwritten ones).
    /// </summary>
    public long TotalAuditEntriesWritten => Interlocked.Read(ref _auditTotalWritten);

    /// <summary>
    /// Gets the ring buffer capacity.
    /// </summary>
    public int AuditBufferCapacity => _auditCapacity;

    // -------------------------------------------------------------------
    // Parallel strategy evaluation
    // -------------------------------------------------------------------

    /// <summary>
    /// Evaluates multiple ACL strategies in parallel with configurable concurrency bounds.
    /// Results are combined based on the specified evaluation mode.
    /// </summary>
    /// <param name="strategies">Strategies to evaluate.</param>
    /// <param name="context">Access context for evaluation.</param>
    /// <param name="mode">Policy evaluation mode (AllMustAllow, AnyMustAllow, Weighted, etc.).</param>
    /// <param name="weights">
    /// Optional per-strategy weights for <see cref="PolicyEvaluationMode.Weighted"/> mode.
    /// Keys are strategy names, values are weights (0.0-1.0).
    /// </param>
    /// <param name="weightedThreshold">
    /// Threshold for weighted evaluation (0.0-1.0). Default: 1.0 (all must pass).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Aggregated policy decision.</returns>
    public async Task<PolicyAccessDecision> EvaluateStrategiesParallelAsync(
        IReadOnlyList<IAccessControlStrategy> strategies,
        AccessContext context,
        PolicyEvaluationMode mode = PolicyEvaluationMode.AllMustAllow,
        IReadOnlyDictionary<string, double>? weights = null,
        double weightedThreshold = 1.0,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(strategies);
        ArgumentNullException.ThrowIfNull(context);

        if (strategies.Count == 0)
        {
            return new PolicyAccessDecision
            {
                IsGranted = false,
                Reason = "No strategies registered",
                DecisionId = Guid.NewGuid().ToString("N"),
                Timestamp = DateTime.UtcNow,
                EvaluationTimeMs = 0,
                EvaluationMode = mode,
                StrategyDecisions = Array.Empty<StrategyDecisionDetail>(),
                Context = context
            };
        }

        // Check policy cache
        string cacheKey = ComputePolicyCacheKey(context);
        var cached = _policyCache.GetOrDefault(cacheKey);
        if (cached != null)
        {
            Interlocked.Increment(ref _policyCacheHits);
            return cached;
        }
        Interlocked.Increment(ref _policyCacheMisses);

        Interlocked.Increment(ref _totalEvaluations);

        var startTime = DateTime.UtcNow;

        // Parallel evaluation with concurrency throttle
        var tasks = strategies.Select(async strategy =>
        {
            await _evaluationThrottle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var result = await strategy.EvaluateAccessAsync(context, ct).ConfigureAwait(false);
                return (Strategy: strategy, Result: result);
            }
            finally
            {
                _evaluationThrottle.Release();
            }
        }).ToList();

        Interlocked.Increment(ref _parallelEvaluations);

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;

        // Combine results based on evaluation mode
        var strategyDecisions = results.Select(r => new StrategyDecisionDetail
        {
            StrategyId = r.Strategy.StrategyId,
            StrategyName = r.Strategy.StrategyName,
            Decision = r.Result,
            Weight = weights != null && weights.TryGetValue(r.Strategy.StrategyName, out var w) ? w : 1.0
        }).ToList();

        bool isGranted = mode switch
        {
            PolicyEvaluationMode.AllMustAllow => results.All(r => r.Result.IsGranted),
            PolicyEvaluationMode.AnyMustAllow => results.Any(r => r.Result.IsGranted),
            PolicyEvaluationMode.FirstMatch => results.FirstOrDefault().Result?.IsGranted ?? false,
            PolicyEvaluationMode.Weighted => EvaluateWeighted(results, weights, weightedThreshold),
            _ => results.All(r => r.Result.IsGranted)
        };

        var decision = new PolicyAccessDecision
        {
            IsGranted = isGranted,
            Reason = isGranted ? "Access granted by policy evaluation" : "Access denied by policy evaluation",
            DecisionId = Guid.NewGuid().ToString("N"),
            Timestamp = DateTime.UtcNow,
            EvaluationTimeMs = elapsed,
            EvaluationMode = mode,
            StrategyDecisions = strategyDecisions,
            Context = context
        };

        // Cache the decision
        _policyCache.Put(cacheKey, decision);

        // Record in audit log
        RecordAuditEntry(decision);

        return decision;
    }

    /// <summary>
    /// Invalidates the policy decision cache. Call when policies are updated.
    /// </summary>
    public void InvalidatePolicyCache()
    {
        // Evict all entries by resetting the cache version counter.
        // The next evaluation will miss the cache and re-evaluate strategies.
        Interlocked.Increment(ref _policyCacheVersion);
    }

    private long _policyCacheVersion;

    // -------------------------------------------------------------------
    // Threshold management
    // -------------------------------------------------------------------

    /// <summary>
    /// Gets the current value of a threat detection threshold.
    /// </summary>
    /// <param name="thresholdName">Name of the threshold (e.g., "failedAuthRate").</param>
    /// <returns>The threshold value, or <c>null</c> if not configured.</returns>
    public double? GetThreshold(string thresholdName)
    {
        ArgumentNullException.ThrowIfNull(thresholdName);
        // Use ContainsKey to distinguish "key not found" from "key has value 0.0"
        if (!_thresholdCache.ContainsKey(thresholdName))
            return null;
        return _thresholdCache.GetOrDefault(thresholdName);
    }

    /// <summary>
    /// Sets or updates a threat detection threshold at runtime.
    /// </summary>
    /// <param name="thresholdName">Name of the threshold.</param>
    /// <param name="value">New threshold value.</param>
    public void SetThreshold(string thresholdName, double value)
    {
        ArgumentNullException.ThrowIfNull(thresholdName);
        _thresholdCache.Put(thresholdName, value);
    }

    /// <summary>
    /// Sets multiple thresholds at once.
    /// </summary>
    /// <param name="thresholds">Dictionary of threshold name to value.</param>
    public void SetThresholds(IReadOnlyDictionary<string, double> thresholds)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        foreach (var kvp in thresholds)
            _thresholdCache.Put(kvp.Key, kvp.Value);
    }

    // -------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------

    private string ComputePolicyCacheKey(AccessContext context)
    {
        // Include all authorization-relevant fields to prevent cross-privilege cache collisions
        var sb = new StringBuilder(256);
        sb.Append(Interlocked.Read(ref _policyCacheVersion)).Append('|');
        sb.Append(context.SubjectId).Append('|');
        sb.Append(context.ResourceId).Append('|');
        sb.Append(context.Action).Append('|');
        if (context.Roles.Count > 0)
            sb.Append(string.Join(",", context.Roles)).Append('|');
        if (context.SubjectAttributes.Count > 0)
        {
            foreach (var kvp in context.SubjectAttributes.OrderBy(k => k.Key))
                sb.Append(kvp.Key).Append('=').Append(kvp.Value).Append(',');
        }
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hashBytes);
    }

    private static bool EvaluateWeighted(
        (IAccessControlStrategy Strategy, AccessDecision Result)[] results,
        IReadOnlyDictionary<string, double>? weights,
        double threshold)
    {
        if (weights == null || weights.Count == 0)
        {
            // Equal weighting: all must pass
            return results.All(r => r.Result.IsGranted);
        }

        double totalWeight = 0;
        double grantedWeight = 0;

        foreach (var (strategy, result) in results)
        {
            double weight = weights.TryGetValue(strategy.StrategyName, out var w) ? w : 1.0;
            totalWeight += weight;
            if (result.IsGranted)
                grantedWeight += weight;
        }

        return totalWeight > 0 && (grantedWeight / totalWeight) >= threshold;
    }

    private async void DrainCallback(object? state)
    {
        if (_disposed || _backingStore == null) return;

        try
        {
            long currentIndex = Interlocked.Read(ref _auditWriteIndex);
            long drainFrom = _lastDrainIndex;

            if (currentIndex <= drainFrom) return;

            int toDrain = (int)Math.Min(currentIndex - drainFrom, _auditCapacity);

            for (int i = 0; i < toDrain; i++)
            {
                int slot = (int)((drainFrom + i) % _auditCapacity);
                var entry = _auditBuffer[slot];
                if (entry == null) continue;

                var path = $"dw://internal/acl/audit/{entry.Timestamp:yyyyMMdd}/{entry.DecisionId}";
                var data = Encoding.UTF8.GetBytes(
                    $"{entry.Timestamp:O}|{entry.DecisionId}|{entry.IsGranted}|{entry.Reason}");

                await _backingStore.WriteAsync(path, data).ConfigureAwait(false);
                Interlocked.Increment(ref _drainedEntries);
            }

            _lastDrainIndex = currentIndex;
        }
        catch
        {

            // Best-effort drain; failures are silently ignored
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }
    }

    // -------------------------------------------------------------------
    // IScalableSubsystem
    // -------------------------------------------------------------------

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object> GetScalingMetrics()
    {
        var policyStats = _policyCache.GetStatistics();
        var thresholdStats = _thresholdCache.GetStatistics();

        return new Dictionary<string, object>
        {
            ["acl.auditBuffer.capacity"] = _auditCapacity,
            ["acl.auditBuffer.totalWritten"] = Interlocked.Read(ref _auditTotalWritten),
            ["acl.auditBuffer.currentFill"] = Math.Min(Interlocked.Read(ref _auditWriteIndex), _auditCapacity),
            ["acl.evaluation.total"] = Interlocked.Read(ref _totalEvaluations),
            ["acl.evaluation.parallel"] = Interlocked.Read(ref _parallelEvaluations),
            ["acl.evaluation.maxConcurrent"] = _maxConcurrentEvaluations,
            ["acl.policyCache.size"] = policyStats.ItemCount,
            ["acl.policyCache.hitRate"] = policyStats.HitRatio,
            ["acl.policyCache.hits"] = Interlocked.Read(ref _policyCacheHits),
            ["acl.policyCache.misses"] = Interlocked.Read(ref _policyCacheMisses),
            ["acl.thresholdCache.size"] = thresholdStats.ItemCount,
            ["acl.drain.enabled"] = _drainEnabled,
            ["acl.drain.totalDrained"] = Interlocked.Read(ref _drainedEntries)
        };
    }

    /// <inheritdoc />
    public Task ReconfigureLimitsAsync(ScalingLimits limits, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        SemaphoreSlim? oldThrottle = null;
        lock (_configLock)
        {
            _currentLimits = limits;
            var newMax = limits.MaxConcurrentOperations;
            if (newMax != _maxConcurrentEvaluations)
            {
                // Replace the semaphore so the new concurrency limit takes effect immediately.
                // In-flight waiters continue against the old semaphore; new waiters use the new one.
                oldThrottle = _evaluationThrottle;
                _evaluationThrottle = new SemaphoreSlim(newMax, newMax);
                _maxConcurrentEvaluations = newMax;
            }
        }

        // Dispose the old semaphore outside the lock to avoid blocking
        oldThrottle?.Dispose();

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
            long fill = Math.Min(Interlocked.Read(ref _auditWriteIndex), _auditCapacity);
            double utilization = (double)fill / _auditCapacity;

            return utilization switch
            {
                >= 0.95 => BackpressureState.Critical,
                >= 0.75 => BackpressureState.Warning,
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
        if (_disposed) return;
        _disposed = true;

        _drainTimer?.Dispose();
        _drainTimer = null;
        _policyCache.Dispose();
        _thresholdCache.Dispose();
        _evaluationThrottle.Dispose();
    }
}
