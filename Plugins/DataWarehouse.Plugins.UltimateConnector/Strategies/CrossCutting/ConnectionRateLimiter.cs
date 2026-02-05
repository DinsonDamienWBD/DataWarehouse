using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.CrossCutting
{
    /// <summary>
    /// Per-strategy connection rate limiter using the token bucket algorithm.
    /// Each strategy can be registered with independent rate and burst configurations.
    /// Thread-safe using <see cref="SemaphoreSlim"/> for token acquisition.
    /// </summary>
    /// <remarks>
    /// The token bucket algorithm allows bursts of traffic up to the configured burst size
    /// while enforcing a sustained rate limit over time. Tokens are replenished at a fixed
    /// rate, and each connection attempt consumes one token. When no tokens are available,
    /// callers can either wait for a token to become available or receive a rejection.
    /// </remarks>
    public sealed class ConnectionRateLimiter : IAsyncDisposable
    {
        private readonly ConcurrentDictionary<string, TokenBucket> _buckets = new();
        private readonly Timer _replenishTimer;
        private volatile bool _disposed;

        /// <summary>
        /// Initializes a new instance of <see cref="ConnectionRateLimiter"/>.
        /// </summary>
        /// <param name="replenishInterval">Interval between token replenishment ticks. Defaults to 100ms.</param>
        public ConnectionRateLimiter(TimeSpan? replenishInterval = null)
        {
            var interval = replenishInterval ?? TimeSpan.FromMilliseconds(100);
            _replenishTimer = new Timer(ReplenishTokens, null, interval, interval);
        }

        /// <summary>
        /// Registers a rate limit for a specific strategy. Idempotent; re-registration is ignored.
        /// </summary>
        /// <param name="strategyId">Strategy identifier.</param>
        /// <param name="rateConfig">Rate limit configuration.</param>
        public void RegisterStrategy(string strategyId, RateLimitConfiguration rateConfig)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
            ArgumentNullException.ThrowIfNull(rateConfig);

            _buckets.TryAdd(strategyId, new TokenBucket(rateConfig));
        }

        /// <summary>
        /// Acquires a rate limit token for the specified strategy, waiting if necessary.
        /// </summary>
        /// <param name="strategyId">Strategy identifier.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A disposable lease that releases the token when disposed.</returns>
        /// <exception cref="InvalidOperationException">If no rate limit is registered for the strategy.</exception>
        public async Task<RateLimitLease> AcquireAsync(string strategyId, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_buckets.TryGetValue(strategyId, out var bucket))
                throw new InvalidOperationException($"No rate limit registered for strategy '{strategyId}'.");

            await bucket.Semaphore.WaitAsync(ct);
            Interlocked.Increment(ref bucket.ConsumedCount);

            return new RateLimitLease(bucket);
        }

        /// <summary>
        /// Attempts to acquire a rate limit token without waiting.
        /// </summary>
        /// <param name="strategyId">Strategy identifier.</param>
        /// <param name="lease">The acquired lease, or null if the rate limit is exhausted.</param>
        /// <returns>True if a token was acquired; false if the rate limit is currently exhausted.</returns>
        public bool TryAcquire(string strategyId, out RateLimitLease? lease)
        {
            lease = null;

            if (_disposed || !_buckets.TryGetValue(strategyId, out var bucket))
                return false;

            if (!bucket.Semaphore.Wait(TimeSpan.Zero))
            {
                Interlocked.Increment(ref bucket.RejectedCount);
                return false;
            }

            Interlocked.Increment(ref bucket.ConsumedCount);
            lease = new RateLimitLease(bucket);
            return true;
        }

        /// <summary>
        /// Returns the current rate limit statistics for a strategy.
        /// </summary>
        /// <param name="strategyId">Strategy identifier.</param>
        /// <returns>Rate limit statistics, or null if the strategy is not registered.</returns>
        public RateLimitStats? GetStats(string strategyId)
        {
            if (!_buckets.TryGetValue(strategyId, out var bucket))
                return null;

            return new RateLimitStats(
                AvailableTokens: bucket.Semaphore.CurrentCount,
                MaxBurst: bucket.Config.BurstSize,
                TokensPerSecond: bucket.Config.TokensPerSecond,
                TotalConsumed: Interlocked.Read(ref bucket.ConsumedCount),
                TotalRejected: Interlocked.Read(ref bucket.RejectedCount));
        }

        /// <summary>
        /// Replenishes tokens in all registered buckets based on elapsed time.
        /// </summary>
        private void ReplenishTokens(object? state)
        {
            if (_disposed) return;

            foreach (var (_, bucket) in _buckets)
            {
                var now = Stopwatch.GetTimestamp();
                var elapsed = Stopwatch.GetElapsedTime(bucket.LastReplenishTimestamp, now);
                var tokensToAdd = (int)(elapsed.TotalSeconds * bucket.Config.TokensPerSecond);

                if (tokensToAdd <= 0) continue;

                bucket.LastReplenishTimestamp = now;

                var currentCount = bucket.Semaphore.CurrentCount;
                var canAdd = Math.Min(tokensToAdd, bucket.Config.BurstSize - currentCount);

                if (canAdd > 0)
                    bucket.Semaphore.Release(canAdd);
            }
        }

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            await _replenishTimer.DisposeAsync();

            foreach (var (_, bucket) in _buckets)
                bucket.Semaphore.Dispose();

            _buckets.Clear();
        }
    }

    /// <summary>
    /// Configuration for a per-strategy rate limit.
    /// </summary>
    /// <param name="TokensPerSecond">Sustained rate of tokens replenished per second.</param>
    /// <param name="BurstSize">Maximum burst size (bucket capacity).</param>
    public sealed record RateLimitConfiguration(
        double TokensPerSecond = 10.0,
        int BurstSize = 20);

    /// <summary>
    /// Statistics for a rate-limited strategy.
    /// </summary>
    /// <param name="AvailableTokens">Currently available tokens.</param>
    /// <param name="MaxBurst">Maximum burst capacity.</param>
    /// <param name="TokensPerSecond">Configured sustained rate.</param>
    /// <param name="TotalConsumed">Total tokens consumed since registration.</param>
    /// <param name="TotalRejected">Total attempts rejected due to rate limiting.</param>
    public sealed record RateLimitStats(
        int AvailableTokens,
        int MaxBurst,
        double TokensPerSecond,
        long TotalConsumed,
        long TotalRejected);

    /// <summary>
    /// A disposable lease representing an acquired rate limit token.
    /// Releasing the lease does not return the token; tokens are replenished by the timer.
    /// </summary>
    public sealed class RateLimitLease : IDisposable
    {
        private readonly TokenBucket _bucket;
        private volatile bool _disposed;

        internal RateLimitLease(TokenBucket bucket) => _bucket = bucket;

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }
    }

    /// <summary>
    /// Internal token bucket state for a single strategy.
    /// </summary>
    internal sealed class TokenBucket
    {
        public RateLimitConfiguration Config { get; }
        public SemaphoreSlim Semaphore { get; }
        public long LastReplenishTimestamp;
        public long ConsumedCount;
        public long RejectedCount;

        public TokenBucket(RateLimitConfiguration config)
        {
            Config = config;
            Semaphore = new SemaphoreSlim(config.BurstSize, config.BurstSize);
            LastReplenishTimestamp = Stopwatch.GetTimestamp();
        }
    }
}
