using System.Diagnostics;
using System.Runtime.InteropServices;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Virtualization;

namespace DataWarehouse.Plugins.Hypervisor;

/// <summary>
/// oVirt/Red Hat Virtualization (RHV) hypervisor detector plugin.
/// Detects oVirt and RHV hypervisors using guest agent presence, SMBIOS data,
/// CPUID signatures, and storage fabric markers (GlusterFS, NFS).
/// Supports oVirt community platform and Red Hat Virtualization enterprise platform.
/// </summary>
/// <remarks>
/// <para>
/// oVirt is an open-source virtualization management platform that provides
/// enterprise-level features on top of KVM. RHV (Red Hat Virtualization) is the
/// commercially supported version. Both platforms share the same guest agent
/// and virtualization infrastructure.
/// </para>
/// <para>
/// Detection methods include:
/// <list type="bullet">
/// <item>oVirt/RHV guest agent presence and configuration files</item>
/// <item>SMBIOS product name containing oVirt or RHEV identifiers</item>
/// <item>CPUID hypervisor leaf with KVM signature (underlying hypervisor)</item>
/// <item>GlusterFS or NFS storage mounts typical of oVirt deployments</item>
/// <item>QEMU guest agent with oVirt-specific metadata</item>
/// <item>Windows-specific registry entries and guest tools</item>
/// </list>
/// </para>
/// </remarks>
public class OVirtRhvHypervisorDetectorPlugin : HypervisorDetectorPluginBase
{
    /// <summary>
    /// Path to the oVirt guest agent executable on Linux.
    /// </summary>
    private const string OVirtGuestAgentPath = "/usr/bin/ovirt-guest-agent";

    /// <summary>
    /// Path to the oVirt guest agent configuration file on Linux.
    /// </summary>
    private const string OVirtGuestAgentConfigPath = "/etc/ovirt-guest-agent.conf";

    /// <summary>
    /// Path to the oVirt guest agent directory on Linux.
    /// </summary>
    private const string OVirtGuestAgentDir = "/etc/ovirt-guest-agent";

    /// <summary>
    /// Alternative path for RHV guest agent configuration.
    /// </summary>
    private const string RhvGuestAgentConfigPath = "/etc/rhev-agent.conf";

    /// <summary>
    /// Path to QEMU guest agent socket.
    /// </summary>
    private const string QemuGuestAgentPath = "/dev/virtio-ports/org.qemu.guest_agent.0";

    /// <summary>
    /// oVirt/RHV QEMU virtio channel for guest communication.
    /// </summary>
    private const string OVirtChannelPath = "/dev/virtio-ports/ovirt-guest-agent.0";

    /// <inheritdoc />
    public override string Id => "datawarehouse.hypervisor.ovirt-rhv-detector";

    /// <inheritdoc />
    public override string Name => "oVirt/RHV Hypervisor Detector";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    protected override HypervisorType DetectHypervisor()
    {
        // Check for oVirt/RHV guest agent (most reliable indicator)
        if (CheckOVirtGuestAgent())
        {
            return HypervisorType.OVirtRHV;
        }

        // Check SMBIOS/DMI for oVirt or RHEV product name
        if (CheckOVirtSmbios())
        {
            return HypervisorType.OVirtRHV;
        }

        // Check for oVirt-specific virtio channel
        if (CheckOVirtVirtioChannel())
        {
            return HypervisorType.OVirtRHV;
        }

        // Check Windows registry and guest tools
        if (CheckWindowsOVirtTools())
        {
            return HypervisorType.OVirtRHV;
        }

        // Check for RHV environment markers
        if (CheckRhvEnvironmentMarkers())
        {
            return HypervisorType.OVirtRHV;
        }

        // Check storage fabric markers (GlusterFS, NFS with oVirt patterns)
        if (CheckStorageFabricMarkers())
        {
            return HypervisorType.OVirtRHV;
        }

        // Fallback to system info detection
        return DetectFromSystemInfo();
    }

