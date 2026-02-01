using System.Diagnostics;
using System.Runtime.InteropServices;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Virtualization;

namespace DataWarehouse.Plugins.Hypervisor;

/// <summary>
/// Proxmox VE hypervisor detector plugin.
/// Detects Proxmox VE environments using QEMU guest agent, PVE-specific markers,
/// SMBIOS data, and virtio device detection.
/// Supports both KVM/QEMU and LXC containers on Proxmox VE.
/// </summary>
public class ProxmoxHypervisorDetectorPlugin : HypervisorDetectorPluginBase
{
    /// <summary>
    /// Path to the Proxmox VE configuration directory.
    /// </summary>
    private const string PveConfigPath = "/etc/pve";

    /// <summary>
    /// Path to QEMU guest agent device.
    /// </summary>
    private const string QemuGuestAgentPath = "/dev/virtio-ports/org.qemu.guest_agent.0";

    /// <summary>
    /// Alternative path for QEMU guest agent.
    /// </summary>
    private const string QemuGuestAgentAltPath = "/dev/vport0p1";

    /// <inheritdoc />
    public override string Id => "datawarehouse.hypervisor.proxmox-detector";

    /// <inheritdoc />
    public override string Name => "Proxmox VE Detector";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    protected override HypervisorType DetectHypervisor()
    {
        // Check for Proxmox VE specific markers (highest priority)
        if (CheckProxmoxEnvironment())
        {
            return HypervisorType.Proxmox;
        }

        // Check SMBIOS/DMI for Proxmox indicators
        if (CheckProxmoxSmbios())
        {
            return HypervisorType.Proxmox;
        }

        // Check for QEMU guest agent with Proxmox configuration
        if (CheckQemuGuestAgentProxmox())
        {
            return HypervisorType.Proxmox;
        }

        // Check for Proxmox-specific kernel parameters
        if (CheckProxmoxKernelParams())
        {
            return HypervisorType.Proxmox;
        }

        // Check for LXC container on Proxmox
        if (CheckProxmoxLxc())
        {
            return HypervisorType.Proxmox;
        }

        // Check for virtio devices with QEMU device model
        if (CheckQemuDeviceModel())
        {
            // This indicates KVM/QEMU but not necessarily Proxmox
            // Only return Proxmox if we have additional indicators
            if (HasProxmoxIndicators())
            {
                return HypervisorType.Proxmox;
            }
        }

        // Fallback to base system info detection
        return DetectFromSystemInfo();
    }

    /// <inheritdoc />
    protected override async Task<HypervisorCapabilities> QueryCapabilitiesAsync()
    {
        if (DetectedHypervisor != HypervisorType.Proxmox)
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

        var drivers = await DetectVirtioDriversAsync();
        var isLxc = await IsLxcContainerAsync();

        return new HypervisorCapabilities(
            SupportsBalloonDriver: !isLxc && drivers.Contains("virtio_balloon"),
            SupportsTrimDiscard: true, // Proxmox supports discard with virtio-scsi
            SupportsHotAddCpu: !isLxc, // Not for LXC
            SupportsHotAddMemory: !isLxc && drivers.Contains("virtio_balloon"),
            SupportsLiveMigration: true, // Proxmox supports live migration
            SupportsSnapshots: true, // Proxmox supports snapshots
            SupportsBackupApi: true, // Proxmox Backup Server (PBS) integration
            ParavirtDrivers: drivers
        );
    }

    /// <inheritdoc />
    protected override async Task<VmInfo> QueryVmInfoAsync()
    {
        var vmId = await GetProxmoxVmIdAsync();
        var vmName = await GetProxmoxVmNameAsync();
        var cpuCount = Environment.ProcessorCount;
        var memoryBytes = GetTotalMemory();

        return new VmInfo(
            VmId: vmId ?? "unknown",
            VmName: vmName ?? Environment.MachineName,
            Hypervisor: HypervisorType.Proxmox,
            State: VmState.Running,
            CpuCount: cpuCount,
            MemoryBytes: memoryBytes,
            NetworkInterfaces: GetNetworkInterfaces(),
            StorageDevices: GetStorageDevices()
        );
    }

