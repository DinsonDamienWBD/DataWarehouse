using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Deployment.CloudProviders;

/// <summary>
/// Cloud provider abstraction interface for multi-cloud deployment.
/// Implementations provide AWS, Azure, GCP-specific provisioning logic.
/// </summary>
/// <remarks>
/// <para>
/// <b>Purpose:</b>
/// Abstract cloud provider differences so DataWarehouse can deploy across AWS/Azure/GCP
/// without cloud-specific code in the core.
/// </para>
/// <para>
/// <b>Implementations:</b>
/// - <c>AwsProvider:</c> AWS SDK for .NET (EC2, EBS, S3, CloudWatch)
/// - <c>AzureProvider:</c> Azure SDK (ResourceManager, Compute, Blob, Monitor)
/// - <c>GcpProvider:</c> Google Cloud Client Libraries (Compute, Disk, Storage, Monitoring)
/// </para>
/// <para>
/// <b>Dynamic Loading:</b>
/// Cloud SDKs are loaded dynamically via <c>PluginAssemblyLoadContext</c> to avoid bloating
/// the base DataWarehouse installation. Users only download SDKs for their target cloud.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 37: Cloud provider abstraction (ENV-04)")]
public interface ICloudProvider : IDisposable
{
    /// <summary>
    /// Gets the cloud provider name ("AWS", "Azure", "GCP").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Provisions a VM instance.
    /// </summary>
    /// <param name="spec">VM specification (instance type, image, region).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Instance ID of the provisioned VM.</returns>
    /// <remarks>
    /// Waits for VM to reach "running" state before returning.
    /// Uses exponential backoff for rate limiting.
    /// </remarks>
    Task<string> ProvisionVmAsync(VmSpec spec, CancellationToken ct = default);

    /// <summary>
    /// Provisions storage (block volume or object bucket).
    /// </summary>
    /// <param name="spec">Storage specification (type, size, performance tier).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Resource ID of the provisioned storage (volume ID or bucket name).</returns>
    Task<string> ProvisionStorageAsync(StorageSpec spec, CancellationToken ct = default);

    /// <summary>
    /// Deprovisions a resource by ID.
    /// </summary>
    /// <param name="resourceId">Resource ID to deprovision (instance ID, volume ID, bucket name).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if deprovisioning succeeded, false if resource not found.</returns>
    /// <remarks>
    /// Idempotent operation: returns false if resource already deleted.
    /// </remarks>
    Task<bool> DeprovisionAsync(string resourceId, CancellationToken ct = default);

    /// <summary>
    /// Gets current resource metrics (CPU, memory, storage utilization).
    /// </summary>
    /// <param name="resourceId">Resource ID to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Metrics for the resource, or null if not found or metrics unavailable.</returns>
    /// <remarks>
    /// Used by auto-scaling to make provisioning/deprovisioning decisions.
    /// Aggregates metrics from last 5 minutes.
    /// </remarks>
    Task<CloudResourceMetrics?> GetMetricsAsync(string resourceId, CancellationToken ct = default);

    /// <summary>
    /// Lists all DataWarehouse-managed resources (tagged appropriately).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of resource IDs managed by DataWarehouse.</returns>
    /// <remarks>
    /// Filters resources by tag: { "ManagedBy": "DataWarehouse" }.
    /// </remarks>
    Task<IReadOnlyList<string>> ListManagedResourcesAsync(CancellationToken ct = default);
}
