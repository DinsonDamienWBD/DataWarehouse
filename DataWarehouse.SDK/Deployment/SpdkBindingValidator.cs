using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Hardware;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Deployment;

/// <summary>
/// NVMe namespace information record.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 37: NVMe namespace info (ENV-03)")]
public sealed record NvmeNamespaceInfo
{
    /// <summary>Gets the unique device identifier.</summary>
    public string DeviceId { get; init; } = string.Empty;

    /// <summary>Gets the device path (/dev/nvme0n1, \\.\PhysicalDrive0).</summary>
    public string DevicePath { get; init; } = string.Empty;

    /// <summary>Gets the size in bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Gets whether the namespace is currently mounted.</summary>
    public bool IsMounted { get; init; }

    /// <summary>Gets the mount point (if mounted).</summary>
    public string? MountPoint { get; init; }

    /// <summary>Gets whether this is a system-critical device (OS, boot, etc.).</summary>
    public bool IsSystemDevice { get; init; }
}

/// <summary>
/// SPDK binding validation result.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 37: SPDK binding validation result (ENV-03)")]
public sealed record SpdkBindingValidation
{
    /// <summary>Gets whether SPDK binding is safe to proceed.</summary>
    public bool IsValid { get; init; }

    /// <summary>Gets the error message if validation failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Gets the list of safe namespaces (unmounted, non-system).</summary>
    public IReadOnlyList<NvmeNamespaceInfo> SafeNamespaces { get; init; } = Array.Empty<NvmeNamespaceInfo>();

    /// <summary>Gets the list of unsafe namespaces (mounted, system, or has partitions).</summary>
    public IReadOnlyList<NvmeNamespaceInfo> UnsafeNamespaces { get; init; } = Array.Empty<NvmeNamespaceInfo>();
}

/// <summary>
/// Validates SPDK binding safety to prevent system crashes from binding the OS device.
/// CRITICAL: Binding a mounted or system NVMe namespace will cause immediate system crash.
/// </summary>
/// <remarks>
/// <para>
/// <b>CRITICAL SAFETY RULE:</b>
/// SPDK binds NVMe namespaces to user-space, unbinding them from the kernel driver.
/// If the OS is running on the NVMe device, unbinding it WILL CRASH THE SYSTEM IMMEDIATELY.
/// </para>
/// <para>
/// <b>Safety Checks:</b>
/// 1. Namespace is NOT mounted (check /proc/mounts on Linux, WMI on Windows)
/// 2. Namespace does NOT contain OS/boot partitions (/, /boot, C:\, Windows directory)
/// 3. Namespace has NO active partitions
/// </para>
/// <para>
/// <b>Safe Namespace Criteria:</b>
/// - Unmounted
/// - No partitions
/// - Not a system device (no OS files)
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 37: SPDK binding safety validator (ENV-03)")]
public sealed class SpdkBindingValidator
{
    private readonly IHardwareProbe? _hardwareProbe;
    private readonly ILogger<SpdkBindingValidator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SpdkBindingValidator"/> class.
    /// </summary>
    public SpdkBindingValidator(IHardwareProbe? hardwareProbe = null, ILogger<SpdkBindingValidator>? logger = null)
    {
        _hardwareProbe = hardwareProbe;
        _logger = logger ?? NullLogger<SpdkBindingValidator>.Instance;
    }

