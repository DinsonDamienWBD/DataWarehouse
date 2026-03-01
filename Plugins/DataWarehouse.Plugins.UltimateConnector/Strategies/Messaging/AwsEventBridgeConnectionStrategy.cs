using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
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
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { try { var response = await handle.GetConnection<HttpClient>().PostAsync("/", new StringContent("{}", System.Text.Encoding.UTF8, "application/x-amz-json-1.1"), ct); return response.IsSuccessStatusCode; } catch { return false; } }
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>()?.Dispose(); await Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "AWS EventBridge is reachable" : "AWS EventBridge is not responding", sw.Elapsed, DateTimeOffset.UtcNow); }

        public override async Task PublishAsync(IConnectionHandle handle, string topic, byte[] message, Dictionary<string, string>? headers = null, CancellationToken ct = default)
        {
            var httpClient = handle.GetConnection<HttpClient>();
            if (httpClient == null)
                throw new InvalidOperationException("AWS EventBridge connection is not established");
            // EventBridge PutEvents API
            var eventBusName = topic.Contains("/") ? topic.Split('/')[^1] : topic;
            var source = headers?.GetValueOrDefault("Source") ?? "datawarehouse.connector";
            var detailType = headers?.GetValueOrDefault("DetailType") ?? "DataWarehouseEvent";
            // Finding 2023: Binary data embedded via UTF-8 decoding can corrupt the payload.
            // Attempt to parse as JSON; fall back to base64 for binary payloads.
            string detail;
            try
            {
                var decoded = Encoding.UTF8.GetString(message);
                using var _ = System.Text.Json.JsonDocument.Parse(decoded);
                detail = decoded; // valid JSON
            }
            catch
            {
                detail = Convert.ToBase64String(message); // binary — base64 encode
            }
            var requestBody = new
            {
                Entries = new[]
                {
                    new
                    {
                        EventBusName = eventBusName,
                        Source = source,
                        DetailType = detailType,
                        Detail = detail,
                        Time = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    }
                }
            };
            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/x-amz-json-1.1");
            content.Headers.Add("X-Amz-Target", "AWSEvents.PutEvents");
            using var response = await httpClient.PostAsync("/", content, ct);
            response.EnsureSuccessStatusCode();
        }

        public override IAsyncEnumerable<byte[]> SubscribeAsync(IConnectionHandle handle, string topic, string? consumerGroup = null, CancellationToken ct = default)
        {
            // Finding 2024: SubscribeAsync must throw NotSupportedException rather than yield break
            // after a 5s delay. EventBridge is push-based; configure SQS as a target and use
            // the AwsSqsConnectionStrategy for pull-based consumption.
            throw new NotSupportedException(
                "AWS EventBridge does not support pull-based subscriptions. " +
                "Configure an SQS queue as a rule target and use AwsSqsConnectionStrategy to consume events.");
        }
    }
}
