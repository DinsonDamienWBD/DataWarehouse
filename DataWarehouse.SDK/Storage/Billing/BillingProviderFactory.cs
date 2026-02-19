using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Storage.Billing;

/// <summary>
/// Factory for creating billing providers from configuration. Supports AWS, Azure, and GCP
/// with credential resolution from explicit configuration or environment variables.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Billing API integration")]
public static class BillingProviderFactory
{
    /// <summary>
    /// Creates a billing provider for the specified cloud provider.
    /// Credentials are loaded from environment variables if not provided in config.
    /// </summary>
    /// <param name="provider">The cloud provider to create a billing integration for.</param>
    /// <param name="httpClient">Optional HTTP client (a new one is created if null).</param>
    /// <param name="config">Optional configuration dictionary with provider-specific keys.</param>
    /// <returns>A configured billing provider instance.</returns>
    /// <exception cref="NotSupportedException">Thrown when the provider is not supported.</exception>
    /// <exception cref="InvalidOperationException">Thrown when required credentials are missing.</exception>
    public static IBillingProvider Create(
        CloudProvider provider,
        HttpClient? httpClient = null,
        IDictionary<string, string>? config = null)
    {
        var client = httpClient ?? new HttpClient();
        config ??= new Dictionary<string, string>();

        return provider switch
        {
            CloudProvider.AWS => new AwsCostExplorerProvider(
                client,
                GetConfig(config, "AccessKeyId", "AWS_ACCESS_KEY_ID"),
                GetConfig(config, "SecretAccessKey", "AWS_SECRET_ACCESS_KEY"),
                GetConfigOptional(config, "Region", "AWS_REGION") ?? "us-east-1"),

            CloudProvider.Azure => new AzureCostManagementProvider(
                client,
                GetConfig(config, "TenantId", "AZURE_TENANT_ID"),
                GetConfig(config, "ClientId", "AZURE_CLIENT_ID"),
                GetConfig(config, "ClientSecret", "AZURE_CLIENT_SECRET"),
                GetConfig(config, "SubscriptionId", "AZURE_SUBSCRIPTION_ID")),

            CloudProvider.GCP => new GcpBillingProvider(
                client,
                GetConfig(config, "ServiceAccountJson", "GOOGLE_APPLICATION_CREDENTIALS"),
                GetConfig(config, "ProjectId", "GCP_PROJECT_ID")),

            _ => throw new NotSupportedException($"Billing provider not available for {provider}")
        };
    }

    /// <summary>
    /// Creates all available billing providers by probing each cloud provider for valid credentials.
    /// Providers without valid credentials are silently skipped.
    /// </summary>
    /// <param name="httpClient">Optional HTTP client shared across providers.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of billing providers with valid credentials.</returns>
    public static async Task<IReadOnlyList<IBillingProvider>> CreateAllAvailableAsync(
        HttpClient? httpClient = null,
        CancellationToken ct = default)
    {
        var providers = new List<IBillingProvider>();
        foreach (var cloudProvider in new[] { CloudProvider.AWS, CloudProvider.Azure, CloudProvider.GCP })
        {
            try
            {
                var provider = Create(cloudProvider, httpClient);
                if (await provider.ValidateCredentialsAsync(ct).ConfigureAwait(false))
                    providers.Add(provider);
            }
            catch
            {
                // Skip providers without credentials
            }
        }
        return providers;
    }

    /// <summary>
    /// Gets a required configuration value from the config dictionary or environment variable.
    /// For GCP service account credentials, reads file content if the env var points to a file path.
    /// </summary>
    private static string GetConfig(IDictionary<string, string> config, string key, string envVar)
    {
        if (config.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            return value;

        var envValue = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            // For GCP, if GOOGLE_APPLICATION_CREDENTIALS points to a file, read it
            if (envVar == "GOOGLE_APPLICATION_CREDENTIALS" && File.Exists(envValue))
                return File.ReadAllText(envValue);
            return envValue;
        }

        throw new InvalidOperationException(
            $"Billing configuration '{key}' not found. Set via config or environment variable '{envVar}'.");
    }

    /// <summary>
    /// Gets an optional configuration value from the config dictionary or environment variable.
    /// Returns null if neither source provides a value.
    /// </summary>
    private static string? GetConfigOptional(IDictionary<string, string> config, string key, string envVar)
    {
        if (config.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            return value;
        return Environment.GetEnvironmentVariable(envVar);
    }
}
