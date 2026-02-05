using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Messaging
{
    public class AwsEventBridgeConnectionStrategy : MessagingConnectionStrategyBase
    {
        public override string StrategyId => "aws-eventbridge";
        public override string DisplayName => "AWS EventBridge";
        public override ConnectorCategory Category => ConnectorCategory.Messaging;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to AWS EventBridge using HTTPS REST API for serverless event bus.";
        public override string[] Tags => new[] { "aws", "eventbridge", "events", "rest-api", "serverless" };
        public AwsEventBridgeConnectionStrategy(ILogger? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct) { var region = GetConfiguration(config, "Region", "us-east-1"); var endpoint = $"https://events.{region}.amazonaws.com"; var httpClient = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = config.Timeout }; return new DefaultConnectionHandle(httpClient, new Dictionary<string, object> { ["Region"] = region, ["Endpoint"] = endpoint }); }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { try { var response = await handle.GetConnection<HttpClient>().PostAsync("/", new StringContent("{}", System.Text.Encoding.UTF8, "application/x-amz-json-1.1"), ct); return response.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable; } catch { return false; } }
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>()?.Dispose(); await Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "AWS EventBridge is reachable" : "AWS EventBridge is not responding", sw.Elapsed, DateTimeOffset.UtcNow); }
        public override Task PublishAsync(IConnectionHandle handle, string topic, byte[] message, Dictionary<string, string>? headers = null, CancellationToken ct = default) => throw new NotImplementedException("AWS EventBridge publish requires REST API calls");
        public override IAsyncEnumerable<byte[]> SubscribeAsync(IConnectionHandle handle, string topic, string? consumerGroup = null, CancellationToken ct = default) => throw new NotImplementedException("AWS EventBridge subscribe requires rule configuration");
    }
}
