using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Storage;

/// <summary>
/// Discriminates the variant of a <see cref="StorageAddress"/>.
/// Used for pattern matching and runtime type discrimination in the discriminated union.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 32: StorageAddress universal addressing")]
public enum StorageAddressKind
{
    /// <summary>
    /// Local or network file paths (e.g., C:\data, /mnt/data, \\server\share).
    /// </summary>
    FilePath,

    /// <summary>
    /// Raw block devices (e.g., /dev/sda, \\.\PhysicalDrive0).
    /// </summary>
    BlockDevice,

    /// <summary>
    /// NVMe namespace addressing (controller + namespace ID).
    /// </summary>
    NvmeNamespace,

    /// <summary>
    /// Object/key-based storage (S3 keys, AD-04 canonical keys).
    /// </summary>
    ObjectKey,

    /// <summary>
    /// Network storage endpoints (host:port with optional scheme).
    /// </summary>
    NetworkEndpoint,

    /// <summary>
    /// GPIO pin on edge devices (pin number + optional board ID).
    /// </summary>
    GpioPin,

    /// <summary>
    /// I2C bus device (bus ID + device address).
    /// </summary>
    I2cBus,

    /// <summary>
    /// SPI bus device (bus ID + chip select).
    /// </summary>
    SpiBus,

    /// <summary>
    /// Extensibility point for custom address schemes (scheme + opaque address string).
    /// </summary>
    CustomAddress
}
