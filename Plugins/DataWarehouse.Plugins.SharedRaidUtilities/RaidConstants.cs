namespace DataWarehouse.Plugins.SharedRaidUtilities
{
    /// <summary>
    /// Common RAID constants shared across all RAID plugin implementations.
    /// Provides standard minimum device requirements and capacity factors.
    /// </summary>
    public static class RaidConstants
    {
        /// <summary>
        /// Minimum number of devices required for RAID 0 (striping).
        /// </summary>
        public const int Raid0MinDevices = 2;

        /// <summary>
        /// Minimum number of devices required for RAID 1 (mirroring).
        /// </summary>
        public const int Raid1MinDevices = 2;

        /// <summary>
        /// Minimum number of devices required for RAID 2 (bit-level striping with Hamming code).
        /// </summary>
        public const int Raid2MinDevices = 3;

        /// <summary>
        /// Minimum number of devices required for RAID 3 (byte-level striping with dedicated parity).
        /// </summary>
        public const int Raid3MinDevices = 3;

        /// <summary>
        /// Minimum number of devices required for RAID 4 (block-level striping with dedicated parity).
        /// </summary>
        public const int Raid4MinDevices = 3;

        /// <summary>
        /// Minimum number of devices required for RAID 5 (block-level striping with distributed parity).
        /// </summary>
        public const int Raid5MinDevices = 3;

        /// <summary>
        /// Minimum number of devices required for RAID 6 (block-level striping with dual distributed parity).
        /// </summary>
        public const int Raid6MinDevices = 4;

        /// <summary>
        /// Minimum number of devices required for RAID 10 (mirrored striping).
        /// </summary>
        public const int Raid10MinDevices = 4;

        /// <summary>
        /// Minimum number of devices required for RAID-Z1 (ZFS single parity).
        /// </summary>
        public const int RaidZ1MinDevices = 2;

        /// <summary>
        /// Minimum number of devices required for RAID-Z2 (ZFS dual parity).
        /// </summary>
        public const int RaidZ2MinDevices = 3;

        /// <summary>
        /// Minimum number of devices required for RAID-Z3 (ZFS triple parity).
        /// </summary>
        public const int RaidZ3MinDevices = 4;

        /// <summary>
        /// Default stripe size in bytes (64 KB).
        /// </summary>
        public const int DefaultStripeSize = 64 * 1024;

        /// <summary>
        /// Minimum stripe size in bytes (4 KB).
        /// </summary>
        public const int MinStripeSize = 4 * 1024;

        /// <summary>
        /// Maximum stripe size in bytes (16 MB).
        /// </summary>
        public const int MaxStripeSize = 16 * 1024 * 1024;

        /// <summary>
        /// Capacity factor for RAID 0 - all space is usable (100%).
        /// </summary>
        public const double Raid0CapacityFactor = 1.0;

        /// <summary>
        /// Capacity factor for RAID 1 - 50% of space is usable (mirroring).
        /// </summary>
        public const double Raid1CapacityFactor = 0.5;

        /// <summary>
        /// Capacity factor for RAID 10 - 50% of space is usable (mirroring).
        /// </summary>
        public const double Raid10CapacityFactor = 0.5;

        /// <summary>
        /// Calculates the usable capacity factor for RAID 5.
        /// Formula: (N-1)/N where N is the number of devices.
        /// </summary>
        /// <param name="deviceCount">Number of devices in the RAID array.</param>
        /// <returns>Capacity factor (0.0 to 1.0).</returns>
        public static double GetRaid5CapacityFactor(int deviceCount)
        {
            if (deviceCount < Raid5MinDevices)
                throw new ArgumentException($"RAID 5 requires at least {Raid5MinDevices} devices.");
            return (deviceCount - 1.0) / deviceCount;
        }

        /// <summary>
        /// Calculates the usable capacity factor for RAID 6.
        /// Formula: (N-2)/N where N is the number of devices.
        /// </summary>
        /// <param name="deviceCount">Number of devices in the RAID array.</param>
        /// <returns>Capacity factor (0.0 to 1.0).</returns>
        public static double GetRaid6CapacityFactor(int deviceCount)
        {
            if (deviceCount < Raid6MinDevices)
                throw new ArgumentException($"RAID 6 requires at least {Raid6MinDevices} devices.");
            return (deviceCount - 2.0) / deviceCount;
        }

        /// <summary>
        /// Calculates the usable capacity factor for RAID-Z1.
        /// Formula: (N-1)/N where N is the number of devices.
        /// </summary>
        /// <param name="deviceCount">Number of devices in the RAID array.</param>
        /// <returns>Capacity factor (0.0 to 1.0).</returns>
        public static double GetRaidZ1CapacityFactor(int deviceCount)
        {
            if (deviceCount < RaidZ1MinDevices)
                throw new ArgumentException($"RAID-Z1 requires at least {RaidZ1MinDevices} devices.");
            return (deviceCount - 1.0) / deviceCount;
        }

        /// <summary>
        /// Calculates the usable capacity factor for RAID-Z2.
        /// Formula: (N-2)/N where N is the number of devices.
        /// </summary>
        /// <param name="deviceCount">Number of devices in the RAID array.</param>
        /// <returns>Capacity factor (0.0 to 1.0).</returns>
        public static double GetRaidZ2CapacityFactor(int deviceCount)
        {
            if (deviceCount < RaidZ2MinDevices)
                throw new ArgumentException($"RAID-Z2 requires at least {RaidZ2MinDevices} devices.");
            return (deviceCount - 2.0) / deviceCount;
        }

        /// <summary>
        /// Calculates the usable capacity factor for RAID-Z3.
        /// Formula: (N-3)/N where N is the number of devices.
        /// </summary>
        /// <param name="deviceCount">Number of devices in the RAID array.</param>
        /// <returns>Capacity factor (0.0 to 1.0).</returns>
        public static double GetRaidZ3CapacityFactor(int deviceCount)
        {
            if (deviceCount < RaidZ3MinDevices)
                throw new ArgumentException($"RAID-Z3 requires at least {RaidZ3MinDevices} devices.");
            return (deviceCount - 3.0) / deviceCount;
        }

        /// <summary>
        /// Calculates the number of parity blocks for a given RAID level.
        /// </summary>
        /// <param name="raidLevel">RAID level name (e.g., "RAID5", "RAID6", "RAIDZ2").</param>
        /// <returns>Number of parity blocks.</returns>
        public static int GetParityBlockCount(string raidLevel)
        {
            return raidLevel.ToUpperInvariant() switch
            {
                "RAID0" => 0,
                "RAID1" => 0,  // Mirroring, not parity
                "RAID2" => 3,  // Hamming code
                "RAID3" => 1,
                "RAID4" => 1,
                "RAID5" => 1,
                "RAID6" => 2,
                "RAID10" => 0, // Mirroring, not parity
                "RAIDZ1" or "RAID-Z1" => 1,
                "RAIDZ2" or "RAID-Z2" => 2,
                "RAIDZ3" or "RAID-Z3" => 3,
                _ => throw new ArgumentException($"Unknown RAID level: {raidLevel}")
            };
        }

        /// <summary>
        /// Determines if a RAID level supports hot spare functionality.
        /// </summary>
        /// <param name="raidLevel">RAID level name.</param>
        /// <returns>True if hot spares are supported.</returns>
        public static bool SupportsHotSpare(string raidLevel)
        {
            return raidLevel.ToUpperInvariant() switch
            {
                "RAID0" => false,  // No redundancy
                "RAID1" => true,
                "RAID5" => true,
                "RAID6" => true,
                "RAID10" => true,
                "RAIDZ1" or "RAID-Z1" => true,
                "RAIDZ2" or "RAID-Z2" => true,
                "RAIDZ3" or "RAID-Z3" => true,
                _ => false
            };
        }

        /// <summary>
        /// Determines the maximum number of simultaneous drive failures that can be tolerated.
        /// </summary>
        /// <param name="raidLevel">RAID level name.</param>
        /// <returns>Maximum number of drive failures that can be tolerated.</returns>
        public static int GetFailureTolerance(string raidLevel)
        {
            return raidLevel.ToUpperInvariant() switch
            {
                "RAID0" => 0,
                "RAID1" => 1,
                "RAID5" => 1,
                "RAID6" => 2,
                "RAID10" => 1,  // One drive per mirror pair
                "RAIDZ1" or "RAID-Z1" => 1,
                "RAIDZ2" or "RAID-Z2" => 2,
                "RAIDZ3" or "RAID-Z3" => 3,
                _ => 0
            };
        }
    }
}
