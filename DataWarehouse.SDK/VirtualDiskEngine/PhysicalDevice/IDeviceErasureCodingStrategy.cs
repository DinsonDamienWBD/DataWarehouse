using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice;

/// <summary>
/// Specifies the erasure coding algorithm used at the device level.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91: Device erasure coding (CBDV-03)")]
public enum ErasureCodingScheme
{
    /// <summary>
    /// Reed-Solomon coding using Vandermonde matrix encoding in GF(2^8).
    /// Provides optimal storage efficiency with deterministic encoding/decoding.
    /// </summary>
    ReedSolomon = 0,

    /// <summary>
    /// Fountain code (LT / Luby Transform) with rateless encoding.
    /// Produces slightly more parity than minimum but supports flexible device counts.
    /// </summary>
    FountainCode = 1
}

/// <summary>
/// Configuration for device-level erasure coding, specifying the scheme,
/// device counts, and stripe geometry.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="Devices"/> list must contain exactly
/// <see cref="DataDeviceCount"/> + <see cref="ParityDeviceCount"/> entries.
/// The first <see cref="DataDeviceCount"/> devices are treated as data devices,
/// and the remaining as parity devices.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91: Device erasure coding (CBDV-03)")]
public sealed record ErasureCodingConfiguration
{
    /// <summary>
    /// Gets the erasure coding algorithm to use.
    /// </summary>
    public ErasureCodingScheme Scheme { get; }

    /// <summary>
    /// Gets the number of data devices (k). Any k devices out of k+m total
    /// can reconstruct all original data.
    /// </summary>
    public int DataDeviceCount { get; }

    /// <summary>
    /// Gets the number of parity devices (m). Determines how many simultaneous
    /// device failures can be tolerated.
    /// </summary>
    public int ParityDeviceCount { get; }

    /// <summary>
    /// Gets the physical devices in the array. Must contain exactly
    /// <see cref="DataDeviceCount"/> + <see cref="ParityDeviceCount"/> entries.
    /// </summary>
    public IReadOnlyList<IPhysicalBlockDevice> Devices { get; }

    /// <summary>
    /// Gets optional hot spare devices that can automatically replace failed devices.
    /// </summary>
    public IReadOnlyList<IPhysicalBlockDevice>? HotSpares { get; init; }

    /// <summary>
    /// Gets the stripe size in blocks. Each stripe spans one block per device.
    /// Must be a power of two. Default is 256.
    /// </summary>
    public int StripeSizeBlocks { get; init; } = 256;

    /// <summary>
    /// Gets the rebuild priority (0-100). Higher values indicate more urgent rebuilds
    /// that consume more I/O bandwidth. Default is 50.
    /// </summary>
    public int RebuildPriority { get; init; } = 50;

    /// <summary>
    /// Initializes a new <see cref="ErasureCodingConfiguration"/>.
    /// </summary>
    /// <param name="scheme">The erasure coding algorithm.</param>
    /// <param name="dataDeviceCount">Number of data devices (k). Must be at least 1.</param>
    /// <param name="parityDeviceCount">Number of parity devices (m). Must be at least 1.</param>
    /// <param name="devices">
    /// The physical devices. Must contain exactly <paramref name="dataDeviceCount"/> +
    /// <paramref name="parityDeviceCount"/> entries.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="devices"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when device counts are invalid or do not match the device list length.
    /// </exception>
    public ErasureCodingConfiguration(
        ErasureCodingScheme scheme,
        int dataDeviceCount,
        int parityDeviceCount,
        IReadOnlyList<IPhysicalBlockDevice> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);

        if (dataDeviceCount < 1)
            throw new ArgumentOutOfRangeException(nameof(dataDeviceCount), "Must have at least 1 data device.");
        if (parityDeviceCount < 1)
            throw new ArgumentOutOfRangeException(nameof(parityDeviceCount), "Must have at least 1 parity device.");
        if (devices.Count != dataDeviceCount + parityDeviceCount)
        {
            throw new ArgumentException(
                $"Device list must contain exactly {dataDeviceCount + parityDeviceCount} devices " +
                $"(k={dataDeviceCount} + m={parityDeviceCount}), but got {devices.Count}.",
                nameof(devices));
        }

        Scheme = scheme;
        DataDeviceCount = dataDeviceCount;
        ParityDeviceCount = parityDeviceCount;
        Devices = devices;
    }
}

/// <summary>
/// Health status of an erasure-coded device array, including device availability
/// and recovery capability information.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91: Device erasure coding (CBDV-03)")]
public sealed record ErasureCodingHealth
{
    /// <summary>
    /// Gets whether the array is fully healthy (no failed devices).
    /// </summary>
    public bool IsHealthy { get; init; }

    /// <summary>
    /// Gets the number of currently available (online) devices.
    /// </summary>
    public int AvailableDevices { get; init; }

    /// <summary>
    /// Gets the minimum number of devices required to reconstruct all data (k).
    /// </summary>
    public int MinimumRequiredDevices { get; init; }

    /// <summary>
    /// Gets the total number of devices in the array (k+m).
    /// </summary>
    public int TotalDevices { get; init; }

