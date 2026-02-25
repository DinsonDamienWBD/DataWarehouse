using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Scaling;

namespace DataWarehouse.Plugins.UltimateCompliance.Scaling;

/// <summary>
/// Manages scaling for the UltimateCompliance plugin with parallel compliance framework
/// evaluation, TTL-cached compliance check results, and configurable concurrency limits.
/// </summary>
/// <remarks>
/// <para>
/// Addresses DSCL-26: Compliance checks were previously evaluated sequentially across all
/// registered frameworks (GDPR, HIPAA, SOC2, etc.). This manager parallelizes evaluation using
/// <see cref="Task.WhenAll"/> with a configurable concurrency limit (default:
/// <see cref="Environment.ProcessorCount"/>), bounded by <see cref="SemaphoreSlim"/>.
/// </para>
/// <para>
/// Compliance check results are cached in a <see cref="BoundedCache{TKey,TValue}"/> with TTL
/// eviction (default: 5 minutes). The cache key is a SHA256 hash of (resource, framework,
/// timestamp-bucket) where the timestamp bucket rounds to the nearest TTL interval, ensuring
/// consistent caching within each time window.
/// </para>
/// <para>
/// Two levels of concurrency control are provided:
/// <list type="bullet">
///   <item><description><b>Evaluation concurrency</b>: Limits parallel framework evaluations within a single <see cref="CheckAllComplianceAsync"/> call.</description></item>
///   <item><description><b>Global concurrency</b>: Limits total concurrent compliance operations across all callers via <see cref="ScalingLimits.MaxConcurrentOperations"/>.</description></item>
/// </list>
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 88-09: Compliance scaling with parallel checks, TTL cache, configurable concurrency")]
public sealed class ComplianceScalingManager : IScalableSubsystem, IDisposable
{
    // -------------------------------------------------------------------
    // Constants
    // -------------------------------------------------------------------

    /// <summary>Default TTL for cached compliance results.</summary>
    public static readonly TimeSpan DefaultResultTtl = TimeSpan.FromMinutes(5);

    /// <summary>Default result cache capacity.</summary>
    public const int DefaultResultCacheCapacity = 100_000;

    /// <summary>Default maximum concurrent checks per evaluation call.</summary>
    public static readonly int DefaultMaxConcurrentChecks = Environment.ProcessorCount;

    /// <summary>Default maximum concurrent operations across all callers.</summary>
    public const int DefaultMaxGlobalConcurrent = 128;

    // -------------------------------------------------------------------
    // Result cache (TTL)
    // -------------------------------------------------------------------

    private readonly BoundedCache<string, ComplianceResult> _resultCache;
    private readonly TimeSpan _resultTtl;

    // -------------------------------------------------------------------
    // Concurrency control
    // -------------------------------------------------------------------

    private readonly SemaphoreSlim _evaluationThrottle;
    private readonly SemaphoreSlim _globalThrottle;
    private int _maxConcurrentChecks;
    private int _maxGlobalConcurrent;

    // -------------------------------------------------------------------
    // Scaling state
    // -------------------------------------------------------------------

    private readonly object _configLock = new();
    private ScalingLimits _currentLimits;

    // -------------------------------------------------------------------
    // Metrics
    // -------------------------------------------------------------------

    private long _totalChecks;
    private long _parallelEvaluations;
    private long _cacheHits;
    private long _cacheMisses;
    private long _frameworkChecksExecuted;

    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ComplianceScalingManager"/> class.
    /// </summary>
    /// <param name="initialLimits">Initial scaling limits. Uses defaults if <c>null</c>.</param>
    /// <param name="resultTtl">TTL for cached compliance results. Default: 5 minutes.</param>
    /// <param name="resultCacheCapacity">Maximum cached compliance results. Default: 100K.</param>
    /// <param name="maxConcurrentChecks">
    /// Maximum concurrent framework evaluations within a single call. Default: <see cref="Environment.ProcessorCount"/>.
    /// </param>
    /// <param name="maxGlobalConcurrent">
    /// Maximum concurrent compliance operations across all callers. Default: 128.
    /// </param>
    public ComplianceScalingManager(
        ScalingLimits? initialLimits = null,
        TimeSpan? resultTtl = null,
        int resultCacheCapacity = DefaultResultCacheCapacity,
        int? maxConcurrentChecks = null,
        int maxGlobalConcurrent = DefaultMaxGlobalConcurrent)
    {
        _resultTtl = resultTtl ?? DefaultResultTtl;
        _maxConcurrentChecks = maxConcurrentChecks ?? DefaultMaxConcurrentChecks;
        _maxGlobalConcurrent = maxGlobalConcurrent;

        _currentLimits = initialLimits ?? new ScalingLimits(
            MaxCacheEntries: resultCacheCapacity,
            MaxConcurrentOperations: _maxGlobalConcurrent);

        // TTL result cache
        _resultCache = new BoundedCache<string, ComplianceResult>(
            new BoundedCacheOptions<string, ComplianceResult>
            {
                MaxEntries = resultCacheCapacity,
                EvictionPolicy = CacheEvictionMode.TTL,
                DefaultTtl = _resultTtl
            });

        // Per-evaluation concurrency throttle
        _evaluationThrottle = new SemaphoreSlim(_maxConcurrentChecks, _maxConcurrentChecks);

        // Global concurrency throttle
        _globalThrottle = new SemaphoreSlim(_maxGlobalConcurrent, _maxGlobalConcurrent);
    }

    // -------------------------------------------------------------------
    // Parallel compliance checking
    // -------------------------------------------------------------------

