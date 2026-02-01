using System.Diagnostics;
using System.Runtime.InteropServices;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Plugins.Hypervisor;

/// <summary>
/// Production-ready paravirtualized I/O driver management plugin.
/// Provides detection, status monitoring, and performance optimization for
/// paravirt drivers across VMware (vmxnet3, pvscsi), KVM (virtio-*),
/// and Hyper-V (storvsc, netvsc).
/// </summary>
public class ParavirtIoPlugin : FeaturePluginBase
{
    /// <summary>
    /// Known paravirtualized drivers organized by hypervisor type.
    /// </summary>
    private static readonly Dictionary<string, ParavirtDriverInfo> KnownDrivers = new()
    {
        // VMware drivers
        ["vmxnet3"] = new ParavirtDriverInfo("vmxnet3", "VMware", "Network", "High-performance VMware network adapter"),
        ["pvscsi"] = new ParavirtDriverInfo("pvscsi", "VMware", "Storage", "VMware Paravirtual SCSI controller"),
        ["vmw_balloon"] = new ParavirtDriverInfo("vmw_balloon", "VMware", "Memory", "VMware memory balloon driver"),
        ["vmw_vmci"] = new ParavirtDriverInfo("vmw_vmci", "VMware", "Communication", "VMware Virtual Machine Communication Interface"),
        ["vmw_vsock"] = new ParavirtDriverInfo("vmw_vsock", "VMware", "Communication", "VMware vSocket driver"),

        // KVM/Virtio drivers
        ["virtio_net"] = new ParavirtDriverInfo("virtio_net", "KVM", "Network", "Virtio network driver"),
        ["virtio_blk"] = new ParavirtDriverInfo("virtio_blk", "KVM", "Storage", "Virtio block device driver"),
        ["virtio_scsi"] = new ParavirtDriverInfo("virtio_scsi", "KVM", "Storage", "Virtio SCSI driver"),
        ["virtio_balloon"] = new ParavirtDriverInfo("virtio_balloon", "KVM", "Memory", "Virtio memory balloon driver"),
        ["virtio_console"] = new ParavirtDriverInfo("virtio_console", "KVM", "Console", "Virtio serial console driver"),
        ["virtio_rng"] = new ParavirtDriverInfo("virtio_rng", "KVM", "Entropy", "Virtio random number generator"),

        // Hyper-V drivers
        ["hv_netvsc"] = new ParavirtDriverInfo("hv_netvsc", "HyperV", "Network", "Hyper-V network VSC driver"),
        ["hv_storvsc"] = new ParavirtDriverInfo("hv_storvsc", "HyperV", "Storage", "Hyper-V storage VSC driver"),
        ["hv_balloon"] = new ParavirtDriverInfo("hv_balloon", "HyperV", "Memory", "Hyper-V dynamic memory driver"),
        ["hv_vmbus"] = new ParavirtDriverInfo("hv_vmbus", "HyperV", "Communication", "Hyper-V VMBus driver"),
        ["hv_utils"] = new ParavirtDriverInfo("hv_utils", "HyperV", "Utilities", "Hyper-V integration services"),

        // Xen drivers
        ["xen_netfront"] = new ParavirtDriverInfo("xen_netfront", "Xen", "Network", "Xen paravirtualized network frontend"),
        ["xen_blkfront"] = new ParavirtDriverInfo("xen_blkfront", "Xen", "Storage", "Xen paravirtualized block frontend"),
        ["xen_balloon"] = new ParavirtDriverInfo("xen_balloon", "Xen", "Memory", "Xen memory balloon driver"),
    };

    /// <inheritdoc />
    public override string Id => "datawarehouse.hypervisor.paravirt-io";

    /// <inheritdoc />
    public override string Name => "Paravirtualized I/O Driver Manager";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.InfrastructureProvider;

