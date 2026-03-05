using System;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Preamble;

/// <summary>
/// Defines the kernel configuration for building a stripped Linux kernel suitable for the
/// DWVD bootable preamble (VOPT-64). The resulting bzImage targets &lt;8MB with only the
/// drivers required for bare-metal boot: USB, NIC, display, and vfio-pci (SPDK handoff).
/// </summary>
/// <remarks>
/// <para>This class generates deterministic <c>CONFIG_xxx=y/n</c> entries for a monolithic
/// (no loadable modules) kernel. Three factory profiles cover common deployment scenarios:</para>
/// <list type="bullet">
/// <item><description><see cref="ServerFull"/>: Full I/O — USB, NIC, display, vfio-pci.</description></item>
/// <item><description><see cref="EmbeddedMinimal"/>: Serial console only — no display, no USB.</description></item>
/// <item><description><see cref="AirgapReadonly"/>: Air-gapped — no NIC, USB + display + vfio-pci.</description></item>
/// </list>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5 Wave 6: VOPT-64 stripped kernel build spec")]
public sealed class StrippedKernelBuildSpec
{
    /// <summary>Default maximum bzImage size in bytes (8 MiB).</summary>
    private const long DefaultMaxBzImageBytes = 8L * 1024 * 1024;

    /// <summary>Linux LTS kernel version to build.</summary>
    public string KernelVersion { get; init; } = "6.6";

    /// <summary>Target CPU architecture for the kernel image.</summary>
    public TargetArchitecture Architecture { get; init; } = TargetArchitecture.X8664;

    /// <summary>
    /// Maximum acceptable size of the output kernel image in bytes.
    /// The build script will fail if the image exceeds this limit.
    /// </summary>
    public long MaxBzImageBytes { get; init; } = DefaultMaxBzImageBytes;

    /// <summary>Include USB host controller and mass-storage drivers.</summary>
    public bool IncludeUsbDrivers { get; init; } = true;

    /// <summary>Include network interface card drivers (virtio-net, e1000, ixgbe).</summary>
    public bool IncludeNicDrivers { get; init; } = true;

    /// <summary>Include basic display/framebuffer drivers for console output.</summary>
    public bool IncludeDisplayDrivers { get; init; } = true;

    /// <summary>Include vfio-pci for SPDK user-space driver handoff. Required for VDE I/O.</summary>
    public bool IncludeVfioPci { get; init; } = true;

    /// <summary>
    /// Include NVMe kernel driver. Defaults to <c>false</c> because NVMe is handled by
    /// SPDK in user-space; enabling this is only needed for fallback boot paths.
    /// </summary>
    public bool IncludeNvmeDriver { get; init; }

