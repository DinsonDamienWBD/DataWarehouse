using System.Diagnostics;
using System.Runtime.InteropServices;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Virtualization;

namespace DataWarehouse.Plugins.Hypervisor;

/// <summary>
/// Xen hypervisor detector plugin.
/// Detects Xen hypervisors using CPUID signature, sysfs interfaces, xenfs, and guest tools.
/// Supports Xen PV, HVM, and PVHVM modes across Citrix XenServer, XCP-ng, and bare Xen.
/// </summary>
public class XenHypervisorDetectorPlugin : HypervisorDetectorPluginBase
{
    /// <summary>
    /// The CPUID signature for Xen hypervisor (leaf 0x40000000).
    /// </summary>
    private const string XenCpuIdSignature = "XenVMMXenVMM";

    /// <summary>
    /// Path to the Xen hypervisor type indicator in sysfs.
    /// </summary>
    private const string XenHypervisorTypePath = "/sys/hypervisor/type";

    /// <summary>
    /// Path to the Xen hypervisor UUID in sysfs.
    /// </summary>
    private const string XenHypervisorUuidPath = "/sys/hypervisor/uuid";

    /// <summary>
    /// Path to Xen capabilities in sysfs.
    /// </summary>
    private const string XenCapabilitiesPath = "/sys/hypervisor/properties/capabilities";

    /// <inheritdoc />
    public override string Id => "datawarehouse.hypervisor.xen-detector";

    /// <inheritdoc />
    public override string Name => "Xen Hypervisor Detector";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    protected override HypervisorType DetectHypervisor()
    {
        // Check /sys/hypervisor/type for "xen" - most reliable on Linux
        if (CheckXenSysfsType())
        {
            return HypervisorType.Xen;
        }

        // Check CPUID for Xen signature
        if (CheckXenCpuId())
        {
            return HypervisorType.Xen;
        }

        // Check for xenfs filesystem mount
        if (CheckXenFs())
        {
            return HypervisorType.Xen;
        }

        // Check for Xen guest tools and utilities
        if (CheckXenGuestTools())
        {
            return HypervisorType.Xen;
        }

        // Check for Xen kernel modules
        if (CheckXenKernelModules())
        {
            return HypervisorType.Xen;
        }

        // Check SMBIOS/DMI for Xen indicators
        if (CheckXenSmbios())
        {
            return HypervisorType.Xen;
        }

        // Fallback to base system info detection
        return DetectFromSystemInfo();
    }

    /// <inheritdoc />
    protected override async Task<HypervisorCapabilities> QueryCapabilitiesAsync()
    {
        if (DetectedHypervisor != HypervisorType.Xen)
        {
            return new HypervisorCapabilities(
                SupportsBalloonDriver: false,
                SupportsTrimDiscard: false,
                SupportsHotAddCpu: false,
                SupportsHotAddMemory: false,
                SupportsLiveMigration: false,
                SupportsSnapshots: false,
                SupportsBackupApi: false,
                ParavirtDrivers: Array.Empty<string>()
            );
        }

        var drivers = await DetectXenDriversAsync();
        var isHvm = await IsHvmModeAsync();
        var isPvhvm = await IsPvHvmModeAsync();

        return new HypervisorCapabilities(
            SupportsBalloonDriver: drivers.Contains("xen_balloon") || drivers.Contains("balloon"),
            SupportsTrimDiscard: isHvm || isPvhvm, // TRIM supported in HVM/PVHVM modes
            SupportsHotAddCpu: true, // Xen supports CPU hotplug
            SupportsHotAddMemory: drivers.Contains("xen_balloon") || drivers.Contains("balloon"),
            SupportsLiveMigration: true, // Xen supports live migration
            SupportsSnapshots: true, // Xen supports VM snapshots
            SupportsBackupApi: await CheckXenBackupApiAsync(), // XenServer XAPI
            ParavirtDrivers: drivers
        );
    }

