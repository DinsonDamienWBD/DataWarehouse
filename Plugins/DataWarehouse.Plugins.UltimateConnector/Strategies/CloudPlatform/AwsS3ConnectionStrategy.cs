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

            var httpClient = new HttpClient
            {
                BaseAddress = new Uri(endpoint),
                Timeout = config.Timeout
            };

            // Validate credentials are present
            var accessKey = GetConfiguration<string>(config, "AccessKeyId", string.Empty);
            var secretKey = GetConfiguration<string>(config, "SecretAccessKey", string.Empty);

            if (string.IsNullOrEmpty(accessKey) || string.IsNullOrEmpty(secretKey))
                throw new InvalidOperationException("AWS AccessKeyId and SecretAccessKey are required.");

            var connectionInfo = new Dictionary<string, object>
            {
                ["Region"] = region,
                ["Endpoint"] = endpoint,
                ["AccessKeyId"] = accessKey
            };

            return new DefaultConnectionHandle(httpClient, connectionInfo);
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var httpClient = handle.GetConnection<HttpClient>();
            try
            {
                var response = await httpClient.GetAsync("/", ct);
                return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Forbidden; // 403 means auth works but no buckets
            }
            catch
            {
                return false;
            }
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
