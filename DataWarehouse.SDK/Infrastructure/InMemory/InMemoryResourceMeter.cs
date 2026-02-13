using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Observability;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Infrastructure.InMemory
{
    /// <summary>
    /// In-memory implementation of <see cref="IResourceMeter"/>.
    /// Tracks resource allocations and deallocations using atomic operations.
    /// Fires alerts when configurable limits are exceeded.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: In-memory implementation")]
    public sealed class InMemoryResourceMeter : IResourceMeter
    {
        private readonly ResourceLimits? _limits;
        private long _memoryBytes;
        private long _ioReadBytes;
        private long _ioWriteBytes;
        private long _connections;
        private long _threads;

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
            return Task.FromResult(CreateSnapshot());
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<ResourceSnapshot>> GetHistoryAsync(TimeSpan window, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            // In-memory: no history persistence, return current snapshot only
            return Task.FromResult<IReadOnlyList<ResourceSnapshot>>(new[] { CreateSnapshot() });
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

        private ResourceSnapshot CreateSnapshot() => new()
        {
            PluginId = PluginId,
            MemoryBytes = Interlocked.Read(ref _memoryBytes),
            CpuPercent = 0,
            IoReadBytes = Interlocked.Read(ref _ioReadBytes),
            IoWriteBytes = Interlocked.Read(ref _ioWriteBytes),
            ActiveConnections = (int)Interlocked.Read(ref _connections),
            ActiveThreads = (int)Interlocked.Read(ref _threads),
            Timestamp = DateTimeOffset.UtcNow
        };

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
