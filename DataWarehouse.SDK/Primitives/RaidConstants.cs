using System;

namespace DataWarehouse.SDK.Primitives;

/// <summary>
/// Defines constants and configuration values for RAID operations.
/// These values represent industry-standard defaults and operational limits.
/// </summary>
public static class RaidConstants
{
    #region Stripe Configuration

    /// <summary>
    /// Default stripe size in bytes (64 KB).
    /// This is a common stripe size that balances performance and overhead.
    /// </summary>
    public const int DefaultStripeSizeBytes = 64 * 1024;

    /// <summary>
    /// Minimum stripe size in bytes (4 KB).
    /// Smaller stripes increase metadata overhead significantly.
    /// </summary>
    public const int MinimumStripeSizeBytes = 4 * 1024;

    /// <summary>
    /// Maximum stripe size in bytes (1 MB).
    /// Larger stripes reduce parallelism benefits.
    /// </summary>
    public const int MaximumStripeSizeBytes = 1024 * 1024;

    /// <summary>
    /// Common stripe sizes in bytes.
    /// These are power-of-2 values commonly used in storage systems.
    /// </summary>
    public static readonly int[] CommonStripeSizes = new[]
    {
        4 * 1024,      // 4 KB
        8 * 1024,      // 8 KB
        16 * 1024,     // 16 KB
        32 * 1024,     // 32 KB
        64 * 1024,     // 64 KB (default)
        128 * 1024,    // 128 KB
        256 * 1024,    // 256 KB
        512 * 1024,    // 512 KB
        1024 * 1024    // 1 MB
    };

    #endregion

    #region Shard Configuration

    /// <summary>
    /// Minimum number of data shards (1).
    /// A RAID configuration must have at least one data shard.
    /// </summary>
    public const int MinimumDataShards = 1;

    /// <summary>
    /// Maximum number of data shards (200).
    /// Limited by Galois Field arithmetic (GF(2^8) = 256 total shards)
    /// and practical performance considerations.
    /// </summary>
    public const int MaximumDataShards = 200;

    /// <summary>
    /// Minimum number of parity shards (1).
    /// At least one parity shard is required for redundancy.
    /// </summary>
    public const int MinimumParityShards = 1;

    /// <summary>
    /// Maximum number of parity shards (50).
    /// More than 50 parity shards provides diminishing returns
    /// and significant performance overhead.
    /// </summary>
    public const int MaximumParityShards = 50;

    /// <summary>
    /// Maximum total shards (data + parity) (256).
    /// Hard limit imposed by GF(2^8) Galois Field arithmetic.
    /// </summary>
    public const int MaximumTotalShards = 256;

    /// <summary>
    /// Default number of data shards for RAID 5 equivalent (4).
    /// Common configuration: 4 data + 1 parity.
    /// </summary>
    public const int DefaultRaid5DataShards = 4;

    /// <summary>
    /// Default number of parity shards for RAID 5 equivalent (1).
    /// RAID 5 uses single parity for fault tolerance.
    /// </summary>
    public const int DefaultRaid5ParityShards = 1;

    /// <summary>
    /// Default number of data shards for RAID 6 equivalent (4).
    /// Common configuration: 4 data + 2 parity.
    /// </summary>
    public const int DefaultRaid6DataShards = 4;

    /// <summary>
    /// Default number of parity shards for RAID 6 equivalent (2).
    /// RAID 6 uses dual parity (P+Q) for double fault tolerance.
    /// </summary>
    public const int DefaultRaid6ParityShards = 2;

    #endregion

    #region Performance Thresholds

    /// <summary>
    /// Rebuild parallelism threshold in bytes (1 MB).
    /// Blocks larger than this should use parallel processing for rebuild operations.
    /// </summary>
    public const int RebuildParallelismThresholdBytes = 1024 * 1024;

    /// <summary>
    /// Minimum block size for SIMD optimization (256 bytes).
    /// Blocks smaller than this won't benefit from SIMD instructions.
    /// </summary>
    public const int SimdMinimumBlockSize = 256;

    /// <summary>
    /// Buffer size for encoding/decoding operations (256 KB).
    /// Used for temporary buffers during RAID operations.
    /// </summary>
    public const int EncodingBufferSize = 256 * 1024;

    /// <summary>
    /// Maximum concurrent rebuild operations.
    /// Limits parallelism to prevent resource exhaustion.
    /// </summary>
    public const int MaximumConcurrentRebuilds = 8;

    #endregion

    #region Rebuild Configuration

    /// <summary>
    /// Default rebuild priority (Medium).
    /// Balances rebuild speed with normal I/O operations.
    /// </summary>
    public const RebuildPriority DefaultRebuildPriority = RebuildPriority.Medium;

    /// <summary>
    /// Rebuild verification interval in blocks (1000).
    /// Verify integrity every N blocks during rebuild.
    /// </summary>
    public const int RebuildVerificationInterval = 1000;

    /// <summary>
    /// Rebuild checkpoint interval in MB (100 MB).
    /// Save progress every N megabytes during rebuild.
    /// </summary>
    public const int RebuildCheckpointIntervalMB = 100;

    /// <summary>
    /// Maximum rebuild retry attempts (3).
    /// Number of times to retry failed rebuild operations before giving up.
    /// </summary>
    public const int MaximumRebuildRetries = 3;

    /// <summary>
    /// Rebuild timeout in seconds (3600 = 1 hour).
    /// Maximum time allowed for a single rebuild operation.
    /// </summary>
    public const int RebuildTimeoutSeconds = 3600;

    #endregion

    #region Health Check Configuration

