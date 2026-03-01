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
    public class GooglePubSubConnectionStrategy : MessagingConnectionStrategyBase
    {
        public override string StrategyId => "google-pubsub";
        public override string DisplayName => "Google Pub/Sub";
        public override ConnectorCategory Category => ConnectorCategory.Messaging;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to Google Cloud Pub/Sub using HTTPS REST API for managed messaging and streaming.";
        public override string[] Tags => new[] { "gcp", "pubsub", "messaging", "rest-api", "managed" };
        public GooglePubSubConnectionStrategy(ILogger? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var projectId = GetConfiguration<string?>(config, "ProjectId", null);
            var httpClient = new HttpClient { BaseAddress = new Uri("https://pubsub.googleapis.com"), Timeout = config.Timeout };
            if (!string.IsNullOrEmpty(config.AuthCredential))
                httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.AuthCredential);
            return new DefaultConnectionHandle(httpClient, new Dictionary<string, object> { ["Endpoint"] = "https://pubsub.googleapis.com", ["ProjectId"] = projectId ?? "" });
        }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { try { var response = await handle.GetConnection<HttpClient>().GetAsync("/v1/projects", ct); return response.IsSuccessStatusCode; } catch { return false; } }
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>()?.Dispose(); return Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "Google Pub/Sub is reachable" : "Google Pub/Sub is not responding", sw.Elapsed, DateTimeOffset.UtcNow); }

        public override async Task PublishAsync(IConnectionHandle handle, string topic, byte[] message, Dictionary<string, string>? headers = null, CancellationToken ct = default)
        {
            var httpClient = handle.GetConnection<HttpClient>();
            if (httpClient == null)
                throw new InvalidOperationException("Google Pub/Sub connection is not established");
            var projectId = handle.ConnectionInfo.TryGetValue("ProjectId", out var pid) ? pid?.ToString() : "";
            // Pub/Sub publish API: POST /v1/projects/{project}/topics/{topic}:publish
            var publishUrl = $"/v1/{topic}:publish";
            if (!topic.StartsWith("projects/") && !string.IsNullOrEmpty(projectId))
                publishUrl = $"/v1/projects/{projectId}/topics/{topic}:publish";
            var base64Data = Convert.ToBase64String(message);
            var requestBody = new { messages = new[] { new { data = base64Data, attributes = headers ?? new Dictionary<string, string>() } } };
            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await httpClient.PostAsync(publishUrl, content, ct);
            response.EnsureSuccessStatusCode();
        }

        public override async IAsyncEnumerable<byte[]> SubscribeAsync(IConnectionHandle handle, string topic, string? consumerGroup = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var httpClient = handle.GetConnection<HttpClient>();
            if (httpClient == null)
                throw new InvalidOperationException("Google Pub/Sub connection is not established");
            var projectId = handle.ConnectionInfo.TryGetValue("ProjectId", out var pid) ? pid?.ToString() : "";
            var subscription = consumerGroup ?? $"{topic}-sub";
            // Pub/Sub pull API: POST /v1/projects/{project}/subscriptions/{subscription}:pull
            var pullUrl = $"/v1/{subscription}:pull";
            if (!subscription.StartsWith("projects/") && !string.IsNullOrEmpty(projectId))
                pullUrl = $"/v1/projects/{projectId}/subscriptions/{subscription}:pull";
            // Finding 2026: Cache the serialized bytes to avoid re-serializing the constant body on every poll.
            // HttpClient disposes each HttpContent after sending so we must create a new instance per request,
            // but we avoid the JSON serialization overhead by reusing the pre-computed byte array.
            var pullBodyBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { maxMessages = 10 }));
            var retryCount = 0;
            while (!ct.IsCancellationRequested)
            {
                List<byte[]>? collectedMessages = null;
                bool shouldBreak = false;
                try
                {
                    using var content = new ByteArrayContent(pullBodyBytes);
                    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                    using var response = await httpClient.PostAsync(pullUrl, content, ct);
                    if (response.IsSuccessStatusCode)
                    {
                        var responseJson = await response.Content.ReadAsStringAsync(ct);
                        using var doc = JsonDocument.Parse(responseJson);
                        if (doc.RootElement.TryGetProperty("receivedMessages", out var messages))
                        {
                            collectedMessages = new List<byte[]>();
                            foreach (var msg in messages.EnumerateArray())
                            {
                                if (msg.TryGetProperty("message", out var message) && message.TryGetProperty("data", out var data))
                                {
                                    var base64 = data.GetString();
                                    if (!string.IsNullOrEmpty(base64))
                                        try { collectedMessages.Add(Convert.FromBase64String(base64)); } catch (FormatException) { System.Diagnostics.Debug.WriteLine($"Malformed base64 in PubSub message, skipping"); }
                                }
                            }
                        }
                    }
                    // Adaptive back-off: yield quickly if messages received, back off when idle.
                    if (collectedMessages == null || collectedMessages.Count == 0)
                        await Task.Delay(Math.Min(100 * (1 << Math.Min(retryCount++, 4)), 1000), ct);
                    else
                        retryCount = 0;
                }
                catch (Exception) when (ct.IsCancellationRequested) { shouldBreak = true; }
                if (shouldBreak) break;
                if (collectedMessages != null)
                    foreach (var msg in collectedMessages)
                        yield return msg;
            }
        }
    }
}
