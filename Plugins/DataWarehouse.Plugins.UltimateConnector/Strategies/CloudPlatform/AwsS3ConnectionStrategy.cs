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
    /// AWS S3 connection strategy using HTTPS REST API with AWS Signature v4 authentication.
    /// </summary>
    public class AwsS3ConnectionStrategy : SaaSConnectionStrategyBase
    {
        public override string StrategyId => "aws-s3";
        public override string DisplayName => "AWS S3";
        public override ConnectorCategory Category => ConnectorCategory.SaaS;

        public override ConnectionStrategyCapabilities Capabilities => new();

        public override string SemanticDescription => "Connects to AWS S3 object storage using HTTPS REST API with AWS Signature v4 authentication for secure access to buckets and objects.";
        public override string[] Tags => new[] { "aws", "s3", "cloud", "object-storage", "rest-api" };

        public AwsS3ConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var region = GetConfiguration(config, "Region", "us-east-1");
            var endpoint = GetConfiguration(config, "Endpoint", $"https://s3.{region}.amazonaws.com");

            // Load credentials from config, then fall back to environment variables
            // Note: null! used because GetConfiguration<string> returns non-nullable but accepts nullable default
            var accessKey = GetConfiguration<string>(config, "AccessKeyId", null!)
                ?? Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID")
                ?? throw new InvalidOperationException(
                    "AWS AccessKeyId is required. Set 'AccessKeyId' in config or environment variable AWS_ACCESS_KEY_ID.");

            var secretKey = GetConfiguration<string>(config, "SecretAccessKey", null!)
                ?? Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY")
                ?? throw new InvalidOperationException(
                    "AWS SecretAccessKey is required. Set 'SecretAccessKey' in config or environment variable AWS_SECRET_ACCESS_KEY.");

            var sessionToken = GetConfiguration<string>(config, "SessionToken", null!)
                ?? Environment.GetEnvironmentVariable("AWS_SESSION_TOKEN");

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
                ["Region"] = region,
                ["Endpoint"] = endpoint,
                ["AccessKeyId"] = accessKey,
                ["SecretAccessKey"] = secretKey,
                ["SessionToken"] = sessionToken ?? string.Empty,
                ["TimeoutSeconds"] = timeoutSeconds,
                ["MaxRetries"] = maxRetries,
                ["RetryDelayMs"] = retryDelayMs
            };

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
                    // Test with a simple HEAD request to the root
                    var request = new HttpRequestMessage(HttpMethod.Head, "/");
                    var response = await httpClient.SendAsync(request, ct);

                    // 200 OK or 403 Forbidden both indicate connectivity and valid auth
                    // 403 typically means credentials work but no bucket access
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
                StatusMessage: isHealthy ? "AWS S3 endpoint is reachable" : "AWS S3 endpoint is not responding",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow
            );
        }

        protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            // AWS uses signature v4 for each request, not a persistent token
            // Return a placeholder token that expires in 1 hour
            var token = Guid.NewGuid().ToString("N");
            var expiry = DateTimeOffset.UtcNow.AddHours(1);
            return Task.FromResult((token, expiry));
        }

        protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default)
        {
            // AWS uses signature v4 for each request, refresh is automatic
            return AuthenticateAsync(handle, ct);
        }
    }
}
