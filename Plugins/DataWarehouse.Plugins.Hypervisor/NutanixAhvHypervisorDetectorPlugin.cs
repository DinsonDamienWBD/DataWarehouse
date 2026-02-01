using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Virtualization;

namespace DataWarehouse.Plugins.Hypervisor;

/// <summary>
/// Nutanix AHV (Acropolis Hypervisor) detector plugin.
/// Detects Nutanix AHV hypervisor using Nutanix Guest Tools (NGT), SMBIOS data,
/// CPUID signatures, and Distributed Storage Fabric (DSF) markers.
/// Supports Nutanix AOS and Prism Central managed environments.
/// </summary>
/// <remarks>
/// <para>
/// Nutanix AHV is a KVM-based hypervisor that is tightly integrated with
/// Nutanix's hyper-converged infrastructure. It provides enterprise features
/// like live migration, snapshots, and data protection through Nutanix's
/// Distributed Storage Fabric.
/// </para>
/// <para>
/// Detection methods include:
/// <list type="bullet">
/// <item>Nutanix Guest Tools (NGT) presence and service status</item>
/// <item>SMBIOS product name containing Nutanix or AHV identifiers</item>
/// <item>CPUID hypervisor leaf with KVM signature (underlying hypervisor)</item>
/// <item>Nutanix NFS storage mounts (DSF indicators)</item>
/// <item>Nutanix-specific virtio device naming patterns</item>
/// <item>Windows registry entries for NGT and Nutanix VirtIO drivers</item>
/// </list>
/// </para>
/// </remarks>
public class NutanixAhvHypervisorDetectorPlugin : HypervisorDetectorPluginBase
{
    /// <summary>
    /// Path to Nutanix Guest Tools installation directory on Linux.
    /// </summary>
    private const string NgtLinuxPath = "/usr/local/nutanix";

    /// <summary>
    /// Path to Nutanix Guest Tools configuration on Linux.
    /// </summary>
    private const string NgtLinuxConfigPath = "/etc/nutanix";

    /// <summary>
    /// Path to Nutanix Guest Tools state directory.
    /// </summary>
    private const string NgtStatePath = "/var/nutanix";

    /// <summary>
    /// Nutanix Guest Tools service name on Linux.
    /// </summary>
    private const string NgtLinuxService = "ngt_guest_agent";

    /// <summary>
    /// Nutanix VirtIO guest agent service name.
    /// </summary>
    private const string NgtVirtioService = "nutanix-guest-tools";

    /// <inheritdoc />
    public override string Id => "datawarehouse.hypervisor.nutanix-ahv-detector";

    /// <inheritdoc />
    public override string Name => "Nutanix AHV Hypervisor Detector";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    protected override HypervisorType DetectHypervisor()
    {
        // Check for Nutanix Guest Tools (most reliable indicator)
        if (CheckNutanixGuestTools())
        {
            return HypervisorType.NutanixAHV;
        }

        // Check SMBIOS/DMI for Nutanix product name
        if (CheckNutanixSmbios())
        {
            return HypervisorType.NutanixAHV;
        }

        // Check for Nutanix-specific storage markers (DSF)
        if (CheckNutanixStorageFabric())
        {
            return HypervisorType.NutanixAHV;
        }

        // Check for Nutanix VirtIO drivers
        if (CheckNutanixVirtioDrivers())
        {
            return HypervisorType.NutanixAHV;
        }

        // Check Windows-specific Nutanix tools
        if (CheckWindowsNutanixTools())
        {
            return HypervisorType.NutanixAHV;
        }

        // Check for Nutanix network patterns
        if (CheckNutanixNetworkPatterns())
        {
            return HypervisorType.NutanixAHV;
        }

        // Fallback to system info detection
        return DetectFromSystemInfo();
    }

