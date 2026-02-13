using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.RAID
{
    /// <summary>
    /// Defines a strategy for RAID (Redundant Array of Independent Disks) levels including
    /// standard levels, advanced configurations, ZFS variations, and vendor-specific implementations.
    /// Provides stripe calculation, rebuild management, and health monitoring capabilities.
    /// </summary>
    public interface IRaidStrategy
    {
        /// <summary>
        /// Gets the RAID level implemented by this strategy.
        /// </summary>
        RaidLevel Level { get; }

        /// <summary>
        /// Gets the capabilities of this RAID strategy including performance characteristics
        /// and redundancy information.
        /// </summary>
        RaidCapabilities Capabilities { get; }

        /// <summary>
        /// Writes data across the RAID array using the appropriate striping and redundancy strategy.
        /// </summary>
        /// <param name="data">The data to write.</param>
        /// <param name="disks">The disks in the RAID array.</param>
        /// <param name="offset">The offset within the array to write to.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous write operation.</returns>
        Task WriteAsync(
            ReadOnlyMemory<byte> data,
            IEnumerable<DiskInfo> disks,
            long offset,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Reads data from the RAID array, reconstructing data from parity if necessary.
        /// </summary>
        /// <param name="disks">The disks in the RAID array.</param>
        /// <param name="offset">The offset within the array to read from.</param>
        /// <param name="length">The number of bytes to read.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The read data.</returns>
        Task<ReadOnlyMemory<byte>> ReadAsync(
            IEnumerable<DiskInfo> disks,
            long offset,
            int length,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Calculates the stripe distribution for a given data block across disks.
        /// </summary>
        /// <param name="blockIndex">The logical block index.</param>
        /// <param name="diskCount">The total number of disks in the array.</param>
        /// <returns>Stripe distribution information including disk assignments and parity locations.</returns>
        StripeInfo CalculateStripe(long blockIndex, int diskCount);

        /// <summary>
        /// Rebuilds a failed disk using data and parity from remaining healthy disks.
        /// </summary>
        /// <param name="failedDisk">The failed disk information.</param>
        /// <param name="healthyDisks">The healthy disks in the array.</param>
        /// <param name="targetDisk">The replacement disk to rebuild to.</param>
        /// <param name="progressCallback">Optional callback for rebuild progress reporting.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous rebuild operation.</returns>
        Task RebuildDiskAsync(
            DiskInfo failedDisk,
            IEnumerable<DiskInfo> healthyDisks,
            DiskInfo targetDisk,
            IProgress<RebuildProgress>? progressCallback = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Checks the health status of the RAID array including degraded disks and rebuild status.
        /// </summary>
        /// <param name="disks">The disks in the array.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The health status of the array.</returns>
        Task<RaidHealth> CheckHealthAsync(
            IEnumerable<DiskInfo> disks,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Calculates the usable capacity of the RAID array given the disk configuration.
        /// </summary>
        /// <param name="disks">The disks in the array.</param>
        /// <returns>The usable capacity in bytes.</returns>
        long CalculateUsableCapacity(IEnumerable<DiskInfo> disks);

        /// <summary>
        /// Determines if the array can survive the specified number of disk failures.
        /// </summary>
        /// <param name="diskCount">The total number of disks.</param>
        /// <param name="failures">The number of failures to test.</param>
        /// <returns>True if the array can survive the failures.</returns>
        bool CanSurviveFailures(int diskCount, int failures);
    }

    /// <summary>
    /// Describes the capabilities and performance characteristics of a RAID strategy.
    /// </summary>
    /// <param name="RedundancyLevel">The number of disk failures the array can survive.</param>
    /// <param name="MinDisks">The minimum number of disks required for this RAID level.</param>
    /// <param name="MaxDisks">The maximum number of disks supported (null for unlimited).</param>
    /// <param name="StripeSize">The default stripe size in bytes.</param>
    /// <param name="EstimatedRebuildTimePerTB">Estimated rebuild time per terabyte of data.</param>
    /// <param name="ReadPerformanceMultiplier">Read performance multiplier relative to single disk.</param>
    /// <param name="WritePerformanceMultiplier">Write performance multiplier relative to single disk.</param>
    /// <param name="CapacityEfficiency">Percentage of total capacity that is usable (0.0 to 1.0).</param>
    /// <param name="SupportsHotSpare">Indicates if hot spare disks are supported.</param>
    /// <param name="SupportsOnlineExpansion">Indicates if the array can be expanded online.</param>
    /// <param name="RequiresUniformDiskSize">Indicates if all disks must be the same size.</param>
    public record RaidCapabilities(
        int RedundancyLevel,
        int MinDisks,
        int? MaxDisks,
        int StripeSize,
        TimeSpan EstimatedRebuildTimePerTB,
        double ReadPerformanceMultiplier,
        double WritePerformanceMultiplier,
        double CapacityEfficiency,
        bool SupportsHotSpare,
        bool SupportsOnlineExpansion,
        bool RequiresUniformDiskSize);

    /// <summary>
    /// Comprehensive enumeration of RAID levels including standard, advanced, ZFS, erasure coding,
    /// vendor-specific, and extended configurations.
    /// </summary>
    public enum RaidLevel
    {
        // Standard RAID Levels
        /// <summary>
        /// RAID 0 - Striping without redundancy. High performance, no fault tolerance.
        /// </summary>
        Raid0 = 0,

        /// <summary>
        /// RAID 1 - Mirroring. 50% capacity efficiency, survives 1 disk failure.
        /// </summary>
        Raid1 = 1,

        /// <summary>
        /// RAID 2 - Bit-level striping with Hamming code. Rarely used.
        /// </summary>
        Raid2 = 2,

        /// <summary>
        /// RAID 3 - Byte-level striping with dedicated parity disk.
        /// </summary>
        Raid3 = 3,

        /// <summary>
        /// RAID 4 - Block-level striping with dedicated parity disk.
        /// </summary>
        Raid4 = 4,

        /// <summary>
        /// RAID 5 - Block-level striping with distributed parity. Survives 1 disk failure.
        /// </summary>
        Raid5 = 5,

        /// <summary>
        /// RAID 6 - Block-level striping with double distributed parity. Survives 2 disk failures.
        /// </summary>
        Raid6 = 6,

        // Nested/Hybrid RAID Levels
        /// <summary>
        /// RAID 10 (1+0) - Striped mirrors. High performance and redundancy.
        /// </summary>
        Raid10 = 10,

        /// <summary>
        /// RAID 01 (0+1) - Mirrored stripes.
        /// </summary>
        Raid01 = 11,

        /// <summary>
        /// RAID 50 (5+0) - Striped RAID 5 arrays.
        /// </summary>
        Raid50 = 50,

        /// <summary>
        /// RAID 60 (6+0) - Striped RAID 6 arrays.
        /// </summary>
        Raid60 = 60,

        /// <summary>
        /// RAID 100 (10+0) - Striped RAID 10 arrays.
        /// </summary>
        Raid100 = 100,

        // ZFS RAID Variants
        /// <summary>
        /// RAID-Z1 - ZFS variant of RAID 5 with single parity.
        /// </summary>
        RaidZ1 = 1001,

        /// <summary>
        /// RAID-Z2 - ZFS variant of RAID 6 with double parity.
        /// </summary>
        RaidZ2 = 1002,

        /// <summary>
        /// RAID-Z3 - ZFS triple parity. Survives 3 disk failures.
        /// </summary>
        RaidZ3 = 1003,

        // Erasure Coding
        /// <summary>
        /// Reed-Solomon erasure coding with configurable data and parity chunks.
        /// </summary>
        ReedSolomon = 2000,

        /// <summary>
        /// Local Reconstruction Code (LRC) - Microsoft Azure variant.
        /// </summary>
        LocalReconstructionCode = 2001,

        /// <summary>
        /// Intel ISA-L erasure coding.
        /// </summary>
        IsalErasure = 2002,

        // Vendor-Specific
        /// <summary>
        /// NetApp RAID-DP (Double Parity). Similar to RAID 6.
        /// </summary>
        NetAppRaidDp = 3000,

        /// <summary>
        /// NetApp RAID-TEC (Triple Erasure Coding). Survives 3 disk failures.
        /// </summary>
        NetAppRaidTec = 3001,

        /// <summary>
        /// Synology Hybrid RAID (SHR) - 1 disk redundancy.
        /// </summary>
        SynologyShr = 3100,

        /// <summary>
        /// Synology Hybrid RAID 2 (SHR-2) - 2 disk redundancy.
        /// </summary>
        SynologyShr2 = 3101,

        /// <summary>
        /// Drobo BeyondRAID - Dynamic RAID with mixed disk sizes.
        /// </summary>
        DroboBeyondRaid = 3200,

        /// <summary>
        /// QNAP Static Volume - Similar to RAID 6.
        /// </summary>
        QnapStaticVolume = 3300,

        /// <summary>
        /// Unraid parity protection with 1 parity disk.
        /// </summary>
        UnraidSingle = 3400,

        /// <summary>
        /// Unraid parity protection with 2 parity disks.
        /// </summary>
        UnraidDual = 3401,

        // Extended RAID Levels
        /// <summary>
        /// RAID 7 - Proprietary level with embedded real-time OS and cache.
        /// </summary>
        Raid7 = 7,

        /// <summary>
        /// RAID 71 - Nested RAID 7+1.
        /// </summary>
        Raid71 = 71,

        /// <summary>
        /// RAID 72 - Nested RAID 7+2.
        /// </summary>
        Raid72 = 72,

        /// <summary>
        /// Matrix RAID - Partitions disks for multiple RAID levels.
        /// </summary>
        MatrixRaid = 8000,

        /// <summary>
        /// RAID 1E - Striped mirrors with odd number of disks.
        /// </summary>
        Raid1E = 13,

        /// <summary>
        /// RAID 5E - RAID 5 with integrated hot spare.
        /// </summary>
        Raid5E = 53,

        /// <summary>
        /// RAID 5EE - Enhanced RAID 5E with distributed hot spare.
        /// </summary>
        Raid5EE = 54,

        /// <summary>
        /// RAID 6E - RAID 6 with integrated hot spare.
        /// </summary>
        Raid6E = 63,

        // Adaptive/Smart RAID
        /// <summary>
        /// Adaptive RAID - Automatically adjusts level based on workload.
        /// </summary>
        AdaptiveRaid = 9000,

        /// <summary>
        /// Self-Healing RAID - AI-driven predictive failure and automatic rebuild.
        /// </summary>
        SelfHealingRaid = 9001,

        /// <summary>
        /// Tiered RAID - Combines SSDs and HDDs with automatic tiering.
        /// </summary>
        TieredRaid = 9002,

        // Research/Experimental
        /// <summary>
        /// RAID-X - Experimental level with variable parity.
        /// </summary>
        RaidX = 9100,

        /// <summary>
        /// Parity Declustering - Distributes parity across all disks for faster rebuild.
        /// </summary>
        ParityDeclustering = 9101,

        /// <summary>
        /// RAID-S (Sparse RAID) - Optimized for write-once workloads.
        /// </summary>
        RaidS = 9102,

        // Additional Nested/Hybrid
        /// <summary>
        /// RAID 03 (0+3) - Striped RAID 3 arrays.
        /// </summary>
        Raid03 = 30,

        // Additional Extended Levels
        /// <summary>
        /// N-Way Mirror - Mirrors data across N disks (N > 2).
        /// </summary>
        NWayMirror = 12,

        /// <summary>
        /// JBOD - Just a Bunch of Disks. No redundancy, disks concatenated.
        /// </summary>
        Jbod = 9200,

        /// <summary>
        /// Crypto RAID - Encrypted RAID with per-stripe key derivation.
        /// </summary>
        CryptoRaid = 9201,

        /// <summary>
        /// DDP/DUP - Dynamic Disk Pool / Data Unit Protection.
        /// </summary>
        Ddp = 9202,

        /// <summary>
        /// DUP - Duplicate data protection.
        /// </summary>
        Dup = 9203,

        /// <summary>
        /// SPAN/BIG - Disk spanning without striping.
        /// </summary>
        SpanBig = 9204,

        /// <summary>
        /// MAID - Massive Array of Idle Disks. Power-managed RAID.
        /// </summary>
        Maid = 9205,

        /// <summary>
        /// Linear RAID - Simple concatenation without striping.
        /// </summary>
        Linear = 9206,

        // Additional Vendor-Specific
        /// <summary>
        /// StorageTek RAID 7 - Asynchronous RAID with NVRAM write cache.
        /// </summary>
        StorageTekRaid7 = 3500,

        /// <summary>
        /// FlexRAID FR - Snapshot-based flexible parity protection.
        /// </summary>
        FlexRaidFr = 3501,

        // Additional Erasure Coding
        /// <summary>
        /// LDPC - Low-Density Parity-Check codes for erasure coding.
        /// </summary>
        Ldpc = 2003,

        /// <summary>
        /// Fountain Codes - Rateless erasure codes (LT/Raptor).
        /// </summary>
        FountainCodes = 2004
    }

    /// <summary>
    /// Describes the health status of a RAID array including degraded disks, rebuild progress,
    /// and SMART data indicators.
    /// </summary>
    /// <param name="Status">The overall health status of the array.</param>
    /// <param name="DegradedDisks">List of disks that have failed or are failing.</param>
    /// <param name="HealthyDisks">List of healthy disks in the array.</param>
    /// <param name="RebuildInProgress">Indicates if a rebuild operation is currently running.</param>
    /// <param name="RebuildProgress">The current rebuild progress (0.0 to 1.0).</param>
    /// <param name="EstimatedRebuildCompletion">Estimated time until rebuild completion.</param>
    /// <param name="SmartWarnings">List of disks with SMART warnings.</param>
    /// <param name="PerformanceDegradation">Current performance degradation percentage (0.0 to 1.0).</param>
    /// <param name="LastChecked">Timestamp of the last health check.</param>
    public record RaidHealth(
        RaidHealthStatus Status,
        IReadOnlyList<DiskInfo> DegradedDisks,
        IReadOnlyList<DiskInfo> HealthyDisks,
        bool RebuildInProgress,
        double RebuildProgress,
        TimeSpan? EstimatedRebuildCompletion,
        IReadOnlyList<SmartWarning> SmartWarnings,
        double PerformanceDegradation,
        DateTimeOffset LastChecked);

    /// <summary>
    /// Status indicators for RAID array health.
    /// </summary>
    public enum RaidHealthStatus
    {
        /// <summary>
        /// All disks are healthy and functioning normally.
        /// </summary>
        Healthy = 0,

        /// <summary>
        /// One or more disks have failed but data is still accessible.
        /// </summary>
        Degraded = 1,

        /// <summary>
        /// Array is rebuilding after disk replacement.
        /// </summary>
        Rebuilding = 2,

        /// <summary>
        /// Critical failure - data loss imminent or occurred.
        /// </summary>
        Critical = 3,

        /// <summary>
        /// Array is offline or inaccessible.
        /// </summary>
        Offline = 4,

        /// <summary>
        /// SMART warnings indicate potential failure.
        /// </summary>
        Warning = 5
    }

    /// <summary>
    /// Information about a disk in the RAID array including capacity, health, and location.
    /// </summary>
    /// <param name="DiskId">Unique identifier for the disk.</param>
    /// <param name="Capacity">Total capacity in bytes.</param>
    /// <param name="UsedCapacity">Used capacity in bytes.</param>
    /// <param name="HealthStatus">Current health status.</param>
    /// <param name="DiskType">Type of disk (SSD, HDD, NVMe).</param>
    /// <param name="Location">Physical location (bay, slot, etc.).</param>
    /// <param name="SerialNumber">Disk serial number.</param>
    /// <param name="Model">Disk model name.</param>
    /// <param name="FirmwareVersion">Firmware version.</param>
    /// <param name="PowerOnHours">Total hours the disk has been powered on.</param>
    /// <param name="Temperature">Current temperature in Celsius.</param>
    /// <param name="ReadErrors">Count of read errors.</param>
    /// <param name="WriteErrors">Count of write errors.</param>
    public record DiskInfo(
        string DiskId,
        long Capacity,
        long UsedCapacity,
        DiskHealthStatus HealthStatus,
        DiskType DiskType,
        string Location,
        string? SerialNumber = null,
        string? Model = null,
        string? FirmwareVersion = null,
        long? PowerOnHours = null,
        int? Temperature = null,
        long? ReadErrors = null,
        long? WriteErrors = null);

    /// <summary>
    /// Health status of an individual disk.
    /// </summary>
    public enum DiskHealthStatus
    {
        /// <summary>
        /// Disk is healthy and functioning normally.
        /// </summary>
        Healthy = 0,

        /// <summary>
        /// SMART warnings indicate potential failure.
        /// </summary>
        Warning = 1,

        /// <summary>
        /// Disk has failed or is failing.
        /// </summary>
        Failed = 2,

        /// <summary>
        /// Disk is offline or not responding.
        /// </summary>
        Offline = 3,

        /// <summary>
        /// Disk is being rebuilt.
        /// </summary>
        Rebuilding = 4
    }

    /// <summary>
    /// Type of disk storage media.
    /// </summary>
    public enum DiskType
    {
        /// <summary>
        /// Hard Disk Drive (spinning platters).
        /// </summary>
        HDD = 0,

        /// <summary>
        /// Solid State Drive.
        /// </summary>
        SSD = 1,

        /// <summary>
        /// NVMe SSD.
        /// </summary>
        NVMe = 2,

        /// <summary>
        /// Optane persistent memory.
        /// </summary>
        Optane = 3,

        /// <summary>
        /// Unknown or unspecified.
        /// </summary>
        Unknown = 99
    }

    /// <summary>
    /// SMART warning information for predictive disk failure detection.
    /// </summary>
    /// <param name="DiskId">The disk identifier.</param>
    /// <param name="AttributeId">SMART attribute ID.</param>
    /// <param name="AttributeName">Human-readable attribute name.</param>
    /// <param name="CurrentValue">Current attribute value.</param>
    /// <param name="Threshold">Failure threshold.</param>
    /// <param name="Severity">Warning severity level.</param>
    /// <param name="Message">Descriptive warning message.</param>
    public record SmartWarning(
        string DiskId,
        int AttributeId,
        string AttributeName,
        int CurrentValue,
        int Threshold,
        WarningSeverity Severity,
        string Message);

    /// <summary>
    /// Severity level for SMART warnings.
    /// </summary>
    public enum WarningSeverity
    {
        /// <summary>
        /// Informational only.
        /// </summary>
        Info = 0,

        /// <summary>
        /// Warning - monitor closely.
        /// </summary>
        Warning = 1,

        /// <summary>
        /// Critical - failure imminent.
        /// </summary>
        Critical = 2
    }

    /// <summary>
    /// Information about stripe distribution across disks for a data block.
    /// </summary>
    /// <param name="StripeIndex">The stripe index within the array.</param>
    /// <param name="DataDisks">Disk indices that contain data chunks.</param>
    /// <param name="ParityDisks">Disk indices that contain parity chunks.</param>
    /// <param name="ChunkSize">Size of each chunk in bytes.</param>
    /// <param name="DataChunkCount">Number of data chunks in this stripe.</param>
    /// <param name="ParityChunkCount">Number of parity chunks in this stripe.</param>
    public record StripeInfo(
        long StripeIndex,
        int[] DataDisks,
        int[] ParityDisks,
        int ChunkSize,
        int DataChunkCount,
        int ParityChunkCount);

    /// <summary>
    /// Progress information for disk rebuild operations.
    /// </summary>
    /// <param name="PercentComplete">Percentage complete (0.0 to 1.0).</param>
    /// <param name="BytesRebuilt">Number of bytes rebuilt.</param>
    /// <param name="TotalBytes">Total bytes to rebuild.</param>
    /// <param name="EstimatedTimeRemaining">Estimated time until completion.</param>
    /// <param name="CurrentSpeed">Current rebuild speed in bytes per second.</param>
    public record RebuildProgress(
        double PercentComplete,
        long BytesRebuilt,
        long TotalBytes,
        TimeSpan EstimatedTimeRemaining,
        long CurrentSpeed);

    /// <summary>
    /// Abstract base class for RAID strategies providing common stripe calculation utilities,
    /// health monitoring, and rebuild management infrastructure.
    /// </summary>
    public abstract class RaidStrategyBase : StrategyBase, IRaidStrategy
    {
        /// <summary>
        /// Gets the unique identifier for this RAID strategy.
        /// Default implementation derives from the RAID level.
        /// Override in concrete strategies to provide a custom identifier.
        /// </summary>
        public override string StrategyId => $"raid-{Level.ToString().ToLowerInvariant()}";

        /// <summary>
        /// Gets the display name for this RAID strategy.
        /// Default implementation derives from the RAID level.
        /// Override in concrete strategies to provide a custom name.
        /// </summary>
        public virtual string StrategyName => $"RAID {Level}";

        /// <summary>
        /// Bridges StrategyName to the StrategyBase.Name contract.
        /// </summary>
        public override string Name => StrategyName;

        /// <inheritdoc/>
        public abstract RaidLevel Level { get; }

        /// <inheritdoc/>
        public abstract RaidCapabilities Capabilities { get; }

        /// <inheritdoc/>
        public abstract Task WriteAsync(
            ReadOnlyMemory<byte> data,
            IEnumerable<DiskInfo> disks,
            long offset,
            CancellationToken cancellationToken = default);

        /// <inheritdoc/>
        public abstract Task<ReadOnlyMemory<byte>> ReadAsync(
            IEnumerable<DiskInfo> disks,
            long offset,
            int length,
            CancellationToken cancellationToken = default);

        /// <inheritdoc/>
        public abstract StripeInfo CalculateStripe(long blockIndex, int diskCount);

        /// <inheritdoc/>
        public abstract Task RebuildDiskAsync(
            DiskInfo failedDisk,
            IEnumerable<DiskInfo> healthyDisks,
            DiskInfo targetDisk,
            IProgress<RebuildProgress>? progressCallback = null,
            CancellationToken cancellationToken = default);

        /// <inheritdoc/>
        public virtual async Task<RaidHealth> CheckHealthAsync(
            IEnumerable<DiskInfo> disks,
            CancellationToken cancellationToken = default)
        {
            var diskList = disks.ToList();
            var degradedDisks = diskList.Where(d => d.HealthStatus != DiskHealthStatus.Healthy).ToList();
            var healthyDisks = diskList.Where(d => d.HealthStatus == DiskHealthStatus.Healthy).ToList();
            var smartWarnings = new List<SmartWarning>();

            // Check for SMART warnings
            foreach (var disk in diskList)
            {
                if (disk.Temperature > 60)
                {
                    smartWarnings.Add(new SmartWarning(
                        disk.DiskId,
                        194,
                        "Temperature",
                        disk.Temperature ?? 0,
                        60,
                        WarningSeverity.Warning,
                        $"Disk temperature is {disk.Temperature}°C"));
                }

                if (disk.ReadErrors > 100 || disk.WriteErrors > 100)
                {
                    smartWarnings.Add(new SmartWarning(
                        disk.DiskId,
                        1,
                        "Error Rate",
                        (int)((disk.ReadErrors ?? 0) + (disk.WriteErrors ?? 0)),
                        100,
                        WarningSeverity.Critical,
                        "High error rate detected"));
                }
            }

            var status = degradedDisks.Count == 0
                ? (smartWarnings.Any() ? RaidHealthStatus.Warning : RaidHealthStatus.Healthy)
                : (degradedDisks.Count <= Capabilities.RedundancyLevel
                    ? RaidHealthStatus.Degraded
                    : RaidHealthStatus.Critical);

            var rebuildInProgress = diskList.Any(d => d.HealthStatus == DiskHealthStatus.Rebuilding);
            var performanceDegradation = degradedDisks.Count > 0 ? 0.3 : 0.0;

            return new RaidHealth(
                Status: status,
                DegradedDisks: degradedDisks,
                HealthyDisks: healthyDisks,
                RebuildInProgress: rebuildInProgress,
                RebuildProgress: 0.0,
                EstimatedRebuildCompletion: null,
                SmartWarnings: smartWarnings,
                PerformanceDegradation: performanceDegradation,
                LastChecked: DateTimeOffset.UtcNow);
        }

        /// <inheritdoc/>
        public virtual long CalculateUsableCapacity(IEnumerable<DiskInfo> disks)
        {
            var diskList = disks.ToList();
            var minCapacity = diskList.Min(d => d.Capacity);
            var totalCapacity = minCapacity * diskList.Count;

            return (long)(totalCapacity * Capabilities.CapacityEfficiency);
        }

        /// <inheritdoc/>
        public virtual bool CanSurviveFailures(int diskCount, int failures)
        {
            if (diskCount < Capabilities.MinDisks)
                return false;

            return failures <= Capabilities.RedundancyLevel;
        }

        /// <summary>
        /// Calculates XOR parity for a set of data chunks.
        /// </summary>
        /// <param name="dataChunks">The data chunks to calculate parity for.</param>
        /// <returns>The parity data.</returns>
        protected virtual ReadOnlyMemory<byte> CalculateXorParity(IEnumerable<ReadOnlyMemory<byte>> dataChunks)
        {
            var chunkList = dataChunks.ToList();
            if (chunkList.Count == 0)
                return ReadOnlyMemory<byte>.Empty;

            var length = chunkList[0].Length;
            var parity = new byte[length];

            foreach (var chunk in chunkList)
            {
                var span = chunk.Span;
                for (int i = 0; i < length; i++)
                {
                    parity[i] ^= span[i];
                }
            }

            return parity;
        }

        /// <summary>
        /// Validates that the disk configuration meets the minimum requirements for this RAID level.
        /// </summary>
        /// <param name="disks">The disks to validate.</param>
        /// <exception cref="ArgumentException">Thrown when disk configuration is invalid.</exception>
        protected virtual void ValidateDiskConfiguration(IEnumerable<DiskInfo> disks)
        {
            var diskList = disks.ToList();
            var diskCount = diskList.Count;

            if (diskCount < Capabilities.MinDisks)
                throw new ArgumentException(
                    $"RAID {Level} requires at least {Capabilities.MinDisks} disks, got {diskCount}");

            if (Capabilities.MaxDisks.HasValue && diskCount > Capabilities.MaxDisks.Value)
                throw new ArgumentException(
                    $"RAID {Level} supports at most {Capabilities.MaxDisks.Value} disks, got {diskCount}");

            if (Capabilities.RequiresUniformDiskSize)
            {
                var capacities = diskList.Select(d => d.Capacity).Distinct().ToList();
                if (capacities.Count > 1)
                    throw new ArgumentException(
                        $"RAID {Level} requires all disks to have the same capacity");
            }
        }

        /// <summary>
        /// Distributes a data block across multiple disks for striping.
        /// </summary>
        /// <param name="data">The data to distribute.</param>
        /// <param name="stripeInfo">The stripe distribution information.</param>
        /// <returns>Dictionary mapping disk index to data chunk.</returns>
        protected virtual Dictionary<int, ReadOnlyMemory<byte>> DistributeData(
            ReadOnlyMemory<byte> data,
            StripeInfo stripeInfo)
        {
            var result = new Dictionary<int, ReadOnlyMemory<byte>>();
            var chunkSize = stripeInfo.ChunkSize;
            var offset = 0;

            for (int i = 0; i < stripeInfo.DataChunkCount && offset < data.Length; i++)
            {
                var diskIndex = stripeInfo.DataDisks[i];
                var length = Math.Min(chunkSize, data.Length - offset);
                result[diskIndex] = data.Slice(offset, length);
                offset += length;
            }

            return result;
        }

        #region Legacy Intelligence Helpers (Phase 25b removes these)

        // TODO(25b): Remove -- intelligence belongs at plugin level per AD-05
        /// <summary>Legacy: Gets a description for this strategy.</summary>
        protected virtual string GetStrategyDescription() =>
            $"{StrategyName} strategy with {Capabilities.RedundancyLevel} disk fault tolerance and {Capabilities.CapacityEfficiency:P0} capacity efficiency";

        // TODO(25b): Remove -- intelligence belongs at plugin level per AD-05
        /// <summary>Legacy: Gets the knowledge payload for this strategy.</summary>
        protected virtual Dictionary<string, object> GetKnowledgePayload() => new()
        {
            ["level"] = Level.ToString(),
            ["redundancyLevel"] = Capabilities.RedundancyLevel,
            ["minDisks"] = Capabilities.MinDisks,
            ["maxDisks"] = Capabilities.MaxDisks ?? -1,
            ["stripeSize"] = Capabilities.StripeSize,
            ["capacityEfficiency"] = Capabilities.CapacityEfficiency,
            ["readPerformance"] = Capabilities.ReadPerformanceMultiplier,
            ["writePerformance"] = Capabilities.WritePerformanceMultiplier,
            ["supportsHotSpare"] = Capabilities.SupportsHotSpare,
            ["supportsOnlineExpansion"] = Capabilities.SupportsOnlineExpansion
        };

        // TODO(25b): Remove -- intelligence belongs at plugin level per AD-05
        /// <summary>Legacy: Gets tags for this strategy.</summary>
        protected virtual string[] GetKnowledgeTags() => new[]
        {
            "strategy",
            "raid",
            Level.ToString().ToLowerInvariant(),
            $"redundancy-{Capabilities.RedundancyLevel}"
        };

        // TODO(25b): Remove -- intelligence belongs at plugin level per AD-05
        /// <summary>Legacy: Gets capability metadata for this strategy.</summary>
        protected virtual Dictionary<string, object> GetCapabilityMetadata() => new()
        {
            ["level"] = Level.ToString(),
            ["redundancyLevel"] = Capabilities.RedundancyLevel,
            ["capacityEfficiency"] = Capabilities.CapacityEfficiency
        };

        // TODO(25b): Remove -- intelligence belongs at plugin level per AD-05
        /// <summary>Legacy: Gets the semantic description for AI-driven discovery.</summary>
        protected virtual string GetSemanticDescription() =>
            $"Use {StrategyName} for {Capabilities.RedundancyLevel}-disk fault tolerance with {Capabilities.CapacityEfficiency:P0} capacity efficiency";

        #endregion
    }
}
