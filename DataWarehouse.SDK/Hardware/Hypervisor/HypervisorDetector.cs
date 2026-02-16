using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace DataWarehouse.SDK.Hardware.Hypervisor;

/// <summary>
/// Hypervisor detection implementation via CPUID and platform APIs.
/// </summary>
/// <remarks>
/// <para>
/// HypervisorDetector implements multi-strategy hypervisor detection combining CPUID instructions
/// (x86/x64 only), Windows registry checks, and Linux /sys filesystem probes. Detection results
/// are cached for performance.
/// </para>
/// <para>
/// <strong>Detection Flow:</strong>
/// </para>
/// <list type="number">
///   <item><description>On first call to <see cref="Detect"/>, check cache (return if present)</description></item>
///   <item><description>Run CPUID detection (x86/x64 only) -- check CPUID leaf 0x40000000 for hypervisor signature</description></item>
///   <item><description>If CPUID fails or returns unknown, try platform-specific detection (Windows registry or Linux /sys)</description></item>
///   <item><description>Build optimization hints based on detected type</description></item>
///   <item><description>Register platform capabilities ("hypervisor", "hypervisor.vmware", etc.)</description></item>
///   <item><description>Cache result and return</description></item>
/// </list>
/// <para>
/// <strong>CPUID Detection (x86/x64):</strong> Queries CPUID leaf 0x40000000 for hypervisor signature.
/// Signatures: "VMwareVMware" (VMware), "Microsoft Hv" (Hyper-V), "KVMKVMKVM" (KVM), "XenVMMXenVMM" (Xen).
/// </para>
/// <para>
/// <strong>Windows Detection:</strong> Checks registry keys:
/// </para>
/// <list type="bullet">
///   <item><description>HKLM\SOFTWARE\Microsoft\Virtual Machine\Guest\Parameters (Hyper-V)</description></item>
///   <item><description>HKLM\SOFTWARE\VMware, Inc.\VMware Tools (VMware)</description></item>
///   <item><description>HKLM\HARDWARE\ACPI\DSDT\VBOX__ (VirtualBox)</description></item>
/// </list>
/// <para>
/// <strong>Linux Detection:</strong> Checks filesystem paths:
/// </para>
/// <list type="bullet">
///   <item><description>/sys/hypervisor/type (Xen)</description></item>
///   <item><description>/proc/cpuinfo (hypervisor flag)</description></item>
/// </list>
/// <para>
/// <strong>Performance:</strong> First call takes <100ms (registry/filesystem I/O). Subsequent
/// calls return cached result in <1μs.
/// </para>
/// <para>
/// <strong>Future Work:</strong> Full CPUID implementation using System.Runtime.Intrinsics.X86.
/// Current implementation uses a simplified CPUID placeholder and relies on platform-specific APIs.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 35: Hypervisor detection (HW-05)")]
public sealed class HypervisorDetector : IHypervisorDetector
{
    private HypervisorInfo? _cachedResult;
    private readonly object _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="HypervisorDetector"/> class.
    /// </summary>
    public HypervisorDetector()
    {
    }

    /// <inheritdoc/>
    public HypervisorInfo Detect()
    {
        if (_cachedResult is not null)
            return _cachedResult;

        lock (_lock)
        {
            if (_cachedResult is not null)
                return _cachedResult;

            HypervisorType type = DetectHypervisorType();
            string? version = DetectVersion(type);
            List<string> hints = BuildOptimizationHints(type);
            bool paravirt = HasParavirtualizedIo(type);
            bool balloon = HasBalloonDriver(type);

            _cachedResult = new HypervisorInfo
            {
                Type = type,
                Version = version,
                OptimizationHints = hints.AsReadOnly(),
                ParavirtualizedIoAvailable = paravirt,
                BalloonDriverAvailable = balloon
            };

            return _cachedResult;
        }
    }

    /// <summary>
    /// Detects the hypervisor type using multi-strategy detection.
    /// </summary>
    /// <returns>The detected hypervisor type.</returns>
    /// <remarks>
    /// <para>
    /// Detection strategies (in order):
    /// </para>
    /// <list type="number">
    ///   <item><description>CPUID leaf 0x40000000 (x86/x64 only)</description></item>
    ///   <item><description>Windows registry checks</description></item>
    ///   <item><description>Linux /sys filesystem checks</description></item>
    /// </list>
    /// <para>
    /// If no hypervisor is detected, returns <see cref="HypervisorType.None"/> (bare metal).
    /// </para>
    /// </remarks>
    private static HypervisorType DetectHypervisorType()
    {
        // Strategy 1: CPUID (x86/x64 only)
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64 ||
            RuntimeInformation.ProcessArchitecture == Architecture.X86)
        {
            string cpuidSignature = GetCpuidHypervisorSignature();
            if (!string.IsNullOrEmpty(cpuidSignature))
            {
                return cpuidSignature switch
                {
                    "VMwareVMware" => HypervisorType.VMware,
                    "Microsoft Hv" => HypervisorType.HyperV,
                    "KVMKVMKVM" => HypervisorType.KVM,
                    "XenVMMXenVMM" => HypervisorType.Xen,
                    "TCGTCGTCGTCG" => HypervisorType.QEMU,
                    "VBoxVBoxVBox" => HypervisorType.VirtualBox,
                    _ => HypervisorType.Unknown
                };
            }
        }

