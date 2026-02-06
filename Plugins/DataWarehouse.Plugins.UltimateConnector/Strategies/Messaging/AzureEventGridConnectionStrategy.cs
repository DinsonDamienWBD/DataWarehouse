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
    public class AzureEventGridConnectionStrategy : MessagingConnectionStrategyBase
    {
        public override string StrategyId => "azure-eventgrid";
        public override string DisplayName => "Azure Event Grid";
        public override ConnectorCategory Category => ConnectorCategory.Messaging;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to Azure Event Grid using HTTPS REST API for event-driven architectures.";
        public override string[] Tags => new[] { "azure", "eventgrid", "events", "rest-api", "serverless" };
        public AzureEventGridConnectionStrategy(ILogger? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var topic = GetConfiguration<string>(config, "Topic", null!);
            var endpoint = $"https://{topic}.eventgrid.azure.net";
            var httpClient = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = config.Timeout };
            var sasKey = config.AuthCredential;
            if (!string.IsNullOrEmpty(sasKey))
                httpClient.DefaultRequestHeaders.Add("aeg-sas-key", sasKey);
            return new DefaultConnectionHandle(httpClient, new Dictionary<string, object> { ["Topic"] = topic, ["Endpoint"] = endpoint });
        }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { try { var response = await handle.GetConnection<HttpClient>().GetAsync("/api/events", ct); return response.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable; } catch { return false; } }
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>()?.Dispose(); await Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "Azure Event Grid is reachable" : "Azure Event Grid is not responding", sw.Elapsed, DateTimeOffset.UtcNow); }

        public override async Task PublishAsync(IConnectionHandle handle, string topic, byte[] message, Dictionary<string, string>? headers = null, CancellationToken ct = default)
        {
            var httpClient = handle.GetConnection<HttpClient>();
            if (httpClient == null)
                throw new InvalidOperationException("Azure Event Grid connection is not established");
            // Event Grid publish API - array of CloudEvents or EventGridEvents
            var eventType = headers?.GetValueOrDefault("EventType") ?? "DataWarehouse.Event";
            var subject = headers?.GetValueOrDefault("Subject") ?? topic;
            var dataContentType = headers?.GetValueOrDefault("DataContentType") ?? "application/json";
            var events = new[]
            {
                new
                {
                    id = Guid.NewGuid().ToString(),
                    eventType = eventType,
                    subject = subject,
                    eventTime = DateTimeOffset.UtcNow.ToString("o"),
                    dataVersion = "1.0",
                    data = Encoding.UTF8.GetString(message)
                }
            };
            var json = JsonSerializer.Serialize(events);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync("/api/events", content, ct);
            response.EnsureSuccessStatusCode();
        }

        public override async IAsyncEnumerable<byte[]> SubscribeAsync(IConnectionHandle handle, string topic, string? consumerGroup = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            // Azure Event Grid is push-based and delivers events to webhooks/endpoints
            // Direct pull subscription is not supported via REST API
            // To receive events, configure an Event Grid subscription with a webhook or Azure Function
            // This implementation indicates the limitation and yields nothing
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(5000, ct);
                // Event Grid requires webhook endpoints for event delivery
                // Use Azure Service Bus or Storage Queue as dead-letter for pull-based consumption
                yield break;
            }
        }
    }
}
