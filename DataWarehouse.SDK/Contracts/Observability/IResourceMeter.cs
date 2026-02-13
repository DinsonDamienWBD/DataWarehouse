using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Observability
{
    /// <summary>
    /// Contract for per-plugin resource usage metering (OBS-04).
    /// Tracks memory, CPU, I/O, connections, and thread usage
    /// for individual plugins to enable resource monitoring and alerting.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Per-plugin resource metering")]
    public interface IResourceMeter
    {
        /// <summary>
        /// Gets the plugin ID this meter is tracking.
        /// </summary>
        string PluginId { get; }

        /// <summary>
        /// Gets a snapshot of current resource usage.
        /// </summary>
        /// <param name="ct">Cancellation token for the operation.</param>
        /// <returns>A snapshot of current resource usage.</returns>
        Task<ResourceSnapshot> GetCurrentUsageAsync(CancellationToken ct = default);

        /// <summary>
        /// Gets historical resource usage snapshots within the specified time window.
        /// </summary>
        /// <param name="window">The time window to retrieve history for.</param>
        /// <param name="ct">Cancellation token for the operation.</param>
        /// <returns>A read-only list of resource snapshots.</returns>
        Task<IReadOnlyList<ResourceSnapshot>> GetHistoryAsync(TimeSpan window, CancellationToken ct = default);

        /// <summary>
        /// Records a resource allocation.
        /// </summary>
        /// <param name="type">The type of resource allocated.</param>
        /// <param name="amount">The amount allocated (bytes for memory/IO, count for connections/threads).</param>
        void RecordAllocation(ResourceType type, long amount);

        /// <summary>
        /// Records a resource deallocation.
        /// </summary>
        /// <param name="type">The type of resource deallocated.</param>
        /// <param name="amount">The amount deallocated.</param>
        void RecordDeallocation(ResourceType type, long amount);

        /// <summary>
        /// Raised when a resource usage alert is triggered (limit exceeded).
        /// </summary>
        event Action<ResourceAlert>? OnResourceAlert;
    }

    /// <summary>
    /// A point-in-time snapshot of a plugin's resource usage.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Per-plugin resource metering")]
    public record ResourceSnapshot
    {
        /// <summary>
        /// The plugin being metered.
        /// </summary>
        public required string PluginId { get; init; }

        /// <summary>
        /// Memory usage in bytes.
        /// </summary>
        public required long MemoryBytes { get; init; }

        /// <summary>
        /// CPU usage as a percentage (0.0 to 100.0).
        /// </summary>
        public required double CpuPercent { get; init; }

        /// <summary>
        /// Total bytes read.
        /// </summary>
        public required long IoReadBytes { get; init; }

        /// <summary>
        /// Total bytes written.
        /// </summary>
        public required long IoWriteBytes { get; init; }

        /// <summary>
        /// Number of active connections.
        /// </summary>
        public required int ActiveConnections { get; init; }

        /// <summary>
        /// Number of active threads.
        /// </summary>
        public required int ActiveThreads { get; init; }

        /// <summary>
        /// When this snapshot was taken.
        /// </summary>
        public required DateTimeOffset Timestamp { get; init; }
    }

    /// <summary>
    /// Types of resources that can be metered.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Per-plugin resource metering")]
    public enum ResourceType
    {
        /// <summary>Memory usage in bytes.</summary>
        Memory,
        /// <summary>CPU usage.</summary>
        Cpu,
        /// <summary>I/O read bytes.</summary>
        IoRead,
        /// <summary>I/O write bytes.</summary>
        IoWrite,
        /// <summary>Active connections.</summary>
        Connections,
        /// <summary>Active threads.</summary>
        Threads
    }

    /// <summary>
    /// Resource usage limits for a plugin.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Per-plugin resource metering")]
    public record ResourceLimits
    {
        /// <summary>
        /// Maximum memory usage in bytes.
        /// </summary>
        public long MaxMemoryBytes { get; init; }

        /// <summary>
        /// Maximum CPU usage as a percentage (0.0 to 100.0).
        /// </summary>
        public double MaxCpuPercent { get; init; }

        /// <summary>
        /// Maximum number of active connections.
        /// </summary>
        public int MaxConnections { get; init; }

        /// <summary>
        /// Maximum number of active threads.
        /// </summary>
        public int MaxThreads { get; init; }
    }

    /// <summary>
    /// An alert indicating a resource limit has been exceeded.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Per-plugin resource metering")]
    public record ResourceAlert
    {
        /// <summary>
        /// The plugin that exceeded the limit.
        /// </summary>
        public required string PluginId { get; init; }

        /// <summary>
        /// The type of resource that exceeded the limit.
        /// </summary>
        public required ResourceType Type { get; init; }

        /// <summary>
        /// The current value of the resource.
        /// </summary>
        public required long CurrentValue { get; init; }

        /// <summary>
        /// The limit that was exceeded.
        /// </summary>
        public required long LimitValue { get; init; }

        /// <summary>
        /// Severity of the alert.
        /// </summary>
        public required ResourceAlertSeverity Severity { get; init; }

        /// <summary>
        /// When the alert was raised.
        /// </summary>
        public required DateTimeOffset Timestamp { get; init; }
    }

    /// <summary>
    /// Severity levels for resource alerts.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Per-plugin resource metering")]
    public enum ResourceAlertSeverity
    {
        /// <summary>Resource usage is approaching the limit.</summary>
        Warning,
        /// <summary>Resource usage has exceeded the limit.</summary>
        Critical
    }
}