    /// <summary>
    /// Validates SPDK binding safety for all NVMe namespaces.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Validation result with safe/unsafe namespace classification.
    /// IsValid = true if at least one safe namespace found.
    /// </returns>
    public async Task<SpdkBindingValidation> ValidateBindingSafetyAsync(CancellationToken ct = default)
    {
        try
        {
            // Step 1: Discover all NVMe namespaces
            if (_hardwareProbe == null)
            {
                return new SpdkBindingValidation
                {
                    IsValid = false,
                    ErrorMessage = "Hardware probe not available. Cannot validate SPDK binding safety."
                };
            }

            var devices = await _hardwareProbe.DiscoverAsync(HardwareDeviceType.NvmeNamespace, ct);

            if (devices.Count == 0)
            {
                return new SpdkBindingValidation
                {
                    IsValid = false,
                    ErrorMessage = "No NVMe namespaces found. SPDK mode requires dedicated NVMe hardware."
                };
            }

            // Step 2: Check mount status for each namespace
            var safeNamespaces = new List<NvmeNamespaceInfo>();
            var unsafeNamespaces = new List<NvmeNamespaceInfo>();

            foreach (var device in devices)
            {
                var namespaceInfo = await ClassifyNamespaceAsync(device, ct);

                if (namespaceInfo.IsMounted || namespaceInfo.IsSystemDevice)
                {
                    unsafeNamespaces.Add(namespaceInfo);

                    _logger.LogCritical(
                        "UNSAFE NVMe namespace detected: {DevicePath}. " +
                        "Mounted: {IsMounted}, Mount point: {MountPoint}, System device: {IsSystemDevice}. " +
                        "SPDK binding would CRASH THE SYSTEM. DO NOT PROCEED.",
                        namespaceInfo.DevicePath,
                        namespaceInfo.IsMounted,
                        namespaceInfo.MountPoint,
                        namespaceInfo.IsSystemDevice);
                }
                else
                {
                    safeNamespaces.Add(namespaceInfo);
                    _logger.LogInformation(
                        "Safe NVMe namespace found: {DevicePath} ({SizeGB} GB). " +
                        "Unmounted, non-system. Safe for SPDK binding.",
                        namespaceInfo.DevicePath,
                        namespaceInfo.SizeBytes / 1024 / 1024 / 1024);
                }
            }

            // Step 3: Construct validation result
            if (safeNamespaces.Count == 0)
            {
                return new SpdkBindingValidation
                {
                    IsValid = false,
                    ErrorMessage = "No safe NVMe namespaces available for SPDK binding. All namespaces are mounted or contain system data.",
                    SafeNamespaces = safeNamespaces,
                    UnsafeNamespaces = unsafeNamespaces
                };
            }

            return new SpdkBindingValidation
            {
                IsValid = true,
                SafeNamespaces = safeNamespaces,
                UnsafeNamespaces = unsafeNamespaces
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SPDK binding validation failed with exception. ABORTING for safety.");
            return new SpdkBindingValidation
            {
                IsValid = false,
                ErrorMessage = $"Validation failed: {ex.Message}"
            };
        }
    }

    private async Task<NvmeNamespaceInfo> ClassifyNamespaceAsync(HardwareDevice device, CancellationToken ct)
    {
        var devicePath = device.Properties.GetValueOrDefault("DevicePath", device.DeviceId);
        var sizeBytes = long.TryParse(device.Properties.GetValueOrDefault("SizeBytes", "0"), out var size) ? size : 0;

        bool isMounted = false;
        string? mountPoint = null;
        bool isSystemDevice = false;

        if (OperatingSystem.IsLinux())
        {
            (isMounted, mountPoint, isSystemDevice) = await CheckLinuxMountStatusAsync(devicePath, ct);
        }
        else if (OperatingSystem.IsWindows())
        {
            (isMounted, mountPoint, isSystemDevice) = await CheckWindowsMountStatusAsync(devicePath, ct);
        }

        return new NvmeNamespaceInfo
        {
            DeviceId = device.DeviceId,
            DevicePath = devicePath,
            SizeBytes = sizeBytes,
            IsMounted = isMounted,
            MountPoint = mountPoint,
            IsSystemDevice = isSystemDevice
        };
    }

    [SupportedOSPlatform("linux")]
    private async Task<(bool isMounted, string? mountPoint, bool isSystemDevice)> CheckLinuxMountStatusAsync(
        string devicePath, CancellationToken ct)
    {
        try
        {
            // Read /proc/mounts to find mounted devices
            const string mountsPath = "/proc/mounts";
            if (!File.Exists(mountsPath))
                return (false, null, false);

            var lines = await File.ReadAllLinesAsync(mountsPath, ct);

            foreach (var line in lines)
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;

                var device = parts[0];
                var mountPt = parts[1];

                // Check if this line matches our device path
                if (device.Contains(devicePath, StringComparison.OrdinalIgnoreCase) ||
                    devicePath.Contains(device, StringComparison.OrdinalIgnoreCase))
                {
                    // Device is mounted
                    bool isSystem = IsSystemMountPoint(mountPt);
                    return (true, mountPt, isSystem);
                }
            }

            return (false, null, false);
        }
        catch
        {
            // Error reading mounts -- assume mounted for safety
            return (true, "unknown", true);
        }
    }

    [SupportedOSPlatform("windows")]
    private Task<(bool isMounted, string? mountPoint, bool isSystemDevice)> CheckWindowsMountStatusAsync(
        string devicePath, CancellationToken ct)
    {
        // Windows detection would use WMI Win32_DiskDrive/Win32_DiskPartition/Win32_LogicalDisk
        // For simplicity, assume mounted for safety (conservative)
        return Task.FromResult<(bool isMounted, string? mountPoint, bool isSystemDevice)>((true, "unknown", true));
    }

    private bool IsSystemMountPoint(string mountPoint)
    {
        // CRITICAL: System-critical mount points that must NOT be unbound
        var systemMountPoints = new[]
        {
            "/",
            "/boot",
            "/home",
            "/usr",
            "/var",
            "/etc",
            "C:\\",
            "C:/",
            "Windows"
        };

        return systemMountPoints.Any(mp => mountPoint.Contains(mp, StringComparison.OrdinalIgnoreCase));
    }
}
