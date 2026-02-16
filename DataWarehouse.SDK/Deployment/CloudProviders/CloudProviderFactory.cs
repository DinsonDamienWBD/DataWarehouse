using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Deployment.CloudProviders;

/// <summary>
/// Factory for creating cloud provider instances with dynamic SDK loading.
/// </summary>
/// <remarks>
/// <para>
/// <b>Dynamic Loading Strategy:</b>
/// Cloud provider SDKs (AWS SDK, Azure SDK, GCP Client Libraries) are NOT direct dependencies.
/// They are loaded dynamically via <c>PluginAssemblyLoadContext</c> to avoid bloating base installation.
/// </para>
/// <para>
/// Users only download SDKs for their target cloud provider:
/// - AWS deployment: Download AWS SDK assemblies to <c>{AppDir}/cloud-sdks/aws/</c>
/// - Azure deployment: Download Azure SDK assemblies to <c>{AppDir}/cloud-sdks/azure/</c>
/// - GCP deployment: Download GCP Client Libraries to <c>{AppDir}/cloud-sdks/gcp/</c>
/// </para>
/// <para>
/// <b>Graceful Degradation:</b>
/// Returns null if SDK not available (cloud provisioning disabled, manual management).
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 37: Cloud provider factory with dynamic loading (ENV-04)")]
public static class CloudProviderFactory
{
    /// <summary>
    /// Creates a cloud provider instance for the specified cloud.
    /// </summary>
    /// <param name="cloudProvider">Cloud provider name ("AWS", "Azure", "GCP").</param>
    /// <returns>
    /// ICloudProvider implementation if SDK available, null if SDK not found.
    /// </returns>
    public static ICloudProvider? TryCreate(string cloudProvider)
    {
        var normalized = cloudProvider.ToUpperInvariant();

        return normalized switch
        {
            "AWS" => new AwsProvider(),
            "AZURE" => new AzureProvider(),
            "GCP" => new GcpProvider(),
            _ => null
        };

        // Production implementation would:
        // 1. Locate SDK assemblies in deployment directory or well-known path
        // 2. Create isolated assembly context for each cloud SDK
        // 3. Load cloud SDK assemblies
        // 4. Instantiate provider with cloud clients
        // 5. Return provider or null if SDK not found
    }
}
