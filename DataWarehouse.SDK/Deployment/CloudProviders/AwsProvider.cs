using DataWarehouse.SDK.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Deployment.CloudProviders;

/// <summary>
/// AWS cloud provider stub. Fails fast with <see cref="NotSupportedException"/> on all operations.
/// Production deployment requires registering a real ICloudProvider implementation backed by
/// AWSSDK.EC2, AWSSDK.S3, and AWSSDK.CloudWatch via CloudProviderFactory.
/// </summary>
/// <remarks>
/// This class exists as a named registration point. All provisioning, metrics, and lifecycle
/// operations throw <see cref="NotSupportedException"/> directing callers to install the AWS SDK
/// plugin. No hardcoded fake data is returned.
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 37: AWS provisioning provider (ENV-04)")]
public sealed class AwsProvider : ICloudProvider
{
    private readonly ILogger<AwsProvider> _logger;
    private int _disposed;

    public string Name => "AWS";

    public AwsProvider(ILogger<AwsProvider>? logger = null)
    {
        _logger = logger ?? NullLogger<AwsProvider>.Instance;
    }

    public Task<string> ProvisionVmAsync(VmSpec spec, CancellationToken ct = default)
    {
        throw new NotSupportedException(
            "AWS VM provisioning requires the AWSSDK.EC2 package. " +
            "Register a real ICloudProvider implementation via CloudProviderFactory.");
    }

    public Task<string> ProvisionStorageAsync(StorageSpec spec, CancellationToken ct = default)
    {
        throw new NotSupportedException(
            "AWS storage provisioning requires the AWSSDK.EC2/S3 package. " +
            "Register a real ICloudProvider implementation via CloudProviderFactory.");
    }

    public Task<bool> DeprovisionAsync(string resourceId, CancellationToken ct = default)
    {
        throw new NotSupportedException(
            "AWS deprovisioning requires the AWSSDK.EC2 package. " +
            "Register a real ICloudProvider implementation via CloudProviderFactory.");
    }

    public Task<CloudResourceMetrics?> GetMetricsAsync(string resourceId, CancellationToken ct = default)
    {
        throw new NotSupportedException(
            "AWS metrics retrieval requires the AWSSDK.CloudWatch package. " +
            "Register a real ICloudProvider implementation via CloudProviderFactory.");
    }

    public Task<IReadOnlyList<string>> ListManagedResourcesAsync(CancellationToken ct = default)
    {
        throw new NotSupportedException(
            "AWS resource listing requires the AWSSDK.EC2 package. " +
            "Register a real ICloudProvider implementation via CloudProviderFactory.");
    }

    public void Dispose()
    {
        Interlocked.CompareExchange(ref _disposed, 1, 0);
    }
}