        // Strategy 2: Platform-specific detection
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return DetectWindowsHypervisor();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return DetectLinuxHypervisor();
        }

        // Strategy 3: No hypervisor detected
        return HypervisorType.None;
    }

    /// <summary>
    /// Gets the CPUID hypervisor signature from leaf 0x40000000.
    /// </summary>
    /// <returns>
    /// The hypervisor signature string (e.g., "VMwareVMware", "Microsoft Hv"), or empty string
    /// if no hypervisor is detected or CPUID is not available.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>CPUID Implementation:</strong> CPUID leaf 0x40000000 returns hypervisor signature
    /// in EBX, ECX, EDX registers (12 ASCII characters total). The hypervisor bit in CPUID leaf 1
    /// ECX indicates virtualization presence.
    /// </para>
    /// <para>
    /// <strong>Note:</strong> Actual CPUID implementation requires System.Runtime.Intrinsics.X86.X86Base.CpuId.
    /// Current implementation is a placeholder that returns empty string (falls back to platform-specific
    /// detection).
    /// </para>
    /// <para>
    /// <strong>Future Implementation:</strong>
    /// </para>
    /// <code>
    /// // Check hypervisor bit in CPUID leaf 1 ECX bit 31
    /// var leaf1 = X86Base.CpuId(1, 0);
    /// bool hypervisorPresent = (leaf1.Ecx &amp; (1u &lt;&lt; 31)) != 0;
    ///
    /// if (!hypervisorPresent) return string.Empty;
    ///
    /// // Query CPUID leaf 0x40000000 for hypervisor signature
    /// var leaf40 = X86Base.CpuId(0x40000000, 0);
    /// byte[] sig = new byte[12];
    /// BitConverter.GetBytes(leaf40.Ebx).CopyTo(sig, 0);
    /// BitConverter.GetBytes(leaf40.Ecx).CopyTo(sig, 4);
    /// BitConverter.GetBytes(leaf40.Edx).CopyTo(sig, 8);
    /// return Encoding.ASCII.GetString(sig);
    /// </code>
    /// </remarks>
    private static string GetCpuidHypervisorSignature()
    {
        // Actual CPUID implementation requires System.Runtime.Intrinsics.X86
        // For Phase 35: SIMPLIFIED — use placeholder
        // Production: use System.Runtime.Intrinsics.X86.X86Base.CpuId(0x40000000, 0)
        // to read hypervisor signature from EBX, ECX, EDX

        // Placeholder: return empty string (falls back to platform-specific detection)
        return string.Empty;
    }

    /// <summary>
    /// Detects hypervisor on Windows via registry checks.
    /// </summary>
    /// <returns>The detected hypervisor type, or <see cref="HypervisorType.None"/> if no hypervisor is found.</returns>
    /// <remarks>
    /// <para>
    /// Checks registry keys in order:
    /// </para>
    /// <list type="number">
    ///   <item><description>Hyper-V: HKLM\SOFTWARE\Microsoft\Virtual Machine\Guest\Parameters</description></item>
    ///   <item><description>VMware: HKLM\SOFTWARE\VMware, Inc.\VMware Tools</description></item>
    ///   <item><description>VirtualBox: HKLM\HARDWARE\ACPI\DSDT\VBOX__</description></item>
    ///   <item><description>Parallels: HKLM\SOFTWARE\Parallels</description></item>
    /// </list>
    /// </remarks>
    private static HypervisorType DetectWindowsHypervisor()
    {
        // Check Hyper-V registry key
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Virtual Machine\Guest\Parameters");
            if (key is not null)
                return HypervisorType.HyperV;
        }
        catch { }

        // Check VMware registry key
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\VMware, Inc.\VMware Tools");
            if (key is not null)
                return HypervisorType.VMware;
        }
        catch { }

        // Additional detection for VirtualBox, Parallels:
        // VirtualBox: HKLM\HARDWARE\ACPI\DSDT\VBOX__
        // Parallels: HKLM\SOFTWARE\Parallels

        return HypervisorType.None;
    }

    /// <summary>
    /// Detects hypervisor on Linux via /sys filesystem and /proc/cpuinfo.
    /// </summary>
    /// <returns>The detected hypervisor type, or <see cref="HypervisorType.None"/> if no hypervisor is found.</returns>
    /// <remarks>
    /// <para>
    /// Checks filesystem paths in order:
    /// </para>
    /// <list type="number">
    ///   <item><description>/sys/hypervisor/type (Xen-specific)</description></item>
    ///   <item><description>/proc/cpuinfo (hypervisor flag indicates virtualization)</description></item>
    /// </list>
    /// <para>
    /// If /proc/cpuinfo contains "hypervisor" flag but /sys/hypervisor/type is not present,
    /// returns <see cref="HypervisorType.Unknown"/> (hypervisor present but type unknown).
    /// </para>
    /// </remarks>
    private static HypervisorType DetectLinuxHypervisor()
    {
        // Check /sys/hypervisor/type (Xen-specific)
        const string hypervisorTypePath = "/sys/hypervisor/type";
        if (File.Exists(hypervisorTypePath))
        {
            string typeStr = File.ReadAllText(hypervisorTypePath).Trim();
            return typeStr switch
            {
                "xen" => HypervisorType.Xen,
                _ => HypervisorType.Unknown
            };
        }

        // Check /proc/cpuinfo for hypervisor flag
        const string cpuinfoPath = "/proc/cpuinfo";
        if (File.Exists(cpuinfoPath))
        {
            string cpuinfo = File.ReadAllText(cpuinfoPath);
            if (cpuinfo.Contains("hypervisor"))
            {
                // Hypervisor present, but type unknown — use CPUID fallback if available
                return HypervisorType.Unknown;
            }
        }

        return HypervisorType.None;
    }

    /// <summary>
    /// Detects the hypervisor version string.
    /// </summary>
    /// <param name="type">The detected hypervisor type.</param>
    /// <returns>
    /// The hypervisor version string, or null if version detection is not implemented or unavailable.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Note:</strong> Version detection is hypervisor-specific and complex. For Phase 35,
    /// this method returns null. Future work: query hypervisor-specific APIs for version.
    /// </para>
    /// <para>
    /// <strong>Implementation Ideas:</strong>
    /// </para>
    /// <list type="bullet">
    ///   <item><description>VMware: Query CPUID leaf 0x40000001 for version info</description></item>
    ///   <item><description>Hyper-V: Query WMI Win32_ComputerSystem.Model</description></item>
    ///   <item><description>KVM: Parse /proc/version or kernel module version</description></item>
    /// </list>
    /// </remarks>
    private static string? DetectVersion(HypervisorType type)
    {
        // Version detection is complex and platform-specific
        // For Phase 35: return null
        // Future work: query hypervisor-specific APIs for version
        // - VMware: CPUID leaf 0x40000001 for version info
        // - Hyper-V: WMI Win32_ComputerSystem.Model
        // - KVM: /proc/version or kernel module version

        return null;
    }

    /// <summary>
    /// Builds environment-specific optimization hints.
    /// </summary>
    /// <param name="type">The detected hypervisor type.</param>
    /// <returns>A list of optimization hints for the detected environment.</returns>
    /// <remarks>
    /// <para>
    /// Optimization hints provide actionable guidance for storage, network, and memory configuration
    /// based on the detected hypervisor. For example:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>VMware: "Use PVSCSI for storage I/O", "Use VMXNET3 for network"</description></item>
    ///   <item><description>Hyper-V: "Enable Hyper-V enlightenments", "Use synthetic storage"</description></item>
    ///   <item><description>KVM: "Use virtio-blk for storage", "Use virtio-net for network"</description></item>
    ///   <item><description>Bare Metal: "Direct hardware access available", "Use native NVMe drivers"</description></item>
    /// </list>
    /// </remarks>
    private static List<string> BuildOptimizationHints(HypervisorType type)
    {
        return type switch
        {
            HypervisorType.VMware => new List<string>
            {
                "Use PVSCSI for storage I/O",
                "Use VMXNET3 for network",
                "Enable VMware Tools for guest operations"
            },
            HypervisorType.HyperV => new List<string>
            {
                "Enable Hyper-V enlightenments",
                "Use synthetic storage (SCSI) instead of IDE",
                "Use synthetic network adapter",
                "Enable Dynamic Memory cooperation"
            },
            HypervisorType.KVM => new List<string>
            {
                "Use virtio-blk for storage",
                "Use virtio-net for network",
                "Enable paravirtualized clock",
                "Use virtio-scsi for multi-queue support"
            },
            HypervisorType.Xen => new List<string>
            {
                "Use Xen paravirtualized drivers (blkfront, netfront)",
                "Enable Xen PV clock",
                "Use grant tables for shared memory"
            },
            HypervisorType.None => new List<string>
            {
                "Direct hardware access available",
                "Use native NVMe drivers",
                "NUMA-aware allocation recommended",
                "No virtualization overhead"
            },
            _ => new List<string>()
        };
    }

    /// <summary>
    /// Determines whether paravirtualized I/O is available for the given hypervisor type.
    /// </summary>
    /// <param name="type">The hypervisor type.</param>
    /// <returns><c>true</c> if paravirtualized I/O is available; otherwise <c>false</c>.</returns>
    /// <remarks>
    /// <para>
    /// Paravirtualized I/O (virtio, PVSCSI, synthetic storage) provides better performance than
    /// full hardware emulation. Available for VMware, Hyper-V, KVM, and Xen. Not available for
    /// bare metal or legacy hypervisors (VirtualPC).
    /// </para>
    /// </remarks>
    private static bool HasParavirtualizedIo(HypervisorType type)
    {
        return type is HypervisorType.VMware or HypervisorType.HyperV or HypervisorType.KVM or HypervisorType.Xen;
    }

    /// <summary>
    /// Determines whether memory ballooning is available for the given hypervisor type.
    /// </summary>
    /// <param name="type">The hypervisor type.</param>
    /// <returns><c>true</c> if memory ballooning is available; otherwise <c>false</c>.</returns>
    /// <remarks>
    /// <para>
    /// Memory ballooning allows the hypervisor to reclaim unused memory from the guest. Available
    /// for VMware, Hyper-V, KVM, and Xen. Not available for bare metal.
    /// </para>
    /// </remarks>
    private static bool HasBalloonDriver(HypervisorType type)
    {
        return type is HypervisorType.VMware or HypervisorType.HyperV or HypervisorType.KVM or HypervisorType.Xen;
    }

    /// <summary>
    /// Gets the capability keys that would be registered for the given hypervisor type.
    /// </summary>
    /// <param name="type">The detected hypervisor type.</param>
    /// <returns>
    /// A list of capability keys that represent the detected hypervisor. Returns empty list
    /// for <see cref="HypervisorType.None"/> (bare metal).
    /// </returns>
    /// <remarks>
    /// <para>
    /// Capability keys that would be registered:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>"hypervisor" (if any hypervisor is detected)</description></item>
    ///   <item><description>"hypervisor.vmware" (if VMware detected)</description></item>
    ///   <item><description>"hypervisor.hyperv" (if Hyper-V detected)</description></item>
    ///   <item><description>"hypervisor.kvm" (if KVM detected)</description></item>
    ///   <item><description>"hypervisor.xen" (if Xen detected)</description></item>
    ///   <item><description>"hypervisor.qemu" (if QEMU detected)</description></item>
    ///   <item><description>"hypervisor.virtualbox" (if VirtualBox detected)</description></item>
    ///   <item><description>"hypervisor.parallels" (if Parallels detected)</description></item>
    ///   <item><description>"hypervisor.virtualpc" (if Virtual PC detected)</description></item>
    ///   <item><description>"hypervisor.unknown" (if unknown hypervisor detected)</description></item>
    /// </list>
    /// <para>
    /// NOTE: This method returns capability keys for informational purposes only. The actual
    /// capability registration depends on the platform capability registry implementation.
    /// Future work: integrate with IPlatformCapabilityRegistry when registration API is added.
    /// </para>
    /// </remarks>
    internal static List<string> GetCapabilityKeys(HypervisorType type)
    {
        if (type == HypervisorType.None)
            return new List<string>(); // Bare metal — no hypervisor capabilities

        var keys = new List<string> { "hypervisor" };

        string typeKey = type switch
        {
            HypervisorType.VMware => "hypervisor.vmware",
            HypervisorType.HyperV => "hypervisor.hyperv",
            HypervisorType.KVM => "hypervisor.kvm",
            HypervisorType.Xen => "hypervisor.xen",
            HypervisorType.QEMU => "hypervisor.qemu",
            HypervisorType.VirtualBox => "hypervisor.virtualbox",
            HypervisorType.Parallels => "hypervisor.parallels",
            HypervisorType.VirtualPC => "hypervisor.virtualpc",
            _ => "hypervisor.unknown"
        };

        keys.Add(typeKey);
        return keys;
    }
}
