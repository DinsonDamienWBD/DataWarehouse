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
    /// AWS Kinesis connection strategy using HTTPS REST API.
    /// </summary>
    public class AwsKinesisConnectionStrategy : SaaSConnectionStrategyBase
    {
        public override string StrategyId => "aws-kinesis";
        public override string DisplayName => "AWS Kinesis";
        public override ConnectorCategory Category => ConnectorCategory.SaaS;

        public override ConnectionStrategyCapabilities Capabilities => new();

        public override string SemanticDescription => "Connects to AWS Kinesis data streaming service using HTTPS REST API for real-time data ingestion and processing.";
        public override string[] Tags => new[] { "aws", "kinesis", "streaming", "realtime", "rest-api" };

        public AwsKinesisConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var region = GetConfiguration(config, "Region", "us-east-1");
            var endpoint = $"https://kinesis.{region}.amazonaws.com";

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
                var response = await httpClient.PostAsync("/", new StringContent("{\"StreamName\":\"test\"}", System.Text.Encoding.UTF8, "application/x-amz-json-1.1"), ct);
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
                StatusMessage: isHealthy ? "AWS Kinesis API is reachable" : "AWS Kinesis API is not responding",
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
