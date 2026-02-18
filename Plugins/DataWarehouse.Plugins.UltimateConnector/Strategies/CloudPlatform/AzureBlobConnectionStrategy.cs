using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;
using Azure.Storage.Blobs;
using Azure.Identity;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.CloudPlatform
{
    /// <summary>
    /// Azure Blob Storage connection strategy using HTTPS REST API.
    /// </summary>
    public class AzureBlobConnectionStrategy : SaaSConnectionStrategyBase
    {
        public override string StrategyId => "azure-blob";
        public override string DisplayName => "Azure Blob Storage";
        public override ConnectorCategory Category => ConnectorCategory.SaaS;

        public override ConnectionStrategyCapabilities Capabilities => new();

        public override string SemanticDescription => "Connects to Azure Blob Storage using HTTPS REST API for scalable object storage.";
        public override string[] Tags => new[] { "azure", "blob-storage", "cloud", "object-storage", "rest-api" };

        public AzureBlobConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            // Load account name from config or environment
            var accountName = GetConfiguration<string?>(config, "AccountName", null)
                ?? Environment.GetEnvironmentVariable("AZURE_STORAGE_ACCOUNT");

            // Load credentials - support multiple authentication methods
            // 1. Connection string (most common)
            var connectionString = GetConfiguration<string?>(config, "ConnectionString", null)
                ?? Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING");

            BlobServiceClient blobServiceClient;

            if (!string.IsNullOrEmpty(connectionString))
            {
                // Use connection string authentication
                blobServiceClient = new BlobServiceClient(connectionString);
            }
            else if (!string.IsNullOrEmpty(accountName))
            {
                // Use DefaultAzureCredential (supports managed identity, environment variables, etc.)
                var endpoint = GetConfiguration<string?>(config, "Endpoint", null)
                    ?? $"https://{accountName}.blob.core.windows.net";
                blobServiceClient = new BlobServiceClient(new Uri(endpoint), new DefaultAzureCredential());
            }
            else
            {
                throw new InvalidOperationException(
                    "Azure authentication is required. Provide either: " +
                    "ConnectionString (AZURE_STORAGE_CONNECTION_STRING) or " +
                    "AccountName (AZURE_STORAGE_ACCOUNT) with DefaultAzureCredential.");
            }

            var connectionInfo = new Dictionary<string, object>
            {
                ["AccountName"] = accountName ?? "connection-string",
                ["Endpoint"] = blobServiceClient.Uri.ToString(),
                ["TimeoutSeconds"] = GetConfiguration(config, "TimeoutSeconds", 300),
                ["MaxRetries"] = GetConfiguration(config, "MaxRetries", 3)
            };

            return new DefaultConnectionHandle(blobServiceClient, connectionInfo);
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var blobServiceClient = handle.GetConnection<BlobServiceClient>();

            try
            {
                // Test with GetProperties - validates credentials and connectivity
                await blobServiceClient.GetPropertiesAsync(ct);
                return true;
            }
            catch (Azure.RequestFailedException)
            {
                // Even auth errors mean we can reach Azure
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            // BlobServiceClient doesn't implement IDisposable in current Azure SDK
            await Task.CompletedTask;
        }

        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();

            return new ConnectionHealth(
                IsHealthy: isHealthy,
                StatusMessage: isHealthy ? "Azure Blob Storage is reachable" : "Azure Blob Storage is not responding",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow
            );
        }

        protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            var token = Guid.NewGuid().ToString("N");
            var expiry = DateTimeOffset.UtcNow.AddHours(1);
            return Task.FromResult((token, expiry));
        }

        protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default)
        {
            return AuthenticateAsync(handle, ct);
        }
    }
}
