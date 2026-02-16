using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Hardware
{
    /// <summary>
    /// Null object pattern implementation for unsupported platforms.
    /// Returns empty results and never fires events.
    /// </summary>
    [SdkCompatibility("3.0.0", Notes = "Phase 32: Fallback probe for unsupported platforms")]
    public sealed class NullHardwareProbe : IHardwareProbe
    {
        /// <inheritdoc />
        public Task<IReadOnlyList<HardwareDevice>> DiscoverAsync(HardwareDeviceType? typeFilter = null, CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<HardwareDevice>>(Array.Empty<HardwareDevice>());
        }

        /// <inheritdoc />
        public Task<HardwareDevice?> GetDeviceAsync(string deviceId, CancellationToken ct = default)
        {
            return Task.FromResult<HardwareDevice?>(null);
        }

        /// <inheritdoc />
        public event EventHandler<HardwareChangeEventArgs>? OnHardwareChanged
        {
            add { }
            remove { }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            // No resources to dispose
        }
    }
}
