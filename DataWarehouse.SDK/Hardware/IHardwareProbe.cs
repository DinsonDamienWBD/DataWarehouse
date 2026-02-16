using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Hardware
{
    /// <summary>
    /// Interface for platform-specific hardware discovery.
    /// Implementations are platform-specific and should be obtained via <see cref="HardwareProbeFactory.Create"/>.
    /// </summary>
    /// <remarks>
    /// Hardware probes discover devices at runtime, enabling DataWarehouse to adapt to the
    /// available hardware capabilities. Probes support filtering by device type and provide
    /// event-based notifications for hot-plug devices (USB, Thunderbolt).
    /// </remarks>
    [SdkCompatibility("3.0.0", Notes = "Phase 32: Hardware discovery interface (HAL-02)")]
    public interface IHardwareProbe : IDisposable
    {
        /// <summary>
        /// Discovers all hardware devices, optionally filtered by type.
        /// </summary>
        /// <param name="typeFilter">
        /// Optional device type filter. If specified, only devices matching the flags are returned.
        /// Use null to discover all device types.
        /// </param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>
        /// A read-only list of discovered devices. Returns an empty list (never null) if no devices are found.
        /// </returns>
        Task<IReadOnlyList<HardwareDevice>> DiscoverAsync(HardwareDeviceType? typeFilter = null, CancellationToken ct = default);

        /// <summary>
        /// Gets a specific hardware device by ID.
        /// </summary>
        /// <param name="deviceId">The unique device identifier to retrieve.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The device if found; otherwise null.</returns>
        Task<HardwareDevice?> GetDeviceAsync(string deviceId, CancellationToken ct = default);

        /// <summary>
        /// Event fired when hardware is added, removed, or modified.
        /// Not all platforms support change notifications. Check platform-specific documentation.
        /// </summary>
        event EventHandler<HardwareChangeEventArgs>? OnHardwareChanged;
    }
}
