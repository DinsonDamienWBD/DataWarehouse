using System;
using System.Collections.Generic;
using System.Linq;
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
    /// Jira Cloud connection strategy with full REST API v3 integration.
    /// Supports issue CRUD, JQL search, sprint management, and webhook registration.
    /// </summary>
    public class JiraConnectionStrategy : SaaSConnectionStrategyBase
    {
        private volatile string _instance = "";
        private volatile string _email = "";
        private volatile string _apiToken = "";

        public override string StrategyId => "jira";
        public override string DisplayName => "Jira";
        public override ConnectorCategory Category => ConnectorCategory.SaaS;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to Atlassian Jira using REST API v3 with issue CRUD, JQL search, sprint management, and webhooks.";
        public override string[] Tags => new[] { "jira", "atlassian", "project-management", "saas", "rest-api", "agile" };

        public JiraConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            _instance = GetConfiguration<string>(config, "Instance", ""); if (string.IsNullOrEmpty(_instance) || _instance == "example") throw new ArgumentException("Jira instance name is required, 'example' is not valid");
            _email = GetConfiguration<string>(config, "Email", "");
            _apiToken = GetConfiguration<string>(config, "ApiToken", "");

            var endpoint = $"https://{_instance}.atlassian.net";
            var httpClient = new HttpClient
            {
                BaseAddress = new Uri(endpoint),
                Timeout = config.Timeout
            };

            if (!string.IsNullOrEmpty(_email) && !string.IsNullOrEmpty(_apiToken))
            {
                var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_email}:{_apiToken}"));
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", creds);
            }
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            return new DefaultConnectionHandle(httpClient, new Dictionary<string, object>
            {
                ["Instance"] = _instance,
                ["Endpoint"] = endpoint
            });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            try
            {
                var response = await handle.GetConnection<HttpClient>()
                    .GetAsync("/rest/api/3/myself", ct);
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
                isHealthy ? "Jira is reachable" : "Jira is not responding",
                sw.Elapsed, DateTimeOffset.UtcNow);
        }

        protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(
            IConnectionHandle handle, CancellationToken ct = default) =>
            Task.FromResult((Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddHours(24)));

        protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(
            IConnectionHandle handle, string currentToken, CancellationToken ct = default) =>
            AuthenticateAsync(handle, ct);

        /// <summary>
        /// Searches issues using JQL (Jira Query Language).
        /// </summary>
        public async Task<JiraSearchResult> SearchIssuesAsync(IConnectionHandle handle, string jql,
            int startAt = 0, int maxResults = 50, string[]? fields = null, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            var body = new Dictionary<string, object>
            {
                ["jql"] = jql,
                ["startAt"] = startAt,
                ["maxResults"] = maxResults
            };
            if (fields != null)
                body["fields"] = fields;

            var json = JsonSerializer.Serialize(body);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("/rest/api/3/search", content, ct);
            response.EnsureSuccessStatusCode();
            var responseJson = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return new JiraSearchResult { Success = false, ErrorMessage = responseJson };

            using var doc = JsonDocument.Parse(responseJson);
            var issues = new List<JiraIssue>();

            if (doc.RootElement.TryGetProperty("issues", out var issuesEl))
            {
                foreach (var issue in issuesEl.EnumerateArray())
                {
                    issues.Add(ParseIssue(issue));
                }
            }

            return new JiraSearchResult
            {
                Success = true,
                Total = doc.RootElement.GetProperty("total").GetInt32(),
                StartAt = doc.RootElement.GetProperty("startAt").GetInt32(),
                MaxResults = doc.RootElement.GetProperty("maxResults").GetInt32(),
                Issues = issues
            };
        }

        /// <summary>
        /// Creates a Jira issue.
        /// </summary>
        public async Task<JiraIssueResult> CreateIssueAsync(IConnectionHandle handle, string projectKey,
            string issueType, string summary, string? description = null, string? assignee = null,
            string? priority = null, Dictionary<string, object>? customFields = null, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            var fields = new Dictionary<string, object>
            {
                ["project"] = new { key = projectKey },
                ["issuetype"] = new { name = issueType },
                ["summary"] = summary
            };

            if (!string.IsNullOrEmpty(description))
            {
                fields["description"] = new
                {
                    type = "doc",
                    version = 1,
                    content = new[]
                    {
                        new { type = "paragraph", content = new[] { new { type = "text", text = description } } }
                    }
                };
            }
            if (!string.IsNullOrEmpty(assignee))
                fields["assignee"] = new { accountId = assignee };
            if (!string.IsNullOrEmpty(priority))
                fields["priority"] = new { name = priority };

            if (customFields != null)
            {
                foreach (var (key, value) in customFields)
                    fields[key] = value;
            }

            var body = JsonSerializer.Serialize(new { fields });
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("/rest/api/3/issue", content, ct);
            response.EnsureSuccessStatusCode();
            var responseJson = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return new JiraIssueResult { Success = false, ErrorMessage = responseJson };

            using var doc = JsonDocument.Parse(responseJson);
            return new JiraIssueResult
            {
                Success = true,
                IssueId = doc.RootElement.GetProperty("id").GetString(),
                IssueKey = doc.RootElement.GetProperty("key").GetString()
            };
        }

        /// <summary>
        /// Updates an existing Jira issue.
        /// </summary>
        public async Task<JiraIssueResult> UpdateIssueAsync(IConnectionHandle handle, string issueKeyOrId,
            Dictionary<string, object> fields, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            var body = JsonSerializer.Serialize(new { fields });
            using var request = new HttpRequestMessage(HttpMethod.Put, $"/rest/api/3/issue/{issueKeyOrId}")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

            using var response = await client.SendAsync(request, ct);
            return new JiraIssueResult
            {
                Success = response.IsSuccessStatusCode,
                IssueKey = issueKeyOrId,
                ErrorMessage = response.IsSuccessStatusCode ? null : await response.Content.ReadAsStringAsync(ct)
            };
        }

        /// <summary>
        /// Transitions an issue to a new status.
        /// </summary>
        public async Task<JiraIssueResult> TransitionIssueAsync(IConnectionHandle handle, string issueKeyOrId,
            string transitionId, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            var body = JsonSerializer.Serialize(new { transition = new { id = transitionId } });
            var content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await client.PostAsync($"/rest/api/3/issue/{issueKeyOrId}/transitions", content, ct);
            return new JiraIssueResult
            {
                Success = response.IsSuccessStatusCode,
                IssueKey = issueKeyOrId,
                ErrorMessage = response.IsSuccessStatusCode ? null : await response.Content.ReadAsStringAsync(ct)
            };
        }

        /// <summary>
        /// Gets all active sprints for a board.
        /// </summary>
        public async Task<JiraSprintResult> GetActiveSprintsAsync(IConnectionHandle handle, int boardId,
            CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            using var response = await client.GetAsync($"/rest/agile/1.0/board/{boardId}/sprint?state=active", ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return new JiraSprintResult { Success = false, ErrorMessage = json };

            using var doc = JsonDocument.Parse(json);
            var sprints = new List<JiraSprint>();

            if (doc.RootElement.TryGetProperty("values", out var values))
            {
                foreach (var sprint in values.EnumerateArray())
                {
                    sprints.Add(new JiraSprint
                    {
                        Id = sprint.GetProperty("id").GetInt32(),
                        Name = sprint.GetProperty("name").GetString() ?? "",
                        State = sprint.GetProperty("state").GetString() ?? "",
                        StartDate = sprint.TryGetProperty("startDate", out var sd) ? sd.GetString() : null,
                        EndDate = sprint.TryGetProperty("endDate", out var ed) ? ed.GetString() : null
                    });
                }
            }

            return new JiraSprintResult { Success = true, Sprints = sprints };
        }

        /// <summary>
        /// Registers a webhook for issue events.
        /// </summary>
        public async Task<JiraWebhookResult> RegisterWebhookAsync(IConnectionHandle handle, string name,
            string url, string[] events, string? jqlFilter = null, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            var body = JsonSerializer.Serialize(new
            {
                name,
                url,
                events,
                filters = jqlFilter != null ? new { issue_related_events_section = jqlFilter } : null,
                enabled = true
            });
            var content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await client.PostAsync("/rest/webhooks/1.0/webhook", content, ct);
            response.EnsureSuccessStatusCode();
            var responseJson = await response.Content.ReadAsStringAsync(ct);

            return new JiraWebhookResult
            {
                Success = response.IsSuccessStatusCode,
                WebhookName = name,
                ErrorMessage = response.IsSuccessStatusCode ? null : responseJson
            };
        }

        private static JiraIssue ParseIssue(JsonElement el)
        {
            var fields = el.TryGetProperty("fields", out var f) ? f : default;
            return new JiraIssue
            {
                Id = el.GetProperty("id").GetString() ?? "",
                Key = el.GetProperty("key").GetString() ?? "",
                Summary = fields.ValueKind != JsonValueKind.Undefined && fields.TryGetProperty("summary", out var s)
                    ? s.GetString() ?? "" : "",
                Status = fields.ValueKind != JsonValueKind.Undefined && fields.TryGetProperty("status", out var st)
                    && st.TryGetProperty("name", out var sn) ? sn.GetString() ?? "" : "",
                IssueType = fields.ValueKind != JsonValueKind.Undefined && fields.TryGetProperty("issuetype", out var it)
                    && it.TryGetProperty("name", out var itn) ? itn.GetString() ?? "" : ""
            };
        }
    }

    public sealed record JiraSearchResult
    {
        public bool Success { get; init; }
        public int Total { get; init; }
        public int StartAt { get; init; }
        public int MaxResults { get; init; }
        public List<JiraIssue> Issues { get; init; } = new();
        public string? ErrorMessage { get; init; }
    }

    public sealed record JiraIssue
    {
        public required string Id { get; init; }
        public required string Key { get; init; }
        public string Summary { get; init; } = "";
        public string Status { get; init; } = "";
        public string IssueType { get; init; } = "";
    }

    public sealed record JiraIssueResult
    {
        public bool Success { get; init; }
        public string? IssueId { get; init; }
        public string? IssueKey { get; init; }
        public string? ErrorMessage { get; init; }
    }

    public sealed record JiraSprintResult
    {
        public bool Success { get; init; }
        public List<JiraSprint> Sprints { get; init; } = new();
        public string? ErrorMessage { get; init; }
    }

    public sealed record JiraSprint
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";
        public string State { get; init; } = "";
        public string? StartDate { get; init; }
        public string? EndDate { get; init; }
    }

    public sealed record JiraWebhookResult
    {
        public bool Success { get; init; }
        public string? WebhookName { get; init; }
        public string? ErrorMessage { get; init; }
    }
}
