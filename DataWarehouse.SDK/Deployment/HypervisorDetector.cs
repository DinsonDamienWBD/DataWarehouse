using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Virtualization;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Deployment;

/// <summary>
/// Detects hypervisor environment with direct integration capabilities.
/// Delegates to existing <see cref="IHypervisorDetector"/> from SDK.Virtualization (Phase 32 infrastructure).
/// </summary>
/// <remarks>
/// <para>
/// A "hypervisor" deployment has direct hypervisor integration with paravirtualized I/O and balloon driver support.
/// This differs from HostedVm which focuses on filesystem-level optimizations.
/// </para>
/// <para>
/// Detection: Leverages Phase 32 <see cref="IHypervisorDetector"/> to identify VMware, KVM, Hyper-V, Xen.
/// </para>
/// <para>
/// Capabilities checked:
/// - SupportsBalloonDriver: Dynamic memory management
/// - SupportsParavirtualization: virtio-blk, PVSCSI availability
/// - SupportsLiveMigration: Pre-migration WAL flush hooks
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 37: Hypervisor environment detection (ENV-02)")]
public sealed class HypervisorDetector : IDeploymentDetector
{
    private readonly IHypervisorDetector? _hypervisorDetector;

    /// <summary>
    /// Initializes a new instance of the <see cref="HypervisorDetector"/> class.
    /// </summary>
    /// <param name="hypervisorDetector">
    /// Optional hypervisor detector from SDK.Virtualization. If null, detection is skipped.
    /// </param>
    public HypervisorDetector(IHypervisorDetector? hypervisorDetector = null)
    {
        _hypervisorDetector = hypervisorDetector;
    }

    /// <summary>
    /// Detects hypervisor environment by delegating to existing SDK.Virtualization infrastructure.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="DeploymentContext"/> with <see cref="DeploymentEnvironment.Hypervisor"/> if detected.
    /// Returns null if not running in a hypervisor or detection infrastructure unavailable.
    /// </returns>
    public async Task<DeploymentContext?> DetectAsync(CancellationToken ct = default)
    {
        try
        {
            if (_hypervisorDetector == null)
            {
                // No hypervisor detector available -- graceful degradation
                return null;
            }

            if (!_hypervisorDetector.IsVirtualized)
            {
                // Not running in a hypervisor (bare metal)
                return null;
            }

            var hypervisorType = _hypervisorDetector.DetectedHypervisor;
            if (hypervisorType == HypervisorType.Bare || hypervisorType == HypervisorType.Unknown)
            {
                // Bare metal or unknown hypervisor
                return null;
            }

            // Get hypervisor capabilities
            HypervisorCapabilities? capabilities = null;
            VmInfo? vmInfo = null;

            try
            {
                capabilities = await _hypervisorDetector.GetCapabilitiesAsync();
                vmInfo = await _hypervisorDetector.GetVmInfoAsync();
            }
            catch
            {
                // Capability/info retrieval failed, continue with partial detection
            }

            // Construct metadata
            var metadata = new Dictionary<string, string>
            {
                ["HypervisorType"] = hypervisorType.ToString()
            };

            if (vmInfo != null)
            {
                metadata["HypervisorVersion"] = vmInfo.VmId ?? "unknown";
                metadata["VmName"] = vmInfo.VmName ?? "unknown";
                metadata["CpuCount"] = vmInfo.CpuCount.ToString();
                metadata["MemoryMB"] = (vmInfo.MemoryBytes / 1024 / 1024).ToString();
            }

            if (capabilities != null)
            {
                metadata["BalloonSupport"] = capabilities.SupportsBalloonDriver.ToString();
                metadata["ParavirtSupport"] = (capabilities.ParavirtDrivers?.Length > 0).ToString();
                metadata["LiveMigrationSupport"] = capabilities.SupportsLiveMigration.ToString();
                metadata["TrimSupport"] = capabilities.SupportsTrimDiscard.ToString();

                if (capabilities.ParavirtDrivers?.Length > 0)
                {
                    metadata["ParavirtDrivers"] = string.Join(",", capabilities.ParavirtDrivers);
                }
            }

            return new DeploymentContext
            {
                Environment = DeploymentEnvironment.Hypervisor,
                HypervisorType = hypervisorType.ToString(),
                Metadata = metadata
            };
        }
        catch
        {
            // Any error during detection returns null (graceful degradation)
            return null;
        }
    }
}
