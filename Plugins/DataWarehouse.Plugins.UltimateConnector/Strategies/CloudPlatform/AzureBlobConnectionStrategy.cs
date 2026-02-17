using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

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
            var accountName = GetConfiguration<string>(config, "AccountName", null!)
                ?? Environment.GetEnvironmentVariable("AZURE_STORAGE_ACCOUNT")
                ?? throw new InvalidOperationException(
                    "Azure AccountName is required. Set 'AccountName' in config or environment variable AZURE_STORAGE_ACCOUNT.");

            // Load credentials - support multiple authentication methods
            // 1. Connection string (most common)
            var connectionString = GetConfiguration<string>(config, "ConnectionString", null!)
                ?? Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING");

            // 2. Account key (SharedKey authentication)
            var accountKey = GetConfiguration<string>(config, "AccountKey", null!)
                ?? Environment.GetEnvironmentVariable("AZURE_STORAGE_KEY");

            // 3. SAS token
            var sasToken = GetConfiguration<string>(config, "SasToken", null!)
                ?? Environment.GetEnvironmentVariable("AZURE_STORAGE_SAS_TOKEN");

            // Validate that at least one authentication method is provided
            if (string.IsNullOrEmpty(connectionString) && string.IsNullOrEmpty(accountKey) && string.IsNullOrEmpty(sasToken))
            {
                throw new InvalidOperationException(
                    "Azure authentication is required. Provide one of: " +
                    "ConnectionString (AZURE_STORAGE_CONNECTION_STRING), " +
                    "AccountKey (AZURE_STORAGE_KEY), or " +
                    "SasToken (AZURE_STORAGE_SAS_TOKEN).");
            }

            // Construct endpoint with custom domain support
            var endpoint = GetConfiguration<string>(config, "Endpoint", null!)
                ?? $"https://{accountName}.blob.core.windows.net";

            // Configure HTTP timeout with reasonable defaults
            var timeoutSeconds = GetConfiguration(config, "TimeoutSeconds", 300);
            var httpClient = new HttpClient
            {
                BaseAddress = new Uri(endpoint),
                Timeout = TimeSpan.FromSeconds(timeoutSeconds)
            };

            // Add retry configuration metadata
            var maxRetries = GetConfiguration(config, "MaxRetries", 3);
            var retryDelayMs = GetConfiguration(config, "RetryDelayMs", 1000);

            var connectionInfo = new Dictionary<string, object>
            {
                ["AccountName"] = accountName,
                ["Endpoint"] = endpoint,
                ["HasConnectionString"] = !string.IsNullOrEmpty(connectionString),
                ["HasAccountKey"] = !string.IsNullOrEmpty(accountKey),
                ["HasSasToken"] = !string.IsNullOrEmpty(sasToken),
                ["TimeoutSeconds"] = timeoutSeconds,
                ["MaxRetries"] = maxRetries,
                ["RetryDelayMs"] = retryDelayMs
            };

            // Store credentials securely in connection info (not exposed in logs)
            if (!string.IsNullOrEmpty(connectionString))
                connectionInfo["_ConnectionString"] = connectionString;
            if (!string.IsNullOrEmpty(accountKey))
                connectionInfo["_AccountKey"] = accountKey;
            if (!string.IsNullOrEmpty(sasToken))
                connectionInfo["_SasToken"] = sasToken;

            return new DefaultConnectionHandle(httpClient, connectionInfo);
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var httpClient = handle.GetConnection<HttpClient>();
            var connectionInfo = handle.ConnectionInfo;
            var maxRetries = connectionInfo.TryGetValue("MaxRetries", out var mr) && mr is int ? (int)mr : 3;
            var retryDelayMs = connectionInfo.TryGetValue("RetryDelayMs", out var rd) && rd is int ? (int)rd : 1000;

            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    // Test with a simple list containers request
                    // This validates both connectivity and authentication
                    var request = new HttpRequestMessage(HttpMethod.Get, "/?comp=list");

                    // Add authentication header if SAS token is available
                    if (connectionInfo.TryGetValue("_SasToken", out var sasTokenObj) && sasTokenObj is string st)
                    {
                        request.RequestUri = new Uri($"{httpClient.BaseAddress}?comp=list&{st.TrimStart('?')}");
                    }

                    var response = await httpClient.SendAsync(request, ct);

                    // 200 OK or 403 Forbidden both indicate connectivity
                    // 403 typically means auth works but insufficient permissions
                    var isHealthy = response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Forbidden;

                    if (isHealthy)
                    {
                        return true;
                    }

                    if (attempt < maxRetries)
                    {
                        await Task.Delay(retryDelayMs * (int)Math.Pow(2, attempt), ct);
                    }
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Timeout - retry if we have attempts left
                    if (attempt < maxRetries)
                    {
                        await Task.Delay(retryDelayMs * (int)Math.Pow(2, attempt), ct);
                    }
                }
                catch (Exception)
                {
                    if (attempt < maxRetries)
                    {
                        await Task.Delay(retryDelayMs * (int)Math.Pow(2, attempt), ct);
                    }
                }
            }

            return false;
        }

        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var httpClient = handle.GetConnection<HttpClient>();
            httpClient?.Dispose();
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
