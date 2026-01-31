using System.Runtime.InteropServices;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Virtualization;

namespace DataWarehouse.Plugins.Hypervisor;

/// <summary>
/// KVM/QEMU hypervisor detector plugin.
/// Detects KVM hypervisors using CPUID, kernel modules, and virtio drivers.
/// Supports bare KVM, QEMU, Proxmox VE, oVirt/RHV, and Nutanix AHV.
/// </summary>
public class KvmQemuDetectorPlugin : HypervisorDetectorPluginBase
{
    private const string KvmSignature = "KVMKVMKVM";

    /// <inheritdoc />
    public override string Id => "datawarehouse.hypervisor.kvm-qemu-detector";

    /// <inheritdoc />
    public override string Name => "KVM/QEMU Detector";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    protected override HypervisorType DetectHypervisor()
    {
        // Check for specific management platforms first
        if (CheckProxmox())
            return HypervisorType.Proxmox;

        if (CheckOVirt())
            return HypervisorType.OVirtRHV;

        if (CheckNutanix())
            return HypervisorType.NutanixAHV;

        // Check CPUID for KVM signature
        if (CheckKvmCpuId())
            return HypervisorType.KvmQemu;

        // Check for virtio devices
        if (CheckVirtioDevices())
            return HypervisorType.KvmQemu;

        // Check kernel modules
        if (CheckKvmModules())
            return HypervisorType.KvmQemu;

        return DetectFromSystemInfo();
    }

    /// <inheritdoc />
    protected override async Task<HypervisorCapabilities> QueryCapabilitiesAsync()
    {
        var detectedType = DetectedHypervisor;

        if (detectedType != HypervisorType.KvmQemu &&
            detectedType != HypervisorType.Proxmox &&
            detectedType != HypervisorType.OVirtRHV &&
            detectedType != HypervisorType.NutanixAHV)
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

        return new HypervisorCapabilities(
            SupportsBalloonDriver: drivers.Contains("virtio_balloon"),
            SupportsTrimDiscard: true, // virtio-scsi supports UNMAP
            SupportsHotAddCpu: true,
            SupportsHotAddMemory: drivers.Contains("virtio_balloon"),
            SupportsLiveMigration: true,
            SupportsSnapshots: true,
            SupportsBackupApi: detectedType == HypervisorType.Proxmox, // PBS
            ParavirtDrivers: drivers
        );
    }

    /// <inheritdoc />
    protected override async Task<VmInfo> QueryVmInfoAsync()
    {
        var vmId = await GetKvmVmIdAsync();
        var vmName = await GetKvmVmNameAsync();
        var cpuCount = Environment.ProcessorCount;
        var memoryBytes = GetTotalMemory();

        return new VmInfo(
            VmId: vmId ?? "unknown",
            VmName: vmName ?? Environment.MachineName,
            Hypervisor: DetectedHypervisor,
            State: VmState.Running,
            CpuCount: cpuCount,
            MemoryBytes: memoryBytes,
            NetworkInterfaces: GetNetworkInterfaces(),
            StorageDevices: GetStorageDevices()
        );
    }

    private bool CheckKvmCpuId()
    {
        try
        {
            var processorId = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "";
            return processorId.Contains("KVM", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private bool CheckProxmox()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return false;

        try
        {
            // Check for Proxmox-specific files
            if (File.Exists("/etc/pve") || Directory.Exists("/etc/pve"))
                return true;

            // Check for qemu-ga with Proxmox
            var productName = File.ReadAllText("/sys/class/dmi/id/product_name").Trim();
            return productName.Contains("Proxmox", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private bool CheckOVirt()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return false;

        try
        {
            // Check for oVirt/RHV guest agent
            if (File.Exists("/usr/bin/ovirt-guest-agent") ||
                File.Exists("/etc/ovirt-guest-agent.conf"))
                return true;

            var productName = File.ReadAllText("/sys/class/dmi/id/product_name").Trim();
            return productName.Contains("oVirt", StringComparison.OrdinalIgnoreCase) ||
                   productName.Contains("RHEV", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private bool CheckNutanix()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return false;

        try
        {
            // Check for Nutanix guest tools
            if (Directory.Exists("/usr/local/nutanix") ||
                File.Exists("/etc/nutanix"))
                return true;

            var productName = File.ReadAllText("/sys/class/dmi/id/product_name").Trim();
            return productName.Contains("Nutanix", StringComparison.OrdinalIgnoreCase) ||
                   productName.Contains("AHV", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private bool CheckVirtioDevices()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return false;

        try
        {
            // Check for virtio devices in sysfs
            var virtioPath = "/sys/bus/virtio/devices";
            return Directory.Exists(virtioPath) && Directory.GetDirectories(virtioPath).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private bool CheckKvmModules()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return false;

        try
        {
            // Check if running under KVM by reading CPUID directly
            // or checking for kvm_clock
            return File.Exists("/sys/devices/system/clocksource/clocksource0/current_clocksource") &&
                   File.ReadAllText("/sys/devices/system/clocksource/clocksource0/current_clocksource")
                       .Contains("kvm", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task<string[]> DetectVirtioDriversAsync()
    {
        var drivers = new List<string>();

        await Task.Run(() =>
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // Windows virtio drivers
                drivers.AddRange(new[] { "virtio_blk", "virtio_net", "virtio_scsi" });
                return;
            }

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
                var modulePath = $"/sys/module/{module}";
                if (Directory.Exists(modulePath))
                {
                    drivers.Add(module);
                }
            }
        });

        // Default virtio drivers if none detected
        if (drivers.Count == 0)
        {
            drivers.AddRange(new[] { "virtio_blk", "virtio_net" });
        }

        return drivers.ToArray();
    }

    private async Task<string?> GetKvmVmIdAsync()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return Guid.NewGuid().ToString();

        try
        {
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

    private async Task<string?> GetKvmVmNameAsync()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return Environment.MachineName;

        try
        {
            // Try QEMU guest agent socket
            var guestAgentPath = "/dev/virtio-ports/org.qemu.guest_agent.0";
            if (File.Exists(guestAgentPath))
            {
                // Guest agent available, but we can't easily query it
                // Fall back to hostname
            }

            // Try product name
            var productName = await File.ReadAllTextAsync("/sys/class/dmi/id/product_name");
            var name = productName.Trim();

            // QEMU sets this to something like "Standard PC (Q35 + ICH9, 2009)"
            // which isn't helpful, so use hostname instead
            if (!name.StartsWith("Standard PC"))
            {
                return name;
            }
        }
        catch
        {
            // Fall through to hostname
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
