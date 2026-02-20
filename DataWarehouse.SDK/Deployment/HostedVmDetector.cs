using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Virtualization;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Deployment;

/// <summary>
/// Detects hosted VM environment (VM running on a filesystem, not bare metal).
/// Uses <see cref="IHypervisorDetector"/> to confirm virtualization and <see cref="FilesystemDetector"/> to identify filesystem.
/// </summary>
/// <remarks>
/// <para>
/// A "hosted VM" is a virtual machine running on top of a host filesystem (ext4, XFS, NTFS).
/// This scenario suffers from the double-WAL penalty when both the filesystem and VDE use journaling.
/// </para>
/// <para>
/// Detection heuristic:
/// 1. Hypervisor detected (IHypervisorDetector returns non-null)
/// 2. Filesystem detected (data is stored on a filesystem, not raw block device)
/// </para>
/// <para>
/// Returns null if not a hosted VM (bare metal or hypervisor-direct integration).
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 37: Hosted VM environment detection (ENV-01)")]
public sealed class HostedVmDetector : IDeploymentDetector
{
    private readonly IHypervisorDetector? _hypervisorDetector;

    /// <summary>
    /// Initializes a new instance of the <see cref="HostedVmDetector"/> class.
    /// </summary>
    /// <param name="hypervisorDetector">
    /// Optional hypervisor detector. If null, attempts to create a default instance.
    /// </param>
    public HostedVmDetector(IHypervisorDetector? hypervisorDetector = null)
    {
        _hypervisorDetector = hypervisorDetector;
    }

    /// <summary>
    /// Detects hosted VM environment by checking for hypervisor and filesystem presence.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="DeploymentContext"/> with <see cref="DeploymentEnvironment.HostedVm"/> if detected.
    /// Returns null if not a hosted VM (bare metal or hypervisor-direct).
    /// </returns>
    public async Task<DeploymentContext?> DetectAsync(CancellationToken ct = default)
    {
        try
        {
            // Step 1: Check if running in a hypervisor
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

            // Get VM info for metadata
            VmInfo? vmInfo = null;
            try
            {
                vmInfo = await _hypervisorDetector.GetVmInfoAsync();
            }
            catch
            {
                // VM info retrieval failed, continue with partial detection
            }

            // Step 2: Detect filesystem type (supported on Linux, Windows, macOS)
            var currentDirectory = Environment.CurrentDirectory;
            string? filesystemType = null;
            if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            {
                filesystemType = await FilesystemDetector.DetectFilesystemTypeAsync(currentDirectory, ct);
            }

            // Step 3: Construct DeploymentContext
            var metadata = new Dictionary<string, string>();

            if (vmInfo != null)
            {
                metadata["HypervisorVersion"] = vmInfo.VmId ?? "unknown";
                metadata["VmName"] = vmInfo.VmName ?? "unknown";
                metadata["CpuCount"] = vmInfo.CpuCount.ToString();
                metadata["MemoryMB"] = (vmInfo.MemoryBytes / 1024 / 1024).ToString();
            }

            if (filesystemType != null)
            {
                metadata["FilesystemType"] = filesystemType;
            }

            return new DeploymentContext
            {
                Environment = DeploymentEnvironment.HostedVm,
                HypervisorType = hypervisorType.ToString(),
                FilesystemType = filesystemType,
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
