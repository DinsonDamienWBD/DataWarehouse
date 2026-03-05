using DataWarehouse.SDK.Contracts;
using System.Diagnostics;

namespace DataWarehouse.Kernel.Infrastructure
{
    /// <summary>
    /// Monitors memory pressure and signals when throttling is needed.
    /// Helps prevent OOM situations by providing early warning.
    /// </summary>
    public sealed class MemoryPressureMonitor : IMemoryPressureMonitor, IDisposable
    {
        private readonly Timer _monitorTimer;
        private readonly object _lock = new();
        private readonly double _elevatedThreshold;
        private readonly double _highThreshold;
        private readonly double _criticalThreshold;

        private MemoryPressureLevel _currentLevel = MemoryPressureLevel.Normal;
        private MemoryStatistics _lastStats;
        private bool _disposed;

        public event Action<MemoryPressureLevel>? OnPressureChanged;

        public MemoryPressureLevel CurrentLevel
        {
            get
            {
                lock (_lock)
                {
                    return _currentLevel;
                }
            }
        }

        public bool ShouldThrottle => CurrentLevel >= MemoryPressureLevel.High;

        public MemoryPressureMonitor(
            TimeSpan? checkInterval = null,
            double elevatedThreshold = 0.70,
            double highThreshold = 0.85,
            double criticalThreshold = 0.95)
        {
            _elevatedThreshold = elevatedThreshold;
            _highThreshold = highThreshold;
            _criticalThreshold = criticalThreshold;

            _lastStats = CollectStatistics();

            var interval = checkInterval ?? TimeSpan.FromSeconds(5);
            _monitorTimer = new Timer(OnTimerTick, null, interval, interval);

            // Register for GC notifications
            try
            {
                GC.RegisterForFullGCNotification(10, 10);
                _ = Task.Run(MonitorGCNotifications);
            }
            catch
            {
                // GC notifications not available on all platforms
            }
        }

        private void OnTimerTick(object? state)
        {
            if (_disposed) return;

            var stats = CollectStatistics();
            var newLevel = DetermineLevel(stats);

            lock (_lock)
            {
                _lastStats = stats;

                if (newLevel != _currentLevel)
                {
                    _currentLevel = newLevel;
                    OnPressureChanged?.Invoke(newLevel);
                }
            }
        }

        private async Task MonitorGCNotifications()
        {
            while (!_disposed)
            {
                try
                {
                    var status = GC.WaitForFullGCApproach();
                    if (status == GCNotificationStatus.Succeeded)
                    {
                        // Full GC is approaching - this often means memory pressure
                        lock (_lock)
                        {
                            if (_currentLevel < MemoryPressureLevel.Elevated)
                            {
                                _currentLevel = MemoryPressureLevel.Elevated;
                                OnPressureChanged?.Invoke(_currentLevel);
                            }
                        }
                    }

                    status = GC.WaitForFullGCComplete();
                    if (status == GCNotificationStatus.Succeeded)
                    {
                        // Full GC completed - re-evaluate
                        OnTimerTick(null);
                    }
                }
                catch (InvalidOperationException)
                {
                    // GC notifications not enabled
                    break;
                }
                catch
                {
                    await Task.Delay(1000);
                }
            }
        }

        private MemoryPressureLevel DetermineLevel(MemoryStatistics stats)
        {
            if (stats.UsagePercent >= _criticalThreshold * 100)
                return MemoryPressureLevel.Critical;

            if (stats.UsagePercent >= _highThreshold * 100)
                return MemoryPressureLevel.High;

            if (stats.UsagePercent >= _elevatedThreshold * 100)
                return MemoryPressureLevel.Elevated;

            return MemoryPressureLevel.Normal;
        }

        public MemoryStatistics GetStatistics()
        {
            lock (_lock)
            {
                return _lastStats;
            }
        }

        private MemoryStatistics CollectStatistics()
        {
            var gcMemory = GC.GetTotalMemory(false);
            var gcInfo = GC.GetGCMemoryInfo();

            // Get process memory
            long totalMemory;
            long availableMemory;

            try
            {
                using var process = Process.GetCurrentProcess();
                totalMemory = gcInfo.TotalAvailableMemoryBytes;
                availableMemory = totalMemory - process.WorkingSet64;

                if (totalMemory <= 0)
                {
                    // Fallback for platforms where this info isn't available
                    totalMemory = Environment.WorkingSet * 4; // Rough estimate
                    availableMemory = totalMemory - Environment.WorkingSet;
                }
            }
            catch (Exception ex)
            {
                // Log the failure so operators are aware memory metrics may be inaccurate.
                // Using Trace (no logger dependency) to surface persistent GC API failures (finding 948).
                Trace.TraceWarning($"[MemoryPressureMonitor] Failed to query process memory; using fallback estimate. {ex.GetType().Name}: {ex.Message}");
                totalMemory = gcInfo.TotalAvailableMemoryBytes > 0
                    ? gcInfo.TotalAvailableMemoryBytes
                    : 8L * 1024 * 1024 * 1024; // Assume 8GB if we can't determine
                availableMemory = totalMemory - gcMemory;
            }

            var usedMemory = totalMemory - availableMemory;

            return new MemoryStatistics
            {
                TotalMemoryBytes = totalMemory,
                UsedMemoryBytes = usedMemory,
                AvailableMemoryBytes = availableMemory,
                UsagePercent = totalMemory > 0 ? (double)usedMemory / totalMemory * 100 : 0,
                GCTotalMemory = gcMemory,
                Gen0Collections = GC.CollectionCount(0),
                Gen1Collections = GC.CollectionCount(1),
                Gen2Collections = GC.CollectionCount(2),
                Timestamp = DateTime.UtcNow
            };
        }

        public void RequestResourceRelease()
        {
            // Force garbage collection
            GC.Collect(2, GCCollectionMode.Optimized, false);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _monitorTimer.Dispose();

            try
            {
                GC.CancelFullGCNotification();
            }
            catch
            {
                // Ignore
            }
        }
    }
}
