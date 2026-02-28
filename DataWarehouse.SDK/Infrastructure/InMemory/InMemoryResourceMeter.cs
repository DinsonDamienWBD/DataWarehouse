using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Observability;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Infrastructure.InMemory
{
    /// <summary>
    /// In-memory implementation of <see cref="IResourceMeter"/>.
    /// Tracks resource allocations and deallocations using atomic operations.
    /// Fires alerts when configurable limits are exceeded.
    /// Maintains a circular snapshot buffer for history queries (finding P2-462).
    /// CPU% is measured via process TotalProcessorTime delta (finding P2-463).
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: In-memory implementation")]
    public sealed class InMemoryResourceMeter : IResourceMeter
    {
        private const int HistoryCapacity = 120; // up to 120 snapshots (2 min at 1/s)

        private readonly ResourceLimits? _limits;
        private long _memoryBytes;
        private long _ioReadBytes;
        private long _ioWriteBytes;
        private long _connections;
        private long _threads;

        // CPU% tracking (finding P2-463): measure process CPU delta between snapshots
        private TimeSpan _lastCpuTime = TimeSpan.Zero;
        private long _lastCpuTimestampTicks = Stopwatch.GetTimestamp();
        private static readonly double s_processorCount = Environment.ProcessorCount;

        // Circular snapshot history for GetHistoryAsync (finding P2-462)
        private readonly ResourceSnapshot[] _history = new ResourceSnapshot[HistoryCapacity];
        private int _historyHead = 0; // next write index
        private int _historyCount = 0;
        private readonly object _historyLock = new();

        /// <summary>
        /// Initializes a new resource meter for a plugin.
        /// </summary>
        /// <param name="pluginId">The plugin identifier to meter.</param>
        /// <param name="limits">Optional resource limits for alerting.</param>
        public InMemoryResourceMeter(string pluginId, ResourceLimits? limits = null)
        {
            PluginId = pluginId;
            _limits = limits;
        }

        /// <inheritdoc />
        public string PluginId { get; }

        /// <inheritdoc />
        public event Action<ResourceAlert>? OnResourceAlert;

        /// <inheritdoc />
        public Task<ResourceSnapshot> GetCurrentUsageAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = CreateSnapshot();
            RecordSnapshotToHistory(snapshot);
            return Task.FromResult(snapshot);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<ResourceSnapshot>> GetHistoryAsync(TimeSpan window, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var cutoff = DateTimeOffset.UtcNow - window;
            lock (_historyLock)
            {
                var result = new List<ResourceSnapshot>(_historyCount);
                // Read from oldest to newest
                int start = _historyCount < HistoryCapacity ? 0 : _historyHead;
                for (int i = 0; i < _historyCount; i++)
                {
                    var s = _history[(start + i) % HistoryCapacity];
                    if (s.Timestamp >= cutoff)
                        result.Add(s);
                }
                if (result.Count == 0)
                    result.Add(CreateSnapshot()); // at minimum return current state
                return Task.FromResult<IReadOnlyList<ResourceSnapshot>>(result);
            }
        }

        /// <inheritdoc />
        public void RecordAllocation(ResourceType type, long amount)
        {
            var newValue = type switch
            {
                ResourceType.Memory => Interlocked.Add(ref _memoryBytes, amount),
                ResourceType.IoRead => Interlocked.Add(ref _ioReadBytes, amount),
                ResourceType.IoWrite => Interlocked.Add(ref _ioWriteBytes, amount),
                ResourceType.Connections => Interlocked.Add(ref _connections, amount),
                ResourceType.Threads => Interlocked.Add(ref _threads, amount),
                _ => 0
            };

            CheckLimits(type, newValue);
        }

        /// <inheritdoc />
        public void RecordDeallocation(ResourceType type, long amount)
        {
            switch (type)
            {
                case ResourceType.Memory: Interlocked.Add(ref _memoryBytes, -amount); break;
                case ResourceType.IoRead: Interlocked.Add(ref _ioReadBytes, -amount); break;
                case ResourceType.IoWrite: Interlocked.Add(ref _ioWriteBytes, -amount); break;
                case ResourceType.Connections: Interlocked.Add(ref _connections, -amount); break;
                case ResourceType.Threads: Interlocked.Add(ref _threads, -amount); break;
            }
        }

        private ResourceSnapshot CreateSnapshot()
        {
            // Measure CPU% via process TotalProcessorTime delta (finding P2-463)
            double cpuPercent = 0;
            try
            {
                var proc = Process.GetCurrentProcess();
                var nowCpu = proc.TotalProcessorTime;
                var nowTick = Stopwatch.GetTimestamp();
                var elapsedSec = (nowTick - _lastCpuTimestampTicks) / (double)Stopwatch.Frequency;
                if (elapsedSec > 0)
                {
                    var cpuUsedSec = (nowCpu - _lastCpuTime).TotalSeconds;
                    cpuPercent = Math.Min(100.0, cpuUsedSec / (elapsedSec * s_processorCount) * 100.0);
                }
                _lastCpuTime = nowCpu;
                _lastCpuTimestampTicks = nowTick;
            }
            catch { /* non-critical; fall back to 0 */ }

            return new ResourceSnapshot
            {
                PluginId = PluginId,
                MemoryBytes = Interlocked.Read(ref _memoryBytes),
                CpuPercent = cpuPercent,
                IoReadBytes = Interlocked.Read(ref _ioReadBytes),
                IoWriteBytes = Interlocked.Read(ref _ioWriteBytes),
                ActiveConnections = (int)Interlocked.Read(ref _connections),
                ActiveThreads = (int)Interlocked.Read(ref _threads),
                Timestamp = DateTimeOffset.UtcNow
            };
        }

        private void RecordSnapshotToHistory(ResourceSnapshot snapshot)
        {
            lock (_historyLock)
            {
                _history[_historyHead] = snapshot;
                _historyHead = (_historyHead + 1) % HistoryCapacity;
                if (_historyCount < HistoryCapacity) _historyCount++;
            }
        }

        private void CheckLimits(ResourceType type, long currentValue)
        {
            if (_limits == null) return;

            long limit = type switch
            {
                ResourceType.Memory => _limits.MaxMemoryBytes,
                ResourceType.Connections => _limits.MaxConnections,
                ResourceType.Threads => _limits.MaxThreads,
                _ => 0
            };

            if (limit > 0 && currentValue > limit)
            {
                OnResourceAlert?.Invoke(new ResourceAlert
                {
                    PluginId = PluginId,
                    Type = type,
                    CurrentValue = currentValue,
                    LimitValue = limit,
                    Severity = ResourceAlertSeverity.Critical,
                    Timestamp = DateTimeOffset.UtcNow
                });
            }
            else if (limit > 0 && currentValue > limit * 80 / 100)
            {
                OnResourceAlert?.Invoke(new ResourceAlert
                {
                    PluginId = PluginId,
                    Type = type,
                    CurrentValue = currentValue,
                    LimitValue = limit,
                    Severity = ResourceAlertSeverity.Warning,
                    Timestamp = DateTimeOffset.UtcNow
                });
            }
        }
    }
}
