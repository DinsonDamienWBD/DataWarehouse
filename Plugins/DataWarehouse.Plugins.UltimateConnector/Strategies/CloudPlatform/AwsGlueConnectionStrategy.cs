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
    /// AWS Glue connection strategy using HTTPS REST API.
    /// </summary>
    public class AwsGlueConnectionStrategy : SaaSConnectionStrategyBase
    {
        public override string StrategyId => "aws-glue";
        public override string DisplayName => "AWS Glue";
        public override ConnectorCategory Category => ConnectorCategory.SaaS;

        public override ConnectionStrategyCapabilities Capabilities => new();

        public override string SemanticDescription => "Connects to AWS Glue ETL and data catalog service using HTTPS REST API for data integration workflows.";
        public override string[] Tags => new[] { "aws", "glue", "etl", "data-catalog", "rest-api" };

        public AwsGlueConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var region = GetConfiguration(config, "Region", "us-east-1");
            var endpoint = $"https://glue.{region}.amazonaws.com";

            var httpClient = new HttpClient
            {
                BaseAddress = new Uri(endpoint),
                Timeout = config.Timeout
            };

            var connectionInfo = new Dictionary<string, object>
            {
                ["Region"] = region,
                ["Endpoint"] = endpoint
            };

            return new DefaultConnectionHandle(httpClient, connectionInfo);
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var httpClient = handle.GetConnection<HttpClient>();
            try
            {
                var response = await httpClient.PostAsync("/", new StringContent("{}", System.Text.Encoding.UTF8, "application/x-amz-json-1.1"), ct);
                return response.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable;
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
                StatusMessage: isHealthy ? "AWS Glue API is reachable" : "AWS Glue API is not responding",
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
