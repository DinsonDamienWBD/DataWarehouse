using DataWarehouse.SDK.Contracts;
using System;
using System.IO;

namespace DataWarehouse.SDK.Storage;

/// <summary>
/// Universal storage addressing type that can represent any storage location:
/// file path, block device, NVMe namespace, object key, network endpoint,
/// GPIO pin, I2C bus, SPI bus, or custom address.
/// </summary>
/// <remarks>
/// <para>
/// StorageAddress is a discriminated union with 9 variants, each represented by a sealed record.
/// Use pattern matching (switch on Kind or 'is' patterns) to handle specific variants.
/// </para>
/// <para>
/// <b>Backward Compatibility (HAL-05):</b> Implicit conversions from string and Uri ensure
/// zero breaking changes to existing code. Strings are heuristically converted to ObjectKeyAddress
/// or FilePathAddress based on path rooting and directory separators.
/// </para>
/// <para>
/// <b>Recommended Usage:</b> Prefer explicit factory methods (FromFilePath, FromObjectKey, etc.)
/// over implicit conversions for clarity. Implicit conversions are provided for backward compatibility.
/// </para>
/// <para>
/// <b>No IStorageAddress Interface:</b> The abstract record IS the abstraction. C# pattern matching
/// is sufficient for variant discrimination. Adding an interface would be an anti-pattern.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 32: Universal storage addressing (HAL-01)")]
public abstract record StorageAddress
{
    /// <summary>
    /// Gets the kind of this storage address, discriminating the variant.
    /// </summary>
    public abstract StorageAddressKind Kind { get; }

    /// <summary>
    /// Converts this storage address to a string key for backward compatibility
    /// with IObjectStorageCore and IStorageStrategy string-based APIs.
    /// </summary>
    public abstract string ToKey();

    /// <summary>
    /// Converts this storage address to a URI representation.
    /// Variants that have natural URI representations (file paths, network endpoints) override this.
    /// Default: creates a URI with the variant kind as the scheme.
    /// </summary>
    public virtual Uri ToUri() => new Uri($"{Kind.ToString().ToLowerInvariant()}://{ToKey()}");

    /// <summary>
    /// Converts this storage address to a file system path.
    /// Only FilePathAddress supports this conversion; other variants throw NotSupportedException.
    /// </summary>
    /// <exception cref="NotSupportedException">Thrown when the variant cannot be converted to a file path.</exception>
    public virtual string ToPath() => throw new NotSupportedException($"StorageAddress of kind {Kind} cannot be converted to a file path");

    /// <summary>
    /// Returns a debug-friendly string representation.
    /// </summary>
    public override string ToString() => $"{Kind}:{ToKey()}";

    #region Implicit Conversions (HAL-05 Backward Compatibility)

    /// <summary>
    /// Implicitly converts a string to a StorageAddress.
    /// Heuristic: if the string is NOT path-rooted AND does NOT contain directory separators,
    /// it is treated as an ObjectKeyAddress. Otherwise, it is treated as a FilePathAddress.
    /// </summary>
    public static implicit operator StorageAddress(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return IsObjectKey(value) ? new ObjectKeyAddress(value) : new FilePathAddress(value);
    }

    /// <summary>
    /// Implicitly converts a Uri to a StorageAddress.
    /// Delegates to FromUri for scheme-based parsing.
    /// </summary>
    public static implicit operator StorageAddress(Uri uri) => FromUri(uri);

    #endregion

    #region Static Factory Methods