    /// <inheritdoc />
    protected override async Task<HypervisorCapabilities> QueryCapabilitiesAsync()
    {
        if (DetectedHypervisor != HypervisorType.OVirtRHV)
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

        var drivers = await DetectOVirtDriversAsync();
        var hasGuestAgent = CheckOVirtGuestAgent();
        var isRhv = await IsRedHatVirtualizationAsync();

        return new HypervisorCapabilities(
            SupportsBalloonDriver: drivers.Contains("virtio_balloon"),
            SupportsTrimDiscard: true, // virtio-scsi supports UNMAP/TRIM
            SupportsHotAddCpu: true, // oVirt 4.0+ supports CPU hot-plug
            SupportsHotAddMemory: drivers.Contains("virtio_balloon"), // Memory hot-plug via balloon
            SupportsLiveMigration: true, // Native KVM live migration
            SupportsSnapshots: true, // Live snapshots supported
            SupportsBackupApi: isRhv, // RHV has backup API, oVirt community may not
            ParavirtDrivers: drivers
        );
    }

    /// <inheritdoc />
    protected override async Task<VmInfo> QueryVmInfoAsync()
    {
        var vmId = await GetOVirtVmIdAsync();
        var vmName = await GetOVirtVmNameAsync();
        var cpuCount = Environment.ProcessorCount;
        var memoryBytes = GetTotalMemory();

        return new VmInfo(
            VmId: vmId ?? "unknown",
            VmName: vmName ?? Environment.MachineName,
            Hypervisor: HypervisorType.OVirtRHV,
            State: VmState.Running,
            CpuCount: cpuCount,
            MemoryBytes: memoryBytes,
            NetworkInterfaces: GetNetworkInterfaces(),
            StorageDevices: GetStorageDevices()
        );
    }

    /// <summary>
    /// Checks for the presence of oVirt/RHV guest agent.
    /// The guest agent provides enhanced VM management capabilities.
    /// </summary>
    /// <returns>True if oVirt guest agent is detected.</returns>
    private bool CheckOVirtGuestAgent()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                // Check for ovirt-guest-agent executable
                if (File.Exists(OVirtGuestAgentPath))
                {
                    return true;
                }

                // Check for configuration files
                if (File.Exists(OVirtGuestAgentConfigPath) || Directory.Exists(OVirtGuestAgentDir))
                {
                    return true;
                }

                // Check for RHV-specific configuration
                if (File.Exists(RhvGuestAgentConfigPath))
                {
                    return true;
                }

                // Check for running ovirt-guest-agent process
                if (CheckProcessRunning("ovirt-guest-agent"))
                {
                    return true;
                }

