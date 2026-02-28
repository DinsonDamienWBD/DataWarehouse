using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// PID controller-based adaptive backpressure strategy that dynamically adjusts the
    /// send window size using a proportional-integral-derivative feedback loop driven by
    /// measured acknowledgement rates from the remote endpoint.
    /// </summary>
    /// <remarks>
    /// The PID loop operates as follows:
    /// <list type="bullet">
    ///   <item>Proportional: reacts to the current gap between target and measured ack rate</item>
    ///   <item>Integral: compensates for sustained steady-state offset with anti-windup clamp</item>
    ///   <item>Derivative: dampens oscillation by reacting to rate-of-change of error</item>
    ///   <item>Auto-tuning: starts with conservative gains and adjusts based on observed stability</item>
    /// </list>
    /// The output of the PID controller is clamped and applied as a multiplicative adjustment
    /// to the current send window size, which controls how many bytes are dispatched per cycle.
    /// </remarks>
    public class PidAdaptiveBackpressureStrategy : ConnectionStrategyBase
    {
        private readonly BoundedDictionary<string, PidConnectionState> _states = new BoundedDictionary<string, PidConnectionState>(1000);

        /// <inheritdoc/>
        public override string StrategyId => "innovation-pid-backpressure";

        /// <inheritdoc/>
        public override string DisplayName => "PID Adaptive Backpressure";

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
            MaxConcurrentConnections: 150
        );

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "PID controller-driven adaptive backpressure that monitors acknowledgement rates " +
            "and continuously adjusts send window size for optimal flow control without congestion";

        /// <inheritdoc/>
        public override string[] Tags => ["pid", "backpressure", "flow-control", "adaptive", "streaming", "congestion"];

        /// <summary>
        /// Initializes a new instance of <see cref="PidAdaptiveBackpressureStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger for diagnostics.</param>
        public PidAdaptiveBackpressureStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var endpoint = config.ConnectionString
                ?? throw new ArgumentException("Endpoint URL is required in ConnectionString.");

            var targetAckRate = GetConfiguration<double>(config, "target_ack_rate", 0.95);
            var initialWindowSize = GetConfiguration<int>(config, "initial_window_kb", 64);
            var minWindowKb = GetConfiguration<int>(config, "min_window_kb", 4);
            var maxWindowKb = GetConfiguration<int>(config, "max_window_kb", 1024);
            var monitorIntervalMs = GetConfiguration<int>(config, "monitor_interval_ms", 500);

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

            var sw = Stopwatch.StartNew();
            using var response = await client.GetAsync("/health", ct);
            sw.Stop();
            response.EnsureSuccessStatusCode();

            var connectionId = $"pid-bp-{Guid.NewGuid():N}";

            var pidState = new PidState
            {
                Kp = 0.5,
                Ki = 0.05,
                Kd = 0.1,
                LastError = 0.0,
                Integral = 0.0,
                LastTimestamp = DateTimeOffset.UtcNow,
                IntegralClamp = 10.0
            };

            var state = new PidConnectionState
            {
                TargetAckRate = targetAckRate,
                MeasuredAckRate = 1.0,
                CurrentWindowKb = initialWindowSize,
                MinWindowKb = minWindowKb,
                MaxWindowKb = maxWindowKb,
                MonitorIntervalMs = monitorIntervalMs,
                Pid = pidState,
                PidOutput = 0.0,
                StabilityPercent = 100.0,
                TotalAdjustments = 0,
                RecentOutputs = new CircularBuffer<double>(50),
                AckMeasurements = new CircularBuffer<double>(100),
                MonitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct)
            };

            state.AckMeasurements.Add(1.0);
            _states[connectionId] = state;

            // Start background monitoring loop
            _ = Task.Run(() => MonitorAckRateLoopAsync(connectionId, client, state), CancellationToken.None);

            var info = new Dictionary<string, object>
            {
                ["endpoint"] = endpoint,
                ["target_ack_rate"] = targetAckRate,
                ["initial_window_kb"] = initialWindowSize,
                ["pid_kp"] = pidState.Kp,
                ["pid_ki"] = pidState.Ki,
                ["pid_kd"] = pidState.Kd,
                ["initial_latency_ms"] = sw.Elapsed.TotalMilliseconds,
                ["connected_at"] = DateTimeOffset.UtcNow
            };

            return new DefaultConnectionHandle(client, info, connectionId);
        }

        /// <inheritdoc/>
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            try
            {
                var sw = Stopwatch.StartNew();
                using var response = await client.GetAsync("/health", ct);
                sw.Stop();

                if (_states.TryGetValue(handle.ConnectionId, out var state))
                {
                    var ackRate = response.IsSuccessStatusCode
                        ? Math.Min(1.0, 1000.0 / Math.Max(1.0, sw.Elapsed.TotalMilliseconds))
                        : 0.0;
                    state.AckMeasurements.Add(ackRate > 1.0 ? 1.0 : ackRate);
                }

                return response.IsSuccessStatusCode;
            }
            catch
            {
                if (_states.TryGetValue(handle.ConnectionId, out var state))
                    state.AckMeasurements.Add(0.0);
                return false;
            }
        }

        /// <inheritdoc/>
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            if (_states.TryRemove(handle.ConnectionId, out var state))
            {
                state.MonitorCts.Cancel();
                state.MonitorCts.Dispose();
            }
            client.Dispose();
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();

            _states.TryGetValue(handle.ConnectionId, out var state);

            var currentWindow = state?.CurrentWindowKb ?? 0;
            var ackRate = state?.MeasuredAckRate ?? 0;
            var pidOutput = state?.PidOutput ?? 0;
            var stability = state?.StabilityPercent ?? 0;

            return new ConnectionHealth(
                IsHealthy: isHealthy && stability > 30.0,
                StatusMessage: isHealthy
                    ? $"PID active: window={currentWindow}KB, ackRate={ackRate:F3}, stability={stability:F1}%"
                    : $"Degraded: ackRate={ackRate:F3}, stability={stability:F1}%",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow,
                Details: new Dictionary<string, object>
                {
                    ["current_window_kb"] = currentWindow,
                    ["ack_rate"] = ackRate,
                    ["pid_output"] = pidOutput,
                    ["stability_percent"] = stability,
                    ["pid_kp"] = state?.Pid.Kp ?? 0,
                    ["pid_ki"] = state?.Pid.Ki ?? 0,
                    ["pid_kd"] = state?.Pid.Kd ?? 0,
                    ["total_adjustments"] = state?.TotalAdjustments ?? 0
                });
        }

        /// <summary>
        /// Background monitoring loop that periodically measures ack rate and runs
        /// the PID controller to adjust the send window size.
        /// </summary>
        private async Task MonitorAckRateLoopAsync(
            string connectionId, HttpClient client, PidConnectionState state)
        {
            var token = state.MonitorCts.Token;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(state.MonitorIntervalMs, token);

                    // Measure current ack rate via a lightweight probe
                    try
                    {
                        var sw = Stopwatch.StartNew();
                        using var request = new HttpRequestMessage(HttpMethod.Head, "/");
                        using var response = await client.SendAsync(request, token);
                        sw.Stop();

                        var latencyMs = sw.Elapsed.TotalMilliseconds;
                        var measuredAck = response.IsSuccessStatusCode
                            ? Math.Clamp(1.0 - (latencyMs / 5000.0), 0.0, 1.0)
                            : 0.0;

                        state.AckMeasurements.Add(measuredAck);
                        state.MeasuredAckRate = state.AckMeasurements.Average();
                    }
                    catch
                    {
                        state.AckMeasurements.Add(0.0);
                        state.MeasuredAckRate = state.AckMeasurements.Average();
                    }

                    // Run PID controller
                    var pidResult = ComputePid(state);
                    state.PidOutput = pidResult;

                    // Apply PID output as window adjustment
                    var newWindow = state.CurrentWindowKb + (int)(pidResult * 4);
                    state.CurrentWindowKb = Math.Clamp(newWindow, state.MinWindowKb, state.MaxWindowKb);
                    state.TotalAdjustments++;

                    // Track output for stability calculation
                    state.RecentOutputs.Add(pidResult);
                    state.StabilityPercent = CalculateStability(state.RecentOutputs);

                    // Auto-tune gains based on stability
                    AutoTuneGains(state);
                }
            }
            catch (OperationCanceledException ex)
            {

                // Normal shutdown
                System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Computes the PID controller output from current state.
        /// Error = targetAckRate - measuredAckRate. Positive error means we should slow down
        /// (reduce window) because acks are below target.
        /// </summary>
        private static double ComputePid(PidConnectionState state)
        {
            var pid = state.Pid;
            var now = DateTimeOffset.UtcNow;
            var deltaTime = Math.Max(0.001, (now - pid.LastTimestamp).TotalSeconds);

            var error = state.TargetAckRate - state.MeasuredAckRate;

            // Proportional term
            var proportional = pid.Kp * error;

            // Integral term with anti-windup clamp
            pid.Integral += error * deltaTime;
            pid.Integral = Math.Clamp(pid.Integral, -pid.IntegralClamp, pid.IntegralClamp);
            var integral = pid.Ki * pid.Integral;

            // Derivative term
            var derivative = pid.Kd * ((error - pid.LastError) / deltaTime);

            pid.LastError = error;
            pid.LastTimestamp = now;

            var output = proportional + integral + derivative;

            // Clamp output to prevent extreme adjustments
            return Math.Clamp(output, -10.0, 10.0);
        }

        /// <summary>
        /// Calculates stability as 100% minus the coefficient of variation of recent PID outputs.
        /// A perfectly stable controller outputs near-zero consistently.
        /// </summary>
        private static double CalculateStability(CircularBuffer<double> recentOutputs)
        {
            if (recentOutputs.Count < 3) return 100.0;

            var values = recentOutputs.ToArray();
            var mean = values.Average();
            var variance = values.Select(v => (v - mean) * (v - mean)).Average();
            var stdDev = Math.Sqrt(variance);

            // Lower stdDev = more stable. Map to 0-100%
            var instability = Math.Min(stdDev / 5.0, 1.0);
            return (1.0 - instability) * 100.0;
        }

        /// <summary>
        /// Auto-tunes PID gains based on observed stability. If unstable, reduce gains
        /// toward conservative values. If very stable, gradually increase responsiveness.
        /// </summary>
        private static void AutoTuneGains(PidConnectionState state)
        {
            var pid = state.Pid;
            var stability = state.StabilityPercent;

            if (stability < 40.0)
            {
                // High instability: reduce all gains toward conservative values
                pid.Kp = Math.Max(0.1, pid.Kp * 0.9);
                pid.Ki = Math.Max(0.01, pid.Ki * 0.85);
                pid.Kd = Math.Max(0.02, pid.Kd * 0.9);
            }
            else if (stability > 85.0 && state.TotalAdjustments > 20)
            {
                // Very stable with enough data: gently increase responsiveness
                pid.Kp = Math.Min(2.0, pid.Kp * 1.01);
                pid.Ki = Math.Min(0.5, pid.Ki * 1.005);
                pid.Kd = Math.Min(0.5, pid.Kd * 1.01);
            }
        }

        /// <summary>
        /// Maintains PID controller state between iterations including error accumulation
        /// and timing information for derivative computation.
        /// </summary>
        private class PidState
        {
            public double Kp { get; set; }
            public double Ki { get; set; }
            public double Kd { get; set; }
            public double LastError { get; set; }
            public double Integral { get; set; }
            public double IntegralClamp { get; set; }
            public DateTimeOffset LastTimestamp { get; set; }
        }

        private class PidConnectionState
        {
            public readonly object SyncRoot = new();
            public double TargetAckRate { get; set; }
            public double MeasuredAckRate { get; set; }
            public int CurrentWindowKb { get; set; }
            public int MinWindowKb { get; set; }
            public int MaxWindowKb { get; set; }
            public int MonitorIntervalMs { get; set; }
            public PidState Pid { get; set; } = new();
            public double PidOutput { get; set; }
            public double StabilityPercent { get; set; }
            public long TotalAdjustments { get; set; }
            public CircularBuffer<double> RecentOutputs { get; set; } = new(50);
            public CircularBuffer<double> AckMeasurements { get; set; } = new(100);
            public CancellationTokenSource MonitorCts { get; set; } = new();
        }

        /// <summary>
        /// Fixed-size circular buffer for maintaining sliding window metrics.
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

            public T[] ToArray()
            {
                lock (_lock)
                {
                    var result = new T[_count];
                    var start = _count < _buffer.Length ? 0 : _head;
                    for (int i = 0; i < _count; i++)
                        result[i] = _buffer[(start + i) % _buffer.Length];
                    return result;
                }
            }

            public double Average()
            {
                lock (_lock)
                {
                    if (_count == 0) return 0;
                    double sum = 0;
                    var start = _count < _buffer.Length ? 0 : _head;
                    for (int i = 0; i < _count; i++)
                        sum += Convert.ToDouble((object)_buffer[(start + i) % _buffer.Length]!);
                    return sum / _count;
                }
            }
        }
    }
}
