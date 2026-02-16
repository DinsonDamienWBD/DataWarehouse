using DataWarehouse.SDK.Contracts;
using System;

namespace DataWarehouse.SDK.Hardware
{
    /// <summary>
    /// Factory for creating platform-appropriate hardware probes.
    /// </summary>
    [SdkCompatibility("3.0.0", Notes = "Phase 32: Hardware probe factory (HAL-02)")]
    public static class HardwareProbeFactory
    {
        /// <summary>
        /// Creates a hardware probe for the current platform.
        /// </summary>
        /// <returns>
        /// A platform-specific hardware probe (Windows, Linux, macOS),
        /// or <see cref="NullHardwareProbe"/> if the platform is not supported.
        /// </returns>
        public static IHardwareProbe Create()
        {
            if (OperatingSystem.IsWindows())
            {
                return new WindowsHardwareProbe();
            }

            if (OperatingSystem.IsLinux())
            {
                return new LinuxHardwareProbe();
            }

            if (OperatingSystem.IsMacOS())
            {
                return new MacOsHardwareProbe();
            }

            return new NullHardwareProbe();
        }
    }
}