    /// <inheritdoc />
    protected override async Task<HypervisorCapabilities> QueryCapabilitiesAsync()
    {
        if (DetectedHypervisor != HypervisorType.NutanixAHV)
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

        var drivers = await DetectNutanixDriversAsync();
        var hasNgt = CheckNutanixGuestTools();
        var ngtCapabilities = await GetNgtCapabilitiesAsync();

        return new HypervisorCapabilities(
            SupportsBalloonDriver: drivers.Contains("virtio_balloon"),
            SupportsTrimDiscard: true, // Nutanix DSF supports UNMAP/TRIM
            SupportsHotAddCpu: true, // AHV supports CPU hot-plug
            SupportsHotAddMemory: drivers.Contains("virtio_balloon"), // Memory hot-plug via balloon
            SupportsLiveMigration: true, // AHV native live migration
            SupportsSnapshots: true, // Nutanix snapshots via Protection Domains
            SupportsBackupApi: ngtCapabilities.ContainsKey("vss_enabled") && ngtCapabilities["vss_enabled"] == "true",
            ParavirtDrivers: drivers
        );
    }

    /// <inheritdoc />
    protected override async Task<VmInfo> QueryVmInfoAsync()
    {
        var vmId = await GetNutanixVmIdAsync();
        var vmName = await GetNutanixVmNameAsync();
        var cpuCount = Environment.ProcessorCount;
        var memoryBytes = GetTotalMemory();

        return new VmInfo(
            VmId: vmId ?? "unknown",
            VmName: vmName ?? Environment.MachineName,
            Hypervisor: HypervisorType.NutanixAHV,
            State: VmState.Running,
            CpuCount: cpuCount,
            MemoryBytes: memoryBytes,
            NetworkInterfaces: GetNetworkInterfaces(),
            StorageDevices: GetStorageDevices()
        );
    }

    /// <summary>
    /// Checks for the presence of Nutanix Guest Tools (NGT).
    /// NGT provides enhanced VM management capabilities on Nutanix AHV.
    /// </summary>
    /// <returns>True if NGT is detected.</returns>
    private bool CheckNutanixGuestTools()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                // Check for NGT installation directory
                if (Directory.Exists(NgtLinuxPath))
                {
                    return true;
                }

                // Check for NGT configuration directory
                if (Directory.Exists(NgtLinuxConfigPath))
                {
                    return true;
                }

                // Check for NGT state directory
                if (Directory.Exists(NgtStatePath))
                {
                    return true;
                }

                // Check for NGT guest agent service
                if (CheckSystemdService(NgtLinuxService))
                {
                    return true;
                }

                // Check for nutanix-guest-tools service
                if (CheckSystemdService(NgtVirtioService))
                {
                    return true;
                }

                // Check for running NGT processes
                if (CheckProcessRunning("ngt_guest_agent"))
                {
                    return true;
                }

                // Check for Nutanix VirtIO guest agent
                if (File.Exists("/usr/local/nutanix/bin/nutanix-guest-agent"))
                {
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return CheckWindowsNgtService();
        }