    /// <summary>
    /// Default scrub interval in hours (168 = 1 week).
    /// How often to perform full array verification.
    /// </summary>
    public const int DefaultScrubIntervalHours = 168;

    /// <summary>
    /// Fast scrub sample rate (0.1 = 10%).
    /// Percentage of stripes to verify during fast scrub.
    /// </summary>
    public const double FastScrubSampleRate = 0.1;

    /// <summary>
    /// Parity check batch size (1000 stripes).
    /// Number of stripes to verify in one batch during scrub.
    /// </summary>
    public const int ParityCheckBatchSize = 1000;

    #endregion

    #region Error Handling

    /// <summary>
    /// Maximum bit error rate threshold (1e-15).
    /// Ratio of errors to total bits that triggers warning.
    /// </summary>
    public const double MaximumBitErrorRate = 1e-15;

    /// <summary>
    /// Critical bit error rate threshold (1e-12).
    /// Ratio of errors that triggers critical alert.
    /// </summary>
    public const double CriticalBitErrorRate = 1e-12;

    /// <summary>
    /// Error correction history size (10000).
    /// Number of error events to keep in history.
    /// </summary>
    public const int ErrorCorrectionHistorySize = 10000;

    #endregion

    #region Metadata

    /// <summary>
    /// RAID metadata version identifier.
    /// Updated when metadata format changes.
    /// </summary>
    public const int MetadataVersion = 1;

    /// <summary>
    /// RAID metadata magic number (0x52414944 = "RAID").
    /// Used to identify valid RAID metadata blocks.
    /// </summary>
    public const uint MetadataMagicNumber = 0x52414944;

    /// <summary>
    /// Metadata block size in bytes (4 KB).
    /// Size of metadata headers and descriptors.
    /// </summary>
    public const int MetadataBlockSize = 4096;

    #endregion

    #region RAID Levels

    /// <summary>
    /// RAID level identifiers.
    /// Standard RAID levels plus erasure coding variants.
    /// </summary>
    public enum RaidLevel
    {
        /// <summary>
        /// RAID 0: Striping without redundancy
        /// </summary>
        Raid0 = 0,

        /// <summary>
        /// RAID 1: Mirroring
        /// </summary>
        Raid1 = 1,

        /// <summary>
        /// RAID 5: Striping with single parity
        /// </summary>
        Raid5 = 5,

        /// <summary>
        /// RAID 6: Striping with dual parity (P+Q)
        /// </summary>
        Raid6 = 6,

        /// <summary>
        /// RAID 10: Mirrored striping
        /// </summary>
        Raid10 = 10,

        /// <summary>
        /// Reed-Solomon: Erasure coding with configurable redundancy
        /// </summary>
        ReedSolomon = 100,

        /// <summary>
        /// Triple parity: Three parity shards for maximum protection
        /// </summary>
        TripleParity = 101
    }

    /// <summary>
    /// Rebuild priority levels.
    /// Controls resource allocation during rebuild operations.
    /// </summary>
    public enum RebuildPriority
    {
        /// <summary>
        /// Low priority: Minimal impact on normal operations
        /// </summary>
        Low = 0,

        /// <summary>
        /// Medium priority: Balanced rebuild speed
        /// </summary>
        Medium = 1,

        /// <summary>
        /// High priority: Fast rebuild, may impact performance
        /// </summary>
        High = 2,

        /// <summary>
        /// Critical priority: Maximum rebuild speed
        /// </summary>
        Critical = 3
    }

    #endregion

    #region Validation Methods

    /// <summary>
    /// Validates that stripe size is within acceptable range and power of 2.
    /// </summary>
    /// <param name="stripeSize">Stripe size to validate</param>
    /// <returns>True if valid, false otherwise</returns>
    public static bool IsValidStripeSize(int stripeSize)
    {
        if (stripeSize < MinimumStripeSizeBytes || stripeSize > MaximumStripeSizeBytes)
        {
            return false;
        }

        // Must be power of 2
        return (stripeSize & (stripeSize - 1)) == 0;
    }

    /// <summary>
    /// Validates that shard configuration is within limits.
    /// </summary>
    /// <param name="dataShards">Number of data shards</param>
    /// <param name="parityShards">Number of parity shards</param>
    /// <returns>True if valid, false otherwise</returns>
    public static bool IsValidShardConfiguration(int dataShards, int parityShards)
    {
        if (dataShards < MinimumDataShards || dataShards > MaximumDataShards)
        {
            return false;
        }

        if (parityShards < MinimumParityShards || parityShards > MaximumParityShards)
        {
            return false;
        }

        if (dataShards + parityShards > MaximumTotalShards)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Calculates storage efficiency for a given RAID configuration.
    /// </summary>
    /// <param name="dataShards">Number of data shards</param>
    /// <param name="parityShards">Number of parity shards</param>
    /// <returns>Storage efficiency as a ratio (0.0 to 1.0)</returns>
    public static double CalculateStorageEfficiency(int dataShards, int parityShards)
    {
        if (dataShards <= 0 || parityShards < 0)
        {
            return 0.0;
        }

        int totalShards = dataShards + parityShards;
        return (double)dataShards / totalShards;
    }

    /// <summary>
    /// Calculates the number of simultaneous failures that can be tolerated.
    /// </summary>
    /// <param name="parityShards">Number of parity shards</param>
    /// <returns>Maximum number of tolerable failures</returns>
    public static int CalculateFaultTolerance(int parityShards)
    {
        // For Reed-Solomon, fault tolerance equals parity shards
        return System.Math.Max(0, parityShards);
    }

    #endregion
}
