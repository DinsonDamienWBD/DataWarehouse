using DataWarehouse.SDK.Contracts;
using System.Collections.Concurrent;

namespace DataWarehouse.Kernel.RateLimiting
{
    /// <summary>
    /// Token bucket rate limiter implementation.
    /// Allows bursting while maintaining average rate limits.
    /// </summary>
    public sealed class TokenBucketRateLimiter : IRateLimiter
    {
        private readonly ConcurrentDictionary<string, TokenBucket> _buckets = new();
        private readonly RateLimitConfig _defaultConfig;
        private readonly ConcurrentDictionary<string, RateLimitConfig> _keyConfigs = new();
        private readonly Timer _cleanupTimer;

        public TokenBucketRateLimiter(RateLimitConfig? defaultConfig = null)
        {
            _defaultConfig = defaultConfig ?? new RateLimitConfig();

            // Cleanup expired buckets every 5 minutes
            _cleanupTimer = new Timer(CleanupExpiredBuckets, null,
                TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }

        /// <summary>
        /// Configures rate limiting for a specific key.
        /// </summary>
        public void Configure(string key, RateLimitConfig config)
        {
            _keyConfigs[key] = config;
        }

        public Task<RateLimitResult> AcquireAsync(string key, int permits = 1, CancellationToken ct = default)
        {
            var bucket = _buckets.GetOrAdd(key, k =>
            {
                var config = _keyConfigs.TryGetValue(k, out var c) ? c : _defaultConfig;
                return new TokenBucket(config);
            });

            var result = bucket.TryAcquire(permits);
            return Task.FromResult(result);
        }

        public RateLimitStatus GetStatus(string key)
        {
            if (!_buckets.TryGetValue(key, out var bucket))
            {
                var config = _keyConfigs.TryGetValue(key, out var c) ? c : _defaultConfig;
                return new RateLimitStatus
                {
                    Key = key,
                    CurrentPermits = config.PermitsPerWindow,
                    MaxPermits = config.PermitsPerWindow,
                    WindowStart = DateTime.UtcNow,
                    WindowDuration = config.WindowDuration
                };
            }

            return bucket.GetStatus(key);
        }

        public void Reset(string key)
        {
            _buckets.TryRemove(key, out _);
        }

        private void CleanupExpiredBuckets(object? state)
        {
            var now = DateTime.UtcNow;
            var keysToRemove = _buckets
                .Where(kvp => kvp.Value.IsExpired(now))
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                _buckets.TryRemove(key, out _);
            }
        }

        private sealed class TokenBucket
        {
            private readonly RateLimitConfig _config;
            private readonly object _lock = new();

            private double _tokens;
            private DateTime _lastRefill;
            private DateTime _windowStart;

            public TokenBucket(RateLimitConfig config)
            {
                _config = config;
                _tokens = config.PermitsPerWindow;
                _lastRefill = DateTime.UtcNow;
                _windowStart = DateTime.UtcNow;
            }

            public RateLimitResult TryAcquire(int permits)
            {
                lock (_lock)
                {
                    RefillTokens();

                    if (_tokens >= permits)
                    {
                        _tokens -= permits;
                        return RateLimitResult.Allowed((int)_tokens);
                    }

                    // Calculate when tokens will be available
                    var tokensNeeded = permits - _tokens;
                    var refillRate = (double)_config.PermitsPerWindow / _config.WindowDuration.TotalSeconds;
                    var secondsToWait = tokensNeeded / refillRate;
                    var retryAfter = TimeSpan.FromSeconds(Math.Ceiling(secondsToWait));

                    return RateLimitResult.Denied(retryAfter, $"Rate limit exceeded. Retry after {retryAfter.TotalSeconds:F1}s");
                }
            }

            private void RefillTokens()
            {
                var now = DateTime.UtcNow;
                var elapsed = now - _lastRefill;

                // Calculate tokens to add based on time elapsed
                var refillRate = (double)_config.PermitsPerWindow / _config.WindowDuration.TotalSeconds;
                var tokensToAdd = elapsed.TotalSeconds * refillRate;

                _tokens = Math.Min(_tokens + tokensToAdd, _config.PermitsPerWindow + _config.BurstLimit);
                _lastRefill = now;

                // Reset window if needed
                if (now - _windowStart > _config.WindowDuration)
                {
                    _windowStart = now;
                }
            }

            public RateLimitStatus GetStatus(string key)
            {
                lock (_lock)
                {
                    RefillTokens();

                    return new RateLimitStatus
                    {
                        Key = key,
                        CurrentPermits = (int)_tokens,
                        MaxPermits = _config.PermitsPerWindow,
                        WindowStart = _windowStart,
                        WindowDuration = _config.WindowDuration
                    };
                }
            }

            public bool IsExpired(DateTime now)
            {
                lock (_lock)
                {
                    // Consider expired if no activity for 10 minutes
                    return now - _lastRefill > TimeSpan.FromMinutes(10);
                }
            }
        }
    }
}
