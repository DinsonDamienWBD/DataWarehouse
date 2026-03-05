using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Deployment;

/// <summary>
/// Detects hyperscale cloud provider (AWS, Azure, GCP) via metadata endpoints.
/// Uses standard cloud-init pattern (169.254.169.254) with 500ms timeout.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cloud Metadata Endpoints:</b>
/// - AWS: http://169.254.169.254/latest/meta-data/ (no headers)
/// - Azure: http://169.254.169.254/metadata/instance?api-version=2021-02-01 (Metadata: true header)
/// - GCP: http://metadata.google.internal/computeMetadata/v1/ (Metadata-Flavor: Google header)
/// </para>
/// <para>
/// <b>Timeout Strategy:</b>
/// 500ms timeout prevents 30+ second hangs on non-cloud systems. Cloud metadata responds in <10ms on actual cloud VMs.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 37: Cloud provider detection (ENV-04)")]
public sealed class CloudDetector : IDeploymentDetector
{
    private static readonly HttpClient SharedMetadataClient = new() { Timeout = TimeSpan.FromMilliseconds(500) };
    private const string AwsMetadataUrl = "http://169.254.169.254/latest/meta-data/";
    private const string AzureMetadataUrl = "http://169.254.169.254/metadata/instance?api-version=2021-02-01";
    private const string GcpMetadataUrl = "http://metadata.google.internal/computeMetadata/v1/";

    /// <summary>
    /// Detects cloud provider via metadata endpoint HTTP requests.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="DeploymentContext"/> with <see cref="DeploymentEnvironment.HyperscaleCloud"/> if detected.
    /// Returns null if not running in a cloud environment.
    /// </returns>
    public async Task<DeploymentContext?> DetectAsync(CancellationToken ct = default)
    {
        // Reuse static HttpClient to avoid socket exhaustion
        var client = SharedMetadataClient;

        // Try AWS detection first (most common)
        var awsContext = await TryDetectAwsAsync(client, ct);
        if (awsContext != null) return awsContext;

        // Try Azure detection
        var azureContext = await TryDetectAzureAsync(client, ct);
        if (azureContext != null) return azureContext;

        // Try GCP detection
        var gcpContext = await TryDetectGcpAsync(client, ct);
        if (gcpContext != null) return gcpContext;

        // Not a cloud environment
        return null;
    }

    private async Task<DeploymentContext?> TryDetectAwsAsync(HttpClient client, CancellationToken ct)
    {
        try
        {
            var instanceId = await client.GetStringAsync($"{AwsMetadataUrl}instance-id", ct);

            if (string.IsNullOrWhiteSpace(instanceId))
                return null;

            // Fetch additional metadata
            var region = await FetchAwsMetadataAsync(client, "placement/region", ct);
            var instanceType = await FetchAwsMetadataAsync(client, "instance-type", ct);

            return new DeploymentContext
            {
                Environment = DeploymentEnvironment.HyperscaleCloud,
                CloudProvider = "AWS",
                Metadata = new Dictionary<string, string>
                {
                    ["InstanceId"] = instanceId,
                    ["Region"] = region ?? "unknown",
                    ["InstanceType"] = instanceType ?? "unknown"
                }
            };
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
    }

    private async Task<DeploymentContext?> TryDetectAzureAsync(HttpClient client, CancellationToken ct)
    {
        try
        {
            // Azure requires "Metadata: true" header
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("Metadata", "true");

            var response = await client.GetStringAsync(AzureMetadataUrl, ct);

            if (string.IsNullOrWhiteSpace(response))
                return null;

            // Parse JSON response
            using var json = JsonDocument.Parse(response);
            var compute = json.RootElement.GetProperty("compute");

            var vmId = compute.GetProperty("vmId").GetString() ?? "unknown";
            var location = compute.GetProperty("location").GetString() ?? "unknown";
            var vmSize = compute.GetProperty("vmSize").GetString() ?? "unknown";

            return new DeploymentContext
            {
                Environment = DeploymentEnvironment.HyperscaleCloud,
                CloudProvider = "Azure",
                Metadata = new Dictionary<string, string>
                {
                    ["VmId"] = vmId,
                    ["Location"] = location,
                    ["VmSize"] = vmSize
                }
            };
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<DeploymentContext?> TryDetectGcpAsync(HttpClient client, CancellationToken ct)
    {
        try
        {
            // GCP requires "Metadata-Flavor: Google" header
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("Metadata-Flavor", "Google");

            var instanceId = await client.GetStringAsync($"{GcpMetadataUrl}instance/id", ct);

            if (string.IsNullOrWhiteSpace(instanceId))
                return null;

            // Fetch additional metadata
            var zone = await FetchGcpMetadataAsync(client, "instance/zone", ct);
            var machineType = await FetchGcpMetadataAsync(client, "instance/machine-type", ct);

            return new DeploymentContext
            {
                Environment = DeploymentEnvironment.HyperscaleCloud,
                CloudProvider = "GCP",
                Metadata = new Dictionary<string, string>
                {
                    ["InstanceId"] = instanceId,
                    ["Zone"] = zone ?? "unknown",
                    ["MachineType"] = machineType ?? "unknown"
                }
            };
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
    }

    private async Task<string?> FetchAwsMetadataAsync(HttpClient client, string path, CancellationToken ct)
    {
        try
        {
            return await client.GetStringAsync($"{AwsMetadataUrl}{path}", ct);
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> FetchGcpMetadataAsync(HttpClient client, string path, CancellationToken ct)
    {
        try
        {
            return await client.GetStringAsync($"{GcpMetadataUrl}{path}", ct);
        }
        catch
        {
            return null;
        }
    }
}
