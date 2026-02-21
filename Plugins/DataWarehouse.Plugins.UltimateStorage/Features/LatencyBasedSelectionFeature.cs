using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

#pragma warning disable CS0618 // StorageStrategyRegistry is transitionally obsolete; Feature migration planned for v6.0
namespace DataWarehouse.Plugins.UltimateStorage.Features
{
    /// <summary>
    /// Latency-Based Backend Selection Feature (C6) - Selects optimal storage backend based on latency metrics.
    ///
    /// Features:
    /// - Health-check based latency tracking per backend
    /// - Latency percentile tracking (p50, p95, p99)
    /// - Automatic selection of lowest-latency backend
    /// - SLA-aware routing (select backend within latency budget)
    /// - Geographic proximity consideration
    /// - Dynamic backend ranking based on recent performance
    /// - Latency threshold filtering
    /// - Automatic failover to next-best backend
    /// </summary>
    public sealed class LatencyBasedSelectionFeature : IDisposable
    {
        private readonly StorageStrategyRegistry _registry;
        private readonly BoundedDictionary<string, BackendLatencyProfile> _latencyProfiles = new BoundedDictionary<string, BackendLatencyProfile>(1000);
        private readonly Timer _healthCheckTimer;
        private bool _disposed;

        // Configuration
        private TimeSpan _healthCheckInterval = TimeSpan.FromMinutes(1);
        private int _latencySampleSize = 100;
        private double _maxAcceptableLatencyMs = 5000;
        private bool _enableGeographicAwareness = true;

        // Statistics
        private long _totalSelections;
        private long _fastestBackendSelections;
        private long _slaViolations;
        private long _healthCheckFailures;

        /// <summary>
        /// Initializes a new instance of the LatencyBasedSelectionFeature.
        /// </summary>
        /// <param name="registry">The storage strategy registry.</param>
        public LatencyBasedSelectionFeature(StorageStrategyRegistry registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));