    /// <summary>
    /// Checks for Proxmox VE specific environment indicators.
    /// </summary>
    private bool CheckProxmoxEnvironment()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return false;
        }

        try
        {
            // Check for /etc/pve (Proxmox config directory)
            if (Directory.Exists(PveConfigPath))
            {
                return true;
            }

            // Check for Proxmox-specific files
            if (File.Exists("/etc/pve-release") ||
                File.Exists("/usr/share/proxmox-ve-release") ||
                File.Exists("/etc/apt/sources.list.d/pve-enterprise.list") ||
                File.Exists("/etc/apt/sources.list.d/pve-no-subscription.list"))
            {
                return true;
            }

            // Check for pvesh command (Proxmox VE shell)
            if (File.Exists("/usr/bin/pvesh") || File.Exists("/usr/sbin/pvesh"))
            {
                return true;
            }

            // Check for pveversion command
            if (File.Exists("/usr/bin/pveversion"))
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
    /// Checks SMBIOS/DMI for Proxmox indicators.
    /// </summary>
    private bool CheckProxmoxSmbios()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                // Check product name
                if (File.Exists("/sys/class/dmi/id/product_name"))
                {
                    var productName = File.ReadAllText("/sys/class/dmi/id/product_name").Trim();
                    if (productName.Contains("Proxmox", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                // Check system vendor
                if (File.Exists("/sys/class/dmi/id/sys_vendor"))
                {
                    var vendor = File.ReadAllText("/sys/class/dmi/id/sys_vendor").Trim();
                    if (vendor.Contains("Proxmox", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                // Check BIOS vendor
                if (File.Exists("/sys/class/dmi/id/bios_vendor"))
                {
                    var biosVendor = File.ReadAllText("/sys/class/dmi/id/bios_vendor").Trim();
                    if (biosVendor.Contains("Proxmox", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                // Check chassis manufacturer
                if (File.Exists("/sys/class/dmi/id/chassis_vendor"))
                {
                    var chassisVendor = File.ReadAllText("/sys/class/dmi/id/chassis_vendor").Trim();
                    if (chassisVendor.Contains("Proxmox", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // SMBIOS read failed
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "wmic",
                    Arguments = "bios get manufacturer,smbiosbiosversion /format:list",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                if (process != null)
                {
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    return output.Contains("Proxmox", StringComparison.OrdinalIgnoreCase);
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
    /// Checks for QEMU guest agent presence with Proxmox configuration.
    /// </summary>
    private bool CheckQemuGuestAgentProxmox()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return false;
        }

        try
        {
            // Check for QEMU guest agent device
            var hasGuestAgent = File.Exists(QemuGuestAgentPath) ||
                               File.Exists(QemuGuestAgentAltPath);

            if (!hasGuestAgent)
            {
                return false;
            }

            // Check for qemu-guest-agent service
            if (File.Exists("/usr/bin/qemu-ga") ||
                File.Exists("/usr/sbin/qemu-ga") ||
                File.Exists("/etc/qemu-ga"))
            {
                // Guest agent present, now check for Proxmox-specific indicators
                return HasProxmoxIndicators();
            }
        }
        catch
        {
            // Detection failed
        }

        return false;
    }

    /// <summary>
    /// Checks kernel parameters for Proxmox indicators.
    /// </summary>
    private bool CheckProxmoxKernelParams()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return false;
        }

        try
        {
            if (File.Exists("/proc/cmdline"))
            {
                var cmdline = File.ReadAllText("/proc/cmdline");
                // Proxmox often sets specific boot parameters
                return cmdline.Contains("proxmox", StringComparison.OrdinalIgnoreCase) ||
                       cmdline.Contains("pve", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // Cmdline read failed
        }

        return false;
    }

    /// <summary>
    /// Checks if running in an LXC container on Proxmox.
    /// </summary>
    private bool CheckProxmoxLxc()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return false;
        }

        try
        {
            // Check for LXC container indicators
            if (File.Exists("/proc/1/environ"))
            {
                var environ = File.ReadAllText("/proc/1/environ");
                if (environ.Contains("container=lxc", StringComparison.OrdinalIgnoreCase))
                {
                    // Check if it's Proxmox LXC
                    return File.Exists("/etc/pve-release") ||
                           HasProxmoxIndicators();
                }
            }

            // Check /run/systemd/container
            if (File.Exists("/run/systemd/container"))
            {
                var container = File.ReadAllText("/run/systemd/container").Trim();
                if (container.Equals("lxc", StringComparison.OrdinalIgnoreCase) ||
                    container.Equals("lxc-libvirt", StringComparison.OrdinalIgnoreCase))
                {
                    return HasProxmoxIndicators();
                }
            }
        }
        catch
        {
            // LXC check failed
        }

        return false;
    }

    /// <summary>
    /// Checks for QEMU device model presence.
    /// </summary>
    private bool CheckQemuDeviceModel()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return false;
        }

        try
        {
            // Check for virtio devices in sysfs
            var virtioPath = "/sys/bus/virtio/devices";
            if (Directory.Exists(virtioPath) && Directory.GetDirectories(virtioPath).Length > 0)
            {
                return true;
            }

            // Check for QEMU in product name
            if (File.Exists("/sys/class/dmi/id/product_name"))
            {
                var productName = File.ReadAllText("/sys/class/dmi/id/product_name").Trim();
                return productName.Contains("QEMU", StringComparison.OrdinalIgnoreCase) ||
                       productName.Contains("Standard PC", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // Device model check failed
        }

        return false;
    }

    /// <summary>
    /// Checks for additional Proxmox-specific indicators.
    /// </summary>
    private bool HasProxmoxIndicators()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return false;
        }

        try
        {
            // Check for Proxmox backup client
            if (File.Exists("/usr/bin/proxmox-backup-client") ||
                File.Exists("/usr/sbin/proxmox-backup-client"))
            {
                return true;
            }

            // Check for pve-qemu-kvm
            if (Directory.Exists("/usr/share/pve-manager") ||
                Directory.Exists("/var/lib/pve-cluster"))
            {
                return true;
            }

            // Check for Proxmox in /etc/hostname or similar
            if (File.Exists("/etc/hostname"))
            {
                var hostname = File.ReadAllText("/etc/hostname").Trim();
                // Check for common Proxmox VM naming patterns
                if (System.Text.RegularExpressions.Regex.IsMatch(hostname, @"^(VM|CT)\d+$"))
                {
                    return true;
                }
            }

            // Check systemd machine info for virtualization
            if (File.Exists("/etc/machine-info"))
            {
                var machineInfo = File.ReadAllText("/etc/machine-info");
                if (machineInfo.Contains("proxmox", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Indicator check failed
        }

        return false;
    }

    /// <summary>
    /// Checks if running inside an LXC container.
    /// </summary>
    private async Task<bool> IsLxcContainerAsync()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return false;
        }

        try
        {
            await Task.Yield();

            // Check /run/systemd/container
            if (File.Exists("/run/systemd/container"))
            {
                var container = File.ReadAllText("/run/systemd/container").Trim();
                return container.Equals("lxc", StringComparison.OrdinalIgnoreCase);
            }

            // Check cgroup for LXC
            if (File.Exists("/proc/1/cgroup"))
            {
                var cgroup = File.ReadAllText("/proc/1/cgroup");
                return cgroup.Contains("/lxc/", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // LXC check failed
        }

        return false;
    }

    /// <summary>
    /// Detects available virtio paravirtualization drivers.
    /// </summary>
    private async Task<string[]> DetectVirtioDriversAsync()
    {
        var drivers = new List<string>();

        await Task.Run(() =>
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var driverModules = new[]
                {
                    "virtio_blk",       // Block device
                    "virtio_net",       // Network
                    "virtio_scsi",      // SCSI controller
                    "virtio_balloon",   // Memory balloon
                    "virtio_console",   // Serial console
                    "virtio_rng",       // Random number generator
                    "virtio_gpu",       // GPU
                    "virtio_input",     // Input devices
                    "9pnet_virtio",     // 9P filesystem
                    "virtiofs"          // VirtioFS
                };

                foreach (var module in driverModules)
                {
                    try
                    {
                        var modulePath = $"/sys/module/{module}";
                        if (Directory.Exists(modulePath))
                        {
                            drivers.Add(module);
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
                // Check for Windows virtio drivers (Red Hat VirtIO drivers)
                var windowsDrivers = new[]
                {
                    ("viostor", "virtio_blk"),
                    ("vioscsi", "virtio_scsi"),
                    ("netkvm", "virtio_net"),
                    ("balloon", "virtio_balloon"),
                    ("vioserial", "virtio_console"),
                    ("viorng", "virtio_rng"),
                    ("viogpudo", "virtio_gpu"),
                    ("vioinput", "virtio_input"),
                    ("viofs", "virtiofs")
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

        // Add default virtio drivers if none detected
        if (drivers.Count == 0)
        {
            drivers.AddRange(new[] { "virtio_blk", "virtio_net", "virtio_scsi" });
        }

        return drivers.ToArray();
    }

    /// <summary>
    /// Gets the VM ID from Proxmox.
    /// </summary>
    private async Task<string?> GetProxmoxVmIdAsync()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                // Try DMI product UUID first
                if (File.Exists("/sys/class/dmi/id/product_uuid"))
                {
                    var uuid = await File.ReadAllTextAsync("/sys/class/dmi/id/product_uuid");
                    return uuid.Trim();
                }

                // Try machine-id as fallback
                if (File.Exists("/etc/machine-id"))
                {
                    var machineId = await File.ReadAllTextAsync("/etc/machine-id");
                    return machineId.Trim();
                }

                // Try to extract VM ID from hostname (Proxmox pattern: VM100, CT101, etc.)
                var hostname = Environment.MachineName;
                var match = System.Text.RegularExpressions.Regex.Match(hostname, @"^(?:VM|CT)(\d+)$");
                if (match.Success)
                {
                    return match.Groups[1].Value;
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
    /// Gets the VM name from Proxmox.
    /// </summary>
    private async Task<string?> GetProxmoxVmNameAsync()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                // Try QEMU guest agent for VM name via product name
                if (File.Exists("/sys/class/dmi/id/product_name"))
                {
                    var productName = await File.ReadAllTextAsync("/sys/class/dmi/id/product_name");
                    var name = productName.Trim();

                    // Proxmox might set VM name in product name
                    if (!string.IsNullOrWhiteSpace(name) &&
                        !name.StartsWith("Standard PC", StringComparison.OrdinalIgnoreCase) &&
                        !name.Equals("QEMU", StringComparison.OrdinalIgnoreCase))
                    {
                        return name;
                    }
                }

                // Try to read from Proxmox-specific location
                // Note: Inside a VM, we typically don't have direct access to PVE config
                // The hostname is often the best we can get
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
