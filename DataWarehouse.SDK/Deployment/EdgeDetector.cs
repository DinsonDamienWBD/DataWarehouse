using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Hardware;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Deployment;

/// <summary>
/// Detects edge/IoT platform deployments via GPIO/I2C/SPI presence and platform-specific signatures.
/// Uses <see cref="IHardwareProbe"/> from Phase 32 for hardware enumeration.
/// </summary>
/// <remarks>
/// <para>
/// Edge device indicators:
/// - GPIO controllers present (/sys/class/gpio on Linux)
/// - I2C or SPI buses present
/// - Low total memory (<2GB typically indicates edge/embedded)
/// - Platform-specific signatures (Raspberry Pi, industrial gateways)
/// </para>
/// <para>
/// Supported edge platforms:
/// - <b>Raspberry Pi:</b> /proc/device-tree/model contains "Raspberry Pi"
/// - <b>Industrial Gateway:</b> Serial ports + GPIO/I2C + moderate memory (512MB-2GB)
/// - <b>Generic IoT:</b> GPIO/I2C/SPI present + low memory (<512MB)
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 37: Edge/IoT platform detection (ENV-05)")]
public sealed class EdgeDetector : IDeploymentDetector
{
    private readonly IHardwareProbe? _hardwareProbe;

    /// <summary>
    /// Initializes a new instance of the <see cref="EdgeDetector"/> class.
    /// </summary>
    /// <param name="hardwareProbe">
    /// Optional hardware probe from Phase 32. If null, detection is limited.
    /// </param>
    public EdgeDetector(IHardwareProbe? hardwareProbe = null)
    {
        _hardwareProbe = hardwareProbe;
    }

    /// <summary>
    /// Detects edge/IoT platform deployment.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="DeploymentContext"/> with <see cref="DeploymentEnvironment.EdgeDevice"/> if detected.
    /// Returns null if not an edge device (desktop/server/cloud).
    /// </returns>
    public async Task<DeploymentContext?> DetectAsync(CancellationToken ct = default)
    {
        try
        {
            // Step 1: Detect GPIO/I2C/SPI hardware (edge indicator)
            int gpioCount = 0, i2cCount = 0, spiCount = 0;

            if (_hardwareProbe != null)
            {
                var devices = await _hardwareProbe.DiscoverAsync(
                    HardwareDeviceType.GpioController | HardwareDeviceType.I2cBus | HardwareDeviceType.SpiBus,
                    ct);

                foreach (var device in devices)
                {
                    if (device.Type == HardwareDeviceType.GpioController) gpioCount++;
                    else if (device.Type == HardwareDeviceType.I2cBus) i2cCount++;
                    else if (device.Type == HardwareDeviceType.SpiBus) spiCount++;
                }
            }

            // If no GPIO/I2C/SPI found, probably not an edge device
            if (gpioCount == 0 && i2cCount == 0 && spiCount == 0)
            {
                // Fallback: Check filesystem for GPIO presence (Linux)
                if (OperatingSystem.IsLinux() && !Directory.Exists("/sys/class/gpio"))
                {
                    return null; // Not an edge device
                }
            }

            // Step 2: Platform-specific edge detection (Linux primarily)
            string? platformModel = null;
            long totalMemoryMb = 0;

            if (OperatingSystem.IsLinux())
            {
                platformModel = await DetectLinuxEdgePlatformAsync(ct);
                totalMemoryMb = await GetLinuxTotalMemoryMbAsync(ct);
            }
            else if (OperatingSystem.IsWindows())
            {
                // Windows IoT detection
                totalMemoryMb = Environment.WorkingSet / 1024 / 1024; // Rough estimate
            }

            // If memory is high (>2GB), probably not an edge device
            if (totalMemoryMb > 2048 && platformModel == null)
            {
                return null;
            }

            // Step 3: Classify edge platform
            platformModel ??= ClassifyGenericEdgePlatform(gpioCount, i2cCount, spiCount, totalMemoryMb);

            if (platformModel == null)
            {
                return null; // Not confident this is an edge device
            }

            // Step 4: Construct DeploymentContext
            var metadata = new Dictionary<string, string>
            {
                ["PlatformModel"] = platformModel,
                ["GpioControllerCount"] = gpioCount.ToString(),
                ["I2cBusCount"] = i2cCount.ToString(),
                ["SpiBusCount"] = spiCount.ToString(),
                ["TotalMemoryMB"] = totalMemoryMb.ToString()
            };

            return new DeploymentContext
            {
                Environment = DeploymentEnvironment.EdgeDevice,
                IsEdgeDevice = true,
                Metadata = metadata
            };
        }
        catch
        {
            // Detection failure returns null (graceful degradation)
            return null;
        }
    }

    [SupportedOSPlatform("linux")]
    private async Task<string?> DetectLinuxEdgePlatformAsync(CancellationToken ct)
    {
        try
        {
            // Check /proc/device-tree/model for platform identification
            const string deviceTreeModelPath = "/proc/device-tree/model";
            if (File.Exists(deviceTreeModelPath))
            {
                var model = await File.ReadAllTextAsync(deviceTreeModelPath, ct);
                model = model.Trim('\0', '\n', '\r'); // Remove null terminators

                if (model.Contains("Raspberry Pi", StringComparison.OrdinalIgnoreCase))
                {
                    return model; // "Raspberry Pi 4 Model B", etc.
                }

                if (model.Contains("BeagleBone", StringComparison.OrdinalIgnoreCase))
                {
                    return model;
                }

                // Other known edge platforms
                return model;
            }
        }
        catch
        {
            // File read failure, continue with other detection
        }

        return null;
    }

    [SupportedOSPlatform("linux")]
    private async Task<long> GetLinuxTotalMemoryMbAsync(CancellationToken ct)
    {
        try
        {
            // Read /proc/meminfo to get total memory
            const string meminfoPath = "/proc/meminfo";
            if (File.Exists(meminfoPath))
            {
                var lines = await File.ReadAllLinesAsync(meminfoPath, ct);
                foreach (var line in lines)
                {
                    if (line.StartsWith("MemTotal:", StringComparison.OrdinalIgnoreCase))
                    {
                        // Format: "MemTotal:        1024000 kB"
                        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2 && long.TryParse(parts[1], out var memKb))
                        {
                            return memKb / 1024; // Convert KB to MB
                        }
                    }
                }
            }
        }
        catch
        {
            // File read failure
        }

        return 0;
    }

    private string? ClassifyGenericEdgePlatform(int gpioCount, int i2cCount, int spiCount, long memoryMb)
    {
        // If GPIO/I2C/SPI present AND low memory, classify as generic edge
        if ((gpioCount > 0 || i2cCount > 0 || spiCount > 0) && memoryMb < 2048 && memoryMb > 0)
        {
            if (memoryMb <= 512)
            {
                return "Generic IoT (Low Memory)";
            }
            else if (memoryMb <= 1024)
            {
                return "Industrial Gateway (512MB-1GB)";
            }
            else
            {
                return "Industrial Gateway (1GB-2GB)";
            }
        }

        return null;
    }
}
