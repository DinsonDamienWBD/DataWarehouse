using DataWarehouse.SDK.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Deployment.CloudProviders;

/// <summary>
/// Azure cloud provider implementation (placeholder).
/// Production uses Azure SDK (ResourceManager, Compute, Blob, Monitor).
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 37: Azure provisioning provider (ENV-04)")]
public sealed class AzureProvider : ICloudProvider
{
    private readonly ILogger<AzureProvider> _logger;
    private bool _disposed;

    public string Name => "Azure";

    public AzureProvider(ILogger<AzureProvider>? logger = null)
    {
        _logger = logger ?? NullLogger<AzureProvider>.Instance;
    }

    public Task<string> ProvisionVmAsync(VmSpec spec, CancellationToken ct = default)
    {
        _logger.LogInformation("Azure: Provisioning VM (size: {InstanceType}, location: {Region})",
            spec.InstanceType, spec.Region);
        return Task.FromResult($"vm-{Guid.NewGuid():N}");
    }

    public Task<string> ProvisionStorageAsync(StorageSpec spec, CancellationToken ct = default)
    {
        _logger.LogInformation("Azure: Provisioning storage ({Type}, {SizeGb} GB)",
            spec.StorageType, spec.SizeGb);
        return Task.FromResult($"disk-{Guid.NewGuid():N}");
    }

    public Task<bool> DeprovisionAsync(string resourceId, CancellationToken ct = default)
    {
        _logger.LogInformation("Azure: Deprovisioning resource {ResourceId}", resourceId);
        return Task.FromResult(true);
    }

    public Task<CloudResourceMetrics?> GetMetricsAsync(string resourceId, CancellationToken ct = default)
    {
        return Task.FromResult<CloudResourceMetrics?>(new CloudResourceMetrics
        {
            ResourceId = resourceId,
            CpuUtilizationPercent = 50.0,
            StorageUtilizationPercent = 70.0,
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
