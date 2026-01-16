using DataWarehouse.SDK.Contracts;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace DataWarehouse.Kernel.Telemetry
{
    /// <summary>
    /// Default implementation of metrics collector.
    /// Provides in-memory metrics collection with support for counters, gauges, and histograms.
    /// </summary>
    public sealed class MetricsCollector : IMetricsCollector
    {
        private readonly ConcurrentDictionary<string, CounterState> _counters = new();
        private readonly ConcurrentDictionary<string, GaugeState> _gauges = new();
        private readonly ConcurrentDictionary<string, HistogramState> _histograms = new();

        public void IncrementCounter(string name, long value = 1, params string[] tags)
        {
            var key = BuildKey(name, tags);
            _counters.AddOrUpdate(
                key,
                _ => new CounterState(name, tags) { Value = value },
                (_, state) =>
                {
                    Interlocked.Add(ref state.Value, value);
                    return state;
                });
        }

        public void RecordGauge(string name, double value, params string[] tags)
        {
            var key = BuildKey(name, tags);
            _gauges.AddOrUpdate(
                key,
                _ => new GaugeState(name, tags) { Value = value },
                (_, state) =>
                {
                    state.Value = value;
                    return state;
                });
        }

        public void RecordHistogram(string name, double value, params string[] tags)
        {
            var key = BuildKey(name, tags);
            _histograms.AddOrUpdate(
                key,
                _ =>
                {
                    var state = new HistogramState(name, tags);
                    state.Record(value);
                    return state;
                },
                (_, state) =>
                {
                    state.Record(value);
                    return state;
                });
        }

        public IDisposable StartTimer(string name, params string[] tags)
        {
            return new TimerHandle(this, name, tags);
        }

        public MetricsSnapshot GetSnapshot()
        {
            var snapshot = new MetricsSnapshot
            {
                Timestamp = DateTime.UtcNow,
                Counters = new Dictionary<string, CounterMetric>(),
                Gauges = new Dictionary<string, GaugeMetric>(),
                Histograms = new Dictionary<string, HistogramMetric>()
            };

            foreach (var kvp in _counters)
            {
                snapshot.Counters[kvp.Key] = new CounterMetric
                {
                    Name = kvp.Value.Name,
                    Value = Interlocked.Read(ref kvp.Value.Value),
                    Tags = kvp.Value.Tags
                };
            }

            foreach (var kvp in _gauges)
            {
                snapshot.Gauges[kvp.Key] = new GaugeMetric
                {
                    Name = kvp.Value.Name,
                    Value = kvp.Value.Value,
                    Tags = kvp.Value.Tags
                };
            }

            foreach (var kvp in _histograms)
            {
                var percentiles = kvp.Value.GetPercentiles();
                snapshot.Histograms[kvp.Key] = new HistogramMetric
                {
                    Name = kvp.Value.Name,
                    Count = kvp.Value.Count,
                    Sum = kvp.Value.Sum,
                    Min = kvp.Value.Min,
                    Max = kvp.Value.Max,
                    P50 = percentiles.p50,
                    P95 = percentiles.p95,
                    P99 = percentiles.p99,
                    Tags = kvp.Value.Tags
                };
            }

            return snapshot;
        }

        public void Reset()
        {
            _counters.Clear();
            _gauges.Clear();
            _histograms.Clear();
        }

        private static string BuildKey(string name, string[] tags)
        {
            if (tags.Length == 0)
            {
                return name;
            }

            return $"{name}|{string.Join(",", tags.OrderBy(t => t))}";
        }

        #region Internal State Classes

        private sealed class CounterState
        {
            public string Name { get; }
            public string[] Tags { get; }
            public long Value;

            public CounterState(string name, string[] tags)
            {
                Name = name;
                Tags = tags;
            }
        }

        private sealed class GaugeState
        {
            public string Name { get; }
            public string[] Tags { get; }
            public double Value;

            public GaugeState(string name, string[] tags)
            {
                Name = name;
                Tags = tags;
            }
        }

        private sealed class HistogramState
        {
            public string Name { get; }
            public string[] Tags { get; }

            private readonly object _lock = new();
            private readonly List<double> _values = new();
            private const int MaxStoredValues = 10000;

            public long Count { get; private set; }
            public double Sum { get; private set; }
            public double Min { get; private set; } = double.MaxValue;
            public double Max { get; private set; } = double.MinValue;

            public HistogramState(string name, string[] tags)
            {
                Name = name;
                Tags = tags;
            }

            public void Record(double value)
            {
                lock (_lock)
                {
                    Count++;
                    Sum += value;

                    if (value < Min) Min = value;
                    if (value > Max) Max = value;

                    // Keep a sample of values for percentile calculation
                    if (_values.Count < MaxStoredValues)
                    {
                        _values.Add(value);
                    }
                    else
                    {
                        // Reservoir sampling
                        var index = Random.Shared.Next((int)Count);
                        if (index < MaxStoredValues)
                        {
                            _values[index] = value;
                        }
                    }
                }
            }

            public (double p50, double p95, double p99) GetPercentiles()
            {
                lock (_lock)
                {
                    if (_values.Count == 0)
                    {
                        return (0, 0, 0);
                    }

                    var sorted = _values.OrderBy(v => v).ToList();
                    return (
                        GetPercentile(sorted, 0.50),
                        GetPercentile(sorted, 0.95),
                        GetPercentile(sorted, 0.99)
                    );
                }
            }

            private static double GetPercentile(List<double> sortedValues, double percentile)
            {
                if (sortedValues.Count == 0) return 0;
                if (sortedValues.Count == 1) return sortedValues[0];

                var index = percentile * (sortedValues.Count - 1);
                var lower = (int)Math.Floor(index);
                var upper = (int)Math.Ceiling(index);
                var fraction = index - lower;

                if (lower == upper || upper >= sortedValues.Count)
                {
                    return sortedValues[lower];
                }

                return sortedValues[lower] * (1 - fraction) + sortedValues[upper] * fraction;
            }
        }

        private sealed class TimerHandle : IDisposable
        {
            private readonly MetricsCollector _collector;
            private readonly string _name;
            private readonly string[] _tags;
            private readonly Stopwatch _stopwatch;
            private bool _disposed;

            public TimerHandle(MetricsCollector collector, string name, string[] tags)
            {
                _collector = collector;
                _name = name;
                _tags = tags;
                _stopwatch = Stopwatch.StartNew();
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;

                _stopwatch.Stop();
                _collector.RecordHistogram(_name, _stopwatch.Elapsed.TotalMilliseconds, _tags);
            }
        }

        #endregion
    }

    /// <summary>
    /// Pre-defined metric names for kernel operations.
    /// </summary>
    public static class KernelMetrics
    {
        // Operation counters
        public const string OperationsTotal = "kernel.operations.total";
        public const string OperationsSuccess = "kernel.operations.success";
        public const string OperationsFailure = "kernel.operations.failure";

        // Latency histograms
        public const string OperationLatency = "kernel.operation.latency_ms";
        public const string PipelineLatency = "kernel.pipeline.latency_ms";
        public const string StorageLatency = "kernel.storage.latency_ms";

        // Gauges
        public const string ActiveConnections = "kernel.connections.active";
        public const string PluginCount = "kernel.plugins.count";
        public const string MemoryUsedMB = "kernel.memory.used_mb";
        public const string QueueDepth = "kernel.queue.depth";

        // Circuit breaker
        public const string CircuitBreakerOpen = "kernel.circuit_breaker.open";
        public const string CircuitBreakerRejections = "kernel.circuit_breaker.rejections";

        // Message bus
        public const string MessagesPublished = "kernel.messagebus.published";
        public const string MessagesReceived = "kernel.messagebus.received";
        public const string SubscriptionCount = "kernel.messagebus.subscriptions";
    }
}