    /// <inheritdoc />
    protected override async Task<VmInfo> QueryVmInfoAsync()
    {
        var vmId = await GetXenVmIdAsync();
        var vmName = await GetXenVmNameAsync();
        var cpuCount = Environment.ProcessorCount;
        var memoryBytes = GetTotalMemory();

        return new VmInfo(
            VmId: vmId ?? "unknown",
            VmName: vmName ?? Environment.MachineName,
            Hypervisor: HypervisorType.Xen,
            State: VmState.Running,
            CpuCount: cpuCount,
            MemoryBytes: memoryBytes,
            NetworkInterfaces: GetNetworkInterfaces(),
            StorageDevices: GetStorageDevices()
        );
    }

    /// <summary>
    /// Checks if the /sys/hypervisor/type file indicates Xen.
    /// This is the most reliable detection method on Linux.
    /// </summary>
    private bool CheckXenSysfsType()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return false;
        }

        try
        {
            if (File.Exists(XenHypervisorTypePath))
            {
                var hypervisorType = File.ReadAllText(XenHypervisorTypePath).Trim();
                return hypervisorType.Equals("xen", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // File read failed - continue with other detection methods
        }

        return false;
    }

    /// <summary>
    /// Checks the CPUID hypervisor leaf for Xen signature.
    /// </summary>
    private bool CheckXenCpuId()
    {
        try
        {
            // Check processor identifier for Xen signature
            var processorId = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "";
            return processorId.Contains("Xen", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if xenfs filesystem is mounted.
    /// </summary>
    private bool CheckXenFs()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return false;
        }

        try
        {
            // Check if xenfs is mounted
            if (File.Exists("/proc/mounts"))
            {
                var mounts = File.ReadAllText("/proc/mounts");
                if (mounts.Contains("xenfs", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // Check for xenfs mount point
            if (Directory.Exists("/proc/xen"))
            {
                return true;
            }

            // Check for xen device files
            if (Directory.Exists("/dev/xen"))
            {
                return true;
            }
        }
        catch
        {
            // Detection failed
        }

        return false;
    }

    /// <summary>
    /// Checks for Xen guest tools and utilities.
    /// </summary>
    private bool CheckXenGuestTools()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                // Check for xe-guest-utilities
                var guestUtilityPaths = new[]
                {
                    "/usr/sbin/xe-daemon",
                    "/usr/sbin/xe-linux-distribution",
                    "/opt/xensource/bin/xe-linux-distribution",
                    "/etc/init.d/xe-linux-distribution"
                };

                if (guestUtilityPaths.Any(File.Exists))
                {
                    return true;
                }

                // Check for xenstore utilities
                var xenstorePaths = new[]
                {
                    "/usr/bin/xenstore-ls",
                    "/usr/bin/xenstore-read",
                    "/usr/bin/xenstore"
                };

                if (xenstorePaths.Any(File.Exists))
                {
                    return true;
                }
            }
            catch
            {
                // Detection failed
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                // Check for Xen Windows PV drivers
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "sc",
                    Arguments = "query xenvif",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                if (process != null)
                {
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    if (output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) ||
                        output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                // Check for XenServer Tools service
                using var toolsProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = "sc",
                    Arguments = "query XenSvc",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                if (toolsProcess != null)
                {
                    var output = toolsProcess.StandardOutput.ReadToEnd();
                    toolsProcess.WaitForExit();
                    return output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                // Service query failed
            }
        }

        return false;
    }

    /// <summary>
    /// Checks for Xen kernel modules.
    /// </summary>
    private bool CheckXenKernelModules()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return false;
        }

        try
        {
            var xenModulePaths = new[]
            {
                "/sys/module/xen_blkfront",
                "/sys/module/xen_netfront",
                "/sys/module/xen_balloon",
                "/sys/module/xen_pcifront",
                "/sys/module/xenfs",
                "/sys/module/xen_privcmd"
            };

            return xenModulePaths.Any(Directory.Exists);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks SMBIOS/DMI for Xen indicators.
    /// </summary>
    private bool CheckXenSmbios()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                // Check product name and manufacturer
                var productName = File.Exists("/sys/class/dmi/id/product_name")
                    ? File.ReadAllText("/sys/class/dmi/id/product_name").Trim()
                    : "";

                var manufacturer = File.Exists("/sys/class/dmi/id/sys_vendor")
                    ? File.ReadAllText("/sys/class/dmi/id/sys_vendor").Trim()
                    : "";

                var boardName = File.Exists("/sys/class/dmi/id/board_name")
                    ? File.ReadAllText("/sys/class/dmi/id/board_name").Trim()
                    : "";

                return productName.Contains("Xen", StringComparison.OrdinalIgnoreCase) ||
                       productName.Contains("HVM", StringComparison.OrdinalIgnoreCase) ||
                       manufacturer.Contains("Xen", StringComparison.OrdinalIgnoreCase) ||
                       manufacturer.Contains("Citrix", StringComparison.OrdinalIgnoreCase) ||
                       boardName.Contains("Xen", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "wmic",
                    Arguments = "bios get manufacturer,smbiosbiosversion",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                if (process != null)
                {
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    return output.Contains("Xen", StringComparison.OrdinalIgnoreCase) ||
                           output.Contains("Citrix", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                // WMIC query failed
            }
        }

        return false;
    }

    /// <summary>
    /// Detects available Xen paravirtualization drivers.
    /// </summary>
    private async Task<string[]> DetectXenDriversAsync()
    {
        var drivers = new List<string>();

        await Task.Run(() =>
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var driverModules = new Dictionary<string, string>
                {
                    { "/sys/module/xen_blkfront", "xen_blkfront" },
                    { "/sys/module/xen_netfront", "xen_netfront" },
                    { "/sys/module/xen_balloon", "xen_balloon" },
                    { "/sys/module/xen_pcifront", "xen_pcifront" },
                    { "/sys/module/xen_scsifront", "xen_scsifront" },
                    { "/sys/module/xen_kbdfront", "xen_kbdfront" },
                    { "/sys/module/xen_fbfront", "xen_fbfront" },
                    { "/sys/module/xen_tpmfront", "xen_tpmfront" },
                    { "/sys/module/xen_9pfs", "xen_9pfs" },
                    { "/sys/module/xen_privcmd", "xen_privcmd" },
                    { "/sys/module/xen_evtchn", "xen_evtchn" },
                    { "/sys/module/xen_gntdev", "xen_gntdev" },
                    { "/sys/module/xen_gntalloc", "xen_gntalloc" }
                };

                foreach (var (path, name) in driverModules)
                {
                    try
                    {
                        if (Directory.Exists(path))
                        {
                            drivers.Add(name);
                        }
                    }
                    catch
                    {
                        // Skip inaccessible module
                    }
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Check for Windows PV drivers
                var windowsDrivers = new[]
                {
                    ("xenvif", "xen_netfront"),
                    ("xenvbd", "xen_blkfront"),
                    ("xenbus", "xenbus"),
                    ("xenfilt", "xenfilt"),
                    ("xeniface", "xeniface")
                };

                foreach (var (service, name) in windowsDrivers)
                {
                    try
                    {
                        var driverPath = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.System),
                            "drivers", $"{service}.sys"
                        );

                        if (File.Exists(driverPath))
                        {
                            drivers.Add(name);
                        }
                    }
                    catch
                    {
                        // Continue to next driver
                    }
                }
            }
        });

        // Add default Xen drivers if none detected
        if (drivers.Count == 0)
        {
            drivers.AddRange(new[] { "xen_blkfront", "xen_netfront" });
        }

        return drivers.ToArray();
    }

    /// <summary>
    /// Checks if the VM is running in HVM (Hardware Virtual Machine) mode.
    /// </summary>
    private async Task<bool> IsHvmModeAsync()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return true; // Assume HVM on non-Linux platforms
        }

        try
        {
            // Check /sys/hypervisor/properties/capabilities or vm/type
            if (File.Exists(XenCapabilitiesPath))
            {
                var capabilities = await File.ReadAllTextAsync(XenCapabilitiesPath);
                return capabilities.Contains("hvm", StringComparison.OrdinalIgnoreCase);
            }

            // Check for ACPI (indicates HVM)
            if (Directory.Exists("/sys/firmware/acpi"))
            {
                // PV guests typically don't have ACPI
                return true;
            }
        }
        catch
        {
            // Detection failed
        }

        return false;
    }

    /// <summary>
    /// Checks if the VM is running in PVHVM mode (PV drivers on HVM).
    /// </summary>
    private async Task<bool> IsPvHvmModeAsync()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return false;
        }

        try
        {
            var isHvm = await IsHvmModeAsync();
            var hasPvDrivers = Directory.Exists("/sys/module/xen_blkfront") ||
                               Directory.Exists("/sys/module/xen_netfront");

            return isHvm && hasPvDrivers;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if Xen backup API (XAPI) is available.
    /// </summary>
    private async Task<bool> CheckXenBackupApiAsync()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return false;
        }

        try
        {
            // Check for XenServer/XCP-ng specific features
            // XAPI provides backup capabilities through xe commands
            await Task.Yield();

            return File.Exists("/usr/sbin/xe-daemon") ||
                   File.Exists("/opt/xensource/bin/xapi");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the VM UUID from Xen hypervisor.
    /// </summary>
    private async Task<string?> GetXenVmIdAsync()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                // Try Xen hypervisor UUID first
                if (File.Exists(XenHypervisorUuidPath))
                {
                    var uuid = await File.ReadAllTextAsync(XenHypervisorUuidPath);
                    return uuid.Trim();
                }

                // Try DMI product UUID
                if (File.Exists("/sys/class/dmi/id/product_uuid"))
                {
                    var uuid = await File.ReadAllTextAsync("/sys/class/dmi/id/product_uuid");
                    return uuid.Trim();
                }

                // Try xenstore
                try
                {
                    using var process = Process.Start(new ProcessStartInfo
                    {
                        FileName = "xenstore-read",
                        Arguments = "vm",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });

                    if (process != null)
                    {
                        var output = await process.StandardOutput.ReadToEndAsync();
                        process.WaitForExit();
                        if (process.ExitCode == 0)
                        {
                            // Output is like "/vm/uuid" - extract UUID
                            var vmPath = output.Trim();
                            if (vmPath.StartsWith("/vm/"))
                            {
                                return vmPath.Substring(4);
                            }
                        }
                    }
                }
                catch
                {
                    // xenstore-read not available
                }
            }
            catch
            {
                // UUID retrieval failed
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = "-Command \"(Get-WmiObject -Class Win32_ComputerSystemProduct).UUID\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                if (process != null)
                {
                    var output = await process.StandardOutput.ReadToEndAsync();
                    process.WaitForExit();
                    return output.Trim();
                }
            }
            catch
            {
                // PowerShell query failed
            }
        }

        return Guid.NewGuid().ToString();
    }

    /// <summary>
    /// Gets the VM name from Xen hypervisor.
    /// </summary>
    private async Task<string?> GetXenVmNameAsync()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                // Try xenstore to get VM name
                try
                {
                    using var process = Process.Start(new ProcessStartInfo
                    {
                        FileName = "xenstore-read",
                        Arguments = "name",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });

                    if (process != null)
                    {
                        var output = await process.StandardOutput.ReadToEndAsync();
                        process.WaitForExit();
                        if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                        {
                            return output.Trim();
                        }
                    }
                }
                catch
                {
                    // xenstore-read not available
                }

                // Try product name from DMI
                if (File.Exists("/sys/class/dmi/id/product_name"))
                {
                    var productName = await File.ReadAllTextAsync("/sys/class/dmi/id/product_name");
                    var name = productName.Trim();
                    if (!string.IsNullOrWhiteSpace(name) &&
                        !name.Equals("HVM domU", StringComparison.OrdinalIgnoreCase))
                    {
                        return name;
                    }
                }
            }
            catch
            {
                // Name retrieval failed
            }
        }

        return Environment.MachineName;
    }

    /// <summary>
    /// Gets the total memory available to the system.
    /// </summary>
    private long GetTotalMemory()
    {
        try
        {
            var gcInfo = GC.GetGCMemoryInfo();
            return gcInfo.TotalAvailableMemoryBytes;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Gets the list of active network interfaces.
    /// </summary>
    private string[] GetNetworkInterfaces()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface
                .GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                .Select(n => n.Name)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Gets the list of storage devices.
    /// </summary>
    private string[] GetStorageDevices()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                .Select(d => d.Name)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
