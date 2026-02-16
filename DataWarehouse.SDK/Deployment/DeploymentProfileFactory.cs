using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Deployment.EdgeProfiles;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace DataWarehouse.SDK.Deployment;

/// <summary>
/// Factory for creating deployment profiles based on detected environment.
/// Selects optimal configuration for each deployment type (hosted VM, hypervisor, bare metal, cloud, edge).
/// </summary>
/// <remarks>
/// <para>
/// <b>Profile Selection Logic:</b>
/// - <b>HostedVm:</b> Double-WAL bypass enabled (when VDE WAL active)
/// - <b>Hypervisor:</b> Paravirtualized I/O enabled (virtio-blk, PVSCSI)
/// - <b>BareMetalSpdk:</b> SPDK user-space NVMe enabled
/// - <b>BareMetalLegacy:</b> Standard kernel NVMe (no SPDK)
/// - <b>HyperscaleCloud:</b> Auto-scaling enabled
/// - <b>EdgeDevice:</b> Memory ceiling, plugin filtering (selects Raspberry Pi or Industrial Gateway preset)
/// </para>
/// <para>
/// Edge profile selection uses platform metadata:
/// - "Raspberry Pi" in platform model -> <see cref="RaspberryPiProfile"/>
/// - "Industrial" or "Gateway" in platform model -> <see cref="IndustrialGatewayProfile"/>
/// - Generic edge -> Conservative defaults (512MB, essential plugins)
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 37: Deployment profile factory (ENV-01-05)")]
public static class DeploymentProfileFactory
{
    /// <summary>
    /// Creates an optimal deployment profile for the detected environment.
    /// </summary>
    /// <param name="context">Detected deployment context from <see cref="IDeploymentDetector"/>.</param>
    /// <returns>Deployment profile with environment-specific optimizations.</returns>
    public static DeploymentProfile CreateProfile(DeploymentContext context)
    {
        return context.Environment switch
        {
            DeploymentEnvironment.HostedVm => new DeploymentProfile
            {
                Name = "hosted-vm",
                Environment = DeploymentEnvironment.HostedVm,
                DoubleWalBypassEnabled = true,  // ENV-01: Disable OS journaling when VDE WAL active
                ParavirtIoEnabled = false,      // Not applicable to hosted (filesystem-level optimization)
                SpdkEnabled = false,            // Not applicable to VMs
                AutoScalingEnabled = false,     // Manual VM management
                MaxMemoryBytes = null           // No memory ceiling (server-class resources)
            },

            DeploymentEnvironment.Hypervisor => new DeploymentProfile
            {
                Name = "hypervisor",
                Environment = DeploymentEnvironment.Hypervisor,
                DoubleWalBypassEnabled = false, // Hypervisor I/O path differs from hosted
                ParavirtIoEnabled = true,       // ENV-02: Enable virtio-blk/PVSCSI
                SpdkEnabled = false,            // Not applicable to VMs
                AutoScalingEnabled = false,     // Hypervisor manages resources
                MaxMemoryBytes = null           // No memory ceiling
            },

            DeploymentEnvironment.BareMetalSpdk => new DeploymentProfile
            {
                Name = "bare-metal-spdk",
                Environment = DeploymentEnvironment.BareMetalSpdk,
                DoubleWalBypassEnabled = false, // No OS filesystem (SPDK user-space)
                ParavirtIoEnabled = false,      // Not applicable to bare metal
                SpdkEnabled = true,             // ENV-03: SPDK user-space NVMe
                AutoScalingEnabled = false,     // Manual hardware management
                MaxMemoryBytes = null           // No memory ceiling
            },

            DeploymentEnvironment.BareMetalLegacy => new DeploymentProfile
            {
                Name = "bare-metal-legacy",
                Environment = DeploymentEnvironment.BareMetalLegacy,
                DoubleWalBypassEnabled = false, // Standard kernel driver
                ParavirtIoEnabled = false,      // Not applicable to bare metal
                SpdkEnabled = false,            // SPDK not available
                AutoScalingEnabled = false,     // Manual hardware management
                MaxMemoryBytes = null           // No memory ceiling
            },

            DeploymentEnvironment.HyperscaleCloud => new DeploymentProfile
            {
                Name = "hyperscale-cloud",
                Environment = DeploymentEnvironment.HyperscaleCloud,
                DoubleWalBypassEnabled = true,  // Cloud VMs on filesystems (same as hosted)
                ParavirtIoEnabled = true,       // Cloud providers use paravirt I/O (AWS ENA, Azure accelerated networking)
                SpdkEnabled = false,            // Not applicable to cloud VMs
                AutoScalingEnabled = true,      // ENV-04: Auto-provision cloud resources
                MaxMemoryBytes = null           // No memory ceiling (cloud resources scale dynamically)
            },

            DeploymentEnvironment.EdgeDevice => CreateEdgeProfile(context),

            _ => new DeploymentProfile
            {
                Name = "default",
                Environment = DeploymentEnvironment.Unknown,
                DoubleWalBypassEnabled = false,
                ParavirtIoEnabled = false,
                SpdkEnabled = false,
                AutoScalingEnabled = false,
                MaxMemoryBytes = null
            }
        };
    }

    private static DeploymentProfile CreateEdgeProfile(DeploymentContext context)
    {
        // Select edge preset based on platform metadata
        var platformModel = context.Metadata.GetValueOrDefault("PlatformModel", "");

        EdgeProfile edgeProfile;

        if (platformModel.Contains("Raspberry Pi", StringComparison.OrdinalIgnoreCase))
        {
            // Raspberry Pi preset: 256MB, essential plugins, 10MB/s bandwidth
            edgeProfile = RaspberryPiProfile.Create();
        }
        else if (platformModel.Contains("Industrial", StringComparison.OrdinalIgnoreCase) ||
                 platformModel.Contains("Gateway", StringComparison.OrdinalIgnoreCase))
        {
            // Industrial gateway preset: 1GB, MQTT/CoAP, 50MB/s bandwidth
            edgeProfile = IndustrialGatewayProfile.Create();
        }
        else
        {
            // Generic edge: conservative defaults (512MB, essential plugins, 20MB/s)
            edgeProfile = new CustomEdgeProfileBuilder()
                .WithName("generic-edge")
                .WithMemoryCeilingMB(512)
                .AllowPlugins("UltimateStorage", "TamperProof", "EdgeSensorMesh")
                .WithMaxConnections(25)
                .WithBandwidthCeilingMBps(20)
                .Build();
        }

        // Wrap EdgeProfile in DeploymentProfile
        return new DeploymentProfile
        {
            Name = edgeProfile.Name,
            Environment = DeploymentEnvironment.EdgeDevice,
            DoubleWalBypassEnabled = false, // Edge typically uses flash storage (different optimization)
            ParavirtIoEnabled = false,      // Not applicable to edge (physical hardware)
            SpdkEnabled = false,            // Not applicable to edge (limited resources)
            AutoScalingEnabled = false,     // Manual edge device management
            MaxMemoryBytes = edgeProfile.MaxMemoryBytes,
            AllowedPlugins = edgeProfile.AllowedPlugins,
            CustomSettings = new Dictionary<string, object>
            {
                ["EdgeProfile"] = edgeProfile // Store full EdgeProfile for enforcement
            }.ToImmutableDictionary()
        };
    }
}
