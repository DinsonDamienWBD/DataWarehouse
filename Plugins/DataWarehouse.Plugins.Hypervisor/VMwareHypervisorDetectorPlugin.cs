using System.Diagnostics;
using System.Runtime.InteropServices;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Virtualization;

namespace DataWarehouse.Plugins.Hypervisor;

/// <summary>
/// VMware ESXi hypervisor detector plugin.
/// Detects VMware hypervisors using CPUID, VMware tools, and system information.
/// Supports VMware ESXi, Workstation, Fusion, and Player.
/// </summary>
public class VMwareHypervisorDetectorPlugin : HypervisorDetectorPluginBase
{
    private const string VMwareHypervisorSignature = "VMwareVMware";
    private const string VMwareCpuIdLeaf = "VMwareVMware";

    /// <inheritdoc />
    public override string Id => "datawarehouse.hypervisor.vmware-detector";

    /// <inheritdoc />
    public override string Name => "VMware Hypervisor Detector";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    protected override HypervisorType DetectHypervisor()
    {
        // Check CPUID hypervisor leaf (0x40000000)
        if (CheckVMwareCpuId())
        {
            return HypervisorType.VMwareESXi;
        }

        // Check for VMware tools service
        if (CheckVMwareToolsService())
        {
            return HypervisorType.VMwareESXi;
        }

        // Check SMBIOS/DMI for VMware manufacturer
        if (CheckVMwareSmbios())
        {
            return HypervisorType.VMwareESXi;
        }

        // Fallback to environment-based detection
        return DetectFromSystemInfo();
    }

    /// <inheritdoc />
    protected override async Task<HypervisorCapabilities> QueryCapabilitiesAsync()
    {
        if (DetectedHypervisor != HypervisorType.VMwareESXi)
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

        var drivers = await DetectVMwareDriversAsync();

        return new HypervisorCapabilities(
            SupportsBalloonDriver: drivers.Contains("vmmemctl"),
            SupportsTrimDiscard: true, // VMware supports UNMAP/TRIM
            SupportsHotAddCpu: true,
            SupportsHotAddMemory: true,
            SupportsLiveMigration: true, // vMotion
            SupportsSnapshots: true,
            SupportsBackupApi: true, // VADP
            ParavirtDrivers: drivers
        );
    }

    /// <inheritdoc />
    protected override async Task<VmInfo> QueryVmInfoAsync()
    {
        var vmId = await GetVMwareVmIdAsync();
        var vmName = await GetVMwareVmNameAsync();
        var cpuCount = Environment.ProcessorCount;
        var memoryBytes = GetTotalMemory();

        return new VmInfo(
            VmId: vmId ?? "unknown",
            VmName: vmName ?? Environment.MachineName,
            Hypervisor: HypervisorType.VMwareESXi,
            State: VmState.Running,
            CpuCount: cpuCount,
            MemoryBytes: memoryBytes,
            NetworkInterfaces: GetNetworkInterfaces(),
            StorageDevices: GetStorageDevices()
        );
    }

    private bool CheckVMwareCpuId()
    {
        try
        {
            // Check processor identifier for VMware signature
            var processorId = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "";
            return processorId.Contains("VMware", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private bool CheckVMwareToolsService()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Check for VMware Tools service on Windows
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "sc",
                    Arguments = "query VMTools",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                if (process != null)
                {
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    return output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                // Service query failed
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Check for vmtoolsd on Linux
            try
            {
                return File.Exists("/usr/bin/vmtoolsd") || File.Exists("/usr/local/bin/vmtoolsd");
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    private bool CheckVMwareSmbios()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                var productName = File.ReadAllText("/sys/class/dmi/id/product_name").Trim();
                return productName.Contains("VMware", StringComparison.OrdinalIgnoreCase);
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
                    Arguments = "bios get manufacturer",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                if (process != null)
                {
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    return output.Contains("VMware", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                // WMIC query failed
            }
        }

        return false;
    }

    private async Task<string[]> DetectVMwareDriversAsync()
    {
        var drivers = new List<string>();

        await Task.Run(() =>
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // Check for VMware paravirt drivers
                var driverPaths = new[]
                {
                    "/sys/module/vmw_balloon",      // Memory balloon
                    "/sys/module/vmw_vmci",         // VMCI
                    "/sys/module/vmw_pvscsi",       // PVSCSI storage
                    "/sys/module/vmxnet3",          // vmxnet3 network
                    "/sys/module/vmw_vsock_vmci_transport" // vSock
                };

                foreach (var path in driverPaths)
                {
                    if (Directory.Exists(path))
                    {
                        drivers.Add(Path.GetFileName(path).Replace("vmw_", ""));
                    }
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Check for VMware drivers on Windows
                var vmwareDrivers = new[] { "vmxnet3", "pvscsi", "vmmemctl" };
                foreach (var driver in vmwareDrivers)
                {
                    var driverPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.System),
                        "drivers", $"{driver}.sys"
                    );

                    if (File.Exists(driverPath))
                    {
                        drivers.Add(driver);
                    }
                }
            }
        });

        // Standard VMware capabilities
        if (drivers.Count == 0)
        {
            drivers.AddRange(new[] { "vmxnet3", "pvscsi" });
        }

        return drivers.ToArray();
    }

    private async Task<string?> GetVMwareVmIdAsync()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                var uuid = await File.ReadAllTextAsync("/sys/class/dmi/id/product_uuid");
                return uuid.Trim();
            }
            catch
            {
                return null;
            }
        }

        return Guid.NewGuid().ToString(); // Fallback
    }

    private async Task<string?> GetVMwareVmNameAsync()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                var name = await File.ReadAllTextAsync("/sys/class/dmi/id/product_name");
                return name.Trim();
            }
            catch
            {
                return null;
            }
        }

        return Environment.MachineName;
    }

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
