using System;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.CrossCutting
{
    /// <summary>
    /// Per-endpoint circuit breaker that prevents cascading failures by temporarily blocking
    /// connection attempts to endpoints that are experiencing repeated failures.
    /// Implements the standard Closed / Open / HalfOpen state machine with configurable
    /// failure thresholds and recovery timeouts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Closed</b>: Connections are allowed. Consecutive failures are tracked. When the
    /// failure count reaches the threshold, the circuit trips to Open.
    /// </para>
    /// <para>
    /// <b>Open</b>: All connection attempts are rejected immediately without reaching the
    /// endpoint. After the configured recovery timeout elapses, the circuit transitions to HalfOpen.
    /// </para>
    /// <para>
    /// <b>HalfOpen</b>: A single probe connection is allowed through. If it succeeds, the
    /// circuit closes. If it fails, the circuit re-opens.
    /// </para>
    /// All state transitions are thread-safe using interlocked operations and SemaphoreSlim.
    /// </remarks>
    public sealed class ConnectionCircuitBreaker : IDisposable
    {
        private readonly BoundedDictionary<string, CircuitState> _circuits = new BoundedDictionary<string, CircuitState>(1000);
        private volatile bool _disposed;

        /// <summary>
        /// Registers a circuit breaker for a specific endpoint. Idempotent.
        /// </summary>
        /// <param name="endpointKey">Unique key identifying the endpoint (e.g., host:port).</param>
        /// <param name="config">Circuit breaker configuration.</param>
        public void RegisterEndpoint(string endpointKey, CircuitBreakerConfiguration? config = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(endpointKey);
            _circuits.TryAdd(endpointKey, new CircuitState(config ?? new CircuitBreakerConfiguration()));
        }

        /// <summary>
        /// Checks whether a connection attempt is allowed for the given endpoint.
        /// If the circuit is Open and the recovery timeout has elapsed, transitions to HalfOpen.
        /// </summary>
        /// <param name="endpointKey">Endpoint key.</param>
        /// <returns>True if the connection attempt is allowed.</returns>
        public bool AllowRequest(string endpointKey)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_circuits.TryGetValue(endpointKey, out var circuit))
                return true;

            var currentState = (CircuitBreakerState)Interlocked.CompareExchange(
                ref circuit.State, 0, 0);

            switch (currentState)
            {
                case CircuitBreakerState.Closed:
                    return true;

                case CircuitBreakerState.Open:
                    if (DateTimeOffset.UtcNow >= circuit.OpenedAt + circuit.Config.RecoveryTimeout)
                    {
                        if (circuit.HalfOpenGate.Wait(TimeSpan.Zero))
                        {
                            Interlocked.Exchange(ref circuit.State, (int)CircuitBreakerState.HalfOpen);
                            return true;
                        }
                    }
                    Interlocked.Increment(ref circuit.RejectedCount);
                    return false;

                case CircuitBreakerState.HalfOpen:
                    return false;

                default:
                    return true;
            }
        }

        /// <summary>
        /// Records a successful connection for the given endpoint. Resets the failure counter
        /// and transitions HalfOpen circuits back to Closed.
        /// </summary>
        /// <param name="endpointKey">Endpoint key.</param>
        public void RecordSuccess(string endpointKey)
        {
            if (!_circuits.TryGetValue(endpointKey, out var circuit)) return;

            var currentState = (CircuitBreakerState)Interlocked.CompareExchange(
                ref circuit.State, 0, 0);

            Interlocked.Exchange(ref circuit.ConsecutiveFailures, 0);
            Interlocked.Increment(ref circuit.SuccessCount);

            if (currentState == CircuitBreakerState.HalfOpen)
            {
                Interlocked.Exchange(ref circuit.State, (int)CircuitBreakerState.Closed);

                if (circuit.HalfOpenGate.CurrentCount == 0)
                    circuit.HalfOpenGate.Release();
            }
        }

        /// <summary>
        /// Records a failed connection for the given endpoint. Increments the failure counter
        /// and may trip the circuit to Open.
        /// </summary>
        /// <param name="endpointKey">Endpoint key.</param>
        public void RecordFailure(string endpointKey)
        {
            if (!_circuits.TryGetValue(endpointKey, out var circuit)) return;

            var failures = Interlocked.Increment(ref circuit.ConsecutiveFailures);
            Interlocked.Increment(ref circuit.TotalFailures);

            var currentState = (CircuitBreakerState)Interlocked.CompareExchange(
                ref circuit.State, 0, 0);

            if (currentState == CircuitBreakerState.HalfOpen)
            {
                // Finding 1866: Use CAS to prevent concurrent callers both tripping the circuit.
                if (Interlocked.CompareExchange(
                        ref circuit.State,
                        (int)CircuitBreakerState.Open,
                        (int)CircuitBreakerState.HalfOpen) == (int)CircuitBreakerState.HalfOpen)
                {
                    circuit.OpenedAt = DateTimeOffset.UtcNow;
                    if (circuit.HalfOpenGate.CurrentCount == 0)
                        circuit.HalfOpenGate.Release();
                }
            }
            else if (currentState == CircuitBreakerState.Closed && failures >= circuit.Config.FailureThreshold)
            {
                // Finding 1866: CAS from Closed → Open so only one thread trips the circuit.
                if (Interlocked.CompareExchange(
                        ref circuit.State,
                        (int)CircuitBreakerState.Open,
                        (int)CircuitBreakerState.Closed) == (int)CircuitBreakerState.Closed)
                {
                    circuit.OpenedAt = DateTimeOffset.UtcNow;
                }
            }
        }

        /// <summary>
        /// Returns the current state of the circuit breaker for an endpoint.
        /// </summary>
        /// <param name="endpointKey">Endpoint key.</param>
        /// <returns>Circuit breaker status, or null if the endpoint is not registered.</returns>
        public CircuitBreakerStatus? GetStatus(string endpointKey)
        {
            if (!_circuits.TryGetValue(endpointKey, out var circuit))
                return null;

            var state = (CircuitBreakerState)Interlocked.CompareExchange(ref circuit.State, 0, 0);

            return new CircuitBreakerStatus(
                State: state,
                ConsecutiveFailures: (int)Interlocked.Read(ref circuit.ConsecutiveFailures),
                TotalFailures: Interlocked.Read(ref circuit.TotalFailures),
                TotalSuccesses: Interlocked.Read(ref circuit.SuccessCount),
                TotalRejected: Interlocked.Read(ref circuit.RejectedCount),
                LastOpenedAt: circuit.OpenedAt,
                FailureThreshold: circuit.Config.FailureThreshold,
                RecoveryTimeout: circuit.Config.RecoveryTimeout);
        }

        /// <summary>
        /// Manually resets a circuit breaker to the Closed state.
        /// </summary>
        /// <param name="endpointKey">Endpoint key.</param>
        public void Reset(string endpointKey)
        {
            if (!_circuits.TryGetValue(endpointKey, out var circuit)) return;

            Interlocked.Exchange(ref circuit.State, (int)CircuitBreakerState.Closed);
            Interlocked.Exchange(ref circuit.ConsecutiveFailures, 0);

            if (circuit.HalfOpenGate.CurrentCount == 0)
                circuit.HalfOpenGate.Release();
        }

        // TripCircuit removed: inline CAS in RecordFailure prevents double-trip races (Finding 1866).

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var (_, circuit) in _circuits)
                circuit.HalfOpenGate.Dispose();

            _circuits.Clear();
        }
    }

    /// <summary>
    /// Circuit breaker states.
    /// </summary>
    public enum CircuitBreakerState
    {
        /// <summary>Circuit is closed; connections are allowed.</summary>
        Closed = 0,
        /// <summary>Circuit is open; connections are blocked.</summary>
        Open = 1,
        /// <summary>Circuit is half-open; one probe connection is allowed.</summary>
        HalfOpen = 2
    }

    /// <summary>
    /// Configuration for a per-endpoint circuit breaker.
    /// </summary>
    /// <param name="FailureThreshold">Number of consecutive failures to trip the circuit.</param>
    /// <param name="RecoveryTimeoutValue">Duration the circuit stays open before transitioning to HalfOpen.</param>
    public sealed record CircuitBreakerConfiguration(
        int FailureThreshold = 5,
        TimeSpan? RecoveryTimeoutValue = null)
    {
        /// <summary>
        /// Effective recovery timeout, defaulting to 30 seconds.
        /// </summary>
        public TimeSpan RecoveryTimeout { get; } = RecoveryTimeoutValue ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Current status of a circuit breaker for a specific endpoint.
    /// </summary>
    /// <param name="State">Current circuit state.</param>
    /// <param name="ConsecutiveFailures">Current streak of consecutive failures.</param>
    /// <param name="TotalFailures">Total failures since registration.</param>
    /// <param name="TotalSuccesses">Total successes since registration.</param>
    /// <param name="TotalRejected">Total requests rejected while circuit was open.</param>
    /// <param name="LastOpenedAt">When the circuit was last tripped to Open.</param>
    /// <param name="FailureThreshold">Configured failure threshold.</param>
    /// <param name="RecoveryTimeout">Configured recovery timeout.</param>
    public sealed record CircuitBreakerStatus(
        CircuitBreakerState State,
        int ConsecutiveFailures,
        long TotalFailures,
        long TotalSuccesses,
        long TotalRejected,
        DateTimeOffset? LastOpenedAt,
        int FailureThreshold,
        TimeSpan RecoveryTimeout);

    /// <summary>
    /// Internal mutable state for a single endpoint circuit.
    /// </summary>
    internal sealed class CircuitState
    {
        public CircuitBreakerConfiguration Config { get; }
        public SemaphoreSlim HalfOpenGate { get; }
        public int State; // CircuitBreakerState stored as int for Interlocked
        public long ConsecutiveFailures;
        public long TotalFailures;
        public long SuccessCount;
        public long RejectedCount;
        private long _openedAtTicks;
        public DateTimeOffset? OpenedAt
        {
            get { var ticks = Interlocked.Read(ref _openedAtTicks); return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero); }
            set { Interlocked.Exchange(ref _openedAtTicks, value?.UtcTicks ?? 0); }
        }

        public CircuitState(CircuitBreakerConfiguration config)
        {
            Config = config;
            HalfOpenGate = new SemaphoreSlim(1, 1);
        }
    }
}
