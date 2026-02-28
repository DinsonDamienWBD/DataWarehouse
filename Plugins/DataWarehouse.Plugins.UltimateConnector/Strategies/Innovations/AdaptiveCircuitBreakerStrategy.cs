using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Innovations
{
    /// <summary>
    /// Probabilistic circuit breaker strategy with canary-based recovery and rate-limit awareness.
    /// Uses a three-state model (Closed, Canary, Open) where the Canary state progressively
    /// increases traffic through geometric scaling of the pass rate.
    /// </summary>
    /// <remarks>
    /// Key differences from a standard circuit breaker:
    /// <list type="bullet">
    ///   <item>Canary state replaces HalfOpen: allows a configurable fraction of requests (starting at 1%)</item>
    ///   <item>On canary success: doubles pass rate (0.01 to 0.02 to 0.04 ... to 1.0 = Closed)</item>
    ///   <item>On canary failure: halves pass rate (minimum 0.001) for graceful backoff</item>
    ///   <item>Rate limit headers (Retry-After, X-RateLimit-Reset/Remaining) are parsed and honored</item>
    ///   <item>Token bucket decay calculates exact sleep duration with 0-10% jitter</item>
    ///   <item>Hysteresis: failure threshold scales inversely with recent success rate</item>
    /// </list>
    /// </remarks>
    public class AdaptiveCircuitBreakerStrategy : ConnectionStrategyBase
    {
        private readonly BoundedDictionary<string, BreakerConnectionState> _states = new BoundedDictionary<string, BreakerConnectionState>(1000);

        /// <inheritdoc/>
        public override string StrategyId => "innovation-adaptive-breaker";

        /// <inheritdoc/>
        public override string DisplayName => "Adaptive Circuit Breaker";

        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.Protocol;

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: true,
            SupportsStreaming: true,
            SupportsReconnection: true,
            SupportsSsl: true,
            SupportsHealthCheck: true,
            SupportsConnectionTesting: true,
            MaxConcurrentConnections: 250
        );

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "Probabilistic circuit breaker with canary recovery (geometric pass-rate scaling), " +
            "rate-limit header parsing, token bucket decay with jitter, and hysteresis-based failure thresholds";

        /// <inheritdoc/>
        public override string[] Tags => ["circuit-breaker", "canary", "rate-limit", "resilience", "adaptive", "probabilistic"];

        /// <summary>
        /// Initializes a new instance of <see cref="AdaptiveCircuitBreakerStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger for diagnostics.</param>
        public AdaptiveCircuitBreakerStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var endpoint = config.ConnectionString
                ?? throw new ArgumentException("Endpoint URL is required in ConnectionString.");

            var baseFailureThreshold = GetConfiguration<int>(config, "failure_threshold", 5);
            var initialCanaryRate = GetConfiguration<double>(config, "initial_canary_rate", 0.01);
            var minCanaryRate = GetConfiguration<double>(config, "min_canary_rate", 0.001);
            var openDurationSec = GetConfiguration<int>(config, "open_duration_seconds", 30);
            var windowSize = GetConfiguration<int>(config, "window_size", 100);

            var handler = new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = config.PoolSize,
                ConnectTimeout = TimeSpan.FromSeconds(5)
            };

            var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(endpoint),
                Timeout = config.Timeout,
                DefaultRequestVersion = new Version(2, 0)
            };

            if (!string.IsNullOrEmpty(config.AuthCredential))
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", config.AuthCredential);

            // Use a stable ID derived from the endpoint so all connections to the same
            // endpoint share the same breaker state across reconnects.
            var connectionId = $"cb-{endpoint.GetHashCode():X8}";

            if (_states.TryGetValue(connectionId, out var existingState) &&
                existingState.Breaker.State == CircuitState.Open)
            {
                var elapsed = DateTimeOffset.UtcNow - existingState.Breaker.OpenedAt;
                if (elapsed.TotalSeconds < openDurationSec)
                    throw new InvalidOperationException(
                        $"Circuit breaker is OPEN for {endpoint}. Reset in {openDurationSec - elapsed.TotalSeconds:F0}s.");
            }

            // Attempt connection
            var sw = Stopwatch.StartNew();
            var response = await client.GetAsync("/health", ct);
            sw.Stop();

            // Parse rate limit headers from initial response
            var rateLimitInfo = ParseRateLimitHeaders(response);

            if (!response.IsSuccessStatusCode)
            {
                if (rateLimitInfo.RetryAfter.HasValue)
                {
                    var jitter = TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * rateLimitInfo.RetryAfter.Value.TotalMilliseconds * 0.1);
                    await Task.Delay(rateLimitInfo.RetryAfter.Value + jitter, ct);
                    response = await client.GetAsync("/health", ct);
                }

                response.EnsureSuccessStatusCode();
            }

            var breakerState = new BreakerState
            {
                State = CircuitState.Closed,
                FailureCount = 0,
                SuccessCount = 1,
                CanaryPassRate = initialCanaryRate,
                BaseFailureThreshold = baseFailureThreshold,
                MinCanaryRate = minCanaryRate,
                SuccessStreak = 1,
                OpenedAt = DateTimeOffset.MinValue,
                OpenDuration = TimeSpan.FromSeconds(openDurationSec),
                RecentResults = new CircularBuffer<bool>(windowSize)
            };
            breakerState.RecentResults.Add(true);

            var connState = new BreakerConnectionState
            {
                Breaker = breakerState,
                RateLimitResetAt = rateLimitInfo.ResetAt,
                RateLimitRemaining = rateLimitInfo.Remaining,
                TotalRequests = 1,
                TotalTripped = 0
            };

            _states[connectionId] = connState;

            var info = new Dictionary<string, object>
            {
                ["endpoint"] = endpoint,
                ["breaker_state"] = "Closed",
                ["failure_threshold"] = baseFailureThreshold,
                ["canary_pass_rate"] = initialCanaryRate,
                ["initial_latency_ms"] = sw.Elapsed.TotalMilliseconds,
                ["connected_at"] = DateTimeOffset.UtcNow
            };

            return new DefaultConnectionHandle(client, info, connectionId);
        }

        /// <inheritdoc/>
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();

            if (!_states.TryGetValue(handle.ConnectionId, out var connState))
                return false;

            var breaker = connState.Breaker;

            // Check circuit state
            switch (breaker.State)
            {
                case CircuitState.Open:
                    var elapsed = DateTimeOffset.UtcNow - breaker.OpenedAt;
                    if (elapsed < breaker.OpenDuration)
                    {
                        // Check if rate limit reset has passed
                        if (connState.RateLimitResetAt.HasValue &&
                            DateTimeOffset.UtcNow >= connState.RateLimitResetAt.Value)
                        {
                            TransitionToCanary(breaker);
                        }
                        else
                        {
                            return false; // Circuit is open, reject
                        }
                    }
                    else
                    {
                        TransitionToCanary(breaker);
                    }
                    break;

                case CircuitState.Canary:
                    // Probabilistic pass: only allow canaryPassRate fraction through
                    if (Random.Shared.NextDouble() >= breaker.CanaryPassRate)
                        return false; // Rejected by canary gate
                    break;
            }

            // Attempt the request
            connState.TotalRequests++;
            try
            {
                using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/"), ct);

                // Parse rate limit info from every response
                var rateLimitInfo = ParseRateLimitHeaders(response);
                connState.RateLimitResetAt = rateLimitInfo.ResetAt ?? connState.RateLimitResetAt;
                connState.RateLimitRemaining = rateLimitInfo.Remaining;

                if (response.IsSuccessStatusCode)
                {
                    RecordSuccess(breaker);
                    return true;
                }
                else
                {
                    // If rate limited (429), handle specially
                    if ((int)response.StatusCode == 429 && rateLimitInfo.RetryAfter.HasValue)
                    {
                        connState.RateLimitResetAt = DateTimeOffset.UtcNow + rateLimitInfo.RetryAfter.Value;
                    }
                    RecordFailure(breaker, connState);
                    return false;
                }
            }
            catch
            {
                RecordFailure(breaker, connState);
                return false;
            }
        }

        /// <inheritdoc/>
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            _states.TryRemove(handle.ConnectionId, out _);
            client.Dispose();
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();

            _states.TryGetValue(handle.ConnectionId, out var connState);
            var breaker = connState?.Breaker;

            var stateName = breaker?.State.ToString() ?? "Unknown";
            var canaryRate = breaker?.CanaryPassRate ?? 0;
            var failureRate = ComputeFailureRate(breaker);
            var resetIn = connState?.RateLimitResetAt.HasValue == true
                ? Math.Max(0, (connState.RateLimitResetAt.Value - DateTimeOffset.UtcNow).TotalSeconds)
                : 0.0;

            return new ConnectionHealth(
                IsHealthy: isHealthy && breaker?.State != CircuitState.Open,
                StatusMessage: breaker?.State switch
                {
                    CircuitState.Closed => $"Breaker CLOSED: streak={breaker.SuccessStreak}, failRate={failureRate:P1}",
                    CircuitState.Canary => $"Breaker CANARY: passRate={canaryRate:P2}, failRate={failureRate:P1}",
                    CircuitState.Open => $"Breaker OPEN: reset in {resetIn:F0}s",
                    _ => "Unknown state"
                },
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow,
                Details: new Dictionary<string, object>
                {
                    ["breaker_state"] = stateName,
                    ["canary_pass_rate"] = canaryRate,
                    ["failure_rate"] = failureRate,
                    ["rate_limit_reset_in_seconds"] = resetIn,
                    ["success_streak"] = breaker?.SuccessStreak ?? 0,
                    ["total_requests"] = connState?.TotalRequests ?? 0,
                    ["total_tripped"] = connState?.TotalTripped ?? 0
                });
        }

        /// <summary>
        /// Records a successful request and potentially promotes the circuit state.
        /// In Canary mode, doubles the pass rate. At 100%, transitions to Closed.
        /// </summary>
        private static void RecordSuccess(BreakerState breaker)
        {
            breaker.SuccessCount++;
            breaker.SuccessStreak++;
            breaker.RecentResults.Add(true);

            if (breaker.State == CircuitState.Canary)
            {
                // Double the canary pass rate on success
                breaker.CanaryPassRate = Math.Min(1.0, breaker.CanaryPassRate * 2.0);

                // If pass rate reaches 1.0, fully close the circuit
                if (breaker.CanaryPassRate >= 1.0)
                {
                    breaker.State = CircuitState.Closed;
                    breaker.FailureCount = 0;
                }
            }
        }

        /// <summary>
        /// Records a failed request and potentially trips the circuit.
        /// In Canary mode, halves the pass rate. In Closed mode, checks hysteresis threshold.
        /// </summary>
        private static void RecordFailure(BreakerState breaker, BreakerConnectionState connState)
        {
            breaker.FailureCount++;
            breaker.SuccessStreak = 0;
            breaker.RecentResults.Add(false);

            if (breaker.State == CircuitState.Canary)
            {
                // Halve the canary pass rate on failure
                breaker.CanaryPassRate = Math.Max(breaker.MinCanaryRate, breaker.CanaryPassRate / 2.0);

                // If canary rate drops to minimum, re-open the circuit
                if (breaker.CanaryPassRate <= breaker.MinCanaryRate)
                {
                    breaker.State = CircuitState.Open;
                    breaker.OpenedAt = DateTimeOffset.UtcNow;
                    connState.TotalTripped++;
                }
            }
            else if (breaker.State == CircuitState.Closed)
            {
                // Hysteresis: failure threshold scales with recent success rate
                // More successes = more tolerance before tripping
                var recentSuccessRate = ComputeSuccessRate(breaker);
                var adjustedThreshold = (int)(breaker.BaseFailureThreshold * (1.0 + recentSuccessRate));

                if (breaker.FailureCount >= adjustedThreshold)
                {
                    breaker.State = CircuitState.Open;
                    breaker.OpenedAt = DateTimeOffset.UtcNow;
                    connState.TotalTripped++;
                }
            }
        }

        /// <summary>
        /// Transitions from Open to Canary state with initial pass rate.
        /// </summary>
        private static void TransitionToCanary(BreakerState breaker)
        {
            breaker.State = CircuitState.Canary;
            breaker.CanaryPassRate = 0.01;
            breaker.FailureCount = 0;
        }

        /// <summary>
        /// Computes the recent success rate from the sliding window.
        /// </summary>
        private static double ComputeSuccessRate(BreakerState breaker)
        {
            var total = breaker.RecentResults.Count;
            if (total == 0) return 1.0;
            var successes = breaker.RecentResults.CountWhere(r => r);
            return successes / (double)total;
        }

        /// <summary>
        /// Computes the recent failure rate from the sliding window.
        /// </summary>
        private static double ComputeFailureRate(BreakerState? breaker)
        {
            if (breaker == null) return 0;
            var total = breaker.RecentResults.Count;
            if (total == 0) return 0;
            var failures = breaker.RecentResults.CountWhere(r => !r);
            return failures / (double)total;
        }

        /// <summary>
        /// Parses Retry-After, X-RateLimit-Reset, and X-RateLimit-Remaining headers
        /// from an HTTP response to determine rate-limit timing.
        /// </summary>
        private static RateLimitInfo ParseRateLimitHeaders(HttpResponseMessage response)
        {
            TimeSpan? retryAfter = null;
            DateTimeOffset? resetAt = null;
            int? remaining = null;

            // Parse Retry-After header (seconds or HTTP-date)
            if (response.Headers.TryGetValues("Retry-After", out var retryValues))
            {
                var value = retryValues.FirstOrDefault();
                if (value != null)
                {
                    if (int.TryParse(value, out var seconds))
                    {
                        retryAfter = TimeSpan.FromSeconds(seconds);
                    }
                    else if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var httpDate))
                    {
                        retryAfter = httpDate - DateTimeOffset.UtcNow;
                        if (retryAfter < TimeSpan.Zero) retryAfter = TimeSpan.Zero;
                    }
                }
            }

            // Parse X-RateLimit-Reset (Unix timestamp)
            if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues))
            {
                var value = resetValues.FirstOrDefault();
                if (value != null && long.TryParse(value, out var unixTimestamp))
                {
                    resetAt = DateTimeOffset.FromUnixTimeSeconds(unixTimestamp);
                    retryAfter ??= resetAt - DateTimeOffset.UtcNow;
                }
            }

            // Parse X-RateLimit-Remaining
            if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remainingValues))
            {
                var value = remainingValues.FirstOrDefault();
                if (value != null && int.TryParse(value, out var rem))
                    remaining = rem;
            }

            return new RateLimitInfo(retryAfter, resetAt, remaining);
        }

        private record RateLimitInfo(TimeSpan? RetryAfter, DateTimeOffset? ResetAt, int? Remaining);

        private enum CircuitState
        {
            /// <summary>Circuit is closed, all requests pass through normally.</summary>
            Closed,
            /// <summary>Circuit is in canary mode, a fraction of requests pass through.</summary>
            Canary,
            /// <summary>Circuit is open, all requests are rejected.</summary>
            Open
        }

        private class BreakerState
        {
            public readonly object SyncRoot = new();
            public CircuitState State { get; set; }
            public int FailureCount { get; set; }
            public int SuccessCount { get; set; }
            public double CanaryPassRate { get; set; }
            public int BaseFailureThreshold { get; set; }
            public double MinCanaryRate { get; set; }
            public int SuccessStreak { get; set; }
            public DateTimeOffset OpenedAt { get; set; }
            public TimeSpan OpenDuration { get; set; }
            public CircularBuffer<bool> RecentResults { get; set; } = new(100);
        }

        private class BreakerConnectionState
        {
            public BreakerState Breaker { get; set; } = new();
            public DateTimeOffset? RateLimitResetAt { get; set; }
            public int? RateLimitRemaining { get; set; }
            public long TotalRequests { get; set; }
            public long TotalTripped { get; set; }
        }

        /// <summary>
        /// Fixed-size circular buffer for maintaining sliding window of results.
        /// </summary>
        private class CircularBuffer<T>
        {
            private readonly T[] _buffer;
            private int _head;
            private int _count;
            private readonly object _lock = new();

            public int Count { get { lock (_lock) return _count; } }

            public CircularBuffer(int capacity) => _buffer = new T[capacity];

            public void Add(T item)
            {
                lock (_lock)
                {
                    _buffer[_head] = item;
                    _head = (_head + 1) % _buffer.Length;
                    if (_count < _buffer.Length) _count++;
                }
            }

            public int CountWhere(Func<T, bool> predicate)
            {
                lock (_lock)
                {
                    int c = 0;
                    var start = _count < _buffer.Length ? 0 : _head;
                    for (int i = 0; i < _count; i++)
                        if (predicate(_buffer[(start + i) % _buffer.Length])) c++;
                    return c;
                }
            }
        }
    }
}
