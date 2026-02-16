using DataWarehouse.SDK.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Deployment.CloudProviders;

/// <summary>
/// GCP cloud provider implementation (placeholder).
/// Production uses Google Cloud Client Libraries (Compute, Storage, Monitoring).
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 37: GCP provisioning provider (ENV-04)")]
public sealed class GcpProvider : ICloudProvider
{
    private readonly ILogger<GcpProvider> _logger;
    private bool _disposed;

    public string Name => "GCP";

    public GcpProvider(ILogger<GcpProvider>? logger = null)
    {
        _logger = logger ?? NullLogger<GcpProvider>.Instance;
    }

    public Task<string> ProvisionVmAsync(VmSpec spec, CancellationToken ct = default)
    {
        _logger.LogInformation("GCP: Provisioning VM (machine type: {InstanceType}, zone: {Region})",
            spec.InstanceType, spec.Region);
        return Task.FromResult($"instance-{Guid.NewGuid():N}");
    }

    public Task<string> ProvisionStorageAsync(StorageSpec spec, CancellationToken ct = default)
    {
        _logger.LogInformation("GCP: Provisioning storage ({Type}, {SizeGb} GB)",
            spec.StorageType, spec.SizeGb);
        return Task.FromResult($"pd-{Guid.NewGuid():N}");
    }

    public Task<bool> DeprovisionAsync(string resourceId, CancellationToken ct = default)
    {
        _logger.LogInformation("GCP: Deprovisioning resource {ResourceId}", resourceId);
        return Task.FromResult(true);
    }

    public Task<CloudResourceMetrics?> GetMetricsAsync(string resourceId, CancellationToken ct = default)
    {
        return Task.FromResult<CloudResourceMetrics?>(new CloudResourceMetrics
        {
            ResourceId = resourceId,
            CpuUtilizationPercent = 55.0,
            StorageUtilizationPercent = 75.0,
            Timestamp = DateTimeOffset.UtcNow
        });
    }

    public Task<IReadOnlyList<string>> ListManagedResourcesAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