    /// <summary>
    /// Gets the number of failed or offline devices.
    /// </summary>
    public int FailedDevices { get; init; }

    /// <summary>
    /// Gets whether the array can still recover all data from the surviving devices.
    /// True when <see cref="AvailableDevices"/> >= <see cref="MinimumRequiredDevices"/>.
    /// </summary>
    public bool CanRecoverData { get; init; }

    /// <summary>
    /// Gets the storage efficiency ratio: k / (k+m).
    /// For example, an 8+3 configuration has efficiency 0.727 (72.7%).
    /// </summary>
    public double StorageEfficiency { get; init; }
}

/// <summary>
/// Strategy interface for device-level erasure coding. Implementations encode data across
/// arrays of <see cref="IPhysicalBlockDevice"/> instances, producing a
/// <see cref="CompoundBlockDevice"/> that tolerates multiple simultaneous device failures.
/// </summary>
/// <remarks>
/// <para>
/// Device-level erasure coding distributes encoded blocks across physical devices,
/// allowing reconstruction from any k devices out of k+m total. This provides more
/// flexible redundancy than traditional RAID parity, supporting configurable
/// data-to-parity ratios.
/// </para>
/// <para>
/// Implementations must be thread-safe for concurrent encode/decode operations
/// on different stripe offsets.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91: Device erasure coding (CBDV-03)")]
public interface IDeviceErasureCodingStrategy
{
    /// <summary>
    /// Gets the unique identifier for this erasure coding strategy
    /// (e.g., "device-rs-8+3", "device-fountain-8x2").
    /// </summary>
    string StrategyId { get; }

    /// <summary>
    /// Gets the erasure coding scheme used by this strategy.
    /// </summary>
    ErasureCodingScheme Scheme { get; }

    /// <summary>
    /// Gets the maximum number of simultaneous device failures this configuration
    /// can tolerate. Equals the parity device count (m).
    /// </summary>
    int MaxTolerableFailures { get; }

    /// <summary>
    /// Gets the storage efficiency ratio: k / (k+m).
    /// </summary>
    double StorageEfficiency { get; }

    /// <summary>
    /// Creates a <see cref="CompoundBlockDevice"/> from the configured device array,
    /// initializing the erasure coding layout and encoding metadata.
    /// </summary>
    /// <param name="config">The erasure coding configuration with device list and parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A compound block device that presents the erasure-coded array as a single device.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="config"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when the configuration is invalid for this strategy.</exception>
    Task<CompoundBlockDevice> CreateCompoundDeviceAsync(
        ErasureCodingConfiguration config, CancellationToken ct = default);

    /// <summary>
    /// Checks the health of an erasure-coded device array, reporting device
    /// availability and recovery capability.
    /// </summary>
    /// <param name="config">The erasure coding configuration to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Health status of the erasure-coded array.</returns>
    Task<ErasureCodingHealth> CheckHealthAsync(
        ErasureCodingConfiguration config, CancellationToken ct = default);

    /// <summary>
    /// Rebuilds a failed device by reconstructing its data from the surviving
    /// devices and writing the reconstructed blocks to a replacement device.
    /// </summary>
    /// <param name="failedDeviceIndex">Zero-based index of the failed device in the array.</param>
    /// <param name="replacement">The replacement physical device to write reconstructed data to.</param>
    /// <param name="config">The erasure coding configuration.</param>
    /// <param name="progress">Optional progress reporter (0.0 to 1.0).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="failedDeviceIndex"/> is out of range.</exception>
    /// <exception cref="InvalidOperationException">Thrown when recovery is not possible (too many failed devices).</exception>
    Task RebuildDeviceAsync(
        int failedDeviceIndex,
        IPhysicalBlockDevice replacement,
        ErasureCodingConfiguration config,
        IProgress<double>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Encodes a single stripe of data and writes the encoded chunks (data + parity)
    /// to the configured devices.
    /// </summary>
    /// <param name="data">The raw data to encode. Will be split into k equal chunks.</param>
    /// <param name="devices">The target devices (k data + m parity).</param>
    /// <param name="stripeOffset">The stripe number (block offset on each device).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException">Thrown when data length or device count is invalid.</exception>
    Task EncodeStripeAsync(
        ReadOnlyMemory<byte> data,
        IReadOnlyList<IPhysicalBlockDevice> devices,
        long stripeOffset,
        CancellationToken ct = default);

    /// <summary>
    /// Reads and decodes a single stripe from the device array, reconstructing
    /// data from surviving devices if some are unavailable.
    /// </summary>
    /// <param name="devices">The device array (k data + m parity).</param>
    /// <param name="deviceAvailable">Boolean array indicating which devices are available for reading.</param>
    /// <param name="stripeOffset">The stripe number (block offset on each device).</param>
    /// <param name="dataLength">The expected data length in bytes (k * chunk size).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The decoded data for this stripe.</returns>
    /// <exception cref="InvalidOperationException">Thrown when insufficient devices are available for decoding.</exception>
    Task<ReadOnlyMemory<byte>> DecodeStripeAsync(
        IReadOnlyList<IPhysicalBlockDevice> devices,
        bool[] deviceAvailable,
        long stripeOffset,
        int dataLength,
        CancellationToken ct = default);
}