            // Start background health check worker
            _healthCheckTimer = new Timer(
                callback: async _ => await RunHealthCheckCycleAsync(),
                state: null,
                dueTime: TimeSpan.Zero, // Run immediately
                period: _healthCheckInterval);
        }

        /// <summary>
        /// Gets the total number of backend selections performed.
        /// </summary>
        public long TotalSelections => Interlocked.Read(ref _totalSelections);

        /// <summary>
        /// Gets the number of SLA violations detected.
        /// </summary>
        public long SlaViolations => Interlocked.Read(ref _slaViolations);

        /// <summary>
        /// Gets or sets the health check interval.
        /// </summary>
        public TimeSpan HealthCheckInterval
        {
            get => _healthCheckInterval;
            set
            {
                _healthCheckInterval = value;
                _healthCheckTimer.Change(TimeSpan.Zero, value);
            }
        }

        /// <summary>
        /// Gets or sets the maximum acceptable latency in milliseconds.
        /// Backends exceeding this threshold are filtered out.
        /// </summary>
        public double MaxAcceptableLatencyMs
        {
            get => _maxAcceptableLatencyMs;
            set => _maxAcceptableLatencyMs = value > 0 ? value : 5000;
        }

        /// <summary>
        /// Gets or sets the number of latency samples to track for percentile calculations.
        /// </summary>
        public int LatencySampleSize
        {
            get => _latencySampleSize;
            set => _latencySampleSize = value > 10 ? value : 10;
        }

        /// <summary>
        /// Gets or sets whether geographic awareness is enabled.
        /// </summary>
        public bool EnableGeographicAwareness
        {
            get => _enableGeographicAwareness;
            set => _enableGeographicAwareness = value;
        }

        /// <summary>
        /// Selects the optimal backend based on latency metrics and optionally SLA requirements.
        /// </summary>
        /// <param name="category">Optional category filter (e.g., "cloud", "local").</param>
        /// <param name="slaLatencyMs">Optional SLA latency requirement in milliseconds.</param>
        /// <param name="region">Optional geographic region preference.</param>
        /// <returns>The selected storage strategy ID, or null if none available.</returns>
        public string? SelectOptimalBackend(string? category = null, double? slaLatencyMs = null, string? region = null)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            Interlocked.Increment(ref _totalSelections);

            // Get all available strategies
            var strategies = string.IsNullOrWhiteSpace(category)
                ? _registry.GetAllStrategies()
                : _registry.GetStrategiesByCategory(category);

            if (!strategies.Any())
            {
                return null;
            }

            // Filter by SLA if specified
            var slaThreshold = slaLatencyMs ?? _maxAcceptableLatencyMs;
            var candidates = strategies
                .Select(s => new
                {
                    StrategyId = s.StrategyId,
                    Profile = GetOrCreateProfile(s.StrategyId),
                    Strategy = s
                })
                .Where(x => x.Profile.IsHealthy && x.Profile.P95LatencyMs <= slaThreshold)
                .ToList();

            if (!candidates.Any())
            {
                // No backends meet SLA - try without SLA filter
                candidates = strategies
                    .Select(s => new
                    {
                        StrategyId = s.StrategyId,
                        Profile = GetOrCreateProfile(s.StrategyId),
                        Strategy = s
                    })
                    .Where(x => x.Profile.IsHealthy)
                    .ToList();

                if (!candidates.Any())
                {
                    return null;
                }

                Interlocked.Increment(ref _slaViolations);
            }

            // Apply geographic filtering if enabled and region specified
            if (_enableGeographicAwareness && !string.IsNullOrWhiteSpace(region))
            {
                var regionalCandidates = candidates
                    .Where(x => x.Profile.Region?.Equals(region, StringComparison.OrdinalIgnoreCase) == true)
                    .ToList();

                if (regionalCandidates.Any())
                {
                    candidates = regionalCandidates;
                }
            }

            // Select backend with lowest P95 latency
            var selected = candidates
                .OrderBy(x => x.Profile.P95LatencyMs)
                .ThenBy(x => x.Profile.AverageLatencyMs)
                .FirstOrDefault();

            if (selected != null)
            {
                Interlocked.Increment(ref _fastestBackendSelections);
                selected.Profile.LastSelectedTime = DateTime.UtcNow;
                Interlocked.Increment(ref selected.Profile.SelectionCount);
            }

            return selected?.StrategyId;
        }

        /// <summary>
        /// Selects multiple backends ranked by latency for redundancy/replication.
        /// </summary>
        /// <param name="count">Number of backends to select.</param>
        /// <param name="category">Optional category filter.</param>
        /// <param name="slaLatencyMs">Optional SLA latency requirement.</param>
        /// <returns>List of strategy IDs ranked by latency.</returns>
        public List<string> SelectMultipleBackends(int count, string? category = null, double? slaLatencyMs = null)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (count <= 0)
            {
                return new List<string>();
            }

            var strategies = string.IsNullOrWhiteSpace(category)
                ? _registry.GetAllStrategies()
                : _registry.GetStrategiesByCategory(category);

            var slaThreshold = slaLatencyMs ?? _maxAcceptableLatencyMs;

            return strategies
                .Select(s => new
                {
                    StrategyId = s.StrategyId,
                    Profile = GetOrCreateProfile(s.StrategyId)
                })
                .Where(x => x.Profile.IsHealthy && x.Profile.P95LatencyMs <= slaThreshold)
                .OrderBy(x => x.Profile.P95LatencyMs)
                .Take(count)
                .Select(x => x.StrategyId)
                .ToList();
        }

        /// <summary>
        /// Records a latency measurement for a backend.
        /// </summary>
        /// <param name="strategyId">Backend strategy ID.</param>
        /// <param name="latencyMs">Measured latency in milliseconds.</param>
        public void RecordLatency(string strategyId, double latencyMs)
        {
            if (string.IsNullOrWhiteSpace(strategyId) || latencyMs < 0)
            {
                return;
            }

            var profile = GetOrCreateProfile(strategyId);
            profile.RecordLatency(latencyMs);
        }

        /// <summary>
        /// Gets the latency profile for a specific backend.
        /// </summary>
        /// <param name="strategyId">Backend strategy ID.</param>
        /// <returns>Latency profile or null if not found.</returns>
        public BackendLatencyProfile? GetLatencyProfile(string strategyId)
        {
            return _latencyProfiles.TryGetValue(strategyId, out var profile) ? profile : null;
        }

        /// <summary>
        /// Gets all latency profiles ranked by P95 latency.
        /// </summary>
        /// <returns>List of latency profiles sorted by performance.</returns>
        public List<BackendLatencyProfile> GetAllLatencyProfiles()
        {
            return _latencyProfiles.Values
                .OrderBy(p => p.P95LatencyMs)
                .ToList();
        }

        /// <summary>
        /// Sets the geographic region for a backend (for proximity-based routing).
        /// </summary>
        /// <param name="strategyId">Backend strategy ID.</param>
        /// <param name="region">Geographic region (e.g., "us-east-1", "eu-west-1").</param>
        public void SetBackendRegion(string strategyId, string region)
        {
            if (string.IsNullOrWhiteSpace(strategyId) || string.IsNullOrWhiteSpace(region))
            {
                return;
            }

            var profile = GetOrCreateProfile(strategyId);
            profile.Region = region;
        }

        #region Private Methods

        private BackendLatencyProfile GetOrCreateProfile(string strategyId)
        {
            return _latencyProfiles.GetOrAdd(strategyId, _ => new BackendLatencyProfile
            {
                StrategyId = strategyId,
                MaxSampleSize = _latencySampleSize
            });
        }

        private async Task RunHealthCheckCycleAsync()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                var strategies = _registry.GetAllStrategies().OfType<IStorageStrategyExtended>();

                foreach (var strategy in strategies)
                {
                    try
                    {
                        var sw = Stopwatch.StartNew();
                        var isHealthy = await strategy.HealthCheckAsync();
                        sw.Stop();

                        var profile = GetOrCreateProfile(strategy.StrategyId);
                        profile.IsHealthy = isHealthy;
                        profile.LastHealthCheck = DateTime.UtcNow;

                        if (isHealthy)
                        {
                            // Record health check latency
                            RecordLatency(strategy.StrategyId, sw.Elapsed.TotalMilliseconds);
                        }
                    }
                    catch
                    {
                        var profile = GetOrCreateProfile(strategy.StrategyId);
                        profile.IsHealthy = false;
                        profile.LastHealthCheck = DateTime.UtcNow;
                        Interlocked.Increment(ref _healthCheckFailures);
                    }
                }
            }
            catch
            {
                // Log error but don't crash the timer
            }
        }

        #endregion

        /// <summary>
        /// Disposes resources.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _healthCheckTimer.Dispose();
            _latencyProfiles.Clear();
        }
    }

    #region Supporting Types

    /// <summary>
    /// Latency profile for a storage backend.
    /// Tracks latency metrics and health status.
    /// </summary>
    public sealed class BackendLatencyProfile
    {
        private readonly List<double> _latencySamples = new();
        private readonly object _lock = new();

        /// <summary>Backend strategy ID.</summary>
        public string StrategyId { get; init; } = string.Empty;

        /// <summary>Whether the backend is currently healthy.</summary>
        public bool IsHealthy { get; set; } = true;

        /// <summary>Last health check timestamp.</summary>
        public DateTime? LastHealthCheck { get; set; }

        /// <summary>Last time this backend was selected.</summary>
        public DateTime? LastSelectedTime { get; set; }

        /// <summary>Number of times this backend has been selected.</summary>
        public long SelectionCount;

        /// <summary>Geographic region of this backend.</summary>
        public string? Region { get; set; }

        /// <summary>Maximum number of samples to retain.</summary>
        public int MaxSampleSize { get; init; } = 100;

        /// <summary>
        /// Gets the average latency in milliseconds.
        /// </summary>
        public double AverageLatencyMs
        {
            get
            {
                lock (_lock)
                {
                    return _latencySamples.Any() ? _latencySamples.Average() : 0;
                }
            }
        }

        /// <summary>
        /// Gets the 50th percentile (median) latency in milliseconds.
        /// </summary>
        public double P50LatencyMs => GetPercentile(0.50);

        /// <summary>
        /// Gets the 95th percentile latency in milliseconds.
        /// </summary>
        public double P95LatencyMs => GetPercentile(0.95);

        /// <summary>
        /// Gets the 99th percentile latency in milliseconds.
        /// </summary>
        public double P99LatencyMs => GetPercentile(0.99);

        /// <summary>
        /// Gets the minimum latency in milliseconds.
        /// </summary>
        public double MinLatencyMs
        {
            get
            {
                lock (_lock)
                {
                    return _latencySamples.Any() ? _latencySamples.Min() : 0;
                }
            }
        }

        /// <summary>
        /// Gets the maximum latency in milliseconds.
        /// </summary>
        public double MaxLatencyMs
        {
            get
            {
                lock (_lock)
                {
                    return _latencySamples.Any() ? _latencySamples.Max() : 0;
                }
            }
        }

        /// <summary>
        /// Gets the number of latency samples recorded.
        /// </summary>
        public int SampleCount
        {
            get
            {
                lock (_lock)
                {
                    return _latencySamples.Count;
                }
            }
        }

        /// <summary>
        /// Records a latency measurement.
        /// </summary>
        /// <param name="latencyMs">Latency in milliseconds.</param>
        public void RecordLatency(double latencyMs)
        {
            if (latencyMs < 0) return;

            lock (_lock)
            {
                _latencySamples.Add(latencyMs);

                // Keep only the most recent samples
                if (_latencySamples.Count > MaxSampleSize)
                {
                    _latencySamples.RemoveAt(0);
                }
            }
        }

        /// <summary>
        /// Gets the latency at a specific percentile.
        /// </summary>
        /// <param name="percentile">Percentile (0.0 to 1.0).</param>
        /// <returns>Latency in milliseconds at the specified percentile.</returns>
        private double GetPercentile(double percentile)
        {
            lock (_lock)
            {
                if (!_latencySamples.Any())
                {
                    return 0;
                }

                var sorted = _latencySamples.OrderBy(x => x).ToList();
                var index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
                index = Math.Max(0, Math.Min(index, sorted.Count - 1));
                return sorted[index];
            }
        }

        /// <summary>
        /// Clears all latency samples.
        /// </summary>
        public void ClearSamples()
        {
            lock (_lock)
            {
                _latencySamples.Clear();
            }
        }
    }

    #endregion
}
