using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Hardware;
using DataWarehouse.SDK.Virtualization;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Deployment;

/// <summary>
/// Detects bare metal (physical hardware, not VM) deployment.
/// Distinguishes between SPDK-capable (dedicated NVMe) and legacy (standard kernel driver) bare metal.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bare Metal Detection Criteria:</b>
/// 1. NO hypervisor detected (verified via <see cref="IHypervisorDetector"/>)
/// 2. NO cloud metadata endpoint (not a cloud VM)
/// 3. Physical NVMe controllers present
/// 4. SPDK availability (Phase 35 NvmePassthroughStrategy available)
/// </para>
/// <para>
/// <b>SPDK vs Legacy:</b>
/// - <b>BareMetalSpdk:</b> SPDK available + NVMe present -> Maximum throughput (90%+ of hardware spec)
/// - <b>BareMetalLegacy:</b> SPDK not available OR no NVMe -> Standard kernel driver
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 37: Bare metal environment detection (ENV-03)")]
public sealed class BareMetalDetector : IDeploymentDetector
{
    private readonly IHypervisorDetector? _hypervisorDetector;
    private readonly IHardwareProbe? _hardwareProbe;

    /// <summary>
    /// Initializes a new instance of the <see cref="BareMetalDetector"/> class.
    /// </summary>
    public BareMetalDetector(IHypervisorDetector? hypervisorDetector = null, IHardwareProbe? hardwareProbe = null)
    {
        _hypervisorDetector = hypervisorDetector;
        _hardwareProbe = hardwareProbe;
    }

    /// <summary>
    /// Detects bare metal environment and determines SPDK availability.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="DeploymentContext"/> with <see cref="DeploymentEnvironment.BareMetalSpdk"/> or
    /// <see cref="DeploymentEnvironment.BareMetalLegacy"/>. Returns null if not bare metal (VM detected).
    /// </returns>
    public async Task<DeploymentContext?> DetectAsync(CancellationToken ct = default)
    {
        try
        {
            // Step 1: Verify NOT running in a hypervisor
            if (_hypervisorDetector != null && _hypervisorDetector.IsVirtualized)
            {
                // Running in a VM, not bare metal
                return null;
            }

            // Step 2: Verify NOT running in a cloud environment
            if (await IsCloudEnvironmentAsync(ct))
            {
                // Cloud VM, not bare metal
                return null;
            }

            // Step 3: Detect NVMe controllers and namespaces
            int nvmeControllerCount = 0;
            int nvmeNamespaceCount = 0;

            if (_hardwareProbe != null)
            {
                var devices = await _hardwareProbe.DiscoverAsync(
                    HardwareDeviceType.NvmeController | HardwareDeviceType.NvmeNamespace,
                    ct);

                foreach (var device in devices)
                {
                    if (device.Type == HardwareDeviceType.NvmeController)
                        nvmeControllerCount++;
                    else if (device.Type == HardwareDeviceType.NvmeNamespace)
                        nvmeNamespaceCount++;
                }
            }

            // Step 4: Check for SPDK availability
            // In production, this would check if Phase 35 NvmePassthroughStrategy is available
            // For now, assume SPDK available if NVMe present
            bool spdkAvailable = nvmeControllerCount > 0 && nvmeNamespaceCount > 0;

            // Step 5: Construct DeploymentContext
            var metadata = new Dictionary<string, string>
            {
                ["NvmeControllerCount"] = nvmeControllerCount.ToString(),
                ["NvmeNamespaceCount"] = nvmeNamespaceCount.ToString(),
                ["SpdkAvailable"] = spdkAvailable.ToString()
            };

            var environment = spdkAvailable
                ? DeploymentEnvironment.BareMetalSpdk
                : DeploymentEnvironment.BareMetalLegacy;

            return new DeploymentContext
            {
                Environment = environment,
                IsBareMetalSpdk = spdkAvailable,
                Metadata = metadata
            };
        }
        catch
        {
            // Detection failure returns null (graceful degradation)
            return null;
        }
    }

    private async Task<bool> IsCloudEnvironmentAsync(CancellationToken ct)
    {
        try
        {
            // Quick check for cloud metadata endpoint (169.254.169.254)
            // If this responds, we're in a cloud VM, not bare metal
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromMilliseconds(100) };
            using var response = await httpClient.GetAsync("http://169.254.169.254/", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            // No cloud metadata endpoint (expected for bare metal)
            return false;
        }
    }
}
