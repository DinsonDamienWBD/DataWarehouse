using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace DataWarehouse.SDK.Hardware
{
    /// <summary>
    /// Represents a hardware device discovered on the system.
    /// </summary>
    [SdkCompatibility("3.0.0", Notes = "Phase 32: Hardware device record (HAL-02)")]
    public sealed record HardwareDevice
    {
        /// <summary>
        /// Gets the unique device identifier.
        /// Platform-specific: PnP device ID on Windows, sysfs path on Linux.
        /// </summary>
        public required string DeviceId { get; init; }

        /// <summary>
        /// Gets the human-readable device name.
        /// </summary>
        public required string Name { get; init; }

        /// <summary>
        /// Gets the optional detailed description.
        /// </summary>
        public string? Description { get; init; }

        /// <summary>
        /// Gets the device type flags.
        /// </summary>
        public required HardwareDeviceType Type { get; init; }

        /// <summary>
        /// Gets the vendor name (e.g., "Intel", "Samsung").
        /// </summary>
        public string? Vendor { get; init; }

        /// <summary>
        /// Gets the PCI vendor ID or USB VID (hex string).
        /// </summary>
        public string? VendorId { get; init; }

        /// <summary>
        /// Gets the PCI device ID or USB PID (hex string).
        /// </summary>
        public string? ProductId { get; init; }

        /// <summary>
        /// Gets the current driver name.
        /// </summary>
        public string? DriverName { get; init; }

        /// <summary>
        /// Gets the OS device path (/dev/nvme0, \\.\PhysicalDrive0).
        /// </summary>
        public string? DevicePath { get; init; }

        /// <summary>
        /// Gets additional platform-specific properties.
        /// </summary>
        public IReadOnlyDictionary<string, string> Properties { get; init; } = ImmutableDictionary<string, string>.Empty;
    }

    /// <summary>
    /// Hardware change event type.
    /// </summary>
    [SdkCompatibility("3.0.0", Notes = "Phase 32: Hardware change event classification (HAL-02)")]
    public enum HardwareChangeType
    {
        /// <summary>Hardware device was added to the system.</summary>
        Added,

        /// <summary>Hardware device was removed from the system.</summary>
        Removed,

        /// <summary>Hardware device properties were modified.</summary>
        Modified
    }

    /// <summary>
    /// Event arguments for hardware change notifications.
    /// </summary>
    [SdkCompatibility("3.0.0", Notes = "Phase 32: Hardware change event args (HAL-02)")]
    public sealed record HardwareChangeEventArgs
    {
        /// <summary>
        /// Gets the device that changed.
        /// </summary>
        public required HardwareDevice Device { get; init; }

        /// <summary>
        /// Gets the type of change that occurred.
        /// </summary>
        public required HardwareChangeType ChangeType { get; init; }
    }
}