    /// <summary>
    /// Evaluates compliance across all provided strategies in parallel using <see cref="Task.WhenAll"/>.
    /// Results are cached with TTL. Concurrency is bounded by <see cref="_evaluationThrottle"/>
    /// (per-call) and <see cref="_globalThrottle"/> (cross-caller).
    /// </summary>
    /// <param name="strategies">Compliance strategies to evaluate (one per framework).</param>
    /// <param name="complianceContext">The compliance context for the check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Dictionary of framework name to compliance result.</returns>
    public async Task<IReadOnlyDictionary<string, ComplianceResult>> CheckAllComplianceAsync(
        IReadOnlyList<IComplianceStrategy> strategies,
        ComplianceContext complianceContext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(strategies);
        ArgumentNullException.ThrowIfNull(complianceContext);

        Interlocked.Increment(ref _totalChecks);

        if (strategies.Count == 0)
            return new Dictionary<string, ComplianceResult>();

        // Acquire global throttle for this entire evaluation
        await _globalThrottle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Interlocked.Increment(ref _parallelEvaluations);

            // Parallel evaluation with per-strategy throttle
            var tasks = strategies.Select(async strategy =>
            {
                var framework = strategy.Framework;
                var cacheKey = ComputeResultCacheKey(complianceContext.OperationType, framework);

                // Check cache first
                var cached = _resultCache.GetOrDefault(cacheKey);
                if (cached != null)
                {
                    Interlocked.Increment(ref _cacheHits);
                    return (Framework: framework, Result: cached);
                }
                Interlocked.Increment(ref _cacheMisses);

                // Evaluate under concurrency throttle
                await _evaluationThrottle.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    Interlocked.Increment(ref _frameworkChecksExecuted);
                    var result = await strategy.CheckComplianceAsync(complianceContext, ct).ConfigureAwait(false);

                    // Cache the result
                    _resultCache.Put(cacheKey, result);

                    return (Framework: framework, Result: result);
                }
                finally
                {
                    _evaluationThrottle.Release();
                }
            }).ToList();

            var results = await Task.WhenAll(tasks).ConfigureAwait(false);

            return results.ToDictionary(
                r => r.Framework,
                r => r.Result,
                StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _globalThrottle.Release();
        }
    }

    /// <summary>
    /// Checks compliance for a single framework with caching support.
    /// </summary>
    /// <param name="strategy">The compliance strategy to evaluate.</param>
    /// <param name="complianceContext">The compliance context for the check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The compliance result.</returns>
    public async Task<ComplianceResult> CheckSingleComplianceAsync(
        IComplianceStrategy strategy,
        ComplianceContext complianceContext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(complianceContext);

        var cacheKey = ComputeResultCacheKey(complianceContext.OperationType, strategy.Framework);
        var cached = _resultCache.GetOrDefault(cacheKey);
        if (cached != null)
        {
            Interlocked.Increment(ref _cacheHits);
            return cached;
        }
        Interlocked.Increment(ref _cacheMisses);

        await _globalThrottle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Interlocked.Increment(ref _frameworkChecksExecuted);
            var result = await strategy.CheckComplianceAsync(complianceContext, ct).ConfigureAwait(false);
            _resultCache.Put(cacheKey, result);
            return result;
        }
        finally
        {
            _globalThrottle.Release();
        }
    }

    /// <summary>
    /// Invalidates cached compliance results for a specific resource.
    /// Relies on TTL expiry (default 5 min) since <see cref="BoundedCache{TKey,TValue}"/>
    /// does not support bulk removal by prefix. Call on policy change events.
    /// </summary>
    public void InvalidateCache()
    {
        // TTL expiry handles invalidation within the configured window.
        // For immediate invalidation needs, callers should subscribe to policy change events.
    }

    // -------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------

    private string ComputeResultCacheKey(string resourceId, string framework)
    {
        // Timestamp bucket: round to nearest TTL interval for consistent caching
        long ticksBucket = DateTime.UtcNow.Ticks / _resultTtl.Ticks;
        var input = $"{resourceId}|{framework}|{ticksBucket}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes);
    }

    // -------------------------------------------------------------------
    // IScalableSubsystem
    // -------------------------------------------------------------------

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object> GetScalingMetrics()
    {
        var cacheStats = _resultCache.GetStatistics();

        return new Dictionary<string, object>
        {
            ["compliance.totalChecks"] = Interlocked.Read(ref _totalChecks),
            ["compliance.parallelEvaluations"] = Interlocked.Read(ref _parallelEvaluations),
            ["compliance.frameworkChecksExecuted"] = Interlocked.Read(ref _frameworkChecksExecuted),
            ["compliance.cache.size"] = cacheStats.ItemCount,
            ["compliance.cache.hitRate"] = cacheStats.HitRatio,
            ["compliance.cache.hits"] = Interlocked.Read(ref _cacheHits),
            ["compliance.cache.misses"] = Interlocked.Read(ref _cacheMisses),
            ["compliance.concurrency.maxPerEvaluation"] = _maxConcurrentChecks,
            ["compliance.concurrency.maxGlobal"] = _maxGlobalConcurrent,
            ["compliance.concurrency.globalAvailable"] = _globalThrottle.CurrentCount
        };
    }

    /// <inheritdoc />
    public Task ReconfigureLimitsAsync(ScalingLimits limits, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_configLock)
        {
            _currentLimits = limits;
            _maxConcurrentChecks = Math.Max(1, limits.MaxConcurrentOperations);
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
            int available = _globalThrottle.CurrentCount;
            double utilization = _maxGlobalConcurrent > 0
                ? 1.0 - ((double)available / _maxGlobalConcurrent)
                : 0;

            return utilization switch
            {
                >= 0.90 => BackpressureState.Critical,
                >= 0.70 => BackpressureState.Warning,
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

        _resultCache.Dispose();
        _evaluationThrottle.Dispose();
        _globalThrottle.Dispose();
    }
}