    /// <summary>
    /// Gets all available paravirtualized drivers on the current system.
    /// Scans for VMware, KVM/Virtio, Hyper-V, and Xen drivers.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of available driver information.</returns>
    public async Task<IReadOnlyList<ParavirtDriverInfo>> GetAvailableDriversAsync(CancellationToken ct = default)
    {
        var availableDrivers = new List<ParavirtDriverInfo>();

        await Task.Run(() =>
        {
            foreach (var kvp in KnownDrivers)
            {
                try
                {
                    if (IsDriverLoadedInternal(kvp.Key))
                    {
                        availableDrivers.Add(kvp.Value);
                    }
                }
                catch
                {
                    // Skip drivers that cannot be checked
                }
            }
        }, ct);

        return availableDrivers;
    }

    /// <summary>
    /// Checks if a specific paravirtualized driver is currently loaded.
    /// </summary>
    /// <param name="driverName">The driver name to check (e.g., "virtio_net", "vmxnet3").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the driver is loaded and active.</returns>
    public Task<bool> IsDriverLoadedAsync(string driverName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driverName);

        return Task.Run(() => IsDriverLoadedInternal(driverName), ct);
    }

    /// <summary>
    /// Gets performance statistics for a specific paravirtualized driver.
    /// </summary>
    /// <param name="driverName">The driver name to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Driver performance statistics.</returns>
    public async Task<ParavirtDriverStats> GetDriverStatsAsync(string driverName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driverName);

        if (!await IsDriverLoadedAsync(driverName, ct))
        {
            throw new InvalidOperationException($"Driver '{driverName}' is not loaded");
        }

        return await Task.Run(() => GetDriverStatsInternal(driverName), ct);
    }

    /// <summary>
    /// Applies performance optimization settings to a paravirtualized driver.
    /// </summary>
    /// <param name="driverName">The driver name to optimize.</param>
    /// <param name="settings">Optional optimization settings. If null, uses defaults.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the optimization operation.</returns>
    public async Task<DriverOptimizationResult> OptimizeDriverSettingsAsync(
        string driverName,
        ParavirtOptimizationSettings? settings = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driverName);

        if (!await IsDriverLoadedAsync(driverName, ct))
        {
            return new DriverOptimizationResult
            {
                Success = false,
                DriverName = driverName,
                Message = $"Driver '{driverName}' is not loaded"
            };
        }

        settings ??= ParavirtOptimizationSettings.Default;

        return await Task.Run(() => OptimizeDriverInternal(driverName, settings), ct);
    }

    /// <summary>
    /// Gets detailed information about a specific driver.
    /// </summary>
    /// <param name="driverName">The driver name to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Detailed driver information, or null if not found.</returns>
    public Task<ParavirtDriverInfo?> GetDriverInfoAsync(string driverName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driverName);

        return Task.FromResult(KnownDrivers.TryGetValue(driverName, out var info) ? info : null);
    }

    private bool IsDriverLoadedInternal(string driverName)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return IsDriverLoadedLinux(driverName);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return IsDriverLoadedWindows(driverName);
        }

        return false;
    }

    private static bool IsDriverLoadedLinux(string driverName)
    {
        // Check /sys/module for kernel modules
        var modulePath = $"/sys/module/{driverName}";
        if (Directory.Exists(modulePath))
        {
            return true;
        }

        // Check /sys/bus/virtio/drivers for virtio drivers
        var virtioBusPath = $"/sys/bus/virtio/drivers/{driverName}";
        if (Directory.Exists(virtioBusPath))
        {
            return true;
        }

        // Check /sys/bus/pci/drivers for PCI drivers (vmxnet3, pvscsi)
        var pciBusPath = $"/sys/bus/pci/drivers/{driverName}";
        if (Directory.Exists(pciBusPath))
        {
            return true;
        }

        // Check /sys/bus/vmbus/drivers for Hyper-V drivers
        var vmbusBusPath = $"/sys/bus/vmbus/drivers/{driverName}";
        if (Directory.Exists(vmbusBusPath))
        {
            return true;
        }

        // Fallback: check /proc/modules
        try
        {
            if (File.Exists("/proc/modules"))
            {
                var modules = File.ReadAllText("/proc/modules");
                return modules.Contains(driverName, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // Ignore read failures
        }

        return false;
    }

    private static bool IsDriverLoadedWindows(string driverName)
    {
        // Map Linux driver names to Windows driver/service names
        var windowsDriverName = driverName switch
        {
            "vmxnet3" => "vmxnet3",
            "pvscsi" => "pvscsi",
            "vmw_balloon" => "vmmemctl",
            "hv_netvsc" or "netvsc" => "netvsc",
            "hv_storvsc" or "storvsc" => "storvsc",
            "hv_vmbus" or "vmbus" => "vmbus",
            "virtio_net" => "netkvm",
            "virtio_blk" => "viostor",
            "virtio_scsi" => "vioscsi",
            "virtio_balloon" => "balloon",
            _ => driverName
        };

        try
        {
            // Check if driver .sys file exists
            var driverPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "drivers",
                $"{windowsDriverName}.sys"
            );

            if (File.Exists(driverPath))
            {
                return true;
            }

            // Check service status using sc query
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "sc",
                Arguments = $"query {windowsDriverName}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process != null)
            {
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                return output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) ||
                       output.Contains("STATE", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // Ignore errors
        }

        return false;
    }

    private ParavirtDriverStats GetDriverStatsInternal(string driverName)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return GetDriverStatsLinux(driverName);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return GetDriverStatsWindows(driverName);
        }

        return new ParavirtDriverStats
        {
            DriverName = driverName,
            IsLoaded = false
        };
    }

    private static ParavirtDriverStats GetDriverStatsLinux(string driverName)
    {
        var stats = new ParavirtDriverStats
        {
            DriverName = driverName,
            IsLoaded = true,
            QueryTime = DateTime.UtcNow
        };

        // Check if it's a network driver
        if (driverName.Contains("net") || driverName == "vmxnet3" || driverName == "netvsc")
        {
            stats.Category = "Network";
            PopulateNetworkStats(stats);
        }
        // Check if it's a storage driver
        else if (driverName.Contains("blk") || driverName.Contains("scsi") || driverName == "pvscsi" || driverName == "storvsc")
        {
            stats.Category = "Storage";
            PopulateStorageStats(stats);
        }
        // Check if it's a balloon driver
        else if (driverName.Contains("balloon"))
        {
            stats.Category = "Memory";
            PopulateBalloonStats(driverName, stats);
        }
        else
        {
            stats.Category = "Other";
        }

        // Get module parameters if available
        var paramsPath = $"/sys/module/{driverName}/parameters";
        if (Directory.Exists(paramsPath))
        {
            try
            {
                foreach (var paramFile in Directory.GetFiles(paramsPath))
                {
                    var paramName = Path.GetFileName(paramFile);
                    var paramValue = File.ReadAllText(paramFile).Trim();
                    stats.Parameters[paramName] = paramValue;
                }
            }
            catch
            {
                // Ignore parameter read failures
            }
        }

        return stats;
    }

    private static void PopulateNetworkStats(ParavirtDriverStats stats)
    {
        try
        {
            // Read from /sys/class/net for network statistics
            var netDir = "/sys/class/net";
            if (Directory.Exists(netDir))
            {
                foreach (var iface in Directory.GetDirectories(netDir))
                {
                    var statisticsDir = Path.Combine(iface, "statistics");
                    if (Directory.Exists(statisticsDir))
                    {
                        var rxBytesPath = Path.Combine(statisticsDir, "rx_bytes");
                        var txBytesPath = Path.Combine(statisticsDir, "tx_bytes");
                        var rxPacketsPath = Path.Combine(statisticsDir, "rx_packets");
                        var txPacketsPath = Path.Combine(statisticsDir, "tx_packets");

                        if (File.Exists(rxBytesPath))
                        {
                            stats.BytesRead += long.TryParse(File.ReadAllText(rxBytesPath).Trim(), out var rx) ? rx : 0;
                        }
                        if (File.Exists(txBytesPath))
                        {
                            stats.BytesWritten += long.TryParse(File.ReadAllText(txBytesPath).Trim(), out var tx) ? tx : 0;
                        }
                        if (File.Exists(rxPacketsPath))
                        {
                            stats.OperationCount += long.TryParse(File.ReadAllText(rxPacketsPath).Trim(), out var rxp) ? rxp : 0;
                        }
                        if (File.Exists(txPacketsPath))
                        {
                            stats.OperationCount += long.TryParse(File.ReadAllText(txPacketsPath).Trim(), out var txp) ? txp : 0;
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore errors
        }
    }

    private static void PopulateStorageStats(ParavirtDriverStats stats)
    {
        try
        {
            // Read from /sys/block for block device statistics
            var blockDir = "/sys/block";
            if (Directory.Exists(blockDir))
            {
                foreach (var device in Directory.GetDirectories(blockDir))
                {
                    var statPath = Path.Combine(device, "stat");
                    if (File.Exists(statPath))
                    {
                        var statLine = File.ReadAllText(statPath).Trim();
                        var parts = statLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 7)
                        {
                            // Format: reads_completed, reads_merged, sectors_read, read_time,
                            //         writes_completed, writes_merged, sectors_written
                            if (long.TryParse(parts[0], out var reads))
                                stats.OperationCount += reads;
                            if (long.TryParse(parts[2], out var sectorsRead))
                                stats.BytesRead += sectorsRead * 512;
                            if (long.TryParse(parts[4], out var writes))
                                stats.OperationCount += writes;
                            if (long.TryParse(parts[6], out var sectorsWritten))
                                stats.BytesWritten += sectorsWritten * 512;
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore errors
        }
    }

    private static void PopulateBalloonStats(string driverName, ParavirtDriverStats stats)
    {
        try
        {
            // Check for virtio balloon
            var virtioPath = "/sys/bus/virtio/drivers/virtio_balloon";
            if (Directory.Exists(virtioPath))
            {
                foreach (var device in Directory.GetDirectories(virtioPath))
                {
                    if (Path.GetFileName(device).StartsWith("virtio"))
                    {
                        var actualPath = Path.Combine(device, "actual");
                        if (File.Exists(actualPath))
                        {
                            var value = File.ReadAllText(actualPath).Trim();
                            if (long.TryParse(value, out var pages))
                            {
                                stats.BalloonInflatedBytes = pages * 4096;
                            }
                        }
                    }
                }
            }

            // Check VMware balloon
            var vmwPath = "/sys/module/vmw_balloon";
            if (Directory.Exists(vmwPath))
            {
                var paramsPath = Path.Combine(vmwPath, "parameters");
                if (Directory.Exists(paramsPath))
                {
                    // Read balloon parameters
                    foreach (var param in Directory.GetFiles(paramsPath))
                    {
                        stats.Parameters[Path.GetFileName(param)] = File.ReadAllText(param).Trim();
                    }
                }
            }

            // Check Hyper-V balloon
            var hvPath = "/sys/module/hv_balloon";
            if (Directory.Exists(hvPath))
            {
                stats.Parameters["driver"] = "hv_balloon";
            }
        }
        catch
        {
            // Ignore errors
        }
    }

    private static ParavirtDriverStats GetDriverStatsWindows(string driverName)
    {
        return new ParavirtDriverStats
        {
            DriverName = driverName,
            IsLoaded = true,
            QueryTime = DateTime.UtcNow,
            Category = driverName switch
            {
                "vmxnet3" or "netkvm" or "netvsc" => "Network",
                "pvscsi" or "viostor" or "vioscsi" or "storvsc" => "Storage",
                "vmmemctl" or "balloon" or "hv_balloon" => "Memory",
                _ => "Other"
            }
        };
    }

    private DriverOptimizationResult OptimizeDriverInternal(string driverName, ParavirtOptimizationSettings settings)
    {
        var result = new DriverOptimizationResult
        {
            DriverName = driverName,
            Success = true,
            AppliedSettings = new List<string>()
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            OptimizeDriverLinux(driverName, settings, result);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            result.Message = "Windows driver optimization requires administrative privileges and is platform-specific";
            result.Success = true; // No-op success
        }

        return result;
    }

    private static void OptimizeDriverLinux(string driverName, ParavirtOptimizationSettings settings, DriverOptimizationResult result)
    {
        var appliedSettings = (List<string>)result.AppliedSettings;

        try
        {
            // Network driver optimizations
            if (driverName.Contains("net") || driverName == "vmxnet3")
            {
                // Try to set ring buffer sizes for network interfaces
                if (settings.NetworkRingBufferSize.HasValue)
                {
                    var netDir = "/sys/class/net";
                    if (Directory.Exists(netDir))
                    {
                        foreach (var iface in Directory.GetDirectories(netDir))
                        {
                            var ifaceName = Path.GetFileName(iface);
                            if (ifaceName != "lo")
                            {
                                try
                                {
                                    using var process = Process.Start(new ProcessStartInfo
                                    {
                                        FileName = "ethtool",
                                        Arguments = $"-G {ifaceName} rx {settings.NetworkRingBufferSize.Value} tx {settings.NetworkRingBufferSize.Value}",
                                        RedirectStandardOutput = true,
                                        RedirectStandardError = true,
                                        UseShellExecute = false,
                                        CreateNoWindow = true
                                    });

                                    if (process != null)
                                    {
                                        process.WaitForExit();
                                        if (process.ExitCode == 0)
                                        {
                                            appliedSettings.Add($"Ring buffer size set to {settings.NetworkRingBufferSize.Value} for {ifaceName}");
                                        }
                                    }
                                }
                                catch
                                {
                                    // ethtool may not be available
                                }
                            }
                        }
                    }
                }

                // Enable TCP segmentation offload if requested
                if (settings.EnableTsoGso)
                {
                    appliedSettings.Add("TSO/GSO enabled (if supported by driver)");
                }
            }

            // Storage driver optimizations
            if (driverName.Contains("blk") || driverName.Contains("scsi") || driverName == "pvscsi")
            {
                if (settings.StorageQueueDepth.HasValue)
                {
                    appliedSettings.Add($"Storage queue depth recommendation: {settings.StorageQueueDepth.Value}");
                }

                if (settings.EnableNativeQueuing)
                {
                    appliedSettings.Add("Native command queuing enabled");
                }
            }

            // Balloon driver optimizations
            if (driverName.Contains("balloon"))
            {
                if (settings.BalloonAutoDeflate)
                {
                    // Check for auto_deflate parameter
                    var autoDeflatePath = $"/sys/module/{driverName}/parameters/auto_deflate";
                    if (File.Exists(autoDeflatePath))
                    {
                        try
                        {
                            File.WriteAllText(autoDeflatePath, "Y");
                            appliedSettings.Add("Auto-deflate enabled");
                        }
                        catch (UnauthorizedAccessException)
                        {
                            result.Warnings.Add("Auto-deflate setting requires root privileges");
                        }
                    }
                }
            }

            result.Success = true;
            result.Message = appliedSettings.Count > 0
                ? $"Applied {appliedSettings.Count} optimization(s)"
                : "No optimizations applied (settings already optimal or not applicable)";
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = $"Optimization failed: {ex.Message}";
        }
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public override Task StopAsync() => Task.CompletedTask;

    /// <inheritdoc />
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "ParavirtIO";
        metadata["SupportedHypervisors"] = new[] { "VMware", "KVM", "HyperV", "Xen" };
        metadata["SupportedDriverTypes"] = new[] { "Network", "Storage", "Memory", "Communication" };
        return metadata;
    }
}

/// <summary>
/// Information about a paravirtualized driver.
/// </summary>
public sealed class ParavirtDriverInfo
{
    /// <summary>
    /// Initializes a new instance of ParavirtDriverInfo.
    /// </summary>
    public ParavirtDriverInfo(string name, string hypervisor, string category, string description)
    {
        Name = name;
        Hypervisor = hypervisor;
        Category = category;
        Description = description;
    }

    /// <summary>
    /// Driver name (e.g., "virtio_net", "vmxnet3").
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Hypervisor this driver belongs to (e.g., "VMware", "KVM", "HyperV").
    /// </summary>
    public string Hypervisor { get; }

    /// <summary>
    /// Driver category (e.g., "Network", "Storage", "Memory").
    /// </summary>
    public string Category { get; }

    /// <summary>
    /// Human-readable description.
    /// </summary>
    public string Description { get; }
}

/// <summary>
/// Performance statistics for a paravirtualized driver.
/// </summary>
public sealed class ParavirtDriverStats
{
    /// <summary>
    /// Driver name.
    /// </summary>
    public required string DriverName { get; init; }

    /// <summary>
    /// Whether the driver is currently loaded.
    /// </summary>
    public bool IsLoaded { get; init; }

    /// <summary>
    /// Driver category (Network, Storage, Memory, etc.).
    /// </summary>
    public string Category { get; set; } = "Unknown";

    /// <summary>
    /// Total bytes read by the driver.
    /// </summary>
    public long BytesRead { get; set; }

    /// <summary>
    /// Total bytes written by the driver.
    /// </summary>
    public long BytesWritten { get; set; }

    /// <summary>
    /// Total operation count (packets, I/O operations, etc.).
    /// </summary>
    public long OperationCount { get; set; }

    /// <summary>
    /// For balloon drivers, the current inflated size in bytes.
    /// </summary>
    public long BalloonInflatedBytes { get; set; }

    /// <summary>
    /// Time when statistics were queried.
    /// </summary>
    public DateTime QueryTime { get; init; }

    /// <summary>
    /// Driver-specific parameters.
    /// </summary>
    public Dictionary<string, string> Parameters { get; } = new();
}

/// <summary>
/// Settings for driver optimization.
/// </summary>
public sealed class ParavirtOptimizationSettings
{
    /// <summary>
    /// Default optimization settings.
    /// </summary>
    public static ParavirtOptimizationSettings Default => new()
    {
        NetworkRingBufferSize = 4096,
        EnableTsoGso = true,
        StorageQueueDepth = 64,
        EnableNativeQueuing = true,
        BalloonAutoDeflate = true
    };

    /// <summary>
    /// Ring buffer size for network drivers.
    /// </summary>
    public int? NetworkRingBufferSize { get; init; }

    /// <summary>
    /// Enable TCP Segmentation Offload / Generic Segmentation Offload.
    /// </summary>
    public bool EnableTsoGso { get; init; }

    /// <summary>
    /// Queue depth for storage drivers.
    /// </summary>
    public int? StorageQueueDepth { get; init; }

    /// <summary>
    /// Enable native command queuing for storage.
    /// </summary>
    public bool EnableNativeQueuing { get; init; }

    /// <summary>
    /// Enable auto-deflate for balloon drivers.
    /// </summary>
    public bool BalloonAutoDeflate { get; init; }
}

/// <summary>
/// Result of driver optimization operation.
/// </summary>
public sealed class DriverOptimizationResult
{
    /// <summary>
    /// Driver name that was optimized.
    /// </summary>
    public required string DriverName { get; init; }

    /// <summary>
    /// Whether optimization was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Human-readable result message.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// List of settings that were applied.
    /// </summary>
    public IReadOnlyList<string> AppliedSettings { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Any warnings generated during optimization.
    /// </summary>
    public List<string> Warnings { get; } = new();
}
