using DataWarehouse.SDK.Contracts;
using System;

namespace DataWarehouse.SDK.Hardware
{
    /// <summary>
    /// Marks a class as a dynamically loadable storage driver.
    /// </summary>
    /// <remarks>
    /// Apply this attribute to classes that implement IStorageStrategy (or a derived interface)
    /// to make them discoverable by <see cref="IDriverLoader"/>. The loader scans assemblies
    /// for this attribute and can load/unload drivers dynamically without recompilation.
    /// <para>
    /// <see cref="SupportedDevices"/> should match the <see cref="HardwareDeviceType"/> flags
    /// of devices this driver can handle. When <see cref="AutoLoad"/> is true, the driver
    /// will be loaded automatically when matching hardware is detected via <see cref="IHardwareProbe"/>.
    /// </para>
    /// <para>
    /// Example:
    /// <code>
    /// [StorageDriver("Samsung NVMe Driver",
    ///     SupportedDevices = HardwareDeviceType.NvmeController | HardwareDeviceType.NvmeNamespace,
    ///     Version = "1.0.0",
    ///     AutoLoad = true)]
    /// public class SamsungNvmeDriver : IStorageStrategy { }
    /// </code>
    /// </para>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    [SdkCompatibility("3.0.0", Notes = "Phase 32: Storage driver discovery attribute (HAL-04)")]
    public sealed class StorageDriverAttribute : Attribute
    {
        /// <summary>
        /// Gets the human-readable driver name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets or sets the hardware device types this driver supports.
        /// </summary>
        /// <remarks>
        /// Use bitwise OR to combine multiple device types (e.g., <c>NvmeController | NvmeNamespace</c>).
        /// </remarks>
        public HardwareDeviceType SupportedDevices { get; set; }

        /// <summary>
        /// Gets or sets the driver version string.
        /// </summary>
        public string? Version { get; set; }

        /// <summary>
        /// Gets or sets the driver description.
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// Gets or sets the minimum platform version required for this driver.
        /// </summary>
        /// <remarks>
        /// Format examples: "windows10.0.19041", "linux5.10", "macos12.0".
        /// If null, the driver is assumed to support all platforms.
        /// </remarks>
        public string? MinimumPlatform { get; set; }

        /// <summary>
        /// Gets or sets whether the driver should be auto-loaded when supported hardware is detected.
        /// </summary>
        /// <remarks>
        /// Default is <c>true</c>. Set to <c>false</c> for drivers that should only be loaded explicitly.
        /// </remarks>
        public bool AutoLoad { get; set; } = true;

        /// <summary>
        /// Initializes a new instance of the <see cref="StorageDriverAttribute"/> class.
        /// </summary>
        /// <param name="name">The human-readable driver name (e.g., "Samsung NVMe Driver").</param>
        /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="name"/> is empty or whitespace.</exception>
        public StorageDriverAttribute(string name)
        {
            if (name == null)
                throw new ArgumentNullException(nameof(name));
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Driver name cannot be empty or whitespace.", nameof(name));

            Name = name;
        }
    }
}
