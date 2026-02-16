using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Deployment;

/// <summary>
/// Deployment environment classification for DataWarehouse runtime optimization.
/// Determines which optimizations are available and appropriate for the detected environment.
/// </summary>
/// <remarks>
/// <para>
/// Each environment type enables specific performance optimizations:
/// - <see cref="HostedVm"/>: VM on filesystem, double-WAL bypass optimization
/// - <see cref="Hypervisor"/>: Direct hypervisor integration, paravirtualized I/O
/// - <see cref="BareMetalSpdk"/>: User-space NVMe via SPDK (maximum throughput)
/// - <see cref="BareMetalLegacy"/>: Standard kernel I/O (no SPDK)
/// - <see cref="HyperscaleCloud"/>: Cloud platform with auto-scaling
/// - <see cref="EdgeDevice"/>: Edge/IoT with resource constraints
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 37: Deployment environment classification (ENV-01-05)")]
public enum DeploymentEnvironment
{
    /// <summary>
    /// Unknown or undetected deployment environment.
    /// No environment-specific optimizations applied.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Virtual machine running on a filesystem (cloud or on-premises).
    /// Enables double-WAL bypass optimization when VDE WAL is active.
    /// </summary>
    /// <remarks>
    /// Detected when running in a hypervisor on top of a journaling filesystem (ext4, XFS, NTFS).
    /// Every write is journaled twice (filesystem + VDE WAL), causing 30%+ throughput penalty.
    /// Double-WAL bypass disables OS-level journaling for VDE container files when safe.
    /// </remarks>
    HostedVm = 1,

    /// <summary>
    /// Direct hypervisor integration with paravirtualized I/O.
    /// Enables virtio-blk, virtio-scsi, PVSCSI for near-native performance.
    /// </summary>
    /// <remarks>
    /// Detected when running in VMware, KVM, Hyper-V, or Xen with paravirt driver support.
    /// Paravirtualized I/O achieves 95%+ of bare metal throughput.
    /// </remarks>
    Hypervisor = 2,

    /// <summary>
    /// Bare metal deployment with SPDK user-space NVMe access.
    /// Maximum throughput: 90%+ of NVMe hardware specification.
    /// </summary>
    /// <remarks>
    /// Requires dedicated NVMe namespace (cannot share with OS).
    /// SPDK bypasses kernel NVMe driver for lowest latency and highest IOPS.
    /// </remarks>
    BareMetalSpdk = 3,

    /// <summary>
    /// Bare metal deployment using standard kernel NVMe driver.
    /// No SPDK available or no dedicated NVMe namespace.
    /// </summary>
    /// <remarks>
    /// Standard kernel I/O path with no user-space driver optimizations.
    /// </remarks>
    BareMetalLegacy = 4,

    /// <summary>
    /// Hyperscale cloud platform (AWS, Azure, GCP).
    /// Enables auto-scaling and cloud-specific optimizations.
    /// </summary>
    /// <remarks>
    /// Detected via cloud metadata endpoint (169.254.169.254).
    /// Auto-scaling provisions new nodes when storage >80%, deprovisions when <40%.
    /// </remarks>
    HyperscaleCloud = 5,

    /// <summary>
    /// Edge or IoT device with resource constraints.
    /// Enforces memory ceiling, plugin filtering, flash optimization, bandwidth limits.
    /// </summary>
    /// <remarks>
    /// Detected via GPIO/I2C/SPI presence and low memory (<2GB).
    /// Profiles: Raspberry Pi (256MB ceiling), Industrial Gateway (1GB ceiling).
    /// </remarks>
    EdgeDevice = 6
}
