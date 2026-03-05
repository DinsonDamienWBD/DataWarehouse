using DataWarehouse.SDK.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Deployment.CloudProviders;

/// <summary>
/// GCP cloud provider stub. Fails fast with <see cref="NotSupportedException"/> on all operations.
/// Production deployment requires registering a real ICloudProvider backed by Google.Cloud.Compute.V1,
/// Google.Cloud.Storage.V1, and Google.Cloud.Monitoring.V3 via CloudProviderFactory.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 37: GCP provisioning provider (ENV-04)")]
public sealed class GcpProvider : ICloudProvider
{
    private readonly ILogger<GcpProvider> _logger;
    private int _disposed;

    public string Name => "GCP";

    public GcpProvider(ILogger<GcpProvider>? logger = null)
    {
        _logger = logger ?? NullLogger<GcpProvider>.Instance;
    }

    public Task<string> ProvisionVmAsync(VmSpec spec, CancellationToken ct = default)
    {
        throw new NotSupportedException(
            "GCP VM provisioning requires the Google.Cloud.Compute.V1 package. " +
            "Register a real ICloudProvider implementation via CloudProviderFactory.");
    }

    public Task<string> ProvisionStorageAsync(StorageSpec spec, CancellationToken ct = default)
    {
        throw new NotSupportedException(
            "GCP storage provisioning requires the Google.Cloud.Compute.V1/Storage.V1 package. " +
            "Register a real ICloudProvider implementation via CloudProviderFactory.");
    }

    public Task<bool> DeprovisionAsync(string resourceId, CancellationToken ct = default)
    {
        throw new NotSupportedException(
            "GCP deprovisioning requires the Google.Cloud.Compute.V1 package. " +
            "Register a real ICloudProvider implementation via CloudProviderFactory.");
    }

    public Task<CloudResourceMetrics?> GetMetricsAsync(string resourceId, CancellationToken ct = default)
    {
        throw new NotSupportedException(
            "GCP metrics retrieval requires the Google.Cloud.Monitoring.V3 package. " +
            "Register a real ICloudProvider implementation via CloudProviderFactory.");
    }

    public Task<IReadOnlyList<string>> ListManagedResourcesAsync(CancellationToken ct = default)
    {
        throw new NotSupportedException(
            "GCP resource listing requires the Google.Cloud.Compute.V1 package. " +
            "Register a real ICloudProvider implementation via CloudProviderFactory.");
    }

    public void Dispose()
    {
        Interlocked.CompareExchange(ref _disposed, 1, 0);
    }
}