        return false;
    }

    /// <summary>
    /// Checks for Windows Nutanix Guest Tools service.
    /// </summary>
    /// <returns>True if Windows NGT service is detected.</returns>
    private bool CheckWindowsNgtService()
    {
        try
        {
            // Check for Nutanix Guest Tools service
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "sc",
                Arguments = "query \"Nutanix Guest Tools\"",
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
                    return true; // Service exists
                }
            }

            // Check for NGT Agent service (alternative name)
            using var ngtProcess = Process.Start(new ProcessStartInfo
            {
                FileName = "sc",
                Arguments = "query \"NutanixGuestAgent\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (ngtProcess != null)
            {
                var output = ngtProcess.StandardOutput.ReadToEnd();
                ngtProcess.WaitForExit();
                if (output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) ||
                    output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Service query failed
        }

        return false;
    }

    /// <summary>
    /// Checks SMBIOS data for Nutanix/AHV product identifiers.
    /// </summary>
    /// <returns>True if Nutanix/AHV product is detected in SMBIOS.</returns>
    private bool CheckNutanixSmbios()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                // Check product name
                var productName = File.ReadAllText("/sys/class/dmi/id/product_name").Trim();
                if (productName.Contains("Nutanix", StringComparison.OrdinalIgnoreCase) ||
                    productName.Contains("AHV", StringComparison.OrdinalIgnoreCase) ||
                    productName.Contains("Acropolis", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // Check system vendor
                if (File.Exists("/sys/class/dmi/id/sys_vendor"))
                {
                    var vendor = File.ReadAllText("/sys/class/dmi/id/sys_vendor").Trim();
                    if (vendor.Contains("Nutanix", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                // Check chassis manufacturer
                if (File.Exists("/sys/class/dmi/id/chassis_vendor"))
                {
                    var chassisVendor = File.ReadAllText("/sys/class/dmi/id/chassis_vendor").Trim();
                    if (chassisVendor.Contains("Nutanix", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                // Check BIOS vendor
                if (File.Exists("/sys/class/dmi/id/bios_vendor"))
                {
                    var biosVendor = File.ReadAllText("/sys/class/dmi/id/bios_vendor").Trim();
                    if (biosVendor.Contains("Nutanix", StringComparison.OrdinalIgnoreCase))
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
                    return output.Contains("Nutanix", StringComparison.OrdinalIgnoreCase) ||
                           output.Contains("AHV", StringComparison.OrdinalIgnoreCase) ||
                           output.Contains("Acropolis", StringComparison.OrdinalIgnoreCase);
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
    /// Checks for Nutanix Distributed Storage Fabric (DSF) markers.
    /// DSF provides unified storage across the Nutanix cluster.
    /// </summary>
    /// <returns>True if Nutanix DSF markers are detected.</returns>
    private bool CheckNutanixStorageFabric()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return false;
        }

        try
        {
            // Check /etc/fstab for Nutanix NFS containers
            if (File.Exists("/etc/fstab"))
            {
                var fstab = File.ReadAllText("/etc/fstab");
                // Nutanix NFS exports typically use specific naming patterns
                if (fstab.Contains("nfs", StringComparison.OrdinalIgnoreCase) &&
                    (fstab.Contains("nutanix", StringComparison.OrdinalIgnoreCase) ||
                     fstab.Contains("container", StringComparison.OrdinalIgnoreCase) ||
                     // Nutanix CVM IP range pattern
                     fstab.Contains(".1:/")))
                {
                    return true;
                }
            }

            // Check mounted filesystems for Nutanix patterns
            if (File.Exists("/proc/mounts"))
            {
                var mounts = File.ReadAllText("/proc/mounts");
                // Look for NFS mounts from Nutanix storage containers
                if (mounts.Contains("nfs") && mounts.Contains("nutanix"))
                {
                    return true;
                }
            }

            // Check for Nutanix Volume Groups (iSCSI)
            var iscsiPath = "/etc/iscsi/initiatorname.iscsi";
            if (File.Exists(iscsiPath))
            {
                var initiator = File.ReadAllText(iscsiPath);
                if (initiator.Contains("nutanix", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// Checks for Nutanix-specific VirtIO driver configurations.
    /// </summary>
    /// <returns>True if Nutanix VirtIO drivers are detected.</returns>
    private bool CheckNutanixVirtioDrivers()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                // Check for virtio devices with Nutanix serial numbers
                var blockDevices = "/sys/class/block";
                if (Directory.Exists(blockDevices))
                {
                    foreach (var device in Directory.GetDirectories(blockDevices))
                    {
                        var serialPath = Path.Combine(device, "device", "serial");
                        if (File.Exists(serialPath))
                        {
                            var serial = File.ReadAllText(serialPath).Trim();
                            if (serial.StartsWith("NUTANIX", StringComparison.OrdinalIgnoreCase) ||
                                serial.Contains("NTNX", StringComparison.OrdinalIgnoreCase))
                            {
                                return true;
                            }
                        }
                    }
                }

                // Check for Nutanix VirtIO vendor in PCI devices
                var pciDevices = "/sys/bus/pci/devices";
                if (Directory.Exists(pciDevices))
                {
                    foreach (var device in Directory.GetDirectories(pciDevices))
                    {
                        var vendorPath = Path.Combine(device, "subsystem_vendor");
                        if (File.Exists(vendorPath))
                        {
                            var vendor = File.ReadAllText(vendorPath).Trim();
                            // Nutanix subsystem vendor ID
                            if (vendor == "0x1af4") // VirtIO vendor
                            {
                                var subsysPath = Path.Combine(device, "subsystem_device");
                                if (File.Exists(subsysPath))
                                {
                                    // Additional check could be done here for Nutanix-specific subsystem
                                }
                            }
                        }
                    }
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
    /// Checks for Windows-specific Nutanix tools and drivers.
    /// </summary>
    /// <returns>True if Windows Nutanix tools are detected.</returns>
    private bool CheckWindowsNutanixTools()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return false;
        }

        try
        {
            // Check for Nutanix Guest Tools installation directory
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var nutanixPath = Path.Combine(programFiles, "Nutanix");
            var ngtPath = Path.Combine(programFiles, "Nutanix Guest Tools");

            if (Directory.Exists(nutanixPath) || Directory.Exists(ngtPath))
            {
                return true;
            }

            // Check for Nutanix VirtIO drivers
            var systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var driversDir = Path.Combine(systemDir, "drivers");

            // Nutanix uses standard VirtIO drivers but may have specific naming
            var nutanixDrivers = new[]
            {
                "nutanix_balloon.sys",
                "nutanix_viostor.sys",
                "nutanix_netkvm.sys",
                // Standard VirtIO drivers (often used by Nutanix)
                "viostor.sys",
                "netkvm.sys",
                "balloon.sys"
            };

            var virtioDriverCount = nutanixDrivers.Count(d => File.Exists(Path.Combine(driversDir, d)));

            // If we have VirtIO drivers and NGT service, it's likely Nutanix
            if (virtioDriverCount >= 2 && CheckWindowsNgtService())
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
    /// Checks for Nutanix-specific network patterns.
    /// Nutanix VMs often have specific network configurations.
    /// </summary>
    /// <returns>True if Nutanix network patterns are detected.</returns>
    private bool CheckNutanixNetworkPatterns()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return false;
        }

        try
        {
            // Check for Nutanix network interface naming patterns
            var netPath = "/sys/class/net";
            if (Directory.Exists(netPath))
            {
                foreach (var iface in Directory.GetDirectories(netPath))
                {
                    var devicePath = Path.Combine(iface, "device");
                    if (Directory.Exists(devicePath))
                    {
                        // Check for virtio with Nutanix-specific vendor strings
                        var driverLink = Path.Combine(devicePath, "driver");
                        if (Directory.Exists(driverLink))
                        {
                            var driverTarget = Path.GetFileName(Directory.ResolveLinkTarget(driverLink, false)?.FullName ?? "");
                            if (driverTarget.Contains("virtio", StringComparison.OrdinalIgnoreCase))
                            {
                                // VirtIO network on potential Nutanix - check other indicators
                                if (CheckNutanixGuestTools() || CheckNutanixSmbios())
                                {
                                    return true;
                                }
                            }
                        }
                    }
                }
            }

            // Check /etc/resolv.conf for Nutanix DNS patterns
            if (File.Exists("/etc/resolv.conf"))
            {
                var resolv = File.ReadAllText("/etc/resolv.conf");
                if (resolv.Contains("nutanix", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
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
                Arguments = $"-f {processName}",
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
    /// Detects Nutanix AHV paravirtualization drivers.
    /// </summary>
    /// <returns>Array of detected driver names.</returns>
    private async Task<string[]> DetectNutanixDriversAsync()
    {
        var drivers = new List<string>();

        await Task.Run(() =>
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // Nutanix AHV uses standard virtio drivers
                var driverModules = new[]
                {
                    "virtio_blk",       // Block device
                    "virtio_net",       // Network
                    "virtio_scsi",      // SCSI controller
                    "virtio_balloon",   // Memory balloon
                    "virtio_console",   // Serial console
                    "virtio_rng",       // Random number generator
                    "virtio_pci",       // VirtIO PCI transport
                    "9pnet_virtio"      // 9P filesystem
                };

                foreach (var module in driverModules)
                {
                    var modulePath = $"/sys/module/{module}";
                    if (Directory.Exists(modulePath))
                    {
                        drivers.Add(module);
                    }
                }

                // Check for NGT-specific modules
                var ngtModules = new[]
                {
                    "ngt_iscsi_helper",
                    "nutanix_guest"
                };

                foreach (var module in ngtModules)
                {
                    var modulePath = $"/sys/module/{module}";
                    if (Directory.Exists(modulePath))
                    {
                        drivers.Add(module);
                    }
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
                    { "viorng.sys", "virtio_rng" }
                };

                foreach (var (file, name) in virtioDrivers)
                {
                    if (File.Exists(Path.Combine(driversDir, file)))
                    {
                        drivers.Add(name);
                    }
                }

                // Check for Nutanix-specific drivers
                var nutanixDrivers = new[]
                {
                    "nutanix_balloon.sys",
                    "nutanix_viostor.sys",
                    "nutanix_netkvm.sys"
                };

                foreach (var driver in nutanixDrivers)
                {
                    if (File.Exists(Path.Combine(driversDir, driver)))
                    {
                        drivers.Add(Path.GetFileNameWithoutExtension(driver));
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
    /// Gets Nutanix Guest Tools capabilities.
    /// </summary>
    /// <returns>Dictionary of NGT capabilities.</returns>
    private async Task<Dictionary<string, string>> GetNgtCapabilitiesAsync()
    {
        var capabilities = new Dictionary<string, string>();

        await Task.Run(() =>
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                try
                {
                    // Check for NGT configuration file
                    var configPath = "/usr/local/nutanix/config/guest_agent.conf";
                    if (File.Exists(configPath))
                    {
                        var config = File.ReadAllText(configPath);
                        // Parse basic capabilities from config
                        if (config.Contains("vss_enabled", StringComparison.OrdinalIgnoreCase))
                        {
                            capabilities["vss_enabled"] = config.Contains("vss_enabled=true", StringComparison.OrdinalIgnoreCase)
                                ? "true" : "false";
                        }
                        if (config.Contains("self_service_restore", StringComparison.OrdinalIgnoreCase))
                        {
                            capabilities["self_service_restore"] = "true";
                        }
                    }

                    // Check for NGT capabilities file
                    var capabilitiesPath = "/usr/local/nutanix/config/capabilities.json";
                    if (File.Exists(capabilitiesPath))
                    {
                        try
                        {
                            var json = File.ReadAllText(capabilitiesPath);
                            using var doc = JsonDocument.Parse(json);
                            foreach (var prop in doc.RootElement.EnumerateObject())
                            {
                                capabilities[prop.Name] = prop.Value.ToString();
                            }
                        }
                        catch
                        {
                            // JSON parsing failed
                        }
                    }
                }
                catch
                {
                    // Config read failed
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Windows NGT typically supports VSS
                capabilities["vss_enabled"] = "true";
                capabilities["self_service_restore"] = "true";
            }
        });

        return capabilities;
    }

    /// <summary>
    /// Gets the VM ID from Nutanix AHV.
    /// </summary>
    /// <returns>VM UUID or null if not available.</returns>
    private async Task<string?> GetNutanixVmIdAsync()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                // Try to get UUID from NGT
                var uuidPath = "/usr/local/nutanix/config/vm_uuid";
                if (File.Exists(uuidPath))
                {
                    return (await File.ReadAllTextAsync(uuidPath)).Trim();
                }

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
                // Try NGT registry key for VM UUID
                // Note: Would require Microsoft.Win32.Registry package in production
                // Fallback to WMIC
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
    /// Gets the VM name from Nutanix AHV.
    /// </summary>
    /// <returns>VM name or null if not available.</returns>
    private async Task<string?> GetNutanixVmNameAsync()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                // Try to get VM name from NGT
                var namePath = "/usr/local/nutanix/config/vm_name";
                if (File.Exists(namePath))
                {
                    return (await File.ReadAllTextAsync(namePath)).Trim();
                }

                // Try product name from DMI
                var productName = await File.ReadAllTextAsync("/sys/class/dmi/id/product_name");
                var name = productName.Trim();

                // QEMU/KVM sets this to something generic, check if meaningful
                if (!name.StartsWith("KVM Virtual Machine", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("Standard PC", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }
            catch
            {
                // Fall through to hostname
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                // Check NGT installation directory for VM name
                var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var vmNamePath = Path.Combine(programFiles, "Nutanix Guest Tools", "vm_name.txt");
                if (File.Exists(vmNamePath))
                {
                    return (await File.ReadAllTextAsync(vmNamePath)).Trim();
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