    /// <summary>
    /// Optional additional kernel modules to enable (e.g., <c>"CONFIG_VIRTIO_BLK"</c>).
    /// Each entry should be a bare config symbol name without the <c>=y</c> suffix.
    /// </summary>
    public IReadOnlyList<string> AdditionalModules { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Kernel subsystems to explicitly disable. Defaults to sound, Bluetooth, and wireless
    /// to minimize image size and attack surface.
    /// </summary>
    public IReadOnlyList<string> DisabledSubsystems { get; init; } = new[]
    {
        "CONFIG_SOUND",
        "CONFIG_BLUETOOTH",
        "CONFIG_WIRELESS",
        "CONFIG_WLAN",
    };

    /// <summary>
    /// Creates a full server profile with USB, NIC, display, and vfio-pci drivers.
    /// This is the standard deployment configuration for bare-metal servers.
    /// </summary>
    public static StrippedKernelBuildSpec ServerFull() => new();

    /// <summary>
    /// Creates an embedded/headless profile with serial console only.
    /// No display or USB drivers — minimal footprint for edge and IoT deployments.
    /// </summary>
    public static StrippedKernelBuildSpec EmbeddedMinimal() => new()
    {
        IncludeUsbDrivers = false,
        IncludeDisplayDrivers = false,
    };

    /// <summary>
    /// Creates an air-gapped profile with no network interface drivers.
    /// USB and display are enabled for local console access; NIC is disabled to prevent
    /// any network capability in high-security environments.
    /// </summary>
    public static StrippedKernelBuildSpec AirgapReadonly() => new()
    {
        IncludeNicDrivers = false,
    };

    /// <summary>
    /// Generates deterministic kernel configuration entries (<c>CONFIG_xxx=y</c> or <c>=n</c>)
    /// based on the current spec settings.
    /// </summary>
    /// <returns>An ordered list of kconfig lines ready to be written to a <c>.config</c> file.</returns>
    public IReadOnlyList<string> GenerateKconfigEntries()
    {
        var entries = new List<string>(64);

        // -- Architecture --
        switch (Architecture)
        {
            case TargetArchitecture.X8664:
                entries.Add("CONFIG_X86_64=y");
                entries.Add("CONFIG_64BIT=y");
                break;
            case TargetArchitecture.Aarch64:
                entries.Add("CONFIG_ARM64=y");
                entries.Add("CONFIG_64BIT=y");
                break;
            case TargetArchitecture.RiscV64:
                entries.Add("CONFIG_RISCV=y");
                entries.Add("CONFIG_64BIT=y");
                entries.Add("CONFIG_ARCH_RV64I=y");
                break;
            default:
                entries.Add("CONFIG_64BIT=y");
                break;
        }

        // -- Always-on core subsystems --
        entries.Add("CONFIG_SMP=y");
        entries.Add("CONFIG_IOMMU_SUPPORT=y");
        entries.Add("CONFIG_EFI=y");
        entries.Add("CONFIG_EFI_STUB=y");
        entries.Add("CONFIG_BLK_DEV=y");
        entries.Add("CONFIG_BLOCK=y");
        entries.Add("CONFIG_PRINTK=y");
        entries.Add("CONFIG_SERIAL_8250=y");
        entries.Add("CONFIG_SERIAL_8250_CONSOLE=y");
        entries.Add("CONFIG_TTY=y");

        // -- VFIO / SPDK handoff (always required when spec says so) --
        if (IncludeVfioPci)
        {
            entries.Add("CONFIG_VFIO=y");
            entries.Add("CONFIG_VFIO_PCI=y");
            entries.Add("CONFIG_VFIO_IOMMU_TYPE1=y");
        }

        // -- USB --
        if (IncludeUsbDrivers)
        {
            entries.Add("CONFIG_USB_SUPPORT=y");
            entries.Add("CONFIG_USB=y");
            entries.Add("CONFIG_USB_XHCI_HCD=y");
            entries.Add("CONFIG_USB_EHCI_HCD=y");
            entries.Add("CONFIG_USB_STORAGE=y");
        }
        else
        {
            entries.Add("CONFIG_USB_SUPPORT=n");
        }

        // -- Networking --
        if (IncludeNicDrivers)
        {
            entries.Add("CONFIG_NET=y");
            entries.Add("CONFIG_INET=y");
            entries.Add("CONFIG_NETDEVICES=y");
            entries.Add("CONFIG_VIRTIO_NET=y");
            entries.Add("CONFIG_E1000=y");
            entries.Add("CONFIG_E1000E=y");
            entries.Add("CONFIG_IXGBE=y");
        }
        else
        {
            entries.Add("CONFIG_NET=n");
        }

        // -- Display / framebuffer --
        if (IncludeDisplayDrivers)
        {
            entries.Add("CONFIG_DRM=y");
            entries.Add("CONFIG_DRM_FBDEV_EMULATION=y");
            entries.Add("CONFIG_FRAMEBUFFER_CONSOLE=y");
            entries.Add("CONFIG_FB=y");
            entries.Add("CONFIG_VGA_CONSOLE=y");
        }
        else
        {
            entries.Add("CONFIG_DRM=n");
            entries.Add("CONFIG_FRAMEBUFFER_CONSOLE=n");
        }

        // -- NVMe (optional, SPDK normally handles this in user-space) --
        if (IncludeNvmeDriver)
        {
            entries.Add("CONFIG_BLK_DEV_NVME=y");
            entries.Add("CONFIG_NVME_CORE=y");
        }

        // -- Always disabled: size and attack-surface reduction --
        entries.Add("CONFIG_DEBUG_INFO=n");
        entries.Add("CONFIG_MODULES=n");
        entries.Add("CONFIG_KPROBES=n");
        entries.Add("CONFIG_FTRACE=n");
        entries.Add("CONFIG_PROFILING=n");

        // -- Explicitly disabled subsystems --
        foreach (var subsystem in DisabledSubsystems)
        {
            var normalized = subsystem.StartsWith("CONFIG_", StringComparison.Ordinal)
                ? subsystem
                : "CONFIG_" + subsystem;
            entries.Add(normalized + "=n");
        }

        // -- Additional user-specified modules --
        foreach (var module in AdditionalModules)
        {
            var normalized = module.StartsWith("CONFIG_", StringComparison.Ordinal)
                ? module
                : "CONFIG_" + module;
            entries.Add(normalized + "=y");
        }

        return entries;
    }
}