    /// <summary>
    /// Creates a FilePathAddress from a file system path.
    /// </summary>
    public static StorageAddress FromFilePath(string path)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(path);
        return new FilePathAddress(path);
    }

    /// <summary>
    /// Creates an ObjectKeyAddress from an object key.
    /// </summary>
    public static StorageAddress FromObjectKey(string key)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        return new ObjectKeyAddress(key);
    }

    /// <summary>
    /// Creates an NvmeNamespaceAddress from a namespace ID and optional controller ID.
    /// </summary>
    public static StorageAddress FromNvme(int nsid, int? controllerId = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nsid);
        if (controllerId.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(controllerId.Value);
        }
        return new NvmeNamespaceAddress(nsid, controllerId);
    }

    /// <summary>
    /// Creates a BlockDeviceAddress from a block device path.
    /// </summary>
    public static StorageAddress FromBlockDevice(string devicePath)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(devicePath);
        return new BlockDeviceAddress(devicePath);
    }

    /// <summary>
    /// Creates a NetworkEndpointAddress from host, port, and optional scheme.
    /// </summary>
    public static StorageAddress FromNetworkEndpoint(string host, int port, string? scheme = null)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        return new NetworkEndpointAddress(host, port, scheme);
    }

    /// <summary>
    /// Creates a GpioPinAddress from a pin number and optional board ID.
    /// </summary>
    public static StorageAddress FromGpioPin(int pin, string? boardId = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pin);
        return new GpioPinAddress(pin, boardId);
    }

    /// <summary>
    /// Creates an I2cBusAddress from a bus ID and device address.
    /// </summary>
    public static StorageAddress FromI2cBus(int busId, int deviceAddress)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(busId);
        ArgumentOutOfRangeException.ThrowIfLessThan(deviceAddress, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(deviceAddress, 127);
        return new I2cBusAddress(busId, deviceAddress);
    }

    /// <summary>
    /// Creates a SpiBusAddress from a bus ID and chip select.
    /// </summary>
    public static StorageAddress FromSpiBus(int busId, int chipSelect)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(busId);
        ArgumentOutOfRangeException.ThrowIfNegative(chipSelect);
        return new SpiBusAddress(busId, chipSelect);
    }

    /// <summary>
    /// Creates a CustomAddress from a scheme and address string.
    /// </summary>
    public static StorageAddress FromCustom(string scheme, string address)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(scheme);
        ArgumentNullException.ThrowIfNullOrEmpty(address);
        return new CustomAddress(scheme, address);
    }

    /// <summary>
    /// Parses a URI into the appropriate StorageAddress variant based on the URI scheme.
    /// </summary>
    /// <remarks>
    /// Parsing rules:
    /// <list type="bullet">
    /// <item><description>file:// -> FilePathAddress (uses LocalPath)</description></item>
    /// <item><description>nvme:// -> NvmeNamespaceAddress (parses nsid from path)</description></item>
    /// <item><description>gpio://, i2c://, spi:// -> respective hardware addresses</description></item>
    /// <item><description>block:// -> BlockDeviceAddress</description></item>
    /// <item><description>http://, https://, tcp://, udp:// -> NetworkEndpointAddress</description></item>
    /// <item><description>default -> ObjectKeyAddress (uses full URI string)</description></item>
    /// </list>
    /// </remarks>
    public static StorageAddress FromUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (!uri.IsAbsoluteUri)
        {
            // Relative URI - treat as object key
            return new ObjectKeyAddress(uri.ToString());
        }

        return uri.Scheme.ToLowerInvariant() switch
        {
            "file" => new FilePathAddress(uri.LocalPath),
            "nvme" => ParseNvmeUri(uri),
            "gpio" => ParseGpioUri(uri),
            "i2c" => ParseI2cUri(uri),
            "spi" => ParseSpiUri(uri),
            "block" => new BlockDeviceAddress(uri.AbsolutePath.TrimStart('/')),
            "http" or "https" or "tcp" or "udp" => new NetworkEndpointAddress(
                uri.Host,
                uri.Port > 0 ? uri.Port : uri.Scheme == "https" ? 443 : 80,
                uri.Scheme),
            _ => new ObjectKeyAddress(uri.ToString())
        };
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Heuristic: a string is an object key if it is NOT path-rooted AND does NOT contain directory separators.
    /// This is consistent with AD-04 key conventions.
    /// </summary>
    private static bool IsObjectKey(string s)
    {
        return !Path.IsPathRooted(s) &&
               !s.Contains(Path.DirectorySeparatorChar) &&
               !s.Contains(Path.AltDirectorySeparatorChar);
    }

    private static StorageAddress ParseNvmeUri(Uri uri)
    {
        // Expected format: nvme://{controllerId}/ns/{nsid}
        var path = uri.AbsolutePath.TrimStart('/');
        var parts = path.Split('/');
        if (parts.Length >= 2 && parts[^2] == "ns" && int.TryParse(parts[^1], out var nsid))
        {
            int? controllerId = parts.Length >= 4 && int.TryParse(parts[0], out var cid) ? cid : null;
            return new NvmeNamespaceAddress(nsid, controllerId);
        }
        throw new FormatException($"Invalid nvme URI format: {uri}. Expected nvme://[controllerId]/ns/{{nsid}}");
    }

    private static StorageAddress ParseGpioUri(Uri uri)
    {
        // Expected format: gpio://{boardId}/pin/{pin}
        var path = uri.AbsolutePath.TrimStart('/');
        var parts = path.Split('/');
        if (parts.Length >= 2 && parts[^2] == "pin" && int.TryParse(parts[^1], out var pin))
        {
            var boardId = parts.Length >= 4 ? parts[0] : uri.Host != string.Empty ? uri.Host : null;
            return new GpioPinAddress(pin, boardId);
        }
        throw new FormatException($"Invalid gpio URI format: {uri}. Expected gpio://[boardId]/pin/{{pin}}");
    }

    private static StorageAddress ParseI2cUri(Uri uri)
    {
        // Expected format: i2c://{busId}/0x{deviceAddress}
        var path = uri.AbsolutePath.TrimStart('/');
        var parts = path.Split('/');
        if (parts.Length >= 2 &&
            int.TryParse(parts[0], out var busId) &&
            parts[1].StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(parts[1].AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var deviceAddress))
        {
            return new I2cBusAddress(busId, deviceAddress);
        }
        throw new FormatException($"Invalid i2c URI format: {uri}. Expected i2c://{{busId}}/0x{{deviceAddress}}");
    }

    private static StorageAddress ParseSpiUri(Uri uri)
    {
        // Expected format: spi://{busId}/cs/{chipSelect}
        var path = uri.AbsolutePath.TrimStart('/');
        var parts = path.Split('/');
        if (parts.Length >= 3 &&
            int.TryParse(parts[0], out var busId) &&
            parts[1] == "cs" &&
            int.TryParse(parts[2], out var chipSelect))
        {
            return new SpiBusAddress(busId, chipSelect);
        }
        throw new FormatException($"Invalid spi URI format: {uri}. Expected spi://{{busId}}/cs/{{chipSelect}}");
    }

    #endregion
}