                // Check systemd service
                return CheckSystemdService("ovirt-guest-agent");
            }
            catch
            {
                return false;
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return CheckWindowsOVirtGuestAgent();
        }

        return false;
    }

    /// <summary>
    /// Checks for oVirt guest tools on Windows.
    /// </summary>
    /// <returns>True if Windows oVirt guest tools are detected.</returns>
    private bool CheckWindowsOVirtGuestAgent()
    {
        try
        {
            // Check for oVirt Guest Agent service
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "sc",
                Arguments = "query \"oVirt Guest Agent\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process != null)
            {
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                if (output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // Check for RHEV Agent service (alternative name)
            using var rhevProcess = Process.Start(new ProcessStartInfo
            {
                FileName = "sc",
                Arguments = "query \"RHEV-Agent\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (rhevProcess != null)
            {
                var output = rhevProcess.StandardOutput.ReadToEnd();
                rhevProcess.WaitForExit();
                return output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // Service query failed
        }

        return false;
    }

    /// <summary>
    /// Checks SMBIOS data for oVirt/RHEV product identifiers.
    /// </summary>
    /// <returns>True if oVirt/RHEV product is detected in SMBIOS.</returns>
    private bool CheckOVirtSmbios()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                // Check product name
                var productName = File.ReadAllText("/sys/class/dmi/id/product_name").Trim();
                if (productName.Contains("oVirt", StringComparison.OrdinalIgnoreCase) ||
                    productName.Contains("RHEV", StringComparison.OrdinalIgnoreCase) ||
                    productName.Contains("Red Hat", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // Check system vendor
                if (File.Exists("/sys/class/dmi/id/sys_vendor"))
                {
                    var vendor = File.ReadAllText("/sys/class/dmi/id/sys_vendor").Trim();
                    if (vendor.Contains("oVirt", StringComparison.OrdinalIgnoreCase) ||
                        vendor.Contains("Red Hat", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                // Check BIOS vendor
                if (File.Exists("/sys/class/dmi/id/bios_vendor"))
                {
                    var biosVendor = File.ReadAllText("/sys/class/dmi/id/bios_vendor").Trim();
                    if (biosVendor.Contains("oVirt", StringComparison.OrdinalIgnoreCase) ||
                        biosVendor.Contains("Red Hat", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
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
                    Arguments = "computersystem get manufacturer,model",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                if (process != null)
                {
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    return output.Contains("oVirt", StringComparison.OrdinalIgnoreCase) ||
                           output.Contains("RHEV", StringComparison.OrdinalIgnoreCase) ||
                           output.Contains("Red Hat", StringComparison.OrdinalIgnoreCase);
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
    /// Checks for oVirt-specific virtio communication channel.
    /// </summary>
    /// <returns>True if oVirt virtio channel is detected.</returns>
    private bool CheckOVirtVirtioChannel()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return false;
        }

        try
        {
            // Check for oVirt-specific virtio channel
            if (File.Exists(OVirtChannelPath))
            {
                return true;
            }

            // Check for virtio-ports directory with oVirt entries
            var virtioPorts = "/dev/virtio-ports";
            if (Directory.Exists(virtioPorts))
            {
                var ports = Directory.GetFiles(virtioPorts);
                return ports.Any(p =>
                    Path.GetFileName(p).Contains("ovirt", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(p).Contains("rhev", StringComparison.OrdinalIgnoreCase));
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// Checks for Windows-specific oVirt tools and registry entries.
    /// </summary>
    /// <returns>True if Windows oVirt tools are detected.</returns>
    private bool CheckWindowsOVirtTools()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return false;
        }

        try
        {
            // Check for oVirt/RHEV guest tools installation directory
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var ovirtPath = Path.Combine(programFiles, "oVirt Guest Tools");
            var rhevPath = Path.Combine(programFiles, "RHEV-Agent");

            if (Directory.Exists(ovirtPath) || Directory.Exists(rhevPath))
            {
                return true;
            }

            // Check for Spice guest tools (often bundled with oVirt)
            var spicePath = Path.Combine(programFiles, "SPICE Guest Tools");
            if (Directory.Exists(spicePath))
            {
                // Spice alone doesn't confirm oVirt, but combined with virtio drivers it might
                return CheckWindowsVirtioDrivers();
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// Checks for Windows virtio drivers typically installed by oVirt.
    /// </summary>
    /// <returns>True if virtio drivers are detected on Windows.</returns>
    private bool CheckWindowsVirtioDrivers()
    {
        try
        {
            var systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var driversDir = Path.Combine(systemDir, "drivers");

            var virtioDrivers = new[] { "vioscsi.sys", "viostor.sys", "netkvm.sys", "balloon.sys" };
            return virtioDrivers.Any(d => File.Exists(Path.Combine(driversDir, d)));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks for RHV-specific environment markers.
    /// </summary>
    /// <returns>True if RHV environment markers are detected.</returns>
    private bool CheckRhvEnvironmentMarkers()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                // Check for RHV-specific directories
                var rhvPaths = new[]
                {
                    "/etc/rhev",
                    "/var/log/rhev",
                    "/var/lib/rhev"
                };

                if (rhvPaths.Any(p => Directory.Exists(p) || File.Exists(p)))
                {
                    return true;
                }

                // Check for subscription-manager with RHV repos
                if (File.Exists("/etc/yum.repos.d/rhel-rhevm.repo"))
                {
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks for oVirt storage fabric markers (GlusterFS, NFS patterns).
    /// </summary>
    /// <returns>True if oVirt storage patterns are detected.</returns>
    private bool CheckStorageFabricMarkers()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return false;
        }

        try
        {
            // Check /etc/fstab for GlusterFS or NFS mounts with oVirt patterns
            if (File.Exists("/etc/fstab"))
            {
                var fstab = File.ReadAllText("/etc/fstab");
                if (fstab.Contains("glusterfs", StringComparison.OrdinalIgnoreCase) &&
                    (fstab.Contains("ovirt", StringComparison.OrdinalIgnoreCase) ||
                     fstab.Contains("rhev", StringComparison.OrdinalIgnoreCase) ||
                     fstab.Contains("data-domain", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            // Check mounted filesystems
            if (File.Exists("/proc/mounts"))
            {
                var mounts = File.ReadAllText("/proc/mounts");
                // GlusterFS volumes with oVirt naming patterns
                if (mounts.Contains("glusterfs") &&
                    (mounts.Contains("ovirt") || mounts.Contains("rhev") || mounts.Contains("data-domain")))
                {
                    return true;
                }
            }

            // Check for GlusterFS client with oVirt integration
            if (Directory.Exists("/var/lib/glusterd") && CheckOVirtGuestAgent())
            {
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// Checks if a process is running on Linux.
    /// </summary>
    /// <param name="processName">Name of the process to check.</param>
    /// <returns>True if the process is running.</returns>
    private bool CheckProcessRunning(string processName)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "pgrep",
                Arguments = $"-x {processName}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process != null)
            {
                process.WaitForExit();
                return process.ExitCode == 0;
            }
        }
        catch
        {
            // pgrep not available or failed
        }

        return false;
    }

    /// <summary>
    /// Checks if a systemd service is active.
    /// </summary>
    /// <param name="serviceName">Name of the service to check.</param>
    /// <returns>True if the service is active.</returns>
    private bool CheckSystemdService(string serviceName)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "systemctl",
                Arguments = $"is-active {serviceName}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process != null)
            {
                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                return output.Equals("active", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // systemctl not available or failed
        }

        return false;
    }

    /// <summary>
    /// Detects oVirt/RHV paravirtualization drivers.
    /// </summary>
    /// <returns>Array of detected driver names.</returns>
    private async Task<string[]> DetectOVirtDriversAsync()
    {
        var drivers = new List<string>();

        await Task.Run(() =>
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // oVirt/RHV uses standard virtio drivers
                var driverModules = new[]
                {
                    "virtio_blk",       // Block device
                    "virtio_net",       // Network
                    "virtio_scsi",      // SCSI controller
                    "virtio_balloon",   // Memory balloon
                    "virtio_console",   // Serial console
                    "virtio_rng",       // Random number generator
                    "virtio_gpu",       // GPU (SPICE graphics)
                    "virtio_input",     // Input devices
                    "9pnet_virtio",     // 9P filesystem (shared folders)
                    "qxl"               // QXL graphics driver for SPICE
                };

                foreach (var module in driverModules)
                {
                    var modulePath = $"/sys/module/{module}";
                    if (Directory.Exists(modulePath))
                    {
                        drivers.Add(module);
                    }
                }

                // Check for SPICE vdagent
                if (File.Exists("/usr/bin/spice-vdagent") || CheckProcessRunning("spice-vdagent"))
                {
                    drivers.Add("spice_vdagent");
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
                var driversDir = Path.Combine(systemDir, "drivers");

                var virtioDrivers = new Dictionary<string, string>
                {
                    { "viostor.sys", "virtio_blk" },
                    { "vioscsi.sys", "virtio_scsi" },
                    { "netkvm.sys", "virtio_net" },
                    { "balloon.sys", "virtio_balloon" },
                    { "vioserial.sys", "virtio_console" },
                    { "viorng.sys", "virtio_rng" },
                    { "vioinput.sys", "virtio_input" },
                    { "qxldod.sys", "qxl" }
                };

                foreach (var (file, name) in virtioDrivers)
                {
                    if (File.Exists(Path.Combine(driversDir, file)))
                    {
                        drivers.Add(name);
                    }
                }
            }
        });

        // Default drivers if none detected
        if (drivers.Count == 0)
        {
            drivers.AddRange(new[] { "virtio_blk", "virtio_net", "virtio_balloon" });
        }

        return drivers.ToArray();
    }

    /// <summary>
    /// Determines if this is Red Hat Virtualization (enterprise) vs oVirt (community).
    /// </summary>
    /// <returns>True if running on RHV enterprise platform.</returns>
    private async Task<bool> IsRedHatVirtualizationAsync()
    {
        return await Task.Run(() =>
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                try
                {
                    // Check for RHEL with RHV subscription
                    if (File.Exists("/etc/redhat-release"))
                    {
                        var release = File.ReadAllText("/etc/redhat-release");
                        if (release.Contains("Red Hat Enterprise Linux", StringComparison.OrdinalIgnoreCase))
                        {
                            // Check for RHV-specific repos or packages
                            if (File.Exists("/etc/yum.repos.d/rhel-rhevm.repo") ||
                                Directory.Exists("/etc/rhev"))
                            {
                                return true;
                            }
                        }
                    }

                    // Check product name for RHEV
                    if (File.Exists("/sys/class/dmi/id/product_name"))
                    {
                        var productName = File.ReadAllText("/sys/class/dmi/id/product_name").Trim();
                        return productName.Contains("RHEV", StringComparison.OrdinalIgnoreCase);
                    }
                }
                catch
                {
                    return false;
                }
            }

            return false;
        });
    }

    /// <summary>
    /// Gets the VM ID from oVirt/RHV.
    /// </summary>
    /// <returns>VM UUID or null if not available.</returns>
    private async Task<string?> GetOVirtVmIdAsync()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                // Try to get UUID from DMI
                var uuid = await File.ReadAllTextAsync("/sys/class/dmi/id/product_uuid");
                return uuid.Trim();
            }
            catch
            {
                // Try machine-id as fallback
                try
                {
                    var machineId = await File.ReadAllTextAsync("/etc/machine-id");
                    return machineId.Trim();
                }
                catch
                {
                    return null;
                }
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "wmic",
                    Arguments = "csproduct get uuid",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                if (process != null)
                {
                    var output = await process.StandardOutput.ReadToEndAsync();
                    process.WaitForExit();
                    var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length > 1)
                    {
                        return lines[1].Trim();
                    }
                }
            }
            catch
            {
                // WMIC query failed
            }
        }

        return Guid.NewGuid().ToString();
    }

    /// <summary>
    /// Gets the VM name from oVirt/RHV.
    /// </summary>
    /// <returns>VM name or null if not available.</returns>
    private async Task<string?> GetOVirtVmNameAsync()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                // Try to get VM name from oVirt guest agent data
                var ovirtDataPath = "/var/run/ovirt-guest-agent/vmname";
                if (File.Exists(ovirtDataPath))
                {
                    return (await File.ReadAllTextAsync(ovirtDataPath)).Trim();
                }

                // Try QEMU guest agent for VM name via product name
                var productName = await File.ReadAllTextAsync("/sys/class/dmi/id/product_name");
                var name = productName.Trim();

                // QEMU/KVM sets this to something like "KVM Virtual Machine"
                // or the actual VM name in oVirt
                if (!name.StartsWith("KVM Virtual Machine", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("Standard PC", StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }
            catch
            {
                // Fall through to hostname
            }
        }

        return Environment.MachineName;
    }

    /// <summary>
    /// Gets the total available memory.
    /// </summary>
    /// <returns>Total memory in bytes.</returns>
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
    /// <returns>Array of network interface names.</returns>
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
    /// Gets the list of fixed storage devices.
    /// </summary>
    /// <returns>Array of storage device names.</returns>
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
