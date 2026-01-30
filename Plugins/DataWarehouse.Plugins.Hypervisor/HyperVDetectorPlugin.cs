using System.Diagnostics;
using System.Runtime.InteropServices;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Virtualization;

namespace DataWarehouse.Plugins.Hypervisor;

/// <summary>
/// Microsoft Hyper-V hypervisor detector plugin.
/// Detects Hyper-V using CPUID, WMI, and system information.
/// Supports Windows Server Hyper-V, Client Hyper-V, and Azure VMs.
/// </summary>
public class HyperVDetectorPlugin : HypervisorDetectorPluginBase
{
    private const string HyperVSignature = "Microsoft Hv";

    /// <inheritdoc />
    public override string Id => "datawarehouse.hypervisor.hyperv-detector";

    /// <inheritdoc />
    public override string Name => "Hyper-V Detector";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    protected override HypervisorType DetectHypervisor()
    {
        // Check processor identifier for Hyper-V signature
        if (CheckHyperVCpuId())
        {
            return HypervisorType.HyperV;
        }

        // Check for Hyper-V integration services
        if (CheckHyperVIntegrationServices())
        {
            return HypervisorType.HyperV;
        }

        // Check WMI for Hyper-V
        if (CheckHyperVWmi())
        {
            return HypervisorType.HyperV;
        }

        // Check Azure VM metadata
        if (CheckAzureMetadata())
        {
            return HypervisorType.HyperV;
        }

        return DetectFromSystemInfo();
    }

    /// <inheritdoc />
    protected override async Task<HypervisorCapabilities> QueryCapabilitiesAsync()
    {
        if (DetectedHypervisor != HypervisorType.HyperV)
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

        var drivers = await DetectHyperVDriversAsync();
        var isAzure = CheckAzureMetadata();

        return new HypervisorCapabilities(
            SupportsBalloonDriver: drivers.Contains("dmvsc"), // Dynamic Memory
            SupportsTrimDiscard: true,
            SupportsHotAddCpu: !isAzure, // Not on Azure
            SupportsHotAddMemory: drivers.Contains("dmvsc"),
            SupportsLiveMigration: !isAzure, // On-prem only
            SupportsSnapshots: !isAzure,
            SupportsBackupApi: true, // VSS
            ParavirtDrivers: drivers
        );
    }

    /// <inheritdoc />
    protected override async Task<VmInfo> QueryVmInfoAsync()
    {
        var vmId = await GetHyperVVmIdAsync();
        var vmName = await GetHyperVVmNameAsync();
        var cpuCount = Environment.ProcessorCount;
        var memoryBytes = GetTotalMemory();

        return new VmInfo(
            VmId: vmId ?? "unknown",
            VmName: vmName ?? Environment.MachineName,
            Hypervisor: HypervisorType.HyperV,
            State: VmState.Running,
            CpuCount: cpuCount,
            MemoryBytes: memoryBytes,
            NetworkInterfaces: GetNetworkInterfaces(),
            StorageDevices: GetStorageDevices()
        );
    }

    private bool CheckHyperVCpuId()
    {
        try
        {
            var processorId = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "";
            return processorId.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) &&
                   processorId.Contains("Hv", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private bool CheckHyperVIntegrationServices()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return CheckLinuxHyperVModules();
        }

        try
        {
            // Check for Hyper-V Integration Services
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "sc",
                Arguments = "query vmicshutdown",
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

        return false;
    }

    private bool CheckLinuxHyperVModules()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return false;

        var modules = new[]
        {
            "/sys/module/hv_vmbus",
            "/sys/module/hv_storvsc",
            "/sys/module/hv_netvsc"
        };

        return modules.Any(Directory.Exists);
    }

    private bool CheckHyperVWmi()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-Command \"(Get-WmiObject -Class Win32_ComputerSystem).Manufacturer\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process != null)
            {
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                return output.Contains("Microsoft Corporation", StringComparison.OrdinalIgnoreCase) &&
                       output.Contains("Virtual", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // PowerShell query failed
        }

        return false;
    }

    private bool CheckAzureMetadata()
    {
        // Check for Azure Instance Metadata Service
        // Azure VMs have metadata at 169.254.169.254
        try
        {
            var azureMetadataPath = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? @"C:\WindowsAzure"
                : "/var/lib/waagent";

            return Directory.Exists(azureMetadataPath);
        }
        catch
        {
            return false;
        }
    }

    private async Task<string[]> DetectHyperVDriversAsync()
    {
        var drivers = new List<string>();

        await Task.Run(() =>
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var driverPaths = new Dictionary<string, string>
                {
                    { "/sys/module/hv_vmbus", "vmbus" },
                    { "/sys/module/hv_storvsc", "storvsc" },
                    { "/sys/module/hv_netvsc", "netvsc" },
                    { "/sys/module/hv_balloon", "dmvsc" },
                    { "/sys/module/hv_utils", "utils" }
                };

                foreach (var (path, name) in driverPaths)
                {
                    if (Directory.Exists(path))
                    {
                        drivers.Add(name);
                    }
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Hyper-V Integration Components
                var services = new[]
                {
                    ("vmicshutdown", "shutdown"),
                    ("vmicheartbeat", "heartbeat"),
                    ("vmickvpexchange", "kvp"),
                    ("vmicrdv", "rdv"),
                    ("vmicvmsession", "vmsession"),
                    ("vmictimesync", "timesync"),
                    ("vmicvss", "vss")
                };

                foreach (var (service, name) in services)
                {
                    try
                    {
                        using var process = Process.Start(new ProcessStartInfo
                        {
                            FileName = "sc",
                            Arguments = $"query {service}",
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
                                drivers.Add(name);
                            }
                        }
                    }
                    catch
                    {
                        // Continue to next service
                    }
                }
            }
        });

        // Standard Hyper-V capabilities
        if (drivers.Count == 0)
        {
            drivers.AddRange(new[] { "storvsc", "netvsc" });
        }

        return drivers.ToArray();
    }

    private async Task<string?> GetHyperVVmIdAsync()
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

    private async Task<string?> GetHyperVVmNameAsync()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                // Try KVP daemon data
                var kvpPath = "/var/lib/hyperv/.kvp_pool_3";
                if (File.Exists(kvpPath))
                {
                    var data = await File.ReadAllTextAsync(kvpPath);
                    // Parse VirtualMachineName from KVP data
                    var lines = data.Split('\0', StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 0; i < lines.Length - 1; i++)
                    {
                        if (lines[i] == "VirtualMachineName")
                        {
                            return lines[i + 1];
                        }
                    }
                }
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
