using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.SaaS
{
    /// <summary>
    /// Slack Web API connection strategy with full messaging integration.
    /// Supports posting messages, channel management, file upload, and interactive messages.
    /// </summary>
    public class SlackConnectionStrategy : SaaSConnectionStrategyBase
    {
        private string _botToken = "";
        private string _signingSecret = "";

        public override string StrategyId => "slack";
        public override string DisplayName => "Slack";
        public override ConnectorCategory Category => ConnectorCategory.SaaS;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to Slack using Web API with message posting, channel management, file uploads, and interactive messages.";
        public override string[] Tags => new[] { "slack", "collaboration", "messaging", "saas", "rest-api", "webhooks" };

        public SlackConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            _botToken = GetConfiguration<string>(config, "BotToken", "");
            _signingSecret = GetConfiguration<string>(config, "SigningSecret", "");

            var httpClient = new HttpClient
            {
                BaseAddress = new Uri("https://slack.com"),
                Timeout = config.Timeout
            };

            if (!string.IsNullOrEmpty(_botToken))
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _botToken);

            return new DefaultConnectionHandle(httpClient, new Dictionary<string, object>
            {
                ["Endpoint"] = "https://slack.com/api"
            });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            try
            {
                var response = await handle.GetConnection<HttpClient>().GetAsync("/api/api.test", ct);
                return response.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            handle.GetConnection<HttpClient>()?.Dispose();
            await Task.CompletedTask;
        }

        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();
            return new ConnectionHealth(isHealthy,
                isHealthy ? "Slack is reachable" : "Slack is not responding",
                sw.Elapsed, DateTimeOffset.UtcNow);
        }

        protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(
            IConnectionHandle handle, CancellationToken ct = default) =>
            Task.FromResult((_botToken, DateTimeOffset.UtcNow.AddHours(24)));

        protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(
            IConnectionHandle handle, string currentToken, CancellationToken ct = default) =>
            AuthenticateAsync(handle, ct);

        /// <summary>
        /// Posts a message to a Slack channel.
        /// </summary>
        public async Task<SlackMessageResult> PostMessageAsync(IConnectionHandle handle, string channel,
            string text, SlackBlock[]? blocks = null, string? threadTs = null, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            var body = new Dictionary<string, object> { ["channel"] = channel, ["text"] = text };
            if (blocks != null) body["blocks"] = blocks;
            if (threadTs != null) body["thread_ts"] = threadTs;

            var json = JsonSerializer.Serialize(body);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await client.PostAsync("/api/chat.postMessage", content, ct);
            response.EnsureSuccessStatusCode();
            var responseJson = await response.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(responseJson);
            var ok = doc.RootElement.GetProperty("ok").GetBoolean();

            return new SlackMessageResult
            {
                Success = ok,
                Timestamp = ok && doc.RootElement.TryGetProperty("ts", out var ts) ? ts.GetString() : null,
                Channel = ok && doc.RootElement.TryGetProperty("channel", out var ch) ? ch.GetString() : channel,
                ErrorMessage = !ok && doc.RootElement.TryGetProperty("error", out var err) ? err.GetString() : null
            };
        }

        /// <summary>
        /// Lists channels in the workspace.
        /// </summary>
        public async Task<SlackChannelListResult> ListChannelsAsync(IConnectionHandle handle,
            int limit = 100, string? cursor = null, bool excludeArchived = true, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            var url = $"/api/conversations.list?limit={limit}&exclude_archived={excludeArchived}";
            if (cursor != null) url += $"&cursor={cursor}";

            var response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(json);
            var ok = doc.RootElement.GetProperty("ok").GetBoolean();
            var channels = new List<SlackChannel>();

            if (ok && doc.RootElement.TryGetProperty("channels", out var chArr))
            {
                foreach (var ch in chArr.EnumerateArray())
                {
                    channels.Add(new SlackChannel
                    {
                        Id = ch.GetProperty("id").GetString() ?? "",
                        Name = ch.GetProperty("name").GetString() ?? "",
                        IsPrivate = ch.TryGetProperty("is_private", out var priv) && priv.GetBoolean(),
                        MemberCount = ch.TryGetProperty("num_members", out var mc) ? mc.GetInt32() : 0
                    });
                }
            }

            return new SlackChannelListResult { Success = ok, Channels = channels };
        }

        /// <summary>
        /// Uploads a file to a Slack channel.
        /// </summary>
        public async Task<SlackFileResult> UploadFileAsync(IConnectionHandle handle, string channel,
            byte[] fileContent, string filename, string? title = null, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(channel), "channels");
            form.Add(new StringContent(filename), "filename");
            if (title != null) form.Add(new StringContent(title), "title");
            form.Add(new ByteArrayContent(fileContent), "file", filename);

            var response = await client.PostAsync("/api/files.upload", form, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            return new SlackFileResult
            {
                Success = doc.RootElement.GetProperty("ok").GetBoolean(),
                FileId = doc.RootElement.TryGetProperty("file", out var f)
                    && f.TryGetProperty("id", out var fid) ? fid.GetString() : null
            };
        }
    }

    public sealed record SlackBlock
    {
        public required string Type { get; init; }
        public Dictionary<string, object>? Text { get; init; }
        public object[]? Elements { get; init; }
    }

    public sealed record SlackMessageResult
    {
        public bool Success { get; init; }
        public string? Timestamp { get; init; }
        public string? Channel { get; init; }
        public string? ErrorMessage { get; init; }
    }

    public sealed record SlackChannelListResult
    {
        public bool Success { get; init; }
        public List<SlackChannel> Channels { get; init; } = new();
    }

    public sealed record SlackChannel
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public bool IsPrivate { get; init; }
        public int MemberCount { get; init; }
    }

    public sealed record SlackFileResult
    {
        public bool Success { get; init; }
        public string? FileId { get; init; }
    }
}
