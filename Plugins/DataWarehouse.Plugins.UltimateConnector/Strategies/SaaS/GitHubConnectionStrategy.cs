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
    /// GitHub connection strategy with full REST API integration.
    /// Supports repository CRUD, issue/PR management, webhook handling, and API rate limiting.
    /// </summary>
    public class GitHubConnectionStrategy : SaaSConnectionStrategyBase
    {
        private string _personalAccessToken = "";
        private volatile int _rateLimitRemaining = 5000;
        private DateTimeOffset _rateLimitReset = DateTimeOffset.UtcNow;

        public override string StrategyId => "github";
        public override string DisplayName => "GitHub";
        public override ConnectorCategory Category => ConnectorCategory.SaaS;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to GitHub using REST API v3 with repo CRUD, issue/PR management, webhook handling, and rate limiting.";
        public override string[] Tags => new[] { "github", "vcs", "git", "saas", "rest-api", "webhooks", "devops" };

        public GitHubConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            _personalAccessToken = GetConfiguration<string>(config, "PersonalAccessToken", "");

            var httpClient = new HttpClient
            {
                BaseAddress = new Uri("https://api.github.com"),
                Timeout = config.Timeout
            };

            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DataWarehouse/1.0");
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            httpClient.DefaultRequestHeaders.Remove("X-GitHub-Api-Version");
            httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

            if (!string.IsNullOrEmpty(_personalAccessToken))
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _personalAccessToken);

            return new DefaultConnectionHandle(httpClient, new Dictionary<string, object>
            {
                ["Endpoint"] = "https://api.github.com"
            });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            try
            {
                var response = await handle.GetConnection<HttpClient>().GetAsync("/user", ct);
                UpdateRateLimits(response);
                return response.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) {
            handle.GetConnection<HttpClient>()?.Dispose(); return Task.CompletedTask; }

        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();
            return new ConnectionHealth(isHealthy,
                isHealthy ? $"GitHub is reachable (rate limit: {_rateLimitRemaining} remaining)" : "GitHub is not responding",
                sw.Elapsed, DateTimeOffset.UtcNow);
        }

        protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(
            IConnectionHandle handle, CancellationToken ct = default) =>
            Task.FromResult((_personalAccessToken, DateTimeOffset.UtcNow.AddHours(8)));

        protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(
            IConnectionHandle handle, string currentToken, CancellationToken ct = default) =>
            AuthenticateAsync(handle, ct);

        /// <summary>
        /// Lists repositories for the authenticated user or an organization.
        /// </summary>
        public async Task<GitHubListResult<GitHubRepo>> ListRepositoriesAsync(IConnectionHandle handle,
            string? org = null, int perPage = 30, int page = 1, CancellationToken ct = default)
        {
            // Finding 2159: Clamp perPage to GitHub maximum of 100; validate page is positive.
            perPage = Math.Clamp(perPage, 1, 100);
            if (page < 1) page = 1;

            var client = handle.GetConnection<HttpClient>();
            await CheckRateLimitAsync(ct);

            var url = org != null
                ? $"/orgs/{org}/repos?per_page={perPage}&page={page}"
                : $"/user/repos?per_page={perPage}&page={page}";

            using var response = await client.GetAsync(url, ct);
            UpdateRateLimits(response);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return new GitHubListResult<GitHubRepo> { Success = false, ErrorMessage = json };

            var repos = JsonSerializer.Deserialize<List<GitHubRepo>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            return new GitHubListResult<GitHubRepo> { Success = true, Items = repos };
        }

        /// <summary>
        /// Creates an issue in a repository.
        /// </summary>
        public async Task<GitHubIssueResult> CreateIssueAsync(IConnectionHandle handle, string owner,
            string repo, string title, string? body = null, string[]? labels = null,
            string[]? assignees = null, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            await CheckRateLimitAsync(ct);

            var payload = new Dictionary<string, object> { ["title"] = title };
            if (body != null) payload["body"] = body;
            if (labels != null) payload["labels"] = labels;
            if (assignees != null) payload["assignees"] = assignees;

            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync($"/repos/{owner}/{repo}/issues", content, ct);
            UpdateRateLimits(response);
            response.EnsureSuccessStatusCode();
            var responseJson = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return new GitHubIssueResult { Success = false, ErrorMessage = responseJson };

            using var doc = JsonDocument.Parse(responseJson);
            return new GitHubIssueResult
            {
                Success = true,
                Number = doc.RootElement.GetProperty("number").GetInt32(),
                HtmlUrl = doc.RootElement.GetProperty("html_url").GetString()
            };
        }

        /// <summary>
        /// Creates a pull request.
        /// </summary>
        public async Task<GitHubPrResult> CreatePullRequestAsync(IConnectionHandle handle, string owner,
            string repo, string title, string head, string baseBranch, string? body = null,
            CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            await CheckRateLimitAsync(ct);

            var payload = new Dictionary<string, object>
            {
                ["title"] = title,
                ["head"] = head,
                ["base"] = baseBranch
            };
            if (body != null) payload["body"] = body;

            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync($"/repos/{owner}/{repo}/pulls", content, ct);
            UpdateRateLimits(response);
            response.EnsureSuccessStatusCode();
            var responseJson = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return new GitHubPrResult { Success = false, ErrorMessage = responseJson };

            using var doc = JsonDocument.Parse(responseJson);
            return new GitHubPrResult
            {
                Success = true,
                Number = doc.RootElement.GetProperty("number").GetInt32(),
                HtmlUrl = doc.RootElement.GetProperty("html_url").GetString()
            };
        }

        /// <summary>
        /// Registers a webhook for a repository.
        /// </summary>
        public async Task<GitHubWebhookResult> CreateWebhookAsync(IConnectionHandle handle, string owner,
            string repo, string payloadUrl, string[] events, string secret, CancellationToken ct = default)
        {
            // Finding 2160: Require a minimum-entropy webhook secret (GitHub recommends >= 20 chars).
            if (string.IsNullOrWhiteSpace(secret) || secret.Length < 20)
                throw new ArgumentException(
                    "Webhook secret must be at least 20 characters to provide adequate HMAC security.", nameof(secret));

            var client = handle.GetConnection<HttpClient>();
            await CheckRateLimitAsync(ct);

            var payload = new
            {
                name = "web",
                active = true,
                events,
                config = new
                {
                    url = payloadUrl,
                    content_type = "json",
                    secret,
                    insecure_ssl = "0"
                }
            };

            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync($"/repos/{owner}/{repo}/hooks", content, ct);
            UpdateRateLimits(response);
            response.EnsureSuccessStatusCode();
            var responseJson = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return new GitHubWebhookResult { Success = false, ErrorMessage = responseJson };

            using var doc = JsonDocument.Parse(responseJson);
            return new GitHubWebhookResult
            {
                Success = true,
                WebhookId = doc.RootElement.GetProperty("id").GetInt64()
            };
        }

        /// <summary>
        /// Gets the current rate limit status.
        /// </summary>
        public GitHubRateLimit GetRateLimitStatus() => new()
        {
            Remaining = _rateLimitRemaining,
            ResetAt = _rateLimitReset
        };

        private void UpdateRateLimits(HttpResponseMessage response)
        {
            if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining))
            { if (int.TryParse(System.Linq.Enumerable.FirstOrDefault(remaining), out var remainingVal)) _rateLimitRemaining = remainingVal; }
            if (response.Headers.TryGetValues("X-RateLimit-Reset", out var reset))
            {
                if (long.TryParse(System.Linq.Enumerable.FirstOrDefault(reset), out var resetEpoch))
                    _rateLimitReset = DateTimeOffset.FromUnixTimeSeconds(resetEpoch);
            }
        }

        private async Task CheckRateLimitAsync(CancellationToken ct)
        {
            if (_rateLimitRemaining <= 10 && DateTimeOffset.UtcNow < _rateLimitReset)
            {
                var delay = _rateLimitReset - DateTimeOffset.UtcNow;
                if (delay.TotalSeconds > 0 && delay.TotalSeconds < 60)
                    await Task.Delay(delay, ct);
            }
        }
    }

    public sealed record GitHubRepo
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        // Finding 2166: Use PascalCase with JsonPropertyName to follow C# conventions.
        [System.Text.Json.Serialization.JsonPropertyName("full_name")]
        public string FullName { get; init; } = "";
        public bool Private { get; init; }
        public string? Description { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("html_url")]
        public string HtmlUrl { get; init; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("default_branch")]
        public string DefaultBranch { get; init; } = "main";
    }

    public sealed record GitHubListResult<T>
    {
        public bool Success { get; init; }
        public List<T> Items { get; init; } = new();
        public string? ErrorMessage { get; init; }
    }

    public sealed record GitHubIssueResult
    {
        public bool Success { get; init; }
        public int Number { get; init; }
        public string? HtmlUrl { get; init; }
        public string? ErrorMessage { get; init; }
    }

    public sealed record GitHubPrResult
    {
        public bool Success { get; init; }
        public int Number { get; init; }
        public string? HtmlUrl { get; init; }
        public string? ErrorMessage { get; init; }
    }

    public sealed record GitHubWebhookResult
    {
        public bool Success { get; init; }
        public long WebhookId { get; init; }
        public string? ErrorMessage { get; init; }
    }

    public sealed record GitHubRateLimit
    {
        public int Remaining { get; init; }
        public DateTimeOffset ResetAt { get; init; }
    }
}