#region Sealed Record Variants

/// <summary>
/// Storage address for local or network file paths (e.g., C:\data, /mnt/data, \\server\share).
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 32: StorageAddress universal addressing")]
public sealed record FilePathAddress(string Path) : StorageAddress
{
    public override StorageAddressKind Kind => StorageAddressKind.FilePath;

    public override string ToKey() => PathStorageAdapter.NormalizePath(Path);

    public override string ToPath() => Path;

    public override Uri ToUri() => new Uri(Path);
}

/// <summary>
/// Storage address for object/key-based storage (S3 keys, AD-04 canonical keys).
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 32: StorageAddress universal addressing")]
public sealed record ObjectKeyAddress(string Key) : StorageAddress
{
    public override StorageAddressKind Kind => StorageAddressKind.ObjectKey;

    public override string ToKey() => Key;
}

/// <summary>
/// Storage address for NVMe namespace (controller + namespace ID).
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 32: StorageAddress universal addressing")]
public sealed record NvmeNamespaceAddress(int NamespaceId, int? ControllerId = null) : StorageAddress
{
    public override StorageAddressKind Kind => StorageAddressKind.NvmeNamespace;

    public override string ToKey() => $"nvme://{ControllerId ?? 0}/ns/{NamespaceId}";
}

/// <summary>
/// Storage address for raw block devices (e.g., /dev/sda, \\.\PhysicalDrive0).
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 32: StorageAddress universal addressing")]
public sealed record BlockDeviceAddress(string DevicePath) : StorageAddress
{
    public override StorageAddressKind Kind => StorageAddressKind.BlockDevice;

    public override string ToKey() => DevicePath;

    public override string ToPath() => DevicePath;
}

/// <summary>
/// Storage address for network storage endpoints (host:port with optional scheme).
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 32: StorageAddress universal addressing")]
public sealed record NetworkEndpointAddress(string Host, int Port, string? Scheme = null) : StorageAddress
{
    public override StorageAddressKind Kind => StorageAddressKind.NetworkEndpoint;

    public override string ToKey() => $"{Scheme ?? "tcp"}://{Host}:{Port}";

    public override Uri ToUri() => new Uri(ToKey());
}

/// <summary>
/// Storage address for GPIO pin on edge devices (pin number + optional board ID).
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 32: StorageAddress universal addressing")]
public sealed record GpioPinAddress(int Pin, string? BoardId = null) : StorageAddress
{
    public override StorageAddressKind Kind => StorageAddressKind.GpioPin;

    public override string ToKey() => $"gpio://{BoardId ?? "default"}/pin/{Pin}";
}

/// <summary>
/// Storage address for I2C bus device (bus ID + device address).
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 32: StorageAddress universal addressing")]
public sealed record I2cBusAddress(int BusId, int DeviceAddress) : StorageAddress
{
    public override StorageAddressKind Kind => StorageAddressKind.I2cBus;

    public override string ToKey() => $"i2c://{BusId}/0x{DeviceAddress:X2}";
}

/// <summary>
/// Storage address for SPI bus device (bus ID + chip select).
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 32: StorageAddress universal addressing")]
public sealed record SpiBusAddress(int BusId, int ChipSelect) : StorageAddress
{
    public override StorageAddressKind Kind => StorageAddressKind.SpiBus;

    public override string ToKey() => $"spi://{BusId}/cs/{ChipSelect}";
}

/// <summary>
/// Storage address for custom address schemes (scheme + opaque address string).
/// Extensibility point for future storage types not covered by the standard variants.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 32: StorageAddress universal addressing")]
public sealed record CustomAddress(string Scheme, string Address) : StorageAddress
{
    public override StorageAddressKind Kind => StorageAddressKind.CustomAddress;

    public override string ToKey() => $"{Scheme}://{Address}";
}

#endregion
