using DataWarehouse.SDK.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Deployment.CloudProviders;

/// <summary>
/// Azure cloud provider stub. Fails fast with <see cref="NotSupportedException"/> on all operations.
/// Production deployment requires registering a real ICloudProvider backed by Azure.ResourceManager,
/// Azure.Compute, Azure.Storage.Blobs, and Azure.Monitor via CloudProviderFactory.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 37: Azure provisioning provider (ENV-04)")]
public sealed class AzureProvider : ICloudProvider
{
    private readonly ILogger<AzureProvider> _logger;
    private int _disposed;

    public string Name => "Azure";

    public AzureProvider(ILogger<AzureProvider>? logger = null)
    {
        _logger = logger ?? NullLogger<AzureProvider>.Instance;
    }

    public Task<string> ProvisionVmAsync(VmSpec spec, CancellationToken ct = default)
    {
        throw new NotSupportedException(
            "Azure VM provisioning requires the Azure.ResourceManager.Compute package. " +
            "Register a real ICloudProvider implementation via CloudProviderFactory.");
    }

    public Task<string> ProvisionStorageAsync(StorageSpec spec, CancellationToken ct = default)
    {
        throw new NotSupportedException(
            "Azure storage provisioning requires the Azure.ResourceManager.Compute/Storage package. " +
            "Register a real ICloudProvider implementation via CloudProviderFactory.");
    }

    public Task<bool> DeprovisionAsync(string resourceId, CancellationToken ct = default)
    {
        throw new NotSupportedException(
            "Azure deprovisioning requires the Azure.ResourceManager package. " +
            "Register a real ICloudProvider implementation via CloudProviderFactory.");
    }

    public Task<CloudResourceMetrics?> GetMetricsAsync(string resourceId, CancellationToken ct = default)
    {
        throw new NotSupportedException(
            "Azure metrics retrieval requires the Azure.Monitor.Query package. " +
            "Register a real ICloudProvider implementation via CloudProviderFactory.");
    }

    public Task<IReadOnlyList<string>> ListManagedResourcesAsync(CancellationToken ct = default)
    {
        throw new NotSupportedException(
            "Azure resource listing requires the Azure.ResourceManager package. " +
            "Register a real ICloudProvider implementation via CloudProviderFactory.");
    }

    public void Dispose()
    {
        Interlocked.CompareExchange(ref _disposed, 1, 0);
    }
}
